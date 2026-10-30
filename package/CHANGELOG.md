# Changelog

## [0.8.1] - Unreleased

Fixes replacing the Windows Bridge with `scripts/install-bridge.ps1`, which stopped twice during the 0.7.1 → 0.8.0 update on 2026-10-31. The Bridge and the Unity package have no functional changes. Follow `docs/setup-windows.md`, step 1 ("版を入れ替える").

### Fixed (Windows scripts)

- `install-bridge.ps1` is saved as UTF-8 with a BOM and no longer starts with `#Requires -Version 7.0`, so Windows PowerShell 5.1 reads its Japanese text and stops, changing nothing, with the command to run instead: `pwsh.exe -NoProfile -ExecutionPolicy Bypass -File …` with the same arguments. `docs/setup-windows.md` now gives that form; running the script inside 5.1 with `&` was stopped by the default Restricted execution policy.
- An existing task whose definition (action, trigger, principal, settings and description) already matches is kept and started, instead of being unregistered and registered again. During the 0.8.0 update, `Unregister-ScheduledTask` failed with "Access is denied" on the task 0.7.1 had registered, after the executable had been replaced, and the script ended without registering or starting anything. When the definition differs and the task cannot be removed, the script lists the differences and how to remove the task, then stops.
- When the task step fails, the script prints the state it stopped in (the executable replaced or not; the task kept, left as it was, removed or registered) and the command to run again.
- The messages for a running Bridge and for a refused replacement include the command to run again.

### Tests

- `scripts/test-install-bridge.ps1` runs with `pwsh` on any OS: the BOM, parsing, no PowerShell 7-only operators, the 5.1 message, and the task-definition comparison with stand-in objects.

## [0.8.0] - 2026-10-31

Day-to-day Bridge management from the Mac, so nobody has to work on the Windows PC once the Bridge is installed and paired. **Update both the Windows Bridge** (`docs/setup-windows.md`, step 1) **and the Unity package**. An existing `config.json` and pairing keep working; either side also works with the other still on 0.7.x, without the new features.

### Added (Bridge)

- `GET /api/v1/bridge`: version, executable SHA-256, host generation, listen URL, certificate fingerprint, the active configuration (paths with the Windows user name masked) and its etag, the etag of `config.json` on disk, the last configuration change, and notes such as "AutoLaunchVrchat is false".
- `GET /api/v1/logs/bridge`: the tail of the Bridge's own log, masked.
- `GET`/`PUT /api/v1/config`: change `AutoLaunchVrchat`, `VrchatClientMode`, `VrchatProfiles`, `VrchatClientCount`, `VrchatMode`, the startup timeouts, `RetainBuilds`, `MaxArtifactSizeBytes` and `LogMarkerPrefixes` within fixed ranges, and pick `VrchatExecutable` from the VRChat.exe found in the user's Steam libraries. `If-Match` with the file's SHA-256, a JSON Merge Patch body, validation through the production options binding, the exact previous file kept as `config.json.bak-<time>` (newest five), and an in-process restart that rolls back to the previous file when the new host cannot start. The listen address and port, the state and Worlds directories and the pairing cannot be changed remotely.
- `POST /api/v1/bridge/restart`: rebuilds the host in the same process (at most once every 10 s); the instance lock and pairing are kept. Hosted services start their work only once Kestrel listens.
- `POST /api/v1/clients/close`: closes the VRChat clients the Bridge launched. Reload launches are recorded too now (`state\reload-clients.json`); changing `VrchatClientMode` closes the Bridge's clients first and refuses when any other VRChat runs.
- `/clients` entries gain `loginState` and `account` (display name, `idDigest`: an HMAC of the usr_ ID under a key that never leaves the PC, and the login time). `GET /api/v1/profiles` keeps the account each Relaunch profile last logged in to and warns when two profiles share one; the warning is added to build results too.
- Build results gain an optional `launch` report (`disabled`, `reusedRunning`, `launched`, `relaunched`, `partial`, `failed`, `notReached`, with counts) and `warnings`. Every 413 answer carries `{error, limitBytes, declaredBytes}`.
- One operation gate: deploys, configuration changes, closing clients and restarts run one at a time; a management request that finds it taken gets 409 `busy`.

