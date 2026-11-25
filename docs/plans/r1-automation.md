# VRC Remote Test 追加開発の計画（全体の方向と R1a・R1b の実装計画）

基準: vrc-remote-test main `909dd6d`（v0.10.0）、vrchat-commander main `e53ca065`（2026-11-25）。今回の成果物はこの計画だけで、実装は承認のあと。

## 0. Context（なぜやるか）

VRChat ワールドの確認のうち、配備・台の起動し直し・証拠の回収・結果の記録は、今は人が Unity・PowerShell・Mac のシェルを行き来して行っている（Commander の `handoff/queue/` 141件）。これを (1) 完全に自動、(2) Unity の VRC Remote Test ウィンドウからボタン1つ、(3) 人の判断だけを人に残して記録は道具が取る、の3段で減らす。最初の段 R1 は、配備の結果を構造化して渡すことと、台ごとの操作と準備の判定を道具に入れること。Bridge を触らない R1a と、Bridge に原子的な前提条件（guard）を足す R1b に分ける。

## 1. 現状の要点（計画の前提にした事実）

- Unity: `RemoteBuildCoordinator.ExecuteRemoteBuildAsync`／`DeployLastBuildAsync` は `Task<RemoteBuildOutcome>` を返すが、メニューは結果を捨て、headless は終了コード 0/1 にだけ使う（`RemoteBuildCommand.cs:101-102,121`）。`GetCoordinator()` は internal。成功の判定は `status == "deployed"` だけで、`launch` は `disabled` の注記にしか使っていない（`RemoteBuildCoordinator.cs:559,787`）。buildId は SDK のビルドのあとに作られ（519）、ビルドの前の失敗や取り消しは buildId が null のまま `UnknownError` になる（430,486,578,620）。`LastArtifact` はメモリだけで、再配備で hash が変われば書き換わる（631-640）。台ごとの close/launch は Transport に無い。
- Bridge: 起動が一部だけのとき status は `failed`＋`launch.outcome=partial`（`StagingWatcher.cs:292-306,744-756`）。よって `deployed`＋`partial` は Bridge が出さない組＝矛盾。
- Bridge の1台の close/launch は本文を読まない（`BridgeAdminApi.cs:70-72`）。操作の門は待たない1枠の錠（`BridgeOperationGate.cs`）。`hostGeneration` はプロセスごとに1から（`BridgeRuntime.cs:207`）、`processStartedAtUtc` は `/bridge` に出ている。起動の計画は配備の始めに無条件に消える（`VrchatRelaunchCoordinator.cs:136`）。設定の mode の切り替えは台を閉じてから 409／412 で終わることがある（`BridgeAdminApi.cs:340-376`）。操作の revision、バッチの buildId（`LaunchClientsAsync` は world のパスしか受けない、`IVrchatClientCoordinator.cs:26`）は無い。`clientGeneration` は PID＋作成時刻の派生値。全文ログの書き出しは Windows の `state/exports` に1件ずつ残り、消す仕組みが無い（`BridgeLanApi.cs:342-428`）。目印は長い行を切り詰めて印を付ける（`VrchatMarkerLog.cs:171-180`）。
- Mac: `vrcrt-run.py` は Editor のログを正規表現で削って buildId を拾い（86-90）、トークンを子の環境に入れる（53-67）。`vrcrt.sh` の正規表現の子プロセスも親の環境を引き継ぐ（1005-1011）。`wait-same-instance` は `inErrorWorld` と `loginState` を見ない（1299）。`export-log` は「いまその slot にいる台」を取り、世代を指定できない（885）。`.prev` の目印は LAN API で取れない。`vrcrt.sh` は自分の置き場所に頼らない（埋め込みの Python だけ）。
- 配布の zip は `git archive ...:package`（`release-package.yml:258`）なので、`package/` の外の `scripts/` は Unity の利用者に届かない。

## 2. 効果の測り方と代表の件（Commander）

注: Commander の main は今 `57c77868` で、基準の `e53ca065` はその7つ前の祖先。分類（141件）は `e53ca065` のまま使い、代表のファイルは両方で同じものを選んだ。

### 代表の件

| 記号 | 件 | 類型 | 今の人の操作（記録から） | R1 で減らせる部分 |
|---|---|---|---|---|
| A | 場面12の救済と持ち主の退出・途中参加（`handoff/queue/rescue-revision-windows.md`、記録 `handoff/validation/2026-11-10-adr173-rescue-windows.md`） | 所有者の退出・途中参加 | VRChat の窓でのクリック4回、Windows の PowerShell で PID を調べて `Stop-Process`・40秒後に `Start-Process`、`export-log`、判定の手実行。配備の終わりから判定の材料がそろうまで約15分（成果物の時刻から数えた値。記録に所要時間の欄は無い） | R1b: PID を調べる・閉じる・起動し直す・世代ごとの証拠の回収を runner の1回に。クリック4回はゲームの中の操作なので人に残る（R5 の調査の対象） |
| B | 2台の配備と判定（`handoff/queue/2026-11-09-adr170-two-clients.md`） | Windows の2〜4台の配備とログ判定 | 押す操作は無いが、台ごとの `export-log`、控えの保存、採点、Prepare のやり直しを人が順に打つ。queue に「約15分」 | R1a: 配備・台の準備・証拠の回収・記録をボタン1つに。採点は Commander の判定スクリプトのまま |
| C（参考） | 手の並び替え（`handoff/queue/2026-11-25-hand-reorder.md`） | VR の手触り | VR とデスクトップで約15分。合否の欄が無く感想を集める形 | R1 では減らない。R3 の効果を測る基準としてだけ測る |

上限の参考: 自動になっているダイスの退出と途中参加（`2026-11-24-adr193-drawer-layout.md`、`dice-life-auto2`）は人の操作0回、閉じてから終わりの目印まで 101 s、run 全体で約6分。

### 測り方

- 人の操作の回数: 次の1つずつを1回と数える。(1) コマンド1行の入力、(2) 画面上のクリック・キー操作1回、(3) 別の機械・画面・端末への切り替え、(4) 値を目で読んで写すこと（PID、buildId、runId、slot）、(5) 合否やメモの入力。待つだけの時間は数えない。
- 時間: 「人が始めた時刻」から「判定の材料がそろった時刻」までの経過と、そのうち人が手を動かしていた時間を分ける。人は記録用の表（`docs/validation/baseline-manual-ops.md`）に時刻を書き、成果物の時刻と照らす。
- 基準（2026-11-25 に変更）: 手でやり直さず、過去の回の記録（queue、validation、成果物の時刻）から数えた推定にした。結果は `docs/validation/baseline-manual-ops.md`（A: 始め〜判定 23分01秒、人の操作10回＋受け渡し。B: 始め〜材料 15分58秒、人の操作0回・エージェントの操作10回。C: 終わった回が無く未取得）。人が手を動かした時間と受け渡しは `requires_validation`。各リリースのあとは `run.json` の時刻と押したボタンの数で測り直し、方法の違いを添える。

## 3. 全体の方向

| 段 | 中身 | Bridge | 状態 |
|---|---|---|---|
| R1a（v0.11.0） | 配備の結果ファイル、公開の入口、台ごとの手動 close/launch の画面、同梱の runner（prepare/continue）、台の準備とワールド・ビルドの準備の判定、通常の複数台の準備と証拠の回収 | 変更なし | 今回、実装の計画まで |
| R1b（v0.12.0） | close/launch の前提条件（guard）、操作の revision、書き出しの掃除、「宣言した slot の退出と起動し直し」の固定の手順 | 変更あり（Windows で入れ替え） | 今回、実装の計画まで |
| R2 画面の記録 | Bridge が起動した1台のウィンドウの明示の撮影 | 変更あり | 候補（調べる範囲と測り方だけ） |
| R3 手作業の試験の準備と記録 | VR 台＋観戦台の準備、次に押すものの表示、押した時刻の目印、OK/NG の記録。空き待ちは別機能 | R2 に依存 | 候補 |
| R4 起動の幅 | 追加 profile での途中参加、アップロード版への参加、doctor の強化 | 変更あり | 候補 |
| R5 デスクトップ入力 | 公式 OSC `/input/*` の調査が先 | 変更あり | 候補（調べてから相談） |
| R6 配信と性能 | 既存の再配備で減る分の測定、Windows 環境の記録 | 一部 | 候補 |

R1 のあと §2 の代表の件で測り直し、R2〜R6 を効果の大きい順に並べ直す。R2 以降の詳細設計・hook の決まり・受け入れの試験は、採用するリリースへの条件として扱い、今回は §9 に調べる範囲と測り方だけを書く。

## 4. R1a（v0.11.0、Bridge の変更なし）

