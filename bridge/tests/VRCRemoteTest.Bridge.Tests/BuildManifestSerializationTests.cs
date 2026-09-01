using System.Text.Json;
using FluentAssertions;
using VRCRemoteTest.Bridge.Protocol;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Verifies the Bridge-side (System.Text.Json) model against the golden fixture that
/// the Unity-side (Newtonsoft.Json) model must also pass, guarding against wire-format
/// drift between the two serializers (see codex plan review, protocol drift finding).
/// </summary>
public class BuildManifestSerializationTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine("fixtures", name));

    [Fact]
    public void Deserializes_golden_fixture_manifest()
    {
        var manifest = JsonSerializer.Deserialize<BuildManifest>(ReadFixture("sample-manifest.json"), Options);

        manifest.Should().NotBeNull();
        manifest!.ProtocolVersion.Should().Be(1);
        manifest.BuildId.Should().Be("20260901T112522481Z-a91f02cc");
        manifest.FileName.Should().Be("20260901T112522481Z-a91f02cc.vrcw");
        manifest.Size.Should().Be(48233421);
        manifest.Sha256.Should().Be("3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1");
        manifest.CreatedAtUtc.Should().Be(DateTimeOffset.Parse("2026-09-01T11:25:22.481Z"));
    }

    [Fact]
    public void Roundtrips_manifest_through_serialize_and_deserialize()
    {
        var manifest = JsonSerializer.Deserialize<BuildManifest>(ReadFixture("sample-manifest.json"), Options)!;

        var reserialized = JsonSerializer.Serialize(manifest, Options);
        var roundTripped = JsonSerializer.Deserialize<BuildManifest>(reserialized, Options);

        roundTripped.Should().BeEquivalentTo(manifest);
    }

    [Fact]
    public void Deserializes_golden_fixture_result()
    {
        var result = JsonSerializer.Deserialize<BuildResult>(ReadFixture("sample-result.json"), Options);

        result.Should().NotBeNull();
        result!.ProtocolVersion.Should().Be(1);
        result.BuildId.Should().Be("20260901T112522481Z-a91f02cc");
        result.Status.Should().Be("deployed");
        result.DeployedFileName.Should().Be("vrc-remote-20260901T112522481Z-a91f02cc.vrcw");
        result.ErrorCode.Should().BeNull();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Roundtrips_result_through_serialize_and_deserialize()
    {
        var result = JsonSerializer.Deserialize<BuildResult>(ReadFixture("sample-result.json"), Options)!;

        var reserialized = JsonSerializer.Serialize(result, Options);
        var roundTripped = JsonSerializer.Deserialize<BuildResult>(reserialized, Options);

        roundTripped.Should().BeEquivalentTo(result);
    }
}
