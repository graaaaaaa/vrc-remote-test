# 人の段（manual-steps）の実機の確かめ（R3、v0.13.0）

runner の `manual-steps`（`docs/plans/r3-contract.md`）を Windows 実機で確かめる手順と、その記録です。2026-11-26 にここで PASS を記録した commit で、runner の `VALIDATED_PROCEDURES` に manual-steps を足しました。それより前の runner では `--unvalidated` を付けたときだけ動きます。Unity の Run の始めには、画面から始めることを確かめるまで出しません（待ちの画面は出ます）。

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

### 2026-11-26（UTC）、Bridge 0.12.0+4892f91、パッケージ claude/r3-manual-steps（df2f6d2 に下の直しを足したもの）

Mac のエージェントが CLI と computer use で確かめた。Windows で人がしたことは無い。統合役の perf のあと、統合役と合意した時間（14:06〜14:20 JST）に、`VrchatProfiles` を `[0,1]` にして行い、そのままにした（統合役の頼み）。

| 項目 | 結果 |
|---|---|
| 環境 | Bridge `0.12.0+4892f91a0ad230b5bd2732dd3af48d857327fb46`、exe SHA-256 `a69a379a5637b527f74050d901c22a9424e50e336df1e66da755bbd79f3bae6e`（入れ替えなし）。Unity 2022.3.22f1（Mac、試験用のプロジェクト、シーン B）。Relaunch profiles `[0,1]`、Desktop。画面の Python は `/usr/bin/python3`（3.9.6） |
| 1. 項目の答えと、途中の退出と起動し直し | **PASS**。run `20261126T050657Z-d7ac6a4b`、build `20261126T050721790Z-a7689e88`。results すべて PASS（deploy・launch・clientReadiness・worldBuildMatch・evidence・leaveRejoin・manualSteps）、testVerdict notJudged。項目 a は Unity の待ちの画面から OK（`by: unity`、メモつき）、b と c は `answer` から（`by: cli`）。b のあと slot 1 を前提つきで閉じて起動し直した（close 2 s、退出の確認 1 s、launch 1 s、起動し直しの準備 16 s）。c は新しい世代（7647873623555514）で聞き、`clientChanged` にならなかった。各項目の `seqRange` があり、`evidenceGap` は無し。証拠 34 件（配備・item:a〜c・before-close・after-close・after-rejoin・end）の sha256 が一致。配備から待ちの始めまで 46 s（ビルドと配備 31 s、台の準備 11 s） |
| 2. Editor を起動し直したあとの待ちの画面 | **PASS**（下の直しのあと）。run `20261126T051739Z-c18d4669`（ターミナルから始めた run）。待ちのあいだに Unity を開くと、待ちの画面が出た。Unity を終了して（自分が起動した Editor のプロセスだけに SIGTERM）開き直すと、待ちの画面がまた出て、項目 a を OK で答えられた（`by: unity`）。次の項目 b ではメモの欄が空になった |
| 3. 待っているあいだの変化 | **PASS**。同じ run の項目 b の待ちのあいだに、Mac から本文なしの `vrcrt.sh close-client 1`。5 s で UNKNOWN `clientChanged`（`revisionChanged: operationRevision 39, this run expected 38`）、項目 b は interrupted、c は notAsked、`waiting.json` は閉じ、そのあとの `answer` は `notWaiting` で断られた（終了コード 1）。Unity の待ちの画面も消えた。`launch-client 1` で戻し、2台が同じインスタンスに入った |
| VR 台＋観戦台 | 未確認（利用者の決定） |
| 4台 | 未確認 |
| Unity の Run から manual-steps を始めること | 未確認（確かめはターミナルから始めた）。そのため画面の手順の選びには、まだ manual-steps を出さない |

### 確かめの中で直したこと

- Unity の待ちの画面が出なかった。Unity は待ちの期限の時刻を `...Z` の形しか読まず、runner が書く `+00:00` の形（Python の `isoformat`）を断っていた。ゾーンを書いた ISO 8601 ならどちらも読むように直した（試験1件）。
- Unity の待ちの画面で、答えたあとも次の項目のメモの欄に前の文が残った。メモの値は空にしていたが、文の欄がキーボードの焦点を持ったまま自分の写しを持っていた。新しい項目で焦点も外すようにした。

## 気づいたこと

- サンドボックスの中から `waiting` を呼ぶと、runner のプロセスを確かめられず、`valid: false` になる（runner が死んだのと同じ扱い）。サンドボックスの外（ふつうのターミナルと Unity）では正しく `valid: true`。
- 待ちのあいだの変化は、Bridge の revision が先に効く（本文なしの close でも revision が動く）。一覧の更新を待たずに 5 s で止まった。

## 結論

manual-steps は、本物の Bridge 0.12.0 と VRChat 2台（デスクトップ）で、Unity の画面とターミナルの両方から答えられ、途中の退出と起動し直しのあとも新しい世代で聞き続け、待ちのあいだの変化では送り直さずに止まることを確かめた。これをもって runner の `VALIDATED_PROCEDURES` に manual-steps を足した。VR、4台、Unity の Run からの始めは未確認。
