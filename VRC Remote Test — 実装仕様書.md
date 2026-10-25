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

## 検証の境界

Bridge の認証、容量・ハッシュ・重複送信、中断、再起動、ログの世代照合は自動テストで確認します。Unity 2022.3.22f1 のコンパイルと Editor テストを行います。Windows 実機の Build & Test、結果、複数台の状態・ログ、全文保存、CLI 観測は別の実機記録に残し、模擬試験と混同しません。セットアップは `docs/setup-windows.md` を参照してください。
