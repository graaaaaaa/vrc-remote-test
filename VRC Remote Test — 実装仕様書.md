# VRC Remote Test — 実装仕様書

**Status:** Draft for Implementation  
**Target:** macOS Unity → Windows VRChat Remote Local Test  
**Primary UI:** Unity Editor  
**Remote display/control:** Moonlight + Sunshine  
**Package type:** VPM-compatible Unity Editor package + Windows companion bridge  
**Working name:** `VRC Remote Test`

---

# 1. 目的

macOS上のUnityでVRChat Worldを開発し、Windowsマシン上の実VRChatクライアントへローカルWorld Buildを自動転送してテストできる開発環境を構築する。

最終的なユーザー操作は以下の1クリックとする。

```text
Mac / Unity

[ Remote Build & Test ]
          │
          ├─ VRChat SDKでWindows向け.vrcwをBuild
          │
          ├─ Build artifactを特定
          │
          ├─ SHA-256計算
          │
          ├─ Windowsへ転送
          │
          ├─ Windows Bridgeが検証
          │
          ├─ Windows VRChat Worldsへ配置
          │
          └─ VRChat --watch-worlds がreload
                     │
                     ▼
               Moonlightで確認
```

通常の開発はMacのClientSimで行い、

- DX11でのShader確認
- 実VRChat Client上でのUdon確認
- ClientSimとの差異確認
- Desktop Modeでの操作確認
- 必要に応じてWindows側HMDでVR確認

だけをWindows側VRChatで行う。

---

# 2. 最重要要件

## 2.1 UX

Unity上に以下のボタンを追加する。

```text
Remote Build & Test
```

これを押した後は、正常系ではWindowsを直接操作しなくても最新BuildがWindows VRChatへ反映されること。

正常系で必要な操作は**1クリックのみ**とする。

---

## 2.2 Windows側でUnityを使用しない

WindowsマシンにはUnity Projectを配置しない。

Windows側の役割は以下のみ。

```text
Windows
├─ VRChat
├─ Steam
├─ Sunshine
├─ VRC Remote Test Bridge
└─ local .vrcw storage
```

プロジェクト本体、Assets、LibraryなどをWindowsへ同期しない。

転送対象は原則として生成済み `.vrcw` のみとする。

---

## 2.3 SDK内部APIへ依存しない

VRChat SDKとの統合には**Public SDK APIのみ使用すること**。

禁止：

```text
- private/internal classへのreflection
- SDK内部フィールドへのreflection
- VRCSdkControlPanelの非Public implementationへの依存
- SDKソースの改変
- Harmony等によるpatch
```

SDK Public APIの実際のinterfaceはインストール済みSDKから確認すること。

特に実装開始時に以下を読むこと。

```text
Packages/VRChat SDK - Worlds/Editor/VRCSDK/SDK3/Public SDK API

Packages/VRChat SDK - Base/
Editor/VRCSDK/Dependencies/VRChat/Public SDK API
```

Public APIの型・メソッド名を推測して実装してはならない。

---

# 3. 非目標

v1では以下を実装対象外とする。

- VRChat OnlineへのWorld Upload
- Blueprint管理
- Quest/AndroidへのRemote Build
- Windows Unityの遠隔操作
- SteamVR映像のMacへのVR転送
- 複数Windowsマシンへの同時deploy
- 複数VRChat Clientの自動network test
- VRChat Clientそのものへのmod
- Easy Anti-Cheat回避
- Wine/CrossOver対応
- VRChat内部IPCの解析
- VRC Quick Launcherとの直接統合

将来的に拡張可能な構造にはしておく。

---

# 4. 想定環境

## macOS

```text
macOS
├─ Unity
├─ VRChat Worlds SDK
├─ ALCOM
├─ ClientSim
├─ VRC Remote Test Unity Package
└─ Moonlight
```

---

## Windows 11

```text
Windows 11
├─ Steam
├─ VRChat
├─ Sunshine
└─ VRC Remote Test Bridge
```

MacとWindowsは同一LAN内に存在することを前提とする。

---

# 5. 全体アーキテクチャ

```text
┌──────────────────── macOS ────────────────────┐
│                                               │
│ Unity                                         │
│                                               │
│ VRC Remote Test                               │
│ ├─ UI                                        │
│ ├─ BuildCoordinator                          │
│ ├─ VrcSdkAdapter                             │
│ ├─ BuildArtifactResolver                     │
│ ├─ HashCalculator                            │
│ ├─ RemoteTransport                           │
│ ├─ BridgeStatusClient                        │
│ └─ MoonlightIntegration                      │
│                                               │
└───────────────────────┬───────────────────────┘
                        │
                        │ LAN / SMB
                        │
                        ▼
┌────────────────── Windows 11 ─────────────────┐
│                                               │
│ VRC Remote Test Bridge                        │
│ ├─ IncomingBuildWatcher                       │
│ ├─ ManifestValidator                          │
│ ├─ ArtifactVerifier                          │
│ ├─ VrchatWorldDeployer                       │
│ ├─ VrchatProcessMonitor                      │
│ ├─ VrchatLogReader                           │
│ └─ StatusWriter                              │
│                                               │
│                ↓                              │
│                                               │
│ VRChat local Worlds                          │
│                ↓                              │
│ VRChat.exe --watch-worlds                    │
│                ↓                              │
│ Sunshine                                     │
└───────────────────────┬───────────────────────┘
                        │
                        ▼
                    Moonlight
```

---

# 6. Repository構成

monorepoとする。

```text
vrc-remote-test/
│
├─ README.md
├─ SPEC.md
├─ LICENSE
├─ .gitignore
│
├─ package/
│  ├─ package.json
│  ├─ CHANGELOG.md
│  ├─ README.md
│  │
│  └─ Editor/
│     ├─ VRCRemoteTest.Editor.asmdef
│     │
│     ├─ SDK/
│     │  ├─ IVrcSdkBuildAdapter.cs
│     │  └─ VrcSdkBuildAdapter.cs
│     │
│     ├─ Build/
│     │  ├─ RemoteBuildCoordinator.cs
│     │  ├─ BuildArtifactResolver.cs
│     │  └─ BuildArtifact.cs
│     │
│     ├─ Transport/
│     │  ├─ IRemoteTransport.cs
│     │  └─ SmbRemoteTransport.cs
│     │
│     ├─ Protocol/
│     │  ├─ BuildManifest.cs
│     │  ├─ BuildStatus.cs
│     │  └─ ProtocolConstants.cs
│     │
│     ├─ Settings/
│     │  └─ RemoteTestSettings.cs
│     │
│     ├─ UI/
│     │  └─ RemoteTestWindow.cs
│     │
│     ├─ Integration/
│     │  └─ MoonlightIntegration.cs
│     │
│     └─ Utility/
│        ├─ Sha256Calculator.cs
│        ├─ AtomicFile.cs
│        └─ PathUtility.cs
│
├─ bridge/
│  ├─ VRCRemoteTest.Bridge.sln
│  │
│  ├─ src/
│  │  └─ VRCRemoteTest.Bridge/
│  │     ├─ Program.cs
│  │     ├─ BridgeWorker.cs
│  │     ├─ Configuration/
│  │     ├─ Protocol/
│  │     ├─ Deployment/
│  │     ├─ VRChat/
│  │     └─ Logging/
│  │
│  └─ tests/
│     └─ VRCRemoteTest.Bridge.Tests/
│
├─ scripts/
│  ├─ install-bridge.ps1
│  ├─ uninstall-bridge.ps1
│  └─ start-vrchat-dev.ps1
│
└─ docs/
   ├─ setup-macos.md
   ├─ setup-windows.md
   ├─ troubleshooting.md
   └─ sdk-api-notes.md
```

