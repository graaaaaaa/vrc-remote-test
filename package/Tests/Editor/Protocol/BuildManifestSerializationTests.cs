using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    /// <summary>
    /// Golden-fixture tests guarding against protocol drift between Unity's
    /// Newtonsoft.Json model and the Bridge's System.Text.Json model. Both
    /// sides independently deserialize/serialize the exact same fixture files
    /// (spec-precedent: Phase 1 did this for the Bridge; this is the Unity
    /// counterpart).
    /// </summary>
    public class BuildManifestSerializationTests
    {
        [Test]
        public void Deserializes_golden_fixture_manifest()
        {
            var json = TestFixtures.Read("sample-manifest.json");
            var manifest = JsonConvert.DeserializeObject<BuildManifest>(json);

            Assert.AreEqual(1, manifest.ProtocolVersion);
            Assert.AreEqual("20260901T112522481Z-a91f02cc", manifest.BuildId);
            Assert.AreEqual("20260901T112522481Z-a91f02cc.vrcw", manifest.FileName);
            Assert.AreEqual(48233421L, manifest.Size);
            Assert.AreEqual(
                "8f2a5f6802f9dc5307740870312b2df9a99104960df3a9df66166d54665d4d7c", manifest.Sha256);
            Assert.AreEqual(
                DateTimeOffset.Parse("2026-09-01T11:25:22.481Z"), manifest.CreatedAtUtc);
        }

        [Test]
        public void Serializes_manifest_to_structurally_match_golden_fixture()
        {
            var manifest = new BuildManifest
            {
                ProtocolVersion = 1,
                BuildId = "20260901T112522481Z-a91f02cc",
                FileName = "20260901T112522481Z-a91f02cc.vrcw",
                Size = 48233421,
                Sha256 = "8f2a5f6802f9dc5307740870312b2df9a99104960df3a9df66166d54665d4d7c",
                CreatedAtUtc = DateTimeOffset.Parse("2026-09-01T11:25:22.481Z"),
            };

            var producedJson = JsonConvert.SerializeObject(manifest);
            // DateParseHandling.None is required: JObject.Parse's default
            // behavior auto-converts ISO-8601-looking strings into DateTime
            // tokens, and "Z"-suffixed vs "+00:00"-suffixed strings then get
            // silently re-stringified with different (and here, WRONG) time
            // zone handling on (string) cast — turning two representations of
            // the same instant into two different wall-clock times. Reading
            // with DateParseHandling.None keeps these as plain strings.
            var produced = ParseWithoutDateConversion(producedJson);
            var golden = ParseWithoutDateConversion(TestFixtures.Read("sample-manifest.json"));

            // Compare field-by-field (not raw string equality) since
            // Newtonsoft.Json's default DateTimeOffset format can differ in
            // punctuation from System.Text.Json's while representing the same
            // instant.
            Assert.AreEqual((int)golden["protocolVersion"], (int)produced["protocolVersion"]);
            Assert.AreEqual((string)golden["buildId"], (string)produced["buildId"]);
            Assert.AreEqual((string)golden["fileName"], (string)produced["fileName"]);
            Assert.AreEqual((long)golden["size"], (long)produced["size"]);
            Assert.AreEqual((string)golden["sha256"], (string)produced["sha256"]);
            Assert.AreEqual(
                DateTimeOffset.Parse((string)golden["createdAtUtc"]),
                DateTimeOffset.Parse((string)produced["createdAtUtc"]));
        }

        private static JObject ParseWithoutDateConversion(string json)
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(reader);
        }

        [Test]
        public void Deserializes_golden_fixture_result()
        {
            var json = TestFixtures.Read("sample-result.json");
            var result = JsonConvert.DeserializeObject<BuildResult>(json);

            Assert.AreEqual(1, result.ProtocolVersion);
            Assert.AreEqual("20260901T112522481Z-a91f02cc", result.BuildId);
            Assert.AreEqual("deployed", result.Status);
            Assert.AreEqual("vrc-remote-20260901T112522481Z-a91f02cc.vrcw", result.DeployedFileName);
            Assert.AreEqual(
                "8f2a5f6802f9dc5307740870312b2df9a99104960df3a9df66166d54665d4d7c", result.Sha256);
            Assert.IsNull(result.ErrorCode);
            Assert.IsNull(result.ErrorMessage);
        }

        [Test]
        public void Result_roundtrips_through_serialize_and_deserialize()
        {
            var original = new BuildResult
            {
                ProtocolVersion = 1,
                BuildId = "20260901T112522481Z-a91f02cc",
                Status = "deployed",
                DeployedFileName = "vrc-remote-20260901T112522481Z-a91f02cc.vrcw",
                Sha256 = "8f2a5f6802f9dc5307740870312b2df9a99104960df3a9df66166d54665d4d7c",
                ErrorCode = null,
                ErrorMessage = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            var json = JsonConvert.SerializeObject(original);
            var roundtripped = JsonConvert.DeserializeObject<BuildResult>(json);

            Assert.AreEqual(original.ProtocolVersion, roundtripped.ProtocolVersion);
            Assert.AreEqual(original.BuildId, roundtripped.BuildId);
            Assert.AreEqual(original.Status, roundtripped.Status);
            Assert.AreEqual(original.DeployedFileName, roundtripped.DeployedFileName);
            Assert.AreEqual(original.Sha256, roundtripped.Sha256);
            Assert.IsNull(roundtripped.ErrorCode);
            Assert.IsNull(roundtripped.ErrorMessage);
        }

        [Test]
        public void Failed_result_has_null_deployed_fields()
        {
            var failed = new BuildResult
            {
                ProtocolVersion = 1,
                BuildId = "20260901T112522481Z-a91f02cc",
                Status = "failed",
                DeployedFileName = null,
                Sha256 = null,
                ErrorCode = "HASH_MISMATCH",
                ErrorMessage = "Computed hash did not match manifest.",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            var json = JsonConvert.SerializeObject(failed);
            var roundtripped = JsonConvert.DeserializeObject<BuildResult>(json);

            Assert.AreEqual("failed", roundtripped.Status);
            Assert.IsNull(roundtripped.DeployedFileName);
            Assert.IsNull(roundtripped.Sha256);
            Assert.AreEqual("HASH_MISMATCH", roundtripped.ErrorCode);
        }
    }
}
