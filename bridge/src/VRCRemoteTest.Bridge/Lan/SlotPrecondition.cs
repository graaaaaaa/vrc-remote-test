using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace VRCRemoteTest.Bridge.Lan;

/// <summary>
/// What the caller of POST /api/v1/clients/{slot}/close or /launch expects to find (docs/plans/r1b-contract.md
/// §1.3): this Bridge process and host, its configuration, its operation revision, the current
/// batch and the client generation. The request does nothing when any of it differs (412), so a
/// close or launch prepared from an earlier view cannot act on what changed since.
///
/// An empty body is the old request, without any of this. A body that is not exactly the
/// contract's shape is refused (400), never read as an empty one. This narrows what a close or
/// launch can do; it is not authentication.
/// </summary>
public sealed record SlotPrecondition(
    string BridgeInstanceId,
    int HostGeneration,
    string? ActiveConfigEtag,
    string? SavedConfigEtag,
    long OperationRevision,
    string? BuildId,
    long ClientGeneration)
{
    public const string Feature = "slotPreconditions.v1";
    internal const int MaxBodyBytes = 4096;
    private const int MaxEtagLength = 128;

    // \z, not $: in .NET, $ also matches before a final newline.
    private static readonly Regex InstanceIdPattern = new("^[0-9a-f]{32}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex BuildIdPattern = new("^[0-9A-Za-z_-]{1,128}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex OperationIdPattern = new("^[A-Za-z0-9_-]{1,64}\\z", RegexOptions.CultureInvariant);

    internal static readonly string[] Keys =
    [
        "bridgeInstanceId", "hostGeneration", "activeConfigEtag", "savedConfigEtag",
        "operationRevision", "buildId", "clientGeneration",
    ];

    /// <summary>The request body, read by what arrives, not by Content-Length (chunked too), up to one byte past the limit.</summary>
    internal static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > MaxBodyBytes)
            {
                break;
            }
        }

        return memory.ToArray();
    }

    /// <summary>
    /// The body of a close or launch: (null, null, null) for an empty body (the old request);
    /// the precondition and the optional operation ID; or the problems that refuse it.
    /// </summary>
    internal static (SlotPrecondition? Precondition, string? OperationId, IReadOnlyList<string>? Problems) Parse(
        byte[] body, string? contentType)
    {
        if (body.Length == 0)
        {
            return (null, null, null);
        }

        if (body.Length > MaxBodyBytes)
        {
            return Refuse($"The body is larger than {MaxBodyBytes} bytes.");
        }

        if (!IsJson(contentType))
        {
            return Refuse("A body must be sent as Content-Type: application/json.");
        }

        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Refuse("The body must be a JSON object.");
            }

            var problems = new List<string>();
            var top = Properties(root, problems, "the body");
            foreach (var name in top.Keys.Where(k => k is not ("expect" or "operationId")))
            {
                problems.Add($"Unknown key '{Clip(name)}' in the body (only expect and operationId).");
            }

            string? operationId = null;
            if (top.TryGetValue("operationId", out var idElement))
            {
                if (idElement.ValueKind != JsonValueKind.String || !OperationIdPattern.IsMatch(idElement.GetString()!))
                {
                    problems.Add("operationId must be 1 to 64 letters, digits, '_' or '-'.");
                }
                else
                {
                    operationId = idElement.GetString();
                }
            }

            if (!top.TryGetValue("expect", out var expect) || expect.ValueKind != JsonValueKind.Object)
            {
                problems.Add("expect is required and must be an object.");
                return Refuse(problems);
            }

            var fields = Properties(expect, problems, "expect");
            foreach (var name in fields.Keys.Where(k => !Keys.Contains(k, StringComparer.Ordinal)))
            {
                problems.Add($"Unknown key '{Clip(name)}' in expect.");
            }

            foreach (var name in Keys.Where(k => !fields.ContainsKey(k)))
            {
                problems.Add($"expect.{name} is missing.");
            }

            if (problems.Count > 0)
            {
                return Refuse(problems);
            }

            var instanceId = StringMatching(fields["bridgeInstanceId"], InstanceIdPattern, nullable: false, "bridgeInstanceId", problems);
            var host = Integer(fields["hostGeneration"], 1, int.MaxValue, "hostGeneration", problems);
            var active = Etag(fields["activeConfigEtag"], "activeConfigEtag", problems);
            var saved = Etag(fields["savedConfigEtag"], "savedConfigEtag", problems);
            var revision = Integer(fields["operationRevision"], 0, long.MaxValue, "operationRevision", problems);
            var buildId = StringMatching(fields["buildId"], BuildIdPattern, nullable: true, "buildId", problems);
            var generation = Integer(fields["clientGeneration"], 1, long.MaxValue, "clientGeneration", problems);
            if (problems.Count > 0)
            {
                return Refuse(problems);
            }

            return (new SlotPrecondition(instanceId!, (int)host, active, saved, revision, buildId, generation), operationId, null);
        }
        catch (JsonException)
        {
            return Refuse("The body is not valid JSON.");
        }
    }

    /// <summary>The names of the process-level values that differ from <paramref name="current"/> (clientGeneration is the slot's, checked later).</summary>
    internal IReadOnlyList<string> Mismatched(SlotPrecondition current)
    {
        var mismatched = new List<string>();
        if (!string.Equals(BridgeInstanceId, current.BridgeInstanceId, StringComparison.Ordinal)) mismatched.Add("bridgeInstanceId");
        if (HostGeneration != current.HostGeneration) mismatched.Add("hostGeneration");
        if (!string.Equals(ActiveConfigEtag, current.ActiveConfigEtag, StringComparison.Ordinal)) mismatched.Add("activeConfigEtag");
        if (!string.Equals(SavedConfigEtag, current.SavedConfigEtag, StringComparison.Ordinal)) mismatched.Add("savedConfigEtag");
        if (OperationRevision != current.OperationRevision) mismatched.Add("operationRevision");
        if (!string.Equals(BuildId, current.BuildId, StringComparison.Ordinal)) mismatched.Add("buildId");
        return mismatched;
    }

    private static bool IsJson(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        var media = contentType.Split(';', 2)[0].Trim();
        return string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The object's properties; a repeated name (in any case form, compared exactly) is a problem.</summary>
    private static Dictionary<string, JsonElement> Properties(JsonElement element, List<string> problems, string where)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                problems.Add($"'{Clip(property.Name)}' appears more than once in {where}.");
            }
        }

        return properties;
    }

    private static string? StringMatching(JsonElement value, Regex pattern, bool nullable, string name, List<string> problems)
    {
        if (nullable && value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && pattern.IsMatch(value.GetString()!))
        {
            return value.GetString();
        }

        problems.Add($"expect.{name} has the wrong type or form.");
        return null;
    }

    private static string? Etag(JsonElement value, string name, List<string> problems)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length is > 0 and <= MaxEtagLength)
        {
            return value.GetString();
        }

        problems.Add($"expect.{name} must be a string of 1 to {MaxEtagLength} characters, or null.");
        return null;
    }

    private static long Integer(JsonElement value, long min, long max, string name, List<string> problems)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n >= min && n <= max
            && !value.GetRawText().Contains('.') && !value.GetRawText().Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            return n;
        }

        problems.Add($"expect.{name} must be an integer from {min}.");
        return 0;
    }

    private static string Clip(string text) => text.Length <= 40 ? text : text[..40] + "...";

    private static (SlotPrecondition?, string?, IReadOnlyList<string>?) Refuse(params string[] problems) => (null, null, problems);

    private static (SlotPrecondition?, string?, IReadOnlyList<string>?) Refuse(List<string> problems) => (null, null, problems);
}