### 4.1 狙い

- 配備の結果を構造化したファイルで渡し、「配備できた」「起動できた」「準備ができた」「試験に合格した」を分ける。
- Unity のウィンドウから、台の状態を見て1台ずつ close/launch できる（既存 API への薄い接続。原子的な競合の保証は無いと画面と文書に書く）。
- 「通常の複数台の準備と証拠の回収」（deploy-collect）を、Unity のボタン1つと CLI の両方で、同じ runner の prepare/continue で行う。
- R1a は自動の close/launch を行わない（状態を変えるのは配備だけ）。ポーリングで変化に気づいても guard とは扱わず、気づいたら UNKNOWN で止めるだけ。

### 4.2 置き場所と実行の流れ

- 同梱の実装: `package/Tools~/`（`~` で終わるフォルダーは Unity が取り込まず、`git archive` で zip に入る）。
  - `package/Tools~/vrcrt.sh`: いまの `scripts/vrcrt.sh` をここへ移し正本にする。`scripts/vrcrt.sh` は引数・標準入出力・終了コードをそのまま渡す `exec` だけの互換の入口（どの作業フォルダーからも動き、実行の権限を保つ）。Commander・vrc-world-kit がこのパスを呼ぶ。
  - `package/Tools~/vrcrt_runner.py`: 新しい runner（標準ライブラリだけ。起動時に `sys.version_info >= (3, 9)` を確かめる）。既存の CLI（`vrcrt.sh`）をサブプロセスで呼び、HTTP を自分では持たない。
  - `scripts/vrcrt-run.py`: 今の流れのまま残す互換の入口（厳しい runner に置き換えない。今の「配備できれば成功、`--watch-worlds` の無い台は警告」「`--expect-marker` のときだけ目印を確かめる」「動いている全台を書き出す」を保つ）。変えるのは、buildId を結果ファイルから読むこと（無ければ今のログからの取り出しに落ちて警告）と、子の環境の決まり（§4.9）だけ。
- 実行の流れ（Unity のボタン）:
  1. Unity が Python を決め（`VRCRT_PYTHON`、無ければ `/usr/bin/python3`。GUI の Unity は PATH が狭いので `command -v` に頼らない）、`-I -c` で版を確かめる（10 s で打ち切り、Command Line Tools が無いときの案内）。
  2. `runner prepare --procedure deploy-collect --slots 0,1 [--expect-clients ...]`。runner が実行フォルダーを作り、配備の前のカーソル・`/clients`・`/bridge` を保存し、宣言と Bridge の設定を比べ、JSON 1行（runId、runDir、候補の worldBuildTag）を返す。
  3. Unity が開いている Editor で `RemoteBuildApi.BuildAndDeployAsync`（または `DeployLastBuildAsync`）を await し、結果ファイルを書く。
  4. `runner continue --run <runDir>` を、Editor の寿命から切り離して起こす（新しいセッション、stdin・stdout・stderr は使わない）。進捗は runner が `<runDir>/progress.jsonl`（1行 4 KiB・合計 1 MiB まで）に書き、ウィンドウはそれを読んで表示する。Editor を閉じても runner は最後まで進めて `run.json` を書き終え、開き直したウィンドウが続きを表示する（パイプが切れて runner が止まる形にしない）。
- 実行の流れ（CLI）: `runner run --build headless --project <path> --scene <Assets/...> --slots ...` が prepare → headless Unity → continue を順に行う。Editor の中から使う利用者のコード（Commander など）は、prepare → 公開の Task に `RunDirectory` を渡す → continue を自分で呼ぶ（別の入口は作らない）。headless の前に、その project の Editor が開いていれば断る（`Temp/UnityLockfile` の錠が取れるか＋同じ `-projectPath` の Unity プロセスが無いか。macOS の Unity 2022.3 での錠の挙動は `requires_validation`）。
- 取り消しと期限（runner が監督する）: 子は `subprocess.Popen`（シェルを通さない引数の並び、新しいプロセスグループ）で起こし、0.5 s ごとに `<runDir>/cancel.request` と、実行全体と各操作の単調時計の期限を見る。子の stdout/stderr は上限つきでファイルに受ける。
  - 読み取りだけの子（`vrcrt.sh` の観測・書き出し）: SIGTERM → 5 s → SIGKILL でグループごと止めて回収し、CANCELED（取り消し）か UNKNOWN（期限）を書く。
  - 状態を変える子と操作（headless の Unity による配備、R1b の close/launch）: headless の Unity は SIGTERM → 30 s → SIGKILL でグループごと止めて回収する。そのとき既に送信や commit まで進んでいた（結果ファイルの stage が upload 以降、または段が分からない）なら、「Bridge が配備を続けているかもしれない。取り消しは配備の撤回ではない」と記録し、送り直さない。結果は取り消しなら CANCELED、期限・応答不明なら UNKNOWN。close/launch の最中も同じく送り直さない。
  - Unity は Cancel で `cancel.request` を作る。開いている Editor での配備（ボタンの流れの3）は、今の `CancellationToken` で止める（取り消しの扱いは上と同じ）。
- 同じ Bridge への run の重なり（同じ Mac の中）: 根の `locks/<Bridge の URL と証明書の指紋のハッシュ>.json` を claim にする。
  - 中身: runId、claim token（乱数）、持ち主 {PID, 起動時刻}、作った時刻、記録した子 {PID, 起動時刻, プロセスグループ}。claim の作成・持ち主の移し替え・引き継ぎ・削除は、同じフォルダーの `.lock` を `flock` で取ってから行い、直列にする。削除と移し替えは自分の token と一致するときだけ。
  - 持ち主の寿命: prepare が `O_EXCL` で作るときの持ち主は呼び出し元（ボタンの流れでは Unity の Editor、CLI の `run` では runner 自身）で、prepare の短命なプロセスではない。continue は起動時に runId と token を照らし、持ち主を自分に移す。continue の終わり（PASS・FAIL・UNKNOWN・CANCELED のどれでも）で消す。
  - あれば prepare は断る。引き継げるのは、持ち主の PID が起動時刻ごと一致しない（消えた・別のプロセスに再利用された）ときか、実行全体の期限（30分）を過ぎたときで、どちらでも記録した子がすべて止まっている（PID と起動時刻で同じプロセスと確かめたうえで止めた、または既にいない）と確かめられたときだけ。確かめられなければ断る。引き継ぐときは前の run を回復（§4.4）してから。
  - run の最中はウィンドウの Remote Build & Test・Deploy Last Build・Close／Launch を押せなくする。別の Mac・別の利用者・古い CLI からの配備は R1a では確実には見分けられない（Bridge の識別・etag は変わらず、revision も無い）と文書と記録に書き、R1b で準備を固定するときから `currentBatch.buildId` と revision を照らして閉じる。Bridge 全体の lease は作らない。
- C# には待ち・カーソル・準備の判定を書かない。配備の結果の分類（§4.3）だけは、事実（成果物の sha256 と Bridge の結果）を持つ Unity 側で1か所に書き、runner は形と値を検証して使う（別の分類を持たない）。
- ドメインの再読み込みのあいだも runner（別プロセス）は動き続け、`run.json` に書く。ウィンドウは開き直したとき最新の `run.json` を読み直して表示する。新しく再読み込みの錠はかけない。配備の途中で再読み込みが起きて結果ファイルが書かれなければ、continue は始まらず、run は「配備の結果が無い」で UNKNOWN と表示する。

### 4.3 配備の結果ファイル（Unity）

- 公開の入口: `package/Editor/Build/RemoteBuildApi.cs`（public static）
  - `Task<RemoteBuildOutcome> BuildAndDeployAsync(RemoteBuildRequest request = null, IProgress<RemoteBuildProgress> progress = null, CancellationToken ct = default)`
  - `Task<RemoteBuildOutcome> DeployLastBuildAsync(RemoteBuildRequest request = null, ...)`
  - `string ActiveWorldBuildTag { get; }`（SDK のビルド中だけ値を持つ読み取り専用。利用者のビルド前処理がこれをワールドに埋める。§4.5）
  - どれも `RemoteBuildCommand.GetCoordinator()` を通し、同じ `_executionLock` を共有する。メニューと headless もこの入口を使い、結果ファイルを書く。既存の `RemoteBuildCommand`・`RemoteBuildCoordinator` の名前とシグネチャ、ログ行は変えない（§6）。
