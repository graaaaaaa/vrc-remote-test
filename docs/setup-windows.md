# Windows Bridge セットアップ（LAN Direct）

Bridge と Unity パッケージは同じ版へ更新してください。旧共有への接続経路はありません。Bridge は Windows の通常ユーザーの対話セッションで動かし、設定と受信ファイルは `%LOCALAPPDATA%\VRCRemoteTest` に保存します。

## Windows で人が行うこと

v0.8.0 からは、日々の確認と設定の変更を Mac から行えます（4 を参照）。次のことだけは Windows の前で人が行います。

- 最初のインストールと、Bridge の版の入れ替え（1）。Bridge は自分の実行体を入れ替えません。
- ファイアウォールの規則（1、管理者の権限が要る）。
- ペアリング（2）。トークンを Windows の画面から読み取ります。
- VRChat の profile ごとの初めてのログインと、ログイン切れのときのログイン（パスワードと二段階認証）。Mac からは `vrcrt.sh doctor` で、ログインしていない profile が分かります。
- VRChat の Settings > Debug > Logging を Full にすること。
- PowerShell 7 のインストール。
- 電源を入れただけで Bridge を動かしたいときの、Windows の自動サインイン。Bridge のタスクはログオンのときに始まります。

## 1. Bridge を配置する

リリースの `VRCRemoteTest.Bridge.exe` を Windows 上で入手し、公開された SHA-256 と照合してください。自分で開いた PowerShell（5.1 でも 7 でもよい）から、次の形で PowerShell 7 の `pwsh.exe` に実行させます。このスクリプトは実行体を `%LOCALAPPDATA%\Programs\VRCRemoteTest\Bridge` に置き、ログオン時のタスクを登録します。

```powershell
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File <リポジトリ>\scripts\install-bridge.ps1 -ExpectedSha256 <SHA-256> -SourcePath <ダウンロードしたexe> -StartNow
```

- `& .\install-bridge.ps1` のように今の PowerShell の中で動かすと、Windows PowerShell 5.1 では実行ポリシー（既定は Restricted）で止まります。ダウンロードしたスクリプトは、PowerShell 7 の RemoteSigned でも止まることがあります。上の形なら、このコマンドに限って実行ポリシーを迂回します。
- 5.1 で起動したときは、何も変更せずに、上の形のコマンドを示して止まります。
- winget で入れた PowerShell 7 はストア版になり、`%LOCALAPPDATA%\Microsoft\WindowsApps\pwsh.exe` にあります。
- Windows PowerShell 5.1 は、BOM の無い UTF-8 の日本語を読み違えます。`install-bridge.ps1` は BOM 付きで保存してあります。5.1 で動かすスクリプトを足すときは、ASCII にするか BOM 付きで保存してください。

### 版を入れ替える

1. Bridge を止めます: `Stop-ScheduledTask -TaskName VRCRemoteTestBridge`。`Get-Process VRCRemoteTest.Bridge -ErrorAction SilentlyContinue` で何も出なければ止まっています。手で起動した Bridge は `Stop-Process -Id <PID>` で止めます。スクリプトは Bridge を止めません。入れ替えが要るのに Bridge が動いていれば、何も変えずに止まって、止め方とやり直すコマンドを示します。
2. 上のコマンドを `-StartNow` 付きで実行します。スクリプトは新しい実行体を SHA-256 で照合して置き換え、次にタスクを調べます。
   - 前の版で登録したタスクの定義（Action、Trigger、Principal、Settings、Description）が同じなら、削除も再登録もせずに残して起動します。
   - 定義が違えば、削除して登録し直します。削除できなければ（「アクセスが拒否されました」など）、違う箇所と直し方（タスク スケジューラ `taskschd.msc` で削除する、消せなければ管理者の PowerShell で `Unregister-ScheduledTask`）を示して止まります。
   - タスクの段階で止まったときは、実行体とタスクがどの状態で残ったかと、やり直すコマンドを表示します。実行体はもう照合済みなので、やり直しに `-SourcePath` は要りません。
3. Mac で `vrcrt.sh doctor` を実行し、Bridge の版と実行体の SHA-256 を確かめます。
- Claude デスクトップアプリのシェルから `%LOCALAPPDATA%` や `LocalLow` に書いたファイルは、アプリ専用の領域に仮想化され、タスクスケジューラーの Bridge からは見えません。`config.json` は、自分で開いた PowerShell 7 で書いてください。`pair` も自分のターミナルで実行してください。

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

この例の IP とユーザー名は置き換えてください。Bridge は指定 IP の HTTPS だけで待ち受けます。`LanBindAddress` が変わると、設定、ファイアウォール、ペアリングを全部やり直すことになります。ルーターで Windows の IP を DHCP の予約にしておいてください。

ファイアウォールでは TCP `LanPort` の接続元を開発機の IP に限定してください。例（管理者 PowerShell、両 IP は実値に置換）:

```powershell
New-NetFirewallRule -DisplayName 'VRC Remote Test LAN Direct' -Direction Inbound -Action Allow -Protocol TCP -LocalAddress 192.0.2.20 -LocalPort 38443 -RemoteAddress 192.0.2.10 -Profile Private
```

