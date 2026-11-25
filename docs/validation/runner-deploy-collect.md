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

1. `vrcrt.sh config set VrchatClientMode=Reload VrchatClientCount=1 VrchatProfiles=null`（Reload では VrchatProfiles を消す。残すと 422 invalidConfig）、Slots を `0` にして `Run`（シーン B）。
2. 続けて `Use the last build` で `Run`。
3. 記録すること: clientReadiness と worldBuildMatch の結果、`--watch-worlds` での再読み込みで `joinedSeq`・epoch が進んだか、目印がもう一度出たか。期待:
   - 新しい join が観測されないとき: clientReadiness は FAIL（観測が欠けていれば UNKNOWN）、worldBuildMatch は評価されず null。
   - join はあるが目印が出ないとき: clientReadiness PASS、run 全体も PASS、worldBuildMatch は UNKNOWN（カーソルより前の目印で PASS にならない）。
4. 終わったら `vrcrt.sh config set VrchatClientMode=Relaunch VrchatProfiles='[0,1]' VrchatClientCount=null` で戻す。

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

### 2026-11-25（UTC）、CLI から、パッケージ 1ceb5f3、Bridge 0.10.0+7dbd10bc

Mac のエージェントが CLI だけで確かめた。Windows で人がしたことは無い。Unity の画面（Run ボタン・Cancel・台の表の Close／Launch）は触っていない。

| 項目 | 結果 |
|---|---|
| 環境（Windows、VRChat の版、Bridge の版と SHA-256、パッケージの commit、Unity の版） | VRChat `2026.4.2-1953-d52354d5be-Release`。Bridge `0.10.0+7dbd10bc62d3e88f06c5ee9903cf681fa49b8eaa`、exe SHA-256 `c056e50571964746cb4362257bb37d11c8dc7c7788b9c530460f60875ef464d7`。パッケージ commit `1ceb5f3`（0.11.0）。Unity 2022.3.22f1（Mac）。Python 3.12.4。Relaunch profiles `[0,1]`、Desktop |
| 1. ボタン1つで Relaunch 2台: results、所要時間（`run.json` の createdAtUtc〜finishedAtUtc） | ボタンは未確認（画面を触っていない）。同じ runner の流れを 6 の CLI で通した（下） |
| 1. 証拠のハッシュと大きさ、権限 | 6 の run で確かめた。`run.json` の evidence の sha256・大きさが全ファイルと一致。run・evidence・deploy-results のフォルダーは 700、`run.json`・`deploy-result.json`・`markers.json`・全文ログ・`deploy-results/<buildId>.json` は 600 |
| 2. tag の照合（新しいビルド／同じ bytes の再配置） | 未確認（シーン B が無い） |
| 3. Reload で同じ bytes（joinedSeq・epoch・目印の再出力） | 未確認（同じ bytes の再配置は画面の `Use the last build` が要る。headless は毎回ビルドする） |
| 4. 1台ずつ Close／Launch（PID・世代・EPOCH の変わり方） | 画面のボタンは未確認。同じ API を CLI で: `close-client 1` は 2 s で `closed slot 1 (PID 36212)`、終了コード 0。slot 0（PID 18568、世代 4184232016852734、epoch 1795628236126）は変わらず。直後の `status` はまだ slot 1 を running と出し、約10 s 後に running false・left（台の一覧の更新の間隔ぶん遅れる）。`launch-client 1` は即座に `launched slot 1 (profile 1, PID 32176, generation 6715527127308562)`、終了コード 0。13 s で `wait-same-instance 120 --slots 0,1` が通り、slot 1 は新しい epoch 1795628570191 で slot 0 と同じインスタンスに joined、authenticated、inErrorWorld false |
| 5. Cancel、Editor を閉じたあとの継続、次の Run | 未確認（画面の操作が要る）。runner の側の取り消し・中断・claim の引き継ぎは fixture の試験だけ |
| 6. CLI の headless、Editor を開いたままの断り | **PASS**。run `20261125T173619Z-687e11db`、build `20261125T173712079Z-742b0ef1`、シーン A（`VRCDefaultWorldScene`）、slots 0,1。終了コード 0。results: deploy PASS、launch PASS、clientReadiness PASS、worldBuildMatch UNKNOWN（noTagMarker）、evidence PASS、testVerdict notJudged。17:36:19〜17:39:40（3分22秒）。内訳: prepare 0.5 s、headless のビルドと配備 60 s、台の準備 17 s、目印の待ち 121 s（上限 120 s まで待って UNKNOWN）、照らし直し・回収・最後の判定 2 s。2台とも同じインスタンス。runner の子は残らず、claim も消えた。<br>その前の1回（`20261125T173421Z-6658865b`）は、試験用のプロジェクトに ProjectSettings が足りず SDK のビルドが失敗し、FAIL `deploy:SDK_BUILD_FAILED` を正しく記録した（Bridge には何も送っていない）。<br>Editor を開いたまま（commander-world を開いた Unity に対して）同じコマンド: 終了コード 3、result UNKNOWN、reason `editorOpen`。Unity は起動せず、claim は残らない。macOS の Unity 2022.3.22f1 は `Temp/UnityLockfile` に flock を取っていた（`requires_validation` が解けた。錠だけで見分けられる） |
| 4台（Windows の空き次第） | 未確認 |