- `RemoteBuildRequest`: `RunDirectory`（任意。実行の根の中の、この run のフォルダーだけ）、`WorldBuildTag`（任意、`[A-Za-z0-9_-]{8,64}`。新しくビルドするときに埋める tag の候補）。
- 試みの識別: 入口に入ったとき `attemptId`（時刻＋乱数）を作る。buildId は今どおり SDK のビルドのあと。取り消しは `RemoteBuildOutcome` に internal の `Canceled` を足して運び（公開のシグネチャは変えない）、文言から推測しない。
- 保存先: 既定の根 `~/Library/Application Support/VRCRemoteTest/`（§4.4 の決まり）の `deploy-results/<buildId>.json`、buildId が無い試み（busy、前処理・SDK の失敗、取り消し）は `deploy-results/attempt-<attemptId>.json`。実行中は `<runDir>/deploy-result.json` にも同じ内容。ファイル 0600、フォルダー 0700。buildId は既存の `BuildIdGenerator.IsValid` を通してからファイル名に使い、完了した結果の上書きはしない。headless は環境変数 `VRC_REMOTE_TEST_RUN_DIR`／`VRC_REMOTE_TEST_WORLD_BUILD_TAG` で受ける。
- 形（`schema: "vrcrt.deploy-result"`, `schemaVersion: 1`）: attemptId、buildId（null 可）、packageVersion、startedAtUtc/finishedAtUtc、stage（preflight/sdkBuild/upload/result）、artifact {sha256, size, worldBuildTag（実際に埋めた tag。無ければ null）}、unity {succeeded, canceled, errorCode, message（2000字で切り、Editor のログと同じく秘密を含みうるものとして扱う）}、bridgeResult（検証済みの status・errorCode・launch{attempted,outcome,requested,started,closed}・warnings の件数）、classification {deploy, launch, stopReason}。
- tag と成果物の対応（Deploy Last Build）: 新しくビルドしたとき、埋めた tag を `LastArtifact` と一緒に、sha256・size と組にしてメモリに持つ。再配備で bytes が同じ（sha256 と size が一致）ならその tag をそのまま結果に書き、変わっていれば tag を null にする（worldBuildMatch は UNKNOWN になる）。ドメインの再読み込みで消えれば同じく null（保存した成果物の受領書は作らない。必要と分かったら後で）。
- 分類（`DeployResultClassifier`、純粋関数で Editor テストを書く）:

| Bridge の結果 | deploy | launch | 手順 |
|---|---|---|---|
| deployed＋launched/relaunched、`requested ≥ 1` かつ `started == requested` | PASS | PASS | 続ける |
| deployed＋reusedRunning（Reload）| PASS | notApplicable | 続ける（準備の判定で再読み込みを確かめる） |
| deployed＋disabled | PASS | notApplicable | 止める（台が要る手順は UNKNOWN「AutoLaunchVrchat が off」） |
| failed＋partial、`0 < started < requested` | FAIL | FAIL | 止める。台の準備は FAIL（実際の部分起動） |
| failed＋failed（started 0）／failed＋notReached／failed で launch 無し | FAIL | FAIL／notReached | 止める |
| deployed＋partial/failed/notReached、launched なのに `started < requested`、partial なのに `started == requested` か 0、failed＋launched、sha256 が成果物と違う、buildId 違い、v0.8 以上の Bridge なのに launch が無い | UNKNOWN | UNKNOWN | 止める（矛盾・欠け） |
| Unity 側の失敗 | FAIL（前処理・SDK・Bridge の拒否）／UNKNOWN（時間切れ・応答無し・形違い）／CANCELED | notReached | 止める |

### 4.4 runner の記録と保存先の決まり

- 実行の根: 既定 `~/Library/Application Support/VRCRemoteTest/`（`runs/` と `deploy-results/`、どちらも 0700）。`--run-root` で変えるときは realpath で解いたうえで、git の作業ツリーの中、`~/Library/Mobile Documents`・`~/Library/CloudStorage` の下、`~/Documents`・`~/Desktop` の下（iCloud の同期の有無を確かめられないため）を断る。runId は `YYYYMMDDTHHMMSSZ-<8桁の16進>`、フォルダーは新規作成だけ。
- ファイルの扱い（作る・続ける・書く・取り消し・消す、のすべてに同じ決まり）: 持ち主が自分で 0700 のフォルダーだけを使う、リンクは辿らない（`O_NOFOLLOW`、可能なところは `dir_fd` で相対に開く）、一時ファイルは `O_EXCL` の一意な名前、書いたら fsync してから rename、識別子は形を確かめてからパスに使う、`run.json` に書いたパスを辿って読んだり消したりしない（証拠は実行フォルダーの中の決まった相対の位置だけ）。
- `run.json`（手順ごとに書き直し、落ちても途中まで残る）: runId、procedure {name, version, parameters, sha256（正規化した手順定義と引数のハッシュ）}、tools {packageVersion, runnerVersion, vrcrt.sh と runner の sha256, python の realpath・版・sha256}、bridge {version, exeSha256, processStartedAtUtc, hostGeneration, activeConfigEtag, savedConfigEtag, freeDiskBytes, stateExportsBytes}（前後）、attemptId・buildId、slots（明示の全 slot）、各 slot の世代・epoch、operations[]（名前・開始・終了・結果・断った理由）、evidence[]（相対パス・大きさ・sha256・取った時刻・切った位置）、results（§4.6）。
- 結果の語は PASS・FAIL・UNKNOWN・CANCELED だけ。観測が欠けた・古い・ディスクが一杯・時間切れの観測不足は成功に数えない（UNKNOWN）。
- 落ちたときの回復: 各操作を始める前に、`run.json` に「進行中」の状態・その操作・暫定の結果 UNKNOWN・起こした子の {PID, 起動時刻, プロセスグループ} を書いてから始める。runner が落ちた run は、次の prepare が claim を引き継ぐときか `runner recover --run <runDir>` で、`runnerInterrupted` の UNKNOWN として確定する（それまでに確定した結果と証拠は残す）。残った子は、記録と PID・起動時刻が一致するときだけ止め、止まったと確かめられなければ新しい run を断る。中断した配備・close・launch は自動で送り直さず、再開もしない。
- 新しい CLI の小さな足し（すべて読み取り、Bridge の変更なし。省略時の今の動きは変えない）:
  - `vrcrt.sh bridge --json`（`GET /api/v1/bridge` の本文を検証して出す）
  - `vrcrt.sh log <slot> --markers --json`（ヘッダー {clientGeneration, observationEpoch, catchingUp, droppedThroughSeq} と記録を上限つきで。各記録に切り詰め・伏せの印を付ける）
  - `vrcrt.sh export-log <slot> <dir> --generation G --epoch E`（2つは組でだけ受ける。いまの台が指定と違えば終了コード 3 で何もしない。既存の Bridge の書き出しの要求にそのまま渡す）
- `--expect-clients count=N mode=Relaunch|Reload profiles=0,1 vr=Desktop|VR` を渡したときだけ、`/bridge` の activeConfig と比べ、違えば何もせず UNKNOWN（`declarationMismatch`）で止める（`client_plan.py` と同じく比べて止めるだけ。設定は書き換えない）。

### 4.5 準備の判定（runner の中だけ）

- 台の準備（明示した全 slot。動いている台だけに縮めない）。最新の `/clients` が15秒以内・`processListReadable` で、各 slot が次を満たす:
  - 配備の後に入ったこと: Relaunch なら `clientGeneration` が配備の前のカーソルと違い、その epoch の `joinedSeq` がある。Reload（同じ世代）なら epoch が同じで `joinedSeq` がカーソルの seq より大きい。epoch が変わっていれば比べられないので UNKNOWN。
  - `running == true`、`state == joined`、`catchingUp == false`、`inErrorWorld == false`（null は待ち）、`loginState == authenticated`、`instance` があり全 slot で同じ。
  - `loginState == likelyLoggedOut` は UNKNOWN で止め、「Windows で profile N に手でログインし直す」と案内する。Logging が Full でなければ UNKNOWN。
  - 期限までに満たさなければ FAIL（観測は足りているのに満たさない。エラーワールドのままも FAIL で理由 `inErrorWorld`）。観測が欠けていたのが原因なら UNKNOWN。