---

# 7. Unity Package

Package Display Name:

```text
VRC Remote Test
```

C# namespace:

```csharp
VRCRemoteTest
```

開発中のpackage IDは仮に、

```text
com.local.vrc-remote-test
```

とする。

公開前に正式なreverse-domain package IDへ変更する。

---

# 8. SDK Compatibility Layer

SDK依存コードを以下へ隔離する。

```text
SDK/VrcSdkBuildAdapter.cs
```

interface：

```csharp
public interface IVrcSdkBuildAdapter
{
    bool IsAvailable { get; }

    Task BuildWindowsWorldAsync(
        CancellationToken cancellationToken = default);
}
```

他のコードから、

```text
IVRCSdkWorldBuilderApi
VRCSdkControlPanel
```

等を直接参照してはならない。

SDK API変更時には、

```text
VrcSdkBuildAdapter
```

だけを変更すれば対応可能な構造とする。

---

# 9. Claude Codeが実装前に必ず行う調査

コードを書く前に現在のUnity Projectを確認する。

確認対象：

```text
ProjectSettings/ProjectVersion.txt
Packages/vpm-manifest.json
Packages/manifest.json
Packages/com.vrchat.*
```

Public SDK API内から以下を確認する。

```text
World Builder interface名

Build method
BuildAndTest method
Build Start event
Build End event
build result/event argument
artifact pathをPublic APIから取得できるか
```

結果を、

```text
docs/sdk-api-notes.md
```

へ記録する。

**Web上の古いコードをそのまま使用してはならない。**

現在インストールされているSDKソースを正とする。

---

# 10. Build方式

Primary Button：

```text
Remote Build & Test
```

このボタンからPublic SDK APIを使用して、

```text
StandaloneWindows64
```

向けWorld Buildを実行する。

重要：

```text
BuildAndTest
```

によってMac上でVRChat Clientを起動する設計にはしない。

Public APIに「Buildのみ」が存在する場合はそれを使用する。

SDKの現在のPublic API上でBuild-only APIの仕様が異なる場合は、

```text
VrcSdkBuildAdapter
```

内部で吸収する。

---

# 11. Build Preflight

Build前に以下を検査する。

### VRChat SDK

```text
SDK builderが取得可能
World projectである
SDK validationが実行可能
```

### Platform

```text
Windows / StandaloneWindows64向けbuildが可能
```

### Remote Bridge

```text
SMB shareが存在
書き込み可能
bridge heartbeatが生存
protocol versionが互換
```

### Local state

```text
別のRemote Buildが実行中でない
UnityがPlay Modeでない
```

Preflight失敗時にはWorld Build自体を開始しない。

---

# 12. Build Artifact Resolver

最重要コンポーネントの1つ。

Public SDK APIからartifact pathが取得できる場合はそれを第一優先する。

取得できない場合のみfilesystem resolverを使用する。

## Build開始時

Build開始直前に候補ディレクトリの状態をsnapshotする。

保存：

```text
filename
size
mtime
```

---

## Build終了時

`.vrcw`を検索する。

候補ディレクトリは以下の優先順位。

```text
1. user configured build directory
2. SDK Public APIから判明したbuild directory
3. macOS VRChat local Worlds default candidate
```

macOSのfallback candidate：

```text
~/Library/Application Support/VRChat/VRChat/Worlds
```

ただし、このパスを唯一の前提としてhard-codeしてはならない。

---

## Artifact判定

Build開始snapshotとの差分から、

```text
new file
mtime changed
size changed
```

を検出する。

候補が1つなら採用。

複数存在する場合は、

```text
Build開始時刻以降
StandaloneWindows64
.vrcw
最新mtime
```

を使用して絞る。

ambiguityが残った場合は勝手に送信せず、

```text
ARTIFACT_AMBIGUOUS
```

として失敗させる。

---

# 13. Build ID

Buildごとに一意なIDを生成する。

形式：

```text
yyyyMMddTHHmmssfffZ-xxxxxxxx
```

例：

```text
20260901T112522481Z-a91f02cc
```

後半はrandom 32-bit hexadecimal。

---

# 14. SHA-256

転送前に`.vrcw`全体のSHA-256を計算する。

manifestへ、

```text
fileSize
sha256
```

を保存する。

Windows側でも再計算して一致確認する。

一致しないbuildは絶対にdeployしない。

---

# 15. Network Transport

v1のtransportは、

```text
SMB
```

とする。

ただし抽象化する。

```csharp
public interface IRemoteTransport
{
    bool IsAvailable { get; }

    Task UploadBuildAsync(
        BuildArtifact artifact,
        BuildManifest manifest,
        CancellationToken cancellationToken);
}
```

将来的に、

```text
SFTP
HTTP
SSH
```

へ変更可能とする。

---

# 16. Windows Share

Windows側に、

```text
C:\VRCRemoteTest
```

を作成する。

構造：

```text
C:\VRCRemoteTest              （SMB共有）
│
├─ incoming                   （Mac が書き込む）
├─ _bridge-drop               （Bridge 実行体の受け渡し。Mac が書き込む）
├─ results                    （Bridge が書き込み、Mac は読み取りのみ）
├─ status                     （同上）
└─ logs                       （同上）

%LOCALAPPDATA%\VRCRemoteTest\state   （Bridge 専用。共有しない）
│
├─ processing
├─ archive
└─ failed
```

processing・archive・failed は共有に置かない。SMB アカウントは共有内をいつでも書き換えられるため、Bridge が信頼する状態（claim 済み manifest、検証済み artifact、冪等性の記録）はその外に置く（2026-10 セキュリティレビュー S7/S15）。