### Added (Unity and CLI)

- `scripts/vrcrt.sh`: `doctor`, `config get`, `config set KEY=VALUE…`, `restart`, `close-clients`, `profiles`.
- The Remote Test window shows the Bridge version, its notes and each client's login state and account, and warns when the Bridge and package versions differ.
- Remote Build fails before uploading a world larger than the Bridge accepts, and shows the limit from a 413 answer. A result whose `launch` is `disabled` says that no VRChat was launched.

### Fixed (Bridge)

- Current VRChat builds no longer log "User Authenticated: <name> (usr_…)"; they log "[Authentication - <tag>] Preforming post login checks for <name> (usr_…)". The Bridge now reads that line too, so a logged-in client is no longer reported as "the login of profile N may have expired" (seen on v0.7.1, 2026-10-30), and its account is shown.

### Not included

- The Bridge does not update itself: replacing the executable stays a step on Windows.

### Tests

- Symbolic-link tests end early on Windows when the user cannot create links (Developer Mode off), instead of failing.

## [0.7.1] - 2026-10-30

Fixes the Windows Bridge refusing worlds over 30 MB. The fix is in the Bridge only and the Unity package has no functional changes, so **update the Windows Bridge** (`docs/setup-windows.md`, step 1). An existing `config.json` and pairing keep working.

### Fixed (Bridge)

- A build larger than 30,000,000 bytes (about 28.6 MiB) was refused with HTTP 413 Payload Too Large on `PUT /builds/{id}/artifact`, before `MaxArtifactSizeBytes` (500 MiB by default) was ever checked: Kestrel's default request body limit applied to the artifact upload too. The artifact route now raises the limit to `MaxArtifactSizeBytes` for its own request; every other route keeps the default. Update the Windows Bridge only (`docs/setup-windows.md`, step 1).

## [0.7.0] — LAN Direct (breaking)

- Replaced the legacy transfer and observation path with certificate-pinned, token-authenticated HTTPS. Bridge and Unity must be updated together.
- Interactive `.vrcw` uploads stream through `UploadHandlerFile`; results, client status, log tails and markers use the same API.
- Added an explicit full-log export for a selected client generation and observation epoch, with redaction and SHA-256 verification.
- Added scriptable build-result lookup, verified full-log export for all running clients, and a one-command Unity batch build/deploy/evidence workflow.
- Removed the old share setting, environment variable, CLI flag, transport and Windows shared-folder setup.


## [0.6.0] - 2026-10-18

The Windows Bridge now publishes every VRChat client's log, state and marker lines, not just the first client's (client-observation plan P1), and the Log Viewer and the new `scripts/vrcrt.sh` on the Mac read them. It needs no new share permission and the Mac sends nothing new. To get them, **update both the Windows Bridge** (`docs/setup-windows.md`, steps 1 and 6) **and the Unity package** (in VCC or ALCOM); either one also works with the other still on 0.5.x, and the Log Viewer then shows only the first client's log, as before. An existing `config.json` keeps working as before (`LogMarkerPrefixes` is optional and defaults to `["[VRCRT]"]`).

### Added

- Every client (up to 4) gets a slot number (0–3) that stays while it runs, across Bridge restarts (`%LOCALAPPDATA%\VRCRemoteTest\state\observation-roster.json`, observation only). After a Reload session's slot 0 client exits, the client left keeps slot 1.
- `logs/clients/slot-{n}.log`: each slot's last 200 lines, picked by the same rules as before. Restart Manager is asked about each file once per poll however many slots ask.
- `logs/clients/slot-{n}.markers.log`: the lines whose message starts with one of `LogMarkerPrefixes` (new in `config.json`, default `["[VRCRT]"]`, up to 8 prefixes of 1–32 characters), collected from the start of the client's session so they never scroll out of the tail. Each record is `<seq>\t<line>` with control characters escaped; the first line is a JSON header with the slot, `clientGeneration`, `observationEpoch`, `catchingUp` and how many old records were dropped (at most 2,000 lines or 1 MiB). When a new client takes the slot, the previous one's file moves to `slot-{n}.markers.prev.log`.
- `status/vrchat-clients.json`: per slot, the PID, whether it runs, `state` (`starting` / `joining` / `joined` / `left` / `unknown`), the instance, whether it is in the Error World, the players, whether it is master, VRChat's Logging level, whether `User Authenticated` was logged, `seq` positions and recent events. Read from Logging "Full" lines whose message matches exactly; with Logging "errors only" or "off" the state is left out and a notice says to set it to Full. `vrchat-status.json` keeps its shape.

