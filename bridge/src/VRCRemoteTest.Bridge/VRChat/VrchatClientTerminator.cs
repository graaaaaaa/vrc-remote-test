using Microsoft.Extensions.Logging;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Closes the VRChat clients the Bridge launched (Relaunch mode, multi-client plan
/// sections 13.3 and 13.5), with one deadline for all of them rather than one per client
/// (four clients would otherwise take up to 80 s):
///
/// 1. Every client is asked to close its main window. True only means the request was
///    sent; false means there is no usable window yet (a client started under a second
///    ago, spike gate 3), so until <see cref="CloseGracePeriod"/> the window is looked up
///    again every <see cref="PollInterval"/> and the request resent once one exists.
/// 2. Clients still running after <see cref="CloseGracePeriod"/> are killed (the process
///    only, never its tree; UnityCrashHandler64 exits with VRChat). Repeated kills showed
///    no effect on login, window placement or settings (spike gate 3).
/// 3. Exits are confirmed through the same handles until <see cref="ExitDeadline"/>,
///    counted from the start. Calling Kill is not proof of an exit.
///
/// When closing or killing throws, the same handle says whether the client already
/// exited (a race with its own exit); when the handle cannot tell, the exit is not
/// confirmed. Nothing here ever turns "could not check" into "exited". A stop request
/// ends the wait without killing anything more.
/// </summary>
internal sealed class VrchatClientTerminator
{
    internal static readonly TimeSpan CloseGracePeriod = TimeSpan.FromSeconds(10);

    internal static readonly TimeSpan ExitDeadline = TimeSpan.FromSeconds(20);

    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILogger _logger;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public VrchatClientTerminator(
        ILogger logger, Func<DateTime> utcNow, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _logger = logger;
        _utcNow = utcNow;
        _delay = delay;
    }

    /// <summary>Never throws, not even when <paramref name="cancellationToken"/> fires; see <see cref="TerminationResult.Canceled"/>.</summary>
    public async Task<TerminationResult> CloseAsync(
        IReadOnlyList<IVrchatProcessHandle> clients, CancellationToken cancellationToken)
    {
        var started = _utcNow();
        var states = clients.Select(c => new ClientState(c)).ToList();

        _logger.LogInformation(
            "Closing {Count} VRChat client(s) launched by the Bridge (PID {ProcessIds}).",
            states.Count, string.Join(", ", clients.Select(c => c.ProcessId)));

        foreach (var state in states)
        {
            RequestClose(state);
        }

        var canceled = false;
        while (true)
        {
            foreach (var state in states)
            {
                UpdateExited(state);
            }

            if (states.All(s => s.Exited))
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            var elapsed = _utcNow() - started;
            if (elapsed >= ExitDeadline)
            {
                break;
            }

            var running = states.Where(s => !s.Exited).ToList();
            if (elapsed >= CloseGracePeriod)
            {
                foreach (var state in running.Where(s => !s.KillRequested))
                {
                    Kill(state);
                }
            }
            else
            {
                foreach (var state in running.Where(s => !s.CloseRequested))
                {
                    TryRefresh(state);
                    RequestClose(state);
                }
            }

            // Wakes exactly at the grace period, so the kills are never late.
            var untilNextDeadline = (elapsed < CloseGracePeriod ? CloseGracePeriod : ExitDeadline) - elapsed;
            try
            {
                await _delay(untilNextDeadline < PollInterval ? untilNextDeadline : PollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                foreach (var state in states)
                {
                    UpdateExited(state);
                }

                canceled = true;
                break;
            }
        }

        var exited = states.Where(s => s.Exited).Select(s => s.Client).ToList();
        var notConfirmed = states.Where(s => !s.Exited).Select(s => s.Client).ToList();
        var killed = states.Count(s => s.KillRequested);
        var seconds = (_utcNow() - started).TotalSeconds;

        if (notConfirmed.Count == 0)
        {
            _logger.LogInformation(
                "Closed {Count} VRChat client(s) in {Seconds:F1}s ({Killed} forced).", exited.Count, seconds, killed);
        }
        else if (canceled)
        {
            _logger.LogWarning(
                "Stopped closing VRChat clients after {Seconds:F1}s because the Bridge is stopping; " +
                "{Exited} closed, {Remaining} still running (PID {ProcessIds}).",
                seconds, exited.Count, notConfirmed.Count, string.Join(", ", notConfirmed.Select(c => c.ProcessId)));
        }
        else
        {
            foreach (var state in states.Where(s => !s.Exited))
            {
                _logger.LogError(
                    "Could not confirm that VRChat client {ProcessId} exited within {Deadline}s{Detail}.",
                    state.Client.ProcessId,
                    ExitDeadline.TotalSeconds,
                    state.LastError is null ? string.Empty : $" ({state.LastError})");
            }
        }

        return new TerminationResult(exited, notConfirmed, canceled);
    }

    private void RequestClose(ClientState state)
    {
        try
        {
            state.CloseRequested = state.Client.CloseMainWindow();
        }
        catch (Exception ex)
        {
            state.LastError = $"closing its window failed: {ex.Message}";
            _logger.LogWarning(
                "Asking VRChat client {ProcessId} to close failed: {Message}", state.Client.ProcessId, ex.Message);
            UpdateExited(state);
        }
    }

    private void TryRefresh(ClientState state)
    {
        try
        {
            state.Client.Refresh();
        }
        catch (Exception ex)
        {
            state.LastError = $"looking up its window failed: {ex.Message}";
        }
    }

    private void Kill(ClientState state)
    {
        state.KillRequested = true;
        _logger.LogWarning(
            "VRChat client {ProcessId} did not close within {Grace}s; forcing it to exit.",
            state.Client.ProcessId, CloseGracePeriod.TotalSeconds);

        try
        {
            state.Client.Kill();
        }
        catch (Exception ex)
        {
            state.LastError = $"forcing it to exit failed: {ex.Message}";
            _logger.LogWarning(
                "Forcing VRChat client {ProcessId} to exit failed: {Message}", state.Client.ProcessId, ex.Message);
            UpdateExited(state);
        }
    }

    private static void UpdateExited(ClientState state)
    {
        if (state.Exited)
        {
            return;
        }

        try
        {
            state.Exited = state.Client.HasExited;
        }
        catch (Exception ex)
        {
            state.LastError = $"its exit could not be checked: {ex.Message}";
        }
    }

    private sealed class ClientState(IVrchatProcessHandle client)
    {
        public IVrchatProcessHandle Client { get; } = client;

        public bool CloseRequested { get; set; }

        public bool KillRequested { get; set; }

        public bool Exited { get; set; }

        public string? LastError { get; set; }
    }
}

/// <param name="Exited">Clients whose exit was confirmed through their handle.</param>
/// <param name="NotConfirmed">Clients that may still be running.</param>
/// <param name="Canceled">The Bridge is stopping; the wait ended early and nothing more was killed.</param>
internal sealed record TerminationResult(
    IReadOnlyList<IVrchatProcessHandle> Exited, IReadOnlyList<IVrchatProcessHandle> NotConfirmed, bool Canceled);
