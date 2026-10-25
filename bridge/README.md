# VRC Remote Test Bridge

Windows の通常ユーザーの対話セッションで動く .NET 10 アプリです。指定した LAN IP の認証付き HTTPS で Unity からビルドを受け取り、サイズ・名前・SHA-256 を検査して VRChat Worlds へ配置します。状態・台別ログ・マーカー・明示要求の全文ログも同じ API で返します。

`dotnet test bridge/tests/VRCRemoteTest.Bridge.Tests/VRCRemoteTest.Bridge.Tests.csproj` でテストします。Windows 用単一実行体は `dotnet publish bridge/src/VRCRemoteTest.Bridge/VRCRemoteTest.Bridge.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true` で作成します。

`%LOCALAPPDATA%\VRCRemoteTest\config.json` の例:

```json
{
  "Bridge": {
    "LanBindAddress": "192.0.2.20",
    "LanPort": 38443,
    "VrchatWorldsDirectory": "C:\\Users\\USER\\AppData\\LocalLow\\VRChat\\VRChat\\Worlds",
    "MaxArtifactSizeBytes": 524288000,
    "AutoLaunchVrchat": false,
    "VrchatClientMode": "Reload",
    "LogMarkerPrefixes": ["[VRCRT]"]
  }
}
```

`LanBindAddress` はループバックでも全インターフェースでもない、Windows の具体的な LAN IP にします。Bridge の状態は `%LOCALAPPDATA%\VRCRemoteTest\state` に固定されます。`Bridge.exe pair` が接続 URL、証明書指紋、トークンを表示します。インストール、ペアリング、ファイアウォール、旧環境撤去は [`docs/setup-windows.md`](../docs/setup-windows.md) を参照してください。旧版の詳しい設計・実機記録は `docs/validation/history` に保存しています。
