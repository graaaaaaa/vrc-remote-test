# Changelog

## [Unreleased]

**Update the Unity package only** (the runner is in it); the Bridge does not change. Contract: `docs/plans/end-marker-contract.md`.

### Added (Mac scripts)

- `vrcrt_runner.py --end-marker REGEX [--end-slots LIST] [--end-wait SECONDS]` (deploy-collect only; checked on Windows with two Desktop clients, `docs/validation/end-marker.md`): before collecting the evidence, waits until each named slot logged a marker matching REGEX in its current join (60 to 1500 s, default 900). A line counts once the client list has read through it, and its markers are saved under `evidence/end-marker/` then. `results.endMarker` is PASS, or UNKNOWN (`endMarkerTimeout`, `markersDropped`, `endMarkerUndecided`) with the evidence still collected; another build's tag does not wait. The marker is what the world wrote; the test is still not judged.

### Changed (Mac scripts)

- `continue` refuses a run whose `run.json` procedure no longer matches the hash prepare recorded (`procedureInvalid`), also for a valid but changed value.

## [0.13.1] - 2026-11-26

**Update the Unity package only**; the runner and the Bridge do not change.

### Changed (Unity)

- The window's Run offers manual-steps: choose the checklist file, the item to leave and rejoin after (or None) and the target slot. Checked on Windows with two Desktop clients (`docs/validation/manual-steps.md`, check 4). VR and four clients are still not checked.

## [0.13.0] - 2026-11-26

R3 (`docs/plans/r3-contract.md`): a test with steps a person does by hand. The runner asks the items of a checklist one at a time, waits for each answer, and records it with every client's log position and markers; a declared client can leave and rejoin after an item. **Update the Unity package only**; the Bridge does not change. Checked on Windows with two Desktop clients (`docs/validation/manual-steps.md`); VR, four clients and starting it from the window are not checked yet, so the window answers a waiting run but does not offer the procedure in Run.

### Added (Mac scripts)

- `vrcrt_runner.py --procedure manual-steps --checklist FILE`: after deploy-collect, asks each item of a `vrcrt.checklist` file (up to 20, shown as plain text) and waits for an answer (ok, ng or skip, with a memo; `--answer-wait`, default 1200 s). While it waits it checks every 5 s that the clients and the Bridge stay as they were ready; a change stops the run UNKNOWN. Each answer is recorded with the time, each client's log range and markers; the full logs are collected at the end. `results.manualSteps` is the answers as given (PASS all ok, FAIL any ng, otherwise UNKNOWN); `testVerdict` stays `notJudged` and an answer never turns a tool result into PASS. The run's limit is `--run-minutes` (30 to 120, default 90); other procedures keep 30 minutes.
- `--leave-rejoin-after ITEM --target-slot N [--remaining-marker REGEX]`: leave-rejoin's preconditioned close and launch after that item's answer (Bridge 0.12.0 and later, Relaunch); later items are asked of the new client.
- `vrcrt_runner.py waiting --run DIR [--json]` shows what a run waits for; `answer --run DIR --wait-id ID --item ITEM ok|ng|skip [--memo TEXT]` answers it, only while that wait is open and its runner is alive, once per item (an agent can relay the answer of a person in VR).
- `leave_rejoin_steps` hands back the rejoined client and the revision of its own launch; leave-rejoin's results and evidence do not change.

### Added (Unity)

- The window shows a run of this Bridge that is waiting for an answer, also one started from the terminal or before the Editor restarted: the item, the time left, a memo and OK / NG / Skip, sent through `vrcrt_runner.py answer`. The summary of a manual-steps run counts the answers.

### Changed (Unity)

- The window reads the Bridge's claim and the run's `run.json` without following links and with size limits.

## [0.12.0] - 2026-11-26

