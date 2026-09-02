using NUnit.Framework;
using UnityEditor;

namespace VRCRemoteTest.Tests
{
    public class RemoteTestSettingsTests
    {
        private const string SharePathKey = "VRCRemoteTest_SharePath";
        private const string TimeoutKey = "VRCRemoteTest_ResultTimeoutSeconds";
        private const string IntervalKey = "VRCRemoteTest_PollIntervalSeconds";

        private string _originalSharePath;
        private bool _hadSharePath;
        private int _originalTimeout;
        private int _originalInterval;

        [SetUp]
        public void SetUp()
        {
            _hadSharePath = EditorPrefs.HasKey(SharePathKey);
            _originalSharePath = EditorPrefs.GetString(SharePathKey, string.Empty);
            _originalTimeout = EditorPrefs.GetInt(TimeoutKey, 60);
            _originalInterval = EditorPrefs.GetInt(IntervalKey, 2);
        }

        [TearDown]
        public void TearDown()
        {
            if (_hadSharePath)
            {
                EditorPrefs.SetString(SharePathKey, _originalSharePath);
            }
            else
            {
                EditorPrefs.DeleteKey(SharePathKey);
            }

            EditorPrefs.SetInt(TimeoutKey, _originalTimeout);
            EditorPrefs.SetInt(IntervalKey, _originalInterval);
        }

        [Test]
        public void TryGetSharePath_returns_true_when_configured()
        {
            EditorPrefs.SetString(SharePathKey, "/Volumes/VRCRemoteTest");

            var found = RemoteTestSettings.TryGetSharePath(out var path);

            Assert.IsTrue(found);
            Assert.AreEqual("/Volumes/VRCRemoteTest", path);
        }

        [Test]
        public void TryGetSharePath_returns_false_when_not_configured()
        {
            EditorPrefs.DeleteKey(SharePathKey);

            var found = RemoteTestSettings.TryGetSharePath(out var path);

            Assert.IsFalse(found);
            Assert.IsNull(path);
        }

        [Test]
        public void ResultTimeoutSeconds_clamps_zero_and_negative_to_minimum()
        {
            RemoteTestSettings.ResultTimeoutSeconds = 0;
            Assert.AreEqual(1, RemoteTestSettings.ResultTimeoutSeconds);

            RemoteTestSettings.ResultTimeoutSeconds = -1;
            Assert.AreEqual(1, RemoteTestSettings.ResultTimeoutSeconds);
        }

        [Test]
        public void ResultTimeoutSeconds_clamps_excessive_value_to_maximum()
        {
            RemoteTestSettings.ResultTimeoutSeconds = 3601;

            Assert.AreEqual(3600, RemoteTestSettings.ResultTimeoutSeconds);
        }

        [Test]
        public void PollIntervalSeconds_clamps_zero_and_negative_to_minimum()
        {
            RemoteTestSettings.PollIntervalSeconds = 0;
            Assert.AreEqual(1, RemoteTestSettings.PollIntervalSeconds);

            RemoteTestSettings.PollIntervalSeconds = -1;
            Assert.AreEqual(1, RemoteTestSettings.PollIntervalSeconds);
        }

        [Test]
        public void PollIntervalSeconds_clamps_excessive_value_to_maximum()
        {
            RemoteTestSettings.PollIntervalSeconds = 61;

            Assert.AreEqual(60, RemoteTestSettings.PollIntervalSeconds);
        }

        [Test]
        public void PollIntervalSeconds_clamps_stale_EditorPrefs_value_on_read()
        {
            // Simulates a value written before clamping existed, or written
            // programmatically outside RemoteTestSettings's setter.
            EditorPrefs.SetInt(IntervalKey, 0);

            Assert.AreEqual(1, RemoteTestSettings.PollIntervalSeconds);
        }
    }
}
