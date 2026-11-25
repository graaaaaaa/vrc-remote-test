# 前提つきの close／launch と leave-rejoin の実機の確かめ（R1b、v0.12.0）

Bridge 0.12.0 の前提条件（`docs/plans/r1b-contract.md`）と、runner の leave-rejoin（`docs/plans/r1-automation.md` §5.4）を Windows 実機で確かめる手順と、その記録です。leave-rejoin は、ここで PASS を記録した commit で runner の `VALIDATED_PROCEDURES` に足すまで、`--unvalidated` を付けたときだけ動きます。

Windows で人がすることは、Bridge の入れ替えだけです（Windows 側のセッションに頼む。`docs/setup-windows.md` の1「版を入れ替える」）。VR で遊んでいないこと、電源が入っていることが要ります。

## 前提

- Windows の Bridge が 0.12.0（このブランチのビルド）。`vrcrt.sh doctor` で版と実行体の SHA-256 を確かめる。`vrcrt.sh bridge --json` に `bridgeInstanceId`・`operationRevision`・`currentBatch` が出る。
- Relaunch の profile 0 と 1 に、別々のアカウントで一度ログイン済み。VRChat の Logging は「完全」。
- Mac の試験用の Unity プロジェクトに、このブランチのパッケージが入っている（`docs/validation/runner-deploy-collect.md` と同じ）。

## 1. 本文なしの close／launch が v0.10 と同じに動く

1. 配備して2台を起動する（Unity の `Run`、または runner の deploy-collect）。
2. `vrcrt.sh close-client 1`、続けて `vrcrt.sh launch-client 1`（Commander の `dice_life_auto` の呼び方）。
3. 期待すること: 出力の1行と終了コード 0 が v0.11 のときと同じ。`--json` を付けると、応答に `operationRevision`・`bridgeInstanceId`・`operationId: null` が足されている。

## 2. 前提つきの close／launch

1. `vrcrt.sh bridge --json` と `vrcrt.sh status --json` から、slot 1 の expect（7つの値）をファイルに書き、`vrcrt.sh close-client 1 --expect @<file> --operation-id manual-close --json`。
2. 期待すること: 終了コード 0、`operationRevision` が expect + 1、`clientGeneration` が slot 1 の世代、`operationId` がそのまま返る。
3. 同じ expect の `operationRevision` を close の答えの値にし、`clientGeneration` は閉じた世代のまま `launch-client 1 --expect … --operation-id manual-launch --json`。終了コード 0、revision がさらに +1、新しい世代。

## 3. 古い前提は何もしない

1. 2 のあとで、2 の最初の expect（古い revision）のまま `close-client 0 --expect @<file>`。
2. 期待すること: 終了コード 1、標準エラーに `precondition failed: operationRevision`（ほかの名前も出てよい）。slot 0 は動いたまま、`bridge --json` の revision は変わらない。
3. 本文なしの `close-client 1` で先に閉じてから、閉じる前の世代の expect で `close-client 1 --expect …`。期待: 409 notRunning（終了コード 1）か 412、どちらでも何もしない。
4. `vrcrt.sh restart` で Bridge の host を起動し直したあと、前の expect のまま送る。期待: 412（`hostGeneration` など）。
5. 形の違う expect（キーが1つ欠ける）: CLI が送らずに終了コード 2。

## 4. Unity の台の表（前提つき）

1. VRChat Clients の表で slot 1 の `Close`。確認の画面に「表示のあとに台か Bridge が変わっていれば、Bridge は何もしない」とある。閉じる。
2. `Launch`。期待: 起動し直す。
3. 表を出したまま、ターミナルで本文なしの `vrcrt.sh close-client 1` と `launch-client 1` を打ってから（表が古いまま）、画面の `Close` を押す。期待: 「一覧を読んだあとに変わった。読み直して」の旨で、何もしない。

## 5. leave-rejoin（`--unvalidated`）

```bash
python3 package/Tools~/vrcrt_runner.py run --build headless --unity /Applications/Unity/Hub/Editor/2022.3.22f1/Unity.app/Contents/MacOS/Unity --project <試験用のプロジェクト> --scene Assets/Scenes/VrcrtTagScene.unity --slots 0,1 --procedure leave-rejoin --target-slot 1 --unvalidated
```

期待すること:
- 終了コード 0。`run.json` の results で deploy・launch・clientReadiness・worldBuildMatch・evidence・leaveRejoin が PASS。
- slot 1 が閉じて起動し直し、新しい世代で slot 0 と同じインスタンスに joined。slot 0 は世代・epoch・joinedSeq とも変わらない。
- 新しい世代でも tag の目印が照らされる。
- 証拠が、close の前・残る台・新しい世代に分かれて保存され、`run.json` の evidence の sha256 と合う。
- `--unvalidated` を外すと、prepare が終了コード 2 で断る。

## 6. 書き出しの片付け

- Bridge の `state\exports` に、作ってから 24 時間を過ぎた `<32桁の16進>.log` があれば、host の起動時（または1時間以内）に消える。1日待てないときは、Windows 側のセッションに、古い書き出しの更新日時を 25 時間前にしてから `vrcrt.sh restart` するよう頼み、消えたことを見る。ほかの名前のファイルは残る。

## 記録

### （日付、Bridge の版と SHA-256、パッケージの commit）

| 項目 | 結果 |
|---|---|
| 環境（Windows、VRChat の版、Bridge の版と SHA-256、パッケージの commit、Unity の版） | |
| 1. 本文なしの close／launch（出力・終了コードが v0.11 と同じか） | |
| 2. 前提つきの close／launch（revision +1、世代、operationId） | |
| 3. 古い前提（revision・先に閉じた台・再起動・形の誤り） | |
| 4. Unity の台の表（前提つき、古い表での断り） | |
| 5. leave-rejoin（results、所要時間、証拠） | |
| 6. 書き出しの片付け | |
| 4台 | 未確認 |

## 気づいたこと

## 結論
