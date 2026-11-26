# 前提つきの close／launch と leave-rejoin の実機の確かめ（R1b、v0.12.0）

Bridge 0.12.0 の前提条件（`docs/plans/r1b-contract.md`）と、runner の leave-rejoin（`docs/plans/r1-automation.md` §5.4）を Windows 実機で確かめる手順と、その記録です。leave-rejoin は、2026-11-26 にここで PASS を記録した commit で runner の `VALIDATED_PROCEDURES` に足しました。それより前の runner では `--unvalidated` を付けたときだけ動きます。

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

## 5. leave-rejoin

```bash
python3 package/Tools~/vrcrt_runner.py run --build headless --unity /Applications/Unity/Hub/Editor/2022.3.22f1/Unity.app/Contents/MacOS/Unity --project <試験用のプロジェクト> --scene Assets/Scenes/VrcrtTagScene.unity --slots 0,1 --procedure leave-rejoin --target-slot 1
```

期待すること:
- 終了コード 0。`run.json` の results で deploy・launch・clientReadiness・worldBuildMatch・evidence・leaveRejoin が PASS。
- slot 1 が閉じて起動し直し、新しい世代で slot 0 と同じインスタンスに joined。slot 0 は世代・epoch・joinedSeq とも変わらない。
- 新しい世代でも tag の目印が照らされる。
- 証拠が、close の前・残る台・新しい世代に分かれて保存され、`run.json` の evidence の sha256 と合う。

## 6. 書き出しの片付け

- Bridge の `state\exports` に、作ってから 24 時間を過ぎた `<32桁の16進>.log` があれば、host の起動時（または1時間以内）に消える。1日待てないときは、Windows 側のセッションに、古い書き出しの更新日時を 25 時間前にしてから `vrcrt.sh restart` するよう頼み、消えたことを見る。ほかの名前のファイルは残る。

## 記録

### 2026-11-26（UTC）、Bridge 0.12.0+4892f91、パッケージ main 4892f91

Mac のエージェントが CLI と computer use で確かめた。Windows で人がしたことは無い（Bridge の入れ替えは Windows 側のセッション）。別のセッションの性能計測と重ならないよう、そのセッションと合意した時間（09:14〜09:22 JST）に、`VrchatProfiles` を `[0,1]` にして行い、終わったら `[0]` に戻した。

| 項目 | 結果 |
|---|---|
| 環境 | Bridge `0.12.0+4892f91a0ad230b5bd2732dd3af48d857327fb46`、exe SHA-256 `a69a379a5637b527f74050d901c22a9424e50e336df1e66da755bbd79f3bae6e`（GitHub Actions の run 45726091307 の成果物）。パッケージ main `4892f91`（0.12.0）。Unity 2022.3.22f1（Mac）。Relaunch profiles `[0,1]`、Desktop。`/bridge` に `bridgeInstanceId`・`operationRevision`・`currentBatch` が出る。版の違いの警告は消えた |
| 1. 本文なしの close／launch | **PASS**。`close-client 1` は `closed slot 1 (PID 29264)`、終了コード 0（v0.11 と同じ1行）。`launch-client 1 --json` は終了コード 0、応答に `operationRevision`（8→9）・`bridgeInstanceId`・`operationId: null` が足されている |
| 2. 前提つきの close／launch | **PASS**。expect の revision 9 で `close-client 1 --expect … --operation-id manual-close --json` → 終了コード 0、revision 10（+1）、`clientGeneration` が対象の世代、`operationId` がそのまま返る。続けて revision 10・閉じた世代で `launch-client 1 --expect …` → 終了コード 0、revision 11（+1）、新しい世代で joined |
| 3. 古い前提は何もしない | **PASS**。古い revision の expect で `close-client 0` → 終了コード 1、`precondition failed: operationRevision`、slot 0 は動いたまま、revision も変わらない。本文なしで先に閉じた台へ前提つき close → 409 `notLaunchedByBridge`（何もしない）。キーが欠けた expect → 終了コード 2、何も送らない。`vrcrt.sh restart`（host 2→3、同じプロセスなので `bridgeInstanceId` は同じ）のあと、前の expect で close → 412 `hostGeneration,operationRevision,buildId` |
| 4. Unity の台の表（前提つき） | **PASS**。Bridge 0.12 では確認の画面が前提つきの文言（「一覧を読んだあとに台か Bridge が変わっていれば、Bridge は何もしない」）。確認の画面を開いたまま、ターミナルで slot 1 を本文なしで閉じて起動し直し、観測が追いついてから画面の Close を押すと、`The Bridge did not close slot 1: the client or the Bridge changed after the client list was read (clientGeneration differ). Nothing was done.`、slot 1 は動いたまま。観測が追いつく前に押したときは、R1a からある観測の照合で `slotMismatch` になり、これも何もしない。新しい一覧からの Close（`Closed slot 1 (PID 22512).`）と Launch（`Launched slot 1 (profile 1, PID 22640, …)`、slot 0 と同じインスタンスに joined）は通った。画面の Run も PASS（run `20261126T001914Z-ac6f991d`、35 s、起動の計画と revision の照合を含む） |
| 5. leave-rejoin | **PASS**。run `20261126T001456Z-278f717c`（`--unvalidated` 付き、シーン B）、build `20261126T001519859Z-e07d9fa1`。00:14:56〜00:16:00（64 s）。results すべて PASS（deploy・launch・clientReadiness・worldBuildMatch・evidence・leaveRejoin）。revision は配備で 3→5、close で 5→6、launch で 6→7。close 2 s、退出の確認 5 s、起動し直しの準備 9 s、新しい世代の tag 1 s。slot 0 は世代・epoch・joinedSeq とも変わらない。証拠 22 件（deploy・beforeClose・afterClose・afterRejoin）の sha256 が一致 |
| 6. 書き出しの片付け | 未確認（24 時間を待つか、Windows 側でファイルの日時を変える必要がある）。fixture の試験だけ |
| 0.10 の Bridge との互換（入れ替えの前） | 上の「入れ替えの前」の記録のとおり、新しい CLI は何も送らずに終了コード 3 |
| 4台 | 未確認 |

## 気づいたこと

- 画面の古い一覧からの Close は、Bridge の観測が追いつく前だと R1a からある `slotMismatch` が先に効き、追いついたあとだと前提（`clientGeneration`）が効く。どちらでも何もしない。
- `vrcrt.sh restart` は host を起動し直すだけなので `bridgeInstanceId` は変わらず、`hostGeneration` と revision・起動の計画の build ID の違いで古い前提を断る。Bridge のプロセスを起動し直したときは `bridgeInstanceId` も変わる（入れ替えで確かめた: 起動直後の revision は 1）。

## 結論

R1b は、本物の Bridge 0.12.0 と VRChat 2台で、本文なしの close／launch が今までどおり、前提つきの close／launch が revision をちょうど1つずつ進め、古い前提や形の誤りは何もしないことを確かめた。leave-rejoin は64秒で PASS した。これをもって runner の `VALIDATED_PROCEDURES` に leave-rejoin を足した。書き出しの片付けと4台は未確認。