共有の権限は Change、NTFS は SMB 専用アカウントに対して、ルートとその下を読み取り、`incoming`・`_bridge-drop` だけを変更可とする。手順は `docs/setup-windows.md`。

SMB share名：

```text
VRCRemoteTest
```

Mac側mount例：

```text
/Volumes/VRCRemoteTest
```

credentialsはmacOS Keychain / Windows SMB認証に任せる。

Unity Package内にpasswordを保存してはならない。

---

# 17. Atomic Upload Protocol

転送途中の`.vrcw`をBridgeが処理してはいけない。

Macはまず、

```text
incoming/<buildId>.vrcw.part
```

へ書く。

完了後、

```text
incoming/<buildId>.vrcw
```

へrename。

次に、

```text
incoming/<buildId>.ready.json.part
```

へmanifestを書く。

最後に、

```text
incoming/<buildId>.ready.json
```

へrenameする。

**`.ready.json`の出現をtransaction commitとして扱う。**

Windows Bridgeは`.vrcw`の出現自体では処理を開始しない。

---

# 18. Manifest

Protocol version 1。

例：

```json
{
  "protocolVersion": 1,
  "buildId": "20260901T112522481Z-a91f02cc",

  "project": {
    "name": "ShaderResearchWorld"
  },

  "scene": {
    "name": "ShaderLab"
  },

  "target": "StandaloneWindows64",

  "artifact": {
    "fileName": "20260901T112522481Z-a91f02cc.vrcw",
    "size": 48233421,
    "sha256": "..."
  },

  "createdAtUtc": "2026-09-01T11:25:22.481Z"
}
```

manifest内のpathをそのままfilesystem pathとして信用してはならない。

`fileName`はbasenameのみ許可。

以下を拒否する。

```text
..
/
\
:
absolute path
UNC path
```

---

# 19. Windows Bridge

.NETで実装する。

推奨：

```text
.NET 8
win-x64
self-contained
single-file publish
```

Windows側に.NET runtimeの事前インストールを要求しない。

---

# 20. BridgeをWindows Serviceにしない

Bridgeは**Windows user interactive session**上で実行する。

理由：

将来的に、

```text
VRChat起動
VRChat再起動
debug window
```

等を扱えるようにするため。

Windows Service Session 0からGUI Applicationを操作する構成にはしない。

---

# 21. Bridge自動起動

`install-bridge.ps1`でTask Schedulerへ登録する。

Trigger：

```text
At log on
```

設定：

```text
Run only when user is logged on
```

BridgeはWindowsログイン中のみ動作すればよい。

---

# 22. Bridge heartbeat

Bridgeは、

```text
status/bridge.json
```

を定期更新する。

例：

```json
{
  "protocolVersion": 1,
  "bridgeVersion": "0.1.0",
  "hostName": "WINDOWS-PC",
  "heartbeatUtc": "2026-09-01T11:25:25Z",
  "vrchatRunning": true
}
```

Unity側はheartbeatが古い場合、

```text
BRIDGE_OFFLINE
```

と判断する。

---

# 23. Incoming Build処理

Bridgeは`.ready.json`を検出したら、

```text
incoming
   ↓ 上限付きで読み取り（64 KiB）
state/processing/<name>.part
   ↓ 上書きしない rename
state/processing/<name>
```

とする。この rename をclaim operationとする。共有のファイルは Move しない（同じボリューム内の Move はアップロード元の ACL を引き継ぐため）。claim 後、共有の manifest は削除する。

artifact も同様に、`MaxArtifactSizeBytes` を実際に読んだバイト数で判定しながら `state/processing` へコピーし、そのコピーを検証して、同じコピーから deploy する。manifest だけで判定できる項目（protocolVersion、size、sha256 の書式）は、コピーの前に確認する。

その後、

1. JSON schema validation
2. protocolVersion確認
3. artifact存在確認
4. file size確認
5. SHA-256確認
6. duplicate build確認
7. VRChat状態確認
8. deploy

を実行する。

---

# 24. Idempotency

同一`buildId`を複数回処理してはならない。

Bridgeはprocessed build IDを記録する。

最低限、

```text
state/archive/<buildId>.ready.json
```

の存在で判定可能。archive には manifest だけを残し、artifact は残さない（deploy 済みのものが Worlds にあるため）。重複と判定したアップロードは、共有から消費する。

duplicateの場合：

```text
BUILD_ALREADY_PROCESSED
```

とし、安全に終了する。

---

# 25. Windows側deploy

設定値：

```text
vrchatWorldsDirectory
```

default candidate：

```text
%USERPROFILE%\AppData\LocalLow\VRChat\VRChat\Worlds
```

実際のdirectoryが存在しない場合は勝手に別場所を推測せずconfiguration errorとする。

---

# 26. Deploy filename

Windows Worldsへ配置するfilenameは一意にする。

例：

```text
vrc-remote-20260901T112522481Z-a91f02cc.vrcw
```

同じfilenameを上書きし続けない。

`--watch-worlds`に新規World Buildとして認識させやすくする。

---

# 27. Windows側atomic deploy

直接destinationへstreaming copyしない。

まず同じfilesystem上で、

```text
.vrc-remote-xxx.vrcw.tmp
```

へcopyする。

copy完了後、

```text
vrc-remote-xxx.vrcw
```

へatomic renameする。

---

# 28. VRChat launch configuration

開発用VRChatは以下を基本とする。

Desktop Test：

```text
--watch-worlds
--no-vr
--enable-debug-gui
--enable-sdk-log-levels
--enable-udon-debug-logging
```

VR Test：

```text
--watch-worlds
--enable-debug-gui
--enable-sdk-log-levels
--enable-udon-debug-logging
```

`--no-vr`のみ差分とする。

Bridgeが起動する場合（§30）は、先頭に次を加える。SDKのBuild & Test（`RunWorldTestDesktop`）と同じ形。

```text
--url=create?roomId=<乱数10桁>&hidden=true&name=BuildAndRun&url=file:///<配置先のフルパス>
```

