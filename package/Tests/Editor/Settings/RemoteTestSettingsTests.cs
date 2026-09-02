using NUnit.Framework;
using UnityEditor;

namespace VRCRemoteTest.Tests
{
    public class RemoteTestSettingsTests
    {
        private const string SharePathKey = "VRCRemoteTest_SharePath";
        private const string TimeoutKey = "VRCRemoteTest_ResultTimeoutSeconds";
        private const string IntervalKey = "VRCRemoteTest_PollIntervalSeconds";
        private const string LogAutoRefreshKey = "VRCRemoteTest_LogAutoRefresh";
        private const string MoonlightApplicationNameKey = "VRCRemoteTest_MoonlightApplicationName";
        private const string FocusMoonlightAfterDeployKey = "VRCRemoteTest_FocusMoonlightAfterDeploy";

        private string _originalSharePath;
        private bool _hadSharePath;
        private int _originalTimeout;
        private int _originalInterval;
        private bool _originalLogAutoRefresh;
        private string _originalMoonlightApplicationName;
        private bool _originalFocusMoonlightAfterDeploy;

        [SetUp]
        public void SetUp()
        {
            _hadSharePath = EditorPrefs.HasKey(SharePathKey);
            _originalSharePath = EditorPrefs.GetString(SharePathKey, string.Empty);
            _originalTimeout = EditorPrefs.GetInt(TimeoutKey, 60);
            _originalInterval = EditorPrefs.GetInt(IntervalKey, 2);
            _originalLogAutoRefresh = EditorPrefs.GetBool(LogAutoRefreshKey, true);
            _originalMoonlightApplicationName = EditorPrefs.GetString(MoonlightApplicationNameKey, "Moonlight");
            _originalFocusMoonlightAfterDeploy = EditorPrefs.GetBool(FocusMoonlightAfterDeployKey, false);
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
            EditorPrefs.SetBool(LogAutoRefreshKey, _originalLogAutoRefresh);
            EditorPrefs.SetString(MoonlightApplicationNameKey, _originalMoonlightApplicationName);
            EditorPrefs.SetBool(FocusMoonlightAfterDeployKey, _originalFocusMoonlightAfterDeploy);
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

        [Test]
        public void LogAutoRefresh_defaults_to_true()
        {
            EditorPrefs.DeleteKey(LogAutoRefreshKey);

            Assert.IsTrue(RemoteTestSettings.LogAutoRefresh);
        }

        [Test]
        public void LogAutoRefresh_round_trips()
        {
            RemoteTestSettings.LogAutoRefresh = false;
            Assert.IsFalse(RemoteTestSettings.LogAutoRefresh);

            RemoteTestSettings.LogAutoRefresh = true;
            Assert.IsTrue(RemoteTestSettings.LogAutoRefresh);
        }

        [Test]
        public void MoonlightApplicationName_defaults_to_Moonlight()
        {
            EditorPrefs.DeleteKey(MoonlightApplicationNameKey);

            Assert.AreEqual("Moonlight", RemoteTestSettings.MoonlightApplicationName);
        }

        [Test]
        public void MoonlightApplicationName_round_trips_valid_value()
        {
            RemoteTestSettings.MoonlightApplicationName = "My Moonlight";

            Assert.AreEqual("My Moonlight", RemoteTestSettings.MoonlightApplicationName);
        }

        [Test]
        public void MoonlightApplicationName_rejects_invalid_value_and_keeps_previous()
        {
            RemoteTestSettings.MoonlightApplicationName = "GoodName";

            RemoteTestSettings.MoonlightApplicationName = "bad/name";

            Assert.AreEqual("GoodName", RemoteTestSettings.MoonlightApplicationName);
        }

        [Test]
        public void MoonlightApplicationName_getter_falls_back_when_stale_value_is_whitespace()
        {
            // Simulates a value written before validation existed, or set
            // programmatically outside RemoteTestSettings's setter.
            EditorPrefs.SetString(MoonlightApplicationNameKey, "   ");

            Assert.AreEqual("Moonlight", RemoteTestSettings.MoonlightApplicationName);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("has/slash")]
        public void IsValidMoonlightApplicationName_rejects_invalid_input(string value)
        {
            Assert.IsFalse(RemoteTestSettings.IsValidMoonlightApplicationName(value));
        }

        [Test]
        public void IsValidMoonlightApplicationName_rejects_control_characters()
        {
            Assert.IsFalse(RemoteTestSettings.IsValidMoonlightApplicationName("bad\tname"));
        }

        [Test]
        public void IsValidMoonlightApplicationName_rejects_names_longer_than_255_characters()
        {
            var tooLong = new string('a', 256);

            Assert.IsFalse(RemoteTestSettings.IsValidMoonlightApplicationName(tooLong));
        }

        [Test]
        public void IsValidMoonlightApplicationName_accepts_reasonable_name()
        {
            Assert.IsTrue(RemoteTestSettings.IsValidMoonlightApplicationName("Moonlight"));
        }

        [Test]
        public void FocusMoonlightAfterDeploy_defaults_to_false()
        {
            EditorPrefs.DeleteKey(FocusMoonlightAfterDeployKey);

            Assert.IsFalse(RemoteTestSettings.FocusMoonlightAfterDeploy);
        }

        [Test]
        public void FocusMoonlightAfterDeploy_round_trips()
        {
            RemoteTestSettings.FocusMoonlightAfterDeploy = true;
            Assert.IsTrue(RemoteTestSettings.FocusMoonlightAfterDeploy);

            RemoteTestSettings.FocusMoonlightAfterDeploy = false;
            Assert.IsFalse(RemoteTestSettings.FocusMoonlightAfterDeploy);
        }
    }
}
