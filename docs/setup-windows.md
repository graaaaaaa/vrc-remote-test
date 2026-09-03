# Windows側セットアップ手順

VRC Remote Testを使うために、Windows実機（VRChatクライアントを動かすマシン）側で一度だけ行う設定をまとめる。実際の初回E2Eテスト（2026-09-02実施、macOS Unity → SDK Build → SMBアップロード → Windows Bridge → VRChat `--watch-worlds`リロード、まで実機で成功）で得た手順・詰まりどころをそのまま反映している。

**前提**: README記載のSetup Flow（仕様書§63）のうち、1〜4（Bridge install〜VRChat launch設定）がこのドキュメントの対象。5以降（ALCOMでのpackage追加、Unity側操作）はmacOS側の操作であり対象外。

---

## 前提条件

- [ ] Windows 11マシン（管理者権限を持つアカウントでログイン可能なこと）
- [ ] Steam + VRChatがインストール済み
- [ ] PowerShell 7（`pwsh`）がインストール済み — Windows標準のPowerShell 5.1では`start-vrchat-dev.ps1`等が動かない（`#Requires -Version 7.0`）
- [ ] macOS開発機で `dotnet publish` によりBridgeのwin-x64単一ファイル実行体を発行済み（`bridge/README.md`参照）、またはWindows実機に.NET 10 SDKを入れて直接ビルド

---

## 1. Bridge実行体の発行

macOS開発機で発行:

```bash
cd bridge
dotnet publish src/VRCRemoteTest.Bridge/VRCRemoteTest.Bridge.csproj \
  -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

出力: `bridge/src/VRCRemoteTest.Bridge/bin/Release/net10.0/win-x64/publish/VRCRemoteTest.Bridge.exe`。

Windows実機への実際の配置は、SMB共有が使えるようになった後の**手順6**で行う（この時点ではまだSMB共有が存在しないため）。

---

## 2. ステージングディレクトリとconfig.jsonの作成

```powershell
New-Item -ItemType Directory -Path C:\VRCRemoteTest -Force
```

`%LOCALAPPDATA%\VRCRemoteTest\config.json` を作成する（**Bridgeは自動生成しない。手動作成が必須**）:

```powershell
New-Item -ItemType Directory -Path "$env:LOCALAPPDATA\VRCRemoteTest" -Force
@'
{
  "Bridge": {
    "StagingDirectory": "C:\\VRCRemoteTest",
    "VrchatWorldsDirectory": "C:\\Users\\<ユーザー名>\\AppData\\LocalLow\\VRChat\\VRChat\\Worlds",
    "MaxArtifactSizeBytes": 524288000,
    "RetainBuilds": 10
  }
}
'@ | Set-Content -Path "$env:LOCALAPPDATA\VRCRemoteTest\config.json" -Encoding utf8
```

`<ユーザー名>`は実際のWindowsログインユーザー名に置き換える。`VrchatWorldsDirectory`が実在しない場合Bridgeは起動を拒否する（勝手な推測をしない設計）ので、VRChatを一度でも起動してWorldsディレクトリが生成済みであることを事前に確認しておく。

---

## 3. SMB共有の作成（管理者権限必須）

`New-SmbShare`は非管理者セッションでは `Windows System Error 5`（access denied）で失敗する。**管理者としてPowerShellを起動**すること。

```powershell
# SMB共有を作成
New-SmbShare -Name "VRCRemoteTest" -Path "C:\VRCRemoteTest" -FullAccess "Everyone"