- 配置先のフルパスは、Bridgeが`VrchatWorldsDirectory`と自分で付けた配置名（`vrc-remote-<buildId>.vrcw`）から作り、全体をパーセントエンコードする（`:` `\` 空白 `&` も含む）。manifestの値は引数に入れない（§49）。
- roomIdは起動のまとまりごとにBridgeが1つ作り、全台に同じ値を渡す。VRChatはroomIdをインスタンスIDに使わない。
- 既定（`VrchatClientMode: Reload`）では`--profile`は付けない。全台がprofile 0、つまり同じアカウントになる。
- `VrchatClientMode: Relaunch`（§32）では、`--watch-worlds`を付けず、末尾に`--profile=<n>`（`VrchatProfiles`の値、0〜99）を付ける。profileの値も設定ファイルで検証した整数だけで、manifestからは何も入れない。
- 複数台を起動するとき、VR Testでも HMD を使うのは1台目だけで、2台目以降には`--no-vr`を付ける。

---

# 29. VRChat起動状態

BridgeはVRChat processを監視する。

以下を取得可能にする。

```text
Running
Not Running
CommandLine
PID
StartTime
```

Windows側で可能ならcommand lineを確認し、

```text
--watch-worlds
```

がない場合は警告を返す。

```text
VRCHAT_WATCH_WORLDS_MISSING
```

数えるのは、Bridgeと同じ対話セッションのVRChatだけとする。セッションの確認は、コマンドラインや起動時刻を読むより先に行う。`--watch-worlds`の有無は、コマンドラインをWindowsの規則（Microsoft C ランタイムのargvの規則）で引数に分けてから完全一致で判定する（`--url`の値やパスの中の文字列には一致させない）。

コマンドラインは、1回の観測ごとに1回のWMIの問い合わせ（名前が`VRChat.exe`で、Bridgeと同じセッションのものに限る）でまとめて読み、各行の作成時刻をプロセスの起動時刻と照合する（1µs未満の差だけを同じプロセスとみなす）。合わない行は、列挙のあとにPIDが別のプロセスに使い回されたものとして扱い、一度だけ列挙し直す。WMIの問い合わせの失敗・時間切れ（結果の1行ごとに2秒）や、コマンドラインが返らない台は、`--watch-worlds`無しではなく「確かめられない台」として扱う。

複数台が動いているとき：

- `--watch-worlds`付きの台が1台以上あれば、配置はそれらを対象に成功する。無しの台はリロードされず、Bridgeのログで警告する。
- `--watch-worlds`無しの台だけなら`VRCHAT_WATCH_WORLDS_MISSING`。
- `status/vrchat-status.json`の形は1台のときと同じで、代表の1台（`--watch-worlds`付きのうち作成時刻が最も古い台）だけを書く。台数や、無しの台が混ざっていることはUnityには出ない。

---

# 30. VRChat自動起動

設定：

```json
{
  "AutoLaunchVrchat": true,
  "VrchatClientCount": 2
}
```

の場合、Build受信時にVRChatが起動していなければ起動する（コールドスタート）。`VrchatClientCount`（1〜4、省略時1）は起動する台数で、全台が同じアカウントになる（§28）。

コールドスタートの順序は、

```text
.vrcw deploy
↓
archive（処理済みの記録。移動の成功を確かめる）
↓
VRChatを --url 付きで N 台起動（3秒以上の間隔。前の台のoutput_logができてから）
↓
result
↓
cleanup（今回配置したファイルは消さない）
```

とする。

- `--url`で起動した台は、配置済みのワールドを起動時に直接読むので、`--watch-worlds`の準備を待ってから置く必要がない。Bridgeは`--url`を付けずにVRChatを起動しない。
- archiveを起動より先にするのは、起動の途中でBridgeが止まっても、再起動時に同じビルドを配置し直さない（起動済みの台をリロードさせない）ため。止まった場合はresultが書かれず、Unityは`RESULT_TIMEOUT`になる。起動できた台はそのまま残り、次のビルドは起動済みの台への配置になる。
- archiveに失敗したら起動せず、`UNKNOWN_ERROR`で終える。起動に失敗した台があれば`VRCHAT_START_FAILED`（メッセージに「N台中k台を起動」）。どちらも配置済みのファイルはWorldsに残る。
- 起動の間隔は、ログを書き始めたのが同じ秒の台がoutput_logを1つのファイルに書いてしまうのを避けるため（§35）。2台目以降は、前の台までの全台のoutput_logができ、かつ最も新しいファイルの名前の時刻から2秒たってから起動する（最短3秒）。待つのは1回のコールドスタートで合わせて60秒までで、過ぎたら残りの台を3秒間隔で起動し、警告を出す。この待ちは起動の間隔を決めるだけで、どのファイルがどの台のものかは決めない（§35の対応付けがそのまま判断する）。

起動済みの`--watch-worlds`付きの台があるときは、§31の準備待ちのあと配置するだけで、全台がリロードする。台数が`VrchatClientCount`より少なくても、足りない台は起動しない（`--url`で足した台は、リロードした台と別のインスタンスに入るため）。全台を閉じれば、次のビルドで N 台をコールドスタートする。

状態を確かめられないVRChat（プロセスの一覧が取れない、セッションを読めない）が動いているときは、コールドスタートせずに`VRCHAT_UNMANAGED_RUNNING`で失敗する。

`VrchatClientMode: Relaunch`（§32、別アカウントの台）では、ビルドのたびに次の順で行う。起動済みの台へ配置するだけ、という場合は無い。

```text
manifest と artifact の検証
↓
同じセッションの VRChat を列挙し、ハンドルを開いたまま記録と照合（§32）
  管理下でない台・確かめられない台があれば VRCHAT_UNMANAGED_RUNNING（何も終了させない）
↓
管理下の台を終了（§32）。確かめられない台があれば VRCHAT_START_FAILED（配置しない）
↓
もう一度列挙。新しく現れた台があれば VRCHAT_UNMANAGED_RUNNING（配置しない）
↓
.vrcw deploy
↓
archive（移動の成功を確かめる）
↓
VrchatProfiles の順に --url と --profile=<n> で起動（--watch-worlds なし。間隔は上と同じ）
  1台起動するごとに記録に書く。書けなければそこで止める（UNKNOWN_ERROR）
  2台目以降の前に列挙し直し、管理下でない台・確かめられない台が現れていれば止める（VRCHAT_UNMANAGED_RUNNING、「N台中k台を起動」）
