using Microsoft.Extensions.Configuration;
using System.Net;
using System.Security.Cryptography;
using Serilog;
using VRCRemoteTest.Bridge;
using VRCRemoteTest.Bridge.Lan;
using VRCRemoteTest.Bridge.Runtime;
using VRCRemoteTest.Bridge.VRChat;

var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

// GetFolderPath returns "" when the folder cannot be resolved, and Bridge-private
// state (BridgeOptions.StateDirectory) must never fall back to a relative or
// network location, so refuse to start before anything is created.
if (string.IsNullOrWhiteSpace(localAppData)
    || !Path.IsPathFullyQualified(localAppData)
    || localAppData.StartsWith(@"\\", StringComparison.Ordinal)
    || localAppData.StartsWith("//", StringComparison.Ordinal))
{
    Console.Error.WriteLine(
        $"Cannot resolve a local %LOCALAPPDATA% directory ('{localAppData}'); refusing to start.");
    Environment.ExitCode = 1;
    return;
}

var configDir = Path.Combine(localAppData, "VRCRemoteTest");
var logDir = Path.Combine(configDir, "logs");
Directory.CreateDirectory(configDir);
Directory.CreateDirectory(logDir);

if (args.Length == 1 && args[0] == "pair")
{
    var pairingConfig = new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(configDir, "config.json"), optional: false)
        .Build();
    var bind = pairingConfig["Bridge:LanBindAddress"];
    var port = int.TryParse(pairingConfig["Bridge:LanPort"], out var configuredPort)
        ? configuredPort : 38443;
    if (!IPAddress.TryParse(bind, out var address)
        || IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)
        || address.Equals(IPAddress.IPv6Any))
    {
        Console.Error.WriteLine("Set Bridge:LanBindAddress to the Windows LAN IP before pairing.");
        Environment.ExitCode = 1;
        return;
    }

    var pairing = BridgeIdentity.LoadOrCreate(configDir);
    Console.WriteLine($"URL: https://{(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address)}:{port}");
    Console.WriteLine($"Certificate SHA-256: {pairing.Fingerprint}");
    Console.WriteLine($"Pairing token: {pairing.Token}");
    return;
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logDir, "bridge-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    Log.Information(
        "VRC Remote Test Bridge {Version} starting. Config directory: {ConfigDir}", BridgeRuntime.Version, configDir);

    // Windows Service is deliberately not used (spec section 20 / plan security posture):
    // the Bridge runs in the interactive user session, non-elevated, started via
    // Task Scheduler "At log on" (see scripts/install-bridge.ps1).
    var stateDirectory = Path.Combine(configDir, "state");

    // Acquired once for the whole process, before any host starts, so a second Bridge
    // never touches the state directory or VRChat; restarts keep it.
    using var instanceLock = BridgeInstanceLock.TryAcquire(stateDirectory);
    if (instanceLock is null)
    {
        Log.Fatal(
            "Another VRC Remote Test Bridge is already running (lock file {LockFile} is in use); refusing to start.",
            BridgeInstanceLock.PathFor(stateDirectory));
        Environment.ExitCode = 1;
        return;
    }

    var runtime = new BridgeRuntime(
        configDir,
        BridgeIdentity.LoadOrCreate(configDir),
        AccountDigest.LoadOrCreate(stateDirectory),
        HashExecutable());
    Log.Information("Executable SHA-256: {ExeSha256}", runtime.ExeSha256 ?? "unknown");

    // Ctrl+C or the process being asked to exit stops the whole Bridge, also when it arrives
    // while one host is stopping for a restart and its console lifetime is already gone.
    var processStop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        processStop.Cancel();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => processStop.Cancel();

    Environment.ExitCode = await BridgeHost.RunAsync(runtime, args, test: null, processStop.Token);
}
catch (Exception ex)
{
    Log.Fatal(ex, "VRC Remote Test Bridge terminated unexpectedly.");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

static string? HashExecutable()
{
    try
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return null;
    }
}
