namespace VRCRemoteTest.Bridge.Deployment;

public interface IBridgeWatcher
{
    /// <summary>
    /// Performs a single scan-and-process pass over the incoming/ directory.
    /// Exposed as a distinct method (rather than only running inside the
    /// BackgroundService loop) so tests can drive processing deterministically.
    /// </summary>
    Task RunOnceAsync(CancellationToken cancellationToken);
}
