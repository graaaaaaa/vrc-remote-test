# 1台を閉じて起動し直す（close-client / launch-client）の実機の確かめ

Bridge v0.10.0 の `POST /api/v1/clients/{slot}/close` と `/launch`（`vrcrt.sh close-client` / `launch-client`）を Windows 実機で確かめる手順と、その記録です。目的は、vrchat-commander の試験（ADR-193 D6 の `--dice-test 2`、ADR-173、ADR-190）で、テーブルの持ち主の台だけが抜けて、約40秒後に同じ台が同じインスタンスへ途中参加し直す場面を、Mac だけで作れることです。これまでは Windows で `Stop-Process <PID>` と、同じ引数での `Start-Process` を手でしていました。

Mac の単体試験（`dotnet test`、`bash scripts/test-vrcrt.sh`）は通っています。ここでは Mac では確かめられないことを見ます。本物の VRChat が閉じること、同じ引数で起動し直した台が同じインスタンスに入ること、Restart Manager が新しい台のログを答えること、slot・PID・世代・EPOCH の変わり方です。

## 前提

- Windows に VRChat と .NET 10 SDK があり、Bridge のリポジトリがチェックアウトしてある。
- Relaunch の profile 2つ（例: `0` と `1`）に、別々のアカウントで一度ログイン済み（`docs/setup-windows.md` の4）。
- Mac で `VRCRT_BRIDGE_URL` と `VRCRT_CERT_SHA256` を設定済み。`vrcrt.sh doctor` が通る。
- VRChat の Logging は「完全」。

## 1. Windows: 新しい Bridge を入れる

ブランチ `claude/vrc-individual-client-control-5215a6` を取得して、exe を作ります（これまでの実機の確かめと同じ作り方です）。

```powershell
git fetch origin claude/vrc-individual-client-control-5215a6
git switch --detach FETCH_HEAD
dotnet publish bridge/src/VRCRemoteTest.Bridge/VRCRemoteTest.Bridge.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o $env:TEMP\vrcrt-bridge
(Get-FileHash $env:TEMP\vrcrt-bridge\VRCRemoteTest.Bridge.exe -Algorithm SHA256).Hash
```

今の exe と `config.json` を退避してから、Bridge を止めて入れ替えます。

```powershell
$backup = Join-Path $env:LOCALAPPDATA ("VRCRemoteTest\slot-test-backup-" + (Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory $backup | Out-Null
Copy-Item (Join-Path $env:LOCALAPPDATA 'Programs\VRCRemoteTest\Bridge\VRCRemoteTest.Bridge.exe') $backup
Copy-Item (Join-Path $env:LOCALAPPDATA 'VRCRemoteTest\config.json') $backup
```

```powershell
Stop-ScheduledTask -TaskName VRCRemoteTestBridge
```

```powershell
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-bridge.ps1 -ExpectedSha256 <上の SHA-256> -SourcePath $env:TEMP\vrcrt-bridge\VRCRemoteTest.Bridge.exe -StartNow
```

Windows の Smart App Control が新しい exe を止めたときは、エクスプローラーから exe を手で起動して許可するか、タスク スケジューラで `VRCRemoteTestBridge` を手で実行してください。

## 2. Mac: 版と設定を確かめる

```bash
scripts/vrcrt.sh doctor
```

- 版が 0.10.0 相当（このブランチの commit）で、実行体の SHA-256 が1の値と同じ。
- `VrchatClientMode = Relaunch`、`VrchatProfiles = [0, 1]`、`AutoLaunchVrchat = True`。違えば `vrcrt.sh config set VrchatClientMode=Relaunch VrchatProfiles='[0,1]' AutoLaunchVrchat=true`。

## 3. 2台を起動する

Unity の Remote Build & Test（または batch の配置）で1回配置し、2台が起動するのを待ちます。

```bash
scripts/vrcrt.sh wait-same-instance 180 --slots 0,1
```

```bash
scripts/vrcrt.sh status --json > before.json
```

`before.json` の slot 0 と slot 1 の `processId`、`clientGeneration`、`observationEpoch` を控えます。

## 4. slot 0 を閉じる

```bash
scripts/vrcrt.sh close-client 0
```

- 出力が `closed slot 0 (PID <slot 0 の PID>)`、終了コード 0。
- Windows で slot 0 の VRChat の窓が閉じ、slot 1 の窓は残る。

公開は約5秒ごとです。次で、slot 0・1 の値を5秒ごとに60秒まで出し、slot 0 が抜け、slot 1 が変わっていないことを見ます。

```bash
for i in $(seq 1 12); do scripts/vrcrt.sh status --json | python3 -I -c 'import json,sys; d=json.load(sys.stdin); c={x["slot"]:x for x in d["clients"]}; print(d["updatedAtUtc"], {s:{k:c[s].get(k) for k in ("processId","running","state","clientGeneration","observationEpoch","logStatus","catchingUp")} for s in (0,1)})'; sleep 5; done
```

