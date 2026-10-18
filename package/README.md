# VRC Remote Test — Unity Package

macOS上のUnityでビルドしたVRChat Worldを、Windows上のVRChat Remote Test Bridgeへ自動転送してローカルテストするためのEditor拡張です。

## セットアップ

1. Windows側で VRC Remote Test Bridge を起動しておく（Windows側の初期セットアップは[docs/setup-windows.md](https://github.com/graaaaaaa/vrc-remote-test/blob/main/docs/setup-windows.md)を参照。このファイルはVPMパッケージには含まれないため、リポジトリ上のリンク先を参照すること）。
2. Windows側のステージングディレクトリ（`C:\VRCRemoteTest`）をSMB共有し、Mac側からマウントする（例: `/Volumes/VRCRemoteTest`）。
3. Unity上で `VRChat SDK > VRC Remote Test` からウィンドウを開き、`Share Path` にマウントしたパスを設定する。

## ウィンドウの機能

`VRChat SDK > VRC Remote Test` で開くウィンドウには以下の機能があります。

- **Preflight**: 共有への到達性・VRChat SDKの利用可否・VRChatの起動状態（`--watch-worlds`付きかどうか）を常時表示
- **Remote Build & Test**: アクティブなビルドターゲット（`StandaloneWindows64`）でVRChat SDKビルドを実行し、Windows側へ転送・検証・配置。VRChatが`--watch-worlds`付きで起動していれば自動でリロードされる
- **Deploy Last Build**: 直前のビルド成果物を再配置する（同一Editorセッション内のみ有効）
- **Last Deployment**: 直前のデプロイ結果。失敗時はエラーコードに加え、具体的な対処方法も表示される
- **VRChat Log**: Windows側VRChatの最新ログ（直近200行）をカテゴリフィルタ（All/Error/Exception/Udon/Shader/Warning）付きで表示。新しい順に並び、Auto Refresh対応。Bridge v0.6.0 以降では次も使える（Bridge が古ければ今までどおり1台目のログだけを表示する）
  - **Log / Markers**: `Log` は台のログ、`Markers` は `LogMarkerPrefixes`（Windows の config.json、既定 `[VRCRT]`）で始まるマーカー行。マーカー行はその台のセッションの最初から集めたもので、200行から流れ落ちない。行は Bridge がエスケープした形（タブは `\t` など）で表示する
  - **台の切り替え**: `Latest`（今までと同じ1台目のログ）か `Slot 0`〜`Slot 3`。Markers では `All slots` で全台のマーカー行を時刻の順にまとめて表示する
  - 台を選ぶと、その台の状態（PID、joined などの状態、インスタンス、人数、Master か、VRChat の Logging の設定）と注意（Logging を Full にする、ログインが切れている可能性、など）を表示する。Bridge の台の一覧が15秒より古ければ警告する
  - 続きの行（複数行のメッセージの2行目以降）は先頭に空白4つが付いている
- **Open Moonlight**: macOS側にインストール済みのMoonlightアプリを起動・前面表示（Moonlightでリモートデスクトップ接続している場合）
- **Settings**: 共有パス、タイムアウト・ポーリング間隔、Log Viewerの自動更新、Moonlightアプリ名、デプロイ成功後の自動フォーカス設定

## 設定

`SharePath` はEditorPrefs、環境変数 `VRC_REMOTE_TEST_SHARE_PATH`、CLIフラグ `-vrcRemoteTestSharePath` のいずれかで指定できます（優先順位: CLIフラグ > 環境変数 > EditorPrefs）。

## Headless / CI実行

```
Unity.exe -batchmode -quit -projectPath <path> -executeMethod VRCRemoteTest.RemoteBuildCommand.ExecuteRemoteBuildHeadless -vrcRemoteTestSharePath /Volumes/VRCRemoteTest
```

終了コードはビルド成否を反映します（成功: 0、失敗: 1）。
