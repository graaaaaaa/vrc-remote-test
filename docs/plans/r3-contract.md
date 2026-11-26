# R3 の契約（v0.13.0、人の段: manual-steps）

runner・CLI・Unity のあいだの取り決めです。実装はこれに合わせ、違いが見つかればまずここを直します。計画の背景は `docs/plans/r1-automation.md` §3・§9、R1b の前提つきの close／launch は `docs/plans/r1b-contract.md`。

Bridge は変えません（`apiVersion` 1、features `slotPreconditions.v1` のまま）。VR 台＋観戦台は今の Bridge の設定（`VrchatMode: VR`、`VrchatClientMode: Relaunch`、`VrchatProfiles: [0,1]`）で、slot 0 だけが VR、ほかは `--no-vr` で起動します。VR での確かめはまだしていません（§9）。

## 1. 確認の項目のファイル（checklist）

利用者が書く JSON です。runner に `--checklist FILE` で渡します。

```json
{
  "schema": "vrcrt.checklist",
  "schemaVersion": 1,
  "title": "手札の並べ替え",
  "items": [
    {"id": "grid-insert", "text": "格子を開き、カードを別の所に近づけて、真鍮色の線が出るか見る"},
    {"id": "desktop-click", "text": "デスクトップ: カードを選んで別のカードを指し、クリックで入るか"}
  ]
}
```

- キーはちょうどこれだけ（最上位の4つ、各項目の `id` と `text`）。名前の重複・欠け・余分は断る。
- `title`: 1〜120字。`items`: 1〜20件。`id`: `^[a-z0-9][a-z0-9_-]{0,31}\z`、重複なし。`text`: 1〜1000字。字数は Unicode の符号位置で数える。
- ファイルは UTF-8 の JSON で 64 KiB まで。ふつうのファイルだけ（リンク・FIFO は断る）。
- prepare が検証して、run のフォルダーに `checklist.json`（0600）として**そのままのバイト**を写し、`procedure.parameters.checklistSha256` に sha256 を入れる（手順のハッシュに入る）。元のファイルは以後読まない。continue は写しを読み直し、sha256 が合わなければ UNKNOWN `procedureInvalid`。
- `title` と `text` は人に見せるだけの文字です。runner も Unity も、中身から対象の slot・パス・次の手順・引数を作りません。表示するときは、改行のほかの制御文字（C0、DEL、C1、U+2028/2029、双方向の制御 U+202A〜202E・U+2066〜2069）を `\uXXXX` の形にします。Unity は rich text を切ります。

## 2. CLI（`package/Tools~/vrcrt_runner.py`）

### 2.1 prepare と run

```
prepare|run ... --procedure manual-steps --checklist FILE --unvalidated
        [--leave-rejoin-after ITEM_ID --target-slot N [--remaining-marker REGEX]]
        [--answer-wait SECONDS] [--run-minutes MINUTES]
```

| 引数 | 決まり |
|---|---|
| `--checklist` | manual-steps のときは必ず。ほかの手順では断る（終了コード 2） |
| `--answer-wait` | 1項目の答えを待つ上限。60〜3600 秒、既定 1200（`proposed`）。manual-steps だけ |
| `--run-minutes` | run 全体の期限。30〜120 分、既定 90（`proposed`）。manual-steps だけ。ほかの手順は 30 分のまま（この引数を断る） |
| `--leave-rejoin-after` | checklist の id のどれか（最後の項目でもよい）。その項目の答えのあと、`--target-slot` を前提つきで閉じて起動し直す |
| `--target-slot` | `--leave-rejoin-after` のときだけ、そのときは必ず。`--slots` の中 |
| `--remaining-marker` | `--leave-rejoin-after` のときだけ。leave-rejoin と同じ意味 |