- slot 0: `running` が `false`、`state` が `"left"`。
- slot 1: `processId`、`clientGeneration`、`observationEpoch` が `before.json` と同じで、`running` が `true`。

ほかの台に操作が及ばないことも見ます。

```bash
scripts/vrcrt.sh close-client 0
```

- 2回目は `notLaunchedByBridge` で断られ、終了コード 1。何も閉じない（1回目で記録からその台の項目が消えているため。記録が残ったまま台が終わっていれば `notRunning`）。

## 5. 約40秒後に slot 0 を起動し直す

4の close から40秒ほど待ってから実行します。

```bash
scripts/vrcrt.sh launch-client 0
```

- 出力が `launched slot 0 (profile 0, PID <新しい PID>, generation <G>)`、終了コード 0。`<G>` を控えます。
- Windows で slot 0 の VRChat が、前と同じ profile（アカウント）で起動する。

続けてもう一度実行すると、`alreadyRunning` で断られ、2台目は起動しないことを見ます。

```bash
scripts/vrcrt.sh launch-client 0
```

4と同じループ（`seq 1 36` にして180秒まで）で、次の条件がそろうまで見ます。

- slot 0: `clientGeneration` が `<G>`、`processId` が新しい PID、`running` が `true`。
- slot 0: `observationEpoch` が null でなく、`before.json` の値と違う。
- slot 0: `logStatus` が `"shown"`、`catchingUp` が `false`。
- `updatedAtUtc` が launch のあとの時刻。
- slot 1: `processId`、`clientGeneration`、`observationEpoch` が `before.json` と同じ。

## 6. 同じインスタンスに戻ったことを確かめる

```bash
scripts/vrcrt.sh wait-same-instance 120 --slots 0,1
```

- 終了コード 0（両方が joined で、同じ `instance`）。
- `scripts/vrcrt.sh log 0 --lines 40` が新しい台のログ（新しい時刻、`OnPlayerJoined` など）で、閉じた台のログの続きではない。
- `scripts/vrcrt.sh log 1 --lines 5` が slot 1 のログのまま。

## 7. Bridge のログを見る

```bash
scripts/vrcrt.sh doctor
```

Bridge のログの末尾に次の行があることを見ます。

- `Single-client close of slot 0 (PID …, created …, profile 0): exited True, record updated True.`
- `Single-client launch of slot 0 (profile 0, PID …, created …, generation <G>, roomId …).`
- 2回目の close と launch の `refused (NotLaunchedByBridge)` と `refused (AlreadyRunning)`。

## 8. 断る場合（任意）

- `vrcrt.sh restart` のあとで `launch-client 0` → `noLaunchPlan`（配置し直すまで起動しない）。
- Windows で VRChat を手で1台起動したまま `launch-client` → `othersRunning`。手で起動した台は閉じない。

## 9. 戻す

確かめ終わったら、必要なら退避した exe と `config.json` を戻します（`Stop-ScheduledTask` のあと、`install-bridge.ps1 -ExpectedSha256 <退避した exe のハッシュ> -SourcePath <退避した exe> -StartNow`）。

## 記録

### 2026-11-24（PR #39、`f717bad`）

Mac はこのリポジトリの Claude セッション（Unity の操作と `vrcrt.sh`）、Windows は別の Claude セッション（Bridge の入れ替え、プロセスと Bridge のログの1秒ごとの記録）が行った。時刻はすべて JST。

