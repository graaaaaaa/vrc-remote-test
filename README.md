# VRC Remote Test

macOS上のUnityでVRChat Worldを開発し、Windows上の実VRChatクライアントへローカルWorld Buildを自動転送してテストするための開発ツールです。

Windowsマシンを「Unity開発機」ではなく「VRChat実機テストターゲット」として扱います。

```
Mac / Unity
[ Remote Build & Test ]
      │
      ├─ VRChat SDKでWindows向け.vrcwをBuild
      ├─ SHA-256計算
      ├─ Windowsへ転送 (SMB)
      ├─ Windows Bridgeが検証・配置
      └─ VRChat --watch-worlds がreload
```

## インストール

ALCOMで以下のリポジトリリスティングURLを追加してください（`Settings > Packages > Add Repository`）:

```
https://raw.githubusercontent.com/graaaaaaa/vrc-remote-test/main/index.json
```

追加後、`VRC Remote Test`パッケージがVCC/ALCOMのパッケージ一覧に表示されます。Windows側の初期セットアップ（Bridgeの配置、SMB共有、VRChat起動設定）は [`docs/setup-windows.md`](./docs/setup-windows.md) を参照してください。

## 使い方

セットアップ完了後、Unity上で `VRChat SDK > VRC Remote Test` からウィンドウを開きます。

- **Remote Build & Test**: WindowsビルドしてWindows実機へ転送・配置。VRChatが`--watch-worlds`付きで起動していれば自動でリロードされる
- **Deploy Last Build**: 直前のビルドを再配置（コード変更なしでVRChat側だけ再テストしたい場合）
- **Preflight**: 共有・SDK・VRChatの状態を常時表示
- **VRChat Log**: WindowsのVRChatログをリアルタイム表示・カテゴリフィルタ
- **Moonlight連携**: リモートデスクトップでWindows画面を見ている場合、ボタン一つでMoonlightを前面表示

詳しい機能一覧は [`package/README.md`](./package/README.md) を参照してください。

## 構成

- `package/` — Unity Editor用VPMパッケージ (`com.github.graaaaaaa.vrc-remote-test`)
- `bridge/` — Windows Bridge（.NET 10、Windows側で受信・検証・配置を行うコンソールアプリ）
- `scripts/` — Windows側セットアップ/起動用PowerShellスクリプト
- `docs/` — セットアップ手順（ユーザー向け）と開発資料（後述）

## 開発者向け情報

このツール自体の開発・改修に関わる場合は以下を参照してください（利用のみであれば読む必要はありません）。

- `VRC Remote Test — 実装仕様書.md`（リポジトリルート）— 設計仕様書
- [`docs/sdk-api-notes.md`](./docs/sdk-api-notes.md) — VRChat SDK Public APIの調査ノート
- [`docs/validation/watch-worlds-spike.md`](./docs/validation/watch-worlds-spike.md) — `--watch-worlds`挙動の実機検証記録
- `bridge/README.md`・`package/CHANGELOG.md` — 各コンポーネントのビルド方法・変更履歴

## ライセンス

[LICENSE](./LICENSE) を参照してください。
