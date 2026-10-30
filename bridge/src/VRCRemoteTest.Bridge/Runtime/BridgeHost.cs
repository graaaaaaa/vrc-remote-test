using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Deployment;
using VRCRemoteTest.Bridge.Lan;
using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Runtime;

/// <summary>Test seams for <see cref="BridgeHost"/>; production passes none.</summary>
public sealed class BridgeHostTestOptions
{
    /// <summary>Listen here instead of LanBindAddress:LanPort (tests use loopback, which the validator refuses).</summary>
    public IPEndPoint? ListenOverride { get; init; }

    /// <summary>Replaces services after the production registrations (fakes for the VRChat side).</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>How long a bind failure is retried; 30 s in production.</summary>
    public TimeSpan? BindRetryWindow { get; init; }
}

/// <summary>
/// Builds and runs the Bridge's hosts one after another in one process
/// (<see cref="BridgeRuntime"/>). A restart stops and disposes the running host, then
/// builds the next one from config.json: options, the coordinator for VrchatClientMode,
/// hosted services and Kestrel are all new; the identity, the gate and the instance lock
/// are not. The hosted services that change state on disk wait for ApplicationStarted
/// (<see cref="HostReady"/>), so a host whose Kestrel cannot listen never recovers builds,
/// cleans up Worlds or replaces the published logs.
///
/// A host that cannot start after a configuration change made through the LAN API is
/// replaced by one built from the previous config.json, but only while config.json is
/// still the file that change wrote: a file edited on Windows meanwhile is never
/// overwritten. When no host can start, the process ends with exit code 1 as before.
/// </summary>
public static class BridgeHost
{
    private static readonly TimeSpan DefaultBindRetryWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    public static WebApplication Build(
        BridgeRuntime runtime, string[] args, BridgeHostTestOptions? test, out string? configEtag)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Read once, so the etag is that of exactly the bytes the options come from.
        var configBytes = File.Exists(runtime.ConfigPath) ? File.ReadAllBytes(runtime.ConfigPath) : null;
        configEtag = configBytes is null ? null : ConfigFile.EtagOf(configBytes);
        if (configBytes is not null)
        {
            builder.Configuration.AddJsonStream(new MemoryStream(configBytes));
        }

        builder.Services.AddSerilog();
        ConfigureOptions(builder.Services, builder.Configuration, runtime.StateDirectory);

        if (builder.Configuration["Bridge:StagingDirectory"] is not null)
            throw new InvalidOperationException("Bridge:StagingDirectory is obsolete; remove it and pair LAN Direct.");

        var listen = test?.ListenOverride;
        if (listen is null)
        {
            var lanConfig = builder.Configuration.GetSection(BridgeOptions.SectionName).Get<BridgeOptions>() ?? new BridgeOptions();
            if (!IPAddress.TryParse(lanConfig.LanBindAddress, out var lanAddress))
            {
                throw new InvalidOperationException("Bridge:LanBindAddress must name the Windows LAN IP.");
            }

            listen = new IPEndPoint(lanAddress, lanConfig.LanPort);
        }

        builder.WebHost.ConfigureKestrel(server =>
            server.Listen(listen, endpoint => endpoint.UseHttps(runtime.Identity.Certificate)));

        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton(runtime.Gate);
        builder.Services.AddSingleton(runtime.AccountDigest);

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

        // Phase 4.1 / multi-client: reuses IVrchatProcessMonitor above; only invoked by StagingWatcher when AutoLaunchVrchat is true.
        // VrchatClientMode picks the flow: Reload (same account, --watch-worlds) or Relaunch
        // (separate accounts, the Bridge closes and relaunches the clients it recorded). The
        // choice is made again by every host, so a restart applies a changed mode.
        builder.Services.AddSingleton<IVrchatLauncher, VrchatLauncher>();
        builder.Services.AddSingleton<IManagedClientStore, ManagedClientStore>();
        builder.Services.AddSingleton<ReloadClientStore>();
        builder.Services.AddSingleton<VrchatClientCoordinator>();
        builder.Services.AddSingleton<VrchatRelaunchCoordinator>();
        builder.Services.AddSingleton<IVrchatClientCoordinator>(sp =>
            sp.GetRequiredService<IOptions<BridgeOptions>>().Value.IsRelaunchMode
                ? sp.GetRequiredService<VrchatRelaunchCoordinator>()
                : sp.GetRequiredService<VrchatClientCoordinator>());
        builder.Services.AddSingleton<OwnedClientCloser>();

