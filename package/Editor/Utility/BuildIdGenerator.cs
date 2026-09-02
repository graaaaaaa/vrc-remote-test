using System;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace VRCRemoteTest
{
    public static class BuildIdGenerator
    {
        private static readonly Regex ValidPattern =
            new Regex(@"^\d{8}T\d{9}Z-[0-9a-f]{8}$", RegexOptions.Compiled);

        /// <summary>
        /// Generates a build ID in the format yyyyMMddTHHmmssfffZ-xxxxxxxx
        /// (UTC timestamp + 8 random hex characters), per spec section 13.
        /// </summary>
        public static string Generate()
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'");

            var bytes = new byte[4];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            var hex = BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            return $"{timestamp}-{hex}";
        }

        /// <summary>
        /// Validates that a buildId matches the expected format. Used defensively
        /// even on self-generated IDs before writing a manifest or trusting a
        /// polled result, per the Phase 2 Codex security review.
        /// </summary>
        public static bool IsValid(string buildId)
        {
            return !string.IsNullOrEmpty(buildId) && ValidPattern.IsMatch(buildId);
        }
    }
}
