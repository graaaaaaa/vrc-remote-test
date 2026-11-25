using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEngine;

namespace VRCRemoteTest
{
    /// <summary>
    /// Saves deploy results where the packaged runner reads them: &lt;root&gt;/deploy-results/
    /// &lt;buildId&gt;.json (attempt-&lt;attemptId&gt;.json when no build ID was allocated) and, inside a
    /// run, &lt;runDir&gt;/deploy-result.json. The root is ~/Library/Application Support/VRCRemoteTest
    /// on macOS: outside Git and iCloud. Folders are 0700 and files 0600 (owner only, not
    /// encryption); a file is written under a unique temporary name, flushed to disk, and moved
    /// into place without replacing an existing result. Nothing here throws: a result that
    /// cannot be saved is logged and reported as a null path, which the runner treats as UNKNOWN.
    /// </summary>
    internal sealed class DeployResultWriter
    {
        internal const string RunResultFileName = "deploy-result.json";
        private static readonly Regex RunIdPattern = new Regex(@"^\d{8}T\d{6}Z-[0-9a-f]{8}$", RegexOptions.Compiled);
        private static readonly Regex AttemptIdPattern = new Regex(@"^\d{8}T\d{9}Z-[0-9a-f]{8}$", RegexOptions.Compiled);
        private const int MaxMessageLength = 2000;

        internal DeployResultWriter(string root)
        {
            Root = root;
        }

        internal string Root { get; }

        internal static DeployResultWriter CreateDefault() => new DeployResultWriter(DefaultRoot());

        internal static string DefaultRoot()
        {
            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                var home = Environment.GetEnvironmentVariable("HOME");
                if (!string.IsNullOrEmpty(home))
                {
                    return Path.Combine(home, "Library", "Application Support", "VRCRemoteTest");
                }
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCRemoteTest");
        }

        /// <summary>Builds the file's content from an attempt that has finished.</summary>
        internal static DeployResult Build(RemoteBuildOutcome outcome, string packageVersion)
        {
            var result = outcome.Result;
            return new DeployResult
            {
                AttemptId = outcome.AttemptId,
                BuildId = BuildIdGenerator.IsValid(outcome.BuildId) ? outcome.BuildId : null,
                PackageVersion = string.IsNullOrEmpty(packageVersion) ? null : packageVersion,
                Kind = outcome.Kind,
                StartedAtUtc = outcome.StartedAtUtc,
                FinishedAtUtc = outcome.FinishedAtUtc,
                Stage = outcome.Stage,
                Artifact = new DeployResultArtifact
                {
                    Sha256 = outcome.ArtifactSha256,
                    Size = outcome.ArtifactSize,
                    WorldBuildTag = outcome.WorldBuildTag,
                },
                Unity = new DeployResultUnity
                {
                    Succeeded = outcome.Succeeded,
                    Canceled = outcome.Canceled,
                    ErrorCode = outcome.ErrorCode,
                    Message = Cut(outcome.ErrorMessage),
                },
                BridgeResult = result == null
                    ? null
                    : new DeployResultBridge
                    {
                        Status = result.Status,
                        ErrorCode = result.ErrorCode,
                        Sha256 = result.Sha256,
                        DeployedFileName = result.DeployedFileName,
                        Launch = result.Launch,
                        WarningCount = result.Warnings?.Count ?? 0,
                    },
                Classification = DeployResultClassifier.Classify(outcome),
            };
        }

