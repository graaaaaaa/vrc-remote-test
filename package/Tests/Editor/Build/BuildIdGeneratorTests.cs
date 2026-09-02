using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    public class BuildIdGeneratorTests
    {
        private static readonly Regex ExpectedFormat = new Regex(@"^\d{8}T\d{9}Z-[0-9a-f]{8}$");

        [Test]
        public void BuildId_format_matches_spec()
        {
            var buildId = BuildIdGenerator.Generate();
            Assert.IsTrue(ExpectedFormat.IsMatch(buildId), $"Unexpected format: {buildId}");
        }

        [Test]
        public void BuildIds_are_unique()
        {
            var seen = new HashSet<string>();
            for (var i = 0; i < 1000; i++)
            {
                Assert.IsTrue(seen.Add(BuildIdGenerator.Generate()), "Duplicate buildId generated.");
            }
        }

        [Test]
        public void Generated_buildId_passes_IsValid()
        {
            Assert.IsTrue(BuildIdGenerator.IsValid(BuildIdGenerator.Generate()));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not-a-build-id")]
        [TestCase("20260901T112522481Z-a91f02cc/../etc")]
        [TestCase("20260901T112522481Z-GGGGGGGG")]
        public void IsValid_rejects_malformed_input(string candidate)
        {
            Assert.IsFalse(BuildIdGenerator.IsValid(candidate));
        }

        [Test]
        public void IsValid_accepts_golden_fixture_buildId()
        {
            Assert.IsTrue(BuildIdGenerator.IsValid("20260901T112522481Z-a91f02cc"));
        }
    }
}
