# 試験の終わりの目印を待ってから集める（deploy-collect の `--end-marker`）— 契約

基準: vrc-remote-test main `0eef56c`（v0.13.1）。R1 の計画は `docs/plans/r1-automation.md`、測り直しは `docs/validation/baseline-manual-ops.md`。

## 1. なぜ

今の deploy-collect は、台の準備とビルドの目印（`vrcrt.build=`）を確かめると、すぐに証拠（全文ログ・目印・一覧）を集めて終わる。ワールドの中の試験の終わりは待たない。

代表の件 B（Commander の PRT001、2台の配備と判定）は、配備のあと約11分、ワールドの試験（`NoteOpsProbe`）が各台に `OPS_PROBE step=done ...` を出すのを待ってから、ログを書き出して採点している。測り直し（`baseline-manual-ops.md`）で、B を runner で回すには「終わりの目印を待ってから集める」ことが要ると分かった。今は B6〜B7 の書き出しが runner の外に残る。

## 2. 分かっている事実

- runner は Bridge の目印の控え（`vrcrt.sh log <slot> --markers --json`）を読める。目印になるのは、Bridge の設定 `LogMarkerPrefixes`（既定 `["[VRCRT]"]`、8 個まで）で始まる行だけ。**Commander の `OPS_PROBE` の行がこの接頭辞で始まるかは、Bridge の今の設定で確かめる必要がある**（`requires_validation`）。目印にならない行は待てない。
- 目印の控えは 2000 件または 1 MiB で古い方から捨てる（`droppedThroughSeq`）。行は 4096 字を超えると切られる（`truncated`）。ログの文はワールドが書くもので、信じない。
- 今の `--remaining-marker`（leave-rejoin）が、呼び出す人の正規表現を、環境なしの子プロセスで 2 s で打ち切って照らしている（`regex_matches`）。同じ仕組みを使う。
- deploy-collect の run の期限は 30 分（`RUN_SECONDS`）。配備から台の準備まではふつう 1 分ほど。

## 3. 契約

### 3.1 CLI（`package/Tools~/vrcrt_runner.py`、`prepare` と `run`）

- `--end-marker REGEX`: 終わりの目印の正規表現。1〜256 字、Python が compile できるもの（`--remaining-marker` と同じ `valid_regex`）。
- `--end-slots LIST`: その目印を待つ台。`--slots` の部分集合（重複なし、1 台以上）。省略すると `--slots` の全部。
- `--end-wait SECONDS`: 目印を待つ上限。60〜1500、既定 900（`proposed`）。
- `--end-slots` と `--end-wait` は `--end-marker` があるときだけ。どれも `--procedure deploy-collect` だけ（leave-rejoin と manual-steps に付けると使い方の誤り、終了コード 2）。
- 実機で確かめるまでは `--unvalidated` が要った。2026-11-26 に確かめ（`docs/validation/end-marker.md`）、外した。
- どれも CLI の引数だけから決める。ログ・目印・ワールドの中身からは、待つ台も、パターンも、次の手順も決めない。
- run の期限は 30 分のまま。`--end-wait` が長く、配備と準備に時間がかかれば、run の期限で UNKNOWN `runDeadline` になる。

### 3.2 記録（`procedure.parameters`、手順のハッシュに入る）

`--end-marker` を付けたときだけ、`parameters.endMarker = {"pattern": REGEX, "slots": [..], "waitSeconds": N, "unvalidated": bool}` を足す（`slots` は小さい順）。付けないときの parameters は今と同じ。手順のハッシュは今と同じく runner の版を含むので、0.14.0 ではどの手順のハッシュも変わる（版を上げるたびと同じ）。

continue は、手順を動かす前に、run.json の `procedure.sha256` が、記録された名前・版・parameters から作り直したハッシュと同じかを確かめる（どの手順も）。形が正しくても値を変えたもの（別のパターン、短い待ち、`endMarker` を消したもの）は、UNKNOWN `procedureInvalid` で何もしない。そのうえで `endMarker` の形を prepare と同じ決まりでもう一度確かめる。

### 3.3 流れ

deploy-collect の `recheckReady` のあと、`collectEvidence` の前に、段 `waitEndMarker` を入れる（`--end-marker` のときだけ）。

