using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Deployment;
using VRCRemoteTest.Bridge.Protocol;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Relaunch mode end to end (multi-client plan section 13.3, steps 1-9): StagingWatcher on
/// a real temp filesystem with the real coordinator and the real managed-clients.json,
/// over fake VRChat processes and a fake clock that only the Bridge's own waits advance.
/// A "restart" is a new watcher, coordinator and store over the same directories.
/// </summary>
public class StagingWatcherRelaunchTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 17, 9, 0, 0, 100, DateTimeKind.Utc);

    private readonly string _tempDir;
    private readonly string _stagingDir;
    private readonly string _stateDir;
    private readonly string _worldsDir;
    private readonly FakeVrchatProcessMonitor _monitor = new();
    private readonly FakeVrchatLauncher _launcher = new();
    private readonly RecordingWorldInstaller _installer = new();
    private readonly VrchatLogNotice _logNotice = new();
    private DateTime _now = T0;

    public StagingWatcherRelaunchTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-relaunch-").FullName;
        _stagingDir = Path.Combine(_tempDir, "staging");
        _stateDir = Path.Combine(_tempDir, "state");
        _worldsDir = Path.Combine(_tempDir, "worlds");
        Directory.CreateDirectory(_worldsDir);

        _launcher.Clock = () => _now;
        _launcher.OnLaunch = pid => Running(pid, _launcher.Launched[^1].CreationTimeUtc);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private BridgeOptions Options(int[] profiles, int retainBuilds) => new()
    {
        StagingDirectory = _stagingDir,
        StateDirectory = _stateDir,
        VrchatWorldsDirectory = _worldsDir,
        MaxArtifactSizeBytes = 1024,
        RetainBuilds = retainBuilds,
        AutoLaunchVrchat = true,
        VrchatExecutable = @"C:\VRChat\VRChat.exe",
        VrchatClientMode = "Relaunch",
        VrchatProfiles = profiles,
    };

    private StagingWatcher CreateWatcher(
        int[]? profiles = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IManagedClientStore? store = null,
        int retainBuilds = 10)
    {
        var options = Microsoft.Extensions.Options.Options.Create(Options(profiles ?? [0, 1], retainBuilds));
        var coordinator = new VrchatRelaunchCoordinator(
            options,
            _monitor,
            _launcher,
            store ?? new ManagedClientStore(_stateDir),
            _logNotice,
            NullLogger<VrchatRelaunchCoordinator>.Instance,
            delay ?? ((wait, token) =>
            {
                token.ThrowIfCancellationRequested();
                _now += wait;
                return Task.CompletedTask;
            }),
            () => null,
            () => _now,
            TimeZoneInfo.Utc);

        return new StagingWatcher(
            options,
            new PackageValidator(),
            _installer,
            new ResultWriter(),
            new CleanupService(),
            coordinator,
            _logNotice,
            NullLogger<StagingWatcher>.Instance);
    }

    private FakeVrchatProcess Running(int processId, DateTime creationTimeUtc)
    {
        var process = _monitor.AddProcess(processId, creationTimeUtc);
        process.Clock = () => _now;
        return process;
    }

    private void WriteIncomingBuild(string buildId)
    {
        var bytes = Encoding.UTF8.GetBytes($"bundle-{buildId}");
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);
        File.WriteAllBytes(Path.Combine(incoming, $"{buildId}.vrcw"), bytes);
        File.WriteAllText(
            Path.Combine(incoming, $"{buildId}.ready.json"),
            JsonSerializer.Serialize(new BuildManifest
            {
                ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                BuildId = buildId,
                FileName = $"{buildId}.vrcw",
                Size = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                CreatedAtUtc = DateTimeOffset.UtcNow,
            }));
    }

    private BuildResult ReadResult(string buildId) =>
        JsonSerializer.Deserialize<BuildResult>(
            File.ReadAllText(Path.Combine(_stagingDir, "results", $"{buildId}.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private bool ResultExists(string buildId) => File.Exists(Path.Combine(_stagingDir, "results", $"{buildId}.json"));

    private string DeployedPath(string buildId) => Path.Combine(_worldsDir, $"vrc-remote-{buildId}.vrcw");

    private string ArchivedManifest(string buildId) => Path.Combine(_stateDir, "archive", $"{buildId}.ready.json");

    private string FailedManifest(string buildId) => Path.Combine(_stateDir, "failed", $"{buildId}.ready.json");

    private string ProcessingManifest(string buildId) => Path.Combine(_stateDir, "processing", $"{buildId}.ready.json");

    private string RecordPath => Path.Combine(_stateDir, ManagedClientStore.FileName);

    private IReadOnlyList<ManagedClient> Record() => new ManagedClientStore(_stateDir).Load().Clients;

    private List<FakeVrchatProcess> RunningProcesses() => _monitor.Processes.Where(p => p.IsRunning).ToList();

    [Fact]
    public async Task The_first_build_is_deployed_and_archived_before_one_client_per_profile_is_launched_and_recorded()
    {
        var stateAtEachLaunch = new List<(bool Deployed, bool Archived, bool ResultWritten)>();
        _launcher.OnLaunch = pid =>
        {
            stateAtEachLaunch.Add((File.Exists(DeployedPath("b1")), File.Exists(ArchivedManifest("b1")), ResultExists("b1")));
            Running(pid, _launcher.Launched[^1].CreationTimeUtc);
        };
        WriteIncomingBuild("b1");

        await CreateWatcher(profiles: [2, 0]).RunOnceAsync(CancellationToken.None);

        stateAtEachLaunch.Should().Equal((true, true, false), (true, true, false));
        _launcher.Requests.Select(r => r.Profile).Should().Equal(2, 0);
        _launcher.Requests.Should().AllSatisfy(r =>
        {
            r.WatchWorlds.Should().BeFalse();
            r.WorldPath.Should().Be(Path.GetFullPath(DeployedPath("b1")));
        });
        ReadResult("b1").Status.Should().Be("deployed");
        Record().Select(c => (c.ProcessId, c.Profile, c.LaunchOrder)).Should().Equal(
            (_launcher.Launched[0].ProcessId, 2, 0), (_launcher.Launched[1].ProcessId, 0, 1));
        Record().Select(c => c.CreationTimeUtc).Should().Equal(_launcher.Launched.Select(l => l.CreationTimeUtc));
    }

    [Fact]
    public async Task The_next_build_closes_the_recorded_clients_before_deploying_and_launches_a_new_batch()
    {
        var watcher = CreateWatcher();
        WriteIncomingBuild("b1");
        await watcher.RunOnceAsync(CancellationToken.None);
        var firstBatch = RunningProcesses();
        var runningAtInstall = new List<int>();
        _installer.OnInstall = () => runningAtInstall.Add(RunningProcesses().Count);

        WriteIncomingBuild("b2");
        await watcher.RunOnceAsync(CancellationToken.None);

        firstBatch.Should().HaveCount(2).And.AllSatisfy(p =>
        {
            p.IsRunning.Should().BeFalse();
            p.KillCalls.Should().Be(0);
        });
        runningAtInstall.Should().Equal(0);
        ReadResult("b2").Status.Should().Be("deployed");
        _launcher.Requests.Should().HaveCount(4);
        _launcher.Requests.Skip(2).Should().AllSatisfy(r => r.WorldPath.Should().Be(Path.GetFullPath(DeployedPath("b2"))));
        Record().Select(c => c.ProcessId).Should().Equal(_launcher.Launched.Skip(2).Select(l => l.ProcessId));
        RunningProcesses().Should().HaveCount(2);
    }

    [Fact]
    public async Task Two_builds_in_one_scan_relaunch_twice_without_a_settle_wait()
    {
        var watcher = CreateWatcher();
        WriteIncomingBuild("b1");
        WriteIncomingBuild("b2");

        await watcher.RunOnceAsync(CancellationToken.None);

        ReadResult("b1").Status.Should().Be("deployed");
        ReadResult("b2").Status.Should().Be("deployed");
        _launcher.Requests.Should().HaveCount(4);
        (_now - T0).Should().BeLessThan(TimeSpan.FromSeconds(30), "3 s between launches and about 2 s to close");
    }

    [Fact]
    public async Task A_VRChat_started_by_hand_fails_the_build_without_deploying_closing_or_launching()
    {
        var byHand = Running(777, T0.AddMinutes(-10));
        WriteIncomingBuild("b1");

        await CreateWatcher().RunOnceAsync(CancellationToken.None);

        var result = ReadResult("b1");
        result.ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
        result.ErrorMessage.Should().Contain("777");
        byHand.CloseCalls.Should().Be(0);
        _launcher.LaunchCalled.Should().BeFalse();
        Directory.GetFiles(_worldsDir).Should().BeEmpty();
        File.Exists(FailedManifest("b1")).Should().BeTrue();
    }

    [Fact]
    public async Task A_client_that_cannot_be_closed_fails_the_build_before_the_install()
    {
        var watcher = CreateWatcher();
        WriteIncomingBuild("b1");
        await watcher.RunOnceAsync(CancellationToken.None);
        var stuck = RunningProcesses()[1];
        stuck.CloseTakes = null;
        stuck.IgnoresKill = true;

        WriteIncomingBuild("b2");
        await watcher.RunOnceAsync(CancellationToken.None);

        ReadResult("b2").ErrorCode.Should().Be(ErrorCode.VrchatStartFailed);
        _installer.Installs.Should().Equal("b1");
        File.Exists(FailedManifest("b2")).Should().BeTrue();
        Record().Should().ContainSingle().Which.ProcessId.Should().Be(stuck.ProcessId);
        _launcher.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_Bridge_stopped_between_two_launches_closes_the_recorded_client_on_the_next_build_after_its_restart()
    {
        using var cts = new CancellationTokenSource();
        var stopping = CreateWatcher(delay: (_, token) =>
        {
            cts.Cancel(); // the Bridge is stopped while waiting to launch the second client
            return Task.Delay(Timeout.Infinite, token);
        });
        WriteIncomingBuild("b1");

        var act = async () => await stopping.RunOnceAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        ResultExists("b1").Should().BeFalse("Unity reports RESULT_TIMEOUT");
        File.Exists(ArchivedManifest("b1")).Should().BeTrue();
        var firstClient = RunningProcesses().Should().ContainSingle().Subject;
        Record().Should().ContainSingle().Which.ProcessId.Should().Be(firstClient.ProcessId);

        var restarted = CreateWatcher();
        await restarted.RecoverProcessingAsync(CancellationToken.None);
        WriteIncomingBuild("b2");
        await restarted.RunOnceAsync(CancellationToken.None);

        _installer.Installs.Should().Equal("b1", "b2");
        firstClient.IsRunning.Should().BeFalse();
        ReadResult("b2").Status.Should().Be("deployed");
        RunningProcesses().Should().HaveCount(2);
        Record().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_Bridge_stopped_while_closing_deploys_nothing_and_redoes_the_build_after_its_restart()
    {
        var watcher = CreateWatcher();
        WriteIncomingBuild("b1");
        await watcher.RunOnceAsync(CancellationToken.None);
        var firstBatch = RunningProcesses();
        firstBatch[1].CloseTakes = null; // still running when the stop arrives

        using var cts = new CancellationTokenSource();
        var stopping = CreateWatcher(delay: (wait, token) =>
        {
            _now += wait;
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        WriteIncomingBuild("b2");

        var act = async () => await stopping.RunOnceAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _installer.Installs.Should().Equal("b1");
        File.Exists(ProcessingManifest("b2")).Should().BeTrue("the claim waits for recovery");
        ResultExists("b2").Should().BeFalse();
        firstBatch[1].KillCalls.Should().Be(0);
        Record().Select(c => c.ProcessId).Should().Contain(firstBatch[1].ProcessId);

        firstBatch[1].CloseTakes = TimeSpan.FromSeconds(1);
        var restarted = CreateWatcher();
        await restarted.RecoverProcessingAsync(CancellationToken.None);

        _installer.Installs.Should().Equal("b1", "b2");
        ReadResult("b2").Status.Should().Be("deployed");
        firstBatch.Should().AllSatisfy(p => p.IsRunning.Should().BeFalse());
        RunningProcesses().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_client_that_could_not_be_recorded_blocks_the_next_build_until_it_is_closed_by_hand()
    {
        var store = new FailingSaveStore(new ManagedClientStore(_stateDir)) { FailingSave = 1 }; // [] then client 1
        WriteIncomingBuild("b1");

        await CreateWatcher(store: store).RunOnceAsync(CancellationToken.None);

        var result = ReadResult("b1");
        result.ErrorCode.Should().Be(ErrorCode.UnknownError);
        result.ErrorMessage.Should().Contain("could not be recorded");
        File.Exists(ArchivedManifest("b1")).Should().BeTrue("the build is never deployed again");
        var unrecorded = RunningProcesses().Should().ContainSingle().Subject;

        WriteIncomingBuild("b2");
        await CreateWatcher().RunOnceAsync(CancellationToken.None);

        ReadResult("b2").ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
        unrecorded.CloseCalls.Should().Be(0);

        unrecorded.Exit(); // closed by hand
        WriteIncomingBuild("b3");
        await CreateWatcher().RunOnceAsync(CancellationToken.None);

        ReadResult("b3").Status.Should().Be("deployed");
    }

    [Fact]
    public async Task A_corrupt_record_with_no_VRChat_running_is_rewritten_by_the_next_build()
    {
        Directory.CreateDirectory(_stateDir);
        File.WriteAllText(RecordPath, "{ not json");
        WriteIncomingBuild("b1");

        await CreateWatcher().RunOnceAsync(CancellationToken.None);

        ReadResult("b1").Status.Should().Be("deployed");
        new ManagedClientStore(_stateDir).Load().Status.Should().Be(ManagedClientLoadStatus.Loaded);
        Record().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_record_that_cannot_be_read_fails_the_build_without_deploying()
    {
        Directory.CreateDirectory(RecordPath); // fails the open on every OS, as an access error would
        WriteIncomingBuild("b1");

        await CreateWatcher().RunOnceAsync(CancellationToken.None);

        var result = ReadResult("b1");
        result.ErrorCode.Should().Be(ErrorCode.UnknownError);
        result.ErrorMessage.Should().Contain("could not be read");
        Directory.GetFiles(_worldsDir).Should().BeEmpty();
        _launcher.LaunchCalled.Should().BeFalse();
        File.Exists(FailedManifest("b1")).Should().BeTrue();
    }

    [Fact]
    public async Task A_VRChat_that_appears_between_launches_fails_the_build_but_keeps_it_archived()
    {
        _monitor.OnOpenClients = call =>
        {
            if (call == 3) // after the two checks before the install, i.e. before the second launch
            {
                Running(888, _now);
            }
        };
        WriteIncomingBuild("b1");

        await CreateWatcher().RunOnceAsync(CancellationToken.None);

        var result = ReadResult("b1");
        result.ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
        result.ErrorMessage.Should().Contain("Launched 1 of 2");
        File.Exists(ArchivedManifest("b1")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(_stateDir, "failed")).Should().BeEmpty();
        _launcher.Requests.Should().ContainSingle();
        _monitor.Processes.Single(p => p.ProcessId == 888).CloseCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_cleanup_never_removes_the_world_the_clients_were_launched_with()
    {
        var newerLooking = Path.Combine(_worldsDir, "vrc-remote-other.vrcw");
        File.WriteAllText(newerLooking, "other");
        File.SetLastWriteTimeUtc(newerLooking, DateTime.UtcNow.AddDays(1));
        WriteIncomingBuild("b1");

        await CreateWatcher(profiles: [0], retainBuilds: 1).RunOnceAsync(CancellationToken.None);

        _launcher.Requests.Should().ContainSingle().Which.WorldPath.Should().Be(Path.GetFullPath(DeployedPath("b1")));
        File.Exists(DeployedPath("b1")).Should().BeTrue();
        File.Exists(newerLooking).Should().BeFalse();
    }

    // ---- The Log Viewer during a relaunch (VrchatLogNotice) ----

    private static readonly TimeSpan PublishWait = TimeSpan.FromSeconds(5);

    private string? Published() => PublishedFile.TryRead(Path.Combine(_stagingDir, "logs", "vrchat-latest.log"));

    /// <summary>
    /// The real Log Viewer loop over a fake reader, with polls an hour apart, so that only
    /// the notice's wakes publish during a test. Started on the thread pool: the deploy
    /// runs synchronously on the test thread and waits for those publishes.
    /// </summary>
    private async Task<VrchatLogService> StartLogViewerAsync(
        IVrchatLogReader reader, ILogger<VrchatLogService>? logger, CancellationToken stoppingToken)
    {
        var service = new VrchatLogService(
            Microsoft.Extensions.Options.Options.Create(Options([0, 1], retainBuilds: 10)),
            reader,
            _logNotice,
            logger ?? NullLogger<VrchatLogService>.Instance,
            TimeSpan.FromHours(1));
        await Task.Run(() => service.StartAsync(stoppingToken));
        return service;
    }

    /// <summary>Option C verification item 9: the closed first client's log stayed 3.6 s after the new one started.</summary>
    [Fact]
    public async Task The_share_shows_the_relaunch_notice_from_closing_until_the_first_new_client_is_recorded()
    {
        var watcher = CreateWatcher();
        WriteIncomingBuild("b1");
        await watcher.RunOnceAsync(CancellationToken.None);
        var reader = new FakeVrchatLogReader { ResultToReturn = LogSnapshotResult.Success("b1 first client's log") };
        using var cts = new CancellationTokenSource();
        var service = await StartLogViewerAsync(reader, logger: null, cts.Token);
        SpinWait.SpinUntil(() => Published() == "b1 first client's log", PublishWait).Should().BeTrue();

        var noticeWhileClosing = false;
        RunningProcesses()[0].OnCloseMainWindow = () => noticeWhileClosing =
            SpinWait.SpinUntil(() => Published() == VrchatRelaunchCoordinator.RelaunchNotice, PublishWait);
        var onShareAtLaunch = new List<string?>();
        _launcher.OnLaunch = pid =>
        {
            onShareAtLaunch.Add(Published());
            reader.ResultToReturn = LogSnapshotResult.Success("b2 first client's log");
            Running(pid, _launcher.Launched[^1].CreationTimeUtc);
        };

        WriteIncomingBuild("b2");
        await watcher.RunOnceAsync(CancellationToken.None);
        var newLogShown = SpinWait.SpinUntil(() => Published() == "b2 first client's log", PublishWait);
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        ReadResult("b2").Status.Should().Be("deployed");
        noticeWhileClosing.Should().BeTrue("the notice replaces the closing clients' log at once");
        onShareAtLaunch.First().Should().Be(VrchatRelaunchCoordinator.RelaunchNotice);
        newLogShown.Should().BeTrue("the log is read again as soon as the first client is recorded");
        reader.ReadSnapshotCallCount.Should().Be(2, "once before the relaunch and once after the first launch");
        _logNotice.Message.Should().BeNull();
    }

    /// <summary>
    /// Windows verification of PR #19, item 4: the Bridge was stopped while closing, its
    /// clients exited on the close requests already sent, and the build it recovered after
    /// its restart had nothing to close. The closed first client's log then stayed 5.2 s
    /// after the new first client started.
    /// </summary>
    [Fact]
    public async Task A_recovered_build_whose_recorded_clients_already_exited_shows_the_notice_until_the_first_client_is_recorded()
    {
        var watcher = CreateWatcher();
        WriteIncomingBuild("b1");
        await watcher.RunOnceAsync(CancellationToken.None);
        var firstBatch = RunningProcesses();
        using var stop = new CancellationTokenSource();
        var stopping = CreateWatcher(delay: (wait, token) =>
        {
            _now += wait;
            stop.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        WriteIncomingBuild("b2");
        var act = async () => await stopping.RunOnceAsync(stop.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        _now += TimeSpan.FromSeconds(5);
        RunningProcesses().Should().BeEmpty("the close requests sent before the stop took effect");
        Record().Should().NotBeEmpty();
        var closeRequests = firstBatch.Sum(p => p.CloseCalls);

        var reader = new FakeVrchatLogReader { ResultToReturn = LogSnapshotResult.Success("b1 first client's log") };
        using var cts = new CancellationTokenSource();
        var service = await StartLogViewerAsync(reader, logger: null, cts.Token);
        SpinWait.SpinUntil(() => Published() == "b1 first client's log", PublishWait).Should().BeTrue();
        var noticeAtInstall = false;
        _installer.OnInstall = () => noticeAtInstall =
            SpinWait.SpinUntil(() => Published() == VrchatRelaunchCoordinator.RelaunchNotice, PublishWait);
        var onShareAtLaunch = new List<string?>();
        _launcher.OnLaunch = pid =>
        {
            onShareAtLaunch.Add(Published());
            reader.ResultToReturn = LogSnapshotResult.Success("b2 first client's log");
            Running(pid, _launcher.Launched[^1].CreationTimeUtc);
        };

        await CreateWatcher().RecoverProcessingAsync(CancellationToken.None);
        var newLogShown = SpinWait.SpinUntil(() => Published() == "b2 first client's log", PublishWait);
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        ReadResult("b2").Status.Should().Be("deployed");
        firstBatch.Sum(p => p.CloseCalls).Should().Be(closeRequests, "the recovered build had nothing left to close");
        noticeAtInstall.Should().BeTrue("the newest log is still the closed first client's");
        onShareAtLaunch.First().Should().Be(VrchatRelaunchCoordinator.RelaunchNotice);
        newLogShown.Should().BeTrue();
        _logNotice.Message.Should().BeNull();
    }

    [Fact]
    public async Task A_relaunch_whose_install_fails_leaves_no_notice()
    {
        var watcher = CreateWatcher();
        WriteIncomingBuild("b1");
        await watcher.RunOnceAsync(CancellationToken.None);
        string? atInstall = null;
        _installer.OnInstall = () =>
        {
            atInstall = _logNotice.Message;
            throw new IOException("simulated install failure");
        };

        WriteIncomingBuild("b2");
        await watcher.RunOnceAsync(CancellationToken.None);

        ReadResult("b2").ErrorCode.Should().Be(ErrorCode.DeployFailed);
        RunningProcesses().Should().BeEmpty();
        _launcher.Requests.Should().HaveCount(2);
        atInstall.Should().Be(VrchatRelaunchCoordinator.RelaunchNotice);
        _logNotice.Message.Should().BeNull("nothing is launched, so the newest log is shown as with no VRChat running");
    }

    [Fact]
    public async Task A_Log_Viewer_that_cannot_write_the_share_does_not_stop_the_relaunch()
    {
        // A file where the logs directory should be: every publish throws.
        Directory.CreateDirectory(_stagingDir);
        File.WriteAllText(Path.Combine(_stagingDir, "logs"), "not a directory");
        var logger = new ListLogger<VrchatLogService>();
        int Errors() => logger.Entries.Count(e => e.Level == LogLevel.Error);
        using var cts = new CancellationTokenSource();
        var service = await StartLogViewerAsync(
            new FakeVrchatLogReader { ResultToReturn = LogSnapshotResult.Success("log") }, logger, cts.Token);
        SpinWait.SpinUntil(() => Errors() == 1, PublishWait).Should().BeTrue();
        var watcher = CreateWatcher();
        WriteIncomingBuild("b1");
        await watcher.RunOnceAsync(CancellationToken.None);
        var failedWhileClosing = false;
        RunningProcesses()[0].OnCloseMainWindow = () => failedWhileClosing = SpinWait.SpinUntil(() => Errors() == 2, PublishWait);

        WriteIncomingBuild("b2");
        await watcher.RunOnceAsync(CancellationToken.None);
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        failedWhileClosing.Should().BeTrue("the notice was published, and failed, while the clients closed");
        ReadResult("b2").Status.Should().Be("deployed");
        _launcher.Requests.Should().HaveCount(4);
        RunningProcesses().Should().HaveCount(2);
        Record().Select(c => c.ProcessId).Should().Equal(_launcher.Launched.Skip(2).Select(l => l.ProcessId));
    }

    /// <summary>Records install order; runs <see cref="OnInstall"/> once the file is in place.</summary>
    private sealed class RecordingWorldInstaller : IWorldInstaller
    {
        private readonly WorldInstaller _inner = new();

        public List<string> Installs { get; } = [];

        public Action? OnInstall { get; set; }

        public string Install(string validatedArtifactPath, string buildId, string worldsDirectory)
        {
            OnInstall?.Invoke();
            var name = _inner.Install(validatedArtifactPath, buildId, worldsDirectory);
            Installs.Add(buildId);
            return name;
        }
    }

    /// <summary>The real store, except that one save (zero-based) fails.</summary>
    private sealed class FailingSaveStore(ManagedClientStore inner) : IManagedClientStore
    {
        private int _saves;

        public int FailingSave { get; init; } = -1;

        public string FilePath => inner.FilePath;

        public ManagedClientLoadResult Load() => inner.Load();

        public void Save(IReadOnlyList<ManagedClient> clients)
        {
            if (_saves++ == FailingSave)
            {
                throw new IOException("simulated write failure");
            }

            inner.Save(clients);
        }

        public IReadOnlyList<ManagedClient> Known => inner.Known;
    }
}
