using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace VRCRemoteTest
{
    /// <summary>
    /// A mounted SMB share appears as a regular POSIX filesystem path on macOS
    /// (e.g. /Volumes/VRCRemoteTest), so this transport is plain File.Copy /
    /// File.Move against that path — no SMB protocol code needed. Tests
    /// substitute a local temp directory for sharePath and exercise the same
    /// code path (spec section 55's approach to mocking VRChat.exe, applied
    /// here to the mounted share).
    /// </summary>
    public sealed class SmbRemoteTransport : IRemoteTransport
    {
        private readonly string _sharePath;

        public SmbRemoteTransport(string sharePath)
        {
            ValidateShareRoot(sharePath);
            _sharePath = sharePath;
        }

        private const long MaxVrchatStatusFileBytes = 64 * 1024;

        // Above the Bridge's 384 KiB publish cap for headroom (Codex plan
        // review Phase 5, Round 2, confidence 0.90): a full-to-cap published
        // file can never fail this guard.
        private const long MaxVrchatLogFileBytes = 512 * 1024;

        private string IncomingDir => Path.Combine(_sharePath, "incoming");
        private string ResultsDir => Path.Combine(_sharePath, "results");
        private string StatusDir => Path.Combine(_sharePath, "status");
        private string LogsDir => Path.Combine(_sharePath, "logs");

        public bool IsAvailable
        {
            get
            {
                try
                {
                    if (!Directory.Exists(_sharePath))
                    {
                        return false;
                    }

                    if (!Directory.Exists(IncomingDir))
                    {
                        Directory.CreateDirectory(IncomingDir);
                    }

                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        public async Task UploadBuildAsync(
            BuildArtifact artifact,
            BuildManifest manifest,
            string buildId,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(IncomingDir);

            var artifactFinalPath = CombineUnderRoot(IncomingDir, $"{buildId}.vrcw");
            var manifestFinalPath = CombineUnderRoot(IncomingDir, $"{buildId}{ProtocolConstants.ManifestExtension}");

            await Task.Run(
                () => AtomicFile.CopyAtomic(artifact.FullPath, artifactFinalPath),
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var json = JsonConvert.SerializeObject(manifest, Formatting.Indented);
            AtomicFile.WriteAllText(manifestFinalPath, json);
        }

        public BuildResult PollResult(string buildId)
        {
            var resultPath = CombineUnderRoot(ResultsDir, $"{buildId}.json");
            if (!File.Exists(resultPath))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(resultPath);
                return JsonConvert.DeserializeObject<BuildResult>(json, new JsonSerializerSettings
                {
                    TypeNameHandling = TypeNameHandling.None,
                    MaxDepth = 8,
                });
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Transient: the Bridge may be mid-write (it uses .json.tmp ->
                // rename, but there is a brief window). Caller keeps polling.
                return null;
            }
        }

        /// <summary>
        /// Phase 4a advisory status, never a build-blocking value. The 64 KiB size
        /// guard is stricter than PollResult's (no guard) because this file is
        /// rewritten roughly every 10s versus once per build, giving a partial-read
        /// race over SMB more chances to be hit in practice (Codex plan review
        /// Phase 4a, Round 2, confidence 0.80).
        /// </summary>
        public VrchatStatus PollVrchatStatus()
        {
            var statusPath = CombineUnderRoot(StatusDir, "vrchat-status.json");
            if (!File.Exists(statusPath))
            {
                return null;
            }

            try
            {
                var info = new FileInfo(statusPath);
                if (info.Length > MaxVrchatStatusFileBytes)
                {
                    return null;
                }

                var json = File.ReadAllText(statusPath);
                return JsonConvert.DeserializeObject<VrchatStatus>(json, new JsonSerializerSettings
                {
                    TypeNameHandling = TypeNameHandling.None,
                    MaxDepth = 8,
                });
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Phase 5 advisory log content, purely for display — never gates any
        /// build decision. Plain text, not JSON, so no JsonException catch is
        /// needed (only IOException for the transient mid-write race, same
        /// tolerance as PollResult/PollVrchatStatus).
        /// </summary>
        public string PollVrchatLog()
        {
            var logPath = CombineUnderRoot(LogsDir, "vrchat-latest.log");
            if (!File.Exists(logPath))
            {
                return null;
            }

            try
            {
                var info = new FileInfo(logPath);
                if (info.Length > MaxVrchatLogFileBytes)
                {
                    return null;
                }

                return File.ReadAllText(logPath);
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static void ValidateShareRoot(string sharePath)
        {
            if (string.IsNullOrWhiteSpace(sharePath))
            {
                throw new RemoteBuildException(
                    ErrorCode.InvalidConfiguration,
                    "SharePath is not configured.");
            }

            if (sharePath.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            {
                throw new RemoteBuildException(
                    ErrorCode.InvalidConfiguration,
                    "SharePath must be a mounted filesystem path (e.g. /Volumes/VRCRemoteTest), " +
                    "not an smb:// URI. Mount the share first, then configure the mounted path. " +
                    "SMB credentials belong in the OS mount, never in this setting.");
            }

            var trimmed = sharePath.Trim();
            if (trimmed == "/" || trimmed == string.Empty)
            {
                throw new RemoteBuildException(
                    ErrorCode.InvalidConfiguration,
                    "SharePath cannot be empty or the filesystem root.");
            }
        }

        /// <summary>
        /// Defense-in-depth containment check. The relative names passed in
        /// are always internally generated from buildId, never external input,
        /// but this stays cheap insurance against a future refactor mistake
        /// (Codex plan review Round 2, confidence 0.88).
        /// </summary>
        private string CombineUnderRoot(string directory, string fileName)
        {
            var combined = Path.GetFullPath(Path.Combine(directory, fileName));
            var normalizedRoot = Path.GetFullPath(_sharePath);

            if (!combined.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new RemoteBuildException(
                    ErrorCode.InvalidConfiguration,
                    $"Derived path escapes the configured share root: {fileName}");
            }

            return combined;
        }
    }
}
