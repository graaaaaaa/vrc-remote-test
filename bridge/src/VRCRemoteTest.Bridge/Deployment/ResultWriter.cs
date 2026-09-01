using System.Text.Json;
using VRCRemoteTest.Bridge.Protocol;

namespace VRCRemoteTest.Bridge.Deployment;

public sealed class ResultWriter : IResultWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public void WriteSuccess(string buildId, string deployedFileName, string sha256, string resultsDirectory)
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
            },
            resultsDirectory);
    }

    public void WriteFailure(string buildId, string errorCode, string errorMessage, string resultsDirectory)
    {
        Write(
            buildId,
            new BuildResult
            {
                ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                BuildId = buildId,
                Status = "failed",
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            },
            resultsDirectory);
    }

    private static void Write(string buildId, BuildResult result, string resultsDirectory)
    {
        Directory.CreateDirectory(resultsDirectory);

        var finalPath = Path.Combine(resultsDirectory, $"{buildId}.json");
        var tempPath = Path.Combine(resultsDirectory, $"{buildId}.json.tmp");

        var json = JsonSerializer.Serialize(result, SerializerOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, finalPath, overwrite: true);
    }
}
