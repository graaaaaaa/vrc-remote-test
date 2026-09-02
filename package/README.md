# VRC Remote Test — Unity Package

macOS上のUnityでビルドしたVRChat Worldを、Windows上のVRChat Remote Test Bridgeへ自動転送してローカルテストするためのEditor拡張です。

## セットアップ

1. Windows側で VRC Remote Test Bridge を起動しておく（`bridge/` を参照）。
2. Windows側のステージングディレクトリ（`C:\VRCRemoteTest`）をSMB共有し、Mac側からマウントする（例: `/Volumes/VRCRemoteTest`）。
3. Unity上で `VRChat SDK > Remote Build` を実行するか、共有パスを設定した上でheadless実行する。

## 設定

`SharePath` はEditorPrefs、環境変数 `VRC_REMOTE_TEST_SHARE_PATH`、CLIフラグ `-vrcRemoteTestSharePath` のいずれかで指定できます（優先順位: CLIフラグ > 環境変数 > EditorPrefs）。

## Headless / CI実行

```
Unity.exe -batchmode -quit -projectPath <path> -executeMethod VRCRemoteTest.RemoteBuildCommand.ExecuteRemoteBuildHeadless -vrcRemoteTestSharePath /Volumes/VRCRemoteTest
```

終了コードはビルド成否を反映します（成功: 0、失敗: 1）。