### 2026-11-25（UTC）、Unity の画面から（同じ環境、パッケージは 1ceb5f3 に下の直しを足したもの）

Mac のエージェントが computer use で試験用のプロジェクトの Unity を動かした。Windows で人がしたことは無い。画面の Python は `/usr/bin/python3`（Xcode の Python 3.9.6）。

| 項目 | 結果 |
|---|---|
| 1. ボタン1つで Relaunch 2台（シーン A） | **PASS**。run `20261125T175744Z-128da051`、build `20261125T175754565Z-66fb5bb7`。画面に `PASS: the clients were ready and the evidence was collected. The test itself is not judged.` と results（deploy PASS、launch PASS、clientReadiness PASS、worldBuildMatch UNKNOWN、evidence PASS、testVerdict notJudged）。17:57:44〜18:00:20（2分36秒）。内訳: prepare 1 s、ビルドと配備 20 s、台の準備 12 s、目印の待ち 120 s（目印の無いシーンなので上限まで）、残り 3 s。証拠6件の sha256 と大きさが一致。run のフォルダー 700、`run.json`・`deploy-result.json` 600。claim は消えた |
| 1. 証拠のハッシュと大きさ、権限 | 上のとおり。ただし `progress.jsonl` と `cancel.request` が 644 だった（フォルダーが 700 なので他人は読めない）。600 で作るよう直した（下） |
| 2. tag の照合（新しいビルド／同じ bytes の再配置） | 直したあと **PASS**／**PASS**。シーン B は `Assets/Scenes/VrcrtTagScene.unity`（試験用のプロジェクトだけ）: ビルドの前処理（`IProcessSceneWithReport`）が `RemoteBuildApi.ActiveWorldBuildTag` を目印の GameObject の名前に入れ、UdonSharp の `Start()` が `[VRCRT] probe loaded vrcrt.build=<tag>` を出す。<br>直す前の run `20261125T180836Z-8a35648f` は、両台が正しい tag の目印を出したのに worldBuildMatch UNKNOWN（noTagMarker）だった。slot 0 の全文ログで、Joining が seq 451、目印が 720、Finished entering world（`joinedSeq`）が 766。VRChat はワールドの `Start()` を Finished より前に出すので、`joinedSeq` より後だけを数える runner では一度も数えられない。目印を数える起点を、その参加の Joining の行（Bridge の `/clients` の `joinAttemptSeq`）に直した（下）。<br>直したあと: run `20261125T181620Z-db5df55d` は worldBuildMatch PASS、run 全体 30 s（目印の待ち 1 s）。`Use the last build` の run `20261125T181709Z-4dc0d0c3` は、deploy-result の artifact.worldBuildTag が前の回の `20261125T181620Z-db5df55d` のまま（sha256 `79e660c5…`）で、worldBuildMatch PASS、28 s |
| 3. Reload で同じ bytes（joinedSeq・epoch・目印の再出力） | **PASS**（直したあと）。Reload 1台で新しいビルド: run `20261125T181834Z-17ed895b`、launch launched（1/1）、全 PASS、22 s。続けて `Use the last build`: run `20261125T181912Z-09f2a664`、launch notApplicable（reusedRunning）、全 PASS、14 s。`--watch-worlds` の再読み込みで、世代 4640635345043014・epoch 1795630726960 は同じまま、新しい Joining（seq 937、カーソルは 875）と Finished（1086）があり、インスタンス ID は変わった（新しい local の部屋）。目印は seq 1043 でもう一度出た。前の回の目印（716）は数えていない。終わったあと Relaunch `[0,1]` に戻し、active の設定が切り替え前と同じことを確かめた（切り替えで VRChat は閉じた） |
| 4. 1台ずつ Close／Launch（PID・世代・EPOCH の変わり方） | **PASS**。slot 1 の `Close` の確認画面に slot・PID 26612・profile・世代・epoch と「一覧を読んだあとの変化は止められない」旨が出た。閉じると `Closed slot 1 (PID 26612).`、約10 s で表の slot 1 が exited。slot 0（PID 21612）は変わらず。`Launch` の確認画面のあと `Launched slot 1 (profile 1, PID 18464, generation 859002919688559).`、15 s 以内に表で slot 0 と同じインスタンスに joined・logged in |
| 5. Cancel、Editor を閉じたあとの継続、次の Run | ビルド中の Cancel は**未確認**: シーン A の SDK のビルドは 6 s で、押したクリックは効かなかった（ビルドの間は Unity が入力を受けない）。runner が引き継いだあとの Cancel は **PASS**: run `20261125T180134Z-7d7562d6` は押して1 s 以内に `CANCELED: canceled`（目印の待ちの最中、`cancel.request` を runner が読んだ。配備は済んでいたので deploy-result は deployed）。<br>Editor を閉じたあとの継続は **PASS**: run `20261125T180356Z-9dac1bbb` が running になってから Unity を終了（同じ bundle の Unity が2つ開いていて、メニューの Quit がどちらに効くか確かでないため、自分が起動した Editor のプロセスだけに SIGTERM）。runner（親は launchd）は続け、2分後に PASS、証拠一致、claim は消えた。<br>次の Run: Unity を開き直すとウィンドウは `Not run in this Editor session.`、Run は断られずに始まった（上の 2 の run） |
| 6. CLI の headless、Editor を開いたままの断り | 前の記録のとおり PASS |
| 4台（Windows の空き次第） | 未確認 |

