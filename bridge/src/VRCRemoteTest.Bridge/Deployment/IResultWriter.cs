using VRCRemoteTest.Bridge.Protocol;

namespace VRCRemoteTest.Bridge.Deployment;

/// <summary>What a result carries besides its status (v0.8.0).</summary>
public sealed record BuildResultExtras(LaunchReport? Launch, IReadOnlyList<string>? Warnings = null);

/// <summary>
/// Writes exactly one result file per build (results/{buildId}.json). Replaces the
/// original heartbeat + rich status-polling design (deferred as over-engineered for v1;
/// see the Codex plan review report).
/// </summary>
public interface IResultWriter
{
    void WriteSuccess(string buildId, string deployedFileName, string sha256, string resultsDirectory);

    void WriteFailure(string buildId, string errorCode, string errorMessage, string resultsDirectory);

    void WriteSuccess(
        string buildId, string deployedFileName, string sha256, string resultsDirectory, BuildResultExtras extras) =>
        WriteSuccess(buildId, deployedFileName, sha256, resultsDirectory);

    void WriteFailure(
        string buildId, string errorCode, string errorMessage, string resultsDirectory, BuildResultExtras extras) =>
        WriteFailure(buildId, errorCode, errorMessage, resultsDirectory);
}
