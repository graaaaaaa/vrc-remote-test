# 人の段（manual-steps）の実機の確かめ（R3、v0.13.0）

runner の `manual-steps`（`docs/plans/r3-contract.md`）を Windows 実機で確かめる手順と、その記録です。ここに PASS を記録した commit で、runner の `VALIDATED_PROCEDURES` と Unity の手順の選びに manual-steps を足します。それより前の runner では `--unvalidated` を付けたときだけ動き、Unity の Run の始めには出ません（待ちの画面は出ます）。

確かめはデスクトップの2台だけで行います（2026-11-26 に利用者が決定）。VR 台＋観戦台は「未確認」と書きます。Windows で人がすることはありません。Bridge は 0.12.0 のまま（入れ替えなし）。

## 前提

- Windows の Bridge が 0.12.0（`slotPreconditions.v1`）。ほかのセッション（統合役）と時間を合わせ、そのあいだだけ `VrchatProfiles` を `[0,1]` にして、終わったら `[0]` に戻す。
- Mac の試験用の Unity プロジェクトに、このブランチのパッケージが入っている（`docs/validation/runner-deploy-collect.md` と同じ）。シーン B（`Assets/Scenes/VrcrtTagScene.unity`、tag の目印を出す）。
- 確認の項目のファイル（例。`~/Library/Application Support/VRCRemoteTest/` の外の、自分だけが書けるファイル）:

```json
{"schema": "vrcrt.checklist", "schemaVersion": 1, "title": "R3 の確かめ",
 "items": [{"id": "a", "text": "Unity の待ちの画面から OK を押す"},
           {"id": "b", "text": "ターミナルの answer で OK（このあと slot 1 が退出と起動し直し）"},
           {"id": "c", "text": "ターミナルの answer で OK"}]}
```

## 1. 項目の答えと、途中の退出と起動し直し

```bash
python3 package/Tools~/vrcrt_runner.py run --build headless --unity /Applications/Unity/Hub/Editor/2022.3.22f1/Unity.app/Contents/MacOS/Unity --project <試験用のプロジェクト> --scene Assets/Scenes/VrcrtTagScene.unity --slots 0,1 --procedure manual-steps --checklist <ファイル> --leave-rejoin-after b --target-slot 1 --unvalidated
```

1. headless の Unity がビルドを終えて閉じ、runner が項目 a を待ち始めたら、試験用のプロジェクトを Unity で開く（headless のビルドのあとは、同じプロジェクトを開いてよい）。VRC Remote Test のウィンドウに待ちの画面が出る。項目 a を `OK` で答える（computer use）。
2. 項目 b は `waiting --run <runDir>` で waitId を読み、`answer ... ok` で答える。slot 1 が閉じて起動し直し、slot 0 と同じインスタンスに入る。
3. 項目 c を `answer` で答える。
4. 期待すること: 終了コード 0、`results` の deploy・launch・clientReadiness・worldBuildMatch・evidence・leaveRejoin・manualSteps が PASS、`testVerdict` は notJudged。項目ごとに `seqRange` と目印の控え、`evidence/manual-steps/leave-rejoin/` の3つの段、`end` の全文ログ（slot 1 は新しい世代）。証拠の sha256 が `run.json` と合う。項目 a の `by` は unity、b と c は cli。

## 2. Editor を起動し直したあとの待ちの画面

1. 1 と同じ run を、項目を1つも答えずに始め、待ちの画面が出たら Unity を終了する（自分が起動した Editor のプロセスだけに SIGTERM）。
2. 開き直すと、待ちの画面がまた出て、答えられる。
3. 残りを `answer` で答えるか、run のフォルダーに `cancel.request` を作って終える（CANCELED）。

## 3. 待っているあいだの変化

1. 1 と同じ run を、項目 a の待ちで止めたまま、Mac から本文なしの `vrcrt.sh close-client 1` を打つ。
2. 期待すること: 5 s ほどで UNKNOWN `clientChanged`、項目 a は interrupted、待ちの画面は消え、`answer` は断られる。
3. `vrcrt.sh launch-client 1` で戻す。

## 4. 記録に書くだけのこと

- VR 台＋観戦台（`VrchatMode: VR`）: 未確認。
- 4台: 未確認。

## 記録

（実機で確かめたら、ここに日付・Bridge の版と exe の SHA-256・パッケージの commit・各項目の結果を書く）
