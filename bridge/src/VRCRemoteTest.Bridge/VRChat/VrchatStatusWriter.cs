using System.Text.Json;
using Microsoft.Extensions.Logging;
using VRCRemoteTest.Bridge.Protocol;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Writes status/vrchat-status.json using the same atomic tmp-then-rename pattern as
/// ResultWriter, so a concurrent reader (Unity's PollVrchatStatus) never observes a
/// partially-written file -- including while an API reader has the file open
/// (see PrivateSnapshotWriter).
/// </summary>
public sealed class VrchatStatusWriter : IVrchatStatusWriter
{
    private const string FileName = "vrchat-status.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly PrivateSnapshotWriter _fileWriter;

    public VrchatStatusWriter(ILogger<VrchatStatusWriter> logger)
    {
        _fileWriter = new PrivateSnapshotWriter(logger);
    }

    public void Write(VrchatStatus status, string statusDirectory)
    {
        Directory.CreateDirectory(statusDirectory);

        var json = JsonSerializer.Serialize(status, SerializerOptions);
        _fileWriter.Write(Path.Combine(statusDirectory, FileName), json);
    }
}
