using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRCRemoteTest
{
    /// <summary>
    /// Which VRChat clients the Bridge launches: Reload mode with a client count (one account,
    /// --watch-worlds), or Relaunch mode with one client per VRChat profile (separate accounts,
    /// closed and launched again on every deploy). The window's "VRChat Clients" section edits
    /// these three settings of the Bridge's config.json through PUT /api/v1/config (Bridge
    /// v0.8.0 and later); the Bridge validates them again and restarts its host to apply them.
    /// </summary>
    public sealed class VrchatClientSettings
    {
        public const string ReloadMode = "Reload";
        public const string RelaunchMode = "Relaunch";

        /// <summary>The same limits as the Bridge's BridgeOptions (MaxVrchatClientCount, MaxVrchatProfile).</summary>
        public const int MaxClients = 4;
        public const int MaxProfile = 99;

        private VrchatClientSettings(string mode, int count, IReadOnlyList<int> profiles)
        {
            Mode = mode;
            Count = count;
            Profiles = profiles;
        }

        /// <summary><see cref="ReloadMode"/> or <see cref="RelaunchMode"/>.</summary>
        public string Mode { get; }

        public bool IsRelaunch => Mode == RelaunchMode;

        /// <summary>The number of clients: the count in Reload mode, the number of profiles in Relaunch mode.</summary>
        public int Count { get; }

        /// <summary>The VRChat profiles in Relaunch mode, in launch order; empty in Reload mode.</summary>
        public IReadOnlyList<int> Profiles { get; }

        public static VrchatClientSettings Reload(int count)
        {
            if (count < 1 || count > MaxClients)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, $"Reload mode runs 1 to {MaxClients} clients.");
            }

            return new VrchatClientSettings(ReloadMode, count, Array.Empty<int>());
        }

        public static VrchatClientSettings Relaunch(IEnumerable<int> profiles)
        {
            var list = profiles?.ToArray() ?? Array.Empty<int>();
            var problem = CheckProfiles(list);
            if (problem != null)
            {
                throw new ArgumentException(problem, nameof(profiles));
            }

            return new VrchatClientSettings(RelaunchMode, list.Length, list);
        }

        /// <summary>
        /// The Bridge's settings from the activeConfig of GET /bridge, as its host runs them; null
        /// when they are missing or not a shape the Bridge would run (an older Bridge, a change
        /// not applied yet that the Bridge refused). Untrusted input: never throws.
        /// </summary>
        public static VrchatClientSettings FromActiveConfig(JObject activeConfig)
        {
            if (activeConfig == null)
            {
                return null;
            }

            var mode = activeConfig["VrchatClientMode"];
            if (mode == null || mode.Type != JTokenType.String)
            {
                return null;
            }

            if (string.Equals((string)mode, RelaunchMode, StringComparison.OrdinalIgnoreCase))
            {
                if (!(activeConfig["VrchatProfiles"] is JArray array) || array.Count == 0 || array.Count > MaxClients)
                {
                    return null;
                }

                var profiles = new List<int>();
                foreach (var item in array)
                {
                    var value = SmallInteger(item);
                    if (value == null)
                    {
                        return null;
                    }

                    profiles.Add(value.Value);
                }

                return CheckProfiles(profiles) == null
                    ? new VrchatClientSettings(RelaunchMode, profiles.Count, profiles)
                    : null;
            }

            if (string.Equals((string)mode, ReloadMode, StringComparison.OrdinalIgnoreCase))
            {
                // An omitted VrchatClientCount is one client, as on the Bridge.
                var countToken = activeConfig["VrchatClientCount"];
                var count = countToken == null || countToken.Type == JTokenType.Null ? 1 : SmallInteger(countToken);
                return count is int c && c >= 1 && c <= MaxClients ? new VrchatClientSettings(ReloadMode, c, Array.Empty<int>()) : null;
            }

            return null;
        }

        /// <summary>
        /// "0, 1, 2, 3" (commas or spaces) into profile numbers, in the order given: 1 to
        /// <see cref="MaxClients"/> numbers from 0 to <see cref="MaxProfile"/>, none twice. On
        /// failure, <paramref name="problem"/> says why in one line.
        /// </summary>
        public static bool TryParseProfiles(string text, out int[] profiles, out string problem)
        {
            profiles = null;
            var parts = (text ?? string.Empty).Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<int>();
            foreach (var part in parts)
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    problem = $"\"{BridgeMessages.Sanitize(part, 20)}\" is not a profile number.";
                    return false;
                }

                list.Add(number);
            }

            problem = CheckProfiles(list);
            if (problem != null)
            {
                return false;
            }

            profiles = list.ToArray();
            return true;
        }

        public static string FormatProfiles(IEnumerable<int> profiles) =>
            string.Join(", ", profiles.Select(p => p.ToString(CultureInfo.InvariantCulture)));

        /// <summary>
        /// The JSON Merge Patch for PUT /config. The setting the other mode uses is removed
        /// (null), since the Bridge refuses VrchatClientCount in Relaunch mode and
        /// VrchatProfiles in Reload mode.
        /// </summary>
        public string ToMergePatch()
        {
            var patch = new JObject
            {
                ["VrchatClientMode"] = Mode,
                ["VrchatClientCount"] = IsRelaunch ? JValue.CreateNull() : new JValue(Count),
                ["VrchatProfiles"] = IsRelaunch ? new JArray(Profiles.Cast<object>().ToArray()) : (JToken)JValue.CreateNull(),
            };
            return patch.ToString(Formatting.None);
        }

        /// <summary>"Relaunch · profiles 0, 1, 2, 3" or "Reload · 2 clients".</summary>
        public string Describe() =>
            IsRelaunch
                ? $"Relaunch · profile{(Count == 1 ? string.Empty : "s")} {FormatProfiles(Profiles)}"
                : $"Reload · {Count} client{(Count == 1 ? string.Empty : "s")}";

        public bool SameAs(VrchatClientSettings other) =>
            other != null && Mode == other.Mode && Count == other.Count && Profiles.SequenceEqual(other.Profiles);

        private static string CheckProfiles(IReadOnlyCollection<int> profiles)
        {
            if (profiles.Count == 0 || profiles.Count > MaxClients)
            {
                return $"List 1 to {MaxClients} profiles.";
            }

            var outOfRange = profiles.Where(p => p < 0 || p > MaxProfile).ToArray();
            if (outOfRange.Length > 0)
            {
                return $"Profiles are 0 to {MaxProfile} (not {FormatProfiles(outOfRange)}).";
            }

            var repeated = profiles.GroupBy(p => p).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            return repeated.Length > 0
                ? $"Each profile once: two clients on one profile share one account (repeated: {FormatProfiles(repeated)})."
                : null;
        }

        private static int? SmallInteger(JToken token)
        {
            var value = BridgeMessages.ByteCount(token);
            return value != null && value.Value <= int.MaxValue ? (int)value.Value : (int?)null;
        }
    }

    /// <summary>The result of applying <see cref="VrchatClientSettings"/>; see RemoteBuildCoordinator.ApplyVrchatClientSettingsAsync.</summary>
    public sealed class VrchatClientSettingsOutcome
    {
        private VrchatClientSettingsOutcome(bool applied, string message, IReadOnlyList<string> details)
        {
            Applied = applied;
            Message = message;
            Details = details;
        }

        /// <summary>True only when the Bridge reported the change as applied by a restarted host.</summary>
        public bool Applied { get; }

        /// <summary>One line for the window; Bridge text in it is sanitized.</summary>
        public string Message { get; }

        /// <summary>The Bridge's problems or notes, sanitized; may be empty.</summary>
        public IReadOnlyList<string> Details { get; }

        internal static VrchatClientSettingsOutcome Success(string message, IEnumerable<string> notes) =>
            new VrchatClientSettingsOutcome(true, message, Clean(notes));

        internal static VrchatClientSettingsOutcome Failure(string message, IEnumerable<string> problems = null) =>
            new VrchatClientSettingsOutcome(false, message, Clean(problems));

        private static IReadOnlyList<string> Clean(IEnumerable<string> lines) =>
            (lines ?? Enumerable.Empty<string>())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Take(10)
                .Select(l => BridgeMessages.Sanitize(l, 500))
                .ToList();
    }
}
