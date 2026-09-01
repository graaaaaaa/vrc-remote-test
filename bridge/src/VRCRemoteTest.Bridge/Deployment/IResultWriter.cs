namespace VRCRemoteTest.Bridge.Deployment;

/// <summary>
/// Writes exactly one result file per build (results/{buildId}.json). Replaces the
/// original heartbeat + rich status-polling design (deferred as over-engineered for v1;
/// see the Codex plan review report).
/// </summary>
public interface IResultWriter
{
    void WriteSuccess(string buildId, string deployedFileName, string sha256, string resultsDirectory);

    void WriteFailure(string buildId, string errorCode, string errorMessage, string resultsDirectory);
}
