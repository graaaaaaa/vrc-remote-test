using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using VRCRemoteTest.Bridge.Lan;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>The Bridge deletes its own day-old log exports and nothing else.</summary>
public sealed class ExportCleanupServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "export-cleanup-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    public ExportCleanupServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string File(string name, TimeSpan age)
    {
        var path = Path.Combine(_directory, name);
        System.IO.File.WriteAllText(path, "x");
        System.IO.File.SetLastWriteTimeUtc(path, _now - age);
        return path;
    }

    [Fact]
    public void Only_day_old_exports_named_like_the_Bridges_are_deleted()
    {
        var oldLog = File(new string('a', 32) + ".log", TimeSpan.FromHours(25));
        var oldPart = File(new string('b', 32) + ".part", TimeSpan.FromHours(30));
        var newLog = File(new string('c', 32) + ".log", TimeSpan.FromHours(23));
        var otherName = File("notes.log", TimeSpan.FromDays(10));
        var upperHex = File(new string('D', 32) + ".log", TimeSpan.FromDays(10));
        var otherExtension = File(new string('e', 32) + ".txt", TimeSpan.FromDays(10));

        var deleted = ExportCleanupService.CleanOnce(_directory, _now, NullLogger.Instance);

        deleted.Should().Be(2);
        System.IO.File.Exists(oldLog).Should().BeFalse();
        System.IO.File.Exists(oldPart).Should().BeFalse();
        System.IO.File.Exists(newLog).Should().BeTrue();
        System.IO.File.Exists(otherName).Should().BeTrue();
        System.IO.File.Exists(upperHex).Should().BeTrue();
        System.IO.File.Exists(otherExtension).Should().BeTrue();
    }

    [Fact]
    public void A_link_named_like_an_export_is_not_followed_or_deleted()
    {
        var outside = Path.Combine(_directory, "outside");
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "keep.log");
        System.IO.File.WriteAllText(target, "keep");
        var link = Path.Combine(_directory, new string('f', 32) + ".log");
        System.IO.File.CreateSymbolicLink(link, target);
        System.IO.File.SetLastWriteTimeUtc(target, _now - TimeSpan.FromDays(3));

        ExportCleanupService.CleanOnce(_directory, _now, NullLogger.Instance);

        System.IO.File.Exists(target).Should().BeTrue();
        new FileInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint).Should().BeTrue("the link itself is left too");
    }

    [Fact]
    public void A_missing_folder_is_nothing_to_do()
    {
        ExportCleanupService.CleanOnce(Path.Combine(_directory, "none"), _now, NullLogger.Instance).Should().Be(0);
    }
}