R1b (`docs/plans/r1-automation.md` §5, contract `docs/plans/r1b-contract.md`): a single-client close or launch can carry a precondition, so one prepared from an earlier view does nothing if the Bridge or the client changed since; the runner gains the leave-and-rejoin procedure on top of it. **Update both the Unity package and the Windows Bridge.** Checked on Windows with two VRChat clients (`docs/validation/slot-preconditions.md`); the export cleanup and four clients are not checked on Windows yet. Also two small changes found in the Windows check of 0.11.0.

### Added (Bridge)

- `GET /api/v1/bridge` adds `bridgeInstanceId` (random per process), `operationRevision` (moves on every deploy, close or launch attempt, configuration write and host start; never on a refusal) and `currentBatch` (`buildId` and `launchedSlots` of the Relaunch batch). `GET /api/v1/capabilities` adds `features: ["slotPreconditions.v1"]`; `apiVersion` stays 1.
- `POST /api/v1/clients/{slot}/close` and `/launch` take an optional JSON body `{"expect": {...}, "operationId": "..."}`. An empty body is the old request. Anything but exactly the contract's shape is 400 `invalidPrecondition` (never read as no body). Inside the operation gate the Bridge compares the process, host, configuration, revision and batch, then the slot's client generation; a difference is 412 `preconditionFailed` and nothing is done. Answers add `operationRevision`, `bridgeInstanceId`, `operationId` and, for close, the closed `clientGeneration`.
- The Bridge deletes its own log exports (`state\exports`) a day after writing them, at start and hourly.

### Added (Unity)

- With a Bridge that reports `slotPreconditions.v1`, Close and Launch in the clients table carry the row's client generation and a recent `/bridge` read as a precondition; a 412 says the list changed and asks to refresh (never retried). Without the feature, as in 0.11.

### Added (Mac scripts)

- `vrcrt.sh close-client`/`launch-client` take `--expect @FILE`, `--operation-id` and `--json`: exit 1 on a failed precondition, 2 on a malformed one (nothing sent), 3 when the Bridge does not support it (nothing sent). Without `--expect` nothing changes.
- `vrcrt_runner.py --procedure leave-rejoin --target-slot N [--remaining-marker REGEX]`: after deploy-collect, closes the target slot with a precondition, waits until it is gone and the other slots are unchanged, launches it again with a precondition, and waits until it is ready in the same instance (and shows the build tag, when the world logs one). Nothing is ever resent; any surprise stops UNKNOWN, recording that the client may be left closed. Evidence before the close, after it and after the rejoin is kept apart.
- deploy-collect on a Bridge with the feature in Relaunch also checks that the launch plan is this deploy's and that nothing else happened on the Bridge after the clients were ready.

### Changed (Unity)

- Deploy and Collect has **Wait for the build tag** (on by default). Off, the runner looks for the tag once instead of waiting up to 120 s, for worlds that log none; their world build match stays UNKNOWN either way.
- In the VRChat Clients table, Launch is off for a slot past the configured profiles (the Bridge starts only the slots of its last deploy and would refuse). A running client of such a slot can still be closed.

### Changed (Mac scripts)

- `vrcrt_runner.py prepare` and `run` take `--world-build-wait SECONDS` (0 to 600, default 120; 0 looks once). It is recorded with the procedure in `run.json`.

## [0.11.0] - 2026-11-26

Deploy, wait until every listed VRChat client is ready, and collect the evidence with one button in the Unity window (or one command), with a structured record of what happened. The Bridge has no changes, but the Remote Test window warns while their versions differ, so **update both the Unity package and the Windows Bridge** (`docs/setup-windows.md`, step 1, "版を入れ替える"). An existing `config.json` and pairing keep working. Checked on Windows with Bridge 0.10.0 and two VRChat clients (`docs/validation/runner-deploy-collect.md`); four clients are not checked yet. The plan behind this release is `docs/plans/r1-automation.md` (R1a).

### Added (Unity)

