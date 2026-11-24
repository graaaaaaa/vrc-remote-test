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

- 2回目は `notRunning` で断られ、終了コード 1。何も閉じない。

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
- 2回目の close と launch の `refused (NotRunning)` と `refused (AlreadyRunning)`。

## 8. 断る場合（任意）

- `vrcrt.sh restart` のあとで `launch-client 0` → `noLaunchPlan`（配置し直すまで起動しない）。
- Windows で VRChat を手で1台起動したまま `launch-client` → `othersRunning`。手で起動した台は閉じない。

## 9. 戻す

確かめ終わったら、必要なら退避した exe と `config.json` を戻します（`Stop-ScheduledTask` のあと、`install-bridge.ps1 -ExpectedSha256 <退避した exe のハッシュ> -SourcePath <退避した exe> -StartNow`）。

## 記録

| 項目 | 結果 |
|---|---|
| 日時・Windows の版・VRChat の版 | |
| 入れた exe の SHA-256 / `doctor` の版 | |
| 3: 2台が同じインスタンス（slot 0 / 1 の PID・世代・EPOCH） | |
| 4: close-client 0 の出力・終了コード、slot 0 が left になるまでの秒数、slot 1 不変 | |
| 4: 2回目の close-client 0 が notRunning | |
| 5: launch-client 0 の出力（PID・generation）、close からの秒数 | |
| 5: 2回目の launch-client 0 が alreadyRunning | |
| 5: slot 0 の新しい世代・EPOCH・logStatus shown・catchingUp false までの秒数 | |
| 6: wait-same-instance の結果、log 0 が新しい台のもの | |
| 7: Bridge のログの行 | |
| 8: noLaunchPlan / othersRunning（任意） | |
| 気づいたこと | |
