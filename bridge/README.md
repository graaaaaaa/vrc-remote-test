# VRC Remote Test Bridge

Windows側で動作する.NET 10 (LTS) コンソールアプリ。SMB共有のステージング領域を監視し、`.vrcw`ビルドを検証してVRChat Worldsディレクトリへ配置します。

VRChat自体（Unity/Mono/IL2CPPベースの独立プロセス）とはランタイム上の依存関係が一切ないため、.NETのバージョンはVRChat側の制約を受けません。開発機の環境に合わせて.NET 10 LTSを採用しています。

Windows Serviceではなく、インタラクティブユーザーセッションで非elevated実行します（将来的なVRChat起動/監視機能のため）。

## 構成

```
bridge/
├── VRCRemoteTest.Bridge.sln
├── src/VRCRemoteTest.Bridge/       — 本体 (net10.0, win-x64 self-contained single-file)
│   ├── Program.cs                  — エントリポイント、Generic Host組み立て
│   ├── Configuration/              — config.json読み込み・検証
│   ├── Protocol/                   — BuildManifest / BuildResult / VrchatStatus (JSON wire format)
│   ├── Deployment/                 — 検証・配置・クリーンアップ・監視ループ
│   └── VRChat/                     — VRChatプロセス監視 (Phase 4a) + 自動起動 (Phase 4.1)
└── tests/VRCRemoteTest.Bridge.Tests/ — xUnit
    └── fixtures/                   — golden JSONフィクスチャ (Unity側と共有)
```

## v1スコープ（最小構成）

Codexレビューを経て、Bridgeは「フルオーケストレーションサービス」ではなく「ファイル検証・配置に特化したプロモーター」として設計しています。

- **heartbeatなし**: ビルドごとに1つの結果ファイル (`results/{buildId}.json`) のみ
- **VRChatプロセス監視**: `VrchatMonitorService`が10秒間隔でVRChatプロセスと`--watch-worlds`引数の有無を`status/vrchat-status.json`へ書き出す（Phase 4a）。Unity側のpreflight表示専用で、ビルドの成否には一切影響しない
- **VRChat自動起動（`AutoLaunchVrchat`、デフォルトOFF）**: 有効時、VRChat未起動ならBridgeが`--watch-worlds`付きで自動起動し、準備が整うまで待ってからdeployする（Phase 4.1）。既に`--watch-worlds`無しで起動中のVRChatは勝手に再起動しない。準備確認は仕様書§31の「新しいoutput_logファイル出現」ではなく、`VrchatMonitorService`と同じWMIベースのプロセス監視シグナルの安定確認＋起動時刻からの最低待機時間（`StartupSettleDelay`）を組み合わせた方式を採用している（意図的な仕様逸脱、詳細は`VrchatReadinessCoordinator.cs`のコメント参照）
- **認証はSMB ACLのみ**: HMAC署名はv1.1で追加予定

## ビルド・テスト方法

macOS開発機で `dotnet build` / `dotnet test` を実行して検証済みです（.NET 10 SDK, `brew install --cask dotnet-sdk`）。

```powershell
# リストア・ビルド
dotnet restore VRCRemoteTest.Bridge.sln
dotnet build VRCRemoteTest.Bridge.sln

# テスト実行
dotnet test VRCRemoteTest.Bridge.Tests/VRCRemoteTest.Bridge.Tests.csproj

# 単一ファイル発行 (Windows実機用)
# RuntimeIdentifier/SelfContained/PublishSingleFileはcsprojに恒久指定せず、
# publish時のみコマンドラインで指定する (通常のbuild/testがmacOS等でも動くようにするため)
dotnet publish src/VRCRemoteTest.Bridge/VRCRemoteTest.Bridge.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## 設定ファイル

`%LOCALAPPDATA%\VRCRemoteTest\config.json`（存在しない場合はデフォルト値で起動を試みるが、`VrchatWorldsDirectory`は必須）:

```json
{
  "Bridge": {
    "StagingDirectory": "C:\\VRCRemoteTest",
    "VrchatWorldsDirectory": "C:\\Users\\USER\\AppData\\LocalLow\\VRChat\\VRChat\\Worlds",
    "MaxArtifactSizeBytes": 524288000,
    "RetainBuilds": 10,
    "VrchatExecutable": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\VRChat\\VRChat.exe",
    "VrchatMode": "Desktop",
    "AutoLaunchVrchat": false,
    "VrchatStartupTimeoutSeconds": 60
  }
}
```

`VrchatWorldsDirectory`が実在しない場合、Bridgeは起動を拒否します（勝手に別の場所を推測しません）。`VrchatExecutable`/`VrchatMode`/`AutoLaunchVrchat`/`VrchatStartupTimeoutSeconds`はPhase 4.1（VRChat自動起動）用の設定で、`AutoLaunchVrchat: true`の場合のみ`VrchatExecutable`が検証される（絶対パス・非UNC・`.exe`拡張子・ファイル名が`VRChat.exe`と完全一致・実在、の全てを満たす必要がある）。`VrchatStartupTimeoutSeconds`は45秒未満に設定できない（`VrchatReadinessCoordinator`の`StartupSettleDelay`15秒+安定確認ポーリング分+実起動時間のばらつきに対するマージンとして必要）。

## ステージングディレクトリ構造

`StagingDirectory`配下に以下が自動生成されます:

```
{StagingDirectory}/
├── incoming/     — Unity側からのアップロード先 (.vrcw + .ready.json)
├── processing/   — 処理中（claim済み）
├── archive/      — 処理完了（冪等性判定に使用）
├── failed/       — 検証失敗・quarantine
├── results/      — ビルドごとの結果ファイル (Unity側がポーリング)
└── status/       — VrchatMonitorServiceが10秒間隔で上書きするvrchat-status.json (Phase 4a)
```

## 手動テスト（Unity不要）

Phase 1時点ではUnity実装がまだ存在しないため、`incoming/`へ手動でファイルを配置してBridgeの動作を確認できます。`tests/VRCRemoteTest.Bridge.Tests/fixtures/sample-manifest.json`を参考にmanifestを作成し、対応する`.vrcw`（ダミーファイルで可、サイズとSHA-256を一致させること）を`incoming/{buildId}.vrcw`として配置後、`incoming/{buildId}.ready.json`を書き込むと処理が始まります。
