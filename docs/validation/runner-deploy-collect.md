# Deploy and Collect（runner）と1台ずつの Close／Launch の実機の確かめ

パッケージ 0.11.0（R1a、`docs/plans/r1-automation.md` §4）を Windows 実機で確かめる手順と、その記録です。Bridge は変えていないので、Bridge 0.10.0 のまま確かめられます。目的は、Unity のウィンドウの `Run` 1つ（または CLI 1行）で、配備・台の準備・この回のビルドとの対応・証拠の回収・`run.json` まで進むこと、そしてウィンドウから1台ずつ閉じて起動し直せることを、本物の VRChat で見ることです。

Mac の試験（Unity 2022.3.22f1 の EditMode 249件、`bash scripts/test-vrcrt.sh`）は通っています。ここでは Mac では確かめられないことを見ます。Windows で人がすることはありません。Windows が空いていて（VR で遊んでいない、電源が入っている）、Bridge が動いていれば足ります。

## 前提

- Windows の Bridge は 0.10.0 以上で、Relaunch の profile 0 と 1 に別々のアカウントで一度ログイン済み（`docs/setup-windows.md` の4）。VRChat の Logging は「完全」。
- Mac の Unity プロジェクトに、このブランチのパッケージ（0.11.0）が入っている。`vrcrt.sh doctor` が通る。
- 確かめ用のシーンが2つある。
  - A: 何も目印を出さないワールド（例: `VRCDefaultWorldScene`）。
  - B: 読み込み時に `vrcrt.build=<tag>` を含む目印を出すワールド（`RemoteBuildApi.ActiveWorldBuildTag` をビルド前処理で埋める小さな試験シーン）。用意できなければ B の項目は「未確認」と書く。

## 1. ボタン1つで Relaunch 2台（シーン A）

1. `vrcrt.sh config set VrchatClientMode=Relaunch VrchatProfiles='[0,1]' AutoLaunchVrchat=true`（すでにその設定なら不要）。
2. Unity の VRC Remote Test ウィンドウで、Deploy and Collect の Slots が `0,1` になっていることを見て、`Run` を押す。
3. 期待すること:
   - 進捗が「Waiting for the clients and collecting evidence...」（そのあと runner の「waiting for the clients …」）を経て、最後に `PASS: the clients were ready and the evidence was collected. The test itself is not judged.` と出る。
   - results: deploy PASS、launch PASS、clientReadiness PASS、worldBuildMatch UNKNOWN（目印が無い）、evidence PASS、testVerdict notJudged。
   - `Open Run Folder` で開くフォルダーに、`run.json`、`deploy-result.json`、`evidence/slot-0/g<世代>-e<epoch>/` と `slot-1/...` があり、それぞれに `markers.json` と `vrchat-slot-*.log` がある。`run.json` の evidence の sha256 と大きさが、ファイルと合う（`shasum -a 256`）。
   - `~/Library/Application Support/VRCRemoteTest/deploy-results/<buildId>.json` もある。フォルダーは 700、ファイルは 600。

## 2. tag でこの回のビルドを確かめる（シーン B）

1. シーン B を開いて `Run`。
2. 期待すること: worldBuildMatch PASS。`run.json` の effectiveWorldBuildTag が runId と同じ。
3. `Use the last build` を入れてもう一度 `Run`（同じ bytes の再配置）。worldBuildMatch PASS（元の tag のまま）。

## 3. Reload で同じ bytes を再配置したとき（`requires_validation`）

1. `vrcrt.sh config set VrchatClientMode=Reload VrchatClientCount=1`、Slots を `0` にして `Run`（シーン B）。
2. 続けて `Use the last build` で `Run`。
3. 記録すること: clientReadiness と worldBuildMatch の結果、`--watch-worlds` での再読み込みで `joinedSeq`・epoch が進んだか、目印がもう一度出たか。期待:
   - 新しい join が観測されないとき: clientReadiness は FAIL（観測が欠けていれば UNKNOWN）、worldBuildMatch は評価されず null。
   - join はあるが目印が出ないとき: clientReadiness PASS、run 全体も PASS、worldBuildMatch は UNKNOWN（カーソルより前の目印で PASS にならない）。
4. 終わったら Relaunch `[0,1]` に戻す。

## 4. ウィンドウから1台ずつ（Relaunch 2台）

1. 1 のあと、VRChat Clients の台の表で slot 1 の `Close` を押し、確認の画面の文言（Bridge は記録にある台を閉じる。一覧を読んだあとの変化は止められない）を見て閉じる。
2. 期待すること: `Closed slot 1 (PID …).`、表の slot 1 が exited になり、slot 0 は PID・世代とも変わらない。
3. slot 1 の `Launch` を押す。期待すること: `Launched slot 1 (profile 1, PID …, generation …).`、数秒で表に新しい PID・世代・epoch が出て、slot 0 と同じインスタンスに joined。
4. `vrcrt.sh status` が同じ内容を出す。

## 5. 取り消しと Editor を閉じること

1. `Run` を押し、「Building and deploying...」のあいだに `Cancel`。期待すること: `run.json` は result CANCELED、reason canceled。送ったあとの取り消しかは `deploy-result.json` の classification.stopReason（`canceledAfterSending`）で見る。
2. `Run` を押し、`run.json` の state が `running` になった（runner が引き継いだ）ら Unity を終了する。数分後に `run.json` を直接開き、state finished で結果が書かれていることを見る（Unity を開き直しても、前の run はウィンドウには出ない。ウィンドウが覚えているのは同じ Editor のセッションのあいだだけ）。
3. 次の `Run` が、残った claim に断られずに始まる。

## 6. CLI から1行（headless）

1. Unity を閉じて、ターミナルで:

```bash
python3 package/Tools~/vrcrt_runner.py run --build headless --unity /Applications/Unity/Hub/Editor/2022.3.22f1/Unity.app/Contents/MacOS/Unity --project /path/to/UnityProject --scene Assets/Scenes/<A>.unity --slots 0,1
```

2. 期待すること: 終了コード 0、出力の JSON の result PASS。
3. Unity を開いたまま同じコマンドを打つと、`editorOpen` で断る（macOS の Unity の `Temp/UnityLockfile` の錠の挙動は `requires_validation`。断らなかったら記録する）。

## 記録

### （日付、パッケージの commit、Bridge の版と SHA-256）

| 項目 | 結果 |
|---|---|
| 環境（Windows、VRChat の版、Bridge の版と SHA-256、パッケージの commit、Unity の版） | |
| 1. ボタン1つで Relaunch 2台: results、所要時間（`run.json` の createdAtUtc〜finishedAtUtc） | |
| 1. 証拠のハッシュと大きさ、権限 | |
| 2. tag の照合（新しいビルド／同じ bytes の再配置） | |
| 3. Reload で同じ bytes（joinedSeq・epoch・目印の再出力） | |
| 4. 1台ずつ Close／Launch（PID・世代・EPOCH の変わり方） | |
| 5. Cancel、Editor を閉じたあとの継続、次の Run | |
| 6. CLI の headless、Editor を開いたままの断り | |
| 4台（Windows の空き次第） | 未確認 |

## 気づいたこと

## 結論
