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

## 構成

- `package/` — Unity Editor用VPMパッケージ (`com.github.graaaaaaa.vrc-remote-test`)
- `bridge/` — Windows Bridge (.NET 10、Windows側で受信・検証・配置を行うコンソールアプリ)
- `scripts/` — Windows側セットアップ/起動用PowerShellスクリプト
- `docs/` — セットアップ手順・SDK API調査ノート

## インストール

ALCOMで以下のリポジトリリスティングURLを追加してください（`Settings > Packages > Add Repository`）:

```
https://raw.githubusercontent.com/graaaaaaa/vrc-remote-test/main/index.json
```

追加後、`VRC Remote Test`パッケージがVCC/ALCOMのパッケージ一覧に表示されます。Windows側の初期セットアップ（Bridgeの配置、SMB共有、VRChat起動設定）は `docs/setup-windows.md` を参照してください。

## ステータス

実装中。詳細は各コンポーネントのREADMEおよび `docs/` を参照してください。

## ライセンス

[LICENSE](./LICENSE) を参照してください。
