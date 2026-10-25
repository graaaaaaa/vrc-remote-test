# VRC Remote Test — Unity Package

macOS Unity 2022.3 から Windows Bridge へ認証付き HTTPS でワールドを送る Editor 拡張です。Bridge とパッケージは同時更新が必要です。

1. [Windows セットアップ](https://github.com/graaaaaaa/vrc-remote-test/blob/main/docs/setup-windows.md)に従い、Bridge を起動して `pair` の3値を取得します。
2. `VRChat SDK > VRC Remote Test > Settings` に Bridge URL、証明書 SHA-256、ペアリングトークンを入力して保存します。トークンは macOS Keychain に保存されます。
3. Preflight の接続表示を確認して `Remote Build & Test` を実行します。

ウィンドウでは結果、台別の状態、ログ抜粋とマーカーを表示します。台を選んで `Save Full Log` を押すと、その台・セッションに紐付いたログを要求時点まで保存します。ログを確認できないときは保存を失敗として表示します。

Headless/CI では `VRC_REMOTE_TEST_BRIDGE_URL`、`VRC_REMOTE_TEST_CERT_SHA256`、`VRC_REMOTE_TEST_TOKEN` を設定し、`-batchmode -buildTarget Win64 -projectPath <path> -executeMethod VRCRemoteTest.RemoteBuildCommand.ExecuteRemoteBuildHeadless` を実行します。保存済みシーンを指定するときは `VRC_REMOTE_TEST_SCENE_PATH=Assets/Scenes/YourWorld.unity` を設定します。`-quit` は付けません。非同期 SDK ビルド完了時に拡張が Unity を終了し、終了コードは成功0、失敗1です。対話エディターのタイムアウト等はユーザーごとの EditorPrefs に保存します。