1. `worldBuildMatch` が FAIL（別のビルドの tag）なら待たない。`results.endMarker = "notReached"`、そのまま今どおり集めて FAIL。
2. 待つ台ごとに、照らし始めは「今の join の始め」（`joinAttemptSeq`、無ければ `joinedSeq`。`wait_world_build` と同じ）。試験が待ちの始めより前に終わっていても見つける。前の join の目印は数えない。
3. 回ごとに（ふつう 5 s ごと）:
   - まず `recheck`（Bridge の同じプロセス・host、guard があれば revision と launch plan、各台の世代・epoch・joinedSeq・instance・エラーワールド・ログイン）。読む時間は 1 回 5 s まで。
     - 台か Bridge が変われば、今の決まりの理由で UNKNOWN、止める（`clientReadiness` も UNKNOWN、証拠は集めない。今の recheck の失敗と同じ）。
     - 一覧が古い・読めない（manual-steps の待ちと同じ語）は「不完全」。15 s 続いたら UNKNOWN `observationIncomplete` で止める。
   - **前の回で見つけた行の確かめ**: Bridge は目印を一覧より先に出すので、見つけた行は、同じ回の一覧ではまだ前の join のものかもしれない。そこで、見つけた行は「確かめ待ち」にして、あとの回の recheck で、その台の `observedThroughSeq` が行の seq 以上になってから採る（そのとき joinedSeq が変わっていないことは recheck が確かめている）。採るときに、その回に読んだ目印の控えを `evidence/end-marker/slot-N/g<gen>-e<epoch>/markers.json` に保存する（試験がまだ続く台では、あとで古い目印が捨てられることがあるため）。確かめ待ちがあるあいだは、次の回を 0.5 s 後にする。
   - まだ見つかっていない台の目印の控えを読む（1 回 5 s まで）。
     - 世代か epoch が固定したものと違う、`catchingUp`、`records` が配列でない → その台はこの回「不完全」。
     - `droppedThroughSeq` が、その台でもう照らし終えたところを越えた → `results.endMarker = UNKNOWN`、理由 `markersDropped`。
     - 照らし終えたところより後の行のうち、`truncated` でも `withheld` でもなく 4096 字以内の行の文を、seq の順に、子プロセス 1 つで照らし、最初に当たった行の seq を得る。子は環境なし、2 s で止め、自分でも CPU 3 s で終わる（runner が先に落ちても残らない）。照らせない → UNKNOWN `endMarkerUndecided`。
     - 当たらなければ、照らし終えたところを進める。ただし一番新しい行は進めない（Bridge は開いている行に続きの行を足すことがあり、同じ seq の文が伸びる）。次の回にもう一度照らす。
   - 行の文は run.json に写さない（seq と、保存した目印の控えの場所だけ）。
4. 待つ台が全部採れれば `results.endMarker = PASS`。`--end-wait` を過ぎたら UNKNOWN `endMarkerTimeout`（採れなかった台 `missing`、確かめ待ちのままの台 `unconfirmed`、最後の回が不完全だったか `lastRoundComplete` を記録）。始まった回は最後まで行う（1 回の読みは 5 s まで）。
5. `endMarker` が PASS でなくても（`markersDropped`・`endMarkerUndecided`・`endMarkerTimeout`）、今どおり証拠を集めて最後の判定をする（なぜ終わらなかったかを後で見るため）。台の変化、`observationIncomplete`、取り消し、run の期限は、今どおり集めずに止める。

### 3.4 結果

- `results.endMarker`: `--end-marker` を付けたときだけある。PASS・UNKNOWN・`notReached`（上の 1、または待ちの前に止まった）・CANCELED（待ちのあいだの取り消し）。待ちが始まってから止まった run は UNKNOWN（`Run.finish` が埋める）。
- run 全体の決め方（この順）:
  1. 止まった理由（台の変化・証拠の回収や最後の判定の失敗・取り消し・期限）があれば、今どおりその語。
  2. `worldBuildMatch` が FAIL なら FAIL `otherBuildTag`（そのときは待たない）。
  3. `endMarker` が PASS でなければ UNKNOWN（理由は 3.3 の語）。
  4. ほかは PASS。
- `testVerdict` は `notJudged` のまま。終わりの目印が出たことは「ワールドがそう書いた」だけで、試験の合否ではない（`notes` に書く）。
- `record.endMarker`: `{pattern, slots, waitSeconds, startedAtUtc, endedAtUtc, matched: {slot: {seq, seenAtUtc, evidence}}, incompleteRounds, reason}`、期限切れのときは `missing`・`unconfirmed`・`lastRoundComplete` も。
- `procedure.endMarkerValidated`: 確かめ済みか（それまでは false）。
- 手順の数: deploy-collect 9 ＋ 1 ＝ 10（上限 32 の内）。

### 3.5 互換

- `--end-marker` を付けない run は、parameters・段・results・終了コードとも今と同じ（回帰の試験で確かめる）。ハッシュは版の分だけ変わる。
- `vrcrt.sh`、`scripts/vrcrt-run.py`、Bridge、Unity、Commander の `dice_life_auto` の使い方は変えない。Unity の画面には出さない（CLI だけ）。

## 4. 新しい依存・集めるデータ・トークンを持つ人が新しくできること

- **新しい依存**: なし。
- **集めるデータ**: 今と同じ（目印の控え・全文ログ・一覧）。加えて、当たった目印の seq と時刻。
- **トークンを持つ人が新しくできること**: なし（Bridge を変えない）。runner が Bridge に送るのは今と同じ読むだけの要求で、待つあいだに回数が増える（5 s ごとに、`/bridge`・`/clients` と、まだ当たっていない台の目印）。

