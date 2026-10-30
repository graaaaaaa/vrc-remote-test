using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Deployment;
using VRCRemoteTest.Bridge.Protocol;
using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Lan;

/// <summary>Only fixed, authenticated routes are exposed to the LAN.</summary>
public static class BridgeLanApi
{
    private const int MaxManifestBytes = 64 * 1024;
    private const long MaxFullLogBytes = 256L * 1024 * 1024;
    private const int MaxFullLogLineChars = 1024 * 1024;
    private static readonly SemaphoreSlim Ingress = new(1, 1);
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// <paramref name="runtime"/> adds the management routes (<see cref="BridgeAdminApi"/>);
    /// without it (tests of the build routes alone) they are not mapped.
    /// </summary>
    public static void Map(WebApplication app, BridgeOptions options, BridgeIdentity identity,
        LogSourceRegistry logSources, Runtime.BridgeRuntime? runtime = null)
    {
        app.Use(async (context, next) =>
        {
            if (!identity.Authenticate(context.Request.Headers.Authorization))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next();
        });

        var api = app.MapGroup("/api/v1");
        api.MapGet("/capabilities", () => Results.Ok(new
        {
            apiVersion = 1,
            buildProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            maxArtifactSizeBytes = options.MaxArtifactSizeBytes,
            maxClients = BridgeOptions.MaxVrchatClientCount,
        }));

        api.MapPut("/builds/{buildId}/artifact", (HttpContext context, string buildId) =>
            PutArtifact(context, options, buildId));
        api.MapPost("/builds/{buildId}/commit", (HttpContext context, string buildId) =>
            runtime?.RestartScheduled == true
                ? Task.FromResult(Results.Json(new { error = "busy", operation = "restart" }, statusCode: StatusCodes.Status409Conflict))
                : Commit(context, options, buildId));
        api.MapGet("/builds/{buildId}", (string buildId) =>
            ReadBuildResult(options, buildId));

        api.MapGet("/status", () => ReadText(options, "status", "vrchat-status.json", 64 * 1024, "application/json"));
        api.MapGet("/clients", () => ReadText(options, "status", "vrchat-clients.json", 256 * 1024, "application/json"));
        api.MapGet("/logs/latest", () => ReadText(options, "logs", "vrchat-latest.log", 512 * 1024, "text/plain; charset=utf-8"));
        api.MapGet("/logs/clients/{slot:int}", (int slot) =>
            ReadClientText(options, slot, false));
        api.MapGet("/markers/clients/{slot:int}", (int slot) =>
            ReadClientText(options, slot, true));
        api.MapPost("/log-exports", (HttpContext context, LogExportRequest request) =>
            ExportLog(context, options, logSources, request));
        api.MapGet("/log-exports/{exportId}", (string exportId) =>
            ReadExport(options, exportId));

        if (runtime is not null)
        {
            BridgeAdminApi.Map(api, runtime);
        }
    }

    /// <summary>
    /// Every 413 says what was too large and the limit (v0.8.0); before, the body was empty
    /// and a 47.7 MB world looked like any other failure. <paramref name="declared"/> is the
    /// Content-Length, or null for a body sent without one.
    /// </summary>
    internal static IResult TooLarge(string error, long limit, long? declared) =>
        Results.Json(new { error, limitBytes = limit, declaredBytes = declared },
            statusCode: StatusCodes.Status413PayloadTooLarge);