- 準備ができたときの各 slot の {generation, epoch, joinedSeq, instance} を固定し、以後の確認（状態を変える操作の直前、最後の判定）で全 slot をその期待と照らす。予期しない退出・入り直し（`joinedSeq` の増加）・エラーワールドへの移り・世代や epoch の変化・Bridge のプロセスや host の世代や etag の変化は、前の準備完了を無効にし UNKNOWN で止める。
- ワールド・ビルドの準備（目印を使うワールドだけ）:
  - prepare が候補の tag（runId から作る）を出し、Unity は新しくビルドするときそれを `ActiveWorldBuildTag` として示す。利用者のビルド前処理がそれをワールドに埋め、ワールドは読み込み時に `vrcrt.build=<tag>` を含む目印の行を出す（接頭辞は Bridge の `LogMarkerPrefixes` のもの。形は `proposed`）。runner が照らすのは、結果ファイルに書かれた「実際に埋めた tag」だけ（prepare の候補ではない）。
  - 各 slot で、固定した世代・epoch の、カーソルより後の目印から `vrcrt.build=` の欄を、空白で区切られた1つの欄として正確に読む（部分一致でなく、1行に2つあれば使えない）。今回の tag なら PASS、別の値なら FAIL（前の配備のワールドに入ったまま）。期限までに無い、結果ファイルの tag が null、切り詰め・伏せの印の付いた行しかない、なら UNKNOWN。同じインスタンスや新しい目印だけでは PASS にしない。
  - この PASS はワールドが目印を偽らない前提だと記録と文書に書く。
- 目印の読み取りの上限: 1回の待ちで slot ごとに1回 `log --markers --json` を読む（Bridge の上限つきファイル＋CLI の上限）だけで、目印の数に比例した仕事を増やさない。`droppedThroughSeq` がカーソルを越えたら「観測の欠け」を記録して UNKNOWN。目印の中身から、対象・保存先・次の手順・引数を作らない。

### 4.6 固定の手順「通常の複数台の準備と証拠の回収」（deploy-collect）

1. prepare（§4.2）。2. 配備（Unity）。3. 結果ファイルの検証と分類、止める条件なら止める。4. 台の準備を待つ。5. ワールド・ビルドの準備を待つ（任意の期限）。6. 準備完了の直後に全 slot を照らし直す。7. Windows の空き（`/bridge` の `freeDiskBytes`）と書き出しの貯まり（`stateExportsBytes`）を見て、上限（§4.8）を越えるなら書き出さずに UNKNOWN（Windows の `state\exports` の片付けを案内）。8. 証拠の回収: slot ごとに `evidence/slot-<n>/g<gen>-e<epoch>/` へ、世代と epoch を指定した全文ログの snapshot、目印の snapshot（ヘッダーつき JSON）、取った時刻と切った位置（seq と大きさ）。全体の `clients.json`・`bridge.json`。書き出しの合計を流しながら数え、上限で止める。9. 最後の判定で全 slot を照らす。10. `run.json` の results を書く。
- results: `deploy`、`launch`、`clientReadiness`、`worldBuildMatch`、`evidence`（回収の成否）、`testVerdict`（R1 では常に `notJudged`。利用者が構造化した結果と証拠を読んで判定する）。証拠の回収の成功や人の OK で `worldBuildMatch` の UNKNOWN を PASS に書き換えない。手順全体の結果は、止めた理由があればその語、無ければ PASS（=準備と回収ができた。試験の合格ではないと画面と記録に書く）。

### 4.7 Unity のウィンドウ

- VRChat Clients 節に台の表（slot、PID、profile、state、instance の短縮、login、errorWorld、generation、epoch、catchingUp）。`/clients` の `creationTimeUtc` と `/bridge` の `processStartedAtUtc`・`exeSha256` を型に足す。
- 台ごとの Close／Launch（Relaunch・Bridge 0.10 以上・観測が新しい・ほかの操作が無いときだけ押せる）。確認の画面に slot・PID・世代を出し、「Bridge はその時点で記録にある台を閉じます。表示とのあいだに変わっても止められません（R1b で止められるようになる）」と書く。応答の読み方は `vrcrt.sh` の `cmd_close_client`／`cmd_launch_client` と同じ（`closed+notConfirmed==1`、200 ⇔ closed かつ recordUpdated、`launchedNotRecorded`、応答無しは「不明」で送り直さない）。
- Run 節: 手順（R1a は deploy-collect だけ）、slots（既定は Bridge の台数から `0..N-1`、編集可）、宣言（任意）、「前のビルドを使う」、Run／Cancel、進捗、最後の run の要約（各 results、「試験の合否は判定していない」と明記）、「実行フォルダーを開く」。古い run の削除は CLI の `runner prune` だけ（画面には作らない）。
- runner の起動: `ProcessStartInfo` の環境は空から作り、許した変数だけを入れる（§4.9）。引数は固定の並び。`ProcessStartInfo.ArgumentList` が Unity 2022.3 の Mono で使えるかは `requires_validation`（使えなければ固定の引数を自前で引用する）。
- 変えるファイル: `Transport/IRemoteTransport.cs`・`LanRemoteTransport.cs`（`CloseClientAsync(slot)`・`LaunchClientAsync(slot)` を `SendAdminAsync` で）、`Build/RemoteBuildCoordinator.cs`（`CloseVrchatClientAsync`・`LaunchVrchatClientAsync` を `CloseVrchatClientsAsync` に倣って同じ錠で。attemptId と取り消しの印）、`Build/RemoteBuildOutcome.cs`（internal の `Canceled`・`AttemptId`）、`Build/RemoteBuildCommand.cs`（結果ファイル、headless の環境変数）、新規 `Build/RemoteBuildApi.cs`・`Build/RemoteBuildRequest.cs`・`Build/DeployResult.cs`・`Build/DeployResultClassifier.cs`・`Build/DeployResultWriter.cs`・`Integration/RunnerProcess.cs`、`UI/RemoteTestWindow.cs`・`UI/VrchatClientsView.cs`（表と文言は試験しやすい純粋関数へ）、`Protocol/BridgeInfo.cs`・`Protocol/VrchatClients.cs`、`Tests/Editor/Build/FakeRemoteTransport.cs`。

### 4.8 上限（すべて `proposed`。実測で直す）

| 項目 | 値 |
|---|---|
| prepare の時間 | 60 s |
| 台の準備の待ち | 300 s（4台は 420 s） |
| ワールド・ビルドの目印の待ち | 120 s |
| 1台の全文ログの書き出し（全体の期限） | 120 s |
| 1回の実行全体 | 30 分 |
| ポーリングと取り消しの確認 | 2 s ごとの観測、0.5 s ごとの取り消しと期限の確認（読み取りの子は最大 5 s で止める） |
| 自動のやり直し | 0（状態を変える操作は送り直さない。読み取りだけ1回まで） |
| 手順の数 | 32 |
| Mac の証拠の合計 | 1 run 1 GiB。Mac の空きが 2 GiB 未満なら書き出さず UNKNOWN |
| Windows | `freeDiskBytes` が 5 GiB 未満、または `stateExportsBytes` が 2 GiB を越えるなら書き出さず UNKNOWN |
| 保持 | 全文ログと目印は 30 日、`run.json`・結果ファイルは 90 日を目安として文書に書く。自動では消さない（保証された削除ではない）。`runner prune --older-than-days N` は runId の形と `run.json` の schema を持つ根の直下のフォルダーと、形の正しい `deploy-results/*.json` だけを消し、リンクは辿らない。Windows の書き出しは R1a では利用者が `state\exports` を消す（`docs/setup-windows.md` に手順）。R1b で Bridge が自分の書き出しを消す（§5.2） |

### 4.9 子プロセスの環境と秘密

| 起こす側 → 起こされる側 | 渡す環境 |
|---|---|
| Unity → runner | 空から: `PATH`（固定）、`HOME`、`LANG`、`VRCRT_BRIDGE_URL`、`VRCRT_CERT_SHA256`、`VRCRT_PYTHON`（あれば）。トークン・`VRCRT_FIXTURE_DIR` などの試験用の変数は入れない |
| runner → `vrcrt.sh` | runner の環境から、上の変数と、CI のときだけ既にある `VRCRT_TOKEN`。`VRC_REMOTE_TEST_TOKEN` は外す（`vrcrt.sh` は Keychain から自分で読む） |
| runner → headless Unity | 上に加えて `VRC_REMOTE_TEST_BRIDGE_URL`・`_CERT_SHA256`・`_SCENE_PATH`・`_RUN_DIR`・`_WORLD_BUILD_TAG` と `VRC_REMOTE_TEST_TOKEN`（Keychain か `VRCRT_TOKEN` から。今の `vrcrt-run.py` と同じで、headless の Unity はこれが無いと配備できない） |
| `vrcrt.sh` → 正規表現の子 | 空の環境（今は親の環境を引き継いでいる。ここは CLI の振る舞いを変えない小さな直し） |
| `vrcrt-run.py` → 子 | 今と同じ（Unity と `vrcrt.sh` にトークン）。正規表現の子は上の直しで空になる |

トークンを引数・進捗の JSON・`run.json`・診断の出力に入れない。見張りの値（偽のトークン）を入れた試験で、子孫・ファイル・stdout/stderr に出ないことを確かめる。

### 4.10 受け入れの条件

