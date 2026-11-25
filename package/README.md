# VRC Remote Test — Unity Package

macOS Unity 2022.3 から Windows Bridge へ認証付き HTTPS でワールドを送る Editor 拡張です。Bridge とパッケージは同時更新が必要です。

1. [Windows セットアップ](https://github.com/graaaaaaa/vrc-remote-test/blob/main/docs/setup-windows.md)に従い、Bridge を起動して `pair` の3値を取得します。
2. `VRChat SDK > VRC Remote Test > Settings` に Bridge URL、証明書 SHA-256、ペアリングトークンを入力して保存します。トークンは macOS Keychain に保存されます。
3. Preflight の接続表示を確認して `Remote Build & Test` を実行します。

ウィンドウでは結果、台別の状態、ログ抜粋とマーカーを表示します。Bridge 0.8.0 以降なら、Bridge の欄の「VRChat Clients」で、起動する VRChat の台数とアカウントを変えられます。Reload は同じアカウントで1〜4台、Relaunch は profile ごとに別のアカウントで、例えば `0, 1, 2, 3` なら4台です。`Apply to Bridge` を押すと、Bridge が設定を書き、起動し直して結果を返します。台を選んで `Save Full Log` を押すと、その台・セッションに紐付いたログを要求時点まで保存します。ログを確認できないときは保存を失敗として表示します。

パッケージ 0.11.0 からは、次のことも行えます。

- VRChat Clients の台の表から、1台ずつ `Close`／`Launch`（Bridge 0.10.0 以降、Relaunch）。Bridge は要求が届いた時点で記録にある台に働きかけます。
- 「Deploy and Collect」の `Run`: 同梱の runner（`Tools~/vrcrt_runner.py`、Python 3.9 以上）が配備の前の状態を保存し、この Editor がビルドして配り、runner が指定した全 slot の準備を待って、目印と全文ログを `~/Library/Application Support/VRCRemoteTest/runs/<runId>/` に集めます。試験の合否は判定しません。
- ビルドと再配置のたびに、構造化した結果を `~/Library/Application Support/VRCRemoteTest/deploy-results/` に保存します。自分のコードからは `RemoteBuildApi.BuildAndDeployAsync`／`DeployLastBuildAsync` で結果を受け取れます（`RemoteBuildOutcome.DeployResultPath`）。ビルドの前処理で `RemoteBuildApi.ActiveWorldBuildTag` をワールドに埋め、読み込み時に `vrcrt.build=<tag>` を目印に出すと、runner がこの回のビルドかを照らせます。

Headless/CI では `VRC_REMOTE_TEST_BRIDGE_URL`、`VRC_REMOTE_TEST_CERT_SHA256`、`VRC_REMOTE_TEST_TOKEN` を設定し、`-batchmode -buildTarget Win64 -projectPath <path> -executeMethod VRCRemoteTest.RemoteBuildCommand.ExecuteRemoteBuildHeadless` を実行します。保存済みシーンを指定するときは `VRC_REMOTE_TEST_SCENE_PATH=Assets/Scenes/YourWorld.unity` を設定します。`-quit` は付けません。非同期 SDK ビルド完了時に拡張が Unity を終了し、終了コードは成功0、失敗1です。runner から呼ぶときは `VRC_REMOTE_TEST_RUN_DIR`（run のフォルダー）と `VRC_REMOTE_TEST_WORLD_BUILD_TAG` も渡り、結果の写しが run のフォルダーに入ります。対話エディターのタイムアウト等はユーザーごとの EditorPrefs に保存します。