        /// <summary>
        /// Writes the result to deploy-results/ and, when <paramref name="runDirectory"/> is a valid
        /// run folder, to the run. Returns the path in the run when there is one, else the other;
        /// null when the file that matters was not written.
        /// </summary>
        internal string TryWrite(DeployResult result, string runDirectory)
        {
            string json;
            try
            {
                json = JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                Debug.LogError("[VRC Remote Test] Could not serialize the deploy result: " + ex.Message);
                return null;
            }

            string savedPath = null;
            var name = FileNameFor(result);
            if (name == null)
            {
                Debug.LogError("[VRC Remote Test] The deploy result has neither a valid build ID nor attempt ID; not saved.");
            }
            else
            {
                try
                {
                    var directory = Path.Combine(Root, "deploy-results");
                    EnsurePrivateDirectory(Root);
                    EnsurePrivateDirectory(directory);
                    savedPath = Path.Combine(directory, name);
                    WriteNew(directory, savedPath, json);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
                {
                    Debug.LogError("[VRC Remote Test] Could not save the deploy result: " + ex.Message);
                    savedPath = null;
                }
            }

            if (string.IsNullOrEmpty(runDirectory))
            {
                return savedPath;
            }

            try
            {
                var run = ValidatedRunDirectory(runDirectory);
                var path = Path.Combine(run, RunResultFileName);
                WriteNew(run, path, json);
                return path;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                Debug.LogError("[VRC Remote Test] Could not save the deploy result in the run folder: " + ex.Message);
                return null;
            }
        }

        internal static string FileNameFor(DeployResult result)
        {
            if (BuildIdGenerator.IsValid(result.BuildId))
            {
                return result.BuildId + ".json";
            }

            return result.AttemptId != null && AttemptIdPattern.IsMatch(result.AttemptId)
                ? "attempt-" + result.AttemptId + ".json"
                : null;
        }

        /// <summary>
        /// A run folder is runs/&lt;runId&gt; made by the runner: an absolute path with no "..", named
        /// like a run ID, whose parent is called runs, with a run.json in it. Neither the folder
        /// nor its parent may be a symbolic link. Throws InvalidOperationException otherwise.
        /// </summary>
        internal static string ValidatedRunDirectory(string runDirectory)
        {
            if (!Path.IsPathRooted(runDirectory))
            {
                throw new InvalidOperationException("the run folder is not an absolute path");
            }

            var full = Path.GetFullPath(runDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(full, runDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("the run folder path is not normalized");
            }

            var name = Path.GetFileName(full);
            var parent = Path.GetDirectoryName(full);
            if (!RunIdPattern.IsMatch(name) || parent == null || Path.GetFileName(parent) != "runs")
            {
                throw new InvalidOperationException("the folder is not runs/<runId>");
            }

            if (!Directory.Exists(full) || !File.Exists(Path.Combine(full, "run.json")))
            {
                throw new InvalidOperationException("the run folder or its run.json does not exist");
            }

            if (IsLink(full) || IsLink(parent))
            {
                throw new InvalidOperationException("the run folder or its parent is a symbolic link");
            }

            if (SyncedOrTrackedLocation(full) is string where)
            {
                throw new InvalidOperationException("the run folder is " + where + "; raw evidence stays outside Git and synced folders");
            }

            // Only the owner can change a folder's mode, so this also proves both are ours.
            SetMode(parent, Convert.ToInt32("700", 8));
            SetMode(full, Convert.ToInt32("700", 8));
            return full;
        }

        /// <summary>
        /// Why a folder must not hold raw evidence, or null: inside a Git work tree, iCloud Drive or
        /// another cloud folder, or ~/Documents or ~/Desktop (which iCloud may sync).
        /// </summary>
        internal static string SyncedOrTrackedLocation(string folder)
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                var resolvedHome = RealPathOrSelf(home);
                var resolved = RealPathOrSelf(folder);
                foreach (var synced in new[]
                         {
                             Path.Combine("Library", "Mobile Documents"), Path.Combine("Library", "CloudStorage"),
                             "Documents", "Desktop",
                         })
                {
                    if (IsInside(resolved, Path.Combine(resolvedHome, synced)) || IsInside(folder, Path.Combine(home, synced)))
                    {
                        return "under ~/" + synced;
                    }
                }
            }

            // Both the path as given and the one it resolves to: a link in an ancestor may lead into a work tree.
            var real = UnixLike ? RealPath(folder) : folder;
            if (real == null)
            {
                return "a path that cannot be resolved";
            }

            return GitWorkTreeAbove(real) ?? GitWorkTreeAbove(folder);
        }

