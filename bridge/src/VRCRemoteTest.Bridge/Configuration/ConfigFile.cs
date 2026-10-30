using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VRCRemoteTest.Bridge.Configuration;

public sealed class ConfigFileException(string message) : Exception(message);

/// <summary>config.json is no longer the file a change was based on (edited on Windows meanwhile).</summary>
public sealed class ConfigChangedException() : IOException("config.json changed on Windows meanwhile; it was left as it is.");

/// <summary>
/// Reading and writing config.json for a change made through the LAN API. The file is read
/// as leniently as the configuration provider reads it (comments, trailing commas, a UTF-8
/// BOM) and written back as plain indented JSON: comments are not kept, but the exact
/// previous bytes are kept next to it (<see cref="Backup"/>).
/// </summary>
public static class ConfigFile
{
    public const int MaxBytes = 256 * 1024;
    public const int KeptBackups = 5;
    internal const string BackupPrefix = "config.json.bak-";

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string EtagOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>The etag GET /config reports for a missing config.json.</summary>
    public const string MissingEtag = "missing";

    /// <summary>The root object; an empty one for a missing file.</summary>
    public static JsonObject Parse(byte[]? bytes)
    {
        if (bytes is null)
        {
            return new JsonObject();
        }

        if (bytes.Length > MaxBytes)
        {
            throw new ConfigFileException($"config.json is larger than {MaxBytes} bytes.");
        }

        var span = bytes.AsSpan();
        if (span.StartsWith(Encoding.UTF8.Preamble))
        {
            span = span[Encoding.UTF8.Preamble.Length..];
        }

        try
        {
            var root = JsonNode.Parse(span, documentOptions: ReadOptions) as JsonObject
                ?? throw new ConfigFileException("config.json must hold a JSON object.");

            // JsonObject reads its properties lazily: a name repeated anywhere surfaces here
            // rather than in the middle of a change.
            Materialize(root);
            return root;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new ConfigFileException("config.json is not valid JSON: " + ex.Message);
        }
    }

    private static void Materialize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    Materialize(child);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    Materialize(child);
                }

                break;
        }
    }

    public static byte[] Serialize(JsonObject root) =>
        new UTF8Encoding(false).GetBytes(root.ToJsonString(WriteOptions) + "\n");

    /// <summary>
    /// The one property of <paramref name="parent"/> whose name equals <paramref name="name"/>
    /// ignoring case, as the configuration provider matches it. Throws when there are several,
    /// since which one the Bridge would use is then unclear.
    /// </summary>
    public static string? FindKey(JsonObject parent, string name)
    {
        string? found = null;
        foreach (var (key, _) in parent)
        {
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found is not null)
            {
                throw new ConfigFileException($"config.json names {name} more than once (\"{found}\" and \"{key}\").");
            }

            found = key;
        }

        return found;
    }

    /// <summary>Names that occur more than once ignoring case, which the configuration provider would merge unpredictably.</summary>
    public static IReadOnlyList<string> CaseDuplicates(JsonObject node) =>
        node.Select(p => p.Key)
            .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" / ", g))
            .ToList();

    /// <summary>
    /// Keeps <paramref name="previous"/> as config.json.bak-&lt;UTC time&gt; and removes all but
    /// the newest <see cref="KeptBackups"/>. Throws when the backup cannot be written.
    /// </summary>
    public static string Backup(string configPath, byte[] previous, DateTimeOffset now)
    {
        var directory = Path.GetDirectoryName(configPath)!;
        var path = Path.Combine(directory, BackupPrefix + now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'"));
        WriteFlushed(path, previous, FileMode.CreateNew);

        var backups = Directory.GetFiles(directory, BackupPrefix + "*")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(KeptBackups);
        foreach (var old in backups)
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the next change.
            }
        }

        return path;
    }

    /// <summary>
    /// Writes a flushed temporary file next to <paramref name="path"/> and moves it over the
    /// file, retrying a few times while Windows reports the file in use. Before every attempt
    /// the file must still be <paramref name="expectedEtag"/> (<see cref="MissingEtag"/>: no
    /// file), so an edit saved on Windows meanwhile is never overwritten
    /// (<see cref="ConfigChangedException"/>); only the instant between that check and the
    /// move stays open. On any failure the old file is unchanged.
    /// </summary>
    public static void Replace(string path, byte[] bytes, string expectedEtag)
    {
        var temp = path + ".tmp";
        WriteFlushed(temp, bytes, FileMode.Create);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                if (CurrentEtag(path) != expectedEtag)
                {
                    throw new ConfigChangedException();
                }

                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
                {
                    Thread.Sleep(200 * attempt);
                }
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>The etag of the file now: <see cref="MissingEtag"/> when there is none. Throws when it cannot be read.</summary>
    public static string CurrentEtag(string path) =>
        File.Exists(path) ? EtagOf(File.ReadAllBytes(path)) : MissingEtag;

    private static void WriteFlushed(string path, byte[] bytes, FileMode mode)
    {
        using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