↓
result
↓
cleanup（今回配置したファイルは消さない）
```

- 起動の途中でBridgeが止まった場合、記録済みの台は次のビルドで終了の対象になる。起動したが記録する前に止まった台は、次のビルドで管理下でない台として止まるので、手で閉じてもらう。archive後なので、同じビルドを配置し直すことはない。
- 外から起動されるVRChatを完全に締め出すことはできないので、2台目以降の前の確認は「見つけたら止める」までの保証とする。

---

# 31. VRChat startup readiness

コールドスタート（§30）では待たない。`VrchatClientMode: Relaunch`（§30・§32）でも待たない（`--watch-worlds`を付けないので、待つ対象が無い。毎回`--url`で起動し直す）。

起動済みの台へ配置する前は、固定sleepだけに依存しない。Bridgeは`--watch-worlds`付きの全台について、

```text
同じ台の集合（PIDと作成時刻の組）を2回続けて観測した
+
全台が作成時刻から VrchatStartupSettleDelaySeconds（既定30秒）以上たっている
```

ことを確認してからdeployする。

- 台はログインが済むまで配置を拾わない（実機で、`--url`での起動から5秒後の配置は取りこぼし、10秒後からは拾った）。コールドスタートの直後に次のビルドが来たときも、この待ちを通る。
- 作成時刻が読めない台は、その待ちの中で最初に観測した時刻から数える。起動してから時間がたっている台なら、待ちは約1秒。
- output_logはreadinessの判定に使わない。入室などの行はLoggingが「完全」でないと出ないため。

timeoutした場合（`VrchatStartupTimeoutSeconds`、既定60秒）：

```text
VRCHAT_START_TIMEOUT
```

---

# 32. VRChat自動restart

v1では、

```text
autoRestartVrchat = false
```

をdefaultとする。

`--watch-worlds`が無いVRChatを勝手に終了させない。

UI上では、

```text
VRChat is running without --watch-worlds
```

と明示する。

将来的にopt-inで自動restartを追加可能。

複数台のとき：

- `--watch-worlds`付きと無しが混ざっているときは、付きの台を対象に配置を成功させ、無しの台は終了させない（リロードされず、Bridgeのログで警告する）。Unityのstatusにはこれが出ない（§29）。
- 状態を確かめられないVRChatがあるときは、何も終了させず、何も起動せずに`VRCHAT_UNMANAGED_RUNNING`で止め、手で閉じてもらう。

別アカウントの台（§65の複数VRChat profile）：

別アカウントの台は、`--watch-worlds`でリロードさせるとアカウントごとに別のインスタンスへ分かれる（VRChatの不具合）が、終了して`--url`で起動し直すとそろう。そこで、配置のたびに終了・起動し直す方式を、明示的なopt-inとして用意する。

```json
{
  "AutoLaunchVrchat": true,
  "VrchatClientMode": "Relaunch",
  "VrchatProfiles": [0, 1]
}
```

- `VrchatClientMode`は`Reload`（既定、同じアカウント）か`Relaunch`。`Relaunch`では`AutoLaunchVrchat: true`と`VrchatProfiles`（1〜4件、0〜99、重複なし。件数が台数）が必須で、`VrchatClientCount`を書くと起動時にエラー。`Reload`で`VrchatProfiles`を書いてもエラー。Bridgeは各台のアカウントを判別しない（profileにはあらかじめ手でログインしておく。Bridgeは資格情報を扱わない）。
- 終了させるのは、Bridgeが起動したと確かめられた台だけ。Bridgeは起動した台を`StateDirectory\managed-clients.json`（共有の外、§49）に記録する（`schemaVersion`、台ごとにPID、作成時刻を精度を落とさないUTCの文字列、セッション、profile、起動の順番）。管理下とみなすのは、PID・作成時刻・同じ対話セッション・プロセス名`VRChat`がすべて記録と一致する台だけで、名前やログの対応だけでは管理下とみなさない。
- 照合の前にプロセスのハンドルを開き、照合から終了の要求・強制終了・終了の確認まで同じハンドルで行う（途中でPIDから開き直さない。ハンドルを開いている間はPIDが使い回されない）。
- 終了は、全台にウィンドウを閉じるよう求め（ウィンドウがまだ無い台には、できてから求め直す）、全体で10秒たって残っている台だけを強制終了し、最初から20秒までに全台の終了を確かめる。確かめられない台があれば配置しない。「確かめられない」を「終了した」に読み替えない。停止要求が来たら、それ以上強制終了せず、配置もしない。
- 記録が壊れているか知らない形式なら、どの台も管理下とみなさない（VRChatが動いていれば`VRCHAT_UNMANAGED_RUNNING`で止まる）。記録を読めない（アクセスの拒否、I/Oの例外）ときは、何も終了・配置せずに`UNKNOWN_ERROR`。記録は一時ファイルに書いて`Flush(true)`してから置き換え、書けなければ次の台を起動しない。
- `status/vrchat-status.json`の`watchWorldsDetected`はfalseのままとし、偽ってtrueにはしない。Unity v0.3.0は正常に動いているときも`VRChat is running without --watch-worlds`の警告を出すが、ビルドは止まらない（手順書に書く）。
- モードを切り替えるときは、VRChatを全部手で閉じてからBridgeを再起動する。動いている台を別のモードへ自動で移すことはしない。
- §34（成功は「配置した」まで）と§49（manifestから何も実行しない）はそのまま。

---

# 33. Build Status

Build単位で、

```text
status/builds/<buildId>.json
```

を書く。

state：

```text
received
verifying
verified
waiting_for_vrchat
deploying
deployed
failed
```

例：

```json
{
  "protocolVersion": 1,
  "buildId": "20260901T112522481Z-a91f02cc",

  "state": "deployed",

  "deployedFile": "vrc-remote-20260901T112522481Z-a91f02cc.vrcw",

  "updatedAtUtc": "2026-09-01T11:25:27Z",

  "error": null
}
```

---

# 34. 「Worldロード完了」の扱い

VRChatには本ツール向けの公式remote completion APIが存在する前提にしてはならない。

したがってv1の確定successは、

```text
Bridgeが.vrcwをWindows VRChat Worldsへ正常配置
```

までとする。

UI：

```text
Deployment    ✓ Complete
VRChat Reload  Waiting / Observed / Unknown
```

VRChat log解析によるreload検出はbest-effort機能とする。

これをBuild成功判定の必須条件にしない。

---

# 35. VRChat Log

Windows VRChatのlog directory：

```text
%USERPROFILE%\AppData\LocalLow\VRChat\VRChat
```

から最新の、

```text
output_log_*.txt
```

を検出する。

Bridgeはtailした内容を、

```text
logs/vrchat-latest.log
```

としてSMB shareへ公開する。

VRChatが動いているときは、最新のファイルではなく、代表の1台（§29）のファイルを公開する（複数台だと、最後に書いた台のファイルに入れ替わり続けるため）。output_logにはPIDが出ないので、ファイル名の時刻（ローカル時刻、秒まで）と台の作成時刻で対応付ける。

- 対応付けるのは、範囲に入るファイルがちょうど1つで、そのファイルがほかのどの台（`--watch-worlds`無しの台も含む）の範囲にも入らないときだけ。範囲は「作成時刻（秒に切り捨て）の1秒前 ≦ t ＜ 次の台の作成時刻（秒に切り捨て）か作成時刻＋60秒の早いほう」。選んだファイルは、その台が終了するかファイルが消えるまで使い続ける。
- ファイル名の時刻を今のタイムゾーンでUTCに直した値と、ファイルの作成時刻の差が10秒を超えるファイル、夏時間の切り替えであいまいか存在しない時刻のファイルは使わない（タイムゾーンの変更は15分単位でずれる。負荷が高いと、名前の秒の次の秒に作られたファイルがあった）。
- 対応付けできないときは、別の台のログを見せず、その旨のメッセージを公開する。代表の台の起動以降の名前のファイルが1つも無く、起動から60秒たっていなければ、「まだログファイルができていない」というメッセージにする（起動直後の最初の読み取りはたいていこれになる）。それ以外は「対応付けられない」というメッセージにする。何も公開しないと、前のセッション（閉じた台）のログが共有に残り、代表の台のログに見えてしまうので、起動直後でもメッセージを公開する。VRChatが1台も動いていないときだけ、最新のファイルを公開する（前回のセッションのログを見られるようにするため）。
- 選んだファイルの台が一時的に確かめられないとき（プロセスの一覧が取れない、その台が確かめられない台になった）は、固定を外さず、その旨のメッセージを公開する。
- VRChatがファイル名に付けるのは、ログを書き始めた時刻で、プロセスの起動時刻ではない。実機では、ファイルができたのは、負荷が無いときは起動の0.70〜0.84秒後、CPUの使用率が約100%のときは11〜22秒後で、3秒間隔で起動した4台が1つのファイルに書いたこともあった（`docs/validation/multi-client-spike.md`、PR #2 の項目12）。混ざったファイルはどの規則でも分けられないので、Bridgeが起動する台は、前の台のファイルができてから次を起動する（§30）。こうすると、各台のファイルの名前は、その台の範囲にだけ入る。
- 時計が正しいことを前提にした、できる範囲の対応付けで、ファイル名だけで持ち主を証明するものではない。Bridge以外の方法（`start-vrchat-dev.ps1`、手動）で起動した台と、§30の待ちの上限（60秒）を過ぎてから起動した台には、この間隔の保証が無い。ログを書き始めたのが同じ秒の台のログは分けられず、混ざったファイルの名前がある台の範囲にだけ入れば、その台のログとして扱われる。ファイルが起動の間隔を超えて遅れると、終了した台のログを次の台に対応付けることもありうる。

---

# 36. Unity Log Viewer

Unity Windowから最新VRChat logを確認できるようにする。

表示件数：

```text
last 200 lines
```

filter：

```text
All
Error
Exception
Udon
Shader
Warning
```

Auto Refresh：

```text
ON / OFF
```

default ON。

---

# 37. Unity Editor Window

Menu：

```text
VRChat SDK
└─ VRC Remote Test
```

または、

```text
Tools
└─ VRC Remote Test
```

SDK標準UIをpatchしない。

---

# 38. UI layout

概略：

```text
┌──────────────────────────────────────┐
│ VRC Remote Test                      │
├──────────────────────────────────────┤
│                                      │
│ Windows Bridge                       │
│ ● Online                             │
│                                      │
│ VRChat                               │
│ ● Running                            │
│ ✓ --watch-worlds                     │
│                                      │
│ Target                               │
│ Windows / Desktop                    │
│                                      │
│ Last Deployment                      │
│ ShaderLab                            │
│ 20:25:22                             │
│ ✓ Complete                           │
│                                      │
│ [     Remote Build & Test      ]     │
│                                      │
│ [ Deploy Last Build ]                │
│ [ Open Moonlight ]                   │
│                                      │
├──────────────────────────────────────┤
│ VRChat Log                           │
│ ...                                  │
└──────────────────────────────────────┘
```

---

# 39. Primary command

Primary button：

```text
Remote Build & Test
```

処理中はdisableする。

状態：

```text
Idle
Preflight
Building
Resolving Artifact
Hashing
Uploading
Waiting for Bridge
Deploying
Complete
Error
```

---

# 40. Deploy Last Build

Buildをせず最後のartifactを再送する機能。

用途：

```text
Bridge test
Windows側問題調査
watch-worlds再確認
```

通常の開発では使用しない。

---

# 41. Moonlight integration

Moonlightは通信プロトコルには関与しない。

目的：

```text
Windows VRChatを表示・操作
```

のみ。

Unityから、

```text
Open Moonlight
```

を押した場合、

macOSで、

```text
open -a Moonlight
```

相当を実行する。

---

# 42. Auto Focus Moonlight

設定：

```text
Focus Moonlight after deploy
```

default：

```text
false
```

有効ならDeployment完了後にMoonlightをforegroundへ出す。

ただしMoonlightのspecific hostへの自動connectはv1要件外。

Moonlightが既に接続済みである運用を主とする。

---

# 43. Settings

Mac固有設定をUnity Projectへcommitしない。

例：

```text
remote share path
Moonlight application path
heartbeat timeout
focus Moonlight
```

は、

```text
Unity EditorPrefs
```

またはUnityのmachine-local preferencesへ保存する。

AssetsやProjectSettingsへmachine-specific hostnameを保存しない。

---

# 44. Windows config

保存場所：

```text
%LOCALAPPDATA%\VRCRemoteTest\config.json
```

例：

```json
{
  "protocolVersion": 1,

  "rootDirectory": "C:\\VRCRemoteTest",

  "vrchat": {
    "executable": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\VRChat\\VRChat.exe",

    "worldsDirectory": "C:\\Users\\USER\\AppData\\LocalLow\\VRChat\\VRChat\\Worlds",

    "mode": "desktop",

    "autoLaunch": true
  },

  "cleanup": {
    "retainBuilds": 10
  }
}
```

---

# 45. Cleanup

Windows Worldsを無制限に増やさない。

本ツール生成ファイルのみ、

```text
vrc-remote-*.vrcw
```

をcleanup対象にする。

default：

```text
retain latest 10 builds
```

VRChat SDK等が作成した別の`.vrcw`を削除してはならない。

---

# 46. Error Codes

最低限以下を定義する。

```text
SDK_NOT_AVAILABLE
SDK_BUILD_FAILED

