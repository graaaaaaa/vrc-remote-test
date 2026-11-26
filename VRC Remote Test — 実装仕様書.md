# VRC Remote Test 実装仕様書 — LAN Direct

この文書は Bridge と Unity パッケージを一組で更新する LAN Direct 版の仕様です。旧方式の設計根拠と実機記録は `docs/validation/history` と `docs/validation` に保存しています。

## 接続と認証

Bridge は `Bridge:LanBindAddress` に指定した単一の LAN IP と `LanPort`（既定38443）で HTTPS を待ち受けます。初回 `pair` コマンドで自己署名証明書の SHA-256 指紋とランダムな256ビットトークンを表示します。Unity と `vrcrt.sh` は証明書指紋を固定照合し、全 API 要求へ Bearer トークンを付けます。Unity の対話設定のトークンは macOS Keychain、CI のトークンは秘密環境変数に保存します。Bridge は API 版とビルドプロトコル版を `/api/v1/capabilities` で返し、一致しないクライアントはビルド前に失敗します。旧接続方式は受け付けません。

Bridge の秘密ファイルと状態は `%LOCALAPPDATA%\VRCRemoteTest` 内に置きます。Bridge プロセスは通常ユーザーの対話セッションで動き、受信したデータをそのユーザーの専用領域だけに書きます。Windows ファイアウォールの接続元は開発機の IP に絞ります。

## ビルド API

1. Unity はビルド済み `.vrcw` のサイズと SHA-256 を求め、英数字等で構成した build ID を割り当てます。
2. `PUT /api/v1/builds/{buildId}/artifact` でファイルを逐次送信します。Bridge は一時ファイルへ逐次保存し、読み取ったバイト数で `MaxArtifactSizeBytes` を強制します。中断した一時ファイルは削除します。同一 ID で同一内容の再送は許し、異なる内容は競合にします。
3. `POST /api/v1/builds/{buildId}/commit` に manifest を送ります。Bridge は build ID、ファイル名、サイズ、SHA-256、プロトコル版を検証し、専用 `incoming` キューへ確定します。同じ manifest の再試行は冪等、異なる manifest は競合です。
4. Bridge の配置ループは専用キューからスナップショットを取得し、既存の検証・配置・archive・結果書き込みを実行します。`GET /api/v1/builds/{buildId}` で結果を取得します。再起動後は専用領域に残るキューと処理中状態から復旧します。

対話 Unity では `UploadHandlerFile` を使い、エディターを止めずに送信します。Headless はファイルストリームから送信します。結果の照会も非同期です。

## 状態とログ API

`GET /api/v1/status`、`/clients`、`/logs/latest`、`/logs/clients/{slot}`、`/markers/clients/{slot}` が Bridge の専用領域に発行した観測情報を返します。`vrcrt.sh` と Unity のログ画面はこれらの同じ API を読みます。

全文ログは Unity の明示操作だけで `POST /api/v1/log-exports` に台番号、client generation、observation epoch を送って作成し、`GET /api/v1/log-exports/{id}` で取得します。Bridge は観測済みの台・セッション・元ファイルを照合して要求時点の長さまで読み、既存の `LogRedactor` を適用します。対応関係が不明、元ファイルが入れ替わった、セッションが変わった場合は競合として失敗します。Unity はサイズと SHA-256 を確認してから保存先を確定します。

## 管理 API（v0.8.0）

Windows の前に人がいなくても日々の管理ができるよう、次の経路を同じ認証で足します。手順は `docs/setup-windows.md` の4です。