## 5. 試験（`scripts/test-vrcrt-runner.sh` の fixture）

- 2 台とも目印がある → PASS、`matched` に seq、段 `waitEndMarker` が PASS、`evidence/end-marker/` に目印の控え、そのあと証拠。parameters に `endMarker`、行の文は run.json に無い。
- 待ちの途中で目印が出る → PASS。
- `--end-slots 0` → slot 1 に目印が無くても PASS。
- 期限まで出ない → UNKNOWN `endMarkerTimeout`、`missing`、それでも証拠を集める、終了コード 3。
- 今の join より前の seq の目印 → 数えない。
- 切られた・`withheld` の行 → 当たりにしない。
- `droppedThroughSeq` が照らし始めを越える → UNKNOWN `markersDropped`、証拠は集める。
- 止まらない正規表現 → 2 s で UNKNOWN `endMarkerUndecided`、runner は止まらない。
- 一覧がまだ読んでいない seq の行（`observedThroughSeq` より後）→ 採らない、期限で `unconfirmed`。
- 一番新しい行の文が同じ seq のまま伸びて当たる → PASS。
- 待ちのあいだに台が変わる → UNKNOWN、`clientReadiness` UNKNOWN、証拠は集めない。
- 待ちのあいだの取り消し → CANCELED、すぐに終わる。
- `worldBuildMatch` が FAIL → 待たない、`endMarker` が `notReached`、FAIL `otherBuildTag`。
- 使い方の誤り（`--unvalidated` 無し、`--end-slots` や `--end-wait` だけ、`--slots` の外、重複、`--end-wait` の範囲外、壊れた正規表現、leave-rejoin や manual-steps に付ける）→ 終了コード 2、run のフォルダーを作らない。
- run.json の `endMarker` のパターン・待ちを変えた、消した → UNKNOWN `procedureInvalid`。
- 回帰: `--end-marker` なしの deploy-collect に `endMarker` の欄も段も無く、結果は今と同じ。ほかの既存の試験（leave-rejoin・manual-steps）がすべて通る。

## 6. 実機の確かめ（`docs/validation/end-marker.md`）

統合役と時間を合わせる。Bridge の設定は変えない。

1. 試験用のプロジェクトに、読み込みの N 秒後に `[VRCRT] vrcrt.end=<runId に依らない固定の語>` を出す試験用の部品を置く（`VrcrtProbe` の形）。`--end-marker 'vrcrt\.end='` で、待ってから PASS になる。証拠の目印の控えに、その行がある。
2. `--end-wait 60` で、目印を出さないシーンなら UNKNOWN `endMarkerTimeout`、証拠はある。
3. Commander の B に使えるか: Bridge の今の `LogMarkerPrefixes` と `OPS_PROBE` の行の接頭辞を、統合役に聞いて書く（確かめは Commander の取り込みの段で）。

PASS を記録した commit で `--unvalidated` の要求を外す。

## 7. Codex のレビュー（契約、gpt-6.1-sol・high、NEEDS_REVISION）

指摘 8 件（major 4、minor 4）をすべて取り込んだ:

- 一覧より先に出た目印を、前の join の一覧と組み合わせてしまう → `observedThroughSeq` が行を越えてから採る（3.3）。
- 行ごとに子を起こすと数千になりうる、子が残りうる → 台ごと・回ごとに子 1 つで最初の当たりの位置を返す、新しい行だけ、子は自分で CPU 3 s で終わる（3.3）。
- 読みの時間と期限 → 1 回の読みは 5 s まで、不完全は 15 s の猶予、取り消しと期限はそのまま止める（3.3）。
- 正しい形のまま値を変えた run.json → continue でハッシュを作り直して比べる（3.2）。
- 版を上げるとハッシュが変わる → そう書いた（3.2、3.5）。
- 同じ seq の行が伸びる → 一番新しい行は次の回にもう一度照らす（3.3）。
- 当たった行があとで捨てられる → 採るときに目印の控えを保存する（3.3）。
- 結果の順と、途中で止まったときの `endMarker` → 3.4 に書いた。

## 8. Codex のレビュー（差分、gpt-6.1-sol・high、APPROVE_WITH_CHANGES）

major は無し。前の join や確かめていない行が PASS になる道、終わらない繰り返し、送り直し、古い runner の run をハッシュで断ることは見つからなかった。minor 3 件をすべて直した:

- 待ちの前に取り消した run の `results.endMarker` が CANCELED になっていた → 待ちが始まっていなければ `notReached`（3.4 のとおり）。
- 待ちの途中で止まった run の `record.endMarker` に、終わりの時刻と理由が残らなかった → `Run.finish` で閉じる（recover と次の prepare の引き継ぎも同じ道を通る）。
- 待ちのあいだの試験が固定の sleep に頼っていた → 段 `waitEndMarker` に入ったこと（伸びる行の試験では、そのあと目印を読んだこと）を待ってから変える形にし、段の結果と理由も確かめる。待ちの前の取り消しの試験を足した。
