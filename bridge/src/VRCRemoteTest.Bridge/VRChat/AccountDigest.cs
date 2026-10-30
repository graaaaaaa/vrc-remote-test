using System.Security.Cryptography;
using System.Text;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Lets a reader tell whether two clients or profiles are logged in to the same VRChat
/// account without ever seeing the account's usr_ ID, which the Bridge never publishes
/// (<see cref="LogRedactor"/>). The digest is HMAC-SHA256 of the raw ID under a random key
/// that never leaves StateDirectory, as the first 12 lowercase hex characters: equal IDs
/// give equal digests on this Bridge, and the ID cannot be found from the digest without
/// the key. The key is read once per process; when it is missing or unreadable a new one is
/// made, and digests recorded under the old key no longer mean anything
/// (<see cref="Regenerated"/>).
/// </summary>
public sealed class AccountDigest
{
    public const string KeyFileName = "account-key.bin";
    private const int KeyBytes = 32;
    private const int DigestHexChars = 12;

    private readonly byte[] _key;

    private AccountDigest(byte[] key, bool regenerated)
    {
        _key = key;
        Regenerated = regenerated;
    }

    /// <summary>True when this process made a new key, so earlier digests are void.</summary>
    public bool Regenerated { get; }

    public static AccountDigest LoadOrCreate(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, KeyFileName);
        try
        {
            if (File.Exists(path))
            {
                var stored = File.ReadAllBytes(path);
                if (stored.Length == KeyBytes)
                {
                    return new AccountDigest(stored, regenerated: false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: replaced below, which voids the old digests.
        }

        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, key);
        File.Move(temp, path, overwrite: true);
        return new AccountDigest(key, regenerated: true);
    }

    /// <summary>For tests: a digest under a fixed key.</summary>
    internal static AccountDigest ForKey(byte[] key) => new(key, regenerated: false);

    public string Of(string userId)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(userId));
        return Convert.ToHexString(mac).ToLowerInvariant()[..DigestHexChars];
    }
}