- Unity のボタン1つで、2台の Relaunch に配備して、台の準備・（tag を埋めたワールドなら）ワールド・ビルドの準備・証拠の回収・`run.json` まで済む。CLI の `runner run --build headless` でも同じ `run.json` の形になる。
- headless・メニュー・ウィンドウのどれでも結果ファイルができ、§4.3 の分類どおりになる。buildId の無い失敗と取り消しも別々に記録される。
- §6 の互換の一覧がすべて今と同じ（`test-vrcrt.sh` が無修正で通る。`vrcrt-run.py` は新しい直接の試験で通る）。
- ウィンドウから1台ずつ close/launch でき、応答の扱いが CLI と同じ。
- §4.9 の環境の決まりが試験で確かめられている。

### 4.11 失敗側の試験（期待する断りの理由と結果）

| 試験 | 場所 | 期待 |
|---|---|---|
| deployed＋partial（矛盾） | Unity Editor テスト（分類） | deploy UNKNOWN、手順は止まる |
| failed＋partial（2中1） | 同上＋runner | launch FAIL、clientReadiness FAIL |
| sha256 が成果物と違う／buildId 違い／launch 欠け | 同上 | UNKNOWN |
| buildId の前の失敗（busy・SDK の失敗）、buildId の前と配備中の取り消し | Unity Editor テスト | `attempt-<id>.json` に stage と FAIL／CANCELED、取り消しは文言でなく印で |
| Deploy Last Build で bytes が変わった／変わっていない | Unity Editor テスト | tag null（worldBuildMatch UNKNOWN）／元の tag |
| 1台がエラーワールド（joined＋inErrorWorld） | runner の fixture | 期限で clientReadiness FAIL、理由 `inErrorWorld` |
| 1台が likelyLoggedOut | runner | UNKNOWN、profile の案内 |
| 準備のあとに1台が退出・入り直し・エラーワールドへ | runner | UNKNOWN（前の準備完了を無効） |
| 偽の目印・古い世代の目印・別の tag・1行に2つの tag・tag の境目で切り詰めた行・伏せた行 | runner | 古い世代は数えない、別の tag は FAIL、2つ・切り詰め・伏せは UNKNOWN |
| 目印の洪水（droppedThroughSeq が進む、1万行/秒相当の fixture） | runner | 1回の待ちの仕事が有界、UNKNOWN（観測の欠け）、取り消しが 1 s 以内に効く |
| 正規表現が 2 s かかる・書き出しが遅い・子が止まる | runner | 取り消しと期限が効き、子が回収され、CANCELED／UNKNOWN |
| headless の Unity の配備中の取り消し・期限（偽の Unity 実行体で、送信の前と後） | runner | グループごと止めて回収、送信の後なら「撤回ではない」を記録、送り直さない、CANCELED／UNKNOWN |
| Editor を閉じる（runner の親が消える）: 待ちの間・書き出しの間 | runner | runner が最後まで進め `run.json` を書き終える、claim が消える |
| 同じ Bridge に2つ目の prepare（ボタンと CLI）。prepare の後の配備中・持ち主の移し替えの最中・期限の停止の猶予中に2つ目 | runner | 2つ目は断る |
| 残った claim: PID が消えた、PID はあるが起動時刻が違う（再利用）、30分の期限切れ、記録した子がまだ動いている | runner | 前の3つは前の run を回復してから引き継ぐ。子が動いていて止まったと確かめられなければ断る |
| runner を SIGKILL（待ちの間・書き出しの間・headless の配備の最中・R1b の close/launch の要求の最中） | runner | 次の prepare か `recover` で `runnerInterrupted` の UNKNOWN、確定済みの結果と証拠は残る、送り直さない |
| 同じ tag・別の buildId の2つの run が同じ Bridge に配備（別の根・claim を通らない） | runner | R1a では見分けられない限界として記録されることを確かめる（R1b では buildId 違いで UNKNOWN） |
| Reload＋同じ bytes で、合う tag の目印がカーソルより前にしかない | runner | worldBuildMatch UNKNOWN（PASS にしない） |
| `/clients` が古い・processListReadable false | runner | UNKNOWN |
| Bridge のプロセス・host 世代・etag が途中で変わる | runner | UNKNOWN |
| 通信の切断（DOWN fixture） | runner・CLI | UNKNOWN、状態を変える操作を送り直さない |
| Mac のディスクが一杯（閾値の差し替え）・Windows の空き不足・書き出しの貯まり | runner | 書き出しせず UNKNOWN |
| 取り消し（cancel.request、SIGINT） | runner・Unity | CANCELED、途中の `run.json` が残る |
| パスの抜け（根が git・iCloud・Documents の中、リンク、`..`、`run.json` のパスを差し替え、既にある敵対的な `.part`、リンクへの差し替え） | runner・Unity | 断る、外のファイルを書かず消さない |
| 見張りのトークンが子孫・ファイル・出力に出ない | runner・Unity・CLI | 出ない |
| 宣言と Bridge の設定の不一致 | runner | 何もせず UNKNOWN（declarationMismatch） |
| 開いている Editor に headless を重ねる | runner | 断る |
| Python が無い・3.8 | Unity・runner | 断って案内 |
| `vrcrt-run.py` の互換（偽の Unity 実行体で: 出力ファイル名、`--expect-marker`・`--marker-slot`・`--marker-timeout`、配備だけの成功、古いパッケージで結果ファイル無し） | 新しい `scripts/test-vrcrt-run.sh` | 今と同じ出力と終了コード、古いパッケージでは警告つきでログから |

試験の置き場所: runner は `scripts/test-vrcrt-runner.sh`（`VRCRT_FIXTURE_DIR` と既存の小さな HTTPS サーバーを使う）、`vrcrt-run.py` は `scripts/test-vrcrt-run.sh`、どちらも `test-vrcrt.sh` の終わりから呼ぶ。Unity は `package/Tests/Editor/Build/`。

### 4.12 Windows 実機で確かめること（`docs/validation/runner-deploy-collect.md` に分けて記録）

- エディター内の Build & Test で、Relaunch 2台（profile 0,1）と Reload 1台。ボタン1つで run が PASS になり、証拠の大きさとハッシュが合う。
- likelyLoggedOut の案内とエラーワールドは、Windows で手で作らない（2026-11-25 に決定）。runner の fixture で確かめ、実機ではそうなった台があったときだけ記録する。
- tag を埋めた小さな試験ワールドで worldBuildMatch PASS、Reload で前のワールドのままのとき FAIL か UNKNOWN。SDK のビルド前処理で tag を確かに埋められるか（Unity 2022.3.22f1、`requires_validation`）。
- ウィンドウの1台の close/launch が CLI と同じ結果になる。
- Reload（`--watch-worlds`）で同じ bytes を Deploy Last Build したとき、`joinedSeq`・epoch が進むか、`Start()` から出す目印がもう一度出るか（`requires_validation`）。出なければ台の準備は FAIL か UNKNOWN、worldBuildMatch は UNKNOWN が期待どおりで、カーソルより前の目印で PASS にならないことを見る。
- 4台は R1a では確かめず、記録に「未確認」と書く（Windows の空き次第で追加）。

### 4.13 新しい依存・集めるデータ・トークンを持つ人が新しくできること

- 新しい依存: なし（Python 標準ライブラリ、既存の Newtonsoft だけ）。runner は利用者の Mac の Python で、利用者の権限で動く（サンドボックスではない）。使った Python の realpath・版・sha256 を記録する。
- 集めるデータ: 明示の run のときだけ、選んだ slot の全文ログ（既存の `LogRedactor` を通したもの。表示名・ワールドの情報・ワールドが出した文字は残りうる）、目印、`/clients`・`/bridge` の snapshot、配備の結果（Unity のメッセージは秘密の伏せをしていない）。画像は R1 では取らない。0700 は持ち主だけが読めるという意味で、暗号化ではない。
- トークンを持つ人が新しくできること: なし（Bridge の変更が無い）。

## 5. R1b（v0.12.0、Bridge の変更あり）

### 5.1 狙い

close/launch の前提を Bridge の操作の門の中で原子的に照らし、違えば何もしない。それを使う固定の手順「宣言した slot の退出と起動し直し」（leave-rejoin）を runner に入れる。実行全体の lease は作らず、競合したら UNKNOWN で止める。応答が切れたら、状態を読み直して記録するだけで、続けずに UNKNOWN で止める（自動の回復はしない）。

### 5.2 Bridge の変更

