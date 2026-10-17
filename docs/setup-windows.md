# Windows側セットアップ手順

VRC Remote Testを使うために、Windows実機（VRChatクライアントを動かすマシン）側で一度だけ行う設定をまとめる。

**前提**: README記載のSetup Flow（仕様書§63）のうち、Bridge install〜VRChat launch設定がこのドキュメントの対象。ALCOMでのpackage追加、Unity側操作はmacOS側の操作であり対象外。

**v0.2 以前の構成から更新する場合**は、新規セットアップではなく[手順14（既存環境からの移行）](#14-既存環境からの移行)に従うこと。共有の権限と状態ファイルの置き場所が変わっている。

> **注意**: このドキュメントの Windows 側のコマンドのうち、共有の権限（手順3）、移行（手順14）、OSC の遮断（手順12）は、まだ実機で通しの確認をしていない。実行したら、各手順の「確認」の結果が期待どおりかを必ず見ること。

---

## セキュリティの考え方（最初に読む）

- **SMB 共有への書き込み権限 = ワールドを配置できる権限**。Bridge の認証は、SMB アカウントの資格情報と共有の ACL だけで成り立っている（仕様書§49）。
- 共有の中で Mac が書き込めるのは `incoming\` と `_bridge-drop\` だけにする。`results\`、`status\`、`logs\` は読み取り専用にし、Bridge が処理中に信頼する状態（processing、archive、failed）は共有の外（`%LOCALAPPDATA%\VRCRemoteTest\state`）に置く。
- Bridge の実行体はハッシュを照合してから登録する。共有に置いた exe は、共有に書ける人なら誰でも差し替えられるため。リリース済みの版は、GitHub Actions がビルドして来歴を署名した exe を、Windows 上で直接ダウンロードして検証する（手順1-A）。
- Windows で実行するスクリプトは、Windows のローカルにあるものだけを使う。Mac の共有上のスクリプトを実行すると、Mac が侵害されたときに Windows でコードを実行されてしまう。
- これらは一人開発・家庭内 LAN を想定した設計。共有の資格情報が漏れれば、任意のワールドを配置される（Mac が侵害された場合も同じ）。
- `vrcremote` には、シンボリックリンクを作成する特権を与えない（既定のまま）。Bridge は共有内のリンクを取り込まないが、検査と読み取りの間に差し替えられる余地までは塞いでいない。
- 受け入れている残余リスク: 共有の資格情報を持つ人は、`incoming` へのアップロードでディスクを埋めたり、処理中のアップロードを同名のファイルで妨害したりできる。また、同じ buildId を再度アップロードすると、`results` の結果が `BUILD_ALREADY_PROCESSED` で上書きされる（Unity は buildId を使い回さないので、通常の利用では起きない）。いずれも配置されるワールドの中身には影響しない。

---

## 前提条件

- [ ] Windows 11 マシン（管理者権限を持つアカウントでログイン可能なこと）
- [ ] Steam + VRChat がインストール済み
- [ ] PowerShell 7（`pwsh`）がインストール済み — Windows 標準の PowerShell 5.1 では各スクリプトが動かない（`#Requires -Version 7.0`）
- [ ] 手順1-A（推奨）を使う場合: Windows に GitHub CLI（`gh`）**2.102.0 以上**。`gh --version` で確認し、`gh auth login` を済ませておく。2.101.0 以前は、来歴の検証で `--signer-workflow` が前方一致で比較される不具合がある（[GHSA-wjmr-j3rp-mh2g](https://github.com/cli/cli/security/advisories/GHSA-wjmr-j3rp-mh2g)）
- [ ] 手順1-B を使う場合: macOS 開発機（または Windows 実機）に .NET 10 SDK

以下、**U** は Bridge を動かす Windows の普段使いのユーザー、**vrcremote** は Mac からの SMB 接続専用のアカウントを指す。

---

## 1. Bridge実行体とスクリプトの入手

どちらかを選ぶ。

- **1-A（推奨）GitHub Release から取得**: リリース済みの版を使う場合。
- **1-B 自分でビルド**: まだリリースしていないコミットを試す場合。

### 1-A. GitHub Release から取得（推奨）

GitHub Actions がタグの付いたコミットからビルドし、ビルドの来歴（どのワークフローが、どのコミットから、GitHub のどのランナーでビルドしたか）を署名付きで記録した exe を使う。**ダウンロードと検証は Windows 上で行い、Mac や SMB 共有を経由させない。** スクリプトも同じコミットのものを Windows に直接取得する。

U の通常の PowerShell 7 で実行する（`$tag` は [Releases](https://github.com/graaaaaaa/vrc-remote-test/releases) で確認した使う版に置き換える）:

```powershell
$ErrorActionPreference = 'Stop'
$repo = 'graaaaaaa/vrc-remote-test'
$tag  = 'vX.Y.Z'
$dir  = Join-Path $env:LOCALAPPDATA "VRCRemoteTest\release\$tag"   # 共有の外
New-Item -ItemType Directory -Path $dir -Force | Out-Null

# タグが指すコミット
$commit = gh api "repos/$repo/commits/$tag" --jq .sha
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw "タグ $tag のコミットを取得できませんでした" }

# 実行体
gh release download $tag --repo $repo --pattern VRCRemoteTest.Bridge.exe --dir $dir --clobber
if ($LASTEXITCODE -ne 0) { throw 'ダウンロードに失敗しました' }
$exe = Join-Path $dir 'VRCRemoteTest.Bridge.exe'

# 来歴の検証: このリポジトリの release-package.yml が、このタグとコミットから、
# GitHub ホストのランナーでビルドしたものか。失敗したらインストールしない。
gh attestation verify $exe --repo $repo `
    --signer-workflow "$repo/.github/workflows/release-package.yml" `
    --source-ref "refs/tags/$tag" --source-digest $commit --deny-self-hosted-runners
if ($LASTEXITCODE -ne 0) { throw '来歴の検証に失敗しました。このファイルはインストールしないでください' }

# 同じコミットのスクリプト（install-bridge.ps1 など）
Invoke-WebRequest "https://github.com/$repo/archive/$commit.zip" -OutFile (Join-Path $dir 'source.zip')
Expand-Archive (Join-Path $dir 'source.zip') -DestinationPath (Join-Path $dir 'source') -Force
$scripts = Join-Path $dir "source\vrc-remote-test-$commit\scripts"
Get-ChildItem $scripts -Filter *.ps1 | Unblock-File

# インストーラーに渡すハッシュ
(Get-FileHash $exe -Algorithm SHA256).Hash
```

表示されたハッシュが、Release ページの「Windows Bridge」の節に書かれた SHA-256 と一致することを確認する（大文字小文字は区別しない）。一致したら、この値を手順6の `-ExpectedSha256` に使う。`$exe`、`$scripts` は手順6と手順8でも使うので、同じ PowerShell のウィンドウで続けるか、パスを控えておく。

**この方法で防げること・防げないこと**:

- 防げる: Mac や SMB 共有での exe の差し替え、Mac でのローカルビルドの改ざん。exe もハッシュも、Mac を経由しない。
- 防げない: GitHub アカウントやリポジトリが侵害されて、悪意のあるコミットが main とタグに入った場合（そのコミットからのビルドとして正しく署名される）。Mac に保存された GitHub の認証情報が盗まれた場合もこれに当たる。依存パッケージ（NuGet）の侵害も防げない。来歴は「どこで何からビルドされたか」の証明であって、コードが安全であることの証明ではない。リリースする前に、main に入っているコミットを自分で確認すること。

### 1-B. 自分でビルドする（リリース前のコミットを試す場合）

#### 1-B-1. 依存パッケージの確認と発行

発行の前に、既知の脆弱性がある依存パッケージがないか確認する:

```bash
dotnet list bridge/src/VRCRemoteTest.Bridge/VRCRemoteTest.Bridge.csproj package --vulnerable --include-transitive
```

macOS 開発機で、リポジトリのルートから発行（以降のコマンドもリポジトリのルートで実行する）:

```bash
dotnet publish bridge/src/VRCRemoteTest.Bridge/VRCRemoteTest.Bridge.csproj \
  -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

出力: `bridge/src/VRCRemoteTest.Bridge/bin/Release/net10.0/win-x64/publish/VRCRemoteTest.Bridge.exe`。

#### 1-B-2. SHA-256 の記録

```bash
shasum -a 256 bridge/src/VRCRemoteTest.Bridge/bin/Release/net10.0/win-x64/publish/VRCRemoteTest.Bridge.exe
```

表示された 64 桁のハッシュを、コミット（`git rev-parse HEAD`）と SDK のバージョン（`dotnet --version`）と一緒に控える。手順6でインストーラーに `-ExpectedSha256` として渡す。

**ハッシュは SMB 共有以外の経路で Windows に渡すこと**（Mac の画面を見て入力する、信頼できるパスワードマネージャーのメモを使う、など）。共有に `.sha256` ファイルを置くと、exe と一緒に差し替えられてしまう。

**限界**: この方法は「共有に書ける第三者による差し替え」を防ぐが、Mac 自体が侵害されている場合は、exe とハッシュの両方が偽物になり得るので防げない。リリース済みの版でよければ 1-A を使う。リリース前のコミットでより強くしたい場合は、Windows 実機に .NET 10 SDK を入れ、信頼できるチェックアウトから Windows 上で直接 `dotnet publish` し、その場で `Get-FileHash -Algorithm SHA256` を取る（この場合 exe を共有経由で運ぶ必要もない）。インストーラーなどのスクリプトも、Windows のローカルにある信頼できるチェックアウトのものを使い、Mac の共有上のスクリプトを実行しないこと。

Windows への exe の配置は、共有ができた後の**手順6**で行う。

---

## 2. SMB 専用アカウント `vrcremote` の作成（管理者）

Mac からの SMB 接続には、**普段使いのアカウント（U）ではなく専用のローカルアカウント**を使う。共有の権限を「Mac が書ける場所」と「Bridge だけが書ける場所」に分けるには、2 つのアカウントが別である必要がある。

これは Windows Hello の PIN ログイン対策も兼ねる。PIN はローカル専用の認証情報で、SMB のネットワーク認証には使えない。

**管理者として PowerShell 7 を起動**して実行:

```powershell
$securePassword = Read-Host -AsSecureString "vrcremote アカウントのパスワードを入力"
New-LocalUser -Name "vrcremote" -Password $securePassword -PasswordNeverExpires
```

- パスワードは長く推測されにくいものにする（Mac 側はキーチェーンに保存される）。
- `vrcremote` を Administrators や Remote Desktop Users に**追加しない**。確認: `Get-LocalGroupMember Administrators`。
- **ログオンの制限（推奨）**: `secpol.msc` →「ローカル ポリシー」→「ユーザー権利の割り当て」で、「ローカル ログオンを拒否」と「リモート デスクトップ サービスを使ったログオンを拒否」に `vrcremote` を追加する。SMB に必要なネットワークログオンは残る。Windows 11 Home には `secpol.msc` がないため、この手順は省略し、上のグループに入っていないことだけ確認する。

---

## 3. ステージングディレクトリと SMB 共有の作成（管理者）

### 3-1. U のアカウント名を確認

**U 本人の通常の（管理者ではない）PowerShell** で実行し、表示された名前を控える:

```powershell
whoami
```

Microsoft アカウントでサインインしている場合も、ここで表示される `コンピューター名\名前` の形を使う（メールアドレスやプロファイルのフォルダー名で代用しない）。

### 3-2. ルートと ACL の作成

**管理者 PowerShell 7** で実行する。`$U` は 3-1 で控えた名前に置き換える。権限は SID で指定するので、日本語版 Windows でもグループ名のローカライズに影響されない。

```powershell
$ErrorActionPreference = 'Stop'
$U = 'コンピューター名\ユーザー名'   # 3-1 の whoami の結果
$uSid = ([System.Security.Principal.NTAccount]::new($U)).Translate([System.Security.Principal.SecurityIdentifier]).Value
$remoteSid = (Get-LocalUser -Name 'vrcremote').SID.Value
if ($uSid -eq $remoteSid) { throw 'U と vrcremote は別のアカウントにしてください' }

$root = 'C:\VRCRemoteTest'
if (Test-Path -LiteralPath $root) { throw "$root が既に存在します。既存環境は手順14で移行してください" }

function Invoke-Icacls { & icacls.exe @args; if ($LASTEXITCODE -ne 0) { throw "icacls が失敗しました ($LASTEXITCODE): $args" } }

# ルート: U・SYSTEM・Administrators はフルコントロール、vrcremote は読み取りのみ。
# 新しく作った空のディレクトリを /reset してから明示的に許可し、最後に継承を切るので、
# C:\ から継承される Authenticated Users の変更権限などは残らない。
New-Item -ItemType Directory -Path $root | Out-Null
Invoke-Icacls $root /reset
Invoke-Icacls $root /grant:r "*${uSid}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "*${remoteSid}:(OI)(CI)RX"
Invoke-Icacls $root /inheritance:r
Invoke-Icacls $root /setowner "*$uSid"

# Mac が書き込む場所だけ、vrcremote に書き込み権限を足す。
# ディレクトリ自体には削除権限を与えない（消されると、Bridge が作り直したときに
# この権限が失われて Mac から書けなくなる）。中に作るファイルには変更権限を継承させる。
foreach ($name in 'incoming', '_bridge-drop') {
    $path = Join-Path $root $name
    New-Item -ItemType Directory -Path $path | Out-Null
    Invoke-Icacls $path /grant:r "*${remoteSid}:(RX,W)"
    Invoke-Icacls $path /grant "*${remoteSid}:(OI)(CI)(IO)M"
}

# Bridge が書き、Mac は読むだけの場所（ルートの読み取りを継承する）。
foreach ($name in 'results', 'status', 'logs') {
    New-Item -ItemType Directory -Path (Join-Path $root $name) | Out-Null
}
```

**確認**:

```powershell
icacls C:\VRCRemoteTest
icacls C:\VRCRemoteTest\incoming
icacls C:\VRCRemoteTest\results
```

- ルート: U・`NT AUTHORITY\SYSTEM`・`BUILTIN\Administrators` が `(OI)(CI)(F)`、`vrcremote` が `(OI)(CI)(RX)` の **4 行だけ**で、`(I)`（継承）の付いた行が無いこと。
- `incoming`: 上の 4 行が `(I)` 付きで並び、加えて `vrcremote` の `(RX,W)` と `(OI)(CI)(IO)(M)` があること（`(M)` が `(IO)` なしで付いていないこと）。
- `results`: 上の 4 行が `(I)` 付きで並ぶだけで、`vrcremote` は `(RX)` のみであること。

`incoming` は必ず Windows 側で作っておくこと。ルートが読み取り専用なので、Mac からは作れない（作れないと Unity 側の Preflight が `Not reachable` になる）。

### 3-3. 共有の作成（暗号化つき）

```powershell
New-SmbShare -Name 'VRCRemoteTest' -Path 'C:\VRCRemoteTest' -ChangeAccess "$env:COMPUTERNAME\vrcremote" -EncryptData $true
Set-SmbServerConfiguration -RejectUnencryptedAccess $true -Force

# ファイアウォール: 「ファイルとプリンター共有」規則を有効化。
# 日本語版 Windows では DisplayGroup 名がローカライズされているため、固定のルール名を使う。
Get-NetFirewallRule -Name "FPS-SMB-In-TCP" | Enable-NetFirewallRule
```

- 共有の権限は **Change**（Full ではない）。Full は ACL や所有者の変更まで含むため与えない。実際に書ける範囲は 3-2 の NTFS の権限で決まる。
- `-EncryptData $true` で、この共有への通信を SMB 3 で暗号化する。ログやワールドを LAN 上で平文で流さないため。
- 任意: SMB のファイアウォール規則の接続元を絞る場合は `Set-NetFirewallRule -Name 'FPS-SMB-In-TCP' -RemoteAddress LocalSubnet`（または Mac の IP アドレス）。

**確認**:

```powershell
Get-SmbShare -Name 'VRCRemoteTest' | Select-Object Name, Path, EncryptData
Get-SmbShareAccess -Name 'VRCRemoteTest'
Get-SmbServerConfiguration | Select-Object RejectUnencryptedAccess, RequireSecuritySignature
```

`EncryptData` が `True`、共有のアクセスが `vrcremote` の `Change` の 1 行だけであること。

---

## 4. config.json の作成

U の通常の PowerShell で、`%LOCALAPPDATA%\VRCRemoteTest\config.json` を作成する（**Bridge は自動生成しない。手動作成が必須**）:

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

- `<ユーザー名>` は実際の Windows ログインユーザー名に置き換える。`VrchatWorldsDirectory` が実在しない場合 Bridge は起動を拒否する（勝手な推測をしない設計）ので、VRChat を一度でも起動して Worlds ディレクトリが生成済みであることを事前に確認しておく。
- Bridge の状態ディレクトリ（processing、archive、failed）は `%LOCALAPPDATA%\VRCRemoteTest\state` に**固定**で、config.json では変更できない。ユーザープロファイルの中は通常 U・SYSTEM・Administrators しかアクセスできないことを前提にしている（プロファイル全体をネットワーク上に置く構成などは対象外）。

---

## 5. macOS側: SMB共有のマウントと権限の確認

Finder の「サーバへ接続」（`Cmd+K`）で以下を入力:

```
smb://vrcremote@<Windowsマシンのホスト名またはIPアドレス>/VRCRemoteTest
```

パスワード入力を求められたら、手順2で設定した `vrcremote` のパスワードを入力する。マウント後、`/Volumes/VRCRemoteTest` で**書ける場所と書けない場所**を確認する:

```bash
touch /Volumes/VRCRemoteTest/incoming/.probe && rm /Volumes/VRCRemoteTest/incoming/.probe && echo "incoming: 書き込みOK"
ls /Volumes/VRCRemoteTest/results > /dev/null && echo "results: 読み取りOK"
touch /Volumes/VRCRemoteTest/results/.probe 2>/dev/null && echo "NG: results に書き込めてしまう" || echo "results: 書き込み拒否OK"
touch /Volumes/VRCRemoteTest/.probe 2>/dev/null && echo "NG: ルートに書き込めてしまう" || echo "ルート: 書き込み拒否OK"
```

暗号化されているかを確認する:

```bash
smbutil statshares -m /Volumes/VRCRemoteTest
```

`SMB_VERSION` が `SMB_3.x`、`ENCRYPTION_REQUIRED` が `TRUE` であること。macOS のバージョンによっては出ないフィールドがある。出なかったフィールドは「未確認」として扱い、Windows 側の `Get-SmbShare` の `EncryptData` と、`RejectUnencryptedAccess` が `True` のまま接続できていることで判断する。

---

## 6. Bridge実行体の配置とTask Schedulerへの自動起動登録

**U の通常の PowerShell 7**（管理者権限は不要 — Bridge は対話ユーザーセッションで非 elevated 実行する設計、仕様書§20）から `install-bridge.ps1` を実行する。

**手順1-A の場合**（共有も Mac も経由しない）。`$exe`、`$scripts` は手順1-A で設定したもの:

```powershell
& (Join-Path $scripts 'install-bridge.ps1') -ExpectedSha256 <手順1-Aで確認したハッシュ> -SourcePath $exe -StartNow
```

**手順1-B の場合**。Mac で発行した exe を、マウント済みの共有の `_bridge-drop` へコピーする（macOS側）:

```bash
cp bridge/src/VRCRemoteTest.Bridge/bin/Release/net10.0/win-x64/publish/VRCRemoteTest.Bridge.exe \
  /Volumes/VRCRemoteTest/_bridge-drop/
```

Windows 側で、Windows のローカルにある信頼できるチェックアウトの `scripts\install-bridge.ps1` を実行する。`-ExpectedSha256` には、手順1-B で控えたハッシュを**共有以外の経路で**持ってきて指定する:

```powershell
.\scripts\install-bridge.ps1 -ExpectedSha256 <手順1-Bの64桁のハッシュ> -SourcePath C:\VRCRemoteTest\_bridge-drop\VRCRemoteTest.Bridge.exe -StartNow
```

これにより:

- 配置先（既定 `%LOCALAPPDATA%\Programs\VRCRemoteTest\Bridge\`）が `%LOCALAPPDATA%` の下にあり、U・SYSTEM・Administrators 以外が書き込めず、シンボリックリンクやジャンクションを含まないことを確認する。
- `-SourcePath` の exe をいったん配置先の中の候補ファイルにコピーし、**その候補の SHA-256** を照合してから、同じディレクトリ内の rename で実行体として配置する。照合後にコピー元を読み直すことはない。ハッシュが一致しなければ、配置済みの実行体とタスクは変更せずに中断する。
- Windows Task Scheduler へ「At log on」トリガー・非 elevated でタスクが登録される。
- `-StartNow` により登録直後に Bridge が起動する。

1-B の場合は、配置が済んだら共有の drop を消しておく:

```powershell
Remove-Item C:\VRCRemoteTest\_bridge-drop\VRCRemoteTest.Bridge.exe
```

再実行しても安全（冪等）。配置済みの実行体が期待するハッシュと一致していれば、`-SourcePath` は読まずに置き換えを省略する。アンインストールする場合は `scripts/uninstall-bridge.ps1` を実行する。詳細は各スクリプトの `-?` ヘルプ、または `bridge/README.md` を参照。

**Bridge を更新するとき**: 新しい版で手順1（1-A なら新しい `$tag`）からやり直す。インストーラーは稼働中の Bridge を自動では止めないので、先に `Stop-ScheduledTask -TaskName 'VRCRemoteTestBridge'` で停止してから、新しい exe とハッシュで同じコマンドを実行する。

**手動起動のみで運用したい場合**（自動起動を使わない場合）でも、実行体は上記のインストーラーで配置し、タスクを使わずに手順7の手動起動を使うこと。

---

## 7. Bridgeの起動（手動、動作確認用）

手順6で自動起動を登録済みの場合、通常はこのステップを実行する必要はない。動作確認のため一時的に手動起動したい場合、または自動起動を使わない運用の場合に使う。管理者権限は不要。

```powershell
& "$env:LOCALAPPDATA\Programs\VRCRemoteTest\Bridge\VRCRemoteTest.Bridge.exe"
```

起動ログ（`%LOCALAPPDATA%\VRCRemoteTest\logs\bridge-*.log`、およびコンソール）に `VRC Remote Test Bridge starting.` が出力され、設定エラーで即終了しないことを確認する。設定エラーの場合は `Configuration error: ...` が出力されるので、`config.json` の `VrchatWorldsDirectory` のパスを再確認する。

---

## 8. VRChatの起動（`--watch-worlds`付き）

U の PowerShell 7（`pwsh`）で、**Windows のローカルにあるスクリプト**を実行する。手順1-A の場合は `$scripts` のもの:

```powershell
& (Join-Path $scripts 'start-vrchat-dev.ps1') -Mode Desktop
```

手順1-B の場合は、Windows のローカルにある信頼できるチェックアウトの `scripts\start-vrchat-dev.ps1` を使う。**Mac の共有（`\\<Macのホスト名>\...`）上のスクリプトを直接実行しないこと。** Bridge の exe を検証していても、Mac が侵害されていれば、そのスクリプトで Windows 上の任意のコードを実行されてしまう。

初回実行時、スクリプトファイルが「ブロック」されている場合は事前に以下を実行（手順1-A の手順では済んでいる）:

```powershell
Unblock-File -Path .\start-vrchat-dev.ps1
# それでも実行ポリシーで弾かれる場合
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
```

VRChatのホーム画面到達を確認したら準備完了。以降はUnity側の `VRChat SDK > VRC Remote Test` ウィンドウから `Remote Build & Test` を実行する（Mac側の操作、仕様書§60）。

---

## 9. （任意）VRChat自動起動の有効化

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
    "VrchatClientCount": 1,
    "VrchatStartupTimeoutSeconds": 60,
    "VrchatStartupSettleDelaySeconds": 30
  }
}
```

- `VrchatExecutable`は実際のSteamインストール先に合わせて調整する。ファイル名は`VRChat.exe`と完全一致している必要がある（大文字小文字は不問）
- `VrchatClientCount`は起動する台数（1〜4、省略時は1）。2以上の使い方は手順9-1。別々のアカウントの台を使うときは手順9-3（`VrchatClientMode: Relaunch`）
- 有効化するとBridgeは、VRChatが1台も起動していない状態でビルドを受け取ると、**先にWorldsディレクトリへ配置してから**、そのファイルを`--url`に渡してVRChatを起動する（コールドスタート）。VRChatは起動時にそのワールドへ直接入るので、ホーム画面に着くのを待つ時間は無い。結果は、全台のプロセスを起動できた時点で書かれる（ワールドへの入室までは確かめない）
- VRChatが起動済みなら、配置するだけで`--watch-worlds`付きの全台がリロードする。ただし、起動から`VrchatStartupSettleDelaySeconds`たっていない台があれば、その時間まで待ってから配置する。VRChatはログインが済むまで配置を拾わないため（コールドスタートの直後に続けて送ったビルドも、この待ちになる）
- **既に`--watch-worlds`無しでVRChatが起動中の場合、Bridgeは自動的に再起動・終了させない**（`VRCHAT_WATCH_WORLDS_MISSING`でビルド失敗となる）。この場合は手動でVRChatを終了させるか、手順8の通り`--watch-worlds`付きで起動し直す。Bridge が VRChat を終了させるのは、手順9-3の Relaunch を選んだときに、自分で起動したと確かめられた台だけ
- Bridgeが状態を確かめられないVRChat（プロセスの一覧を取れない、セッションを読めない、WMI がコマンドラインを返さない・時間内に答えない、など）が動いているときも、何も起動せず、何も終了させずに`VRCHAT_UNMANAGED_RUNNING`でビルドが失敗する。そのVRChatを手で閉じてから、もう一度ビルドを送る。Unity v0.4.0 以降は、この手順の案内文も表示される（v0.3.0 はコードとメッセージだけ）。別のユーザーのセッション（別の Windows アカウントやリモートデスクトップ）で動いている VRChat は数えない
- **`VrchatStartupSettleDelaySeconds`（起動済みの台へ配置するまでの最低待機時間）は環境によって調整が必要**。デフォルト30秒でも短すぎる場合、ファイル配置は成功してもリロードされないことがある。手で起動したVRChatがホーム画面へ到達するまでの体感時間を確認し、それに応じて値を調整すること（`--url`で起動した台は、実測では起動の10秒後から配置を拾った）
- 同じ Windows アカウントでは、Bridge は1つしか動かない。`%LOCALAPPDATA%\VRCRemoteTest\state\bridge.lock`を開いたままにしておき、2つ目のBridgeは「Another VRC Remote Test Bridge is already running」とログに出して起動を拒否する。その日のログファイルは1つ目の Bridge が開いているので、2つ目のメッセージは `%LOCALAPPDATA%\VRCRemoteTest\logs\bridge-<日付>_001.log` のような別名のファイルに書かれる。ロックはアカウントごとなので、共有と Worlds は Bridge 用のアカウント（U）1つだけで使う

**Unity側のResult Timeout推奨値**: 結果が書かれるまでの時間は、「前に並んでいるビルドの残り ＋ このビルドのコピー・検証・配置 ＋ 準備待ち（起動直後の台があれば最大`VrchatStartupTimeoutSeconds`、デフォルト60秒）＋ コールドスタートでの起動の間隔（(台数−1)×3秒、4台で9秒。CPU の負荷が高く VRChat のログファイルができるのが遅いときは延び、最大で 60秒＋(台数−1)×3秒。手順11）＋ 余裕」になる。負荷が高いと VRChat の起動そのものも遅くなり、3秒の間隔は 3.1〜7.5 秒に延びた。実機では、CPU の使用率が約100%のとき、4台のコールドスタートで送信から結果まで 84〜96 秒かかった。Unity側の `VRC Remote Test` ウィンドウ設定foldoutにある `Result Timeout (s)` はデフォルトの60秒だと足りないことがあるので、**120秒以上、ビルドを続けて送るなら180秒から**にする。コールドスタートの直後に次のビルドを送ると、そのビルドは前のビルドの残り（最大で約100秒）と準備待ち（最大60秒）を待つためである。180秒でも、ビルドがいくつ並んでも間に合うという保証ではない。

### 9-1. 複数台（SDK の Number of Clients 相当）

`"VrchatClientCount": 2` のように2以上にすると、SDK の Build & Test の Number of Clients と同じように、コールドスタートで N 台を3秒以上の間隔で起動し（前の台のログファイルができるのを待つ。手順11）、全台が同じインスタンスに入る。以降のビルドでは、全台がそろって同じインスタンスへリロードする（`docs/validation/multi-client-spike.md`、4台まで確認済み）。

- **全台が同じ VRChat アカウントになる**（`--profile` を付けないので、全台が profile 0）。今の VRChat では、別アカウントの台は配置のたびのリロードで別々のインスタンスへ分かれてしまう（スパイクのケース5）。別アカウントの台は手順9-3の Relaunch で使う
- 動いている台が `VrchatClientCount` より少ないとき（1台を手で閉じた、など）は、残っている台に配置するだけで、足りない台は起動しない（足した台は、リロードした台と別のインスタンスに入ってしまうため）。Bridge のログに台数の警告が、変わったときに1度だけ出る。N 台にそろえ直すには、VRChat を全部閉じてから次のビルドを送る
- `--watch-worlds` 無しの VRChat が混ざっていても、`--watch-worlds` 付きの台があれば配置は成功する。無しの台はリロードされない（ログに警告が出る）
- `VrchatMode: VR` では、HMD を使うのは1台目だけで、2台目以降はデスクトップで起動する
- 全台が、前回終了したときと同じ位置・大きさで重なって開く。台を切り替えるには、ウィンドウ表示の Moonlight 越しに Mac で **Option+Tab** を押す（Option を押したままにすると Windows の Alt+Tab の一覧が出る）。VRChat がカーソルをウィンドウに閉じ込めた状態からそのまま効き、切り替えたあとはクリックもキー入力もその台に入る（2026-10-17 に手で確認）。マウスだけでは切り替えられない。全画面表示の Moonlight と Cmd+Tab では未確認
- 2台目以降は VRCNP の名前付きパイプを作れない（ログに `VRCNP: Failed to create server` が出る）。名前付きパイプで VRChat とやりとりする外部ツールは、1台目とだけつながる
- 負荷: 4台でワーキングセットの合計が約10.8GB（1台あたり2.6〜2.9GB）だった。2台目以降のログに API のレート制限の警告（`you are doing it too quickly`）が出るが、入室には影響しなかった。重いワールドやメモリの少ないマシンでは台数を減らす
- Unity の Preflight は、1台目（`--watch-worlds` 付きの台のうち最も早く起動した台）の状態だけを表示する。台数は Bridge のログで確認する
- Log Viewer は1台目のログを表示する（手順11）
- 手動で起動したい場合は、`AutoLaunchVrchat: false` のまま `start-vrchat-dev.ps1 -Clients 2` などで起動しても、配置で全台がリロードする。ただしスクリプトは前の台のログファイルを待たないので、CPU の負荷が高いと台どうしのログが混ざりやすく、1台目のログが混ざると Log Viewer に表示できない（手順11）

### 9-2. Logging を「完全」にする

ゲーム内の Settings > Debug > Logging を「完全」（Full）にしておくことを勧める。None では output_log が空になり、「エラーのみ」では入室などの行が出ないので、Log Viewer がほぼ空になる。この設定は profile ごとではなく全 profile 共通で、`HKCU\Software\VRChat\VRChat` の `LOGGING_ENABLED_h120798204`（オフ = -1、エラーのみ = 0、完全 = 1）に、変えた時点で保存される。Bridge の配置や準備待ちは output_log を使わないので、Logging の設定には左右されない。

**注意**: 「完全」にすると、ログに出る自分の表示名・ユーザー ID・訪れたワールド・同じインスタンスにいた他プレイヤーの表示名などが増え、Bridge はその直近200行を共有の `logs\vrchat-latest.log` に書き出す（手順11）。共有に読み取り権限を持つ人はこれを読める。検証が終わったら、次の順で戻す。

1. ゲーム内の Settings > Debug > Logging を元の値（「エラーのみ」など）に戻す（全 profile に効く）
2. VRChat を全部閉じる。作業が終わるまでビルドを送らない。手で起動した Bridge があれば閉じる
3. Bridge を止める。動いたままだと、読み込み済みのログを、消したあとの共有へ書き直すことがある。U の PowerShell 7 で実行し、エラーが出たら次へ進まない:
   ```powershell
   $ErrorActionPreference = 'Stop'
   Disable-ScheduledTask -TaskName 'VRCRemoteTestBridge' -TaskPath '\' | Out-Null
   Stop-ScheduledTask -TaskName 'VRCRemoteTestBridge' -TaskPath '\'
   Get-Process -Name 'VRCRemoteTest.Bridge' -ErrorAction SilentlyContinue | Wait-Process -Timeout 15
   ```
4. 元のログを消す:
   ```powershell
   Get-ChildItem -LiteralPath "$env:USERPROFILE\AppData\LocalLow\VRChat\VRChat" -Filter 'output_log_*.txt' -File |
       ForEach-Object { Remove-Item -LiteralPath $_.FullName }
   ```
5. 共有の写しと、書き込み途中のファイルを消す:
   ```powershell
   foreach ($path in 'C:\VRCRemoteTest\logs\vrchat-latest.log', 'C:\VRCRemoteTest\logs\vrchat-latest.log.tmp') {
       if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
   }
   ```
6. Bridge を有効に戻して起動する。元のログが無ければ共有の写しは作られず、次に VRChat がログを作ると公開が再開する:
   ```powershell
   Enable-ScheduledTask -TaskName 'VRCRemoteTestBridge' -TaskPath '\' | Out-Null
   Start-ScheduledTask -TaskName 'VRCRemoteTestBridge' -TaskPath '\'
   ```

### 9-3. 別アカウントの複数台（`VrchatClientMode: Relaunch`）

別々の VRChat アカウントの台を同じインスタンスに入れたいとき（プレイヤー間の同期の確認など）に使う。今の VRChat では、別アカウントの台を `--watch-worlds` でリロードさせると、アカウントごとに別のインスタンスに分かれてしまう（`docs/validation/multi-client-spike.md` のケース5）。全台を終了して `--url` で起動し直すとそろう（ケース4、ゲート2）。そこで Relaunch では、Bridge が配置のたびに、自分が起動した台を終了させてからワールドを配置し、profile ごとに起動し直す。Bridge が自分で起動したと確かめられた台しか終了させないので、明示的に選んだときだけ動く（既定は手順9-1の `Reload`）。

`config.json`（手順9の例に2行足し、`VrchatClientCount` は消す）:

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
    "VrchatClientMode": "Relaunch",
    "VrchatProfiles": [0, 1]
  }
}
```

- `VrchatClientMode` は `Reload`（既定）か `Relaunch`（大文字小文字は不問）
- `VrchatProfiles` は台ごとの VRChat の profile 番号（0〜99、1〜4件、重複なし）。並べた順に起動し、件数が台数になる。`VrchatClientCount` を書くと起動時の検証でエラーになる。`Reload` のまま `VrchatProfiles` を書いてもエラーになる
- `AutoLaunchVrchat: true` が必要
- `VrchatStartupSettleDelaySeconds` と `VrchatStartupTimeoutSeconds` は使わない（Relaunch では準備待ちをしない）
- `VrchatMode: VR` では、HMD を使うのは1台目だけ（手順9-1と同じ）

**準備（profile ごとに1回）**: 各 profile に、使うアカウントで一度ログインしておく。Bridge はアカウントの資格情報を扱わない。ビルドを送らずに、profile ごとに1台だけ起動してログインし、閉じる（手順8と同じく Windows のローカルにあるスクリプトを使う）:

```powershell
& (Join-Path $scripts 'start-vrchat-dev.ps1') -Profiles 0
# ログインして VRChat を閉じてから
& (Join-Path $scripts 'start-vrchat-dev.ps1') -Profiles 1
```

ゲーム内の Logging の設定は profile ごとではなく全 profile 共通（手順9-2）。

**配置のたびに Bridge がすること**:

1. 同じセッションの VRChat を調べ、Bridge の記録と照らして、Bridge が起動したと確かめられた台だけを対象にする。それ以外の VRChat（手で起動した台、記録の無い台、状態を確かめられない台）が1台でもあれば、**何も終了させずに**`VRCHAT_UNMANAGED_RUNNING`でビルドが失敗する
2. 対象の台にウィンドウを閉じるよう求める（正常終了）。ウィンドウがまだ無い台（起動から1秒ほどの間）には、ウィンドウができてから求め直す。10秒たっても残っている台は強制終了する（強制終了しても次の起動・ログイン・ウィンドウの位置・設定に影響は無かった。スパイクのゲート3）。20秒以内に全台の終了を確かめられなければ、配置せずに`VRCHAT_START_FAILED`
3. もう一度 VRChat を調べ、その間に起動された VRChat があれば、配置せずに`VRCHAT_UNMANAGED_RUNNING`
4. ワールドを配置し、`VrchatProfiles` の順に `--url` と `--profile=<番号>` を付けて起動する（`--watch-worlds` は付けない）。間隔は手順9-1と同じく3秒以上で、前の台のログファイルができるのを待つ（手順11）。全台が同じインスタンスに入る
5. 起動した台は、1台起動するごとに `%LOCALAPPDATA%\VRCRemoteTest\state\managed-clients.json` に記録する（PID、プロセスの作成時刻、セッション、profile、起動の順番）。次の配置で終了させるのは、PID・作成時刻（100ナノ秒の精度で比べる）・セッション・プロセス名 `VRChat` がすべて記録と一致する台だけ。PID が別のプロセスに使い回されていても、作成時刻が違うので終了させない。名前やログファイルだけで Bridge の台とみなすことはない
6. 2台目以降を起動する前にも VRChat を調べ直し、Bridge が起動していない VRChat が現れていれば、それ以上起動せずに`VRCHAT_UNMANAGED_RUNNING`（メッセージに「Launched 1 of 2 ...」のように何台起動したかが出る）。現れた台は終了させない。外から VRChat を起動されるのを防ぐことはできないので、これは「見つけたら止める」までの保証

- 配置のたびに全台がログインし直し、ワールドを読み込み直す。スパイクでは、終了に1.5〜3.8秒、起動から2台の入室まで9〜15秒だった
- 結果（`deployed`）は、全台のプロセスを起動できた時点で書かれる（入室までは確かめない）
- **Unity の Preflight（v0.3.0・v0.4.0）は、Relaunch で正常に動いているときも `VRChat is running without --watch-worlds` の警告を出す**。Relaunch では `--watch-worlds` を付けないので想定どおりで、ビルドは止まらない
- Log Viewer は、記録の中で最も早く起動した、まだ動いている台のログを表示する。起動し直しの間（台を閉じ始めてから、新しい1台目を起動して記録するまで。記録の台がもう終了していても同じ）は、前のセッションのログの代わりに「The Bridge is relaunching its VRChat clients ...」の1行になる（手順11）
- **Relaunch を使っている間は、VRChat を手で起動しない**。手で起動した VRChat があると、次のビルドは何も終了させずに失敗する。閉じてから送り直す
- Unity 側の Result Timeout: 終了の待ち（最大20秒）が加わる。結果が書かれるまでの時間は「前に並んでいるビルドの残り ＋ コピー・検証 ＋ 終了の待ち（最大20秒）＋ 起動の間隔（(台数−1)×3秒。ログファイルが遅いときは最大で 60秒＋(台数−1)×3秒）＋ 余裕」。手順9の目安どおり **90秒以上、続けて送るなら120秒から**にする
- 2台目以降が VRCNP の名前付きパイプを作れないこと、メモリの目安、台の切り替え方（Option+Tab）は手順9-1と同じ

**モードを切り替えるとき**: VRChat を全部手で閉じてから `config.json` を書き換え、Bridge を再起動する（`config.json` は Bridge の起動時にしか読まない。Task Scheduler で動かしているなら `Stop-ScheduledTask` と `Start-ScheduledTask`）。動いている台を別のモードへ移すことはしない。Reload の台を残したまま Relaunch にすると、それらは手で起動した台と同じ扱いになって止まる。Relaunch の台を残したまま Reload にすると、`--watch-worlds` 無しの台として `VRCHAT_WATCH_WORLDS_MISSING` になる。Relaunch のまま Bridge だけを再起動した場合は、記録と照合できる台をそのまま引き継ぐ。

**うまくいかないとき（復旧手順）**:

| 状況 | 原因と対処 |
|------|------|
| `VRCHAT_UNMANAGED_RUNNING` で失敗する | Bridge が起動したと確かめられない VRChat が動いている。何も終了・配置していない。Bridge のログの `VRChat is running that the Bridge did not launch or could not check (...)` に PID が出るので、その VRChat を手で閉じてから送り直す。Bridge が起動した台でも、記録する前に Bridge が止まった台や、記録を消したあとの台はこの扱いになる |
| Bridge が台を起動している途中で止まった（強制終了、PC の再起動など） | 記録済みの台は、次の配置で終了・起動し直される。止まったビルドの結果は書かれず、Unity は `RESULT_TIMEOUT` になる（そのビルドは処理済みになるので、Unity でビルドし直して送る）。起動したが記録する前に止まった台は、次の配置で `VRCHAT_UNMANAGED_RUNNING` になるので、手で閉じる |
| Bridge が台を終了させている途中で止まった | 配置も起動もしていない。Bridge を起動し直すと、そのビルドを最初（台の確認）からやり直す |
| `VRCHAT_START_FAILED` で、メッセージが `Could not close ...` | Bridge が起動した台を20秒以内に終了させられなかった（応答なし、権限が変わった、など）。配置はしていない。その台をタスクマネージャーなどで終了させてから送り直す |
| `UNKNOWN_ERROR` で、メッセージに `managed-clients.json` が出る | 記録を読めない（`could not be read`）・更新できない（`could not update its record`）・新しい台を記録できない（`could not be recorded`。その台までは起動している）。下の「記録の消し方」で記録を消す |
| Bridge のログに `Ignoring ...managed-clients.json: ...` が出る | 記録が壊れているか、知らない形式。どの台も Bridge が起動した台とみなさないので、VRChat が動いていれば `VRCHAT_UNMANAGED_RUNNING` になる。VRChat を全部手で閉じてから送れば、正しい記録に書き直される |

記録の消し方: 消すと、動いている台を Bridge が自分の台と認めなくなるので、**先に VRChat を全部閉じる**。そのあと Bridge を止め（手順9-2の3.）、`%LOCALAPPDATA%\VRCRemoteTest\state\managed-clients.json` と、あれば `managed-clients.json.tmp` を消して、Bridge を起動し直す。ファイルが無いのは初回として正常に扱われる。

---

## 10. （任意）Moonlight連携

Moonlight（NVIDIA GameStream/Sunshine互換のリモートデスクトップクライアント）でWindows実機のVRChat画面をmacOS側から見ている場合、Unity側の `VRC Remote Test` ウィンドウから直接Moonlightを起動・フォーカスできる。

- **`[Open Moonlight]`ボタン**（`DrawActionButtons`内）: macOS側にインストール済みのMoonlightアプリを`open -a`で起動（既に起動中なら前面に呼び出す）。Windows側の設定は不要 — この機能は完全にmacOS側で完結する
- **Settings foldoutの `Moonlight Application Name`**: `open -a`に渡すアプリ名。通常はデフォルトの`Moonlight`のままでよいが、App Store版など名称が異なるインストールの場合はここで変更する
- **Settings foldoutの `Focus Moonlight after deploy`**（デフォルトOFF）: 有効にすると、`Remote Build & Test`または`Deploy Last Build`が成功した直後に自動でMoonlightを前面に呼び出す。ビルドがVRChat側にリロードされる様子をすぐ確認したい場合に有効化する
- Moonlight側のホスト接続設定（Sunshine側のペアリング等）は本ツールの範囲外。あらかじめMoonlightで対象のWindowsマシンに接続済みであることが前提

## 11. （任意）VRChat Log Viewer

`RemoteBuildCommand`が使う同じSMB共有経由で、BridgeがVRChatの`output_log_*.txt`（最新200行、直近ウィンドウのスナップショット）を5秒間隔で配信する。Windows側の追加設定は不要 — `VrchatWorldsDirectory`が正しく設定済みであれば自動的に有効になる。Unity側の `VRC Remote Test` ウィンドウの `VRChat Log` foldoutから、カテゴリフィルタ（All/Error/Exception/Udon/Shader/Warning）付きで閲覧できる。表示専用で、readiness判定やビルドの成否には一切影響しない。

VRChat が動いている間は、最新のファイルではなく1台目（`--watch-worlds` 付きの台のうち最も早く起動した台。Relaunch（手順9-3）では、Bridge の記録の中で最も早く起動した、まだ動いている台）のログを表示する。output_log には PID が出ないが、VRChat は自分の output_log を開いたままにしている。そこで Bridge は、更新のたびに Windows の Restart Manager で「どのプロセスがそのファイルを開いているか」を調べ、VRChat のうち1台目だけが開いているファイルを1台目のログにする。管理者権限は要らない。Restart Manager で決まらないときは、今までどおりファイル名の時刻と台の起動時刻で対応付ける。選んだファイルは、その台が動いている間は表示し続ける。表示は次のとおり。Logging の設定は手順9-2を参照。

- 1台目のログファイルがまだ無いときは、「The first VRChat client (PID ...) has not created its output_log_*.txt file yet ...」という1行を表示する。起動直後の最初の更新はたいていこの1行になる（ファイルができるのは、負荷が無いときは起動の約1秒後。CPU の使用率が100%近いと、1〜2分かかったこともある）。ファイルができれば、次の更新（5秒以内）で1台目のログに切り替わる。起動から1分以上たってファイルができても、1台目だけが開いていれば表示できる。この1行が、前のセッション（閉じた台）のログの表示を置き換える
- 1台目のログにほかの VRChat の台も書いているとき（ログを書き始めたのが同じ秒の台は、1つのファイルに書く）は、ログの代わりに「... is or was also open by other VRChat clients ...」という1行を表示する。Bridge は、2台以上の VRChat が開いているのを一度でも見たファイルを、ほかの台が終了したあとも表示しない
- 1台目のログを特定できないときは、ログの代わりに「Could not match an output_log_*.txt file to the first VRChat client ...」という1行を表示する。Restart Manager で決まらず（下を参照）、ファイル名でも対応付けられないとき（同じ秒に起動した台がある、起動から1分以上たってログができた、ログを作ったあとに時計やタイムゾーンが変わった、など）にこうなる。別の台のログを1台目のものとして表示することはしない
- Relaunch（手順9-3）で台を起動し直すときは、Bridge が台を閉じ始めてから（記録の台がもう終了していれば、閉じる前の確認を終えてから）、新しい1台目を起動して記録するまで、ログの代わりに「The Bridge is relaunching its VRChat clients with the new build (VrchatClientMode: Relaunch): ...」という1行を表示する。閉じている台のログも、全台を閉じたあとの最新のファイルも、前のセッションのログだから。記録の台が終了していても、最新のファイルはその台のログなので同じく出す（終了の途中で Bridge が止まり、送ってあった終了の要求で台が終わってから、Bridge がそのビルドをやり直したとき。手で閉じたあとの配置も同じ）。この1行は閉じ始めたときにすぐ書き、1台目を記録するとすぐログを読み直す（5秒ごとの更新を待たない）。そのあとは上のとおりで、たいてい「has not created its output_log_*.txt file yet」の1行を経て、1台目のログになる。閉じられない台があった、手で起動した台が現れた、配置や起動に失敗した、などで止まったときは、その時点でこの1行をやめ、動いている台のログ（1台も動いていなければ最新のファイル）に戻る。記録が空の配置（初回や、記録を消したあと）では出さない。手で起動した台があって何も閉じずに止まる配置でも出さない
- VRChat が1台も動いていないときは、今までどおり最新のファイル（前回のセッションのログ）を表示する（Relaunch の起動し直しの間を除く）。Bridge が1台目を起動してから次の更新（約5秒後）までは、このファイルが残る（Reload のコールドスタートと、Relaunch で記録が空の配置。どちらも VRChat が動いていない状態から始まるので、セッションの始めに1度だけ）

ファイルを選んだときは、Bridge のログに `Showing ... as the log of VRChat client <PID> (the only VRChat process that has it open).` が出る。ファイル名の時刻で選んだときは、括弧の中が `matched by the time in its name` になる。

- ファイルを開いているプロセスのうち、VRChat 以外（Bridge 自身、エディタ、ウイルス対策ソフトなど）は数えない。VRChat かどうかは、プロセスのイメージ名（`VRChat.exe`）で見る
- 次のときは Restart Manager では決まらないので、ファイル名の時刻で対応付ける。Restart Manager が答えない（Bridge のログに `Could not ask Windows Restart Manager ...` の警告が1度出て、答えるようになると `Windows Restart Manager answers again` が出る）、1台目がどのファイルも開いていない、ファイルを開いているプロセスを確かめられない（ちょうど終了した、など）。このときも、ほかの VRChat が開いているファイルは表示しない
- 表示中のファイルも更新のたびに確かめる。1台目だけが開いている別のファイルが見つかればそちらに切り替え、表示中のファイルをほかの VRChat が開いたら表示をやめる（Bridge のログに `No longer showing ...`）

VRChat はログファイルに、ログを書き始めた時刻（秒まで）の名前を付ける。実機では、ファイルができたのは、負荷が無いときは起動の約0.8秒後、CPU の使用率が100%近いときは11〜22秒後、別の日には1〜2分後だった。その間に次の台を起動すると、2台のログの名前が同じ秒になって1つのファイルに混ざったり、1台目のログの名前が2台目の起動より後の時刻になって名前では対応付けられなくなったりする。混ざったファイルは、どの方法でも台ごとに分けられない。そこで Bridge はコールドスタートで2台以上を起動するとき、前の台のログファイルができるのを待ってから次の台を起動する。

- 間隔は3秒以上で、さらに、前の台のログの名前の時刻から2秒たつまで待つ。負荷が無いときは、今までどおり3秒間隔になる
- 台ごとに、新しくできたログの名前と、起動からできるまでの秒数が、Bridge のログに `New output_log ...` の行で出る。待ったときは、その前に `Waiting for VRChat client 1/4 to create its output_log file ...` が出る
- 待つのは、1回のコールドスタートで合わせて60秒まで。過ぎると Bridge のログに `... has not created an output_log file within 60s ...` の警告を出し、残りの台を3秒間隔で起動する。このとき、残りの台どうしのログは1つのファイルに混ざることがある。混ざったファイルは表示しない。ただし Restart Manager が答えないときは、前の台を閉じていって残った台が1台目になると、その台のログとして表示されることがある
- ログの一覧を読めないとき（権限、ファイルが512個を超えている、など）は、待たずに3秒間隔で起動し、Bridge のログに警告を出す

この待ちは Bridge が起動する台（`AutoLaunchVrchat: true` のコールドスタートと、Relaunch の起動し直し）だけに効く。`start-vrchat-dev.ps1 -Clients N` は決まった間隔（`-LaunchIntervalSeconds`、既定3秒）で起動するので、CPU の負荷が高いと台どうしのログが混ざりやすく、1台目のログが混ざると Log Viewer にはメッセージの1行だけが出る。Restart Manager が答えないときはファイル名の時刻だけで対応付けるので、1台目のログを表示できなかったり、ログが起動の間隔を超えて遅れたときに、終了した台のログを次の台のものとして表示したりすることもありうる。負荷の高い状態で手で起動するなら、`-LaunchIntervalSeconds 60` のように間隔を延ばす。

VRChat のログには、自分の表示名やユーザー ID、訪れたワールドの情報、同じインスタンスにいた他プレイヤーの表示名が含まれる。共有に読み取り権限を持つ人はこれを読めるので、共有の資格情報は他人に渡さないこと。

---

## 12. （推奨）VRChat の OSC が LAN から届かないことの確認

VRChat の OSC（UDP 9000）には認証がない。LAN から届く状態だと、同じネットワークの誰でも VRChat を操作できる。VRC Remote Test 自体は OSC を使わないが、テスト用マシンとして一度確認しておく。**OSC を使わないなら、VRChat の Action Menu → Options → OSC で無効にしておくのが最も簡単**。

**待ち受けアドレスの確認**（VRChat の OSC を有効にした状態で）:

```powershell
Get-NetUDPEndpoint -LocalPort 9000 | ForEach-Object {
    $p = Get-Process -Id $_.OwningProcess
    [pscustomobject]@{ LocalAddress = $_.LocalAddress; PID = $_.OwningProcess; Process = $p.ProcessName }
}
```

`LocalAddress` が `127.0.0.1` / `::1` だけならループバック専用。`0.0.0.0` / `::` なら全インターフェースで待ち受けている。

**ループバック以外からの OSC を遮断**（管理者 PowerShell 7）。Block 規則は、VRChat のインストール時にできた通常の Allow 規則より優先される:

```powershell
$ranges = @('0.0.0.0-126.255.255.255', '128.0.0.0-255.255.255.255', '::/128', '::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff')
New-NetFirewallRule -Name 'VRCRemoteTest-OSC-Block-NonLoopback' -DisplayName 'VRCRemoteTest OSC block non-loopback' `
    -Direction Inbound -Action Block -Enabled True -Profile Any -Protocol UDP -LocalPort 9000 -RemoteAddress $ranges
Get-NetFirewallRule -Name 'VRCRemoteTest-OSC-Block-NonLoopback' | Get-NetFirewallAddressFilter
```

**確認**: VRChat の OSC Debug 表示を開いた状態で、

1. Windows 上（ループバック）から送ったメッセージが表示されること:

    ```powershell
    $packet = [System.Text.Encoding]::ASCII.GetBytes("/input/Jump`0,i`0`0") + [byte[]]@(0, 0, 0, 0)
    $client = [System.Net.Sockets.UdpClient]::new()
    [void]$client.Send($packet, $packet.Length, '127.0.0.1', 9000)
    $client.Dispose()
    ```

2. Mac から Windows の LAN アドレスへ送ったメッセージが**表示されない**こと（`192.168.x.x` は Windows の IP に置き換える。値 0 の Jump なので、届いても何も起きない）:

    ```bash
    python3 -c 'import socket,struct; m=b"/input/Jump\0,i\0\0"+struct.pack(">i",0); s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM); s.sendto(m,("192.168.x.x",9000)); print("sent")'
    ```

IPv6 の経路がある環境では、IPv6 アドレスでも同様に確認する（確認できなければ「IPv6 は未確認」と記録しておく）。

---

## 13. ディスク使用量の確認と掃除

Bridge は、成功したビルドの artifact を Worlds に `RetainBuilds` 件だけ残し、状態ディレクトリには manifest だけを残す。失敗したビルドの artifact も、新しいものから `RetainBuilds` 件だけ `state\failed` に残す。

一方、**共有へのアップロードの総量には強制的な上限がない**（`vrcremote` の資格情報があればディスクを埋められる）。一人開発の構成では受け入れた残余リスクとし、ときどき次で確認する:

```powershell
$state = "$env:LOCALAPPDATA\VRCRemoteTest\state"
'C:\VRCRemoteTest\incoming', "$state\processing", "$state\failed", "$state\archive" | ForEach-Object {
    $bytes = (Get-ChildItem -LiteralPath $_ -File -Recurse -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    '{0,-70} {1,10:N1} MB' -f $_, ($bytes / 1MB)
}
Get-PSDrive C | Select-Object Used, Free
```

- `incoming` に manifest の無い `.vrcw` や `.part` が残っている場合は、アップロードが途中で止まったもの。Bridge と Unity のビルドが動いていないときに削除してよい。
- `state\failed` の manifest、`state\archive` の古い manifest は削除してよい（archive の manifest は同じ buildId の再配置を防ぐためのもので、Unity は buildId を使い回さない）。
- `state\processing` に何か残っている場合は、Bridge のクラッシュの跡。Bridge の次回起動時に回復・掃除される。

---

## 14. 既存環境からの移行

v0.2 以前の手順で作った環境（共有ルートに Full 権限、`processing`・`archive`・`failed` が共有内）から移行する。**既存のツリーの ACL は修復せず、新しいルートを作り直す**。古い ACE や所有者を確実に消すため。

U の通常のセッションを「U」、管理者 PowerShell 7 を「管理者」と書く。作業中は Mac からのビルドを止めておく。

### 14-1. バックアップ（U）

```powershell
$ErrorActionPreference = 'Stop'
$backup = Join-Path $env:LOCALAPPDATA ("VRCRemoteTest\migration-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup | Out-Null
$task = Get-ScheduledTask -TaskName 'VRCRemoteTestBridge' -TaskPath '\'
Export-ScheduledTask -TaskName 'VRCRemoteTestBridge' -TaskPath '\' | Set-Content -LiteralPath "$backup\task.xml" -Encoding Unicode
Copy-Item -LiteralPath $task.Actions[0].Execute -Destination "$backup\VRCRemoteTest.Bridge.exe"
Copy-Item -LiteralPath "$env:LOCALAPPDATA\VRCRemoteTest\config.json" -Destination "$backup\config.json"
$backup   # 表示されたパスを控える
```

管理者で、共有の設定も保存する（`$backup` は上で控えたパス）:

```powershell
Get-SmbShare -Name 'VRCRemoteTest' | Select-Object Name, Path, SecurityDescriptor, EncryptData | Export-Clixml -LiteralPath "<控えたパス>\share.xml"
```

### 14-2. Bridge の停止（U → 管理者）

U で、タスクを**無効化してから**停止する（移行中に自動起動させないため）:

```powershell
Disable-ScheduledTask -TaskName 'VRCRemoteTestBridge' -TaskPath '\'
Stop-ScheduledTask -TaskName 'VRCRemoteTestBridge' -TaskPath '\'
```

管理者で、別のパスから手動起動された Bridge も残っていないことを確認する。何か表示されたら、パスを確認して `Stop-Process -Id <PID>` で止め、表示されなくなるまで先へ進まない:

```powershell
Get-CimInstance Win32_Process -Filter "Name = 'VRCRemoteTest.Bridge.exe'" | Select-Object ProcessId, ExecutablePath
```

### 14-3. 旧共有の削除と新しいルートの作成（Mac → 管理者）

Mac で共有をアンマウントしてから、管理者で:

```powershell
Remove-SmbShare -Name 'VRCRemoteTest' -Force
Rename-Item -LiteralPath 'C:\VRCRemoteTest' -NewName ('VRCRemoteTest.old-' + (Get-Date -Format 'yyyyMMdd'))
```

旧ルートはもう共有されない。この後、**手順2の vrcremote のグループ確認、手順3-1〜3-3** を新規セットアップと同じように行う（`vrcremote` は既存のものを使う）。旧ルートから processing などを取り込んではいけない。

### 14-4. 新しい Bridge の配置と有効化（Mac → U）

1. Mac で再マウントし、手順5の権限確認を行う。
2. 手順1（1-A 推奨、または 1-B）で新しい exe を用意し、U で手順6のインストーラーを実行する。**`-StartNow` は付けない**。タスクは無効のまま再登録される。
3. 確認ができたら、タスクを有効化して起動する:

    ```powershell
    Enable-ScheduledTask -TaskName 'VRCRemoteTestBridge' -TaskPath '\'
    Start-ScheduledTask -TaskName 'VRCRemoteTestBridge' -TaskPath '\'
    ```

4. Unity から `Remote Build & Test` を 1 回実行し、配置されることを確認する。
5. 問題がなければ、旧ルート（`C:\VRCRemoteTest.old-*`）とバックアップを削除してよい。Bridge のログに `Ignoring legacy ... inside the share` が出る場合は、共有内に旧 `processing`・`archive`・`failed` が残っているので削除する。

### 14-5. うまくいかなかったときの戻し方

1. U: `Disable-ScheduledTask` と `Stop-ScheduledTask` で Bridge を止める。
2. 管理者: 新しい共有を `Remove-SmbShare` で削除し、新しいルートを別名に退避して、旧ルートの名前を `C:\VRCRemoteTest` に戻す。保存した設定で共有を作り直す:

    ```powershell
    $s = Import-Clixml -LiteralPath "<控えたパス>\share.xml"
    New-SmbShare -Name $s.Name -Path $s.Path -SecurityDescriptor $s.SecurityDescriptor -EncryptData $s.EncryptData
    ```

3. U: バックアップの exe と config.json を元の場所に戻し、タスクを登録し直して有効化する:

    ```powershell
    Copy-Item -LiteralPath "<控えたパス>\VRCRemoteTest.Bridge.exe" -Destination "$env:LOCALAPPDATA\Programs\VRCRemoteTest\Bridge\VRCRemoteTest.Bridge.exe" -Force
    Copy-Item -LiteralPath "<控えたパス>\config.json" -Destination "$env:LOCALAPPDATA\VRCRemoteTest\config.json" -Force
    Register-ScheduledTask -TaskName 'VRCRemoteTestBridge' -TaskPath '\' -Xml (Get-Content -LiteralPath "<控えたパス>\task.xml" -Raw) -Force
    Enable-ScheduledTask -TaskName 'VRCRemoteTestBridge' -TaskPath '\'
    ```

戻した状態は、ハードニング前の構成（共有に Full 権限）であることに注意する。

---

## トラブルシューティング

| 症状 | 原因 | 対処 |
|------|------|------|
| `New-SmbShare`が`Windows System Error 5`で失敗 | 非管理者PowerShellで実行している | PowerShellを「管理者として実行」し直す |
| `Enable-NetFirewallRule -DisplayGroup "File and Printer Sharing"`が規則を見つけられない | 日本語版Windowsでは`DisplayGroup`名がローカライズされている | `Get-NetFirewallRule -Name "FPS-SMB-In-TCP"`という固定名で指定する |
| Unity の Preflight が `Remote Share: Not reachable` のまま（共有はマウントできている） | `incoming` が無い、または `vrcremote` に `incoming` への書き込み権限が無い。ルートは読み取り専用なので Mac からは `incoming` を作れない | Windows 側で手順3-2のとおり `incoming` を作り、`icacls C:\VRCRemoteTest\incoming` で `vrcremote` の `(RX,W)` と `(OI)(CI)(IO)(M)` を確認する |
| `results` やルートに Mac から書き込めない | 想定どおりの動作（読み取り専用） | 書き込みが必要なのは `incoming` と `_bridge-drop` だけ |
| `incoming` へのファイル作成/更新が失敗する（読み取りは可能） | 共有の権限（SMB層）だけで、NTFS層のACLが未設定 | 手順3-2の `incoming` への `(RX,W)` と `(OI)(CI)(IO)(M)` を確認する |
| SMBマウント時のパスワード認証が繰り返し失敗する | PINでログインしているアカウントには、SMBが要求する実パスワードが設定されていない/不明 | 手順2の専用アカウント `vrcremote` の資格情報でマウントする |
| `.ps1`スクリプトの実行が"このシステムではスクリプトの実行が無効になっているため"のエラーで拒否される | 実行ポリシー、またはダウンロード/コピーされたファイルの「ブロック」属性 | `Unblock-File`を実行。それでも失敗する場合は`Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned` |
| `start-vrchat-dev.ps1`が構文エラー等で動かない | Windows標準のPowerShell 5.1で実行している（スクリプトは7.0以降が必要） | `pwsh`（PowerShell 7）から実行する |
| Bridgeが起動直後に`Configuration error`で終了する | `config.json`の`VrchatWorldsDirectory`が存在しない、またはパスが誤っている | VRChatを一度起動してWorldsディレクトリを生成させてから、正確なパスを`config.json`に設定する |
| `install-bridge.ps1`が「SHA-256 が一致しません」で失敗する | コピー元の exe が、ハッシュを取ったものと違う（差し替え・別ビルド・コピー途中） | 1-A の場合: ダウンロードからやり直し、来歴の検証とハッシュの確認を通す。**拒否されたファイルから計算したハッシュを期待値に使ってはいけない。** 1-B の場合: 手順1-B をやり直す。どちらの場合も、配置済みの実行体とタスクは変更されていない |
| `gh attestation verify` が失敗する | `gh` が古い（2.102.0 未満）、`gh auth login` をしていない、`$tag` と `$commit` が合っていない、またはファイルが Release のビルドと違う | `gh --version` と `gh auth status` を確認し、手順1-A をやり直す。それでも失敗するファイルはインストールしない |
| `install-bridge.ps1`が「配置先に…書き込み権限があります」「%LOCALAPPDATA% の下に配置してください」で失敗する | `-ExePath` に、他のユーザーが書き込める場所やプロファイル外を指定している | `-ExePath` を省略して既定の配置先を使う |
| `install-bridge.ps1`が「稼働中の Bridge…を先に停止してください」で失敗する | 置き換えには実行中の exe の停止が必要（自動停止はしない） | `Stop-ScheduledTask -TaskName 'VRCRemoteTestBridge'` で停止してから再実行する |
| `install-bridge.ps1`登録後、ログオンしてもBridgeが起動していない | タスクが無効化されたまま、または`config.json`が無効 | `Get-ScheduledTask -TaskName 'VRCRemoteTestBridge'` の `State` を確認し、`Disabled` なら `Enable-ScheduledTask`。`Get-ScheduledTaskInfo -TaskName 'VRCRemoteTestBridge' -TaskPath '\' \| Select-Object LastRunTime, LastTaskResult`で終了コードを確認し、`%LOCALAPPDATA%\VRCRemoteTest\logs\bridge-*.log`の最新ログを確認する |
| `install-bridge.ps1`が「本スクリプトが管理するタスクとは判別できません」で失敗する | 同名の無関係なタスクが既に存在する | `Get-ScheduledTask -TaskName 'VRCRemoteTestBridge' -TaskPath '\'`で内容を確認し、無関係であれば`-TaskName`で別名を指定するか、手動で削除してから再実行する |
| Bridge が `Another VRC Remote Test Bridge is already running` と出して終了する（このメッセージは `logs\bridge-<日付>_001.log` など、末尾に番号の付いた別名のログファイルにある） | 別の Bridge がすでに動いている（手動で起動したものと Task Scheduler のものが重なった、など） | `Get-Process VRCRemoteTest.Bridge` で確認し、1つだけにする。動いている Bridge が無いのにこのメッセージが出るなら、`%LOCALAPPDATA%\VRCRemoteTest\state\bridge.lock` を開いているプロセスを確かめる |
| ビルドが `VRCHAT_UNMANAGED_RUNNING` で失敗する | Bridge が状態を確かめられない VRChat が動いている（手順9）。何も起動・終了していない | その VRChat を手で閉じてから、もう一度ビルドを送る。Bridge のログに理由（プロセスの一覧が取れない、確かめられない台の数）が出る |
| `VrchatClientCount` を2以上にしたのに、1台にしか配置されない・台が増えない | 動いている台があると、足りない台は起動しない（手順9-1） | VRChat を全部閉じてから次のビルドを送る。Bridge のログの `VRChat clients changed` で台数を確認できる |
| 別アカウントの台が、配置のたびに別々のインスタンスに分かれる | `Reload`（既定）では、別アカウントの台は `--watch-worlds` のリロードで分かれる（VRChat の不具合） | 手順9-3の `VrchatClientMode: Relaunch` にする |
| Relaunch で、Unity の Preflight に `VRChat is running without --watch-worlds` が出る | Relaunch では `--watch-worlds` を付けない（手順9-3） | 想定どおり。ビルドは止まらない |
| Relaunch で、ビルドが `VRCHAT_UNMANAGED_RUNNING`・`VRCHAT_START_FAILED`・`UNKNOWN_ERROR` で失敗する | 手で起動した VRChat がある、台を終了させられない、記録を読み書きできない、など | 手順9-3の「うまくいかないとき」を見る |
| Bridge が起動直後に `VrchatProfiles ...` `VrchatClientCount must not be set ...` などの `Configuration error` で終了する | Relaunch の設定の組み合わせが合っていない（手順9-3） | `VrchatClientMode`・`VrchatProfiles`・`AutoLaunchVrchat` を手順9-3のとおりにし、`VrchatClientCount` を消す |
| Log Viewer に `Could not match an output_log_*.txt file ...` が出る | 1台目のログを特定できない（手順11）。Bridge のログに `Could not ask Windows Restart Manager ...` があれば、ファイルを開いているプロセスを調べられず、ファイル名の時刻だけで対応付けている | 台を全部閉じて、Bridge に起動させ直す（前の台のログができてから次を起動するので、ファイルが分かれる）。手で起動するなら、1秒以上あけて1台ずつ起動する |
| Log Viewer に `... is or was also open by other VRChat clients ...` が出る | 1台目のログに、ほかの台も書いている（ログを書き始めたのが同じ秒だった。手順11） | 台を全部閉じて、Bridge に起動させ直す。CPU の負荷が高いときに起きやすいので、負荷を下げてから起動する |
| Bridge のログに `Ignoring legacy ... inside the share` が出る | 共有内に旧構成の `processing`・`archive`・`failed` が残っている | 手順14に従って移行し、旧ディレクトリを削除する |

---

## 関連ドキュメント

- `bridge/README.md` — Bridge本体のビルド・設定ファイル・ステージング構造の詳細

以下は開発者向け（このツールのセットアップ・利用には不要）:

- `docs/validation/watch-worlds-spike.md` — `--watch-worlds`挙動の実機検証記録
- `docs/validation/multi-client-spike.md` — 複数クライアントの実機検証手順と記録
- `docs/sdk-api-notes.md` — Unity側SDK APIの調査ノート
- `VRC Remote Test — 実装仕様書.md` §16（Windows Share）、§44（Windows config）、§49（Security）、§63（Setup Flow）