- `--leave-rejoin-after` のときは、leave-rejoin と同じく slots が2台以上で、prepare が配備の前に Bridge の `slotPreconditions.v1` と Relaunch を確かめる（無ければ何も送らず UNKNOWN）。
- 決めるのは引数だけ。checklist・目印・ログから対象や手順を決めない。
- 期限: prepare で決めた絶対の時刻（`createdAtUtc + run-minutes`）を、`deadlineUtc`、claim の `deadlineUtc`、prepare が作る `Run` の単調時計の期限（headless のビルドもこれを使う）、continue の `Run` のすべてで使う。
- `procedure.parameters` に足すもの: `checklistSha256`、`itemCount`、`answerWaitSeconds`、`runMinutes`、`leaveRejoinAfter`（null 可）、`targetSlot`、`remainingMarker`、`unvalidated`、`leaveWaitSeconds`・`rejoinWaitSeconds`（leave-rejoin のときだけ）。
- `VALIDATED_PROCEDURES` に manual-steps が入るまでは `--unvalidated` が要る。

### 2.2 waiting

```
waiting --run DIR [--json]
```

- 読むだけ。今の待ちを出す。`--json` なら1行の JSON:
  `{"runId", "waiting": true|false, "valid": true|false, "waitId", "index", "count", "itemId", "title", "text", "startedAtUtc", "deadlineUtc", "items": [{"id", "state", "answer"}]}`
  - `title` と `text` は §1 の決まりで見える形にしたもの。`items` は run.json の項目の今の state（§5）。
  - `valid` は §3.3 の「有効な待ち」の条件をすべて満たすとき true。
- 有効な待ちがあれば終了コード 0、無ければ 1（`--json` でも出力はする）。run のフォルダーが違えば 2。
- `--json` なしは人が読む形（`item 2/3 grid-insert`、文、残りの分）。

### 2.3 answer

```
answer --run DIR --wait-id ID --item ITEM_ID (ok|ng|skip) [--memo TEXT] [--by unity|cli]
```

- run のフォルダーの `answer.lock` を `flock` で取り、その中で次をすべて満たすときだけ、`answer-<waitId>.json` を公開する。
  1. §3.3 の有効な待ちで、`--wait-id` と `--item` が waiting.json と一致する。
  2. `answer-<waitId>.json` がまだ無い。
- 公開は、同じフォルダーの一意な一時ファイル（`.answer-<waitId>.<乱数>.part`、0600、`O_CREAT|O_EXCL|O_NOFOLLOW`）に書いて fsync し、`link(2)` で `answer-<waitId>.json` に付けてから一時ファイルを消す。既にあれば link は失敗し、上書きしない。
- 終了コード: 0＝公開した（runner が受けたことではない）、1＝断った（何も書かない。理由を標準エラーに1行）、2＝引数の誤り。
- `--memo`: Unicode の符号位置で 2000字まで（越えれば終了コード 2）。改行（`\n`）とタブのほかの制御文字は捨てる。単独のサロゲートを含めば終了コード 2。`--by` の既定は `cli`。`by` は自己申告で、認証ではない。
- 答えのファイルは UTF-8 のまま（`\uXXXX` に逃がさない）の JSON で書き、公開の前に 16 KiB 以下であることを確かめる（越えれば終了コード 1、何も書かない）。
- 「有効な待ち」かどうかは、`answer.lock` の中で、`link` の直前に確かめる。この時刻が答えの受付の時刻。
- 受けたかどうかは、run.json の項目の `state` が `answered` になったことで見る（`waiting --json` の `items` にも出る）。

## 3. run のフォルダーの新しいファイル

すべて 0700 の run のフォルダーの中、0600。

### 3.1 waiting.json（runner だけが書く。`write_json` で置き換え）

待っているとき:

```json
{"schema": "vrcrt.waiting", "schemaVersion": 1, "runId": "...", "waiting": true,
 "waitId": "<32桁の16進>", "index": 1, "count": 3, "itemId": "grid-insert",
 "startedAtUtc": "...", "deadlineUtc": "..."}
```

待っていないとき（待ちを閉じたあと）: `{"schema": "vrcrt.waiting", "schemaVersion": 1, "runId": "...", "waiting": false, "closedAtUtc": "...", "lastWaitId": "..."}`

