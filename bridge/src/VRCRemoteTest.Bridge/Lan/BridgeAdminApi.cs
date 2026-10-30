using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Runtime;
using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Lan;

/// <summary>
/// The LAN routes that let the Mac look after the Bridge without anyone at the Windows PC
/// (v0.8.0): its version, configuration and log; changing a fixed set of settings, each
/// within a range (<see cref="Editable"/>); restarting the host in-process; closing the
/// VRChat clients it launched; and which account each profile was last logged in to. Same
/// authentication as every other route. Nothing here runs a command, takes a free-form path
/// or changes the listen address, the port, the state directory, the Worlds directory or
/// the pairing. Management operations take the operation gate and answer 409 busy when a
/// deploy or another operation holds it.
/// </summary>
internal static class BridgeAdminApi
{
    private const int MaxPatchBytes = 16 * 1024;
    private const int DefaultLogLines = 200;
    private const int MaxLogLines = 500;
    private const int MaxLogBytes = 512 * 1024;
    internal const string CandidateKey = "VrchatExecutableCandidate";

    /// <summary>A setting the LAN API may change, and the values it accepts (null always removes it from config.json).</summary>
    internal sealed record EditableSetting(string Name, string Kind, Func<JsonNode, string?> Check);

    internal static readonly IReadOnlyList<EditableSetting> Editable =
    [
        new("AutoLaunchVrchat", "boolean", v => v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? null : "must be true or false"),
        new("VrchatClientMode", "string", v => OneOf(v, BridgeOptions.ReloadClientMode, BridgeOptions.RelaunchClientMode)),
        new("VrchatProfiles", "integer[]", v => v is JsonArray a && a.Count is >= 1 and <= BridgeOptions.MaxVrchatClientCount
            && a.All(e => IntIn(e, 0, BridgeOptions.MaxVrchatProfile))
                ? null : $"must list 1 to {BridgeOptions.MaxVrchatClientCount} profiles from 0 to {BridgeOptions.MaxVrchatProfile}"),
        new("VrchatClientCount", "integer", v => IntIn(v, 1, BridgeOptions.MaxVrchatClientCount) ? null : $"must be 1 to {BridgeOptions.MaxVrchatClientCount}"),
        new("VrchatMode", "string", v => OneOf(v, "Desktop", "VR")),
        new("VrchatStartupTimeoutSeconds", "integer", v => IntIn(v, 30, 600) ? null : "must be 30 to 600"),
        new("VrchatStartupSettleDelaySeconds", "integer", v => IntIn(v, 1, 300) ? null : "must be 1 to 300"),
        new("RetainBuilds", "integer", v => IntIn(v, 1, 100) ? null : "must be 1 to 100 (0, which stops cleanup, is not offered)"),
        new("MaxArtifactSizeBytes", "integer", v => LongIn(v, 1L << 20, 2L << 30) ? null : "must be 1 MiB to 2 GiB"),
        new("LogMarkerPrefixes", "string[]", v => v is JsonArray a && a.All(e => e?.GetValueKind() == JsonValueKind.String)
            ? null : "must be an array of strings"),
        new(CandidateKey, "string", v => v.GetValueKind() == JsonValueKind.String ? null : "must be a candidate id from GET /config"),
    ];

    public static void Map(RouteGroupBuilder api, BridgeRuntime runtime)
    {
        api.MapGet("/bridge", (HttpContext context) => Info(context, runtime));
        api.MapGet("/logs/bridge", (int? lines) => BridgeLog(runtime, lines));
        api.MapGet("/profiles", (HttpContext context) => Profiles(context));
        api.MapGet("/config", (HttpContext context) => GetConfig(context, runtime));
        api.MapPut("/config", (HttpContext context) => PutConfig(context, runtime));
        api.MapPost("/bridge/restart", () => Restart(runtime));
        api.MapPost("/clients/close", (HttpContext context) => Close(context, runtime));
    }