INVALID_BUILD_TARGET

BRIDGE_OFFLINE
REMOTE_SHARE_UNAVAILABLE
REMOTE_SHARE_NOT_WRITABLE

ARTIFACT_NOT_FOUND
ARTIFACT_AMBIGUOUS

UPLOAD_FAILED

MANIFEST_INVALID
PROTOCOL_VERSION_MISMATCH
HASH_MISMATCH
SIZE_MISMATCH

BUILD_ALREADY_PROCESSED

VRCHAT_NOT_FOUND
VRCHAT_NOT_RUNNING
VRCHAT_WATCH_WORLDS_MISSING
VRCHAT_START_FAILED
VRCHAT_START_TIMEOUT
VRCHAT_UNMANAGED_RUNNING

DEPLOY_FAILED

UNKNOWN_ERROR
```

---

# 47. Error Message

開発者向けstack traceとは別にユーザー向けmessageを持つ。

例：

```text
BRIDGE_OFFLINE

Windows Bridge is not responding.
Last heartbeat: 35 seconds ago.

Check:
- Windows is running
- VRC Remote Test Bridge is running
- SMB share is mounted
```

---

# 48. Logging

Unity：

```text
[VRC Remote Test]
```

prefixを使用。

例：

```text
[VRC Remote Test] Build started
[VRC Remote Test] Artifact: ...
[VRC Remote Test] SHA256: ...
[VRC Remote Test] Upload complete
[VRC Remote Test] Windows deployment complete
```

Bridge：

structured loggingを使用。

Console + file：

```text
%LOCALAPPDATA%\VRCRemoteTest\logs
```

---

# 49. Security

以下を必須とする。

### Path traversal防止

manifest filenameをsanitizeする。

### Hash validation

deploy前に必ずSHA-256確認。

### Partial file防止

`.part` + rename protocol。

### Share permission

Windows SMB shareはLAN全体へのanonymous writeを許可しない。

SMB には Bridge を動かすユーザーとは別の専用アカウントを使う。共有の権限は Change とし、NTFS で書き込み可能な場所を `incoming`・`_bridge-drop` に限る。Bridge の出力（results・status・logs）は SMB アカウントから読み取り専用とする。

共有は `EncryptData` を有効にして作成する。

### Bridge state

Bridge が信頼する状態（processing・archive・failed）は共有外の `%LOCALAPPDATA%\VRCRemoteTest\state` に置き、config.json では変更できない。検証から deploy までは、共有外の同じコピーを使う。

### Bridge installer

`install-bridge.ps1` は `-ExpectedSha256` を必須とし、配置先の中に作った候補ファイルのハッシュを照合してから実行体を置き換える。期待するハッシュは共有以外の経路で用意する。配置先は `%LOCALAPPDATA%` の下で、本人・SYSTEM・Administrators 以外が書き込めない場所に限る。

リリース済みの版の実行体とハッシュの出所は、GitHub Actions がタグ付きコミットからビルドし、ビルドの来歴（artifact attestation）を署名して Release に添付したものとする。Windows 上で直接ダウンロードし、ワークフロー・タグ・コミット・GitHub ホストのランナーを指定した `gh attestation verify` を通してからインストールする。これは Mac や共有での差し替えを防ぐが、GitHub アカウントやリポジトリの侵害、依存パッケージの侵害までは防がない。

### Secrets

password/tokenをGit repositoryへ保存しない。

### Execution

`.vrcw`以外の任意ファイルをBridgeが実行してはならない。

manifestからcommandを受け取って実行する仕様にはしない。

---

# 50. Windows Bridge state recovery

Bridge起動時、

```text
state/processing
```

をscanする。書き込み途中の `.part` は削除し、claim 済みの manifest を再評価する。完成した private の artifact があれば再検証して使い、無ければ `incoming` から改めてコピーする。

その後、通常のスキャンで `incoming` を処理する。通常のスキャンでも、claim が残っている名前の manifest が再度届き、共有側を消費できた場合は、残っていた private の claim を再開する（共有側の内容は使わない）。claim ごとの処理で想定外の例外が起きた場合は、その claim を failed に移して `UNKNOWN_ERROR` を書き、ほかのビルドの処理を続ける。

共有内に旧構成の processing・archive・failed が残っていても読み込まない（警告のみ）。

ただし既にarchive済みのbuildIdは再deployしない。

---

# 51. FileSystemWatcherだけに依存しない

Windows Bridgeは、

```text
FileSystemWatcher
+
periodic directory scan
```

を併用する。

理由：

filesystem eventのlossやduplicateが起きても処理できるようにする。

処理そのものはidempotentにする。

---

# 52. Unity Domain Reload

Unity script compile/domain reload後も壊れないこと。

Build中状態をstatic fieldだけに保存しない。

ただしUnity Build中に通常domain reloadが発生しない前提にはできる。

必要なtransaction informationは一時ファイルまたはSessionStateへ保持する。

---

# 53. Testability

各主要処理はinterfaceで分離する。

例：

```text
IVrcSdkBuildAdapter
IRemoteTransport
IHashCalculator
IBuildArtifactResolver
IBridgeStatusClient
IMoonlightLauncher
```

EditorWindowから直接filesystem/network処理を書かない。

---

# 54. Unity Tests

Unity Test Frameworkで最低限以下をテストする。

```text
Manifest serialize / deserialize

