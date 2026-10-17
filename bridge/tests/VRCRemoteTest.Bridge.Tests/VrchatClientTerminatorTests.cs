using System.ComponentModel;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// The closing rules of multi-client plan sections 13.3 and 13.5 on a fake clock that
/// only the terminator's own waits advance: one 10 s grace period and one 20 s deadline
/// for all clients, resending the close request while there is no window, and never
/// reading "could not check" as "exited".
/// </summary>
public class VrchatClientTerminatorTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);

    private readonly CapturingLogger<VrchatClientTerminator> _logger = new();
    private readonly List<TimeSpan> _waits = [];
    private DateTime _now = T0;

    private VrchatClientTerminator CreateTerminator(Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(_logger, () => _now, delay ?? ((wait, _) =>
        {
            _waits.Add(wait);
            _now += wait;
            return Task.CompletedTask;
        }));

    private FakeVrchatProcess Process(int processId)
    {
        var process = new FakeVrchatProcess(processId, T0.AddMinutes(-1)) { Clock = () => _now };
        process.Open(enumeration: 1);
        return process;
    }

    private static IVrchatProcessHandle HandleOf(FakeVrchatProcess process) => process.Handles[0];

    private double Elapsed => (_now - T0).TotalSeconds;

    [Fact]
    public async Task Clients_with_a_window_close_without_being_killed()
    {
        var first = Process(1);
        var second = Process(2);
        second.CloseTakes = TimeSpan.FromSeconds(3.8);

        var result = await CreateTerminator().CloseAsync([HandleOf(first), HandleOf(second)], CancellationToken.None);

        result.Exited.Should().HaveCount(2);
        result.NotConfirmed.Should().BeEmpty();
        result.Canceled.Should().BeFalse();
        first.KillCalls.Should().Be(0);
        second.KillCalls.Should().Be(0);
        first.CloseCalls.Should().Be(1, "a request that was sent is not repeated");
        Elapsed.Should().BeApproximately(4.0, 0.01, "exits are polled every 0.5 s");
    }

    [Fact]
    public async Task A_client_without_a_window_yet_gets_the_request_again_once_its_window_appears()
    {
        var process = Process(1);
        process.HasWindow = false;
        process.RefreshesUntilWindow = 3;
        process.CloseTakes = TimeSpan.FromSeconds(2);

        var result = await CreateTerminator().CloseAsync([HandleOf(process)], CancellationToken.None);

        result.NotConfirmed.Should().BeEmpty();
        process.KillCalls.Should().Be(0);
        process.CloseCalls.Should().Be(4, "the first request found no window, then it is resent after each refresh");
        process.RefreshCalls.Should().Be(3);
        process.CloseCallTimes.Select(t => (t - T0).TotalSeconds).Should().Equal(0, 0, 0.5, 1.0);
    }

    [Fact]
    public async Task A_client_whose_window_never_appears_is_killed_at_the_grace_period()
    {
        var process = Process(1);
        process.HasWindow = false;

        var result = await CreateTerminator().CloseAsync([HandleOf(process)], CancellationToken.None);

        result.Exited.Should().ContainSingle();
        process.KillCalls.Should().Be(1);
        (process.KillCallTimes.Single() - T0).Should().Be(VrchatClientTerminator.CloseGracePeriod);
        process.CloseCalls.Should().Be(1 + 20, "resent every 0.5 s during the 10 s grace period");
        _logger.MessagesAt(LogLevel.Warning).Should().Contain(m => m.Contains("forcing it to exit"));
    }

    [Fact]
    public async Task One_grace_period_and_one_deadline_cover_every_client()
    {
        var processes = Enumerable.Range(1, 4).Select(Process).ToList();
        foreach (var process in processes)
        {
            process.CloseTakes = null; // ignores the request
            process.IgnoresKill = true;
        }

        var result = await CreateTerminator().CloseAsync(processes.Select(HandleOf).ToList(), CancellationToken.None);

        result.NotConfirmed.Should().HaveCount(4);
        processes.Should().AllSatisfy(p =>
        {
            p.KillCalls.Should().Be(1);
            (p.KillCallTimes.Single() - T0).Should().Be(TimeSpan.FromSeconds(10));
        });
        (_now - T0).Should().Be(VrchatClientTerminator.ExitDeadline, "20 s in total, not 20 s per client");
        _logger.MessagesAt(LogLevel.Error).Should().HaveCount(4);
    }

    [Fact]
    public async Task Only_clients_still_running_after_the_grace_period_are_killed()
    {
        var quick = Process(1);
        var stuck = Process(2);
        stuck.CloseTakes = null;

        var result = await CreateTerminator().CloseAsync([HandleOf(quick), HandleOf(stuck)], CancellationToken.None);

        result.Exited.Should().HaveCount(2);
        quick.KillCalls.Should().Be(0);
        stuck.KillCalls.Should().Be(1);
        Elapsed.Should().Be(10.5, "the killed client's exit is seen on the next poll");
    }

    [Fact]
    public async Task Calling_kill_is_not_proof_of_an_exit()
    {
        var process = Process(1);
        process.CloseTakes = null;
        process.IgnoresKill = true;

        var result = await CreateTerminator().CloseAsync([HandleOf(process)], CancellationToken.None);

        result.Exited.Should().BeEmpty();
        result.NotConfirmed.Should().ContainSingle();
    }

    [Fact]
    public async Task A_close_request_that_throws_because_the_client_already_exited_counts_as_exited()
    {
        var process = Process(1);
        process.OnCloseMainWindow = process.Exit; // exits just as the request is made
        process.CloseThrows = new InvalidOperationException("No process is associated with this object.");

        var result = await CreateTerminator().CloseAsync([HandleOf(process)], CancellationToken.None);

        result.Exited.Should().ContainSingle();
        process.KillCalls.Should().Be(0);
        Elapsed.Should().Be(0);
    }

    [Fact]
    public async Task A_kill_that_throws_for_a_client_that_did_exit_counts_as_exited()
    {
        var process = Process(1);
        process.CloseTakes = null;
        process.KillThrows = new Win32Exception(5, "Access is denied.");
        var terminator = CreateTerminator((wait, _) =>
        {
            _now += wait;
            if (process.KillCalls > 0)
            {
                process.Exit(); // it was exiting on its own
            }

            return Task.CompletedTask;
        });

        var result = await terminator.CloseAsync([HandleOf(process)], CancellationToken.None);

        result.Exited.Should().ContainSingle();
    }

    [Fact]
    public async Task A_kill_that_throws_for_a_running_client_leaves_its_exit_unconfirmed()
    {
        var process = Process(1);
        process.CloseTakes = null;
        process.KillThrows = new Win32Exception(5, "Access is denied.");

        var result = await CreateTerminator().CloseAsync([HandleOf(process)], CancellationToken.None);

        result.NotConfirmed.Should().ContainSingle();
        process.KillCalls.Should().Be(1, "a kill is attempted once");
        _logger.MessagesAt(LogLevel.Error).Should().ContainSingle().Which.Should().Contain("Access is denied");
    }

    [Fact]
    public async Task A_handle_that_cannot_tell_whether_the_client_exited_never_counts_as_exited()
    {
        var process = Process(1);
        process.HasExitedThrows = new InvalidOperationException("wait failed");
        process.CloseTakes = TimeSpan.Zero; // it is in fact gone

        var result = await CreateTerminator().CloseAsync([HandleOf(process)], CancellationToken.None);

        result.Exited.Should().BeEmpty();
        result.NotConfirmed.Should().ContainSingle();
    }

    [Fact]
    public async Task A_stop_request_ends_the_wait_without_killing_and_reports_what_did_exit()
    {
        using var cts = new CancellationTokenSource();
        var closes = Process(1);
        closes.CloseTakes = TimeSpan.FromSeconds(1);
        var stuck = Process(2);
        stuck.CloseTakes = null;
        var terminator = CreateTerminator((wait, token) =>
        {
            _now += wait;
            if (_now - T0 >= TimeSpan.FromSeconds(5))
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        });

        var result = await terminator.CloseAsync([HandleOf(closes), HandleOf(stuck)], cts.Token);

        result.Canceled.Should().BeTrue();
        result.Exited.Select(h => h.ProcessId).Should().Equal(1);
        result.NotConfirmed.Select(h => h.ProcessId).Should().Equal(2);
        stuck.KillCalls.Should().Be(0);
        Elapsed.Should().BeLessThan(10);
    }

    [Fact]
    public async Task A_stop_request_after_the_kills_still_waits_no_longer()
    {
        using var cts = new CancellationTokenSource();
        var stuck = Process(1);
        stuck.CloseTakes = null;
        stuck.IgnoresKill = true;
        var terminator = CreateTerminator((wait, token) =>
        {
            _now += wait;
            if (stuck.KillCalls > 0)
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        });

        var result = await terminator.CloseAsync([HandleOf(stuck)], cts.Token);

        result.Canceled.Should().BeTrue();
        result.NotConfirmed.Should().ContainSingle();
        Elapsed.Should().BeLessThan(20);
    }
}
