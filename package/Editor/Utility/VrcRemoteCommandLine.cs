using System;

namespace VRCRemoteTest
{
    /// <summary>
    /// Exact-match parser for VRC Remote Test's own custom command-line flags
    /// (e.g. -vrcRemoteTestSharePath), read from
    /// System.Environment.GetCommandLineArgs(). Centralizing this avoids
    /// scattering raw positional-array scanning through settings code
    /// (Codex plan review Round 2-3, confidence 0.93).
    /// </summary>
    public static class VrcRemoteCommandLine
    {
        /// <summary>
        /// Looks up a flag of the form "-flagName value" in the process's raw
        /// command-line arguments.
        /// </summary>
        /// <returns>
        /// true if the flag was found with a value; false if the flag was not
        /// present at all (not an error) OR if parsing failed (see
        /// <paramref name="error"/>, which is non-null only on a real error:
        /// the flag present without a following value, or the flag repeated).
        /// </returns>
        public static bool TryGetFlagValue(string flagName, out string value, out string error)
        {
            return TryGetFlagValue(Environment.GetCommandLineArgs(), flagName, out value, out error);
        }

        internal static bool TryGetFlagValue(string[] args, string flagName, out string value, out string error)
        {
            value = null;
            error = null;
            var foundIndex = -1;

            for (var i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], flagName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (foundIndex != -1)
                {
                    error = $"Duplicate flag: {flagName}";
                    return false;
                }

                foundIndex = i;
            }

            if (foundIndex == -1)
            {
                return false;
            }

            if (foundIndex + 1 >= args.Length)
            {
                error = $"Flag {flagName} is missing a value.";
                return false;
            }

            value = args[foundIndex + 1];
            return true;
        }
    }
}
