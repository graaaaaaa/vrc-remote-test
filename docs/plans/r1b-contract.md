# R1b の契約（Bridge・CLI・runner・Unity のあいだ）

`docs/plans/r1-automation.md` §5 の R1b を、4つの部分（Bridge、`vrcrt.sh`、`vrcrt_runner.py`、Unity）で並列に作るときに守る形です。ここに無いことは §5 に従います。迷ったら、今の動きを変えない側、UNKNOWN の側に倒します。

## 1. Bridge の API（v0.12.0、`apiVersion` は 1 のまま）

### 1.1 `GET /api/v1/capabilities`

今の4つのフィールドに `features` を足す。

```json
{"apiVersion": 1, "buildProtocolVersion": 1, "maxArtifactSizeBytes": 524288000, "maxClients": 4,
 "features": ["slotPreconditions.v1"]}
```

- 前提条件を読むのは、`features` が配列で、文字列 `slotPreconditions.v1` を含むときだけ。`features` が無い（v0.11 以前の Bridge）、配列でない、読めない場合は「無い」と同じに扱う。

### 1.2 `GET /api/v1/bridge` に足すフィールド

```json
{"bridgeInstanceId": "3f2a…（16進の小文字32字）", "operationRevision": 17,
 "currentBatch": {"buildId": "20261126T…-…", "launchedSlots": [0, 1]}}
```

- `bridgeInstanceId`: Bridge のプロセスごとの乱数（128ビット）。再起動すると変わる。
- `operationRevision`: 0 以上の整数。プロセスのあいだ減らない。Windows で何かが変わった、または変わったかもしれないときに増える。
  - 配備: 始める前と、結果を書く**前**（結果を書かずに終わったときは終わりに）。結果を書いたあとは動かさないので、Unity が結果を読んだあとに読んだ値は、その配備のあとの値。
  - 台を閉じる・起動する試み、設定の書き込み、台を閉じた設定の切り替え、全台の close、host の起動。
  - 副作用の前に断った要求（何も閉じなかった全台の close や設定の切り替えも）では増えない。
- `currentBatch`: 今の Relaunch の起動の計画。`buildId` は、その計画を作った配備の build ID。計画が無いとき（Reload の配備、配備の失敗、host の再起動のあと）と、全台を起動できなかったときは null。`launchedSlots` は、1台ずつ起動し直せる slot（全台を起動できなかったときも、起動できた slot は残る）。計画が無ければ空の配列。
- 既存のフィールドはそのまま（`hostGeneration`、`activeConfigEtag`、`savedConfigEtag`、`processStartedAtUtc` など）。

### 1.3 `POST /api/v1/clients/{slot}/close` と `/launch` の本文

- 本文の有無は、実際に読んだバイトで決める（Content-Length は見ない。chunked も同じ）。読むのは 4096 バイトまで。
- **0 バイトのとき**: 今とまったく同じ動きと HTTP の状態。応答には下の §1.4 のフィールドが足されるだけ。
- **1 バイト以上のとき**: 次をすべて満たすときだけ受ける。
  - `Content-Type` が `application/json`（`; charset=utf-8` は可）。
  - JSON の object で、どの段にも名前の重複が無い。
  - 一番上のキーは `expect`（必須）と `operationId`（任意）だけ。
  - `expect` は、次の7つのキーをちょうど持つ object。

| キー | 型と形 |
|---|---|
| `bridgeInstanceId` | 文字列、`^[0-9a-f]{32}$` |
| `hostGeneration` | 整数、1 以上（32 ビットまで） |
| `activeConfigEtag` | 文字列（1〜128字）または null |
| `savedConfigEtag` | 文字列（1〜128字）または null |
| `operationRevision` | 整数、0 以上 |
| `buildId` | 文字列 `^[0-9A-Za-z_-]{1,128}$` または null |
| `clientGeneration` | 整数、1 以上（64ビット） |

  - `operationId` は文字列 `^[A-Za-z0-9_-]{1,64}$`。
- 満たさなければ **400** `{"error": "invalidPrecondition", "problems": ["…"]}`。副作用の前に断り、revision は増えない。欠け・余分・重複・型違い・JSON でない・大きすぎ・Content-Type 違いのどれも、本文なしとして扱わない。
- 本文は門（operation gate）の前に読む。門がふさがっていれば今と同じ **409** `{"error": "busy", …}`。
- 門に入った直後に、プロセスの値を照らす: `bridgeInstanceId`、`hostGeneration`、`activeConfigEtag`、`savedConfigEtag`（`/bridge` と同じ求め方）、`operationRevision`、`buildId`（`currentBatch.buildId`）。
- 1つでも違えば、何もせず **412**（下の形）。
- 合っていれば、今の close／launch の確認（slot の範囲、Relaunch、記録、走っているかなど）を今の順で行う。そのうえで `clientGeneration` を照らし、違えば 412 にする。
  - close: 記録と4点で一致した、今閉じようとしている台の世代。
  - launch: この計画のあいだに 1台の close が閉じたと確かめた、その slot の最後の世代。それが無ければ、記録にある（もう動いていない）その slot の台の世代。どちらとも違えば 412。どちらも無いときも 412（current の `clientGeneration` は null）。

