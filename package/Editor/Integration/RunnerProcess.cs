using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace VRCRemoteTest
{
    /// <summary>
    /// Starting the packaged runner (Tools~/vrcrt_runner.py) from the Editor: the Python to use and
    /// its version, the runner's path in this package, fixed arguments quoted one by one (no shell),
    /// and an environment built from nothing: the Bridge address and pin and a fixed PATH, never a
    /// token (the CLI reads it from the Keychain itself). Output is read up to a limit.
    /// </summary>
    internal static class RunnerProcess
    {
        internal const string DefaultPython = "/usr/bin/python3";
        private const int MaxOutputChars = 64 * 1024;
        private static readonly Regex VersionPattern = new Regex(@"^(\d+)\.(\d+)$", RegexOptions.Compiled);

        internal sealed class Result
        {
            internal int ExitCode;
            internal string Stdout = string.Empty;
            internal string Stderr = string.Empty;
            internal bool TimedOut;
        }

        /// <summary>VRCRT_PYTHON when it names an existing absolute path, else /usr/bin/python3 (a GUI Unity's PATH is short).</summary>
        internal static string PythonPath()
        {
            var configured = System.Environment.GetEnvironmentVariable("VRCRT_PYTHON");
            return !string.IsNullOrEmpty(configured) && Path.IsPathRooted(configured) && File.Exists(configured)
                ? configured
                : DefaultPython;
        }

        /// <summary>The runner in this package, or null when the package has no Tools~ folder.</summary>
        internal static string RunnerPath()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(RunnerProcess).Assembly);
            if (info == null || string.IsNullOrEmpty(info.resolvedPath))
            {
                return null;
            }

            var path = Path.Combine(info.resolvedPath, "Tools~", "vrcrt_runner.py");
            return File.Exists(path) ? path : null;
        }

        /// <summary>Null when <paramref name="python"/> is Python 3.9 or later; otherwise why it cannot be used.</summary>
        internal static string CheckPython(string python)
        {
            if (!File.Exists(python))
            {
                return python + " does not exist. Install the Command Line Tools (xcode-select --install) or set VRCRT_PYTHON.";
            }

            var result = Run(python, new[] { "-I", "-c", "import sys; print('%d.%d' % sys.version_info[:2])" },
                ChildEnvironment(null, null), TimeSpan.FromSeconds(10));
            if (result.TimedOut || result.ExitCode != 0)
            {
                return python + " did not answer (on a Mac without the Command Line Tools it asks to install them: "
                    + "run xcode-select --install, or set VRCRT_PYTHON).";
            }

            return VersionIsEnough(result.Stdout.Trim())
                ? null
                : python + " is Python " + BridgeMessages.Sanitize(result.Stdout.Trim(), 20) + "; the runner needs 3.9 or later.";
        }

        internal static bool VersionIsEnough(string version)
        {
            var match = VersionPattern.Match(version ?? string.Empty);
            if (!match.Success)
            {
                return false;
            }

            var major = int.Parse(match.Groups[1].Value);
            var minor = int.Parse(match.Groups[2].Value);
            return major > 3 || (major == 3 && minor >= 9);
        }

        /// <summary>What the runner gets: HOME, LANG, TMPDIR, a fixed PATH and the pairing's address and pin. No token.</summary>
        internal static Dictionary<string, string> ChildEnvironment(string bridgeUrl, string fingerprint)
        {
            var environment = new Dictionary<string, string>
            {
                ["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin",
                ["LANG"] = "en_US.UTF-8",
            };
            foreach (var name in new[] { "HOME", "TMPDIR", "VRCRT_PYTHON" })
            {
                var value = System.Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value))
                {
                    environment[name] = value;
                }
            }

            if (!string.IsNullOrEmpty(bridgeUrl))
            {
                environment["VRCRT_BRIDGE_URL"] = bridgeUrl;
            }

            if (!string.IsNullOrEmpty(fingerprint))
            {
                environment["VRCRT_CERT_SHA256"] = fingerprint.ToLowerInvariant();
            }

            return environment;
        }

        /// <summary>
        /// Quotes one argument for ProcessStartInfo.Arguments as Mono on macOS and Linux splits it
        /// (shell rules: g_shell_parse_argv): inside single quotes nothing is special, and a single
        /// quote is written as '\''. Checked with a round trip through Python in the tests.
        /// </summary>
        internal static string Quote(string argument) => "'" + argument.Replace("'", "'\\''") + "'";

        /// <summary>Runs <paramref name="file"/> with these arguments and only this environment; stops it at the deadline.</summary>
        internal static Result Run(string file, IEnumerable<string> arguments, Dictionary<string, string> environment,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var info = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            var quoted = new List<string>();
            foreach (var argument in arguments)
            {
                quoted.Add(Quote(argument));
            }

            info.Arguments = string.Join(" ", quoted);
            info.EnvironmentVariables.Clear();
            foreach (var pair in environment)
            {
                info.EnvironmentVariables[pair.Key] = pair.Value;
            }

            var result = new Result();
            using (var process = new Process { StartInfo = info })
            {
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                process.OutputDataReceived += (_, e) => Append(stdout, e.Data);
                process.ErrorDataReceived += (_, e) => Append(stderr, e.Data);
                process.Start();
                process.StandardInput.Close();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                var end = DateTime.UtcNow + timeout;
                while (!process.WaitForExit(200))
                {
                    if (DateTime.UtcNow > end || cancellationToken.IsCancellationRequested)
                    {
                        result.TimedOut = true;
                        try
                        {
                            process.Kill();
                        }
                        catch (InvalidOperationException)
                        {
                        }

                        process.WaitForExit(5000);
                        break;
                    }
                }

                process.WaitForExit();
                result.ExitCode = result.TimedOut ? -1 : process.ExitCode;
                lock (stdout)
                {
                    result.Stdout = stdout.ToString();
                }

                lock (stderr)
                {
                    result.Stderr = stderr.ToString();
                }
            }

            return result;
        }

        private static void Append(StringBuilder builder, string line)
        {
            if (line == null)
            {
                return;
            }

            lock (builder)
            {
                if (builder.Length + line.Length + 1 <= MaxOutputChars)
                {
                    builder.Append(line).Append('\n');
                }
            }
        }
    }
}
