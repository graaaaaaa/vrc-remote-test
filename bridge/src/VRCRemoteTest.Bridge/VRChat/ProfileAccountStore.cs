using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>The account a profile was last seen logged in to, as GET /profiles returns it.</summary>
public sealed class ProfileAccount
{
    [JsonPropertyName("profile")]
    public int Profile { get; init; }

    /// <summary>Untrusted: whatever name the log carried, masked like every published name.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>See <see cref="AccountDigest"/>; equal values mean the same account on this Bridge.</summary>
    [JsonPropertyName("idDigest")]
    public string IdDigest { get; init; } = string.Empty;

    /// <summary>The time on the "User Authenticated" line, not when the Bridge read it.</summary>
    [JsonPropertyName("authenticatedAtUtc")]
    public DateTimeOffset AuthenticatedAtUtc { get; init; }

    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    [JsonPropertyName("clientGeneration")]
    public long ClientGeneration { get; init; }
}

/// <summary>
/// StateDirectory\status\profile-accounts.json: per Relaunch profile, the account its client
/// last logged in to. Only VrchatLogService's loop writes it (through the observer), when a
/// client of a recorded profile shows a "User Authenticated" line newer than the one kept;
/// reading a log again from its start therefore never makes an entry look newer. A profile
/// whose client has not run since is shown as it was: the account may have changed on
/// Windows since then, which only the time tells.
///
/// The file names the digest key it was written under (<see cref="AccountDigest.KeyId"/>);
/// a file under another key is ignored, so a new key voids the old digests without having
/// to delete anything. An entry is kept in memory only once it is on disk, so a write that
/// fails is tried again with the next poll.
/// </summary>
public sealed class ProfileAccountStore
{
    public const string FileName = "profile-accounts.json";
    private const int MaxFileBytes = 64 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly object _sync = new();
    private readonly string _keyId;
    private Dictionary<int, ProfileAccount>? _accounts;

    public ProfileAccountStore(IOptions<BridgeOptions> options, AccountDigest digest)
        : this(options.Value.StateDirectory, digest.KeyId)
    {
    }

    internal ProfileAccountStore(string stateDirectory, string keyId)
    {
        FilePath = Path.Combine(stateDirectory, "status", FileName);
        _keyId = keyId;
    }

    public string FilePath { get; }

    public IReadOnlyList<ProfileAccount> All
    {
        get
        {
            lock (_sync)
            {
                return Accounts().Values.OrderBy(a => a.Profile).ToList();
            }
        }
    }

    /// <summary>Keeps <paramref name="account"/> when it is newer than what the profile has; true when the file changed.</summary>
    public bool Record(ProfileAccount account)
    {
        lock (_sync)
        {
            var accounts = Accounts();
            if (accounts.TryGetValue(account.Profile, out var kept)
                && kept.AuthenticatedAtUtc >= account.AuthenticatedAtUtc)
            {
                return false;
            }

            var updated = new Dictionary<int, ProfileAccount>(accounts) { [account.Profile] = account };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(
                    new StoredFile { KeyId = _keyId, Accounts = updated.Values.OrderBy(a => a.Profile).ToList() },
                    SerializerOptions));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not kept: the next poll offers the same login again.
                return false;
            }

            _accounts = updated;
            return true;
        }
    }

    /// <summary>
    /// Pairs of profiles whose last seen account is the same, as "0 and 1". Relaunch mode
    /// expects one account per profile.
    /// </summary>
    public IReadOnlyList<string> SameAccountWarnings(IEnumerable<int>? profiles)
    {
        var wanted = profiles?.ToHashSet();
        var accounts = All.Where(a => wanted is null || wanted.Contains(a.Profile)).ToList();
        return accounts
            .GroupBy(a => a.IdDigest)
            .Where(g => g.Count() > 1)
            .Select(g =>
                $"Profiles {string.Join(" and ", g.Select(a => a.Profile))} were last seen logged in to the same VRChat account " +
                $"({g.First().DisplayName}); Relaunch mode expects a different account per profile. Log one of them in to " +
                "another account on Windows.")
            .ToList();
    }

    private Dictionary<int, ProfileAccount> Accounts()
    {
        if (_accounts is not null)
        {
            return _accounts;
        }

        _accounts = [];
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length <= MaxFileBytes)
            {
                var stored = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(FilePath));
                if (stored?.KeyId == _keyId)
                {
                    foreach (var account in stored.Accounts ?? [])
                    {
                        if (account.Profile is >= 0 and <= BridgeOptions.MaxVrchatProfile && account.IdDigest.Length > 0)
                        {
                            _accounts[account.Profile] = account;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Starts empty; the next login line fills it again.
        }

        return _accounts;
    }

    private sealed class StoredFile
    {
        [JsonPropertyName("keyId")]
        public string? KeyId { get; init; }

        [JsonPropertyName("accounts")]
        public List<ProfileAccount>? Accounts { get; init; }
    }
}
