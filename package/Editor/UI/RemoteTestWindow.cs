using System;
using System.Linq;
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

        // Independent of the Bridge's own VrchatMonitorService poll interval
        // (10s) — a conservative fixed value on the Unity side so staleness
        // detection doesn't depend on Mac/Windows clock sync assumptions
        // beyond "not wildly skewed" (Codex plan review Phase 4a, Round 2,
        // confidence 0.91).
        private const float VrchatStatusStaleAfterSeconds = 30f;

        // Matches the Bridge's VrchatLogService poll interval (5s) so the
        // viewer doesn't Repaint() faster than new content can actually
        // arrive. Only ticks while the Log Viewer foldout is open and Auto
        // Refresh is enabled (checked inside OnEditorUpdate every tick,
        // rather than subscribing/unsubscribing on toggle — Codex plan
        // review Phase 5, Round 3, confidence 0.90).
        private const float LogRefreshIntervalSeconds = 5f;
        private const float LogViewportHeight = 200f;
        private const float LogTopThresholdPixels = 20f;

        private static readonly string[] LogCategories = { "All", "Error", "Exception", "Udon", "Shader", "Warning" };

        private bool _logFoldout;
        private string _logCategory = "All";
        private Vector2 _logScrollPosition;

        // Newest-first display (real hardware feedback: scrolling to see the
        // latest line was inconvenient) means "the user wants to see new
        // content without acting" now means "stay pinned to the top", not
        // the bottom. Default true so the very first render already sits at
        // the newest line.
        private bool _logPinnedToTop = true;
        private string _lastRenderedLogContent = string.Empty;
        private double _lastLogCheckTime = double.NegativeInfinity;

        private bool _shareConfigured;
        private bool _shareReachable;
        private bool _sdkAvailable;
        private bool _hasLastArtifact;
        private VrchatStatus _vrchatStatus;
        private double _lastPreflightCheckTime = double.NegativeInfinity;

        private RemoteBuildStatus _currentStatus = RemoteBuildStatus.Idle;
        private string _currentMessage = string.Empty;
        private RemoteBuildOutcome _lastOutcome;
        private DateTime _lastOutcomeTime;

        private bool _settingsFoldout;
        private string _sharePathInput = string.Empty;
        private CancellationTokenSource _cts;

        // Wraps the whole window body: when this window is docked into a
        // small tab area (rather than floating and freely resizable), its
        // total content height can exceed the dock's fixed height with no
        // other way to reach the rest (observed on real hardware — content
        // below "VRChat Log" got clipped with no scrollbar).
        private Vector2 _windowScrollPosition;

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
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        /// <summary>
        /// Explicit tick-based refresh for the Log Viewer's Auto Refresh, kept
        /// independent of the existing OnGUI-embedded preflight TTL check
        /// above: preflight status is advisory and stale-tolerant, but a log
        /// viewer a developer is actively watching needs a refresh even while
        /// the window isn't receiving input events (Codex plan review Phase
        /// 5, Round 2, confidence 0.90).
        /// </summary>
        private void OnEditorUpdate()
        {
            if (!_logFoldout || !RemoteTestSettings.LogAutoRefresh)
            {
                return;
            }

            if (EditorApplication.timeSinceStartup - _lastLogCheckTime < LogRefreshIntervalSeconds)
            {
                return;
            }

            _lastLogCheckTime = EditorApplication.timeSinceStartup;
            Repaint();
        }

        private void OnGUI()
        {
            if (EditorApplication.timeSinceStartup - _lastPreflightCheckTime >= PreflightCheckIntervalSeconds)
            {
                RefreshPreflightStatus();
            }

            _windowScrollPosition = EditorGUILayout.BeginScrollView(_windowScrollPosition);

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
            DrawLogViewerSection();
            EditorGUILayout.Space();
            DrawSettingsFoldout();

            EditorGUILayout.EndScrollView();
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

            DrawVrchatStatusLine();
        }

        /// <summary>
        /// Phase 4a: advisory only, never gates the build buttons (unlike Remote
        /// Share/VRChat SDK which do via DrawActionButtons' DisabledScope). A
        /// stale or missing status (Bridge not running Phase 4a's monitor, or an
        /// older Bridge build without it) reads as "Unknown", never green — the
        /// UI never trusts an old IsRunning=true (Codex plan review Phase 4a,
        /// Round 2, confidence 0.91).
        /// </summary>
        private void DrawVrchatStatusLine()
        {
            var fresh = _vrchatStatus != null
                && (DateTimeOffset.UtcNow - _vrchatStatus.UpdatedAtUtc) <= TimeSpan.FromSeconds(VrchatStatusStaleAfterSeconds);
            var running = fresh && _vrchatStatus.IsRunning;
            var text = !fresh ? "Unknown" : (_vrchatStatus.IsRunning ? "Running" : "Not running");

            DrawStatusLine("VRChat", running, text, text);

            if (fresh && _vrchatStatus.IsRunning && !_vrchatStatus.WatchWorldsDetected)
            {
                EditorGUILayout.HelpBox(
                    "VRChat is running without --watch-worlds — builds will deploy but won't auto-reload.",
                    MessageType.Warning);
            }
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

                var guidance = ErrorGuidance.GetGuidance(_lastOutcome.ErrorCode);
                if (guidance != null)
                {
                    EditorGUILayout.HelpBox(guidance, MessageType.Info);
                }
            }
        }

        /// <summary>
        /// Foldout (default closed), category toolbar filter, Auto Refresh
        /// toggle, and a scrollable read-only view of the Bridge's published
        /// vrchat-latest.log (spec section 47, "Unity log viewer"). Never
        /// wired into readiness detection — display only (Phase 5 plan,
        /// explicit non-goal).
        /// </summary>
        private void DrawLogViewerSection()
        {
            _logFoldout = EditorGUILayout.Foldout(_logFoldout, "VRChat Log");
            if (!_logFoldout)
            {
                return;
            }

            var rawLog = RemoteBuildCommand.GetCoordinator().VrchatLog;
            var lines = string.IsNullOrEmpty(rawLog) ? Array.Empty<string>() : rawLog.Split('\n');

            var selectedIndex = Array.IndexOf(LogCategories, _logCategory);
            var newSelectedIndex = GUILayout.Toolbar(Math.Max(0, selectedIndex), LogCategories);
            _logCategory = LogCategories[newSelectedIndex];

            var newAutoRefresh = EditorGUILayout.ToggleLeft("Auto Refresh", RemoteTestSettings.LogAutoRefresh);
            if (newAutoRefresh != RemoteTestSettings.LogAutoRefresh)
            {
                RemoteTestSettings.LogAutoRefresh = newAutoRefresh;
            }

            var filtered = FilterLogLines(lines, _logCategory);

            // Newest-first: the file itself is oldest-to-newest (VRChat
            // appends), so reverse for display only — real hardware feedback
            // was that scrolling to the bottom just to see the latest line
            // was inconvenient. Filtering/truncation upstream are unaffected.
            var displayText = filtered.Length == 0
                ? "(no log content yet)"
                : string.Join("\n", filtered.Reverse());

            // With newest-first order, "show new content without the user
            // having to act" means staying pinned to the TOP — only reset
            // there if the user was already at/near it, otherwise a Repaint
            // mid-review would yank them away from lines they're actively
            // reading (Codex plan review Phase 5, Round 3, confidence 0.89,
            // adapted for newest-first).
            if (displayText != _lastRenderedLogContent)
            {
                _lastRenderedLogContent = displayText;

                // SelectableLabel keeps an internal TextEditor keyed to its
                // control ID; if that control still holds keyboard focus/a
                // selection from a previous frame, Unity can keep rendering
                // the old text even though displayText has already changed
                // underneath it (observed on real hardware: switching the
                // category toolbar kept showing the unfiltered "All" text).
                // Dropping focus forces it to rebuild from the new string.
                GUI.FocusControl(null);

                if (_logPinnedToTop)
                {
                    _logScrollPosition.y = 0f;
                }
            }

            // Computed explicitly rather than via GUILayout.ExpandHeight +
            // GUILayoutUtility.GetLastRect inside the ScrollView: ExpandHeight
            // does not reliably report the label's true unclipped content
            // height there, which silently broke both the max scroll extent
            // and the "was the user already at the top" pinning check
            // (observed on real hardware: newest lines required manual
            // scrolling every time despite Auto Refresh).
            var contentWidth = Mathf.Max(0f, EditorGUIUtility.currentViewWidth - 24f);
            var contentHeight = Mathf.Max(
                LogViewportHeight, EditorStyles.textArea.CalcHeight(new GUIContent(displayText), contentWidth));

            _logScrollPosition = EditorGUILayout.BeginScrollView(
                _logScrollPosition, GUILayout.Height(LogViewportHeight));
            EditorGUILayout.SelectableLabel(
                displayText, EditorStyles.textArea, GUILayout.Height(contentHeight));
            EditorGUILayout.EndScrollView();

            _logPinnedToTop = _logScrollPosition.y <= LogTopThresholdPixels;
        }

        /// <summary>
        /// Internal (not private) so RemoteTestWindowFilterTests can exercise
        /// it directly without going through IMGUI. Simple case-insensitive
        /// substring match — no structured log parsing (Phase 5 plan,
        /// explicit non-goal).
        /// </summary>
        internal static string[] FilterLogLines(string[] lines, string category)
        {
            if (lines == null)
            {
                return Array.Empty<string>();
            }

            if (string.IsNullOrEmpty(category) || category == "All")
            {
                return lines;
            }

            return lines
                .Where(line => line != null && line.IndexOf(category, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();
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

            if (GUILayout.Button("Open Moonlight"))
            {
                MoonlightLauncher.Launch(RemoteTestSettings.MoonlightApplicationName);
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

            // Not gated by the DisabledScope above — these are independent of
            // the coordinator's transport/SharePath and safe to change
            // mid-build.
            var newMoonlightName = EditorGUILayout.DelayedTextField(
                "Moonlight Application Name", RemoteTestSettings.MoonlightApplicationName);
            if (newMoonlightName != RemoteTestSettings.MoonlightApplicationName)
            {
                RemoteTestSettings.MoonlightApplicationName = newMoonlightName;
            }

            var newFocusAfterDeploy = EditorGUILayout.ToggleLeft(
                "Focus Moonlight after deploy", RemoteTestSettings.FocusMoonlightAfterDeploy);
            if (newFocusAfterDeploy != RemoteTestSettings.FocusMoonlightAfterDeploy)
            {
                RemoteTestSettings.FocusMoonlightAfterDeploy = newFocusAfterDeploy;
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

            if (outcome.Succeeded && RemoteTestSettings.FocusMoonlightAfterDeploy)
            {
                MoonlightLauncher.Launch(RemoteTestSettings.MoonlightApplicationName);
            }
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
                _vrchatStatus = null;
                return;
            }

            try
            {
                var coordinator = RemoteBuildCommand.GetCoordinator();
                _shareReachable = coordinator.IsShareReachable;
                _sdkAvailable = coordinator.IsSdkAvailable;
                _hasLastArtifact = coordinator.LastArtifact != null;
                _vrchatStatus = coordinator.VrchatStatus;
            }
            catch (RemoteBuildException)
            {
                _shareReachable = false;
                _sdkAvailable = false;
                _hasLastArtifact = false;
                _vrchatStatus = null;
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