- `GET /api/v1/bridge`: 版、実行体の SHA-256、host の世代、待ち受けの URL、証明書の指紋、いま効いている設定と `config.json` の etag、最後の設定の変更の結果、注意。パスは Windows のユーザー名を伏せて返します。
- `GET /api/v1/logs/bridge`: Bridge 自身のログの末尾（最大500行、`LogRedactor` を通す）。
- `GET /api/v1/config` と `PUT /api/v1/config`: 決まった設定だけを、決まった範囲で変えます。PUT は `If-Match` に `config.json` の SHA-256 を要し、本文は `Bridge` の節への JSON Merge Patch です。本番と同じ binding と検証を通った内容だけを書き、元のバイト列をバックアップに残し、一時ファイルを flush してから置き換えます。
- `POST /api/v1/bridge/restart`: プロセスの中で host を作り直します。インスタンスのロック、証明書とトークン、操作の門はプロセスの寿命で持ち続けます。新しい host は Kestrel が待ち受けを始めるまで、配備の回復、掃除、状態の発行を始めません。設定の変更のあとに起動できなければ、`config.json` がまだその変更の書いたものであるときに限り、前の内容に戻します。
- `POST /api/v1/clients/close`: Bridge が起動して記録した VRChat だけを閉じます。Reload で起動した台も v0.8.0 から `reload-clients.json` に記録します。
- `POST /api/v1/clients/{slot}/close` と `POST /api/v1/clients/{slot}/launch`（v0.10.0、Relaunch だけ）: 1台だけを閉じる、その台を起動し直す。slot は Relaunch の起動の順番（`managed-clients.json` の `launchOrder`）で、観測の slot と同じです。要求から読むのは slot の番号だけです。
  - close は、その順番の記録と4点（PID、作成時刻、session、名前）で一致するプロセスだけを、同じハンドルで閉じて終了を確かめ、記録からその1件だけを消します。観測の slot が別の台を指しているあいだ（起動し直した直後など）は `slotMismatch` で断ります。
  - launch は、Bridge が今の host で最後に配置したときにその slot を起動した引数（ワールド、roomId、profile、VR の有無）だけで起動します。これはメモリにだけ持ち、配置の始めに消え、host が作り直されれば無くなります（`noLaunchPlan`）。起動して記録できた slot だけが対象です。起動の前に記録を書き直して書けることを確かめ、一括起動と同じ間隔を空け、待ったあとにもう一度プロセスを確かめます。起動したあとは要求が切れても記録まで済ませます。記録できなければ 500 `launchedNotRecorded`（PID つき）で、推測で閉じません。
  - 断るときは何もせず、`{error, problems}` を返します（slot が 0〜3 の外は 400 `unknownSlot`、ほかは 409: `notRelaunchMode`、`recordUnreadable`、`recordCorrupt`、`notLaunchedByBridge`、`cannotCheck`、`notRunning`、`slotMismatch`、`noLaunchPlan`、`worldMissing`、`alreadyRunning`、`othersRunning`、`recordUnwritable`、`launchFailed`）。close の結果は `{slot, processId, closed, notConfirmed, recordUpdated, problems}` で、閉じて記録も直したときだけ 200、そうでなければ同じ形で 409。launch の結果は `{slot, profile, processId, clientGeneration}`。
  - 単独で起動した台のログは、Restart Manager がその台だけが開いていると答えたファイルにだけ付け、名前の時刻では照合しません（閉じた台の遅いログや、ほかの台のログと取り違えないため）。この印（PID と作成時刻）は host の寿命のあいだ残り、配置が断られても消えません。host が作り直されると、その台はほかの台と同じ照合に戻ります（Restart Manager が答えれば持ち主が先に決まるので、名前の時刻に進むのは答えが無いときだけです）。起動してから記録するまでのあいだ、観測は記録の無い新しい台に slot を与えません。記録のある台は、その起動の順番の slot が確かめられないあいだ、別の slot に置かず待ちます。
- `GET /api/v1/profiles`: Relaunch の profile ごとに、最後にログインしたアカウント。`/clients` の各台にも `loginState` と `account` が付きます。usr_ の ID は出さず、Windows の外に出ない鍵による HMAC の先頭12文字（`idDigest`）で、同じアカウントかだけを比べます。
- 配備の結果には任意の `launch`（VRChat をどう起動したか）と `warnings` が付きます。413 には何が大きすぎたかと上限を JSON で返します。protocol の版は上げません。

## 配備の結果と runner（v0.11.0）

Bridge は変えず、Mac の側だけで足します。計画は `docs/plans/r1-automation.md`（R1a）。