- `Runtime/BridgeRuntime.cs`: `InstanceId`（プロセスの起動ごとの128ビットの乱数、16進32字）と `OperationRevision`（プロセスの寿命で単調に増える long）。
- revision を増やす決まり: Windows の上で何かが変わった、または変わったかもしれない時点ごとに、門の中で1つ増やす。HTTP の結果が失敗でも、その前に副作用があれば増やす。対象は、配備の claim と結果の書き込み、`PrepareAsync` の起動の計画の消去（失敗した配備を含む）、台を閉じる（全台、1台、設定の mode の切り替えの中で閉じたあと 409／412／500 で終わる場合も）、1台の launch（起動した・記録できない）、設定の書き込みと適用の結果、restart、host の起動。副作用の前に断った要求（形の誤り、前提の不一致、slot の拒否、busy）は増やさない。
- `GET /api/v1/bridge` に `bridgeInstanceId`、`operationRevision`、`currentBatch {buildId, launchedSlots}` を足す。
- `GET /api/v1/capabilities` に `features: ["slotPreconditions.v1"]` を足す。`apiVersion` は 1 のまま（任意の本文と任意のフィールドを足すだけで、古い Unity・CLI は今のまま動く。仕様の「足すだけなら版は上げない」に合わせる）。
- `IVrchatClientCoordinator.LaunchClientsAsync(deployedWorldPath, buildId, ct)` に buildId を足し、`StagingWatcher.cs:730` から manifest の buildId を渡す。`VrchatRelaunchCoordinator`（`BeginBatch(world, roomId, buildId)`）、Reload 側の実装、試験の偽物（`FakeVrchatClientCoordinator`）も合わせる。
- `VRChat/RelaunchLaunchContext.cs`: バッチの buildId と、slot ごとの「Bridge が閉じたと確かめた最後の世代」を持ち、`Clear`/`BeginBatch` で消す。
- 書き出しの掃除: Bridge が自分の `state/exports` の `.log` を、作ってから 24 時間（`proposed`）を過ぎたら host の起動時と1時間ごとに消す（自分が作った形の名前だけ、リンクは辿らない）。`.part` の残りも消す。
- `POST /clients/{slot}/close`・`/launch` の任意の本文:
  ```json
  {"expect": {"bridgeInstanceId": "...", "hostGeneration": 3, "activeConfigEtag": "...", "savedConfigEtag": "...",
              "operationRevision": 17, "buildId": "...", "clientGeneration": 1234}, "operationId": "..."}
  ```
  - 本文の有無は、ヘッダーではなく実際に読んだバイトで決める。上限 4 KiB を読みながら数え（chunked も同じ。既存の `ReadPatchAsync` の型）、0 バイトのときだけ今と同じ動き・応答・HTTP の状態。
  - 1バイトでもあれば、`Content-Type: application/json`、JSON の object、名前の重複なし、決まったフィールドだけ、全フィールドがそろい型と形が正しいときだけ受ける。欠け・余分・重複・型違い・JSON でない・大きすぎは 400 `invalidPrecondition` で、副作用の前に断る（省略として扱わない）。
  - 門に入った直後に、プロセス（instanceId＋hostGeneration）、active/saved の etag、revision、現在のバッチの buildId を照らす。close は記録と4点で一致したハンドル（今の流れの中で、選び直さない）の世代を `clientGeneration` と照らす。launch は今の「動いている台が無い」「Bridge が起動していない VRChat が無い」の確認を保ったうえで、「Bridge が閉じたと確かめた最後の世代」か記録の（終了した）台の世代が `clientGeneration` と同じことを照らす。違えば何もせず 412 `{error:"preconditionFailed", mismatched:[...], current:{...}}`。門がふさがっていれば今と同じ 409 busy。
  - 成功・未完了の応答に `operationId`（送られたとき）、`operationRevision`（操作の後の値。この1つの名前だけを使う）、`bridgeInstanceId` を足し、close には閉じた `clientGeneration` も足す。本文なしの要求にも同じフィールドが付く（足すだけ。今の CLI の検査を壊さないことを試験で確かめる）。operationId は Bridge のログに出して突き合わせに使うだけで、履歴や重複除去は持たない。
- 変えるファイル: `Lan/BridgeAdminApi.cs`、`Lan/BridgeLanApi.cs`、新規 `Lan/SlotPrecondition.cs`、`VRChat/ClientSlotControl.cs`、`VRChat/RelaunchLaunchContext.cs`、`VRChat/IVrchatClientCoordinator.cs`・`VrchatRelaunchCoordinator.cs`・Reload 側の coordinator、`Runtime/BridgeRuntime.cs`、`Deployment/StagingWatcher.cs`、書き出しの掃除（`Lan/` か `Runtime/` の小さなサービス）、試験の偽物。

### 5.3 CLI と Unity

- `vrcrt.sh close-client <slot> --expect @file [--operation-id ID] --json`、`launch-client` も同じ。`--expect` があるときだけ、先に capabilities を読み、`slotPreconditions.v1` が無い・読めなければ POST を送らず終了コード 3（unsupported）。412 は終了コード 1 で、`--json` なら本文を出す。`--expect` の欠け・不正は終了コード 2。`--expect` が無ければ今と同じ（412 を断りとして読むのは `--expect` のときだけ）。
- Unity のウィンドウの Close／Launch は、Bridge が feature を告げるときは、表示した行の世代と `/bridge` の値で前提をつけて送る（412 なら「表示のあとに変わった。読み直して」と出す）。告げなければ R1a と同じ。

### 5.4 固定の手順「宣言した slot の退出と起動し直し」（leave-rejoin）

引数: `--target-slot N`、`--slots 0,1`（明示の全 slot）、`--remaining-marker <regex>`（任意。残る台の移りの条件。利用者が CLI かウィンドウで渡す。目印から作らない）、各期限。対象の slot、所有権の移り方の意味、合否は利用者が決める。`isMaster` をゲームの持ち主と見なさない。同じ run の deploy-collect のあとに続けて行う（前の run を参照する入口は作らない）。

1. 前提: feature の確認（無ければ何も送らず UNKNOWN）、宣言の確認、全対象の台の準備（deploy-collect の最後の判定のまま）。R1b の deploy-collect は、準備を固定するときから `/bridge` の `currentBatch.buildId` が結果ファイルの buildId と同じこと、revision が配備のあと動いていないことも照らす（違えば UNKNOWN）。
2. close の前: `/bridge` と `/clients` を読み直して全 slot を期待と照らす。全文ログの snapshot と目印を全 slot で取り、取った時刻と切った位置を記録（終了の直前の最後の行まで入っているとは扱わない）。操作の意図（operationId、送る expect）を `run.json` に先に書く。
3. close（前提つき）。200 で `operationRevision == 期待+1`、閉じた世代が対象の世代なら予期した変化として受け入れる。412・409・500・応答の形違いは UNKNOWN で止める。応答が切れたら送り直さず、`/bridge` と `/clients` を読み直して記録し、UNKNOWN で止める。
4. 残る台の条件を待つ: 残る台が同じ世代・epoch・joinedSeq・instance のまま、対象の slot の閉じた世代が `running == false`、任意の `--remaining-marker` がカーソルの後に残る台で出る（切り詰め・伏せの行では成り立たない）。revision が自分の操作のほかで動いたら UNKNOWN。
5. launch の前: 対象の slot の前の世代の目印を取り直し、世代・epoch・catchingUp を照らして保存。残る台の条件の証拠を保存。
6. launch（前提: revision は close の後の値、`clientGeneration` は閉じた世代）。応答の新しい世代と `operationRevision == 期待+1` だけを予期した変化として受け入れる。切れたときは 3 と同じく読み直して UNKNOWN で止める（launch を重ねない）。
7. 新しい世代の準備: 対象の slot が応答の世代で、残る台と同じインスタンスに joined、authenticated、エラーワールドでない。tag があれば新しい世代でもワールド・ビルドの目印を待つ。
8. 新しい世代の証拠を別のフォルダーに保存し、最後の判定で全 slot を照らす。
- 利用者の取り消しは CANCELED。close/launch の最中の取り消しは、結果を不明と記録して送り直さない。自動のやり直しや片付けで対象を取り直さない（閉じたまま止まったら、そのことを記録と画面に出し、人が Close/Launch の画面で続けるか決める）。
- 前の世代の `.prev`、過去の世代の API、終了後の完全な全文ログは入れない。必要な証拠が欠けた run は UNKNOWN。

### 5.5 実機で確かめる前は有効にしない

- runner は確かめ済みの手順の一覧を定数で持つ（例 `VALIDATED_PROCEDURES = {"deploy-collect": "0.11.0"}`）。leave-rejoin は一覧に無いあいだ、CLI では `--unvalidated` を付けたときだけ動き（実機の確認用）、Unity の画面には出さない。capability があることだけでは有効にならない。
- 実機の確認（§5.8）を `docs/validation/slot-preconditions.md` に記録し、その Bridge の実行体の SHA-256・パッケージの版・台の構成を書いた commit で、一覧に leave-rejoin を足す。それより前のリリースのタグは打たない（v0.10 と同じく、ブランチのビルドを Windows で確かめてから出す）。