- Every build and redeploy saves a deploy result, `~/Library/Application Support/VRCRemoteTest/deploy-results/<buildId>.json` (`attempt-<id>.json` when it stopped before a build ID), owner-only and never replaced. It keeps "deployed" and "launched" apart: deployed with a partial, failed or unreached launch, counts that disagree with the outcome, a missing launch report or a hash or build ID that differs are UNKNOWN; a real partial start is FAIL; a refusal before the commit is FAIL; a lost answer after sending is UNKNOWN; cancelling is CANCELED (after sending, not a withdrawal).
- `RemoteBuildApi.BuildAndDeployAsync` / `DeployLastBuildAsync` return the outcome through the shared coordinator, with an optional run folder and world build tag; `RemoteBuildApi.ActiveWorldBuildTag` is the tag a project's build preprocessing can embed so that the world logs `vrcrt.build=<tag>`. The tag of a fresh build follows its bytes to Deploy Last Build and is dropped when they change. The existing `RemoteBuildCommand`/`RemoteBuildCoordinator` members and log lines are unchanged.
- VRChat Clients lists each slot (PID, profile, state, instance, login, error world, generation, epoch) with Close and Launch (Bridge 0.10.0 and later, Relaunch), read like `vrcrt.sh close-client`/`launch-client` and never retried. The Bridge acts on the client it has recorded in the slot when the request arrives; a change after the list was read is not caught (the confirmation says so).
- Deploy and Collect: Run prepares with the packaged runner, builds (or redeploys) in this Editor, and hands the run to the runner, which, once it has taken the run over (run.json `running`), finishes even if the Editor closes. Cancel, a pairing change after prepare, and a script reload are all recorded. Results: deploy, launch, client readiness, world build match, evidence; the test itself is never judged.

### Added (Mac scripts)