    private static IResult Info(HttpContext context, BridgeRuntime runtime)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<BridgeOptions>>().Value;
        var profiles = context.RequestServices.GetRequiredService<ProfileAccountStore>();
        var saved = BridgeRuntime.HashFile(runtime.ConfigPath);
        return Results.Ok(new
        {
            version = BridgeRuntime.Version,
            exeSha256 = runtime.ExeSha256,
            processStartedAtUtc = runtime.ProcessStartedAtUtc,
            hostGeneration = runtime.HostGeneration,
            hostStartedAtUtc = runtime.HostStartedAtUtc,
            listenUrl = $"https://{FormatHost(options.LanBindAddress)}:{options.LanPort}",
            certificateSha256 = runtime.Identity.Fingerprint,
            activeConfigEtag = runtime.ActiveConfigEtag,
            savedConfigEtag = saved,
            activeConfig = ActiveConfig(options),
            paths = new
            {
                vrchatWorldsDirectory = new { display = Display(options.VrchatWorldsDirectory), exists = Directory.Exists(options.VrchatWorldsDirectory) },
                vrchatExecutable = new { display = Display(options.VrchatExecutable), exists = File.Exists(options.VrchatExecutable) },
            },
            lastConfigApply = runtime.LastConfigApply,
            busy = runtime.Gate.Current,
            restartPending = runtime.Gate.RestartPending,
            configNotes = Notes(options, profiles, runtime, saved),
            stateExportsBytes = DirectorySize(Path.Combine(options.StateDirectory, "exports")),
            freeDiskBytes = FreeSpace(options.StateDirectory),
        });
    }

    internal static object ActiveConfig(BridgeOptions o) => new
    {
        o.LanBindAddress,
        o.LanPort,
        VrchatWorldsDirectory = Display(o.VrchatWorldsDirectory),
        o.MaxArtifactSizeBytes,
        o.RetainBuilds,
        VrchatExecutable = Display(o.VrchatExecutable),
        o.VrchatMode,
        o.AutoLaunchVrchat,
        o.VrchatClientCount,
        o.VrchatClientMode,
        o.VrchatProfiles,
        o.VrchatStartupTimeoutSeconds,
        o.VrchatStartupSettleDelaySeconds,
        LogMarkerPrefixes = o.ResolvedLogMarkerPrefixes,
    };

    private static List<string> Notes(BridgeOptions o, ProfileAccountStore profiles, BridgeRuntime runtime, string? savedEtag)
    {
        var notes = new List<string>();
        if (!o.AutoLaunchVrchat)
        {
            notes.Add("AutoLaunchVrchat is false: a deploy installs the world but launches no VRChat; only --watch-worlds clients already running load it.");
        }
        else if (!File.Exists(o.VrchatExecutable))
        {
            notes.Add("VrchatExecutable does not exist, so VRChat cannot be launched.");
        }

        if (!o.IsRelaunchMode && o.ResolvedVrchatClientCount > 1)
        {
            notes.Add($"Reload mode with {o.ResolvedVrchatClientCount} clients: every client is logged in to the same account.");
        }

        if (o.IsRelaunchMode)
        {
            notes.AddRange(profiles.SameAccountWarnings(o.VrchatProfiles));
        }

        if (savedEtag != runtime.ActiveConfigEtag)
        {
            notes.Add("config.json differs from the one this host was started with; it takes effect after a restart.");
        }

        return notes;
    }

    private static IResult BridgeLog(BridgeRuntime runtime, int? lines)
    {
        var count = Math.Clamp(lines ?? DefaultLogLines, 1, MaxLogLines);
        string? newest;
        try
        {
            newest = Directory.Exists(runtime.LogDirectory)
                ? Directory.GetFiles(runtime.LogDirectory, "bridge-*.log").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (newest is null)
        {
            return Results.NotFound();
        }

        try
        {
            using var stream = new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - MaxLogBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var all = text.Split('\n');
            var tail = all.Skip(start > 0 ? 1 : 0).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(count);
            var body = string.Join("\n", tail.Select(LogRedactor.Redact)) + "\n";
            return Results.Text(body, "text/plain; charset=utf-8");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IResult Profiles(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<BridgeOptions>>().Value;
        var store = context.RequestServices.GetRequiredService<ProfileAccountStore>();
        return Results.Ok(new
        {
            configuredProfiles = options.IsRelaunchMode ? options.VrchatProfiles : null,
            profiles = store.All,
            warnings = options.IsRelaunchMode ? store.SameAccountWarnings(options.VrchatProfiles) : [],
        });
    }

    private static IResult GetConfig(HttpContext context, BridgeRuntime runtime)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<BridgeOptions>>().Value;
        var locator = context.RequestServices.GetRequiredService<SteamVrchatLocator>();
        byte[]? bytes;
        JsonObject root;
        try
        {
            bytes = File.Exists(runtime.ConfigPath) ? File.ReadAllBytes(runtime.ConfigPath) : null;
            root = ConfigFile.Parse(bytes);
        }
        catch (ConfigFileException ex)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "configUnreadable", [ex.Message]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "configUnreadable", [ex.Message]);
        }

        var values = new JsonObject();
        try
        {
            if (ConfigFile.FindKey(root, BridgeOptions.SectionName) is { } bridgeKey && root[bridgeKey] is JsonObject section)
            {
                foreach (var setting in Editable.Where(s => s.Name != CandidateKey))
                {
                    if (ConfigFile.FindKey(section, setting.Name) is { } key)
                    {
                        values[setting.Name] = section[key]?.DeepClone();
                    }
                }
            }
        }
        catch (ConfigFileException ex)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "configUnreadable", [ex.Message]);
        }

        return Results.Ok(new
        {
            etag = bytes is null ? ConfigFile.MissingEtag : ConfigFile.EtagOf(bytes),
            values,
            active = ActiveConfig(options),
            editable = Editable.Select(s => new { name = s.Name, kind = s.Kind }),
            executableCandidates = locator.Find().Select(c => new { id = c.Id, display = Display(c.Path) }),
        });
    }

    private static async Task<IResult> PutConfig(HttpContext context, BridgeRuntime runtime)
    {
        using var lease = runtime.Gate.TryEnter("config");
        if (lease is null)
        {
            return Busy(runtime);
        }

        var ifMatch = context.Request.Headers.IfMatch.ToString().Trim().Trim('"');
        if (ifMatch.Length == 0)
        {
            return Problem(StatusCodes.Status428PreconditionRequired, "ifMatchRequired",
                ["Send If-Match with the etag from GET /config."]);
        }

        JsonObject patch;
        try
        {
            patch = await ReadPatchAsync(context);
        }
        catch (ConfigFileException ex)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalidPatch", [ex.Message]);
        }

        var patchProblems = CheckPatch(patch);
        if (patchProblems.Count > 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalidPatch", patchProblems);
        }

        byte[]? before;
        try
        {
            before = File.Exists(runtime.ConfigPath) ? File.ReadAllBytes(runtime.ConfigPath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "configUnreadable", [ex.Message]);
        }

        var currentEtag = before is null ? ConfigFile.MissingEtag : ConfigFile.EtagOf(before);
        if (!string.Equals(ifMatch, currentEtag, StringComparison.Ordinal))
        {
            return Results.Json(new { error = "etagMismatch", etag = currentEtag }, statusCode: StatusCodes.Status412PreconditionFailed);
        }

        var locator = context.RequestServices.GetRequiredService<SteamVrchatLocator>();
        byte[] after;
        try
        {
            after = Apply(before, patch, locator.Find());
        }
        catch (ConfigFileException ex)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "invalidConfig", [ex.Message]);
        }

        var (options, failures) = Validate(after, runtime.StateDirectory);
        if (options is null)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "invalidConfig", failures);
        }

        var active = context.RequestServices.GetRequiredService<IOptions<BridgeOptions>>().Value;
        if (options.IsRelaunchMode != active.IsRelaunchMode)
        {
            var closer = context.RequestServices.GetRequiredService<OwnedClientCloser>();
            var closed = await closer.CloseAsync(requireNoOthers: true, context.RequestAborted);
            if (closed.Refused || closed.NotConfirmed > 0)
            {
                return Results.Json(new
                {
                    error = "vrchatRunning",
                    problems = new[] { "VrchatClientMode was not changed: " + closed.Problem },
                    closed = closed.Closed,
                }, statusCode: StatusCodes.Status409Conflict);
            }
        }

        try
        {
            // Compared once more just before writing: an edit on Windows since must not be lost.
            var now = File.Exists(runtime.ConfigPath) ? File.ReadAllBytes(runtime.ConfigPath) : null;
            if ((now is null ? ConfigFile.MissingEtag : ConfigFile.EtagOf(now)) != currentEtag)
            {
                return Results.Json(new { error = "etagMismatch", etag = now is null ? ConfigFile.MissingEtag : ConfigFile.EtagOf(now) },
                    statusCode: StatusCodes.Status412PreconditionFailed);
            }

            if (before is not null)
            {
                ConfigFile.Backup(runtime.ConfigPath, before, DateTimeOffset.UtcNow);
            }

            ConfigFile.Replace(runtime.ConfigPath, after);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Problem(StatusCodes.Status500InternalServerError, "writeFailed",
                ["config.json was not changed: " + ex.Message]);
        }

        var operationId = Guid.NewGuid().ToString("N");
        var written = ConfigFile.EtagOf(after);
        runtime.RequestRestart(new PendingConfigApply(operationId, written, before));
        return Results.Json(new { operationId, requestedEtag = written, restart = "scheduled" },
            statusCode: StatusCodes.Status202Accepted);
    }

    private static IResult Restart(BridgeRuntime runtime)
    {
        using var lease = runtime.Gate.TryEnter("restart");
        if (lease is null)
        {
            return runtime.Gate.RestartPending
                ? Results.Json(new { restart = "scheduled", coalesced = true }, statusCode: StatusCodes.Status202Accepted)
                : Busy(runtime);
        }

        return runtime.RequestRestart(null) switch
        {
            RestartRequestResult.TooSoon => Results.Json(new
            {
                error = "tooSoon",
                retryAfterSeconds = BridgeRuntime.MinRestartInterval.TotalSeconds,
            }, statusCode: StatusCodes.Status429TooManyRequests),
            RestartRequestResult.Coalesced => Results.Json(new { restart = "scheduled", coalesced = true },
                statusCode: StatusCodes.Status202Accepted),
            _ => Results.Json(new { restart = "scheduled", hostGeneration = runtime.HostGeneration },
                statusCode: StatusCodes.Status202Accepted),
        };
    }

    private static async Task<IResult> Close(HttpContext context, BridgeRuntime runtime)
    {
        using var lease = runtime.Gate.TryEnter("close");
        if (lease is null)
        {
            return Busy(runtime);
        }

        var closer = context.RequestServices.GetRequiredService<OwnedClientCloser>();
        var report = await closer.CloseAsync(requireNoOthers: false, context.RequestAborted);
        return Results.Json(new
        {
            closed = report.Closed,
            notConfirmed = report.NotConfirmed,
            othersRunning = report.Others,
            problem = report.Problem,
        }, statusCode: report.Refused ? StatusCodes.Status409Conflict : StatusCodes.Status200OK);
    }

    /// <summary>The bytes config.json would hold after <paramref name="patch"/>; the rest of the file keeps its values.</summary>
    internal static byte[] Apply(byte[]? before, JsonObject patch, IReadOnlyList<ExecutableCandidate> candidates)
    {
        var root = ConfigFile.Parse(before);
        var rootDuplicates = ConfigFile.CaseDuplicates(root);
        if (rootDuplicates.Count > 0)
        {
            throw new ConfigFileException("config.json has names that differ only in case: " + string.Join(", ", rootDuplicates));
        }

        var bridgeKey = ConfigFile.FindKey(root, BridgeOptions.SectionName) ?? BridgeOptions.SectionName;
        if (root[bridgeKey] is not JsonObject section)
        {
            if (root.ContainsKey(bridgeKey) && root[bridgeKey] is not null)
            {
                throw new ConfigFileException("config.json's Bridge section is not an object.");
            }

            section = new JsonObject();
            root[bridgeKey] = section;
        }

        var duplicates = ConfigFile.CaseDuplicates(section);
        if (duplicates.Count > 0)
        {
            throw new ConfigFileException("config.json's Bridge section has names that differ only in case: " + string.Join(", ", duplicates));
        }

        foreach (var (name, value) in patch)
        {
            var target = name;
            var newValue = value?.DeepClone();
            if (name == CandidateKey)
            {
                var id = value!.GetValue<string>();
                var candidate = candidates.FirstOrDefault(c => c.Id == id)
                    ?? throw new ConfigFileException($"No VRChat.exe candidate '{id}'; GET /config lists them.");
                target = "VrchatExecutable";
                newValue = JsonValue.Create(candidate.Path);
            }

            var existing = ConfigFile.FindKey(section, target);
            if (newValue is null)
            {
                if (existing is not null)
                {
                    section.Remove(existing);
                }
            }
            else
            {
                section[existing ?? target] = newValue;
            }
        }

        return ConfigFile.Serialize(root);
    }

    /// <summary>The options a host would get from <paramref name="bytes"/>, through the same binding and validator.</summary>
    internal static (BridgeOptions? Options, IReadOnlyList<string> Failures) Validate(byte[] bytes, string stateDirectory)
    {
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(bytes)).Build();
        if (configuration["Bridge:StagingDirectory"] is not null)
        {
            return (null, ["Bridge:StagingDirectory is obsolete; remove it on Windows."]);
        }

        var services = new ServiceCollection();
        BridgeHost.ConfigureOptions(services, configuration, stateDirectory);
        using var provider = services.BuildServiceProvider();
        try
        {
            return (provider.GetRequiredService<IOptions<BridgeOptions>>().Value, []);
        }
        catch (OptionsValidationException ex)
        {
            return (null, ex.Failures.ToList());
        }
    }

    internal static IReadOnlyList<string> CheckPatch(JsonObject patch)
    {
        var problems = new List<string>();
        if (patch.Count == 0)
        {
            problems.Add("The patch changes nothing.");
        }

        foreach (var (name, value) in patch)
        {
            var setting = Editable.FirstOrDefault(s => s.Name == name);
            if (setting is null)
            {
                problems.Add($"{name} cannot be changed through the LAN API (settings: {string.Join(", ", Editable.Select(s => s.Name))}).");
                continue;
            }

            if (value is null)
            {
                if (name == CandidateKey)
                {
                    problems.Add($"{CandidateKey} cannot be null.");
                }

                continue;
            }

            if (setting.Check(value) is { } problem)
            {
                problems.Add($"{name} {problem}.");
            }
        }

        return problems;
    }

    private static async Task<JsonObject> ReadPatchAsync(HttpContext context)
    {
        if (context.Request.ContentLength > MaxPatchBytes)
        {
            throw new ConfigFileException($"The patch is larger than {MaxPatchBytes} bytes.");
        }

        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
        {
            if (memory.Length + read > MaxPatchBytes)
            {
                throw new ConfigFileException($"The patch is larger than {MaxPatchBytes} bytes.");
            }

            memory.Write(buffer, 0, read);
        }

        try
        {
            return JsonNode.Parse(memory.ToArray()) as JsonObject
                ?? throw new ConfigFileException("The patch must be a JSON object.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new ConfigFileException("The patch is not valid JSON: " + ex.Message);
        }
    }

    private static IResult Busy(BridgeRuntime runtime) => Results.Json(new
    {
        error = "busy",
        operation = runtime.Gate.Current ?? (runtime.Gate.RestartPending ? "restart" : null),
    }, statusCode: StatusCodes.Status409Conflict);

    private static IResult Problem(int status, string error, IReadOnlyList<string> problems) =>
        Results.Json(new { error, problems }, statusCode: status);

    private static string? OneOf(JsonNode v, params string[] allowed) =>
        v.GetValueKind() == JsonValueKind.String && allowed.Contains(v.GetValue<string>(), StringComparer.Ordinal)
            ? null
            : "must be one of " + string.Join(", ", allowed);

    private static bool IntIn(JsonNode? v, int min, int max) =>
        v is JsonValue value && value.TryGetValue<int>(out var n) && n >= min && n <= max;

    private static bool LongIn(JsonNode? v, long min, long max) =>
        v is JsonValue value && value.TryGetValue<long>(out var n) && n >= min && n <= max;

    /// <summary>A path as /bridge and /config show it: the Windows user name masked.</summary>
    internal static string Display(string path) => string.IsNullOrEmpty(path) ? string.Empty : LogRedactor.Redact(path);

    private static string FormatHost(string address) =>
        System.Net.IPAddress.TryParse(address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address;

    private static long DirectorySize(string path)
    {
        try
        {
            return Directory.Exists(path) ? new DirectoryInfo(path).EnumerateFiles().Sum(f => f.Length) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static long FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return -1;
        }
    }
}
