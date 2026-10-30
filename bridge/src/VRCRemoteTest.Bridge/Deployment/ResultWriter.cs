using System.Text.Json;
using VRCRemoteTest.Bridge.Protocol;
using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Deployment;

public sealed class ResultWriter : IResultWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public void WriteSuccess(string buildId, string deployedFileName, string sha256, string resultsDirectory) =>
        WriteSuccess(buildId, deployedFileName, sha256, resultsDirectory, new BuildResultExtras(null));

    public void WriteFailure(string buildId, string errorCode, string errorMessage, string resultsDirectory) =>
        WriteFailure(buildId, errorCode, errorMessage, resultsDirectory, new BuildResultExtras(null));

    public void WriteSuccess(
        string buildId, string deployedFileName, string sha256, string resultsDirectory, BuildResultExtras extras)
    {
        Write(
            buildId,
            new BuildResult
            {
                ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                BuildId = buildId,
                Status = "deployed",
                DeployedFileName = deployedFileName,
                Sha256 = sha256,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Launch = extras.Launch,
                Warnings = Masked(extras.Warnings),
            },
            resultsDirectory);
    }

    /// <summary>
    /// The message is masked before it reaches the share, like everything else the Bridge
    /// publishes (client-observation plan C5): it can name paths under the Windows user's
    /// profile.
    /// </summary>
    public void WriteFailure(
        string buildId, string errorCode, string errorMessage, string resultsDirectory, BuildResultExtras extras)
    {
        Write(
            buildId,
            new BuildResult
            {
                ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                BuildId = buildId,
                Status = "failed",
                ErrorCode = errorCode,
                ErrorMessage = LogRedactor.Redact(errorMessage),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Launch = extras.Launch,
                Warnings = Masked(extras.Warnings),
            },
            resultsDirectory);
    }

    internal static string ResultPath(string resultsDirectory, string buildId) =>
        Path.Combine(resultsDirectory, $"{buildId}.json");

    private static IReadOnlyList<string>? Masked(IReadOnlyList<string>? warnings) =>
        warnings is { Count: > 0 } ? warnings.Select(LogRedactor.Redact).ToList() : null;

    private static void Write(string buildId, BuildResult result, string resultsDirectory)
    {
        Directory.CreateDirectory(resultsDirectory);

        var finalPath = ResultPath(resultsDirectory, buildId);
        var tempPath = Path.Combine(resultsDirectory, $"{buildId}.json.tmp");

        var json = JsonSerializer.Serialize(result, SerializerOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, finalPath, overwrite: true);
    }
}