- `index` は 1 から。`deadlineUtc` は、項目の期限（`startedAtUtc + answerWaitSeconds`）と run の期限の早いほう。
- 項目の文は入れない。読む側は run のフォルダーの `checklist.json` から `itemId` で引く（`waiting` のサブコマンドはそうする）。Unity は自分で waiting.json を解釈せず、`waiting --json` を呼んで、その `valid`・`title`・`text` を使う（§6.2）。

### 3.2 answer-&lt;waitId&gt;.json（`runner answer` だけが書く）

```json
{"schema": "vrcrt.answer", "schemaVersion": 1, "runId": "...", "waitId": "...", "itemId": "grid-insert",
 "answer": "ok", "memo": "線は見えた", "by": "unity", "writtenAtUtc": "..."}
```

- キーはちょうどこれだけ。UTF-8 で 16 KiB まで。`answer` は `ok`・`ng`・`skip`。`memo` は 0〜2000字で単独のサロゲートなし。

### 3.3 answer.lock と「有効な待ち」

- `answer.lock` は runner の待ちと `answer` の公開を直列にするための空のファイル（`O_RDWR|O_CREAT|O_NOFOLLOW`、0600、`flock` の排他）。
- **有効な待ち**は次のすべて:
  - waiting.json が §3.1 の形で `waiting: true`、`runId` が run.json と同じ。
  - run.json の `state` が `running`。
  - run.json の `claimKey` の claim（`locks/<key>.json`）の `runId` がこの run で、その `owner` の PID が起動時刻ごと生きている（`same_process`）。
  - 今が waiting.json の `deadlineUtc` と run.json の `deadlineUtc` より前。
- runner は、待ちを閉じる（答えを受けた・期限・取り消し・照合の失敗・止める）とき、`answer.lock` を取ってから waiting.json を「待っていない」に置き換え、項目の state を run.json に書く。`Run.finish` と `finalize_interrupted`（`recover`・次の prepare の引き継ぎ）も、`waiting: true` が残っていれば同じく閉じ、項目を `interrupted` にする。手順は再開しない。
- 閉じたあとに公開された答えは読まず、記録にも入れない。

### 3.4 答えの読み方（runner）

- `answer.lock` を取り、`answer-<waitId>.json` を `O_RDONLY|O_NOFOLLOW|O_NONBLOCK` で開き、同じ descriptor の `fstat` で、ふつうのファイル・所有者が自分・グループと他人の書き込みなしを確かめ、16 KiB まで読む（新しい `read_owned_json`。checklist の読みにも使う）。
- 無ければ待ち続ける。リンク・FIFO・ほかの人のファイル・形の違い・runId／waitId／itemId の違い・大きすぎ・JSON でない → 受けずに待ちを閉じ、UNKNOWN `invalidAnswer` で止める（それまでの答えは残す）。
- 一時ファイル（`.answer-*.part`）は読まない。

## 4. 手順の流れ（runner）

`continue_run` で deploy-collect が PASS のあと、`manual_steps(run, fixed, guard)` に入る。deploy-collect が PASS でなければ聞かない（全項目 `notAsked`）。

