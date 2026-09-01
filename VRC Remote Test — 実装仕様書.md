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
C:\VRCRemoteTest
│
├─ incoming
├─ processing
├─ archive
├─ failed
├─ status
└─ logs
```

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
   ↓ atomic move
processing
```

へmanifestをmoveする。

これをclaim operationとする。

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
archive/<buildId>.ready.json
```

の存在で判定可能。

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

---

# 30. VRChat自動起動

設定：

```json
{
  "autoLaunchVrchat": true
}
```

の場合、Build受信時にVRChatが起動していなければ起動する。

ただし順序は、

```text
VRChat起動
↓
watch-worlds準備
↓
.vrcw deploy
```

とする。

`.vrcw`を置いてからVRChatを起動しない。

---

# 31. VRChat startup readiness

固定sleepだけに依存しない。

最低限、

```text
VRChat process running
+
新しいoutput_logが生成されている
```

ことを確認する。

その後deployする。

timeoutした場合：

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

### Secrets

password/tokenをGit repositoryへ保存しない。

### Execution

`.vrcw`以外の任意ファイルをBridgeが実行してはならない。

manifestからcommandを受け取って実行する仕様にはしない。

---

# 50. Windows Bridge state recovery

Bridge起動時、

```text
incoming
processing
```

をscanする。

未完了buildが存在する場合はrecoveryする。

processingに残ったmanifestも再評価する。

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

複数VRChat profile

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