### 5.6 受け入れの条件

- 2台の Relaunch で、Unity のボタン1つ（または CLI 1行）で deploy-collect に続けて leave-rejoin が PASS になり、前の世代・残る台・新しい世代の証拠が分かれて保存される。
- 本文なしの close/launch（Commander の `dice_life_auto` の呼び方）が、HTTP の状態・本文（足したフィールドを除く）・CLI の終了コードとも今と同じ。
- feature の無い Bridge（v0.10/v0.11）には前提つきの POST を1回も送らない。
- 繰り返しの書き出しで Windows の `state/exports` が上限を越えて貯まらない。

### 5.7 失敗側の試験

| 試験 | 場所 | 期待 |
|---|---|---|
| 本文なしの close/launch | Bridge HTTP（`BridgeHostTests`）・test-vrcrt.sh | 今と同じ応答（足したフィールドだけ増える）、終了コード同じ |
| chunked の正しい本文・壊れた本文・途中で切れた本文・4 KiB 超、Content-Length 無しの本文 | Bridge HTTP | 正しいものは前提として照らす。ほかは 400 invalidPrecondition、何もしない、revision 不変 |
| 欠けた・余分・名前の重複・型違い・JSON でない expect | Bridge 単体・HTTP | 400、何もしない、revision 不変 |
| instanceId 違い（Bridge が再起動した）・hostGeneration 違い・etag 違い・revision 違い・buildId 違い・世代違い | Bridge | 412、何もしない |
| ほかの経路の操作（全台 close、config、mode の切り替えで閉じてから 409 で終わったもの、配備、失敗した配備による計画の消去）の後の古い revision | Bridge | 412 |
| close／launch の応答が切れる（DOWN/SLOW） | runner | 送り直さない、読み直して記録し UNKNOWN、launch を重ねない |
| feature の無い Bridge | runner・CLI | POST を送らない（requests.log に無い）、UNKNOWN／終了コード 3 |
| 対象のプロセスが close の前に消えた | Bridge・runner | 412 または notRunning で UNKNOWN |
| 待ちの間に残る台が退出・エラーワールド | runner | UNKNOWN |
| 偽の目印・古い世代・切り詰めた目印で残る台の条件が成り立ったように見える | runner | 世代・epoch・カーソル・印で数えない |
| `VALIDATED_PROCEDURES` に無い leave-rejoin を `--unvalidated` 無しで | runner・Unity | 断る、画面に出ない |
| 書き出しの掃除がリンク・ほかの名前のファイルを消さない | Bridge 単体 | 消さない |
| ディスクが一杯・取り消し | runner | UNKNOWN・CANCELED、閉じたままを記録 |

### 5.8 Windows 実機で確かめること（`docs/validation/slot-preconditions.md`）

- Bridge の実行体の入れ替え（Windows 側のセッションに頼む。2026-11-25 に決定。`docs/setup-windows.md` の手順と SHA-256 の照合、リリースの来歴の確認）。
- 2台で leave-rejoin PASS（`--unvalidated` で）。Mac から本文なしの `close-client` で先に閉じてから古い expect で送ると 412／notRunning で何もしない（Windows で手で閉じる代わり）。Bridge を `vrcrt.sh restart` で再起動してから古い expect で送ると 412。
- 本文なしの `vrcrt.sh close-client`／`launch-client` が v0.10 と同じに動く（Commander の `dice_life_auto` の呼び方を1回流す）。
- 書き出しの掃除が 24 時間を過ぎたものだけを消す（時刻を短くした設定ではなく、試験の差し替えで模擬し、実機では消えたことを1回見る）。

### 5.9 新しい依存・集めるデータ・トークンを持つ人が新しくできること

- 新しい依存: なし。
- 集めるデータ: R1a と同じ種類を、世代ごとに分けて取る。
- トークンを持つ人が新しくできること: `bridgeInstanceId`・`operationRevision`・現在のバッチの buildId を読める。前提つきの close/launch は、今の close/launch でできることを狭めるだけで広げない。Bridge は自分の古い書き出しを消す（利用者の手での取り出しの前に消えうる）。仕様書の「トークンを持つ人が新しくできること」に、v0.10 の1台の close/launch（今は書かれていない）と合わせて書き足す。トークンが漏れればその持ち主は今と同じく何でもできる（前提条件は競合を防ぐもので、認証の代わりではない）。

## 6. 互換の一覧（壊さない使い方）

R1a・R1b のどちらでも、下の使い方の引数・出力・終了コード・名前を変えない。新しいものは足すだけにし、`test-vrcrt.sh`・新しい `test-vrcrt-run.sh`・Unity の Editor テストにこの一覧の試験を足す。

| 使い方 | 頼られていること | 利用者 |
|---|---|---|
| `scripts/vrcrt.sh` の場所（`$VRCRT_SH` 無しの既定 `../vrc-remote-test/scripts/vrcrt.sh`、machine.json の `vrcrt`） | このパスで今の全サブコマンドが動く | Commander `prt001_deploy_run.py:1150`、kit `vrcdev_host.py:244` |
| `config get` | 0、JSON の `active`／`values` に `VrchatProfiles`・`VrchatClientMode`・`VrchatClientCount`・`VrchatMode`・`AutoLaunchVrchat` | 両方の `client_plan.py` |
| `status --json` | 0、`clients[].slot/running/processId/isMaster/clientGeneration/state/instance` | Commander |
| `log <slot> --markers`（`--json` 無し） | `seq\t<エスケープした行>` と `# {json}` のヘッダー、今の世代の最初から | Commander の判定 |
| `close-client <slot>`／`launch-client <slot>`（本文・`--expect` 無し） | 0＝済み、launch の 3＝起動したか不明、ほかは止める。stdout の1行 | Commander `dice_life_auto` |
| `wait-same-instance <秒> --slots a,b` | 位置引数の秒、0＝同じインスタンス | Commander、kit |
| `export-log all <dir>` | ファイル名 `vrchat-slot-N-generation-G-epoch-E-<id>.log`、中身は VRChat のログ全体 | 両方の `client_plan.py` |
| `doctor`・`profiles`・`close-clients` | 名前と意味 | kit |
| 終了コード 0/1/2/3 | 意味 | 全員 |
| `scripts/vrcrt-run.py` の引数・出力ファイル名・終了コード 0/1・成功の意味 | そのまま（§12 の3を採るときだけ、`--output-dir` が git・iCloud の中なら断る例外を CHANGELOG に書き、移す先として既定の根を案内する） | README の利用者 |
| メニュー `VRChat SDK/Remote Build` | 実行と有無の検出 | Commander、kit |
| Unity のログ行 `[VRC Remote Test] Upload complete. Build ID: X`、`Deployed as …`、`Bridge error: CODE - …`、`Artifact: … (N bytes)`、`[Failed] …` | 文言と並び（結果ファイルを足しても出し続ける） | Commander `PRT001Evidence.cs:24-27`、kit `KitDispatcher.Sdk.cs:29-33` |
| リフレクションで呼ぶ `RemoteBuildCommand.GetCoordinator`、`RemoteBuildCoordinator.LastArtifact`／`IsRunning`／`DeployLastBuildAsync(null, ct)`、`BuildArtifact.FromSdkPath`、`RemoteBuildOutcome.Succeeded/BuildId/ErrorCode/ErrorMessage` | 名前とシグネチャ（新しい公開の入口は別に足し、これらは消さない） | Commander `PRT001Dispatcher.SavedBuild.cs` |
| `RemoteTestSettings`・`LanRemoteTransport`（3引数）・`VrchatClients`・`BuildResult` の公開の形 | 型と動き | Commander `PRT001Evidence.cs` |

## 7. 工数（同じ範囲で比べる。実装の日数と Windows の空き待ちを分ける）

| 段 | 実装（Mac） | Windows 実機の作業 | Windows の空き待ち |
|---|---|---|---|
| 基準の実測（§2） | 0.5 日 | 0.5 日 | 未知（利用者の VR の利用次第） |
| R1a | 7〜9 日（Unity 3、runner と子の監督 3、CLI と互換の試験 1、試験と文書 1〜2） | 0.5 日 | 未知 |
| R1b | 5〜7 日（Bridge 2.5〜3、CLI・runner 1.5〜2、Unity 0.5、文書 1） | 0.5〜1 日（入れ替え含む） | 未知 |

