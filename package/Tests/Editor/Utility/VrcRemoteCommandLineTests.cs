using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    public class VrcRemoteCommandLineTests
    {
        [Test]
        public void TryGetFlagValue_returns_value_when_present()
        {
            var args = new[] { "Unity.exe", "-vrcRemoteTestSharePath", "/Volumes/VRCRemoteTest", "-batchmode" };

            var found = VrcRemoteCommandLine.TryGetFlagValue(
                args, "-vrcRemoteTestSharePath", out var value, out var error);

            Assert.IsTrue(found);
            Assert.AreEqual("/Volumes/VRCRemoteTest", value);
            Assert.IsNull(error);
        }

        [Test]
        public void TryGetFlagValue_returns_false_when_flag_absent()
        {
            var args = new[] { "Unity.exe", "-batchmode" };

            var found = VrcRemoteCommandLine.TryGetFlagValue(
                args, "-vrcRemoteTestSharePath", out var value, out var error);

            Assert.IsFalse(found);
            Assert.IsNull(value);
            Assert.IsNull(error);
        }

        [Test]
        public void TryGetFlagValue_errors_when_value_missing()
        {
            var args = new[] { "Unity.exe", "-vrcRemoteTestSharePath" };

            var found = VrcRemoteCommandLine.TryGetFlagValue(
                args, "-vrcRemoteTestSharePath", out var value, out var error);

            Assert.IsFalse(found);
            Assert.IsNotNull(error);
        }

        [Test]
        public void TryGetFlagValue_errors_on_duplicate_flag()
        {
            var args = new[]
            {
                "Unity.exe", "-vrcRemoteTestSharePath", "/first", "-vrcRemoteTestSharePath", "/second",
            };

            var found = VrcRemoteCommandLine.TryGetFlagValue(
                args, "-vrcRemoteTestSharePath", out var value, out var error);

            Assert.IsFalse(found);
            Assert.IsNotNull(error);
        }

        [Test]
        public void TryGetFlagValue_does_not_match_unity_reserved_flags()
        {
            var args = new[] { "Unity.exe", "-batchmode", "-quit" };

            var found = VrcRemoteCommandLine.TryGetFlagValue(
                args, "-vrcRemoteTestSharePath", out _, out var error);

            Assert.IsFalse(found);
            Assert.IsNull(error);
        }
    }
}
