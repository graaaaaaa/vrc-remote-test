using System;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace VRCRemoteTest
{
    /// <summary>
    /// Interactive front-end for the Build layer (spec section 60). Never
    /// touches filesystem/network directly (spec section 53) — everything
    /// goes through RemoteBuildCommand.GetCoordinator()'s public API so the
    /// window shares the same coordinator (and therefore the same
    /// single-flight concurrency guard) as the "VRChat SDK/Remote Build"
    /// menu item.
    /// </summary>
    public sealed class RemoteTestWindow : EditorWindow
    {
        // 2.5s TTL cache + event-driven refresh (OnEnable, settings commit,
        // before/after build) rather than the originally-proposed 5s pure
        // debounce — IsShareReachable is a cheap local-filesystem check on a
        // mounted SMB share, and stale status risks the user starting a build
        // that immediately fails preflight (Codex plan review Phase 3, Round
        // 2, confidence 0.88).
        private const float PreflightCheckIntervalSeconds = 2.5f;

        private bool _shareConfigured;
        private bool _shareReachable;
        private bool _sdkAvailable;
        private bool _hasLastArtifact;
        private double _lastPreflightCheckTime = double.NegativeInfinity;

        private RemoteBuildStatus _currentStatus = RemoteBuildStatus.Idle;
        private string _currentMessage = string.Empty;
        private RemoteBuildOutcome _lastOutcome;
        private DateTime _lastOutcomeTime;

        private bool _settingsFoldout;
        private string _sharePathInput = string.Empty;
        private CancellationTokenSource _cts;

        [MenuItem("VRChat SDK/VRC Remote Test", false, 900)]
        public static void ShowWindow()
        {
            var window = GetWindow<RemoteTestWindow>();
            window.titleContent = new GUIContent("VRC Remote Test");
            window.minSize = new Vector2(360, 320);
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("VRC Remote Test");
            minSize = new Vector2(360, 320);
            LoadSettingsIntoFields();
            RefreshPreflightStatus();
        }

        private void OnDisable()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        private void OnGUI()
        {
            if (EditorApplication.timeSinceStartup - _lastPreflightCheckTime >= PreflightCheckIntervalSeconds)
            {
                RefreshPreflightStatus();
            }

            DrawPreflightSection();
            EditorGUILayout.Space();
            DrawBuildTargetSection();
            EditorGUILayout.Space();
            DrawActionButtons();
            EditorGUILayout.Space();
            DrawProgressSection();
            EditorGUILayout.Space();
            DrawLastDeploymentSection();
            EditorGUILayout.Space();
            DrawSettingsFoldout();
        }

        private void DrawPreflightSection()
        {
            EditorGUILayout.LabelField("Preflight", EditorStyles.boldLabel);

            if (!_shareConfigured)
            {
                EditorGUILayout.HelpBox(
                    "Remote share is not configured. Set SharePath below.", MessageType.Warning);
            }
            else
            {
                // "Remote Share: Reachable" rather than "Windows Bridge Online" —
                // this only confirms the SMB mount is present and writable, not
                // that the Bridge process is alive. Phase 1 deliberately has no
                // heartbeat, so claiming to detect the Bridge process would be
                // misleading (Codex plan review Phase 3 plan, design decision).
                DrawStatusLine("Remote Share", _shareReachable, "Reachable", "Not reachable");
            }

            DrawStatusLine("VRChat SDK", _sdkAvailable, "Available", "Not available");
        }

        private void DrawBuildTargetSection()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            var isWindows = target == BuildTarget.StandaloneWindows64;

            EditorGUILayout.LabelField("Build Target", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(target.ToString());

            if (!isWindows)
            {
                EditorGUILayout.HelpBox(
                    "Active build target is not StandaloneWindows64. Switch it via " +
                    "File > Build Settings before running a remote build.",
                    MessageType.Warning);
            }
        }

        private void DrawProgressSection()
        {
            if (_currentStatus != RemoteBuildStatus.Running)
            {
                return;
            }

            EditorGUILayout.LabelField("Progress", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(_currentMessage);

            if (GUILayout.Button("Cancel"))
            {
                _cts?.Cancel();
            }
        }

        private void DrawLastDeploymentSection()
        {
            if (_lastOutcome == null)
            {
                return;
            }

            EditorGUILayout.LabelField("Last Deployment", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("When", _lastOutcomeTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));

            if (_lastOutcome.Succeeded)
            {
                EditorGUILayout.HelpBox(
                    $"Deployed as {_lastOutcome.Result?.DeployedFileName}", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"{_lastOutcome.ErrorCode}: {_lastOutcome.ErrorMessage}", MessageType.Error);
            }
        }

        private void DrawActionButtons()
        {
            var running = RemoteBuildCommand.GetCoordinator().IsRunning;

            using (new EditorGUI.DisabledScope(running || !_shareConfigured))
            {
                if (GUILayout.Button("Remote Build & Test"))
                {
                    StartRemoteBuild();
                }
            }

            using (new EditorGUI.DisabledScope(running || !_shareConfigured || !_hasLastArtifact))
            {
                if (GUILayout.Button("Deploy Last Build"))
                {
                    StartDeployLastBuild();
                }
            }

            if (!_hasLastArtifact)
            {
                EditorGUILayout.HelpBox(
                    "Deploy Last Build is available after a successful Remote Build & Test " +
                    "in this Editor session (not preserved across a domain reload).",
                    MessageType.None);
            }
        }

        private void DrawSettingsFoldout()
        {
            _settingsFoldout = EditorGUILayout.Foldout(_settingsFoldout, "Settings");
            if (!_settingsFoldout)
            {
                return;
            }

            // Disabled while a build/deploy is running: changing SharePath
            // mid-flight would invalidate the coordinator's transport out from
            // under the in-flight operation (see RemoteBuildCommand.InvalidateCoordinator).
            using (new EditorGUI.DisabledScope(RemoteBuildCommand.GetCoordinator().IsRunning))
            {
                EditorGUI.BeginChangeCheck();
                // DelayedTextField (not TextField): commits on Enter/focus-loss
                // only, so we don't write EditorPrefs and invalidate the
                // coordinator on every keystroke.
                var newSharePath = EditorGUILayout.DelayedTextField("Share Path", _sharePathInput);
                if (EditorGUI.EndChangeCheck())
                {
                    _sharePathInput = newSharePath;
                    RemoteTestSettings.SharePath = newSharePath;
                    if (!RemoteBuildCommand.InvalidateCoordinator())
                    {
                        Debug.LogWarning(
                            "[VRC Remote Test] SharePath changed but a build is in progress; " +
                            "the new path will take effect after it finishes.");
                    }

                    RefreshPreflightStatus();
                }

                var newTimeout = EditorGUILayout.IntField(
                    "Result Timeout (s)", RemoteTestSettings.ResultTimeoutSeconds);
                if (newTimeout != RemoteTestSettings.ResultTimeoutSeconds)
                {
                    RemoteTestSettings.ResultTimeoutSeconds = newTimeout;
                }

                var newInterval = EditorGUILayout.IntField(
                    "Poll Interval (s)", RemoteTestSettings.PollIntervalSeconds);
                if (newInterval != RemoteTestSettings.PollIntervalSeconds)
                {
                    RemoteTestSettings.PollIntervalSeconds = newInterval;
                }
            }
        }

        private async void StartRemoteBuild()
        {
            _cts = new CancellationTokenSource();
            var progress = new Progress<RemoteBuildProgress>(OnProgressReport);

            try
            {
                var outcome = await RemoteBuildCommand.GetCoordinator()
                    .ExecuteRemoteBuildAsync(progress, _cts.Token);
                RecordOutcome(outcome);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VRC Remote Test] {ex}");
            }
            finally
            {
                RefreshPreflightStatus();
                Repaint();
            }
        }

        private async void StartDeployLastBuild()
        {
            _cts = new CancellationTokenSource();
            var progress = new Progress<RemoteBuildProgress>(OnProgressReport);

            try
            {
                var outcome = await RemoteBuildCommand.GetCoordinator()
                    .DeployLastBuildAsync(progress, _cts.Token);
                RecordOutcome(outcome);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VRC Remote Test] {ex}");
            }
            finally
            {
                RefreshPreflightStatus();
                Repaint();
            }
        }

        private void OnProgressReport(RemoteBuildProgress p)
        {
            _currentStatus = p.Status;
            _currentMessage = p.Message;
            Repaint();
        }

        private void RecordOutcome(RemoteBuildOutcome outcome)
        {
            _lastOutcome = outcome;
            _lastOutcomeTime = DateTime.UtcNow;
            _currentStatus = outcome.Succeeded ? RemoteBuildStatus.Succeeded : RemoteBuildStatus.Failed;
        }

        private void LoadSettingsIntoFields()
        {
            _sharePathInput = RemoteTestSettings.TryGetSharePath(out var path) ? path : string.Empty;
        }

        private void RefreshPreflightStatus()
        {
            _lastPreflightCheckTime = EditorApplication.timeSinceStartup;

            _shareConfigured = RemoteTestSettings.TryGetSharePath(out _);
            if (!_shareConfigured)
            {
                _shareReachable = false;
                _sdkAvailable = false;
                _hasLastArtifact = false;
                return;
            }

            try
            {
                var coordinator = RemoteBuildCommand.GetCoordinator();
                _shareReachable = coordinator.IsShareReachable;
                _sdkAvailable = coordinator.IsSdkAvailable;
                _hasLastArtifact = coordinator.LastArtifact != null;
            }
            catch (RemoteBuildException)
            {
                _shareReachable = false;
                _sdkAvailable = false;
                _hasLastArtifact = false;
            }
        }

        private static void DrawStatusLine(string label, bool ok, string okText, string notOkText)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(100));
                var previousColor = GUI.color;
                GUI.color = ok ? Color.green : Color.red;
                EditorGUILayout.LabelField(ok ? okText : notOkText);
                GUI.color = previousColor;
            }
        }
    }
}
