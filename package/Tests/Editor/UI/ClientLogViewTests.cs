using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    /// <summary>Client-observation plan F8: the Log Viewer's slot and Markers views, without IMGUI.</summary>
    public class ClientLogViewTests
    {
        private const string Header = "2026.10.18 13:41:41 Debug      -  ";

        private static VrchatClients Clients(params int[] slots) => new VrchatClients
        {
            Clients = slots.Select(s => new VrchatClientEntry { Slot = s, ProcessId = 100 + s }).ToList(),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        [Test]
        public void Without_a_clients_list_there_are_no_slots_to_offer()
        {
            Assert.IsEmpty(ClientLogView.Slots(null));
            Assert.IsEmpty(ClientLogView.Slots(new VrchatClients()));
        }

        [Test]
        public void Sources_are_the_compatible_copy_then_each_slot()
        {
            var slots = ClientLogView.Slots(Clients(2, 0, 7));

            CollectionAssert.AreEqual(new[] { 0, 2 }, slots, "slots outside 0-3 are ignored");
            CollectionAssert.AreEqual(new[] { "Latest", "Slot 0", "Slot 2" }, ClientLogView.SourceLabels(slots, markers: false));
            CollectionAssert.AreEqual(new[] { "All slots", "Slot 0", "Slot 2" }, ClientLogView.SourceLabels(slots, markers: true));
        }

        [Test]
        public void Parses_a_markers_file()
        {
            var file = ClientLogView.ParseMarkers(
                1,
                "# {\"schemaVersion\":1,\"slot\":1,\"clientGeneration\":7,\"observationEpoch\":3,\"catchingUp\":true,\"droppedRecords\":2}\n" +
                "12\t" + Header + "[SPK003] PLAYER_LEFT id=1\n" +
                "not a record\n" +
                "15\t" + Header + @"[SPK003] multi\nline" + "\n");

            Assert.AreEqual(3, file.Header.ObservationEpoch);
            Assert.IsTrue(file.Header.CatchingUp);
            Assert.AreEqual(2, file.Header.DroppedRecords);
            CollectionAssert.AreEqual(new[] { 12L, 15L }, file.Records.Select(r => r.Seq));
            Assert.AreEqual(new DateTime(2026, 10, 18, 13, 41, 41), file.Records[0].LocalTime);
            Assert.AreEqual(Header + @"[SPK003] multi\nline", file.Records[1].Text, "escapes stay as the Bridge wrote them");
        }

        [Test]
        public void An_empty_or_missing_markers_file_has_no_records()
        {
            Assert.IsEmpty(ClientLogView.ParseMarkers(0, null).Records);
            Assert.IsNull(ClientLogView.ParseMarkers(0, string.Empty).Header);
        }

        [Test]
        public void Merges_slots_by_time_then_slot_then_seq()
        {
            var slot0 = ClientLogView.ParseMarkers(0,
                "# {}\n" +
                "5\t2026.10.18 13:41:40 Debug      -  [SPK003] a\n" +
                "9\t2026.10.18 13:41:42 Debug      -  [SPK003] c\n").Records;
            var slot1 = ClientLogView.ParseMarkers(1,
                "# {}\n" +
                "3\t2026.10.18 13:41:42 Debug      -  [SPK003] b\n" +
                "4\tno time on this one\n").Records;

            var merged = ClientLogView.Merge(new List<IReadOnlyList<MarkerRecord>> { slot1, slot0 });

            CollectionAssert.AreEqual(
                new[] { "0:5", "0:9", "1:3", "1:4" },
                merged.Select(r => $"{r.Slot}:{r.Seq}"));
            Assert.AreEqual("[slot 1] 4  no time on this one", ClientLogView.FormatRecord(merged[3], withSlot: true));
        }

        [Test]
        public void Describes_a_client_in_one_line()
        {
            var client = new VrchatClientEntry
            {
                ProcessId = 1234,
                Profile = 1,
                Running = true,
                State = "joined",
                Instance = "local:dab8e405667a4de729f91623db09e31f57071efe70ea4b1ee764bd3563dd5383",
                PlayersInInstance = new List<string> { "A", "B" },
                IsMaster = true,
                LoggingLevel = "full",
            };

            Assert.AreEqual(
                "PID 1234 · profile 1 · joined · local:dab8e405… · 2 players · master · Logging full",
                ClientLogView.Describe(client));
            Assert.AreEqual("PID 5 · not running · Logging unknown", ClientLogView.Describe(new VrchatClientEntry { ProcessId = 5, Running = false }));
        }

        [Test]
        public void A_clients_list_older_than_15_seconds_is_stale()
        {
            var now = DateTimeOffset.UtcNow;

            Assert.IsFalse(ClientLogView.IsStale(new VrchatClients { UpdatedAtUtc = now.AddSeconds(-14) }, now));
            Assert.IsTrue(ClientLogView.IsStale(new VrchatClients { UpdatedAtUtc = now.AddSeconds(-16) }, now));
            Assert.IsTrue(ClientLogView.IsStale(null, now));
        }
    }
}