1. 項目を順に聞く。各項目で:
   1. 新しい `/clients` と全 slot の目印（`log <slot> --markers --json`）を読み、各 slot の**採取のカーソル**を記録する（これが `from`）。採取のカーソルは `max(marker_cut(目印), /clients のその slot の observedThroughSeq)`。目印の見出しと `/clients` の項目の世代・epoch が固定と違う、`catchingUp`、一覧が古い、`observedThroughSeq` が無いときは、カーソルは無し（理由つき）。
   2. waiting.json を書き（新しい waitId）、項目を `waiting` にし、progress に `{"kind": "ask", "message": "<itemId>", "index", "count", "waitId"}` を出す。
   3. 0.5 s ごとに: まず取り消し（`cancel.request`）と run の期限を見る（どちらも答えより先）。次に `answer.lock` を取り、答えのファイルを見て、あれば受ける。無くて項目の期限が来ていれば、同じ lock の中で待ちを閉じ、項目を `unanswered` にしてから lock を外す。期限の直前に公開された答えは、期限のあとに読んでも受ける。
   4. 5 s ごとに: `recheck` と同じ照合（固定の準備と guard。`observedThroughSeq`・`isMaster` は比べない）。operations には足さない。
      - 世代・epoch・joinedSeq・joinAttemptSeq・instance・エラーワールド・ログイン・Bridge のプロセスや host や etag・launch の計画・revision・busy が変われば: 待ちを閉じ、項目は `interrupted`、run は UNKNOWN `clientChanged`（何が変わったかを `detail` に）。
      - 観測が古い・`catchingUp`・一覧が読めない: `observationIncomplete` として数え、続けて 15 s になれば同じく止める（理由 `observationIncomplete`）。一度でも通れば数え直す。
   5. 答えを受けたら: 待ちを閉じ、項目を `answered` にして答え・memo・by・`answeredAtUtc`（答えの `writtenAtUtc`）を書く。続けて新しい `/clients` と全 slot の目印を読み、1 と同じ決まりで採取のカーソル（`to`）を出し、目印を `evidence/manual-steps/<index>-<itemId>/slot-<n>/g<gen>-e<epoch>/markers.json` に保存し（`phase: "item:<itemId>"`。世代・epoch が固定と違う目印は保存しない）、`seqRange[slot] = {"from", "to"}`（(from, to] の意味）と `markersTakenAtUtc` を書く。
      - `from` か `to` が無い、`to < from`（`cursorWentBack`）、`droppedThroughSeq > from`（`markersDropped`）なら、その項目の `evidenceGap` に `{slot, reason}` を書き、`results.evidence` を UNKNOWN にする。`markersDropped` のほかは `to` を null にする。手順は続ける。あとの採取がうまくいっても、`results.evidence` を PASS に戻さない。
      - Bridge の観測は遅れるので、seqRange は「答えた時刻までに起きたこと」の正確な境目ではない。
   6. 期限までに答えが無ければ: 待ちを閉じ、項目は `unanswered`、run は UNKNOWN `answerTimeout`。残りは `notAsked`。
   7. ng・skip でも次の項目へ進む。
2. `--leave-rejoin-after` の項目のあと（答えが何でも）: R1b の leave-rejoin の段（`leave_rejoin_steps`）を、その時点の fixed と guard で回す。証拠は `evidence/manual-steps/leave-rejoin/<before-close|after-close|after-rejoin>/`。
   - `leave_rejoin_steps` は `(result, reason, final_fixed, final_guard)` を返す。`final_guard` の revision は、自分の close と launch の応答の値。`final_fixed` は起動し直した slot だけ新しい世代の準備の値で、ほかは前のまま。
   - PASS のときは、最後の項目かどうかにかかわらず、すぐに呼び出し元の fixed と guard を `final_fixed` と `final_guard` に替える。あとの項目・`endSnapshot`・`manualStepsFinalCheck` は替えた値を使う。その時点の最新の観測から作り直さない。PASS でなければ止める（台が閉じたままかもしれないことは R1b と同じく記録する。送り直さない）。
   - 結果は `results.leaveRejoin`、記録は `record["manualSteps"]["leaveRejoin"]`（R1b の `leaveRejoin` と同じ形）。
3. 全項目のあと: `endSnapshot`（全 slot の目印と全文ログ、一覧を `evidence/manual-steps/end/` に。`phase: "end"`）、`manualStepsFinalCheck`（全 slot を照らす）。
- operations に足すのは `manualSteps`（聞いているあいだで1つ。leave-rejoin を挟めば、その前と後で1つずつ）、`endSnapshot`、`manualStepsFinalCheck` と、leave-rejoin の 12 だけ。最大 9＋1（headless）＋2＋12＋2＝26。

## 5. run.json