        // Phase 5: snapshot reader + its own polling BackgroundService. Since the client
        // observation plan's P1 every client has a slot (VrchatClientRoster, observation only) and
        // its own log, state and marker lines (VrchatClientObserver); vrchat-latest.log stays on
        // the first VRChat client's log (in Relaunch mode the first one the record lists). While a
        // Relaunch replaces the clients, the deploy flow sets VrchatLogNotice and the service
        // publishes it instead of the logs.
        builder.Services.AddSingleton<VrchatClientRoster>();
        builder.Services.AddSingleton<IVrchatLogReader, VrchatLogReader>();
        builder.Services.AddSingleton<ProfileAccountStore>();
        builder.Services.AddSingleton<VrchatClientObserver>();
        builder.Services.AddSingleton<LogSourceRegistry>();
        builder.Services.AddSingleton<VrchatLogNotice>();
        builder.Services.AddHostedService<VrchatLogService>();

        builder.Services.AddSingleton<SteamVrchatLocator>();

        test?.ConfigureServices?.Invoke(builder.Services);

        var app = builder.Build();

        // Fail fast on invalid configuration (e.g. missing VrchatWorldsDirectory) before
        // the watcher loop ever starts (spec section 11 preflight philosophy, applied
        // Bridge-side).
        var options = app.Services.GetRequiredService<IOptions<BridgeOptions>>().Value;
        BridgeLanApi.Map(app, options, runtime.Identity, app.Services.GetRequiredService<LogSourceRegistry>(), runtime);
        return app;
    }

    /// <summary>The options binding every host uses, and the one a configuration change is checked with.</summary>
    internal static void ConfigureOptions(IServiceCollection services, IConfiguration configuration, string stateDirectory)
    {
        services
            .AddOptions<BridgeOptions>()
            .Bind(configuration.GetSection(BridgeOptions.SectionName))
            .PostConfigure(options =>
            {
                // Fixed rather than configurable: see BridgeOptions.StateDirectory.
                options.StateDirectory = stateDirectory;
            });
        services.AddSingleton<IValidateOptions<BridgeOptions>, BridgeOptionsValidator>();
    }

    /// <summary>
    /// Runs hosts until the process is asked to stop (<paramref name="cancellationToken"/>, or
    /// the host's own console lifetime) and returns the exit code: 0 after a normal stop,
    /// 1 when no host could start.
    /// </summary>
    public static async Task<int> RunAsync(
        BridgeRuntime runtime, string[] args, BridgeHostTestOptions? test, CancellationToken cancellationToken)
    {
        while (true)
        {
            var started = await TryStartAsync(runtime, args, test, cancellationToken).ConfigureAwait(false);
            if (started.Host is null)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return 0;
                }

                if (runtime.PendingApply is { } apply)
                {
                    if (RestorePrevious(runtime, apply))
                    {
                        Log.Error(
                            "The host could not start with the configuration change {OperationId} ({Problem}); config.json was restored and the host is started again.",
                            apply.OperationId, started.Problem);
                        runtime.ReportRolledBack(apply, started.Problem!);
                        continue;
                    }

                    runtime.ReportFailed(apply,
                    [
                        started.Problem!,
                        "config.json was not restored: it was no longer the file the change wrote, or the previous file could not be written back.",
                    ]);
                }

                Log.Fatal("The Bridge could not start: {Problem}", started.Problem);
                return 1;
            }

            await using var host = started.Host;
            runtime.OnHostStarted(started.ConfigEtag);
            Log.Information(
                "VRC Remote Test Bridge {Version} serving (host {Generation}, exe SHA-256 {ExeSha256}).",
                BridgeRuntime.Version, runtime.HostGeneration, runtime.ExeSha256 ?? "unknown");

            var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            var restart = runtime.RestartRequested;
            var stopped = WhenCancelled(stopping);
            var cancelled = WhenCancelled(cancellationToken);
            var reason = await Task.WhenAny(restart, stopped, cancelled).ConfigureAwait(false);

            try
            {
                using var stopTimeout = new CancellationTokenSource(StopTimeout);
                await host.StopAsync(stopTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException)
            {
                Log.Warning(ex, "The host did not stop cleanly within {Seconds} s.", StopTimeout.TotalSeconds);
            }

            if (reason != restart)
            {
                return 0;
            }

            Log.Information("Restarting the host (host {Generation} stopped).", runtime.HostGeneration);
        }
    }

    private sealed record StartResult(WebApplication? Host, string? ConfigEtag, string? Problem);

    private static async Task<StartResult> TryStartAsync(
        BridgeRuntime runtime, string[] args, BridgeHostTestOptions? test, CancellationToken cancellationToken)
    {
        var window = test?.BindRetryWindow ?? DefaultBindRetryWindow;
        var deadline = DateTime.UtcNow + window;
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            WebApplication? app = null;
            try
            {
                app = Build(runtime, args, test, out var etag);
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
                return new StartResult(app, etag, null);
            }
            catch (OptionsValidationException ex)
            {
                await DisposeQuietlyAsync(app).ConfigureAwait(false);
                return new StartResult(null, null, "Configuration error: " + string.Join("; ", ex.Failures));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await DisposeQuietlyAsync(app).ConfigureAwait(false);
                return new StartResult(null, null, "Stopped while starting.");
            }
            catch (Exception ex) when (IsBindFailure(ex) && DateTime.UtcNow < deadline)
            {
                await DisposeQuietlyAsync(app).ConfigureAwait(false);
                Log.Warning("The Bridge's address is still in use; trying again in {Seconds} s.", delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 5));
            }
            catch (Exception ex)
            {
                await DisposeQuietlyAsync(app).ConfigureAwait(false);
                return new StartResult(null, null, ex.Message);
            }
        }
    }

    /// <summary>
    /// Puts back the bytes the change replaced, only while config.json is still what the
    /// change wrote. False when it was edited since, or the write fails.
    /// </summary>
    private static bool RestorePrevious(BridgeRuntime runtime, PendingConfigApply apply)
    {
        try
        {
            if (BridgeRuntime.HashFile(runtime.ConfigPath) != apply.WrittenEtag)
            {
                return false;
            }

            if (apply.PreviousBytes is null)
            {
                File.Delete(runtime.ConfigPath);
            }
            else
            {
                ConfigFile.Replace(runtime.ConfigPath, apply.PreviousBytes);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Could not restore config.json after a failed configuration change.");
            return false;
        }
    }

    private static bool IsBindFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                || e.GetType().Name == "AddressInUseException")
            {
                return true;
            }
        }

        return false;
    }

    private static Task WhenCancelled(CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => completion.TrySetResult());
        return completion.Task;
    }

    private static async Task DisposeQuietlyAsync(WebApplication? app)
    {
        if (app is null)
        {
            return;
        }

        try
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Disposing a host that did not start failed.");
        }
    }
}

/// <summary>
/// Lets a hosted service wait until its host has fully started (Kestrel listening
/// included) before it changes anything on disk, so a host that fails to start does no
/// irreversible work. Null lifetime (unit tests) waits for nothing.
/// </summary>
internal static class HostReady
{
    public static async Task WaitAsync(IHostApplicationLifetime? lifetime, CancellationToken stoppingToken)
    {
        if (lifetime is null || lifetime.ApplicationStarted.IsCancellationRequested)
        {
            return;
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var onStarted = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var onStopping = stoppingToken.Register(() => started.TrySetCanceled(stoppingToken));
        await started.Task.ConfigureAwait(false);
    }
}
