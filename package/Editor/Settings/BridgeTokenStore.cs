using System;
using System.Diagnostics;
using UnityEngine;

namespace VRCRemoteTest
{
    /// <summary>Per-user macOS Keychain storage; CI supplies the token by environment.</summary>
    internal static class BridgeTokenStore
    {
        private const string Service = "VRCRemoteTest.LanDirect";

        public static void Save(string url, string token)
        {
            if (Application.platform != RuntimePlatform.OSXEditor)
                throw new RemoteBuildException(ErrorCode.InvalidConfiguration,
                    "Interactive pairing requires the macOS Keychain; use VRC_REMOTE_TEST_TOKEN for headless builds.");
            Run("add-generic-password", "-U", "-s", Service, "-a", url, "-w", token);
        }

        public static string Read(string url)
        {
            if (Application.platform != RuntimePlatform.OSXEditor)
                throw new RemoteBuildException(ErrorCode.InvalidConfiguration,
                    "Set VRC_REMOTE_TEST_TOKEN for this platform.");
            var token = Run("find-generic-password", "-s", Service, "-a", url, "-w").Trim();
            if (token.Length != 64)
                throw new RemoteBuildException(ErrorCode.InvalidConfiguration, "Bridge pairing token is missing from Keychain.");
            return token;
        }

        private static string Run(params string[] arguments)
        {
            var start = new ProcessStartInfo("/usr/bin/security")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using (var process = Process.Start(start))
            {
                var output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(5000) || process.ExitCode != 0)
                    throw new RemoteBuildException(ErrorCode.InvalidConfiguration,
                        "Could not access the VRC Remote Test pairing token in macOS Keychain.");
                return output;
            }
        }
    }
}
