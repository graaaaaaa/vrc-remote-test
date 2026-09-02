using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Deployment;
using VRCRemoteTest.Bridge.VRChat;

var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var configDir = Path.Combine(localAppData, "VRCRemoteTest");
var logDir = Path.Combine(configDir, "logs");
Directory.CreateDirectory(configDir);
Directory.CreateDirectory(logDir);

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
    Log.Information("VRC Remote Test Bridge starting. Config directory: {ConfigDir}", configDir);

    var builder = Host.CreateApplicationBuilder(args);

    // Windows Service is deliberately not used (spec section 20 / plan security posture):
    // the Bridge runs in the interactive user session, non-elevated, started via
    // Task Scheduler "At log on" (see scripts/install-bridge.ps1).
    builder.Configuration.AddJsonFile(
        Path.Combine(configDir, "config.json"),
        optional: true,
        reloadOnChange: false);

    builder.Services.AddSerilog();

    builder.Services
        .AddOptions<BridgeOptions>()
        .Bind(builder.Configuration.GetSection(BridgeOptions.SectionName))
        .PostConfigure(options =>
        {
            if (string.IsNullOrWhiteSpace(options.StagingDirectory))
            {
                options.StagingDirectory = @"C:\VRCRemoteTest";
            }
        });

    builder.Services.AddSingleton<IValidateOptions<BridgeOptions>, BridgeOptionsValidator>();

    builder.Services.AddSingleton<IPackageValidator, PackageValidator>();
    builder.Services.AddSingleton<IWorldInstaller, WorldInstaller>();
    builder.Services.AddSingleton<IResultWriter, ResultWriter>();
    builder.Services.AddSingleton<ICleanupService, CleanupService>();
    builder.Services.AddSingleton<StagingWatcher>();
    builder.Services.AddSingleton<IBridgeWatcher>(sp => sp.GetRequiredService<StagingWatcher>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<StagingWatcher>());

    // Phase 4a: independent of StagingWatcher, see VrchatMonitorService's doc comment.
    builder.Services.AddSingleton<IVrchatProcessMonitor, VrchatProcessMonitor>();
    builder.Services.AddSingleton<IVrchatStatusWriter, VrchatStatusWriter>();
    builder.Services.AddHostedService<VrchatMonitorService>();

    // Phase 4.1: reuses IVrchatProcessMonitor above; only invoked by StagingWatcher when AutoLaunchVrchat is true.
    builder.Services.AddSingleton<IVrchatLauncher, VrchatLauncher>();
    builder.Services.AddSingleton<IVrchatReadinessCoordinator, VrchatReadinessCoordinator>();

    // Phase 5: independent stateless snapshot reader + its own polling BackgroundService.
    builder.Services.AddSingleton<IVrchatLogReader, VrchatLogReader>();
    builder.Services.AddHostedService<VrchatLogService>();

    using var host = builder.Build();

    // Fail fast on invalid configuration (e.g. missing VrchatWorldsDirectory) before
    // the watcher loop ever starts (spec section 11 preflight philosophy, applied
    // Bridge-side).
    _ = host.Services.GetRequiredService<IOptions<BridgeOptions>>().Value;

    await host.RunAsync();
}
catch (OptionsValidationException ex)
{
    Log.Fatal("Configuration error: {Errors}", string.Join("; ", ex.Failures));
    Environment.ExitCode = 1;
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
