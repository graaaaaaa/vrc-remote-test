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
        // debounce. Bridge reachability is checked over the network, and stale status risks a build
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
        private static readonly string[] LogViews = { "Log", "Markers" };

        // The per-slot files are read at most this often while the viewer is
        // drawn: OnGUI runs several times per frame, and a markers file can be
        // 1 MiB per slot.
        private const double ClientFilesCacheSeconds = 1.0;

        private bool _logFoldout;

        // Client-observation plan F8: with a Bridge that writes
        // vrchat-clients.json (v0.6.0), the viewer can show one slot's log or
        // the marker lines; without it, only vrchat-latest.log, as before.
        private int _logSourceIndex;
        private bool _showMarkers;

        // What the toolbars asked for; taken over at the next Layout event.
        private int _requestedLogSourceIndex;
        private bool _requestedShowMarkers;
        private VrchatClients _clients;
        private bool _clientsStale;
        private double _lastClientsRead = double.NegativeInfinity;
        private string _clientFileCacheKey = string.Empty;
        private string _clientFileContent;
        private double _lastClientFilesRead = double.NegativeInfinity;
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

        // The read-only Bridge section (Bridge v0.8.0 and later): taken only at a Layout
        // event, like the Log Viewer's clients list, so a Layout and the event after it
        // draw the same controls.
        private bool _showBridgeSection;
        private BridgeInfo _bridgeInfo;
        private VrchatClients _bridgeClients;
        private double _lastBridgeSectionRead = double.NegativeInfinity;
        private static string _packageVersion;

        private bool _bridgeConfigured;
        private bool _bridgeReachable;
        private bool _sdkAvailable;
        private bool _hasLastArtifact;
        private VrchatStatus _vrchatStatus;
        private double _lastPreflightCheckTime = double.NegativeInfinity;

        private RemoteBuildStatus _currentStatus = RemoteBuildStatus.Idle;
        private string _currentMessage = string.Empty;
        private RemoteBuildOutcome _lastOutcome;
        private DateTime _lastOutcomeTime;

        private bool _settingsFoldout;
        private string _bridgeUrlInput = string.Empty;
        private string _fingerprintInput = string.Empty;
        private string _tokenInput = string.Empty;
        private string _pairingError;
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

            RefreshBridgeSection();

            _windowScrollPosition = EditorGUILayout.BeginScrollView(_windowScrollPosition);

            DrawPreflightSection();
            EditorGUILayout.Space();
            if (_showBridgeSection)
            {
                DrawBridgeSection();
                EditorGUILayout.Space();
            }
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

            if (!_bridgeConfigured)
            {
                EditorGUILayout.HelpBox(
                    "Windows Bridge is not paired. Enter its pairing details in Settings.", MessageType.Warning);
            }
            else
            {
                DrawStatusLine("Windows Bridge", _bridgeReachable, "Connected", "Connecting or unavailable");
            }

            DrawStatusLine("VRChat SDK", _sdkAvailable, "Available", "Not available");

            DrawVrchatStatusLine();
        }

        /// <summary>
        /// Phase 4a: advisory only, never gates the build buttons (unlike Remote
        /// Bridge/VRChat SDK which do via DrawActionButtons' DisabledScope). A
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

        /// <summary>
        /// Read-only: the Bridge's version (warned about when it differs from this
        /// package's), its notes on the active configuration, and which account each running
        /// client is logged in to. Changing the Bridge stays with scripts/vrcrt.sh.
        /// </summary>
        private void DrawBridgeSection()
        {
            EditorGUILayout.LabelField("Bridge", EditorStyles.boldLabel);

            var bridgeVersion = CoreVersion(_bridgeInfo?.Version);
            EditorGUILayout.LabelField("Version",
                bridgeVersion == null ? "Unknown" : BridgeMessages.Sanitize(bridgeVersion, 64));
            if (VersionsDiffer(PackageVersion, bridgeVersion))
            {
                EditorGUILayout.HelpBox(
                    $"This package is {CoreVersion(PackageVersion)} but the Bridge is " +
                    $"{BridgeMessages.Sanitize(bridgeVersion, 64)}. Update both to the same version.",
                    MessageType.Warning);
            }

            if (_bridgeInfo?.ConfigNotes != null)
            {
                foreach (var note in _bridgeInfo.ConfigNotes.Where(n => !string.IsNullOrWhiteSpace(n)).Take(10))
                {
                    EditorGUILayout.HelpBox(BridgeMessages.Sanitize(note, 500), MessageType.Info);
                }
            }

            if (_bridgeClients?.Clients == null)
            {
                return;
            }

            foreach (var client in _bridgeClients.Clients
                         .Where(c => c != null && c.Running != false
                             && c.Slot >= 0 && c.Slot < LanRemoteTransport.SlotCount)
                         .OrderBy(c => c.Slot))
            {
                var login = ClientLogView.DescribeLogin(client);
                if (login != null)
                {
                    EditorGUILayout.LabelField($"Slot {client.Slot}", login);
                }
            }
        }

        private void RefreshBridgeSection()
        {
            var now = EditorApplication.timeSinceStartup;
            if (Event.current.type != EventType.Layout || now - _lastBridgeSectionRead < PreflightCheckIntervalSeconds)
            {
                return;
            }

            _lastBridgeSectionRead = now;
            _showBridgeSection = _bridgeConfigured;
            _bridgeInfo = null;
            _bridgeClients = null;
            if (!_bridgeConfigured)
            {
                return;
            }

            try
            {
                var coordinator = RemoteBuildCommand.GetCoordinator();
                _bridgeInfo = coordinator.BridgeInfo;

                // An old list may name accounts that have since changed; the Log Viewer warns about it.
                var clients = coordinator.VrchatClients;
                _bridgeClients = ClientLogView.IsStale(clients, DateTimeOffset.UtcNow) ? null : clients;
            }
            catch (RemoteBuildException)
            {
                _showBridgeSection = false;
            }
        }

        private static string PackageVersion
        {
            get
            {
                if (_packageVersion == null)
                {
                    var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(RemoteTestWindow).Assembly);
                    _packageVersion = info?.version ?? string.Empty;
                }

                return _packageVersion;
            }
        }

        /// <summary>"0.8.0+abc123" and "v0.8.0" are both "0.8.0"; null for no version.</summary>
        internal static string CoreVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            var plus = version.IndexOf('+');
            var core = (plus >= 0 ? version.Substring(0, plus) : version).Trim();
            if (core.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                core = core.Substring(1);
            }

            return core.Length == 0 ? null : core;
        }

        /// <summary>True only when both versions are known and differ, build metadata aside.</summary>
        internal static bool VersionsDiffer(string packageVersion, string bridgeVersion)
        {
            var package = CoreVersion(packageVersion);
            var bridge = CoreVersion(bridgeVersion);
            return package != null && bridge != null && !string.Equals(package, bridge, StringComparison.OrdinalIgnoreCase);
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

            // AutoLaunchVrchat off, and the result's warnings (Bridge v0.8.0 and later).
            foreach (var note in RemoteBuildCoordinator.ResultNotes(_lastOutcome.Result))
            {
                EditorGUILayout.HelpBox(note, MessageType.Warning);
            }
        }

        /// <summary>
        /// Foldout (default closed), category toolbar filter, Auto Refresh
        /// toggle, and a scrollable read-only view of the Bridge's published
        /// vrchat-latest.log (spec section 47, "Unity log viewer"), or, with a
        /// Bridge that writes vrchat-clients.json, of one slot's log or the
        /// marker lines (spec section 36, client-observation plan F8). Never
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

            var coordinator = RemoteBuildCommand.GetCoordinator();
            var lines = ReadSelectedLog(coordinator);

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
        /// The lines of the selected source, oldest first. Without a clients
        /// list (Bridge 0.5.x) that is vrchat-latest.log, and nothing else is
        /// drawn. Otherwise a source toolbar (Latest or a slot; for markers,
        /// all slots merged or a slot) and the selected client's summary come
        /// first.
        /// </summary>
        private string[] ReadSelectedLog(RemoteBuildCoordinator coordinator)
        {
            // Only during Layout: what decides how many controls are drawn
            // must not change between a Layout event and the event after it.
            var now = EditorApplication.timeSinceStartup;
            var layout = Event.current.type == EventType.Layout;
            if (layout && now - _lastClientsRead >= ClientFilesCacheSeconds)
            {
                _lastClientsRead = now;
                _clients = coordinator.VrchatClients;
                _clientsStale = ClientLogView.IsStale(_clients, DateTimeOffset.UtcNow);
            }

            var slots = ClientLogView.Slots(_clients);
            if (layout)
            {
                _showMarkers = _clients != null && _requestedShowMarkers;
                _logSourceIndex = Math.Min(Math.Max(0, _requestedLogSourceIndex), slots.Length);
            }

            if (_clients == null)
            {
                return SplitLines(coordinator.VrchatLog);
            }

            // A click changes what is drawn only from the next Layout on, so the
            // controls of one Layout and the event after it stay the same.
            var requestedMarkers = GUILayout.Toolbar(_showMarkers ? 1 : 0, LogViews) == 1;
            var requestedSource = GUILayout.Toolbar(_logSourceIndex, ClientLogView.SourceLabels(slots, _showMarkers));
            if (requestedMarkers != _showMarkers || requestedSource != _logSourceIndex)
            {
                _requestedShowMarkers = requestedMarkers;
                _requestedLogSourceIndex = requestedSource;
                Repaint();
            }

            int? slot = _logSourceIndex == 0 || _logSourceIndex > slots.Length ? (int?)null : slots[_logSourceIndex - 1];

            DrawClientSummary(slot);

            if (!_showMarkers && slot == null)
            {
                return SplitLines(coordinator.VrchatLog);
            }

            var key = $"{_showMarkers}:{slot}";
            if (layout && (key != _clientFileCacheKey || now - _lastClientFilesRead >= ClientFilesCacheSeconds))
            {
                _clientFileCacheKey = key;
                _lastClientFilesRead = now;
                _clientFileContent = _showMarkers
                    ? ReadMarkers(coordinator, slot, slots)
                    : coordinator.GetClientLog(slot.Value);
            }

            return SplitLines(_clientFileContent);
        }

        private static string ReadMarkers(RemoteBuildCoordinator coordinator, int? slot, int[] slots)
        {
            var files = (slot.HasValue ? new[] { slot.Value } : slots)
                .Select(s => ClientLogView.ParseMarkers(s, coordinator.GetClientMarkers(s)))
                .ToArray();
            var notes = files
                .Where(f => f.Header != null && (f.Header.CatchingUp || f.Header.DroppedRecords > 0))
                .Select(f => f.Header.CatchingUp
                    ? $"(slot {f.Header.Slot}: still reading its log; markers may be missing)"
                    : $"(slot {f.Header.Slot}: {f.Header.DroppedRecords} older markers were dropped)");
            var records = ClientLogView.Merge(files.Select(f => f.Records))
                .Select(r => ClientLogView.FormatRecord(r, withSlot: !slot.HasValue));
            // Last, so that the newest-first view shows them on top.
            return string.Join("\n", records.Concat(notes));
        }

        private void DrawClientSummary(int? slot)
        {
            if (_clientsStale)
            {
                EditorGUILayout.HelpBox(
                    "The Bridge's client list is more than 15 s old; what is shown may be out of date.",
                    MessageType.Warning);
            }

            if (slot == null)
            {
                return;
            }

            var client = _clients.Clients?.FirstOrDefault(c => c != null && c.Slot == slot.Value);
            EditorGUILayout.LabelField(ClientLogView.Describe(client), EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(_clientsStale || client?.ObservationEpoch == null
                || client.LogStatus != "shown"))
            {
                if (GUILayout.Button("Save Full Log..."))
                {
                    var path = EditorUtility.SaveFilePanel("Save VRChat Full Log", string.Empty,
                        $"vrchat-slot-{slot.Value}-generation-{client.ClientGeneration}.log", "log");
                    if (!string.IsNullOrEmpty(path))
                        StartSaveFullLog(slot.Value, client.ClientGeneration,
                            client.ObservationEpoch.Value, path);
                }
            }
            if (client?.Warnings != null)
            {
                foreach (var warning in client.Warnings.Where(w => !string.IsNullOrEmpty(w)))
                {
                    EditorGUILayout.HelpBox(warning, MessageType.Warning);
                }
            }
        }

        private async void StartSaveFullLog(int slot, long generation, long epoch, string destination)
        {
            try
            {
                await RemoteBuildCommand.GetCoordinator().DownloadFullLogAsync(
                    slot, generation, epoch, destination);
                Debug.Log("[VRC Remote Test] Full VRChat log saved: " + destination);
            }
            catch (Exception ex)
            {
                Debug.LogError("[VRC Remote Test] Could not save full VRChat log: " + ex.Message);
            }
        }

        private static string[] SplitLines(string content) =>
            string.IsNullOrEmpty(content) ? Array.Empty<string>() : content.Split('\n');

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
            var running = _bridgeConfigured && RemoteBuildCommand.GetCoordinator().IsRunning;

            using (new EditorGUI.DisabledScope(running || !_bridgeConfigured))
            {
                if (GUILayout.Button("Remote Build & Test"))
                {
                    StartRemoteBuild();
                }
            }

            using (new EditorGUI.DisabledScope(running || !_bridgeConfigured || !_hasLastArtifact))
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

            // Disabled while a build/deploy is running: changing the pairing
            // mid-flight would invalidate the coordinator's transport out from
            // under the in-flight operation (see RemoteBuildCommand.InvalidateCoordinator).
            using (new EditorGUI.DisabledScope(_bridgeConfigured && RemoteBuildCommand.GetCoordinator().IsRunning))
            {
                _bridgeUrlInput = EditorGUILayout.DelayedTextField("Bridge URL", _bridgeUrlInput);
                _fingerprintInput = EditorGUILayout.DelayedTextField("Certificate SHA-256", _fingerprintInput);
                _tokenInput = EditorGUILayout.PasswordField("Pairing Token", _tokenInput);
                if (GUILayout.Button("Save Pairing"))
                {
                    VerifyAndSavePairing(_bridgeUrlInput.Trim(), _fingerprintInput.Trim(), _tokenInput.Trim());
                }
                if (!string.IsNullOrEmpty(_pairingError))
                    EditorGUILayout.HelpBox(_pairingError, MessageType.Error);

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
            // the coordinator's connection and safe to change
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

        private async void VerifyAndSavePairing(string url, string fingerprint, string token)
        {
            try
            {
                using (var transport = new LanRemoteTransport(url, fingerprint, token))
                    await transport.ProbeAsync();
                BridgeTokenStore.Save(url, token);
                RemoteTestSettings.BridgeUrl = url;
                RemoteTestSettings.BridgeFingerprint = fingerprint;
                _tokenInput = string.Empty;
                _pairingError = null;
                RemoteBuildCommand.InvalidateCoordinator();
                RefreshPreflightStatus();
            }
            catch (Exception ex) { _pairingError = ex.Message; }
            Repaint();
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
            _bridgeUrlInput = RemoteTestSettings.TryGetBridgeUrl(out var url) ? url : string.Empty;
            _fingerprintInput = RemoteTestSettings.BridgeFingerprint;
        }

        private void RefreshPreflightStatus()
        {
            _lastPreflightCheckTime = EditorApplication.timeSinceStartup;

            _bridgeConfigured = RemoteTestSettings.TryGetBridgeUrl(out _)
                && !string.IsNullOrEmpty(RemoteTestSettings.BridgeFingerprint);
            if (!_bridgeConfigured)
            {
                _bridgeReachable = false;
                _sdkAvailable = false;
                _hasLastArtifact = false;
                _vrchatStatus = null;
                return;
            }

            try
            {
                var coordinator = RemoteBuildCommand.GetCoordinator();
                _bridgeReachable = coordinator.IsBridgeReachable;
                _sdkAvailable = coordinator.IsSdkAvailable;
                _hasLastArtifact = coordinator.LastArtifact != null;
                _vrchatStatus = coordinator.VrchatStatus;
            }
            catch (RemoteBuildException)
            {
                _bridgeConfigured = false;
                _bridgeReachable = false;
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
