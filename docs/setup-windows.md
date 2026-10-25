# Windows Bridge セットアップ（LAN Direct）

Bridge と Unity パッケージは同じ版へ更新してください。旧共有への接続経路はありません。Bridge は Windows の通常ユーザーの対話セッションで動かし、設定と受信ファイルは `%LOCALAPPDATA%\VRCRemoteTest` に保存します。

## 1. Bridge を配置する

リリースの `VRCRemoteTest.Bridge.exe` を Windows 上で入手し、公開された SHA-256 と照合してください。PowerShell 7 で `scripts/install-bridge.ps1 -ExpectedSha256 <SHA-256> -SourcePath <ダウンロードしたexe> -StartNow` を実行します。既存 Bridge は停止してから更新します。このスクリプトは実行体を `%LOCALAPPDATA%\Programs\VRCRemoteTest\Bridge` に置き、ログオン時のタスクを登録します。

`%LOCALAPPDATA%\VRCRemoteTest\config.json` に次の `Bridge` セクションを用意します。`LanBindAddress` は Windows の開発用 LAN インターフェースの固定 IP です。`VrchatWorldsDirectory` は実在するローカル Worlds ディレクトリを指定します。その他の VRChat 設定は `bridge/README.md` を参照してください。

```json
{
  "Bridge": {
    "LanBindAddress": "192.0.2.20",
    "LanPort": 38443,
    "VrchatWorldsDirectory": "C:\\Users\\USER\\AppData\\LocalLow\\VRChat\\VRChat\\Worlds",
    "AutoLaunchVrchat": false
  }
}
```

この例の IP とユーザー名は置き換えてください。Bridge は指定 IP の HTTPS だけで待ち受けます。ファイアウォールでは TCP `LanPort` の接続元を開発機の IP に限定してください。例（管理者 PowerShell、両 IP は実値に置換）:

```powershell
New-NetFirewallRule -DisplayName 'VRC Remote Test LAN Direct' -Direction Inbound -Action Allow -Protocol TCP -LocalAddress 192.0.2.20 -LocalPort 38443 -RemoteAddress 192.0.2.10 -Profile Private
```

Windows が初回起動時に Bridge 実行体向けの受信許可を自動生成した場合、その規則に接続元やポートの制限がないことがあります。限定規則の追加後に `Get-NetFirewallRule -DisplayName vrcremotetest.bridge.exe` で確認し、自動生成された広い許可が有効なら管理者 PowerShell で `Disable-NetFirewallRule -DisplayName vrcremotetest.bridge.exe` を実行してください。限定規則は無効にせず、開発機からの接続を再確認します。

## 2. ペアリング

Windows の Bridge 実行体を `pair` 引数付きで起動すると、接続 URL、証明書 SHA-256、トークンが表示されます。トークンは第三者に渡さないでください。Mac の Unity で `VRChat SDK > VRC Remote Test > Settings` に3値を入力し、`Save Pairing` を押します。証明書指紋を照合するため、初回の値は Windows の画面から確認してください。トークンは macOS Keychain に保存され、Unity プロジェクトには保存されません。Windows のファイアウォール規則はペアリング後も必要です。

ターミナルで状態・マーカーを読む場合は `VRCRT_BRIDGE_URL` と `VRCRT_CERT_SHA256` を設定して `scripts/vrcrt.sh status` を使います。トークンは同じ Keychain 項目から読みます。CI では `VRC_REMOTE_TEST_BRIDGE_URL`、`VRC_REMOTE_TEST_CERT_SHA256`、`VRC_REMOTE_TEST_TOKEN`（CLI は `VRCRT_*`）を秘密設定から渡してください。

## 3. 動作確認と旧環境の撤去

Unity の Preflight が `Windows Bridge: Connected` になり、`Remote Build & Test` の結果、台別状態、ログ、`Save Full Log`、`vrcrt.sh status` を確認します。Bridge のプライベート `state` 内に `incoming`、`results`、`status`、`logs` が作られます。

新接続を確認した後、旧 `VRCRemoteTest` 共有、SMB 専用アカウント、SMB 向けファイアウォール規則、Mac の自動マウントを管理者が手動で撤去します。旧共有内のビルドやログで保管が必要なものは、撤去前に別の保管先へ移してください。旧設定の `StagingDirectory` は削除します。以前の実機記録は `docs/validation` に履歴として残します。