### 確認の中で直したこと

- runner: worldBuildMatch の目印を、各台の今の参加の Joining の行（`joinAttemptSeq`）より後から数える（`joinAttemptSeq` を出さない Bridge では今までどおり `joinedSeq` より後）。Reload の「配備の後に入った」も、Joining の行がカーソルより後の参加だけにした（配備の前に始まった参加は前のワールドを読んだかもしれない）。照らし直しで `joinAttemptSeq` の変化も見る。fixture の試験を4件足した（読み込み中の目印は数える、前の参加の目印は数えない、`joinAttemptSeq` が無いとき、Reload で配備の前に始まった参加）。
- runner の `progress.jsonl` と Unity が書く `cancel.request` を 600 にした（試験1件）。


## 気づいたこと

- 目印を出さないワールドでは、worldBuildMatch の待ちで上限の 120 s を毎回使い切る。run の所要時間の6〜8割がこれ。tag を使わないワールドのために、待ちを省く引数（例 `--world-build-timeout 0`）と画面の切り替えを足すかを、次の段で決める。目印を出すワールドでは待ちは 1 s 以内だった。
- close の直後は、Bridge の台の一覧が更新されるまで（約10 s）、`status` も画面の表も閉じる前の状態を出す。画面ではそのあいだ同じ台の `Close` をもう一度押せる（Bridge が notRunning で断るはず。押してはいない）。R1b の leave-rejoin は一覧の時刻（`updatedAtUtc`）が操作より後になるまで待つ必要がある。
- 画面の表は、Bridge がこのホストの起動以来 launch していない slot（今回は profile の無い slot 2・3）にも `Launch` を押せる形で出す。Bridge は `NoLaunchPlan` で断るので害は無いが、押せないようにするかは次の段で決める（Bridge の起動の計画は画面からは見えない）。
- `vrcrt.sh config set` で Reload に切り替えるとき、`VrchatProfiles=null` が要る（手順を直した）。
- ビルド中の Cancel は、小さなシーンでは SDK のビルドが数秒で終わるので押す間が無い。大きなシーンでの確認は別の機会に。
- 試験用の Unity プロジェクトから、利用者のプロジェクト（`~/Documents/VRChatProjects/Vrc-remote-test`）の SDK パッケージを `file:` で参照すると、SDK がそこにサンプルなどのファイルを作る。試験用のプロジェクトには SDK を写して入れること（今回作られた70件は、利用者の了承を得てゴミ箱へ移した）。

## 結論

R1a は本物の Bridge 0.10.0 と VRChat 2台で、Unity のボタン1つ（Relaunch 2台、Reload 1台）でも CLI 1行でも PASS。tag の照合は、実機で見つかった数え始めの誤り（VRChat は `Start()` の目印を参加の完了より前に出す）を直したあと、新しいビルド・同じ bytes の再配置・Reload の再読み込みのどれでも PASS。台の表の Close／Launch、runner が引き継いだあとの Cancel、Editor を閉じたあとの継続、Editor を開いたままの headless の断りも期待どおり。

まだ見ていないのは、ビルド中の Cancel と4台。
