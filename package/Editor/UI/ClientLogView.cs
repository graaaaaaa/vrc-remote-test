using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;

namespace VRCRemoteTest
{
    /// <summary>One record of a slot's markers file: "&lt;seq&gt;\t&lt;line&gt;", the line already escaped by the Bridge.</summary>
    internal sealed class MarkerRecord
    {
        public MarkerRecord(int slot, long seq, string text, DateTime? localTime)
        {
            Slot = slot;
            Seq = seq;
            Text = text;
            LocalTime = localTime;
        }

        public int Slot { get; }

        public long Seq { get; }

        public string Text { get; }

        /// <summary>The time in the line's header (the Windows machine's local time), or null.</summary>
        public DateTime? LocalTime { get; }
    }

    /// <summary>The first line of a markers file, after "# ".</summary>
    internal sealed class MarkerFileHeader
    {
        [JsonProperty("slot")]
        public int Slot { get; set; }

        [JsonProperty("clientGeneration")]
        public long ClientGeneration { get; set; }

        [JsonProperty("observationEpoch")]
        public long ObservationEpoch { get; set; }

        [JsonProperty("catchingUp")]
        public bool CatchingUp { get; set; }

        [JsonProperty("droppedRecords")]
        public long DroppedRecords { get; set; }
    }

    internal sealed class MarkerFile
    {
        public MarkerFile(MarkerFileHeader header, IReadOnlyList<MarkerRecord> records)
        {
            Header = header;
            Records = records;
        }

        /// <summary>Null when the first line is not a header this package knows.</summary>
        public MarkerFileHeader Header { get; }

        public IReadOnlyList<MarkerRecord> Records { get; }
    }

    /// <summary>
    /// The Log Viewer's per-slot views (client-observation plan F8), kept out of IMGUI for
    /// the tests: which sources to offer, the markers files (logs/clients/slot-{n}.markers.log)
    /// and their merge across slots, and a one-line summary of a client from
    /// status/vrchat-clients.json. Nothing here is trusted beyond display.
    /// </summary>
    internal static class ClientLogView
    {
        /// <summary>The Bridge's clients list counts as current for this long (its writes are 5 s apart).</summary>
        internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

        private const string HeaderPrefix = "# ";
        private const string TimeFormat = "yyyy.MM.dd HH:mm:ss";

        /// <summary>The slots that have had a client, ascending; empty without a clients list (Bridge 0.5.x).</summary>
        internal static int[] Slots(VrchatClients clients) =>
            clients?.Clients == null
                ? Array.Empty<int>()
                : clients.Clients
                    .Where(c => c != null && c.Slot >= 0 && c.Slot < LanRemoteTransport.SlotCount)
                    .Select(c => c.Slot)
                    .Distinct()
                    .OrderBy(s => s)
                    .ToArray();

        /// <summary>
        /// The source toolbar: the compatible copy (or, for markers, all slots merged)
        /// first, then one entry per slot.
        /// </summary>
        internal static string[] SourceLabels(int[] slots, bool markers) =>
            new[] { markers ? "All slots" : "Latest" }
                .Concat(slots.Select(s => $"Slot {s}"))
                .ToArray();

        internal static MarkerFile ParseMarkers(int slot, string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return new MarkerFile(null, Array.Empty<MarkerRecord>());
            }

            var lines = content.Split('\n');
            MarkerFileHeader header = null;
            var start = 0;
            if (lines[0].StartsWith(HeaderPrefix, StringComparison.Ordinal))
            {
                start = 1;
                try
                {
                    header = JsonConvert.DeserializeObject<MarkerFileHeader>(
                        lines[0].Substring(HeaderPrefix.Length),
                        new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None, MaxDepth = 4 });
                }
                catch (JsonException)
                {
                    header = null;
                }
            }

            var records = new List<MarkerRecord>();
            for (var i = start; i < lines.Length; i++)
            {
                var line = lines[i];
                var tab = line.IndexOf('\t');
                if (tab <= 0 || !long.TryParse(line.Substring(0, tab), NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
                {
                    continue;
                }

                var text = line.Substring(tab + 1);
                records.Add(new MarkerRecord(slot, seq, text, ParseTime(text)));
            }

            return new MarkerFile(header, records);
        }

        /// <summary>
        /// All slots' records by the time in their header, then slot, then seq. A record
        /// without a readable time keeps the time of the record before it in its slot, so
        /// each slot's own order is kept.
        /// </summary>
        internal static MarkerRecord[] Merge(IEnumerable<IReadOnlyList<MarkerRecord>> perSlot)
        {
            var keyed = new List<(DateTime Time, MarkerRecord Record)>();
            foreach (var records in perSlot)
            {
                var last = DateTime.MinValue;
                foreach (var record in records.OrderBy(r => r.Seq))
                {
                    last = record.LocalTime ?? last;
                    keyed.Add((last, record));
                }
            }

            return keyed
                .OrderBy(k => k.Time)
                .ThenBy(k => k.Record.Slot)
                .ThenBy(k => k.Record.Seq)
                .Select(k => k.Record)
                .ToArray();
        }

        internal static string FormatRecord(MarkerRecord record, bool withSlot) =>
            (withSlot ? $"[slot {record.Slot}] " : string.Empty) + $"{record.Seq}  {record.Text}";

        /// <summary>"PID 1234 · joined · local:dab8e4… · 2 players · master · Logging full", for the slot's line.</summary>
        internal static string Describe(VrchatClientEntry client)
        {
            if (client == null)
            {
                return "No client in this slot.";
            }

            var parts = new List<string> { $"PID {client.ProcessId}" };
            if (client.Profile.HasValue)
            {
                parts.Add($"profile {client.Profile.Value}");
            }

            parts.Add(client.Running == false ? "not running" : client.State ?? "state unknown");
            if (!string.IsNullOrEmpty(client.Instance))
            {
                parts.Add(ShortInstance(client.Instance) + (client.InErrorWorld == true ? " (Error World)" : string.Empty));
            }

            if (client.PlayersInInstance != null)
            {
                parts.Add(client.PlayersInInstance.Count == 1 ? "1 player" : $"{client.PlayersInInstance.Count} players");
            }

            if (client.IsMaster == true)
            {
                parts.Add("master");
            }

            parts.Add($"Logging {client.LoggingLevel ?? "unknown"}");
            if (client.CatchingUp)
            {
                parts.Add("catching up");
            }

            return string.Join(" · ", parts);
        }

        internal static bool IsStale(VrchatClients clients, DateTimeOffset now) =>
            clients == null || now - clients.UpdatedAtUtc > StaleAfter;

        /// <summary>local:dab8e405… : enough to tell instances apart at a glance.</summary>
        internal static string ShortInstance(string instance)
        {
            const string local = "local:";
            return instance.StartsWith(local, StringComparison.Ordinal) && instance.Length > local.Length + 8
                ? instance.Substring(0, local.Length + 8) + "…"
                : instance;
        }

        private static DateTime? ParseTime(string text) =>
            text.Length >= TimeFormat.Length
            && DateTime.TryParseExact(
                text.Substring(0, TimeFormat.Length), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                ? time
                : (DateTime?)null;
    }
}
