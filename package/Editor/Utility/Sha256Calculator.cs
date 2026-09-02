using System;
using System.IO;
using System.Security.Cryptography;

namespace VRCRemoteTest
{
    /// <summary>
    /// Plain static class, not behind an interface — this wraps a single
    /// deterministic computation with no plausible alternate implementation
    /// (Codex plan review, Round 2-3: confidence 0.93).
    /// </summary>
    public static class Sha256Calculator
    {
        /// <summary>Returns the lowercase hex SHA-256 hash of the file.</summary>
        public static string ComputeHash(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