# ファイアウォール: 「ファイルとプリンター共有」規則を有効化
# 注意: 日本語版WindowsではDisplayGroup名 "File and Printer Sharing" が
# ローカライズされているため、-DisplayGroup 指定は失敗する。
# ロケール非依存の固定ルール名を使うこと。
Get-NetFirewallRule -Name "FPS-SMB-In-TCP" | Enable-NetFirewallRule
```

SMB共有アクセス権限（`Grant-SmbShareAccess`）だけでなく、**NTFSレベルのアクセス権限も別途必要**（片方だけでは実際のファイル書き込みができない）:

```powershell
icacls "C:\VRCRemoteTest" /grant "<ユーザーまたはグループ名>:(OI)(CI)F" /T
```

---

## 4. SMB認証 — PINログイン環境での注意

**Windows Helloの PIN ログインはSMBネットワーク認証には使えない。** SMBはWindowsアカウントの実パスワードを要求するが、PINはローカルデバイス限定の認証情報であり、ネットワーク越しの認証には使われない。

ログイン用アカウントに実パスワードを設定していない・思い出せない場合、**SMB専用のローカルアカウントを新規作成**するのが最も簡単:

```powershell
$securePassword = Read-Host -AsSecureString "vrcremote アカウントのパスワードを入力"
New-LocalUser -Name "vrcremote" -Password $securePassword -PasswordNeverExpires

# 作成したアカウントに共有アクセス権限を付与
Grant-SmbShareAccess -Name "VRCRemoteTest" -AccountName "vrcremote" -AccessRight Full -Force
icacls "C:\VRCRemoteTest" /grant "vrcremote:(OI)(CI)F" /T
```

Mac側からのマウント時は、Windowsのメインアカウントではなく `vrcremote` のユーザー名・パスワードで認証する。

---

## 5. macOS側: SMB共有のマウント

Finderの「サーバへ接続」（`Cmd+K`）で以下を入力:

```
smb://vrcremote@<Windowsマシンのホスト名またはIPアドレス>/VRCRemoteTest
```

パスワード入力を求められたら、手順4で設定した`vrcremote`アカウントのパスワードを入力する。マウント後、`/Volumes/VRCRemoteTest`として書き込み可能であることを確認する:

```bash
touch /Volumes/VRCRemoteTest/test.txt && rm /Volumes/VRCRemoteTest/test.txt && echo OK
```

---

## 6. Bridge実行体の配置とTask Schedulerへの自動起動登録

手順1で発行したexeを、マウント済みのSMB共有経由で一時drop位置へコピーする（macOS側）:

```bash
mkdir -p /Volumes/VRCRemoteTest/_bridge-drop
cp bridge/src/VRCRemoteTest.Bridge/bin/Release/net10.0/win-x64/publish/VRCRemoteTest.Bridge.exe \
  /Volumes/VRCRemoteTest/_bridge-drop/