- `package/Tools~/vrcrt_runner.py` (Python 3.9+, standard library only): `prepare`, `continue [--detach]`, `run --build headless`, `recover`, `prune`. The procedure deploy-collect waits until every listed slot is ready (Relaunch: a new generation; Reload: a join that began after the cursor in the same epoch; log read through, joined in one instance, not in the error world, logged in; a likely logged-out profile is named), checks this build's tag in each client's current join (from its Joining line: VRChat logs a world's load-time markers before "Finished entering world"), and saves markers and full logs per slot, generation and epoch with their sizes and hashes in `run.json`. PASS, FAIL, UNKNOWN or CANCELED; a missing or stale observation is never success. Runs live in `~/Library/Application Support/VRCRemoteTest/runs/` (0700); Git, iCloud, `~/Documents` and `~/Desktop` are refused. One run at a time per Bridge among runners on this Mac that use the same run root and the same Bridge URL and certificate pin; children run in their own process groups and are always stopped.
- `vrcrt.sh` now lives in `package/Tools~/` so that the package carries it; `scripts/vrcrt.sh` passes everything through unchanged. New, read-only: `bridge --json`, `log <slot> --markers --json` (cut and withheld records flagged), `export-log <slot> <dir> --generation G --epoch E`. The regular-expression child no longer inherits the environment (it gets only `PYTHONIOENCODING=utf-8`).

### Changed (Mac scripts)

- `scripts/vrcrt-run.py` reads the build ID from Unity's deploy result (older packages: from the log, with a warning) and refuses an `--output-dir` inside a Git work tree or iCloud. Otherwise unchanged.

### Tests

- Unity EditMode: 249 tests (classification, result files, attempts and cancellation, single-client control, the runner process). `scripts/test-vrcrt.sh` now also runs `test-vrcrt-runner.sh` (67 checks: readiness, tags, claims, cancellation, Ctrl-C, stubborn descendants, recovery, prune) and `test-vrcrt-run.sh` (16 checks of the old interface).

### Not included

- A guard that ties a single-client close or launch to the client last seen, and the automatic leave-and-rejoin procedure: R1b, which needs a Bridge change.

## [0.10.0] - 2026-11-24

Closing one VRChat client and starting it again from the Mac, for tests where one player leaves and rejoins the same instance while the others stay (Relaunch mode). The Unity package has no functional changes, but the Remote Test window warns while their versions differ, so **update both the Windows Bridge** (`docs/setup-windows.md`, step 1, "版を入れ替える") **and the Unity package**. An existing `config.json` and pairing keep working. Checked on Windows on 2026-11-24 with two Relaunch clients on separate accounts: `close-client 0` closed only slot 0, and `launch-client 0` about a minute later started the same profile again, which rejoined the same instance with a new generation, observation epoch and its own log (`docs/validation/slot-close-relaunch.md`).

### Added (Bridge, LAN API)

- `POST /api/v1/clients/{slot}/close` closes only the client the Bridge launched in that slot (its launch order), through the handle it was matched with on PID, creation time, session and name, and removes only its entry from `managed-clients.json`; the other clients and entries are left as they are. It refuses (`slotMismatch`) while the observed slot shows another client.
- `POST /api/v1/clients/{slot}/launch` starts that slot again with the arguments the Bridge used for it in the last deploy (world, room id, profile, VR flag). They are kept in memory only, so a configuration change or a restart forgets them until the next deploy (`noLaunchPlan`). The launch is spaced like a batch's, refuses while any VRChat the Bridge did not launch runs, writes the record before starting, and records a client that started even when the request is dropped; one it cannot record is reported with its PID (500 `launchedNotRecorded`).
- Refusals change nothing and answer `{error, problems}`: 400 `unknownSlot`, otherwise 409 (`notRelaunchMode`, `recordCorrupt`, `alreadyRunning`, `othersRunning`, ...). Both routes take the operation gate like the other management routes.

### Changed (Bridge, observation)

- A client started on its own is matched to its log only by the file Restart Manager shows it alone has open, never by the times in the file names, so it cannot be shown the closed client's late log or another client's. This lasts for the Bridge host's lifetime (a refused deploy keeps it); after a restart such a client is matched like any other.
- While a client is between its start and its record, the roster gives unrecorded newcomers no slot, and a recorded client waits for its launch order's slot while that slot cannot be checked instead of taking another one: a relaunched client gets one new generation and observation epoch in its own slot.

### Added (scripts)

- `vrcrt.sh close-client <slot>` and `vrcrt.sh launch-client <slot>` (exit 0 done, 1 refused or not done, 3 no or invalid answer or a Bridge older than v0.10.0). `launch-client` waits up to 90 s and `close-client` up to 45 s for the whole answer, and neither retries: with no answer, whether the client started or closed is unknown until `status --json` shows it.

## [0.9.0] - 2026-11-01

Choosing the VRChat clients from the Unity window. The new section uses the management API of the Bridge v0.8.0 and later, and the Bridge has no functional changes, but the Remote Test window warns while their versions differ, so **update both the Unity package and the Windows Bridge** (`docs/setup-windows.md`, step 1, "版を入れ替える"). An existing `config.json` and pairing keep working. Checked on 2026-11-01 with four clients on separate accounts (Relaunch `0, 1, 2, 3`, all in one instance) and with a switch to two Reload clients and back.

### Added (Unity)

- The Remote Test window's Bridge section gains "VRChat Clients": the Bridge's current client settings, a mode (Reload: one account, 1 to 4 clients; Relaunch: one client per VRChat profile, e.g. `0, 1, 2, 3` for four accounts), and `Apply to Bridge`, which writes `VrchatClientMode`, `VrchatClientCount` and `VrchatProfiles` through `PUT /api/v1/config` with the etag just read, then waits up to 90 s for the restarted Bridge host to report the change applied, rolled back or failed, and shows its problems or notes. Every apply asks first and says when the Bridge will close the clients it launched (a mode change; it refuses while any other VRChat runs); nothing is written when the running Bridge's mode is no longer the one confirmed. Each step has its own deadline (reading 10 s, writing 45 s, the restart 90 s), so the window is never held longer than about 2.5 minutes by a Bridge that stops answering, and `Stop waiting` ends the wait at once; either way the result says that a change the Bridge accepted may still be applied. The mode is a pair of buttons, not a popup (the editor once crashed inside the popup's native menu on the Mac), and the Bridge section is repainted every 2.5 s, so its rows no longer change only when the next click redraws the window and move the control under the pointer; the Slot rows stay up to 60 s after the clients list stops updating, instead of 15 s. A profile the Bridge has not launched yet reads "account not seen yet", since a login made by hand is seen only once the Bridge launches it.
- For Relaunch, each listed profile shows the account it last logged in to (`GET /api/v1/profiles`), or how to log it in on Windows once, plus the Bridge's warning when two profiles share an account. Three or more clients with a Result Timeout under 180 s get a warning.
- `Close Bridge's VRChat` closes the clients the Bridge launched (`POST /api/v1/clients/close`), e.g. so that a new Reload count is launched by the next deploy.
- `RemoteBuildCoordinator.ApplyVrchatClientSettingsAsync` and `CloseVrchatClientsAsync` hold the same lock as a build, so a build and a settings change never overlap; `GetVrchatProfilesAsync` only reads.

