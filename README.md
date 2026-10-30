# VRC Remote Test

macOS上のUnityでVRChat Worldを開発し、Windows上の実VRChatクライアントへローカルWorld Buildを自動転送してテストするための開発ツールです。

Windowsマシンを「Unity開発機」ではなく「VRChat実機テストターゲット」として扱います。

```
Mac / Unity
[ Remote Build & Test ]
      │
      ├─ VRChat SDKでWindows向け.vrcwをBuild
      ├─ SHA-256計算
      ├─ Windowsへ転送 (認証付き HTTPS)
      ├─ Windows Bridgeが検証・配置
      └─ VRChat --watch-worlds がreload
```

## インストール

ALCOMで以下のリポジトリリスティングURLを追加してください（`Settings > Packages > Add Repository`）:

```
https://raw.githubusercontent.com/graaaaaaa/vrc-remote-test/main/index.json
```

追加後、`VRC Remote Test`パッケージがVCC/ALCOMのパッケージ一覧に表示されます。Windows側の初期セットアップ（Bridgeの配置、LAN Directペアリング、VRChat起動設定）は [`docs/setup-windows.md`](./docs/setup-windows.md) を参照してください。

Windows Bridge の実行体（`VRCRemoteTest.Bridge.exe`）は、[Releases](https://github.com/graaaaaaa/vrc-remote-test/releases) に GitHub Actions でビルドされたものが、SHA-256 とビルドの来歴（artifact attestation）付きで添付されています。Windows 上で直接ダウンロードし、来歴を確認してからインストールしてください（[`docs/setup-windows.md`](./docs/setup-windows.md) の手順1-A）。

## 使い方

セットアップ完了後、Unity上で `VRChat SDK > VRC Remote Test` からウィンドウを開きます。

- **Remote Build & Test**: WindowsビルドしてWindows実機へ転送・配置。VRChatが`--watch-worlds`付きで起動していれば自動でリロードされる
- **Deploy Last Build**: 直前のビルドを再配置（コード変更なしでVRChat側だけ再テストしたい場合）
- **Preflight**: Bridge接続・SDK・VRChatの状態を常時表示
- **VRChat Log**: WindowsのVRChatログをリアルタイム表示・カテゴリフィルタ。台ごとのログと、ワールドが出すマーカー行（`[VRCRT]` などで始まる行）を台を切り替えて表示
- **Moonlight連携**: リモートデスクトップでWindows画面を見ている場合、ボタン一つでMoonlightを前面表示

詳しい機能一覧は [`package/README.md`](./package/README.md) を参照してください。

### Mac のスクリプト（`scripts/vrcrt.sh`）

エージェントやターミナルからは、認証付き HTTPS で台の状態とマーカー行を取得できます。接続先と証明書指紋は `VRCRT_BRIDGE_URL`、`VRCRT_CERT_SHA256`、トークンは macOS Keychain または `VRCRT_TOKEN` で設定します。

| コマンド | 内容 |
|---|---|
| `scripts/vrcrt.sh status [--json]` | 台の一覧（slot、PID、状態、インスタンス、人数、Master、Logging など）。15秒より古ければ警告 |
| `scripts/vrcrt.sh result <build-id>` | Bridge の配置結果を取得し、`deployed` 以外は失敗として返す |
| `scripts/vrcrt.sh log <slot> [--markers] [--lines N]` | 台のログかマーカー行。制御文字とタブはエスケープして出す |
| `scripts/vrcrt.sh log all --markers` | 全台のマーカー行を時刻と seq の順にまとめて出す（`slot<TAB>seq<TAB>行`） |
| `scripts/vrcrt.sh export-log all <保存先ディレクトリ>` | 稼働中の全台の全文ログを、世代・セッション照合と SHA-256 検査の後に保存。`all` の代わりに slot 番号も指定できる |
| `scripts/vrcrt.sh cursor` | 今の位置（台ごとの世代・observationEpoch・seq）を JSON で出す。読み終えていない台があれば失敗する |
| `scripts/vrcrt.sh wait-marker <slot\|all> <正規表現> <秒> [--after <cursor>] [--any-generation]` | カーソルより後の、同じ世代・同じ epoch のマーカー行が正規表現に一致するまで待つ。`all` はカーソルの時点で動いていた全台での一致を待つ |
| `scripts/vrcrt.sh wait-same-instance <秒> [--slots 0,1] [--after <cursor>]` | 指定した台がすべて joined で同じインスタンスにいるまで待つ。`--after` なら、カーソルより後に入り直したことも求める |
| `scripts/vrcrt.sh doctor` | Bridge 0.8.0 以降: 版と実行体の SHA-256、いま効いている設定、注意、最後の設定の変更の結果、profile ごとのアカウント、台ごとのログイン状態、Bridge のログの末尾 |
| `scripts/vrcrt.sh config get` | 変えられる設定の今の値と、`VrchatExecutable` の候補 |
| `scripts/vrcrt.sh config set KEY=VALUE…` | 設定を変え、Bridge が起動し直して効いたかを待つ（最大90秒）。`KEY=null` で項目を消す |
| `scripts/vrcrt.sh restart` | Bridge を起動し直し、新しい host が立つのを待つ |
| `scripts/vrcrt.sh close-clients` | Bridge が起動した VRChat を閉じる |
| `scripts/vrcrt.sh profiles` | profile ごとに、最後にログインしたアカウント |

待つ前に `cursor` で位置を取り、操作のあとに `--after` で渡すと、操作より前の行を新しい出来事と取り違えません。

```bash
cursor="$(scripts/vrcrt.sh cursor)"
```

```bash
scripts/vrcrt.sh wait-marker 1 'PLAYER_RESTORED' 60 --after "$cursor"
```

終了コードは 0 が成功、1 が時間切れ（管理のコマンドでは、Bridge が断った、設定の変更が戻された・失敗した、も含む）、2 が使い方の誤り（正規表現の誤りと、2秒を超えた正規表現を含む）、3 が観測では答えられないとき（Bridgeの観測情報が無いか古い、Bridge の再起動などで読み直した、Logging が Full でない、マーカーが上限で捨てられた、管理のコマンドでは Bridge に届かないか 0.8.0 より古い）。管理のコマンドの詳しい使い方は `docs/setup-windows.md` の4。`wait-marker` の正規表現は、Bridge がエスケープしたあとの行に当てます。同じインスタンスでも同じビルドとは限りません。動作の確認は `bash scripts/test-vrcrt.sh`。

ビルドから結果・全文ログまで一度に保存する場合は、対話用 Unity Editor で対象プロジェクトを閉じ、保存済みシーンを指定して実行します。保存先は新しいディレクトリにしてください。ペアリングトークンは Keychain から読みます。`-quit` は付けず、ビルド完了後に拡張が Unity を終了します。

```bash
export VRCRT_BRIDGE_URL=https://<Windows-LAN-IP>:38443
export VRCRT_CERT_SHA256=<pairで表示された証明書SHA-256>
python3 scripts/vrcrt-run.py \
  --unity /Applications/Unity/Hub/Editor/2022.3.22f1/Unity.app/Contents/MacOS/Unity \
  --project /path/to/UnityProject \
  --scene Assets/Scenes/YourWorld.unity \
  --output-dir /path/to/new-test-evidence
```

`vrcrt-run.py` は Windows64 ビルド、Bridge 配置結果の照合、台の状態、稼働中の各台の全文ログ保存を行います。`--watch-worlds` なしのクライアントは配置後に自動再読み込みしないため、その場合は警告を出します。ワールドの動作まで判定する場合は `--expect-marker <正規表現>` を指定し、ビルド前のカーソル以降に出たマーカーを待てます。

## 構成

- `package/` — Unity Editor用VPMパッケージ (`com.github.graaaaaaa.vrc-remote-test`)
- `bridge/` — Windows Bridge（.NET 10、Windows側で受信・検証・配置を行うコンソールアプリ）
- `scripts/` — Windows側セットアップ/起動用PowerShellスクリプトと、Mac で観測を読む `vrcrt.sh`
- `docs/` — セットアップ手順（ユーザー向け）と開発資料（後述）

## 開発者向け情報

このツール自体の開発・改修に関わる場合は以下を参照してください（利用のみであれば読む必要はありません）。

- `VRC Remote Test — 実装仕様書.md`（リポジトリルート）— 設計仕様書
- [`docs/sdk-api-notes.md`](./docs/sdk-api-notes.md) — VRChat SDK Public APIの調査ノート
- [`docs/validation/watch-worlds-spike.md`](./docs/validation/watch-worlds-spike.md) — `--watch-worlds`挙動の実機検証記録
- [`docs/validation/multi-client-spike.md`](./docs/validation/multi-client-spike.md) — 複数クライアント（Number of Clients相当）の実機検証手順と記録
- [`docs/validation/smb-late-empty-manifest.md`](./docs/validation/smb-late-empty-manifest.md) — 配置のあとに空の manifest が現れた件の調査と確認手順
- `bridge/README.md`・`package/CHANGELOG.md` — 各コンポーネントのビルド方法・変更履歴

## ライセンス

[LICENSE](./LICENSE) を参照してください。
