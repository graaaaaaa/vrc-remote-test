using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VRCRemoteTest.Bridge.Lan;
using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Runtime;

/// <summary>What happened to the last configuration change made through the LAN API.</summary>
public sealed class ConfigApplyReport
{
    [JsonPropertyName("operationId")]
    public string OperationId { get; init; } = string.Empty;

    /// <summary>The etag of the config.json the change wrote.</summary>
    [JsonPropertyName("requestedEtag")]
    public string RequestedEtag { get; init; } = string.Empty;

    /// <summary>"pending" | "applied" | "rolledBack" | "failed"</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;

    [JsonPropertyName("problems")]
    public IReadOnlyList<string> Problems { get; init; } = [];

    [JsonPropertyName("atUtc")]
    public DateTimeOffset AtUtc { get; init; }
}

/// <summary>A configuration change written to disk whose host has not started yet.</summary>
internal sealed record PendingConfigApply(
    string OperationId, string WrittenEtag, byte[]? PreviousBytes);

public enum RestartRequestResult
{
    Scheduled,

    /// <summary>A restart was already scheduled; this request is that one.</summary>
    Coalesced,

    /// <summary>The host started less than <see cref="BridgeRuntime.MinRestartInterval"/> ago.</summary>
    TooSoon,
}

/// <summary>
/// Everything that lives as long as the Bridge process, across the hosts it runs one after
/// another: the identity and the pairing token, the account digest key, the operation gate,
/// the version, and the record of restarts and configuration changes. The instance lock and
/// the logger are held by Program around it. A host (DI, options, hosted services, Kestrel)
/// is built from config.json each time, so a restart applies a changed configuration
/// without touching the process, the scheduled task or the pairing.
/// </summary>
public sealed class BridgeRuntime
{
    public const string ConfigFileName = "config.json";
    internal const string ApplyReportFileName = "config-apply.json";

    /// <summary>A plain restart is refused this soon after a host started (a configuration change is not).</summary>
    public static readonly TimeSpan MinRestartInterval = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };

    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private TaskCompletionSource _restartSignal = NewSignal();
    private bool _restartScheduled;
    private ConfigApplyReport? _lastConfigApply;

    public BridgeRuntime(
        string configDirectory,
        BridgeIdentity identity,
        AccountDigest accountDigest,
        string? exeSha256,
        Func<DateTimeOffset>? utcNow = null)
    {
        ConfigDirectory = configDirectory;
        Identity = identity;
        AccountDigest = accountDigest;
        ExeSha256 = exeSha256;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        ProcessStartedAtUtc = _utcNow();
        _lastConfigApply = LoadApplyReport();
    }

    public string ConfigDirectory { get; }

    public string ConfigPath => Path.Combine(ConfigDirectory, ConfigFileName);

    /// <summary>Fixed, never configurable: see BridgeOptions.StateDirectory.</summary>
    public string StateDirectory => Path.Combine(ConfigDirectory, "state");

    public string LogDirectory => Path.Combine(ConfigDirectory, "logs");

    public BridgeIdentity Identity { get; }

    public AccountDigest AccountDigest { get; }

    public BridgeOperationGate Gate { get; } = new();

    public static string Version { get; } =
        typeof(BridgeRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BridgeRuntime).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public string? ExeSha256 { get; }

    public DateTimeOffset ProcessStartedAtUtc { get; }

    /// <summary>1 for the first host, one more for each restart.</summary>
    public int HostGeneration { get; private set; }

    public DateTimeOffset? HostStartedAtUtc { get; private set; }

    /// <summary>The etag of the config.json bytes the current host was built from.</summary>
    public string? ActiveConfigEtag { get; private set; }

    public ConfigApplyReport? LastConfigApply
    {
        get { lock (_sync) return _lastConfigApply; }
    }

    internal PendingConfigApply? PendingApply { get; private set; }

    public static string? HashFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks the running host to stop so the next one is built from config.json.
    /// <paramref name="apply"/> is the configuration change that needs it; a plain request
    /// (null) is refused within <see cref="MinRestartInterval"/> of the last start.
    /// </summary>
    internal RestartRequestResult RequestRestart(PendingConfigApply? apply)
    {
        lock (_sync)
        {
            if (_restartScheduled)
            {
                if (apply is not null)
                {
                    PendingApply = apply;
                    _lastConfigApply = Pending(apply);
                }

                return RestartRequestResult.Coalesced;
            }

            if (apply is null && HostStartedAtUtc is { } started && _utcNow() - started < MinRestartInterval)
            {
                return RestartRequestResult.TooSoon;
            }

            if (apply is not null)
            {
                PendingApply = apply;
                _lastConfigApply = Pending(apply);
            }

            _restartScheduled = true;
            Gate.MarkRestartPending();
            _restartSignal.TrySetResult();
            return RestartRequestResult.Scheduled;
        }
    }

    internal Task RestartRequested
    {
        get { lock (_sync) return _restartSignal.Task; }
    }

    internal void OnHostStarted(string? activeConfigEtag)
    {
        lock (_sync)
        {
            HostGeneration++;
            HostStartedAtUtc = _utcNow();
            ActiveConfigEtag = activeConfigEtag;
            _restartScheduled = false;
            _restartSignal = NewSignal();
            if (PendingApply is { } apply)
            {
                SetReport(new ConfigApplyReport
                {
                    OperationId = apply.OperationId,
                    RequestedEtag = apply.WrittenEtag,
                    Outcome = activeConfigEtag == apply.WrittenEtag ? "applied" : "failed",
                    Problems = activeConfigEtag == apply.WrittenEtag
                        ? []
                        : ["config.json changed on Windows after the change was written; the host runs that file instead."],
                    AtUtc = _utcNow(),
                });
                PendingApply = null;
            }
        }

        Gate.HostStarted();
    }

    internal void ReportRolledBack(PendingConfigApply apply, string problem)
    {
        lock (_sync)
        {
            SetReport(new ConfigApplyReport
            {
                OperationId = apply.OperationId,
                RequestedEtag = apply.WrittenEtag,
                Outcome = "rolledBack",
                Problems = [problem],
                AtUtc = _utcNow(),
            });
            PendingApply = null;
        }
    }

    internal void ReportFailed(PendingConfigApply apply, IReadOnlyList<string> problems)
    {
        lock (_sync)
        {
            SetReport(new ConfigApplyReport
            {
                OperationId = apply.OperationId,
                RequestedEtag = apply.WrittenEtag,
                Outcome = "failed",
                Problems = problems,
                AtUtc = _utcNow(),
            });
            PendingApply = null;
        }
    }

    private ConfigApplyReport Pending(PendingConfigApply apply) => new()
    {
        OperationId = apply.OperationId,
        RequestedEtag = apply.WrittenEtag,
        Outcome = "pending",
        AtUtc = _utcNow(),
    };

    /// <summary>Kept on disk too, so a Bridge that had to stop shows why when it is started again.</summary>
    private void SetReport(ConfigApplyReport report)
    {
        _lastConfigApply = report;
        try
        {
            var directory = Path.Combine(StateDirectory, "status");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, ApplyReportFileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(report, ReportOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory report still answers /bridge.
        }
    }

    private ConfigApplyReport? LoadApplyReport()
    {
        try
        {
            var path = Path.Combine(StateDirectory, "status", ApplyReportFileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024)
            {
                return null;
            }

            return JsonSerializer.Deserialize<ConfigApplyReport>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