412 の形:

```json
{"error": "preconditionFailed", "mismatched": ["operationRevision"],
 "current": {"bridgeInstanceId": "…", "hostGeneration": 3, "activeConfigEtag": "…", "savedConfigEtag": "…",
             "operationRevision": 18, "buildId": "…", "clientGeneration": null},
 "operationId": "…（送られたときだけ。無ければ null）"}
```

- `mismatched` は、照らして違ったキーの名前の配列（上の7つの名前のうち）。
- `current.clientGeneration` は、プロセスの値で止まったときは null。

### 1.4 応答に足すフィールド（本文の有無にかかわらず）

close の 200 と 409（確かめられなかった）、launch の 200 と 500（`launchedNotRecorded`）に足す。

- `operationRevision`: 操作の**あと**の値。
- `bridgeInstanceId`
- `operationId`: 送られた値。無ければ null。
- close には `clientGeneration`（閉じようとした台の世代）も足す。launch には今も `clientGeneration`（新しい台）がある。

断りの応答（`{"error": "notRunning", "problems": […]}` などの 400／409）の形は変えない。

### 1.5 revision の増え方（close／launch）

- close は、台を閉じる処理（terminator）を呼んだときにちょうど 1 増える。閉じたと確かめられなくても増える。
- launch は、プロセスを起動しようとしたときにちょうど 1 増える。起動に失敗しても増える。
- 断り・400・412・409 busy では増えない。
- よって、ほかに何も起きていなければ、200 の `operationRevision` は、送った `expect.operationRevision` + 1 になる。

### 1.6 書き出しの片付け

- Bridge は、自分の `state/exports` にある `<32桁の16進>.log` と `<32桁の16進>.part` のうち、作ってから 24 時間を過ぎたものを、host の起動時と1時間ごとに消す。
- 名前の形が違うもの、リンク、フォルダーは触らない。

## 2. `vrcrt.sh`（`package/Tools~/vrcrt.sh`）

```
vrcrt.sh close-client <slot> [--expect @FILE] [--operation-id ID] [--json]
vrcrt.sh launch-client <slot> [--expect @FILE] [--operation-id ID] [--json]
```

- **`--expect` が無いとき**: 今と同じ要求（本文なし）、同じ出力の1行、同じ終了コード。
  - `--json` を付けたときだけ、1行の代わりに検証した応答の JSON を標準出力に出す（終了コードは同じ）。
  - `--operation-id` は `--expect` が無ければ使い方の誤り（終了コード 2）。
- **`--expect @FILE` のとき**: FILE は §1.3 の `expect` の object だけを持つ JSON。
  1. FILE を読んで同じ規則で検証する。形が違えば何も送らず終了コード 2。
  2. `GET /capabilities` を読み、`slotPreconditions.v1` が無い・読めなければ、POST を送らずに終了コード 3、標準エラーに `unsupported` を含む1行。
  3. `{"expect": …, "operationId": …}`（`operationId` は付けたときだけ）を `Content-Type: application/json` で POST する。
  4. 結果ごとの終了コード:
     - 200: 終了コード 0。出力は `--expect` が無いときと同じ（`--json` なら応答の JSON）。
     - 412: 終了コード 1。標準エラーに `precondition failed: <mismatched をカンマで>`。`--json` なら 412 の本文を標準出力に出す。
     - 400 `invalidPrecondition`: 終了コード 2。
     - それ以外（409 busy、断り、close の未確認、launch の不明・`launchedNotRecorded`、応答なし）: 今と同じ終了コード。launch で起動したか分からないときの 3 も同じ。
  5. 送り直しはしない。
- `bridge --json` は、§1.2 で足したフィールドを落とさずに出す。v0.11 の Bridge（無いとき）でも今と同じに動く。
- ほかのサブコマンド、終了コード 0/1/2/3 の意味、`scripts/vrcrt.sh` の入口は変えない。Commander の `dice_life_auto` は `--expect` 無しで呼ぶ。

## 3. runner（`package/Tools~/vrcrt_runner.py`）