| 項目 | 結果 |
|---|---|
| 日時・Windows の版・VRChat の版 | 2026-11-24 19:02〜19:05。Windows 11 Home 26H2（10.0.26300）。VRChat 2026.4.1p3-1939-4803d92ede-Release（Unity 6000.0.67f1-DWR）。Logging は「完全」相当 |
| 入れた exe の SHA-256 / `doctor` の版 | `9b383de09e41de3d651a5202a69623544a3851c459c087f5ad9fa3694520ea61` / 1.0.0+f717bad（`package.json` を上げていないため 1.0.0）。Windows の `dotnet test` は 796 件すべて合格（シンボリックリンクの3件、`RestartManagerFileOwnersTests` も合格）。Smart App Control の阻止なし。前の exe（0.9.0+156b78e）と `config.json` は `slot-test-backup-20261124-185110` に退避 |
| 3: 2台が同じインスタンス（slot 0 / 1 の PID・世代・EPOCH） | 19:02:28 に Mac の Unity（Vrc-remote-test、VRCDefaultWorldScene）で Remote Build & Test。19:02:43 に slot 0（PID 44364、profile 0 = graaaaa）、19:02:46 に slot 1（PID 19172、profile 1 = graaaaaaa2）が起動（roomId 1855306159）。19:03:00 に `wait-same-instance` 成功（`local:d3cd1506…7d9d8`）。slot 0: 世代 4397920479404826・EPOCH 1795514564853、slot 1: 世代 7707401320639783・EPOCH 1795514570153。ログは両方 Restart Manager の単独所有で付いた |
| 4: close-client 0 の出力・終了コード、slot 0 が left になるまでの秒数、slot 1 不変 | 19:03:12 に送り、19:03:14 に `closed slot 0 (PID 44364)`、終了コード 0。Windows では 19:03:15.474 に VRChat が PID 19172 の1台だけになった。19:03:22 の `status --json`（更新 19:03:21）で slot 0 は `running=false`・`state="left"`・`logStatus="notRunning"`（close から約7秒）。slot 1 は PID・世代・EPOCH が同じで `joined` のまま。slot 1 のログに 19:03:13 の `OnPlayerLeft graaaaa` |
| 4: 2回目の close-client 0 が notLaunchedByBridge | 19:04:05 に送り、409 `notLaunchedByBridge`、終了コード 1。何も閉じていない |
| 5: launch-client 0 の出力（PID・generation）、close からの秒数 | 19:04:13 に送り、すぐに `launched slot 0 (profile 0, PID 1924, generation 724409070576342)`、終了コード 0（close から約59秒）。Windows では PID 1924 が 19:04:14.303 に起動。引数は `--url=create?roomId=1855306159…vrc-remote-20261124T100238067Z-e4123a2a.vrcw --no-vr … --profile=0` で、`--watch-worlds` なし。PID 19172 の引数と `--profile` 以外は同じ |
| 5: 2回目の launch-client 0 が alreadyRunning | 19:04:13 に送り、409 `alreadyRunning`（PID 1924）、終了コード 1。Windows の1秒ごとの記録で、VRChat の台数は全体を通して 1（19:02:44）→ 2（19:02:47）→ 1（19:03:15）→ 2（19:04:14）と変わっただけで、3台目は一度も無い |
| 5: slot 0 の新しい世代・EPOCH・logStatus shown・catchingUp false までの秒数 | Windows で 19:04:17.891 に `output_log_2026-11-24_19-04-14.txt` を slot 0 のログとして表示（Restart Manager で PID 1924 だけが開いていた）。launch から約3.6秒。19:04:24 の `status --json`（更新 19:04:23）で slot 0 は PID 1924・世代 724409070576342（launch の答えと同じ）・EPOCH 1795514657903（前の 1795514564853 と違う）・`logStatus="shown"`・`catchingUp=false`・`state="joining"`。slot 1 は PID・世代・EPOCH が同じ |
| 6: wait-same-instance の結果、log 0 が新しい台のもの | 新しい台は 19:04:19 に Local Test へ Successfully joined、19:04:24 に Finished entering world。19:04:28 に `wait-same-instance 120 --slots 0,1` 成功（同じ `local:d3cd1506…7d9d8`、players は graaaaa と graaaaaaa2）。`log 0` は新しい台のログ（19:04:24 の `OnPlayerJoined` 2人）で、閉じた台のログではない。`log 1` に 19:04:25 の `OnPlayerJoined graaaaa` |
| 7: Bridge のログの行 | 19:03:13.039 `Closing 1 VRChat client(s) launched by the Bridge (PID 44364).` / 19:03:15.049 `Single-client close of slot 0 (PID 44364, created 2026-11-24T10:02:43.7147946Z, profile 0): exited True, record updated True.` / 19:04:06.251 `[WRN] Single-client close of slot 0 refused (NotLaunchedByBridge)` / 19:04:14.312 `Single-client launch of slot 0 (profile 0, PID 1924, created 2026-11-24T10:04:14.3039883Z, generation 724409070576342, roomId 1855306159).` / 19:04:14.601 `[WRN] Single-client launch of slot 0 refused (AlreadyRunning)` |
| 8: noLaunchPlan / othersRunning（任意） | 行っていない（Mac の単体試験と HTTP の試験で確かめている） |
| 気づいたこと | 2回目の close の断り方は、手順書の予想（`notRunning`）ではなく `notLaunchedByBridge` だった。1回目で記録からその台の項目が消えるので、こちらが正しく、手順書を直した。Unity の Remote Test ウィンドウは「パッケージ 0.9.0、Bridge 1.0.0」の版の警告を出す（リリースで両方を 0.10.0 にすれば消える）。 |

結論: 1台だけ閉じる・同じ起動の仕方で起動し直すことが Mac の `vrcrt.sh` だけでできた。閉じた台だけが抜け、ほかの台は PID・世代・EPOCH が変わらず、起動し直した台は同じ slot・同じ profile・同じインスタンスに戻り、新しい世代・EPOCH・自分のログに付いた。