## [0.8.1] - 2026-10-31

Fixes replacing the Windows Bridge with `scripts/install-bridge.ps1`, which stopped twice during the 0.7.1 → 0.8.0 update on 2026-10-31. The Bridge and the Unity package have no functional changes, but the Remote Test window warns while their versions differ, so **update both the Windows Bridge** (`docs/setup-windows.md`, step 1, "版を入れ替える", with the fixed script) **and the Unity package**. An existing `config.json` and pairing keep working.

### Fixed (Windows scripts)

- `install-bridge.ps1` is saved as UTF-8 with a BOM and no longer starts with `#Requires -Version 7.0`, so Windows PowerShell 5.1 reads its Japanese text and stops, changing nothing, with the command to run instead: `pwsh.exe -NoProfile -ExecutionPolicy Bypass -File …` with the same arguments. `docs/setup-windows.md` now gives that form; running the script inside 5.1 with `&` was stopped by the default Restricted execution policy.
- An existing task whose definition (action, trigger, principal, settings and description) already matches is kept and started, instead of being unregistered and registered again. During the 0.8.0 update, `Unregister-ScheduledTask` failed with "Access is denied" on the task 0.7.1 had registered, after the executable had been replaced, and the script ended without registering or starting anything. When the definition differs and the task cannot be removed, the script lists the differences and how to remove the task, then stops.
- The cause of that "Access is denied": the task had been registered from an elevated shell, so its owner was Administrators and the user had read access only (`O:BA…(A;;FR;;;<user>)`). `install-bridge.ps1` now refuses to run elevated, changing nothing. It reads the existing task's security descriptor first, and when the task must be registered again but the user cannot delete it, it stops before replacing the executable, with the commands to fix it (stop the Bridge, which keeps running when its task is removed; unregister from an elevated PowerShell; then run the script again without elevation). When the definition matches, it keeps such a task and says how to fix it for later.
- When the task step fails, the script prints the state it stopped in (the executable replaced or not; the task kept, left as it was, removed or registered) and the command to run again.
- The messages for a running Bridge and for a refused replacement include the command to run again.
- `uninstall-bridge.ps1`, `deploy-test.ps1` and `start-vrchat-dev.ps1` get the same treatment: a BOM, no `#Requires`, and under 5.1 a `pwsh.exe -NoProfile -ExecutionPolicy Bypass -Command "& '…' …"` line with the same arguments. They use `-Command` because `-File` cannot pass an array: `-Profiles 0,1` would arrive as the single number 1.

### Tests

- `scripts/test-windows-scripts.ps1` runs with `pwsh` on any OS: for every script the BOM, parsing, no PowerShell 7-only operators, and the 5.1 message run in a child pwsh, including that its command binds the same arguments; for `install-bridge.ps1` also the task-definition comparison with stand-in objects and the reading of a task's security descriptor, including the one seen on 2026-10-31.

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
