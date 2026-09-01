using System.Text.Json;
using FluentAssertions;
using VRCRemoteTest.Bridge.Deployment;
using VRCRemoteTest.Bridge.Protocol;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

public class ResultWriterTests : IDisposable
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _tempDir;
    private readonly ResultWriter _writer = new();

    public ResultWriterTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-results-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void WriteSuccess_creates_a_single_result_file_with_deployed_status()
    {
        _writer.WriteSuccess("build-1", "vrc-remote-build-1.vrcw", new string('a', 64), _tempDir);

        var path = Path.Combine(_tempDir, "build-1.json");
        File.Exists(path).Should().BeTrue();

        var result = JsonSerializer.Deserialize<BuildResult>(File.ReadAllText(path), DeserializeOptions);

        result!.Status.Should().Be("deployed");
        result.DeployedFileName.Should().Be("vrc-remote-build-1.vrcw");
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void WriteFailure_creates_a_result_file_with_failed_status_and_error_code()
    {
        _writer.WriteFailure("build-2", ErrorCode.HashMismatch, "sha256 did not match", _tempDir);

        var path = Path.Combine(_tempDir, "build-2.json");
        var result = JsonSerializer.Deserialize<BuildResult>(File.ReadAllText(path), DeserializeOptions);

        result!.Status.Should().Be("failed");
        result.ErrorCode.Should().Be(ErrorCode.HashMismatch);
        result.DeployedFileName.Should().BeNull();
    }

    [Fact]
    public void Does_not_leave_tmp_file_behind()
    {
        _writer.WriteSuccess("build-3", "vrc-remote-build-3.vrcw", new string('a', 64), _tempDir);

        Directory.GetFiles(_tempDir).Should().ContainSingle()
            .Which.Should().Be(Path.Combine(_tempDir, "build-3.json"));
    }

    [Fact]
    public void Overwrites_a_previous_result_for_the_same_build_id()
    {
        _writer.WriteSuccess("build-4", "vrc-remote-build-4.vrcw", new string('a', 64), _tempDir);
        _writer.WriteFailure("build-4", ErrorCode.DeployFailed, "retry", _tempDir);

        var result = JsonSerializer.Deserialize<BuildResult>(
            File.ReadAllText(Path.Combine(_tempDir, "build-4.json")), DeserializeOptions);

        result!.Status.Should().Be("failed");
    }
}