### Changed

- The log copies (`vrchat-latest.log` and the slot logs) split lines at CRLF only. A world's `Debug.Log` text keeps its own LFs and CRLFs, so a line after a lone LF is a continuation, and continuation lines are now indented with four spaces. Before, every LF started a line of its own, so a header a world wrote after an LF looked like a real line.
- User IDs (`usr_…`), Windows profile paths, IP addresses and authentication values are masked before anything from a log, or a build result's error message, reaches the share. Display names and world information stay.
- The Log Viewer's diagnostic lines name the slot (`The VRChat client in slot 0 (PID …) has not created …`, `Could not match an output_log_*.txt file to the VRChat client in slot 0 …`) instead of "the first VRChat client".

### Added (Unity package and Mac script)

- The Log Viewer switches between `Log` and `Markers`, and between `Latest` (vrchat-latest.log, as before) and `Slot 0`–`Slot 3`. `Markers` shows a slot's marker lines, or `All slots` merged by time, then slot, then seq. Choosing a slot shows its state (PID, state, instance, players, master, Logging level) and the Bridge's warnings, and a client list older than 15 s is flagged. With a Bridge older than v0.6.0 the viewer stays as it was.
- `IRemoteTransport` gains `PollVrchatClients()`, `PollClientLog(slot)` and `PollClientMarkers(slot)` (a bounded read of up to 1.25 MiB, separate from the log's 512 KiB guard).
- `scripts/vrcrt.sh` reads the same files from the mounted share for agents and terminals: `status`, `log <slot> [--markers]`, `log all --markers`, `cursor`, `wait-marker <slot|all> <regex> <seconds> [--after <cursor>]` and `wait-same-instance <seconds> [--slots] [--after <cursor>]`. It needs python3 3.9 or later (standard library only), escapes control characters, and stops a regular expression after 2 s. `scripts/test-vrcrt.sh` checks it against a fake share.

## [0.5.1] - 2026-10-18

Fixes a Windows Bridge bug that could overwrite a successful build's result with a failure. The fix is in the Bridge only and the Unity package itself has no functional changes, so **update the Windows Bridge** (`docs/setup-windows.md`, steps 1 and 6). An existing `config.json` keeps working as before.

### Fixed

- When the Bridge took in a manifest within milliseconds of the Mac placing it, the Mac's SMB client could recreate an empty file under the same name in `incoming` (it writes an extended attribute to that name, and Windows creates the file). The Bridge read that file as a new build and overwrote the `deployed` result with `MANIFEST_INVALID`, although the world had been deployed and Unity had already shown the build as succeeded. The cause and the checks on Windows are in `docs/validation/smb-late-empty-manifest.md`.
- A manifest the Bridge cannot read (empty, invalid JSON, missing or invalid `buildId`, over 64 KiB) whose file name is a build that was already processed (its `buildId` is in the Bridge's archive or has a result file) is now deleted without writing a result, and the Bridge logs a warning (`Discarding ...: ..., and build <buildId> was already processed, so its result is kept.`). For a build that was not processed, it still fails with `MANIFEST_INVALID`.
- A duplicate upload of a processed build no longer rewrites its result file (`Build <buildId> was already processed; consumed the duplicate upload and kept its result.`). `BUILD_ALREADY_PROCESSED` is written only when the build has no result file. The duplicate check now runs right after the `buildId` is checked, so a duplicate with a disallowed artifact file name no longer overwrites the result either.

## [0.5.0] - 2026-10-18

The Windows Bridge can now put VRChat clients on separate accounts into one instance (`VrchatClientMode: Relaunch`), and the Log Viewer finds the first client's log more reliably. The Unity package itself has no functional changes, so **update the Windows Bridge** (`docs/setup-windows.md`, steps 1 and 6) to get them. An existing `config.json` keeps working as before.

### Added

- `VrchatClientMode: Relaunch` with `VrchatProfiles` (1–4 profile numbers, 0–99) in the Bridge's `config.json`, for clients on separate VRChat accounts (`docs/setup-windows.md`, step 9-3). Clients on different accounts split into separate instances when `--watch-worlds` reloads them, so on every deploy the Bridge closes the clients it launched, deploys, and starts one client per profile again with `--url` and `--profile` (without `--watch-worlds`); they all enter the same instance. It is an explicit opt-in (the default stays `Reload`) and requires `AutoLaunchVrchat`. Log in to each profile once beforehand (`start-vrchat-dev.ps1 -Profiles <n>`). In this mode Unity's Preflight shows "VRChat is running without --watch-worlds" while everything works; this is expected.
- The Bridge records the clients it launches in `%LOCALAPPDATA%\VRCRemoteTest\state\managed-clients.json` and only closes a process whose PID, creation time, session and name all match that record. Any other VRChat (started by hand, not recorded, or one the Bridge cannot check) makes the build fail with `VRCHAT_UNMANAGED_RUNNING` without closing anything.
- While Relaunch closes and restarts the clients, the Log Viewer shows a one-line notice instead of the previous session's log.

### Changed

- The Log Viewer picks the first client's log by asking Windows Restart Manager which process has each log file open, so it can show the log even when VRChat creates it late under heavy CPU load. A file that several clients write to is never shown as one client's log. When Restart Manager gives no answer, the times in the file names are used as before.
- Recommended `Result Timeout` in the Unity window: 120 s or more, 180 s when sending builds back to back (`docs/setup-windows.md`, step 9).

### Fixed

- `install-bridge.ps1` retries the final replacement of the executable (up to 10 times, 0.5 s apart) when Windows briefly refuses it right after the Bridge stopped, instead of failing.

## [0.4.0] - 2026-10-17

The Windows Bridge can now run several VRChat clients on the same account, like the SDK's Number of Clients. The Unity package only adds guidance for one new error code, so **update the Windows Bridge as well** (`docs/setup-windows.md`, steps 1 and 6). An existing `config.json` keeps working as one client.

### Added

- `VrchatClientCount` (1–4, default 1) in the Bridge's `config.json`: with `AutoLaunchVrchat`, the Bridge launches that many VRChat clients on the same account into the same instance, and every deploy reloads all of them (`docs/setup-windows.md`, step 9-1). Clients on different accounts are not supported.
- Error code `VRCHAT_UNMANAGED_RUNNING`: a VRChat process whose state the Bridge cannot check is running, so the build fails without launching or closing anything. The Unity window shows guidance for it (close that VRChat, then build again).
- Only one Bridge runs per Windows account; a second one logs `Another VRC Remote Test Bridge is already running` and exits.
- `start-vrchat-dev.ps1 -Clients <n>` starts several clients by hand.

### Fixed

- The Bridge stopped updating `logs/vrchat-latest.log` and `status/vrchat-status.json` (`UnauthorizedAccessException`) while the Mac's SMB client held them open. It now renames a new file over them (POSIX semantics), so the Log Viewer and Preflight keep updating.
- With several clients, the Log Viewer shows the first client's log and never another client's. Each client is launched only after the previous ones have created their log files (at most 60 s per cold start), and until the first client's log exists the Log Viewer shows a one-line notice instead of the previous session's log.

### Changed

- Cold start: when no VRChat is running, the Bridge deploys first and launches VRChat with `--url` pointing at the deployed world, so the first build no longer waits for VRChat to reach its home screen.
- Recommended `Result Timeout` in the Unity window: 90 s or more with auto-launch, 120 s when sending builds back to back (`docs/setup-windows.md`, step 9).
- `startTimeUtc` in `status/vrchat-status.json` is written in UTC (the same instant as before; Unity reads it unchanged).

## [0.3.0] - 2026-10-17

Security hardening of the Windows side. The Unity package itself has no functional changes, but **existing Windows setups must be migrated** (`docs/setup-windows.md`, step 14): the share permissions and the location of the Bridge's state have changed, and the installer now requires a trusted hash.

### Security

- Bridge state (`processing`, `archive`, `failed`) moved out of the SMB share to `%LOCALAPPDATA%\VRCRemoteTest\state`. Uploads are copied (never moved) into new private files with size limits, and the same private snapshot is validated and installed, so a share writer can no longer swap a validated build or forge Bridge state.
- `install-bridge.ps1` requires `-ExpectedSha256`, verifies a protected candidate copy before replacing the executable, only installs under `%LOCALAPPDATA%` into a directory no one else can write, and keeps a disabled task disabled.
- Releases now include `VRCRemoteTest.Bridge.exe`, built from the tagged commit by GitHub Actions with signed build provenance (artifact attestation) and its SHA-256 in the release notes. Windows users download and verify it directly (`gh attestation verify`, GitHub CLI 2.102.0+), so neither the executable nor its hash has to come from the Mac.
- The Bridge rejects oversized manifests, unknown manifest fields, symbolic links in the share, and artifacts whose actual size exceeds `MaxArtifactSizeBytes`; one bad upload no longer stalls later builds.

### Changed

- Windows setup documentation: dedicated SMB account required, directional share permissions (`incoming` and `_bridge-drop` writable; outputs read-only), encrypted share, OSC exposure check, disk usage checks, and a migration/rollback procedure. Scripts are run only from a local copy on Windows, never from the Mac's share.
- Successful builds no longer keep a duplicate artifact in the Bridge's archive; failed artifacts are limited to `RetainBuilds`.

## [0.2.0] - 2026-09-03

### Added

- Unity UI: `VRC Remote Test` window (`VRChat SDK > VRC Remote Test`) with preflight status, build target check, action buttons, progress, last-deployment result, and a settings foldout.
- VRChat process monitoring: Bridge reports whether VRChat is running and whether it was launched with `--watch-worlds`, surfaced in the Unity window.
- VRChat auto-launch: Bridge can start VRChat with `--watch-worlds` automatically when a build arrives and VRChat isn't already running (`AutoLaunchVrchat`, opt-in).
- VRChat log reader/viewer: Bridge streams a bounded snapshot of the newest `output_log_*.txt`; Unity displays it with category filtering, newest-first ordering, and auto-refresh.
- Moonlight integration: `[Open Moonlight]` button and an optional "focus Moonlight after a successful deploy" setting.
- Actionable error guidance: failed deployments show a short, specific next-step alongside the raw error code.
- Bridge auto-start on Windows logon: `scripts/install-bridge.ps1` / `scripts/uninstall-bridge.ps1` register/unregister a non-elevated Windows Task Scheduler task.

### Fixed

- VRChat startup settle delay was too short by default on real hardware; it is now configurable and defaults to 30s.
- Several real-hardware issues in the Log Viewer (category filtering, auto-scroll, layout when the window is docked).
- `install-bridge.ps1` failed to re-copy an already-running Bridge executable on a repeat run; it now skips the copy when the source and destination are identical.

### Changed

- Package id renamed from `com.local.vrc-remote-test` to `com.github.graaaaaaa.vrc-remote-test` ahead of public VPM distribution.

## [0.1.0] - 2026-09-02

### Added

- Initial implementation: SDK adapter, build coordinator, atomic SMB transport, result polling.
- Menu item: `VRChat SDK > Remote Build`.
- Headless entry point for `-executeMethod` / CI invocation: `VRCRemoteTest.RemoteBuildCommand.ExecuteRemoteBuildHeadless`.