- 配備の結果: Unity は試み1回ごとに `~/Library/Application Support/VRCRemoteTest/deploy-results/<buildId>.json`（buildId の前に止まれば `attempt-<attemptId>.json`）を書きます（schema `vrcrt.deploy-result` v1、フォルダー 0700・ファイル 0600、`link(2)` で置き、終わった結果は上書きしない）。「配備できた」（deploy）と「起動できた」（launch）を分けて書き、準備と試験の合否は書きません。Bridge は一部だけの起動を `failed`＋`partial` で返すので、`deployed` に `partial`・`failed`・`notReached` が付く組、数の食い違い、launch の欠け、成果物と違う sha256 や buildId は UNKNOWN です。commit の前に断られたものは FAIL、送ったあとに答えが無いものは UNKNOWN、取り消しは CANCELED（送ったあとなら撤回ではない）。
- 公開の入口 `RemoteBuildApi` は共有の coordinator を通します。`RemoteBuildRequest` で run のフォルダーと tag を渡し、SDK のビルド中だけ `ActiveWorldBuildTag` が tag を示します。ワールドが `vrcrt.build=<tag>` を目印に出せば、runner がこの回のビルドかを照らせます。照らすのは、各台の今の参加の Joining の行（`/clients` の `joinAttemptSeq`）より後の目印です。VRChat はワールドの `Start()` の目印を「Finished entering world」（`joinedSeq`）より前に出すためです。`joinAttemptSeq` を出さない Bridge では `joinedSeq` より後だけを見ます。tag は成果物の bytes に付き、Deploy Last Build で bytes が変わっていれば外れます。
- runner（`package/Tools~/vrcrt_runner.py`、Python 3.9 以上・標準ライブラリだけ）は `vrcrt.sh` を子として呼ぶだけで、HTTP を自分では持ちません。prepare が配備の前のカーソルと Bridge の識別を `runs/<runId>/run.json` に残し、continue が配備の結果を読んで、明示した全 slot の準備（Relaunch は新しい世代、Reload は同じ世代・epoch でカーソルの後の join。ログを読み終え、同じインスタンスに joined、エラーワールドでない、ログイン済み）と tag を待ち、slot・世代・epoch ごとに目印と全文ログの snapshot を集めます。結果は PASS・FAIL・UNKNOWN・CANCELED で、観測の欠けは成功に数えず、試験の合否は判定しません。目印は観測と待ちにだけ使い、対象・保存先・次の手順を決めません。目印による一致は、ワールドが目印を偽らない前提です。
- 同じ Mac で、同じ run の根を使い、同じ Bridge の URL と証明書の指紋を使う runner のあいだでは、同時に走る run は1つだけです（`locks/` の claim を flock で直列にし、token で照らし、持ち主が消えるか期限を過ぎ、記録した子がすべて止まったと確かめられたときだけ引き継ぐ）。子は自分のプロセスグループで動かし、取り消し・期限・出力の大きさを見張り、どう終わっても止めて回収します。別の Mac、別の run の根、古い CLI からの配備は、R1a では見分けられません。
- Unity のウィンドウの1台ずつの Close／Launch は、v0.10.0 の経路を本文なしで呼びます。Bridge は要求が届いた時点で記録にある台に働きかけるので、一覧を読んだあとの変化は止められません（R1b で前提条件を足す）。

配備、設定の変更、閉じる操作（全台と1台）、1台の起動、起動し直しは、1つの操作の門を通り、同時には1つだけです。門は、host が起動し終えるまで閉じています。起動し直しを予約してから新しい host が立つまでは、新しい commit も 409 で断ります。管理の操作の最中に受けた commit は待ち行列に残り、門が空いてから配備します。

## 安全の範囲

- 任意のコマンドの実行、自由なパスの受け付け、待ち受けのアドレス・ポート・状態のフォルダー・Worlds のフォルダー・ペアリングの変更は、API に含めません。`VrchatExecutable` は、Steam のライブラリで見つけた `VRChat.exe` の候補からしか選べません。
- Bridge の自己更新は入れません。Bridge は自分の実行体を取得も入れ替えもせず、`.vrcw` 以外を実行しない決まりを保ちます。版の入れ替えは Windows で人が行います。
- Bridge が起動していない VRChat は、今までどおり閉じません。mode の切り替えは、記録の無い VRChat が動いていれば行いません（以前の「全部手で閉じてから切り替える」を置き換えます）。
- トークンを持つ人が新しくできること: Bridge の記録にある VRChat を閉じ、配備で起動する。起動し直しで Bridge を短く止める（再起動は10秒に1回まで）。アップロードの上限を 2 GiB まで上げる。`RetainBuilds` を下げて古いワールドを消させる（消したものは設定を戻しても戻りません）。ログの印の範囲を変える。HMD を使う台を変える。表示名と、伏せたパスを読む。v0.10.0 から: Relaunch で、記録にある1台だけを閉じ、その台を最後の配置と同じ引数で起動し直す。v0.11.0 は Bridge を変えないので、増えることはありません（Mac の側の runner が、すでにできる操作を順に呼ぶだけです）。v0.12.0 から: `bridgeInstanceId`・`operationRevision`・今の起動の計画の build ID を読める。1台の close／launch に前提条件を付けられる（前提は、今の close／launch でできることを狭めるだけで広げない）。Bridge は自分の24時間を過ぎた書き出しを消す（利用者が取り出す前に消えうる）。トークンが漏れれば、その持ち主はこれらをすべて行えます。前提条件は競合を防ぐためのもので、認証の代わりではありません。

## 検証の境界

Bridge の認証、容量・ハッシュ・重複送信、中断、再起動、ログの世代照合は自動テストで確認します。Unity 2022.3.22f1 のコンパイルと Editor テストを行います。Windows 実機の Build & Test、結果、複数台の状態・ログ、全文保存、CLI 観測は別の実機記録に残し、模擬試験と混同しません。セットアップは `docs/setup-windows.md` を参照してください。