```

Windows側で`scripts/install-bridge.ps1`を実行する（管理者権限は不要 — Bridgeはインタラクティブユーザーセッションで非elevated実行する設計、仕様書§20）:

```powershell
.\scripts\install-bridge.ps1 -SourcePath C:\VRCRemoteTest\_bridge-drop\VRCRemoteTest.Bridge.exe -StartNow
```

これにより:

- 実行体が`%LOCALAPPDATA%\Programs\VRCRemoteTest\Bridge\VRCRemoteTest.Bridge.exe`へ複写される（SMB共有の外側 — 共有への書き込み権限を持つ第三者が実行体を差し替えてログオン時の自動実行を乗っ取れないようにするため、意図的にSMB共有の外に配置する）
- Windows Task Schedulerへ「At log on」トリガー・非elevatedでタスクが登録される
- `-StartNow`により登録直後にBridgeが起動する

再実行しても安全（冪等）。アンインストールする場合は`scripts/uninstall-bridge.ps1`を実行する。詳細は各スクリプトの`-?`ヘルプ、または`bridge/README.md`を参照。

**手動起動のみで運用したい場合**（自動起動を使わない場合）は、このステップを省略して次のステップ7の手動起動をそのまま使い続けてよい。

---

## 7. Bridgeの起動（手動、動作確認用）

手順6で自動起動を登録済みの場合、通常はこのステップを実行する必要はない。動作確認のため一時的に手動起動したい場合、または自動起動を使わない運用の場合に使う。管理者権限は不要。

```powershell
& "$env:LOCALAPPDATA\Programs\VRCRemoteTest\Bridge\VRCRemoteTest.Bridge.exe"
```

起動ログ（`%LOCALAPPDATA%\VRCRemoteTest\logs\bridge-*.log`、およびコンソール）に `VRC Remote Test Bridge starting.` が出力され、設定エラーで即終了しないことを確認する。設定エラーの場合は`Configuration error: ...`が出力されるので、`config.json`の`VrchatWorldsDirectory`のパスを再確認する。

---

## 8. VRChatの起動（`--watch-worlds`付き）

PowerShell 7（`pwsh`）で:

```powershell
& "\\<Macのホスト名>\...\scripts\start-vrchat-dev.ps1" -Mode Desktop
```

またはリポジトリをWindows側にもコピーしてある場合はローカルパスから直接実行する。初回実行時、スクリプトファイルが「ブロック」されている場合は事前に以下を実行:

```powershell
Unblock-File -Path .\start-vrchat-dev.ps1
# それでも実行ポリシーで弾かれる場合
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
```

VRChatのホーム画面到達を確認したら準備完了。以降はUnity側の `VRChat SDK > VRC Remote Test` ウィンドウから `Remote Build & Test` を実行する（Mac側の操作、仕様書§60）。

---

## 9. （任意）VRChat自動起動の有効化（Phase 4.1）

手順8を毎回手動で行いたくない場合、Bridgeに任せることができる。`config.json`に以下を追加:

```json
{
  "Bridge": {
    "StagingDirectory": "C:\\VRCRemoteTest",
    "VrchatWorldsDirectory": "C:\\Users\\<ユーザー名>\\AppData\\LocalLow\\VRChat\\VRChat\\Worlds",
    "MaxArtifactSizeBytes": 524288000,
    "RetainBuilds": 10,
    "VrchatExecutable": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\VRChat\\VRChat.exe",
    "VrchatMode": "Desktop",
    "AutoLaunchVrchat": true,
    "VrchatStartupTimeoutSeconds": 60,
    "VrchatStartupSettleDelaySeconds": 30
  }
}
```

- `VrchatExecutable`は実際のSteamインストール先に合わせて調整する。ファイル名は`VRChat.exe`と完全一致している必要がある（大文字小文字は不問）
- 有効化するとBridgeは、VRChatが未起動の状態でビルドを受け取った際に自動的に`--watch-worlds`付きで起動し、準備が整うまで待ってからWorldsディレクトリへ配置する
- **既に`--watch-worlds`無しでVRChatが起動中の場合、Bridgeは自動的に再起動・終了させない**（`VRCHAT_WATCH_WORLDS_MISSING`でビルド失敗となる）。この場合は手動でVRChatを終了させるか、手順8の通り`--watch-worlds`付きで起動し直す
- **`VrchatStartupSettleDelaySeconds`（起動後の最低待機時間）は環境によって調整が必要**。実機検証（2026-09-02）ではデフォルト15秒（初期値）ではVRChatの実際の準備が間に合わず、ファイル配置は成功してもリロードされないケースが発生した。VRChatが実際にホーム画面へ到達するまでの体感時間を確認し、それに応じて値を調整すること（30秒でも不十分な場合はさらに引き上げる）

**Unity側のResult Timeout推奨値**: `AutoLaunchVrchat: true`の場合、Bridge側の起動待機（最短でも約15〜17秒、デフォルトタイムアウト60秒）が発生するため、Unity側の `VRC Remote Test` ウィンドウ設定foldoutにある `Result Timeout (s)` はデフォルトの60秒のままだとBridge側のタイムアウトと競合する可能性がある。**90秒以上に引き上げることを推奨**する。

---

## 10. （任意）Moonlight連携（Phase 5）

Moonlight（NVIDIA GameStream/Sunshine互換のリモートデスクトップクライアント）でWindows実機のVRChat画面をmacOS側から見ている場合、Unity側の `VRC Remote Test` ウィンドウから直接Moonlightを起動・フォーカスできる。

- **`[Open Moonlight]`ボタン**（`DrawActionButtons`内）: macOS側にインストール済みのMoonlightアプリを`open -a`で起動（既に起動中なら前面に呼び出す）。Windows側の設定は不要 — この機能は完全にmacOS側で完結する
- **Settings foldoutの `Moonlight Application Name`**: `open -a`に渡すアプリ名。通常はデフォルトの`Moonlight`のままでよいが、App Store版など名称が異なるインストールの場合はここで変更する
- **Settings foldoutの `Focus Moonlight after deploy`**（デフォルトOFF）: 有効にすると、`Remote Build & Test`または`Deploy Last Build`が成功した直後に自動でMoonlightを前面に呼び出す。ビルドがVRChat側にリロードされる様子をすぐ確認したい場合に有効化する
- Moonlight側のホスト接続設定（Sunshine側のペアリング等）は本ツールの範囲外。あらかじめMoonlightで対象のWindowsマシンに接続済みであることが前提

## 11. （任意）VRChat Log Viewer（Phase 5）

`RemoteBuildCommand`が使う同じSMB共有経由で、BridgeがVRChatの`output_log_*.txt`（最新200行、直近ウィンドウのスナップショット）を5秒間隔で配信する。Windows側の追加設定は不要 — `VrchatWorldsDirectory`が正しく設定済みであれば自動的に有効になる。Unity側の `VRC Remote Test` ウィンドウの `VRChat Log` foldoutから、カテゴリフィルタ（All/Error/Exception/Udon/Shader/Warning）付きで閲覧できる。表示専用で、readiness判定やビルドの成否には一切影響しない。

---

## トラブルシューティング

| 症状 | 原因 | 対処 |
|------|------|------|
| `New-SmbShare`が`Windows System Error 5`で失敗 | 非管理者PowerShellで実行している | PowerShellを「管理者として実行」し直す |
| `Enable-NetFirewallRule -DisplayGroup "File and Printer Sharing"`が規則を見つけられない | 日本語版Windowsでは`DisplayGroup`名がローカライズされている | `Get-NetFirewallRule -Name "FPS-SMB-In-TCP"`という固定名で指定する |
| SMB共有はマウントできるが、ファイル作成/更新が失敗する（読み取りは可能） | `Grant-SmbShareAccess`（SMB層）のみで、NTFS層のACLが未設定 | `icacls`で該当ユーザーに`(OI)(CI)F`権限を追加する |
| SMBマウント時のパスワード認証が繰り返し失敗する | PINでログインしているアカウントには、SMBが要求する実パスワードが設定されていない/不明 | SMB専用のローカルアカウント（例: `vrcremote`）を新規作成し、そちらの資格情報でマウントする |
| `.ps1`スクリプトの実行が"このシステムではスクリプトの実行が無効になっているため"のエラーで拒否される | 実行ポリシー、またはダウンロード/コピーされたファイルの「ブロック」属性 | `Unblock-File`を実行。それでも失敗する場合は`Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned` |
| `start-vrchat-dev.ps1`が構文エラー等で動かない | Windows標準のPowerShell 5.1で実行している（スクリプトは7.0以降が必要） | `pwsh`（PowerShell 7）から実行する |
| Bridgeが起動直後に`Configuration error`で終了する | `config.json`の`VrchatWorldsDirectory`が存在しない、またはパスが誤っている | VRChatを一度起動してWorldsディレクトリを生成させてから、正確なパスを`config.json`に設定する |
| `install-bridge.ps1`登録後、ログオンしてもBridgeが起動していない | Task Schedulerの実行結果を確認していない、または`config.json`が無効 | `Get-ScheduledTaskInfo -TaskName 'VRCRemoteTestBridge' -TaskPath '\' \| Select-Object LastRunTime, LastTaskResult`で終了コードを確認し、`%LOCALAPPDATA%\VRCRemoteTest\logs\bridge-*.log`の最新ログを確認する |
| `install-bridge.ps1`が「本スクリプトが管理するタスクとは判別できません」で失敗する | 同名の無関係なタスクが既に存在する | `Get-ScheduledTask -TaskName 'VRCRemoteTestBridge' -TaskPath '\'`で内容を確認し、無関係であれば`-TaskName`で別名を指定するか、手動で削除してから再実行する |

---

## 関連ドキュメント

- `bridge/README.md` — Bridge本体のビルド・設定ファイル・ステージング構造の詳細
- `docs/validation/watch-worlds-spike.md` — `--watch-worlds`挙動の実機検証記録（Phase 0.5）
- `docs/sdk-api-notes.md` — Unity側SDK APIの調査ノート
- `VRC Remote Test — 実装仕様書.md` §16（Windows Share）、§44（Windows config）、§63（Setup Flow）
