using System.Security.Cryptography;
using FluentAssertions;
using VRCRemoteTest.Bridge.Deployment;
using VRCRemoteTest.Bridge.Protocol;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

public class PackageValidatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PackageValidator _validator = new();

    public PackageValidatorTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private (string Path, byte[] Bytes) WriteArtifact(string fileName, int sizeBytes = 128)
    {
        var bytes = new byte[sizeBytes];
        new Random(42).NextBytes(bytes);
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, bytes);
        return (path, bytes);
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static BuildManifest ManifestFor(string buildId, string fileName, long size, string sha256) => new()
    {
        ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
        BuildId = buildId,
        FileName = fileName,
        Size = size,
        Sha256 = sha256,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Accepts_valid_manifest_and_matching_artifact()
    {
        var (path, bytes) = WriteArtifact("20260901T112522481Z-a91f02cc.vrcw");
        var manifest = ManifestFor("20260901T112522481Z-a91f02cc", "20260901T112522481Z-a91f02cc.vrcw", bytes.Length, Sha256Hex(bytes));

        var result = _validator.Validate(manifest, path, maxArtifactSizeBytes: 1024);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("../../evil.vrcw")]
    [InlineData("..\\..\\evil.vrcw")]
    [InlineData("/etc/passwd.vrcw")]
    [InlineData("C:\\Windows\\evil.vrcw")]
    [InlineData("sub/dir.vrcw")]
    [InlineData("sub\\dir.vrcw")]
    [InlineData("..vrcw")]
    [InlineData("no-extension")]
    [InlineData("wrong.extension.txt")]
    [InlineData("")]
    public void Rejects_path_traversal_and_malformed_file_names(string maliciousFileName)
    {
        var manifest = ManifestFor("build-1", maliciousFileName, 128, new string('a', 64));

        var result = _validator.Validate(manifest, Path.Combine(_tempDir, "irrelevant.vrcw"), maxArtifactSizeBytes: 1024);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCode.ManifestInvalid);
    }

    [Theory]
    [InlineData("CON.vrcw")]
    [InlineData("con.vrcw")]
    [InlineData("PRN.vrcw")]
    [InlineData("COM1.vrcw")]
    [InlineData("LPT1.vrcw")]
    public void Rejects_windows_reserved_device_names(string reservedFileName)
    {
        var manifest = ManifestFor("build-1", reservedFileName, 128, new string('a', 64));

        var result = _validator.Validate(manifest, Path.Combine(_tempDir, "irrelevant.vrcw"), maxArtifactSizeBytes: 1024);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCode.ManifestInvalid);
    }

    [Fact]
    public void Rejects_protocol_version_mismatch()
    {
        var manifest = new BuildManifest
        {
            ProtocolVersion = 999,
            BuildId = "build-1",
            FileName = "build.vrcw",
            Size = 128,
            Sha256 = new string('a', 64),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var result = _validator.Validate(manifest, Path.Combine(_tempDir, "irrelevant.vrcw"), maxArtifactSizeBytes: 1024);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCode.ProtocolVersionMismatch);
    }

    [Fact]
    public void Rejects_missing_build_id()
    {
        var manifest = ManifestFor(string.Empty, "build.vrcw", 128, new string('a', 64));

        var result = _validator.Validate(manifest, Path.Combine(_tempDir, "irrelevant.vrcw"), maxArtifactSizeBytes: 1024);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCode.ManifestInvalid);
    }

    [Fact]
    public void Rejects_missing_artifact()
    {
        var manifest = ManifestFor("build-1", "missing.vrcw", 128, new string('a', 64));

        var result = _validator.Validate(manifest, Path.Combine(_tempDir, "missing.vrcw"), maxArtifactSizeBytes: 1024);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCode.ArtifactNotFound);
    }

    [Fact]
    public void Rejects_size_mismatch()
    {
        var (path, bytes) = WriteArtifact("build.vrcw", sizeBytes: 128);
        var manifest = ManifestFor("build-1", "build.vrcw", 999, Sha256Hex(bytes));

        var result = _validator.Validate(manifest, path, maxArtifactSizeBytes: 1024);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCode.SizeMismatch);
    }

    [Fact]
    public void Rejects_hash_mismatch()
    {
        var (path, bytes) = WriteArtifact("build.vrcw", sizeBytes: 128);
        var manifest = ManifestFor("build-1", "build.vrcw", bytes.Length, new string('0', 64));

        var result = _validator.Validate(manifest, path, maxArtifactSizeBytes: 1024);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCode.HashMismatch);
    }

    [Fact]
    public void Rejects_non_hex_hash()
    {
        var (path, bytes) = WriteArtifact("build.vrcw", sizeBytes: 128);
        var manifest = ManifestFor("build-1", "build.vrcw", bytes.Length, new string('z', 64));

        var result = _validator.Validate(manifest, path, maxArtifactSizeBytes: 1024);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCode.ManifestInvalid);
    }

    [Fact]
    public void Rejects_oversized_artifact()
    {
        var (path, bytes) = WriteArtifact("build.vrcw", sizeBytes: 2048);
        var manifest = ManifestFor("build-1", "build.vrcw", bytes.Length, Sha256Hex(bytes));

        var result = _validator.Validate(manifest, path, maxArtifactSizeBytes: 1024);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCode.SizeExceeded);
    }

    [Fact]
    public void Rejects_zero_or_negative_size()
    {
        var manifest = ManifestFor("build-1", "build.vrcw", 0, new string('a', 64));

        var result = _validator.Validate(manifest, Path.Combine(_tempDir, "build.vrcw"), maxArtifactSizeBytes: 1024);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCode.ManifestInvalid);
    }
}