Build ID generation

Artifact resolver
- 0 candidate
- 1 candidate
- multiple candidates

Filename sanitization

SHA-256

Atomic upload naming

Status state transition
```

---

# 55. Bridge Tests

xUnit等を使用。

```text
Manifest validation

Protocol mismatch

Hash mismatch

File size mismatch

Path traversal

Duplicate build

Atomic deploy

Cleanup

Recovery

Status serialization
```

VRChat.exeそのものをCIで起動するテストは不要。

`IVrchatProcessManager`をmockする。

---

# 56. Integration Test

実環境で以下を確認する。

## Case A

```text
VRChat running
--watch-worlds enabled

Unity
↓
Remote Build & Test
↓
Windows VRChat reload
```

---

## Case B

```text
VRChat not running
autoLaunch enabled

Remote Build & Test
↓
VRChat launch
↓
deploy
```

---

## Case C

```text
Bridge stopped

Remote Build & Test
↓
Preflightで停止
↓
World buildしない
```

---

## Case D

```text
SMB disconnected

Remote Build & Test
↓
Preflight error
```

---

## Case E

```text
corrupted .vrcw

↓
HASH_MISMATCH
↓
deployされない
```

---

## Case F

10回以上連続で、

```text
Build
Deploy
Reload
```

を行っても、partial artifactや古いmanifestが原因で誤deployしない。

---

# 57. VPM Packaging

Unity側はVPM-compatible packageとする。

`package.json`には、

```text
vpmDependencies
```

を使用する。

VRChat Worlds SDK Public APIへ依存するため、

SDKのbreaking-version rangeを指定する。

実際のrangeはPhase 0で現在のSDK Public APIを検証して決めること。

例：

```json
{
  "name": "com.local.vrc-remote-test",
  "displayName": "VRC Remote Test",
  "version": "0.1.0",

  "description": "Remote VRChat world testing from macOS Unity to a Windows VRChat client.",

  "vpmDependencies": {
    "com.vrchat.worlds": "<verified-compatible-range>"
  }
}
```

推測したversionをcommitしない。

---

# 58. Assembly Definition

Unity packageにはasmdefを必須とする。

SDK Public APIに必要なassemblyのみreferenceする。

不要な、

```text
Runtime
Udon
ClientSim
```

への依存を追加しない。

Editor-only packageとする。

---

# 59. Git

repositoryに含めないもの：

```text
Unity Library
Unity Temp
Unity Logs

