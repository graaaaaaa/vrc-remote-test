# Changelog

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