- `results` に `manualSteps` を足す（prepare では null）。`--leave-rejoin-after` のときは `leaveRejoin` も（null）。
- `record["manualSteps"]`:

```json
{"title": "...", "checklistSha256": "...", "leaveRejoinAfter": "b" ,
 "items": [{"index": 1, "id": "a", "state": "answered", "answer": "ok", "memo": "", "by": "cli",
            "waitId": "...", "askedAtUtc": "...", "answeredAtUtc": "...", "markersTakenAtUtc": "...",
            "seqRange": {"0": {"from": 120, "to": 188}, "1": {"from": 98, "to": 140}},
            "evidence": "evidence/manual-steps/1-a", "evidenceGap": []}],
 "leaveRejoin": null}
```

- 項目の `state`: `notAsked`・`waiting`・`answered`・`unanswered`・`interrupted`。
- `results.manualSteps`（人の答えを集めたもの。道具の判定ではない）: 全項目が `answered` で ok → PASS。ng が1つでも → FAIL。それ以外（skip、unanswered、interrupted、notAsked が残る）→ UNKNOWN。取り消しで終わったときは CANCELED。
- `testVerdict` は `notJudged` のまま。人の OK で deploy・launch・clientReadiness・worldBuildMatch・evidence・leaveRejoin を書き換えない。
- 手順全体の結果（manual-steps だけ）:
  - 途中で止めたとき: 止めた語と理由（CANCELED・UNKNOWN・FAIL）。deploy-collect が PASS でなければ、その語と理由のまま（項目は聞かない）。
  - 最後まで進んだとき、数えるのは `results.leaveRejoin`（宣言したとき）、`results.evidence`、`results.manualSteps` の3つだけ。`worldBuildMatch` の UNKNOWN と `testVerdict: notJudged` は数えない（deploy-collect と同じ）。
  - 3つのどれかが UNKNOWN（null も UNKNOWN）なら UNKNOWN、そうでなく FAIL があれば FAIL、ほかは PASS。分からないことがある run を FAIL と言い切らない。
  - 理由は、選んだ語に当たるものを leaveRejoin・evidence・manualSteps の順で探した最初のもの: evidence の UNKNOWN は `evidenceIncomplete`、manualSteps の UNKNOWN は `humanNotAllOk`、FAIL は `humanNg`。
  - 例: ng と skip が混じる → manualSteps FAIL、全体 FAIL。ng と目印の欠け → 全体 UNKNOWN（`evidenceIncomplete`）。
- `Run.finish`: manual-steps で `results.manualSteps` が null のまま終われば、上の決まりで埋める（聞く前に止まったなら UNKNOWN、取り消しなら CANCELED）。待ちが残っていれば §3.3 のとおり閉じる。
- deploy-collect と leave-rejoin の結果・理由・終了コード・証拠の置き場は変えない。

## 6. Unity（`package/Editor/`）

### 6.1 run を見つける

- 既定の実行の根（`~/Library/Application Support/VRCRemoteTest/`）だけ。`--run-root` で別の根に置いた run は見つけない。
- 今の Bridge の URL と証明書の指紋から claim の key（`sha256("<URL>|<指紋の小文字>")` の16進の先頭24字。runner の `Claim` と同じ）を作り、`locks/<key>.json` を読む。`runId` が `^\d{8}T\d{6}Z-[0-9a-f]{8}\z` のときだけ、既定の根の `runs/<runId>/` を組み立てる（claim に書かれたパスは使わない）。
- そのフォルダーの `run.json` の `runId` が claim と同じで、`state` が `running` か `building` のときだけ「走っている run」として表示する。Editor を起動し直したあとも、CLI で始めた run も、これで見つかる。claim には書かない。
- 読む所（claim、run.json、waiting.json、checklist.json）はすべて、ファイルがリンクなら断る（`lstat` で見る）、ふつうのファイルだけ、上限つき（claim 64 KiB、run.json 1 MiB、waiting.json 16 KiB、checklist.json 64 KiB）。

### 6.2 待ちの画面