        private static string GitWorkTreeAbove(string folder)
        {
            for (var current = folder; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                var git = Path.Combine(current, ".git");
                if (Directory.Exists(git) || File.Exists(git))
                {
                    return "inside the Git work tree " + current;
                }
            }

            return null;
        }

        private static bool UnixLike =>
            Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.LinuxEditor;

        private static bool IsInside(string path, string folder) =>
            string.Equals(path, folder, StringComparison.Ordinal)
            || path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

        private static string RealPathOrSelf(string path) =>
            Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.LinuxEditor
                ? RealPath(path) ?? path
                : path;

        private static bool IsLink(string path)
        {
            if (Application.platform != RuntimePlatform.OSXEditor && Application.platform != RuntimePlatform.LinuxEditor)
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }

            var parent = Path.GetDirectoryName(path);
            var resolved = RealPath(path);
            var resolvedParent = parent == null ? null : RealPath(parent);
            return resolved == null || resolvedParent == null
                || !string.Equals(resolved, Path.Combine(resolvedParent, Path.GetFileName(path)), StringComparison.Ordinal);
        }

        private static void EnsurePrivateDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            if (IsLink(path))
            {
                throw new InvalidOperationException(path + " is a symbolic link");
            }

            SetMode(path, Convert.ToInt32("700", 8));
        }

        /// <summary>Unique temporary file, flushed to disk, then moved to a name that must not exist yet.</summary>
        private static void WriteNew(string directory, string finalPath, string json)
        {
            if (File.Exists(finalPath))
            {
                throw new IOException(Path.GetFileName(finalPath) + " already exists; a finished result is never replaced");
            }

            var temporary = Path.Combine(directory, "." + Path.GetFileName(finalPath) + "." + Guid.NewGuid().ToString("N") + ".part");
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(json + "\n");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    SetMode(temporary, Convert.ToInt32("600", 8));
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                Publish(temporary, finalPath);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        /// <summary>
        /// Gives the finished temporary file its final name only if that name is free: link(2)
        /// fails atomically when it exists, so two writers cannot replace each other's result
        /// (File.Move checks and renames in two steps). Elsewhere File.Move, which also refuses an
        /// existing name.
        /// </summary>
        private static void Publish(string temporary, string finalPath)
        {
            if (Application.platform != RuntimePlatform.OSXEditor && Application.platform != RuntimePlatform.LinuxEditor)
            {
                File.Move(temporary, finalPath);
                return;
            }

            if (link(temporary, finalPath) != 0)
            {
                var errno = Marshal.GetLastWin32Error();
                throw new IOException(errno == EEXIST
                    ? Path.GetFileName(finalPath) + " already exists; a finished result is never replaced"
                    : "could not save " + Path.GetFileName(finalPath) + " (errno " + errno + ")");
            }
        }

        private static string Cut(string message) =>
            message == null || message.Length <= MaxMessageLength ? message : message.Substring(0, MaxMessageLength) + "...(truncated)";

        private static void SetMode(string path, int mode)
        {
            if (Application.platform != RuntimePlatform.OSXEditor && Application.platform != RuntimePlatform.LinuxEditor)
            {
                return;
            }

            if (chmod(path, mode) != 0)
            {
                throw new IOException("could not set the permissions of " + path + " (errno " + Marshal.GetLastWin32Error() + ")");
            }
        }

        private static string RealPath(string path)
        {
            var pointer = realpath(path, IntPtr.Zero);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUTF8(pointer);
            }
            finally
            {
                free(pointer);
            }
        }

        private const int EEXIST = 17;

        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, int mode);

        [DllImport("libc", SetLastError = true)]
        private static extern int link(string existingPath, string newPath);

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr realpath(string path, IntPtr resolvedPath);

        [DllImport("libc")]
        private static extern void free(IntPtr pointer);
    }
}