- **確かめ済みの手順の一覧**: `VALIDATED_PROCEDURES = {"deploy-collect": "0.11.0"}`。leave-rejoin は一覧に無いあいだ、`--unvalidated` を付けたときだけ動く。付けなければ、prepare と run は終了コード 2 で断る。
- **deploy-collect の追加の照合**: Bridge が `slotPreconditions.v1` を告げ、mode が Relaunch のときだけ、次を照らす。
  - 台の準備を固定するときに、`/bridge` の `currentBatch.buildId` が結果ファイルの buildId と同じこと。
  - `operationRevision` が、配備の結果を読んだあとから動いていないこと。照らし直しのたびに見る。
  - 違えば UNKNOWN（`batchChanged`／`revisionChanged`）。
  - feature が無ければ、今の R1a の動きのまま。
- **leave-rejoin**:
  - 引数（prepare と run）: `--procedure leave-rejoin --target-slot N --unvalidated [--remaining-marker REGEX]`。`--slots` に N が含まれること。continue は deploy-collect のあとに続けて行う。
  - 手順は §5.4 の 1〜8。expect の値は、直前に読んだ `vrcrt.sh bridge --json` と、固定した準備（世代）から作り、`run.json` に先に書いてから送る。
  - close の expect は `clientGeneration` = 対象の台の今の世代。launch の expect は、close の 200 の `operationRevision` と、閉じた世代。
  - 呼び方: `vrcrt.sh close-client N --expect @<run>/expect-close.json --operation-id <runId>-close --json`。launch も同じ（`-launch`）。
  - 終了コード 0 の応答だけを受け入れる。そのうえで、`operationRevision` が expect + 1 で、close の `clientGeneration` が対象の世代であること。
  - 終了コード 1（412 を含む）、2、3、応答なしは、送り直さずに `/bridge` と `/clients` を読み直して記録し、UNKNOWN で止める。
  - close のあとは、`/clients` の `updatedAtUtc` が close の応答より後になり、対象の slot が `running: false` になるまで待つ。台の一覧は約10秒遅れることがある。
  - 結果: `results.leaveRejoin`（PASS／FAIL／UNKNOWN／CANCELED）。
  - 証拠: close の前、残る台、新しい世代を `evidence/` の下に分けて置く（形は runner で決め、`run.json` の evidence に記録する）。
- feature が無い Bridge には、前提つきの close／launch を1回も送らない（leave-rejoin は何もせず UNKNOWN）。

## 4. Unity（`package/Editor`）

- `BridgeInfo` に `BridgeInstanceId`、`OperationRevision`、`CurrentBatch {BuildId, LaunchedSlots}` を足す（無ければ null）。capabilities の `Features` を読む。
- Transport の `CloseClientAsync`／`LaunchClientAsync` に、任意の前提（§1.3 の `expect`）と `operationId` を足す。
  - 前提が null なら、今と同じ本文なしの要求。
  - 前提があれば、§1.3 の本文を `application/json` で送る。
- `SlotOperationReport` は、412 `preconditionFailed` を「一覧を読んだあとに変わった。読み直して」と読む。送り直さない。§1.4 で足したフィールドがあっても、今の読み方を変えない。
- ウィンドウの Close／Launch:
  - Bridge が feature を告げるときは、表の行の世代と、直近（15 秒以内）の `/bridge` の値で前提をつけて送る。古ければ、先に読み直す。
  - 確認の画面の文言は「表示のあとに変わっていたら Bridge は何もしない」にする。
  - feature が無ければ R1a と同じ（本文なし、今の文言）。
- leave-rejoin は画面に出さない（確かめ済みの一覧に入るまで）。

## 5. 互換（変えないこと）

- `--expect` 無しの `vrcrt.sh close-client`／`launch-client`（Commander の `dice_life_auto`）: 出力、終了コード、要求の形。
- 本文なしの close／launch の HTTP の状態と、今あるフィールド。
- v0.11 の Bridge に対する、新しい CLI・runner・Unity の動き: R1a と同じ。
- `scripts/test-vrcrt.sh`、`test-vrcrt-runner.sh`、`test-vrcrt-run.sh`、Unity の既存の試験は、そのまま通る（足すだけ）。

## 6. 分かっている限界

- leave-rejoin の `--remaining-marker` は、close を送る直前に読んだ位置より後の目印を数える。その位置を読んでから close が届くまでのごく短い間に出た目印も数えうるので、正規表現は退出のときだけ出る目印に当てる。
- 前提条件は、Bridge と台が一覧を読んだときから変わっていないことを確かめるだけで、認証の代わりではない。トークンが漏れれば、その持ち主は前提なしの close／launch もできる。