- 見つけた run で、`runner waiting --run <dir> --json` を呼び（固定の引数、今の `ChildEnvironment`、10 s で打ち切り、1 s より短い間隔では呼ばない）、`valid: true` のときだけ Run 節の上に出す。有効かどうかは §3.3 の全部の条件で、Unity は自分で決めない（claim の持ち主は continue のあと runner に移っているので、Editor の PID とは比べない）。
  - `Run <runId> is waiting for you`、`Item i of n: <id>`、checklist の `title` と項目の `text`（§1 の決まりで見える形にし、rich text なし、選んで写せる）、残りの分、メモの欄（2000字まで）、`OK`・`NG`・`Skip` のボタン。
- 押すと確認なしで `runner answer --run <dir> --wait-id <id> --item <id> <ok|ng|skip> --memo <memo> --by unity` を、今の `RunnerProcess.Run` で呼ぶ（固定の引数の並び、今の `ChildEnvironment` の環境、30 s で打ち切り）。
  - 終了コード 0: 「Sent. Waiting for the runner to take it.」。run.json の項目が `answered` になれば「Taken.」。
  - 1: 標準エラーの1行を出す（「もう答えがある」「待ちが閉じた」など）。送り直さない。
- 押すまでのあいだも、すべての部品を毎回描き、有効・無効だけを変える（今の Run 節の決まり）。押してから結果が出るまでボタンは無効。

### 6.3 Run の始め

- 手順の選び: `VALIDATED_PROCEDURES` に入った手順だけ（今は deploy-collect。manual-steps は確かめのあとで足す）。Unity 側に同じ一覧を定数で持つ。
- manual-steps が選べるようになったら: checklist のファイル（ファイルの選びの画面）、`Leave and rejoin after`（checklist の id から選ぶ。`None` も可）と target slot。prepare に `--procedure manual-steps --checklist <path> ...` を渡す。
- 待ちの画面は、手順の選びに出す前でも出す（CLI の `--unvalidated` で始めた run に答えるため）。

### 6.4 要約

- 終わった run の要約に、manual-steps なら `Human answers: ok 2, ng 0, skip 1 (not judged by the tool)` の形で答えの数を出す。results の一覧に `manualSteps` も並ぶ。

## 7. 上限（`proposed`）

| 項目 | 値 |
|---|---|
| 項目の数 | 20 |
| 項目の文 | 1000字 |
| checklist のファイル | 64 KiB |
| 答えの待ち | 60〜3600 s、既定 1200 s |
| run 全体（manual-steps） | 30〜120 分、既定 90 分 |
| 答えのファイル | 16 KiB、memo 2000字 |
| 待ちのあいだの照合 | 5 s ごと、観測の欠けは 15 s まで |
| 答えの確認 | 0.5 s ごと |

## 8. 新しい依存・集めるデータ・トークンを持つ人が新しくできること

- 新しい依存: なし。
- 集めるデータ: R1 と同じ種類に加えて、checklist の写し、人の答えとメモ（人が打った文で、何が入るか分からない。証拠と同じく 0600、Git と自動同期の外）、答えるたびの目印の控え。
- トークンを持つ人が新しくできること: なし（Bridge を変えない）。Mac の上では、同じ利用者のプロセスなら答えのファイルを書ける（`cancel.request` と同じ）。`by` は自己申告。

## 9. 分かっている限界

- VR 台＋観戦台（`VrchatMode: VR` の2台）は、ヘッドセットを付けて確かめていない（`docs/validation/multi-client-spike.md` の項目 7）。
- seqRange は Bridge の観測の遅れぶんずれる。項目のあいだにワールドが出した目印の手がかりで、正確な境目ではない。
- 答えの `by` は自己申告。同じ利用者のプロセスなら、Unity とも CLI とも名乗れる。
- 人の段は、Windows で人がすることをそのまま待つ。VRChat の中のクリックを道具が押すことはない（R5 の調べの対象）。
- Unity が見つけるのは既定の根の run だけ。
