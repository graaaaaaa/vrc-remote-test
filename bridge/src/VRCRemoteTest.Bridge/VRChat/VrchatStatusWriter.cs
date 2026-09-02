using System.Text.Json;
using VRCRemoteTest.Bridge.Protocol;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Writes status/vrchat-status.json using the same atomic tmp-then-rename pattern as
/// ResultWriter, so a concurrent reader (Unity's PollVrchatStatus) never observes a
/// partially-written file.
/// </summary>
public sealed class VrchatStatusWriter : IVrchatStatusWriter
{
    private const string FileName = "vrchat-status.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public void Write(VrchatStatus status, string statusDirectory)
    {
        Directory.CreateDirectory(statusDirectory);

        var finalPath = Path.Combine(statusDirectory, FileName);
        var tempPath = Path.Combine(statusDirectory, $"{FileName}.tmp");

        var json = JsonSerializer.Serialize(status, SerializerOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, finalPath, overwrite: true);
    }
}
