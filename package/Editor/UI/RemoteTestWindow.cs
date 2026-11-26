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
        [NonSerialized] private double _lastBridgeRepaintTime = double.NegativeInfinity;

        // Longer than ClientLogView.StaleAfter: the Slot rows come and go with the list, and
        // every change moves the controls below them, so a list a few reads late still shows.
        private static readonly TimeSpan BridgeClientRowsStaleAfter = TimeSpan.FromSeconds(60);

        // The "VRChat Clients" part of the Bridge section. The fields are edited freely; what
        // decides which controls are drawn (the mode, the parsed profiles, the status of an
        // apply, the profile accounts) is taken over at a Layout event only. A script reload
        // keeps the edited fields but not the state of an operation, whose await does not
        // survive it: those fields are [NonSerialized] (Unity restores private fields).
        private const double ProfilesRefreshSeconds = 15.0;
        private bool _clientFieldsEdited;
        private int _clientModeIndex;
        private int _requestedClientModeIndex;
        private int _clientCount = 1;
        private string _clientProfilesText = string.Empty;
        private VrchatClientSettings _activeClientSettings;
        private VrchatClientSettings _desiredClientSettings;
        [NonSerialized] private string _clientFieldsProblem;
        [NonSerialized] private bool _clientOperationRunning;
        [NonSerialized] private string _clientOperationStatus;
        [NonSerialized] private CancellationTokenSource _clientOperationCts;
        private VrchatClientSettingsOutcome _clientOutcome;
        private BridgeProfiles _profiles;
        private BridgeProfiles _loadedProfiles;
        [NonSerialized] private bool _profilesLoading;
        [NonSerialized] private double _lastProfilesRead = double.NegativeInfinity;
        [NonSerialized] private string _shownClientStatus;
        private VrchatClientSettingsOutcome _shownClientOutcome;
        [NonSerialized] private bool _shownClientOperationRunning;
        private VrchatClientSettings _awaitingAppliedSettings;

        // Whether the Bridge reports slotPreconditions.v1 (Bridge v0.12.0), read every
        // SlotGuardRefreshSeconds while it runs Relaunch. It only picks the confirmation's text:
        // the operation reads the capabilities again before it sends anything.
        private const double SlotGuardRefreshSeconds = 15.0;
        [NonSerialized] private bool _loadedSlotGuard;
        [NonSerialized] private bool _slotGuard;
        [NonSerialized] private bool _slotGuardLoading;
        [NonSerialized] private double _lastSlotGuardRead = double.NegativeInfinity;

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

        // One run at a time for the whole Editor session; a domain reload rebuilds it from SessionState.
        private static RemoteRunController _runController;
        [NonSerialized] private double _lastRunRefresh = double.NegativeInfinity;
        [SerializeField] private string _runSlots;
        [SerializeField] private bool _runSlotsEdited;
        [SerializeField] private bool _runUseLastBuild;
        [SerializeField] private bool _runWaitForTag = true;

        // The procedure choice (r3-contract.md §6.3): only what RunProcedures.Offered() lists. The
        // manual-steps fields are drawn only once that is offered (after validation).
        [SerializeField] private int _runProcedureIndex;
        [NonSerialized] private int _requestedRunProcedureIndex = -1;
        [SerializeField] private string _runChecklistPath;
        [SerializeField] private int _runLeaveRejoinIndex;
        [NonSerialized] private int _requestedRunLeaveRejoinIndex = -1;
        [SerializeField] private int _runTargetSlot;
        [NonSerialized] private string[] _runChecklistIds;
        [NonSerialized] private string _runChecklistIdsPath;
        [NonSerialized] private string _runChecklistProblem;

        // The waiting panel (r3-contract.md §6.2): this Bridge's run found from the default run root,
        // asked through `runner waiting --json` at most once a second on a worker thread. The
        // snapshot is taken at a Layout event only; whether the panel shows is the runner's `valid`.
        private static RunWaitingWatcher _waitingWatcher;
        [NonSerialized] private WaitingSnapshot _waitingSnapshot = new WaitingSnapshot();
        [NonSerialized] private double _lastWaitingTick = double.NegativeInfinity;
        [NonSerialized] private int _waitingVersion = -1;
        [NonSerialized] private string _waitingMemo = string.Empty;
        [NonSerialized] private string _waitingMemoWaitId;

        private static RunWaitingWatcher WaitingWatcher => _waitingWatcher ?? (_waitingWatcher = new RunWaitingWatcher());

        private static RemoteRunController RunController
        {
            get
            {
                if (_runController == null)
                {
                    _runController = new RemoteRunController();
                    _runController.Restore();
                }

                return _runController;
            }
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

            // Stops waiting for a settings change, so the build lock is not held for a closed window.
            _clientOperationCts?.Cancel();
        }

        /// <summary>
        /// Explicit tick-based refresh for the Log Viewer's Auto Refresh, kept
        /// independent of the existing OnGUI-embedded preflight TTL check
        /// above: preflight status is advisory and stale-tolerant, but a log
        /// viewer a developer is actively watching needs a refresh even while
        /// the window isn't receiving input events (Codex plan review Phase
        /// 5, Round 2, confidence 0.90).
        ///
        /// The Bridge section is repainted on its own tick too. The Bridge's answers arrive in
        /// the background, so without it the section changed only when the next input event
        /// redrew the window — often the click itself, moving the control under the pointer.
        /// </summary>
        private void OnEditorUpdate()
        {
            var now = EditorApplication.timeSinceStartup;
            if (RunController.Active && now - _lastRunRefresh >= 1.0)
            {
                _lastRunRefresh = now;
                RunController.Refresh();
                Repaint();
            }

            // On the same one-second beat: the watcher starts a poll only when the last one ended a
            // second ago or more, and polls on a worker thread, so this never waits for Python.
            if (now - _lastWaitingTick >= 1.0)
            {
                _lastWaitingTick = now;
                // Also with no pairing: the watcher then drops what it found for the previous one.
                WaitingWatcher.Tick(DeployResultWriter.DefaultRoot(), RemoteTestSettings.BridgeUrl, RemoteTestSettings.BridgeFingerprint);

                var version = WaitingWatcher.Version;
                if (version != _waitingVersion || _waitingSnapshot.Shown != null)
                {
                    // Also once a second while the panel shows, for the minutes left.
                    _waitingVersion = version;
                    Repaint();
                }
            }

            if (_showBridgeSection && now - _lastBridgeRepaintTime >= PreflightCheckIntervalSeconds)
            {
                _lastBridgeRepaintTime = now;
                Repaint();
            }

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
            if (Event.current.type == EventType.Layout)
            {
                TakeWaitingSnapshot();
            }

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
            if (_waitingSnapshot.Shown != null)
            {
                DrawWaitingPanel();
                EditorGUILayout.Space();
            }

            DrawRunSection();
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
        /// The Bridge's version (warned about when it differs from this package's), its notes
        /// on the active configuration, which account each running client is logged in to,
        /// and the VRChat clients it launches (<see cref="DrawVrchatClientsSection"/>). Every
        /// other Bridge setting stays with scripts/vrcrt.sh.
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

            if (_bridgeClients?.Clients != null)
            {
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

            if (_activeClientSettings != null)
            {
                DrawVrchatClientsSection();
            }
        }

        /// <summary>
        /// Mode, client count or profiles, written into the Bridge's config.json through its
        /// management API (v0.8.0 and later). The Bridge checks the change again, closes the
        /// clients it launched when the mode changes (refusing while another VRChat runs), and
        /// restarts its host; the next deploy launches the new clients. Shown only while the
        /// Bridge reports settings this package can read.
        /// </summary>
        private void DrawVrchatClientsSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("VRChat Clients", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Current", _activeClientSettings.Describe());

            var busy = _clientOperationRunning || RunController.Active || RemoteBuildCommand.GetCoordinator().IsRunning;
            using (new EditorGUI.DisabledScope(busy))
            {
                // A toolbar, not a popup: on the Mac a popup opens a native menu, and the editor
                // crashed inside it (ShowDelayedContextMenu) in the first hands-on check.
                int mode;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel(new GUIContent("Mode",
                        "Reload: every client on one account, reloaded by --watch-worlds on each deploy. "
                        + "Relaunch: one account per profile, all relaunched on each deploy."));
                    mode = GUILayout.Toolbar(_clientModeIndex, VrchatClientsView.ModeLabels);
                }

                if (mode != _clientModeIndex)
                {
                    _requestedClientModeIndex = mode;
                    MarkClientFieldsEdited();
                    if (mode == 1 && string.IsNullOrWhiteSpace(_clientProfilesText))
                    {
                        _clientProfilesText = "0, 1";
                    }
                }

                if (_clientModeIndex == 0)
                {
                    var count = EditorGUILayout.IntSlider("Clients", _clientCount, 1, VrchatClientSettings.MaxClients);
                    if (count != _clientCount)
                    {
                        _clientCount = count;
                        MarkClientFieldsEdited();
                    }

                    EditorGUILayout.HelpBox(
                        "Every client is logged in to the same account (profile 0). They reload the world on each deploy.",
                        MessageType.None);
                }
                else
                {
                    var text = EditorGUILayout.TextField(
                        new GUIContent("Profiles", "VRChat profile numbers, in launch order, e.g. 0, 1, 2, 3"),
                        _clientProfilesText);
                    if (text != _clientProfilesText)
                    {
                        _clientProfilesText = text;
                        MarkClientFieldsEdited();
                    }

                    DrawProfileAccounts();
                }
            }

            if (_clientFieldsProblem != null)
            {
                EditorGUILayout.HelpBox(_clientFieldsProblem, MessageType.Error);
            }

            var clientsRunning = _bridgeClients?.Clients == null
                ? (bool?)null
                : _bridgeClients.Clients.Any(c => c != null && c.Running != false);
            var reloadNote = VrchatClientsView.ReloadCountNote(_activeClientSettings, _desiredClientSettings, clientsRunning);
            if (reloadNote != null)
            {
                EditorGUILayout.HelpBox(reloadNote, MessageType.Info);
            }

            if (VrchatClientsView.NeedsLongerTimeout(_desiredClientSettings, RemoteTestSettings.ResultTimeoutSeconds))
            {
                EditorGUILayout.HelpBox(
                    $"Raise Result Timeout in Settings to {VrchatClientsView.RecommendedTimeoutSeconds} s or more: "
                    + "3 or more clients take longer to close and start.",
                    MessageType.Warning);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                var changed = _desiredClientSettings != null && !_desiredClientSettings.SameAs(_activeClientSettings);
                using (new EditorGUI.DisabledScope(busy || !changed))
                {
                    if (GUILayout.Button("Apply to Bridge"))
                    {
                        ConfirmAndApplyClientSettings(_activeClientSettings, _desiredClientSettings);
                    }
                }

                using (new EditorGUI.DisabledScope(busy || !_clientFieldsEdited))
                {
                    if (GUILayout.Button("Revert"))
                    {
                        _clientFieldsEdited = false;
                        _awaitingAppliedSettings = null;
                        _lastBridgeSectionRead = double.NegativeInfinity;
                    }
                }

                using (new EditorGUI.DisabledScope(busy))
                {
                    if (GUILayout.Button(new GUIContent("Close Bridge's VRChat",
                            "Closes the VRChat clients the Bridge launched; any other VRChat is left running.")))
                    {
                        StartCloseVrchatClients();
                    }
                }
            }

            DrawSlotRows(busy);

            if (_shownClientOperationRunning)
            {
                EditorGUILayout.HelpBox(_shownClientStatus ?? "Working...", MessageType.Info);
                using (new EditorGUI.DisabledScope(_clientOperationCts == null || _clientOperationCts.IsCancellationRequested))
                {
                    if (GUILayout.Button(new GUIContent("Stop waiting",
                            "Stops waiting and frees Remote Build. A change the Bridge already accepted is still applied.")))
                    {
                        _clientOperationCts?.Cancel();
                    }
                }
            }
            else if (_shownClientOutcome != null)
            {
                EditorGUILayout.HelpBox(_shownClientOutcome.Message,
                    _shownClientOutcome.Applied ? MessageType.Info : MessageType.Error);
                foreach (var detail in _shownClientOutcome.Details)
                {
                    EditorGUILayout.HelpBox(detail, _shownClientOutcome.Applied ? MessageType.None : MessageType.Warning);
                }
            }
        }

        /// <summary>
        /// One row per slot the Bridge observes, with Close and Launch for Relaunch. With a Bridge
        /// that reports slotPreconditions.v1 (v0.12.0), the request carries the row's client
        /// generation and the Bridge's state as a precondition, and the Bridge does nothing when
        /// either changed; without it, nothing ties the request to the client shown here, and the
        /// confirmation says so. The list is the one read at the last Layout.
        /// </summary>
        private void DrawSlotRows(bool busy)
        {
            var clients = _bridgeClients?.Clients;
            if (clients == null || clients.Count == 0)
            {
                return;
            }

            EditorGUILayout.LabelField("Clients", EditorStyles.miniBoldLabel);
            var now = DateTimeOffset.UtcNow;
            string blocked = null;
            foreach (var entry in clients.Where(c => c != null).OrderBy(c => c.Slot).Take(VrchatClientSettings.MaxClients))
            {
                var actions = VrchatClientsView.ActionsFor(entry, _activeClientSettings, _bridgeInfo?.Version,
                    _bridgeClients.UpdatedAtUtc, now, busy);
                blocked = blocked ?? actions.Blocked;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Slot " + entry.Slot, VrchatClientsView.DescribeSlot(entry));
                    using (new EditorGUI.DisabledScope(!actions.CanClose))
                    {
                        if (GUILayout.Button(new GUIContent("Close", "Close this client only; the others keep running."),
                                GUILayout.Width(60))
                            && EditorUtility.DisplayDialog("Close one VRChat client",
                                VrchatClientsView.ConfirmClose(entry, _slotGuard), "Close", "Cancel"))
                        {
                            StartSlotOperation(entry.Slot, entry.ClientGeneration, close: true, _slotGuard, _bridgeInfo);
                        }
                    }

                    using (new EditorGUI.DisabledScope(!actions.CanLaunch))
                    {
                        if (GUILayout.Button(new GUIContent("Launch", actions.LaunchOff != null
                                    ? "Off: " + actions.LaunchOff + "; the Bridge would refuse it."
                                    : "Start this slot again as in the last deploy."),
                                GUILayout.Width(60))
                            && EditorUtility.DisplayDialog("Start one VRChat client again",
                                VrchatClientsView.ConfirmLaunch(entry, _slotGuard), "Launch", "Cancel"))
                        {
                            StartSlotOperation(entry.Slot, entry.ClientGeneration, close: false, _slotGuard, _bridgeInfo);
                        }
                    }
                }
            }

            // Always one line, so Layout and Repaint lay out the same controls even when the list
            // turns stale or an operation ends between the two events.
            EditorGUILayout.LabelField(blocked != null && !busy ? "Close and Launch are off: " + blocked + "." : " ",
                EditorStyles.miniLabel);
        }

        /// <param name="clientGeneration">The generation in the row the user confirmed.</param>
        /// <param name="confirmedGuarded">Whether the confirmation said the Bridge checks the request.</param>
        /// <param name="shownBridge">The GET /bridge answer shown with the list; read again when older than 15 s.</param>
        private async void StartSlotOperation(int slot, long clientGeneration, bool close, bool confirmedGuarded,
            BridgeInfo shownBridge)
        {
            _clientOperationRunning = true;
            _clientOperationStatus = (close ? "Closing" : "Starting") + " the VRChat client in slot " + slot + "...";
            _clientOutcome = null;
            _clientOperationCts = new CancellationTokenSource();
            var token = _clientOperationCts.Token;

            VrchatClientSettingsOutcome outcome;
            try
            {
                var coordinator = RemoteBuildCommand.GetCoordinator();
                var guard = await coordinator.ResolveSlotGuardAsync(confirmedGuarded, clientGeneration, shownBridge, token);
                if (guard.Kind == SlotGuardKind.Refused)
                {
                    outcome = VrchatClientSettingsOutcome.Failure(
                        "Did not " + (close ? "close" : "launch") + " slot " + slot + ": " + guard.Problem + ".");
                }
                else
                {
                    // Unguarded: the body-less request of R1a (precondition null).
                    outcome = close
                        ? await coordinator.CloseVrchatClientAsync(slot, guard.Precondition, null, token)
                        : await coordinator.LaunchVrchatClientAsync(slot, guard.Precondition, null, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Only the reads before the request can end here; the request itself reports its own stop.
                outcome = VrchatClientSettingsOutcome.Failure(
                    "Stopped before " + (close ? "closing" : "launching") + " slot " + slot + "; nothing was sent.");
            }
            catch (Exception ex)
            {
                outcome = VrchatClientSettingsOutcome.Failure(
                    "Could not " + (close ? "close" : "launch") + " slot " + slot + ": " + ex.Message);
            }

            FinishClientOperation(outcome);
            if (outcome.Applied)
            {
                Debug.Log("[VRC Remote Test] " + outcome.Message);
            }
            else
            {
                Debug.LogWarning("[VRC Remote Test] " + outcome.Message);
            }
        }

        private void DrawProfileAccounts()
        {
            if (_desiredClientSettings == null || !_desiredClientSettings.IsRelaunch)
            {
                return;
            }

            foreach (var profile in _desiredClientSettings.Profiles)
            {
                EditorGUILayout.LabelField($"  Profile {profile}", VrchatClientsView.DescribeProfile(profile, _profiles));
            }

            if (_profiles?.Warnings != null)
            {
                foreach (var warning in _profiles.Warnings.Where(w => !string.IsNullOrWhiteSpace(w)).Take(5))
                {
                    EditorGUILayout.HelpBox(BridgeMessages.Sanitize(warning, 300), MessageType.Warning);
                }
            }
        }

        /// <summary>At a Layout event: the Bridge's current settings, the fields when not edited, and what they describe.</summary>
        private void RefreshVrchatClientsSection(double now)
        {
            _activeClientSettings = VrchatClientSettings.FromActiveConfig(_bridgeInfo?.ActiveConfig);
            if (!_clientFieldsEdited && _activeClientSettings != null)
            {
                _requestedClientModeIndex = VrchatClientsView.ModeIndex(_activeClientSettings);
                _clientCount = _activeClientSettings.IsRelaunch ? _clientCount : _activeClientSettings.Count;
                if (_activeClientSettings.IsRelaunch)
                {
                    _clientProfilesText = VrchatClientSettings.FormatProfiles(_activeClientSettings.Profiles);
                }
            }

            _clientModeIndex = _requestedClientModeIndex;
            _desiredClientSettings = VrchatClientsView.Desired(_clientModeIndex, _clientCount, _clientProfilesText,
                out _clientFieldsProblem);
            if (_awaitingAppliedSettings != null && _awaitingAppliedSettings.SameAs(_activeClientSettings))
            {
                _awaitingAppliedSettings = null;
                _clientFieldsEdited = false;
            }

            if (_activeClientSettings != null && _clientModeIndex == 1 && !_profilesLoading
                && now - _lastProfilesRead >= ProfilesRefreshSeconds)
            {
                _lastProfilesRead = now;
                StartLoadProfiles();
            }

            if (_activeClientSettings != null && _activeClientSettings.IsRelaunch && !_slotGuardLoading
                && now - _lastSlotGuardRead >= SlotGuardRefreshSeconds)
            {
                _lastSlotGuardRead = now;
                StartLoadSlotGuard();
            }
        }

        /// <summary>Reads whether the Bridge checks a close or launch; an unreadable answer counts as no (contract §1.1).</summary>
        private async void StartLoadSlotGuard()
        {
            _slotGuardLoading = true;
            try
            {
                var capabilities = await RemoteBuildCommand.GetCoordinator().ReadBridgeCapabilitiesAsync();
                _loadedSlotGuard = capabilities != null && capabilities.SupportsSlotPreconditions;
            }
            catch (Exception ex)
            {
                _loadedSlotGuard = false;
                Debug.LogWarning("[VRC Remote Test] Could not read the Bridge's capabilities: " + ex.Message);
            }
            finally
            {
                _slotGuardLoading = false;
                Repaint();
            }
        }

        private void MarkClientFieldsEdited()
        {
            _clientFieldsEdited = true;
            _awaitingAppliedSettings = null;
        }

        private void TakeOverClientOperation()
        {
            _shownClientOperationRunning = _clientOperationRunning;
            _shownClientStatus = _clientOperationStatus;
            _shownClientOutcome = _clientOutcome;
            _profiles = _loadedProfiles;
            _slotGuard = _loadedSlotGuard;
        }

        private async void StartLoadProfiles()
        {
            _profilesLoading = true;
            try
            {
                var profiles = await RemoteBuildCommand.GetCoordinator().GetVrchatProfilesAsync();
                if (profiles != null)
                {
                    _loadedProfiles = profiles;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[VRC Remote Test] Could not read the Bridge's VRChat profiles: " + ex.Message);
            }
            finally
            {
                _profilesLoading = false;
                Repaint();
            }
        }

        /// <summary>
        /// Asked on every apply, not only when the cached mode differs: the Bridge may have been
        /// changed meanwhile, and the coordinator refuses to write when its mode is no longer the
        /// one confirmed here.
        /// </summary>
        private void ConfirmAndApplyClientSettings(VrchatClientSettings active, VrchatClientSettings desired)
        {
            var message = VrchatClientsView.ClosesClients(active, desired)
                ? $"Apply {desired.Describe()} and restart the Bridge? Changing from {active.Mode} to {desired.Mode} mode "
                  + "first closes the VRChat clients the Bridge launched; the Bridge refuses while another VRChat runs on Windows."
                : $"Apply {desired.Describe()} and restart the Bridge? The next deploy launches these clients.";
            if (!EditorUtility.DisplayDialog("Apply VRChat client settings", message, "Apply", "Cancel"))
            {
                return;
            }

            StartApplyClientSettings(desired, active.Mode);
        }

        private async void StartApplyClientSettings(VrchatClientSettings desired, string confirmedActiveMode)
        {
            _clientOperationRunning = true;
            _clientOperationStatus = "Applying " + desired.Describe() + "...";
            _clientOutcome = null;
            _clientOperationCts?.Dispose();
            _clientOperationCts = new CancellationTokenSource();
            var progress = new Progress<string>(message =>
            {
                _clientOperationStatus = message;
                Repaint();
            });

            VrchatClientSettingsOutcome outcome;
            try
            {
                outcome = await RemoteBuildCommand.GetCoordinator().ApplyVrchatClientSettingsAsync(
                    desired, confirmedActiveMode, progress, _clientOperationCts.Token);
            }
            catch (Exception ex)
            {
                outcome = VrchatClientSettingsOutcome.Failure("Could not apply the settings: " + ex.Message);
            }

            // The fields keep what was applied until the Bridge's own report (refreshed every
            // few seconds) shows it, so they never jump back to the old settings meanwhile.
            FinishClientOperation(outcome);
            if (outcome.Applied)
            {
                _awaitingAppliedSettings = desired;
                _lastProfilesRead = double.NegativeInfinity;
            }

            if (outcome.Applied)
            {
                Debug.Log("[VRC Remote Test] " + outcome.Message);
            }
            else
            {
                Debug.LogWarning("[VRC Remote Test] " + outcome.Message);
            }
        }

        private async void StartCloseVrchatClients()
        {
            _clientOperationRunning = true;
            _clientOperationStatus = "Closing the VRChat clients the Bridge launched...";
            _clientOutcome = null;

            VrchatClientSettingsOutcome outcome;
            try
            {
                outcome = await RemoteBuildCommand.GetCoordinator().CloseVrchatClientsAsync();
            }
            catch (Exception ex)
            {
                outcome = VrchatClientSettingsOutcome.Failure("Could not close the VRChat clients: " + ex.Message);
            }

            FinishClientOperation(outcome);
        }

        private void FinishClientOperation(VrchatClientSettingsOutcome outcome)
        {
            _clientOperationRunning = false;
            _clientOutcome = outcome;
            _clientOperationCts?.Dispose();
            _clientOperationCts = null;

            // The section reads the Bridge again at the next Layout, so a restarted host's
            // settings and notes show at once.
            _lastBridgeSectionRead = double.NegativeInfinity;
            Repaint();
        }

        private void RefreshBridgeSection()
        {
            if (Event.current.type != EventType.Layout)
            {
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            if (now - _lastBridgeSectionRead >= PreflightCheckIntervalSeconds)
            {
                ReadBridgeSection(now);
            }

            RefreshVrchatClientsSection(now);
            TakeOverClientOperation();
        }

        private void ReadBridgeSection(double now)
        {
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
                _bridgeClients = clients == null || DateTimeOffset.UtcNow - clients.UpdatedAtUtc > BridgeClientRowsStaleAfter
                    ? null
                    : clients;
            }
            catch (RemoteBuildException)
            {
                _showBridgeSection = false;
            }
        }

        private static string PackageVersion => PackageVersionInfo.Current;

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
            var running = RunController.Active || (_bridgeConfigured && RemoteBuildCommand.GetCoordinator().IsRunning);

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

        /// <summary>
        /// "Deploy and collect": the packaged runner prepares, this Editor builds and deploys, and
        /// the runner waits for every listed slot, checks the build tag and collects evidence in
        /// run.json. Every control is drawn on every event (only enabled or disabled), so the run
        /// finishing between Layout and Repaint does not change the layout.
        /// </summary>
        private void DrawRunSection()
        {
            var run = RunController;
            EditorGUILayout.LabelField("Deploy and Collect", EditorStyles.boldLabel);
            // Until edited, the slots follow the Bridge's client count, once it is known.
            if (!_runSlotsEdited && _activeClientSettings != null)
            {
                _runSlots = string.Join(",", Enumerable.Range(0, Math.Max(1, Math.Min(_activeClientSettings.Count, VrchatClientSettings.MaxClients))));
            }

            using (new EditorGUI.DisabledScope(run.Active))
            {
                var slots = EditorGUILayout.TextField(new GUIContent("Slots",
                    "Every slot that must be ready, e.g. 0,1. Not narrowed to the clients that happen to run."), _runSlots ?? string.Empty);
                if (slots != (_runSlots ?? string.Empty))
                {
                    _runSlots = slots;
                    _runSlotsEdited = true;
                }
                using (new EditorGUI.DisabledScope(!_hasLastArtifact))
                {
                    _runUseLastBuild = EditorGUILayout.Toggle(new GUIContent("Use the last build",
                        "Deploy the last artifact again instead of building (its tag is kept only while its bytes are unchanged)."),
                        _runUseLastBuild && _hasLastArtifact);
                }

                _runWaitForTag = EditorGUILayout.Toggle(new GUIContent("Wait for the build tag",
                    "Wait up to 120 s for each client to log vrcrt.build=<tag>. Off: look once, for worlds that log no tag " +
                    "(their world build match stays UNKNOWN either way)."), _runWaitForTag);
                DrawProcedureFields();
            }

            var procedureArguments = RunProcedureArguments(out var procedureProblem);

            var coordinatorBusy = _bridgeConfigured && RemoteBuildCommand.GetCoordinator().IsRunning;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(run.Active || coordinatorBusy || !_bridgeConfigured || _clientOperationRunning
                                                   || string.IsNullOrWhiteSpace(_runSlots) || procedureArguments == null))
                {
                    if (GUILayout.Button(new GUIContent("Run",
                            "Build (or redeploy), wait until every slot is ready, and collect evidence into a run folder.")))
                    {
                        run.Start(_runSlots.Replace(" ", string.Empty), _runUseLastBuild, RemoteTestSettings.BridgeUrl,
                            RemoteTestSettings.BridgeFingerprint, _runWaitForTag, procedureArguments);
                    }
                }

                using (new EditorGUI.DisabledScope(!run.Active))
                {
                    if (GUILayout.Button(new GUIContent("Cancel", "Stops the build if it is still running and asks the runner to stop.")))
                    {
                        run.Cancel();
                    }
                }

                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(run.RunDirectory) || !System.IO.Directory.Exists(run.RunDirectory)))
                {
                    if (GUILayout.Button("Open Run Folder"))
                    {
                        EditorUtility.RevealInFinder(System.IO.Path.Combine(run.RunDirectory, "run.json"));
                    }
                }
            }

            EditorGUILayout.HelpBox(run.Status ?? "Not run in this Editor session.",
                run.Summary != null && run.Summary.Finished && run.Summary.Result != "PASS" ? MessageType.Warning : MessageType.None);
            EditorGUILayout.LabelField(run.Summary == null || run.Summary.Results.Count == 0
                ? " "
                : string.Join("   ", run.Summary.Results.Select(p => p.Key + ": " + p.Value)), EditorStyles.miniLabel);

            // Any run that holds this Bridge's claim, also one from the CLI or from before a restart.
            var found = _waitingSnapshot.Run;
            EditorGUILayout.LabelField(found != null ? "This Bridge's run: " + found.RunId + " (" + found.State + ")"
                : _waitingSnapshot.EndedNote ?? " ", RunWaitingView.PlainLabel);
            if (SelectedProcedure() == RunProcedures.ManualSteps)
            {
                // Drawn whenever manual-steps is chosen (that changes at a Layout event only), empty when nothing is missing.
                EditorGUILayout.LabelField(procedureProblem ?? " ", EditorStyles.wordWrappedMiniLabel);
            }
        }

        private void TakeWaitingSnapshot()
        {
            _waitingSnapshot = WaitingWatcher.Snapshot();
            var waitId = _waitingSnapshot.Shown?.WaitId;
            if (waitId != _waitingMemoWaitId)
            {
                // A new item starts with an empty memo. The text area keeps its own copy of the
                // text while it has keyboard focus, so the focus goes too (seen on Windows, 2026-11-26).
                _waitingMemoWaitId = waitId;
                _waitingMemo = string.Empty;
                GUIUtility.keyboardControl = 0;
            }

            if (_requestedRunProcedureIndex >= 0)
            {
                _runProcedureIndex = _requestedRunProcedureIndex;
                _requestedRunProcedureIndex = -1;
            }

            if (_requestedRunLeaveRejoinIndex >= 0)
            {
                _runLeaveRejoinIndex = _requestedRunLeaveRejoinIndex;
                _requestedRunLeaveRejoinIndex = -1;
            }
        }

        private void DrawWaitingPanel()
        {
            var pressed = RunWaitingView.Draw(_waitingSnapshot, ref _waitingMemo);
            if (pressed != null)
            {
                WaitingWatcher.Answer(_waitingSnapshot.Shown.WaitId, pressed, _waitingMemo, RemoteTestSettings.BridgeUrl,
                    RemoteTestSettings.BridgeFingerprint);
            }
        }

        private string SelectedProcedure()
        {
            var offered = RunProcedures.Offered();
            return offered[Mathf.Clamp(_runProcedureIndex, 0, offered.Length - 1)];
        }

        /// <summary>
        /// The procedure choice, and for manual-steps (once it is offered) the checklist, "Leave and
        /// rejoin after" and the target slot. Choices are taken over at the next Layout event.
        /// </summary>
        private void DrawProcedureFields()
        {
            var offered = RunProcedures.Offered();
            var index = Mathf.Clamp(_runProcedureIndex, 0, offered.Length - 1);
            var chosen = EditorGUILayout.Popup(new GUIContent("Procedure", "Only procedures validated on real clients are listed."),
                index, offered);
            if (chosen != index)
            {
                _requestedRunProcedureIndex = chosen;
            }

            if (offered[index] != RunProcedures.ManualSteps)
            {
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent("Checklist", _runChecklistPath ?? string.Empty),
                    new GUIContent(string.IsNullOrEmpty(_runChecklistPath) ? "(none)" : System.IO.Path.GetFileName(_runChecklistPath)),
                    RunWaitingView.PlainLabel);
                if (GUILayout.Button("Choose...", GUILayout.Width(80)))
                {
                    // After this event: a modal panel in the middle of OnGUI breaks the layout.
                    EditorApplication.delayCall += ChooseChecklist;
                }
            }

            ReadChecklistIds();
            var options = new[] { "None" }.Concat(_runChecklistIds ?? new string[0]).ToArray();
            var leaveIndex = Mathf.Clamp(_runLeaveRejoinIndex, 0, options.Length - 1);
            var leaveChosen = EditorGUILayout.Popup(new GUIContent("Leave and rejoin after",
                "After this item's answer, close the target slot and launch it again (with preconditions)."), leaveIndex, options);
            if (leaveChosen != leaveIndex)
            {
                _requestedRunLeaveRejoinIndex = leaveChosen;
            }

            using (new EditorGUI.DisabledScope(leaveIndex == 0))
            {
                _runTargetSlot = Math.Max(0, EditorGUILayout.IntField(new GUIContent("Target slot",
                    "The slot to close and launch again; one of the slots above."), _runTargetSlot));
            }
        }

        private void ChooseChecklist()
        {
            var start = string.IsNullOrEmpty(_runChecklistPath) ? string.Empty : System.IO.Path.GetDirectoryName(_runChecklistPath);
            var path = EditorUtility.OpenFilePanel("Checklist (vrcrt.checklist JSON)", start, "json");
            if (!string.IsNullOrEmpty(path))
            {
                _runChecklistPath = path;
                _runChecklistIdsPath = null;
                _requestedRunLeaveRejoinIndex = 0;
                Repaint();
            }
        }

        /// <summary>The checklist's item IDs for the popup, read once per chosen file (display only; the runner checks the file).</summary>
        private void ReadChecklistIds()
        {
            if (_runChecklistIdsPath == (_runChecklistPath ?? string.Empty) || Event.current.type != EventType.Layout)
            {
                return;
            }

            _runChecklistIdsPath = _runChecklistPath ?? string.Empty;
            _runChecklistProblem = null;
            _runChecklistIds = string.IsNullOrEmpty(_runChecklistPath)
                ? null
                : RunProcedures.ChecklistItemIds(_runChecklistPath, out _runChecklistProblem);
        }

        /// <summary>What prepare gets for the chosen procedure, or null (with why) when it cannot start yet.</summary>
        private string[] RunProcedureArguments(out string problem)
        {
            problem = null;
            var procedure = SelectedProcedure();
            if (procedure != RunProcedures.ManualSteps)
            {
                return RunProcedures.PrepareArguments(procedure, null, null, null);
            }

            if (_runChecklistIds == null)
            {
                problem = string.IsNullOrEmpty(_runChecklistPath)
                    ? "Choose a checklist file."
                    : "The checklist cannot be used: " + (_runChecklistProblem ?? "not read yet");
                return null;
            }

            var leaveAfter = _runLeaveRejoinIndex > 0 && _runLeaveRejoinIndex <= _runChecklistIds.Length
                ? _runChecklistIds[_runLeaveRejoinIndex - 1]
                : null;
            try
            {
                return RunProcedures.PrepareArguments(procedure, _runChecklistPath, leaveAfter, leaveAfter == null ? (int?)null : _runTargetSlot);
            }
            catch (ArgumentException ex)
            {
                problem = ex.Message;
                return null;
            }
        }

        /// <summary>A button that cannot be pressed while a Deploy and Collect run holds the pairing it was prepared for.</summary>
        private static bool GUILayoutButtonUnlessRunning(string label)
        {
            using (new EditorGUI.DisabledScope(RunController.Active))
            {
                return GUILayout.Button(new GUIContent(label, RunController.Active ? "Not while a Deploy and Collect run is going on." : null));
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
                if (GUILayoutButtonUnlessRunning("Save Pairing"))
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