*.vrcw
*.part

Windows Bridge runtime logs
local settings
SMB credentials

build output
```

artifactはruntime生成物でありGit管理しない。

---

# 60. 実装フェーズ

## Phase 0 — Environment Investigation

最初にコードを書かず、

```text
Unity version
VRChat SDK version
Public SDK API
Build-only method
Build events
artifact output
```

を調査する。

成果物：

```text
docs/sdk-api-notes.md
```

---

## Phase 1 — Windows Bridge Core

実装：

```text
Bridge process
config
heartbeat
incoming watcher
manifest validation
hash validation
deploy
status
cleanup
```

この段階ではUnity integration不要。

手動でmanifest + `.vrcw`をincomingへ置いてテストする。

---

## Phase 2 — Unity Core

実装：

```text
SDK adapter
Build coordinator
Artifact resolver
SHA256
SMB transport
status polling
```

Console commandからremote buildできるところまで作る。

---

## Phase 3 — Unity UI

実装：

```text
Remote Test Window
Remote Build & Test
Deploy Last Build
Preflight status
progress display
```

この時点でMVP完成。

---

## Phase 4 — VRChat Integration

実装：

```text
VRChat process monitor
--watch-worlds detection
autoLaunch
development launch profile
```

---

## Phase 5 — Developer UX

実装：

```text
VRChat log reader
Unity log viewer
Moonlight launcher
Moonlight auto focus
better error messages
```

---

## Phase 6 — VPM Distribution

実装：

```text
package metadata
VPM dependencies
release package
documentation
GitHub Actions
```

---

# 61. MVP Definition

MVPは以下が成立した状態。

```text
Mac Unity
↓
Remote Build & Test
↓
Windows .vrcw deploy
↓
起動済みVRChat --watch-worlds
↓
World reload
```

さらに、

```text
Windows Unity不要
MacからWindows手動コピー不要
VRChat Online upload不要
```

であること。

---

# 62. Definition of Done

以下をすべて満たすこと。

### Build

- Mac UnityからWindows向けWorld Buildが可能
- SDK Public APIのみ使用
- SDK内部reflectionなし

### Transfer

- 1クリックでWindowsへ送信
- `.part` protocol対応
- SHA-256 verificationあり

### Windows

- Bridge自動起動可能
- partial buildをdeployしない
- duplicate buildに耐える
- stale buildをcleanup可能

### VRChat

- `--watch-worlds`環境へdeploy可能
- Desktop Modeで実VRChat確認可能
- VRChat停止状態を検出可能

### UX

- Remote Build & Testボタン1つ
- Build progress表示
- Windows Bridge状態表示
- error reason表示
- Moonlight起動可能

### Security

- passwordをrepositoryに保存しない
- arbitrary command executionなし
- path traversal対策済み

### Packaging

- VPM compatible
- asmdefあり
- READMEあり
- setup documentationあり

---

# 63. READMEに記載するSetup Flow

最終READMEではSetupを以下の順にする。

```text
1. Windows Bridgeをinstall

2. WindowsでC:\VRCRemoteTestをSMB share

3. Macからshareをmount

4. Windows VRChat development launch設定
   --watch-worlds

5. ALCOMでVRC Remote Test package追加

6. Unity
   VRChat SDK > VRC Remote Test

7. Validate Setup

8. Remote Build & Test
```

---

# 64. Claude Codeへの実装指示

この仕様書を実装する際は以下を守ること。

1. 最初に既存repositoryとUnity Projectを調査する。

2. VRChat SDKのAPI名をWeb記事から推測しない。

3. インストール済みSDKのPublic SDK APIを直接読む。

4. SDK内部reflectionを使わない。

5. 最初から全機能を一括実装せずPhase単位で動作確認可能にする。

6. UIより先にBuild/Transfer/Bridgeのcore logicを完成させる。

7. filesystem/network/process accessをinterfaceで分離しtestableにする。

8. Windows BridgeはWindows Serviceではなくinteractive user sessionで実行する。

9. `.vrcw`をWindowsへ直接上書きコピーしない。

10. 正常系だけではなく、network切断、Bridge停止、hash mismatch、duplicate eventを必ず考慮する。

11. 実装中にVRChat Public SDK APIと本仕様書が矛盾した場合、Public SDK APIを正とし、`docs/sdk-api-notes.md`へ差異を記録する。

12. 未確認のVRChat内部挙動を「動くはず」として実装しない。`--watch-worlds`より先のVRChat reload完了検出はbest-effortとして扱う。

---

# 65. 将来拡張

v1完成後に検討する。

```text
SMB → HTTP transport

SSH/SFTP transport

複数Windows host

Desktop / VR profile切替

複数VRChat profile（配置のたびに終了・起動し直す方式は §32 の`VrchatClientMode: Relaunch`として実装済み。アカウントの自動判別、一部の台だけの起動し直しは未実装）

VRC Quick Launcher integration

Windows側VRChat screenshot取得

Windows側GPU / FPS telemetry

Shader compile error抽出

Udon exception自動抽出

Build前後比較

Remote performance profiling

Quest remote target

VRChatMultiSim integration
```

特にtransportは、

```text
IRemoteTransport
```

を設けているため、将来的にはSMBを廃止して、

```text
Mac Unity
    ↓ HTTP
Windows Bridge
```

へ変更できる構造とする。

---

# 66. 最終的な理想UX

開発者が意識するのは、

```text
ClientSimで確認
       ↓
Windows実機で見たい
       ↓
Remote Build & Test
       ↓
Moonlight
```

だけ。

内部の、

```text
Build
Artifact discovery
Hash
Network transfer
Manifest
Windows deploy
VRChat reload
```

はすべてツール側で処理する。

**Windowsを「Unity開発機」ではなく「VRChat実機テストターゲット」として扱うことが、本プロジェクトの最終的な設計目標である。**