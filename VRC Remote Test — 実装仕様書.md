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
- `GET /api/v1/profiles`: Relaunch の profile ごとに、最後にログインしたアカウント。`/clients` の各台にも `loginState` と `account` が付きます。usr_ の ID は出さず、Windows の外に出ない鍵による HMAC の先頭12文字（`idDigest`）で、同じアカウントかだけを比べます。
- 配備の結果には任意の `launch`（VRChat をどう起動したか）と `warnings` が付きます。413 には何が大きすぎたかと上限を JSON で返します。protocol の版は上げません。

配備、設定の変更、閉じる操作、起動し直しは、1つの操作の門を通り、同時には1つだけです。門は、host が起動し終えるまで閉じています。起動し直しを予約してから新しい host が立つまでは、新しい commit も 409 で断ります。管理の操作の最中に受けた commit は待ち行列に残り、門が空いてから配備します。

## 安全の範囲

- 任意のコマンドの実行、自由なパスの受け付け、待ち受けのアドレス・ポート・状態のフォルダー・Worlds のフォルダー・ペアリングの変更は、API に含めません。`VrchatExecutable` は、Steam のライブラリで見つけた `VRChat.exe` の候補からしか選べません。
- Bridge の自己更新は入れません。Bridge は自分の実行体を取得も入れ替えもせず、`.vrcw` 以外を実行しない決まりを保ちます。版の入れ替えは Windows で人が行います。
- Bridge が起動していない VRChat は、今までどおり閉じません。mode の切り替えは、記録の無い VRChat が動いていれば行いません（以前の「全部手で閉じてから切り替える」を置き換えます）。
- トークンを持つ人が新しくできること: Bridge の記録にある VRChat を閉じ、配備で起動する。起動し直しで Bridge を短く止める（再起動は10秒に1回まで）。アップロードの上限を 2 GiB まで上げる。`RetainBuilds` を下げて古いワールドを消させる（消したものは設定を戻しても戻りません）。ログの印の範囲を変える。HMD を使う台を変える。表示名と、伏せたパスを読む。

## 検証の境界

Bridge の認証、容量・ハッシュ・重複送信、中断、再起動、ログの世代照合は自動テストで確認します。Unity 2022.3.22f1 のコンパイルと Editor テストを行います。Windows 実機の Build & Test、結果、複数台の状態・ログ、全文保存、CLI 観測は別の実機記録に残し、模擬試験と混同しません。セットアップは `docs/setup-windows.md` を参照してください。