Windows がこの LAN を Public（パブリック）として扱っていると、`-Profile Private` の規則は効きません。`Get-NetConnectionProfile` で確かめ、ネットワークを Private にするか、規則を `-Profile Any` にしてください。接続元の IP とポートで絞っているので、Any でも開くのは開発機からの `LanPort` だけです。

Windows が初回起動時に Bridge 実行体向けの受信許可を自動生成した場合、その規則に接続元やポートの制限がないことがあります。限定規則の追加後に `Get-NetFirewallRule -DisplayName vrcremotetest.bridge.exe` で確認し、自動生成された広い許可が有効なら管理者 PowerShell で `Disable-NetFirewallRule -DisplayName vrcremotetest.bridge.exe` を実行してください。限定規則は無効にせず、開発機からの接続を再確認します。

## 2. ペアリング

Windows の Bridge 実行体を `pair` 引数付きで起動すると、接続 URL、証明書 SHA-256、トークンが表示されます。トークンは第三者に渡さないでください。Mac の Unity で `VRChat SDK > VRC Remote Test > Settings` に3値を入力し、`Save Pairing` を押します。証明書指紋を照合するため、初回の値は Windows の画面から確認してください。トークンは macOS Keychain に保存され、Unity プロジェクトには保存されません。Windows のファイアウォール規則はペアリング後も必要です。

ターミナルで状態・マーカーを読む場合は `VRCRT_BRIDGE_URL` と `VRCRT_CERT_SHA256` を設定して `scripts/vrcrt.sh status` を使います。トークンは同じ Keychain 項目から読みます。CI では `VRC_REMOTE_TEST_BRIDGE_URL`、`VRC_REMOTE_TEST_CERT_SHA256`、`VRC_REMOTE_TEST_TOKEN`（CLI は `VRCRT_*`）を秘密設定から渡してください。

## 3. 動作確認と旧環境の撤去

Unity の Preflight が `Windows Bridge: Connected` になり、`Remote Build & Test` の結果、台別状態、ログ、`Save Full Log`、`vrcrt.sh status` を確認します。Bridge のプライベート `state` 内に `incoming`、`results`、`status`、`logs` が作られます。

新接続を確認した後、旧 `VRCRemoteTest` 共有、SMB 専用アカウント、SMB 向けファイアウォール規則、Mac の自動マウントを管理者が手動で撤去します。旧共有内のビルドやログで保管が必要なものは、撤去前に別の保管先へ移してください。旧設定の `StagingDirectory` は削除します。以前の実機記録は `docs/validation` に履歴として残します。

## 4. Mac から管理する（v0.8.0）

ペアリングが済めば、次のことは Mac の `scripts/vrcrt.sh` から行えます。どれも同じ証明書とトークンで動きます。

| コマンド | すること |
|---|---|
| `vrcrt.sh doctor` | Bridge の版と実行体の SHA-256、いま効いている設定、注意（例: `AutoLaunchVrchat is false`）、最後の設定の変更の結果、profile ごとのアカウント、台ごとのログイン状態、Bridge 自身のログの末尾 |
| `vrcrt.sh config get` | 変えられる設定の今の値と、`VrchatExecutable` の候補 |
| `vrcrt.sh config set KEY=VALUE…` | 設定を変えて Bridge を起動し直し、効いたかを確かめる |
| `vrcrt.sh restart` | Bridge を起動し直す |
| `vrcrt.sh close-clients` | Bridge が起動した VRChat を閉じる |
| `vrcrt.sh profiles` | profile ごとに、最後にログインしたアカウント |

変えられる設定は `AutoLaunchVrchat`、`VrchatClientMode`、`VrchatProfiles`、`VrchatClientCount`、`VrchatMode`、`VrchatStartupTimeoutSeconds`（30〜600）、`VrchatStartupSettleDelaySeconds`（1〜300）、`RetainBuilds`（1〜100）、`MaxArtifactSizeBytes`（1 MiB〜2 GiB）、`LogMarkerPrefixes` です。`VrchatExecutable` は、Bridge が Steam のライブラリで見つけた `VRChat.exe` の候補から選びます（`VrchatExecutableCandidate=steam-0` のように指定）。`LanBindAddress`、`LanPort`、`VrchatWorldsDirectory` とペアリングは、Mac からは変えられません。

- 設定を変えると、Bridge は前の `config.json` を `config.json.bak-<時刻>` として残し（新しい5個まで）、プロセスの中で起動し直します。新しい設定で起動できなければ、前の設定に戻して起動し直し、`doctor` に `rolledBack` と理由が出ます。
- `config.json` を書き直すとき、コメントは消えます。元の内容はバックアップに残ります。
- `VrchatClientMode` を変えるときは、Bridge が起動した VRChat を先に閉じます。Bridge が起動していない VRChat が動いていれば、何も閉じず、設定も変えません。v0.7.1 までに Reload で起動した VRChat には記録が無いので、Windows で閉じてください。
- 配備や別の操作の最中は、409 busy で断ります。少し待ってからやり直してください。
- `AutoLaunchVrchat` を直したあとに VRChat を起動するには、Unity の Redeploy で前のワールドを配り直します。