    private static bool IsTooLarge(Exception ex) =>
        ex is Microsoft.AspNetCore.Http.BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge };

    public sealed record LogExportRequest(int Slot, long ClientGeneration, long ObservationEpoch);

    private static async Task<IResult> PutArtifact(HttpContext context, BridgeOptions options, string buildId)
    {
        if (PackageValidator.ValidateBuildId(buildId) is not null)
        {
            return Results.BadRequest("Invalid build ID.");
        }

        var size = context.Request.ContentLength;
        if (size > options.MaxArtifactSizeBytes)
        {
            return TooLarge("artifactTooLarge", options.MaxArtifactSizeBytes, size);
        }

        // Kestrel stops every request body at 30,000,000 bytes by default (413) before MaxArtifactSizeBytes is checked,
        // and a world is often larger. This route enforces MaxArtifactSizeBytes itself below, so the server's limit
        // follows it here (one byte above, so this route's own check answers first, with a body); every other route
        // keeps the default.
        var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false })
        {
            bodyLimit.MaxRequestBodySize = options.MaxArtifactSizeBytes + 1;
        }

        await Ingress.WaitAsync(context.RequestAborted);
        var incoming = Path.Combine(options.StateDirectory, "incoming");
        Directory.CreateDirectory(incoming);
        var final = Path.Combine(incoming, buildId + ".vrcw");
        var partial = Path.Combine(incoming, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            if (AlreadyCommitted(options, buildId))
            {
                return Results.Conflict("Build ID was already committed.");
            }

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            await using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                int read;
                while (true)
                {
                    try
                    {
                        read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
                    }
                    catch (Exception ex) when (IsTooLarge(ex))
                    {
                        return TooLarge("artifactTooLarge", options.MaxArtifactSizeBytes, size);
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    if (read > options.MaxArtifactSizeBytes - total)
                    {
                        return TooLarge("artifactTooLarge", options.MaxArtifactSizeBytes, size);
                    }

                    total += read;
                    sha.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                }

                await destination.FlushAsync(context.RequestAborted);
                destination.Flush(flushToDisk: true);
            }

            if (total == 0 || (size is not null && total != size))
            {
                return Results.BadRequest("Body length did not match Content-Length.");
            }

            var hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (File.Exists(final))
            {
                if (new FileInfo(final).Length != total || PackageValidator.ComputeSha256(final) != hash)
                {
                    return Results.Conflict("Build ID already has a different artifact.");
                }
            }
            else
            {
                File.Move(partial, final);
            }

            return Results.Ok(new { buildId, size = total, sha256 = hash });
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
            Ingress.Release();
        }
    }

    private static async Task<IResult> Commit(HttpContext context, BridgeOptions options, string buildId)
    {
        if (PackageValidator.ValidateBuildId(buildId) is not null)
        {
            return Results.BadRequest("Invalid build ID.");
        }

        if (context.Request.ContentLength > MaxManifestBytes)
        {
            return TooLarge("manifestTooLarge", MaxManifestBytes, context.Request.ContentLength);
        }

        byte[] bytes;
        await using (var memory = new MemoryStream())
        {
            var buffer = new byte[8192];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                if (memory.Length + read > MaxManifestBytes)
                {
                    return TooLarge("manifestTooLarge", MaxManifestBytes, context.Request.ContentLength);
                }

                await memory.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            }

            bytes = memory.ToArray();
        }

        BuildManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BuildManifest>(bytes, ManifestOptions);
        }
        catch (JsonException)
        {
            return Results.BadRequest("Invalid manifest JSON.");
        }

        if (manifest is null || manifest.BuildId != buildId || manifest.FileName != buildId + ".vrcw"
            || PackageValidator.ValidateBuildId(manifest.BuildId) is not null
            || PackageValidator.ValidateFileName(manifest.FileName) is not null
            || PackageValidator.ValidateProtocolVersion(manifest) is not null
            || PackageValidator.ValidateDeclaredArtifact(manifest, options.MaxArtifactSizeBytes) is not null)
        {
            return Results.BadRequest("Invalid manifest.");
        }

        await Ingress.WaitAsync(context.RequestAborted);
        var incoming = Path.Combine(options.StateDirectory, "incoming");
        var artifact = Path.Combine(incoming, manifest.FileName);
        var ready = Path.Combine(incoming, buildId + ProtocolConstants.ManifestExtension);
        var partial = Path.Combine(incoming, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            if (AlreadyCommitted(options, buildId))
            {
                var committed = FindCommittedManifest(options, buildId);
                return committed is not null && File.ReadAllBytes(committed).AsSpan().SequenceEqual(bytes)
                    ? Results.Ok(new { buildId, accepted = true })
                    : Results.Conflict("Build ID was already committed with a different manifest.");
            }

            var validation = new PackageValidator().Validate(manifest, artifact, options.MaxArtifactSizeBytes);
            if (!validation.IsValid)
            {
                return Results.BadRequest(validation.ErrorCode + ": " + validation.ErrorMessage);
            }

            await File.WriteAllBytesAsync(partial, bytes, context.RequestAborted);
            File.Move(partial, ready);
            return Results.Accepted($"/api/v1/builds/{buildId}", new { buildId, accepted = true });
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
            Ingress.Release();
        }
    }

    private static bool AlreadyCommitted(BridgeOptions options, string buildId) =>
        File.Exists(Path.Combine(options.StateDirectory, "incoming", buildId + ProtocolConstants.ManifestExtension))
        || File.Exists(Path.Combine(options.StateDirectory, "processing", buildId + ProtocolConstants.ManifestExtension))
        || File.Exists(Path.Combine(options.StateDirectory, "archive", buildId + ProtocolConstants.ManifestExtension))
        || File.Exists(Path.Combine(options.StateDirectory, "results", buildId + ".json"));

    private static string? FindCommittedManifest(BridgeOptions options, string buildId)
    {
        foreach (var directory in new[] { "incoming", "processing", "archive" })
        {
            var path = Path.Combine(options.StateDirectory, directory, buildId + ProtocolConstants.ManifestExtension);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static IResult ReadBuildResult(BridgeOptions options, string buildId)
    {
        if (PackageValidator.ValidateBuildId(buildId) is not null)
        {
            return Results.BadRequest("Invalid build ID.");
        }

        return ReadText(options, "results", buildId + ".json", 64 * 1024, "application/json");
    }

    private static IResult ReadClientText(BridgeOptions options, int slot, bool markers)
    {
        if (slot is < 0 or >= BridgeOptions.MaxVrchatClientCount)
        {
            return Results.BadRequest("Invalid client slot.");
        }

        return ReadText(options, Path.Combine("logs", "clients"),
            $"slot-{slot}.{(markers ? "markers." : string.Empty)}log",
            markers ? 1280 * 1024 : 512 * 1024, "text/plain; charset=utf-8");
    }

    private static IResult ReadText(BridgeOptions options, string directory, string fileName, long maxBytes, string type)
    {
        var path = Path.Combine(options.StateDirectory, directory, fileName);
        if (!File.Exists(path)) return Results.NotFound();
        try
        {
            var info = new FileInfo(path);
            if (info.Length > maxBytes)
            {
                return TooLarge(directory == "exports" ? "logTooLarge" : "snapshotTooLarge", maxBytes, info.Length);
            }
            return Results.File(path, type, enableRangeProcessing: false);
        }
        catch (IOException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> ExportLog(HttpContext context, BridgeOptions options,
        LogSourceRegistry sources, LogExportRequest request)
    {
        var source = sources.Get(request.Slot);
        if (source is null || source.ClientGeneration != request.ClientGeneration
            || source.ObservationEpoch != request.ObservationEpoch)
        {
            return Results.Conflict("The selected client session is no longer the verified log source.");
        }

        var exports = Path.Combine(options.StateDirectory, "exports");
        Directory.CreateDirectory(exports);
        var id = Guid.NewGuid().ToString("N");
        var partial = Path.Combine(exports, id + ".part");
        var final = Path.Combine(exports, id + ".log");
        try
        {
            using var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var initialLength = input.Length;
            if (initialLength > MaxFullLogBytes)
            {
                return TooLarge("logTooLarge", MaxFullLogBytes, initialLength);
            }

            if (File.GetCreationTimeUtc(source.Path) != source.CreationTimeUtc)
            {
                return Results.Conflict("The log file changed before export.");
            }

            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(output, new UTF8Encoding(false)))
            {
                var decoder = Encoding.UTF8.GetDecoder();
                var bytes = new byte[65536];
                var chars = new char[65536];
                var line = new StringBuilder();
                var remaining = initialLength;
                while (remaining > 0)
                {
                    var read = await input.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, remaining)),
                        context.RequestAborted);
                    if (read == 0) return Results.Conflict("The log was shortened during export.");
                    remaining -= read;
                    var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: remaining == 0);
                    for (var i = 0; i < count; i++)
                    {
                        if (chars[i] == '\n')
                        {
                            var value = line.ToString().TrimEnd('\r');
                            await writer.WriteLineAsync(LogRedactor.Redact(value));
                            line.Clear();
                        }
                        else
                        {
                            line.Append(chars[i]);
                            if (line.Length > MaxFullLogLineChars)
                            {
                                // A limit on one line's characters, not on bytes.
                                return Results.Json(new { error = "logLineTooLong", limitChars = MaxFullLogLineChars },
                                    statusCode: StatusCodes.Status413PayloadTooLarge);
                            }
                        }
                    }
                }

                if (line.Length > 0)
                {
                    await writer.WriteAsync(LogRedactor.Redact(line.ToString()));
                }

                await writer.FlushAsync(context.RequestAborted);
                output.Flush(flushToDisk: true);
            }

            var current = sources.Get(request.Slot);
            if (current != source || File.GetCreationTimeUtc(source.Path) != source.CreationTimeUtc)
            {
                return Results.Conflict("The selected client session changed during export.");
            }

            var size = new FileInfo(partial).Length;
            if (size > MaxFullLogBytes)
            {
                return TooLarge("logTooLarge", MaxFullLogBytes, size);
            }

            File.Move(partial, final);
            var sha256 = PackageValidator.ComputeSha256(final);
            return Results.Ok(new
            {
                exportId = id,
                slot = request.Slot,
                clientGeneration = request.ClientGeneration,
                observationEpoch = request.ObservationEpoch,
                size,
                sha256,
            });
        }
        catch (IOException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private static IResult ReadExport(BridgeOptions options, string exportId)
    {
        if (!Guid.TryParseExact(exportId, "N", out _)) return Results.BadRequest("Invalid export ID.");
        return ReadText(options, "exports", exportId + ".log",
            MaxFullLogBytes, "text/plain; charset=utf-8");
    }
}