（Codex のレビューで、応答が切れたあとの回復・任意の tag・`--build none`・画面の削除・前の run の参照を削り、本文の判定・子の監督・保存先・Windows の掃除を足した。差し引きで初めの見積もりと同じ幅に収まる。どれも実測ではない。）

## 8. 文書と進め方

- リリースごとに `package/CHANGELOG.md`（英語、どちらを更新するかを太字で）、仕様書（R1a: 結果ファイルと runner の節、R1b: 前提条件の節と「トークンを持つ人が新しくできること」）、`README.md`、`package/README.md`、`docs/setup-windows.md`（R1a: Windows の書き出しの片付け、R1b: 入れ替え）を更新する。
- 各段の実装の計画（R1a の Unity／runner・CLI、R1b の Bridge／CLI・runner）と差分ごとに Codex のレビューを受ける。
- 実機の記録は `docs/validation/` に分け、模擬の試験・実機、エディター・ローカル Build & Test・アップロード版、1/2/4台を混同しない。生の証拠は validation の記録に写さず、人が見た要約と証拠の ID だけを書く。
- 試験: `dotnet test`（Bridge）、`bash scripts/test-vrcrt.sh`（＋runner と `vrcrt-run.py` の試験）、`pwsh scripts/test-windows-scripts.ps1`、Unity 2022.3.22f1 のコンパイルと Editor テスト。既存の Actions の SHA 固定とリリースの来歴の確認を保つ。

## 9. R2 以降（候補。調べる範囲と効果の測り方だけ）

R1 のあとの測定で並べ直す。どれも、作る前に新しい依存・集めるデータ・トークンを持つ人が新しくできることを並べて示し、安全の境界を実機で確かめるまで有効にできる完成品として出さない。

| 候補 | 調べる範囲 | 効果の測り方 | 見送る・止める条件 |
|---|---|---|---|
| R2 画面の記録 | Bridge が記録した1台（PID・作成時刻・session・名前）のウィンドウのハンドルを毎回照らして撮れるか（PrintWindow／Windows.Graphics.Capture）、ログイン画面の見分け、1枚の時間と大きさ、保存先が Mac の実行フォルダーから外れないこと | 「実クライアントでの見た目」の代表1件で、人の撮影と保存の手数 vs API 1回 | 対象を確かめられない、デスクトップ全体や前面の窓に代えるしかない |
| R3 手作業の試験の準備と記録 | slot 0 を VR・slot 1 をデスクトップの観戦台にする準備の手数、押した時刻の目印、OK/NG とメモの保存の形、空き待ち（この run の台のほかに VRChat が無い）の確かめ方 | 代表 C の準備と記録の手数 | 手で起動された VRChat を区別できない |
| R4 起動の幅 | 追加の profile で同じ roomId に途中参加（飲み物 D4 の3台目）、同じプロセスのままの入り直しの可否、`launch.exe`／`vrchat://launch` で起きたプロセスの PID・作成時刻・session・profile の対応づけ、既存のプロセスへ渡されないか、doctor の TLS の不達と拒否の区別・許可元と Mac の IP の比較・Smart App Control の兆候（読むだけ） | 飲み物 D4 の手作業の `Start-Process` の手数、「アップロード版と2アカウント」の代表1件 | 起動したプロセスを確かに対応づけられない |
| R5 デスクトップ入力 | 公式 OSC `/input/*` がデスクトップの台で効くか、`--osc=` の台ごとのポートと受け口の bind と持ち主の PID、同じ PC の loopback から効き別の LAN ホストから効かないこと、ファイアウォールで遮れること。OS レベルの入力は利用規約と Easy Anti-Cheat の公式資料を確かめてから利用者に相談 | 代表 A のクリック4回のうち OSC で置き換えられる数（UI のボタンは OSC で押せない可能性が高い。`requires_validation`） | 別の LAN ホストからの入力を遮れると確かめられない |
| R6 配信と性能 | 既存の `Deploy Last Build` で減らせる変種のビルドの数、Commander の localhost:8765 の配信を保つこと、Windows の環境（GPU・ドライバー・リフレッシュレート・電源設定）の記録の項目 | 変種1つの切り替えにかかる時間、`prt001_perf.py` の `--perf-env` の手入力の数 | 管理の認証を弱める・秘密を配る・LAN に出す必要がある |

## 10. Commander 側でやるとよいこと（このリポジトリでは実装しない。提案として報告する）

- `PRT001Dispatcher.SavedBuild.cs` のリフレクションを、R1a の `RemoteBuildApi` に置き換える。
- `PRT001Evidence.cs` と `prt001_deploy_run.py:1581` のログ行の正規表現を、結果ファイルを読む形に置き換える（ログ行は道具が出し続けるので急がなくてよい）。
- `PrtLog` に `vrcrt.build=<tag>` を出す1行を足し、ビルド前処理で `RemoteBuildApi.ActiveWorldBuildTag` を埋める。
- `dice_life_auto` の流れを、場面12（代表 A）にも広げる。持ち主の slot は Commander が自分の目印で決め、R1b の leave-rejoin に `--target-slot` で渡す。
- validation の記録に、runId・buildId・人の操作の回数・経過時間の決まった欄を足す（今は文の中にある）。記録と判定の出力のファイル名（`lifecycle-verdict.json` と `verification-t3-lifecycle.json`）を合わせる。

## 11. Codex のレビュー（/codex-plan-review、gpt-6.1-sol・high、4ラウンド）

| ラウンド | 判定 | 主な指摘と直したところ |
|---|---|---|
| 1（設計と実現性・セキュリティとリスク・代替案と簡素化の3並列） | 3つとも NEEDS_REVISION（0.92〜0.94） | 本文の有無を Content-Length で決めると chunked で前提をすり抜ける → 実際に読んだバイトで判定（§5.2）。副作用ごとの revision（§5.2）。応答が切れたら UNKNOWN で止め `lastSlotOperations` を削除（§5.4）。実機の確認前は有効にしない（§5.5）。Deploy Last Build の tag と成果物の対応（§4.3）。buildId の前の失敗と取り消し（§4.3）。`vrcrt-run.py` は今の流れのまま（§4.2）。子の監督・環境・保存先・Windows の書き出しの貯まり・目印の切り詰め（§4.2・§4.4・§4.5・§4.8・§4.9・§5.2）。任意の tag・`--build none`・削除の画面・`--from-run` を削除 |
| 2（未解決14項目と反例7件） | NEEDS_REVISION（0.94）、14項目中13が解決 | headless の Unity の取り消し、同じ Mac での run の重なり、Editor を閉じたあとの継続、Reload で同じ bytes の実機の挙動 → §4.1・§4.2・§4.11・§4.12・§5.4 |
| 3 | APPROVE_WITH_CHANGES（0.96） | claim の持ち主の移し替え、runner が落ちたときの回復 → §4.2・§4.4・§4.11 |
| 4 | APPROVE（0.96）、残りの懸念なし | — |

- Codex が挙げた file:line は、`BridgeAdminApi.cs:340-376,699-730`、`BridgeLanApi.cs:338-430`、`VrchatMarkerLog.cs:168-182`、`vrcrt.sh:1000-1015`、`IVrchatClientCoordinator.cs:26` を読んで実物と一致することを確かめた。
- plan mode のため、スキルの決まりの `.claude/docs/research/` には書いていない。生の出力は一時フォルダーにある（`/private/tmp/claude-501/.../tasks/` の `bfhgjx32s.output`・`b2yeokoye.output`・`b5fs662m4.output`・`bvmrs5s03.output`・`b9f08qmm2.output`・`bww2vmvt4.output`。どれも 130 KB 以上）。利用者が細かい質問を続けないよう求めたので、スキルの Phase 0・2c・4 の質問は省き、標準の深さ（2〜5ラウンド）・バランス重視で進めた。
- 実装に入ったら、R1a の Unity／runner・CLI、R1b の Bridge／CLI・runner の各段で、実装の計画と差分を Codex にレビューしてもらう。

## 12. 判断の要る質問（2026-11-25 に、利用者が計画の案で進めるよう決めた）

1. ワールド・ビルドの目印の形は `vrcrt.build=<tag>`（Bridge の目印の接頭辞の行の中、空白で区切った1つの欄）。
2. 基準は記録からの推定にした（§2）。R1b の Bridge の入れ替えは Windows 側のセッションに頼む。
3. 実行の根の既定は `~/Library/Application Support/VRCRemoteTest/` で、`~/Documents`・`~/Desktop`・iCloud・git の中は断る。`scripts/vrcrt-run.py --output-dir` は git と iCloud の中だけを断る（CHANGELOG に互換の例外として書く）。
4. R1b の Bridge は自分の書き出しを 24 時間で消す。
