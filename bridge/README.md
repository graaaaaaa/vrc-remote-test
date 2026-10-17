# VRC Remote Test Bridge

Windows側で動作する.NET 10 (LTS) コンソールアプリ。SMB共有のステージング領域を監視し、`.vrcw`ビルドを検証してVRChat Worldsディレクトリへ配置します。

VRChat自体（Unity/Mono/IL2CPPベースの独立プロセス）とはランタイム上の依存関係が一切ないため、.NETのバージョンはVRChat側の制約を受けません。開発機の環境に合わせて.NET 10 LTSを採用しています。

Windows Serviceではなく、インタラクティブユーザーセッションで非elevated実行します（VRChat起動・監視機能のため）。

## 構成

```
bridge/
├── VRCRemoteTest.Bridge.sln
├── src/VRCRemoteTest.Bridge/       — 本体 (net10.0, win-x64 self-contained single-file)
│   ├── Program.cs                  — エントリポイント、Generic Host組み立て
│   ├── Configuration/              — config.json読み込み・検証
│   ├── Protocol/                   — BuildManifest / BuildResult / VrchatStatus (JSON wire format)
│   ├── Deployment/                 — 検証・配置・クリーンアップ・監視ループ
│   └── VRChat/                     — VRChatプロセス監視・自動起動・ログ配信
└── tests/VRCRemoteTest.Bridge.Tests/ — xUnit
    └── fixtures/                   — golden JSONフィクスチャ (Unity側と共有)
```

## 機能概要

Bridgeは「フルオーケストレーションサービス」ではなく「ファイル検証・配置に特化したプロモーター」として設計しています。

- **heartbeatなし**: ビルドごとに1つの結果ファイル (`results/{buildId}.json`) のみ
- **共有の内容は信用しない**: SMB アカウントは共有内をいつでも書き換えられるため、Bridge は共有のファイルを自分の状態へ Move しない（同じボリューム内の Move はアップロード元の ACL を引き継ぐ）。manifest（64 KiB まで）と artifact（`MaxArtifactSizeBytes` まで、実際に読んだバイト数で判定）を共有外の状態ディレクトリへコピーし、そのコピーを検証して、同じコピーから配置する。検証後に共有のファイルを開き直すことはない
- **VRChatプロセス監視**: `VrchatMonitorService`が10秒間隔でVRChatプロセスと`--watch-worlds`引数の有無を`status/vrchat-status.json`へ書き出す。Unity側のpreflight表示専用で、ビルドの成否には一切影響しない
- **VRChat自動起動（`AutoLaunchVrchat`、デフォルトOFF）**: 有効時の流れは`VrchatClientCoordinator`が決める（詳細は同ファイルのコメント）
  - **コールドスタート**（同じセッションの VRChat が1台も無い）: 先に`.vrcw`を Worlds へ配置し、archive（処理済みの記録）の成功を確かめてから、配置したファイルを`--url`に渡して`VrchatClientCount`台（1〜4、省略時1）を3秒間隔で起動し、結果を書く。`--url`で起動した台はそのワールドに直接入るので、起動の準備は待たない。全台が同じアカウント（`--profile`なし）で、同じインスタンスに入る。VR モードでは HMD を使うのは1台目だけ
  - **起動済みの台への配置**（`--watch-worlds`付きが1台以上）: 全台について、（PID、作成時刻）の集合が2回続けて同じで、全台が作成時刻から`VrchatStartupSettleDelaySeconds`以上たつのを待ってから配置する。全台がリロードする。台数が足りなくても足さない（足した台は別のインスタンスに入るため）。台数の違いや`--watch-worlds`無しの台が混ざっていることは、変わったときに1度だけログで警告する
  - 既に`--watch-worlds`無しだけで起動中のVRChatは勝手に再起動しない（`VRCHAT_WATCH_WORLDS_MISSING`）。状態を確かめられない VRChat（一覧が取れない、セッションを読めない）があれば何も起動せず`VRCHAT_UNMANAGED_RUNNING`
  - 数える VRChat は Bridge と同じ対話セッションのものだけで、`--watch-worlds`はコマンドラインを引数に分けて完全一致で判定する（`VrchatProcessMonitor`）。コマンドラインは観測ごとに1回の WMI の問い合わせでまとめて読み、行の作成時刻をプロセスの起動時刻と照合する。WMI が答えない・時間内に返らない台は「確かめられない台」として扱う
- **VRChatログ配信（`VrchatLogService`）**: 5秒間隔で`output_log_*.txt`の末尾を再読み込みし、直近200行を`logs/vrchat-latest.log`へ書き出す。VRChat が動いている間は、最新のファイルではなく1台目（`--watch-worlds`付きで作成時刻が最も古い台）のファイルを、ファイル名の時刻と作成時刻で対応付け、その台が終了するかファイルが消えるまで使い続ける（`VrchatLogMatcher`）。対応付けられなければ、ログの代わりに診断の1行を書く。VRChat が起動から1秒ほどのうちにログファイルを作ることを前提にしている（未測定）。VRChat が動いていないときだけ最新のファイルを使う。Unity側Log Viewer（表示専用）のためのもので、readiness判定には一切使用しない
- **二重起動の防止**: 起動時に`%LOCALAPPDATA%\VRCRemoteTest\state\bridge.lock`を`FileShare.None`で開き、終了まで持ち続ける。開けなければ起動を拒否する（Task Scheduler の設定だけに頼らない）。ロックは Windows アカウントごとなので、共有と Worlds は Bridge 用のアカウント1つで使う
- **自動起動**: `scripts/install-bridge.ps1`でWindows Task Scheduler（Trigger: At log on、Principal: Interactive/Limitedで非elevated）へ登録できる。実行体はSMB共有の外側（既定`%LOCALAPPDATA%\Programs\VRCRemoteTest\Bridge\`）に配置する — 共有への書き込み権限を持つ者が実行体を差し替えてログオン時の自動実行を乗っ取れないようにするため、意図的にSMB共有の外に配置する。共有の drop から配置するときは、配置先の中に作った候補ファイルの SHA-256 を、共有以外の経路で用意した `-ExpectedSha256` と照合してから置き換える。手順は`docs/setup-windows.md`、詳細は`scripts/install-bridge.ps1 -?`を参照
- **配布**: `v*.*.*` タグのリリースでは、GitHub Actions（`.github/workflows/release-package.yml` の `bridge` ジョブ）がタグ付きコミットから win-x64 の単一ファイル exe をビルドし、テストを通したうえで、ビルドの来歴（artifact attestation）を署名して Release に添付する。SHA-256 はリリースノートに載る。Windows では `gh attestation verify` で来歴を確認してから `install-bridge.ps1 -ExpectedSha256` に渡す（`docs/setup-windows.md` の手順1-A）。ビルドは再現可能ではないので、同じタグを全体再実行して exe を作り直しても、公開済みの exe は置き換えない
- **認証はSMB ACLのみ**: HMAC署名は将来検討事項

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
    "VrchatClientCount": 1,
    "VrchatStartupTimeoutSeconds": 60,
    "VrchatStartupSettleDelaySeconds": 30
  }
}
```

`VrchatWorldsDirectory`が実在しない場合、Bridgeは起動を拒否します（勝手に別の場所を推測しません）。状態ディレクトリは `%LOCALAPPDATA%\VRCRemoteTest\state` に固定で、config.json では変更できません（`StagingDirectory` と重なる設定は起動時に拒否します）。`VrchatExecutable`/`VrchatMode`/`AutoLaunchVrchat`/`VrchatClientCount`/`VrchatStartupTimeoutSeconds`/`VrchatStartupSettleDelaySeconds`はVRChat自動起動用の設定で、`VrchatClientCount`は省略時1、指定するなら1〜4（`AutoLaunchVrchat: false`でも範囲は検証する）。`AutoLaunchVrchat: true`の場合のみ`VrchatExecutable`が検証される（絶対パス・非UNC・`.exe`拡張子・ファイル名が`VRChat.exe`と完全一致・実在、の全てを満たす必要がある）。

`VrchatStartupSettleDelaySeconds`（デフォルト30秒）は、起動済みの台へ配置する前に、各台のプロセス起動から待つ最低時間（実際にWorldsディレクトリの監視が有効になる、と推定されるまで）。コールドスタートでは使わない。WMIでの`--watch-worlds`引数検出はプロセス起動直後にほぼ即座に可能になるが、それは「VRChatが実際にファイル配置を検知できる状態」を意味しない（VRChatがホーム画面に到達するまで体感15〜30秒程度）。マシンやVRChatのバージョンによって実際の起動時間は変動するため、必要に応じて値を調整すること。`VrchatStartupTimeoutSeconds`は`VrchatStartupSettleDelaySeconds + 30秒`未満に設定できない。

## ステージングディレクトリ構造

SMB 共有（`StagingDirectory`）と、Bridge 専用の状態ディレクトリに分かれます。無ければ Bridge が作成しますが、共有側は権限を付けるため `docs/setup-windows.md` の手順3で事前に作成してください:

```
{StagingDirectory}/          — SMB 共有（Mac が書けるのは incoming と _bridge-drop だけ）
├── incoming/     — Unity側からのアップロード先 (.vrcw + .ready.json)。取り込み後に消費される
├── _bridge-drop/ — Bridge 実行体の受け渡し用（install-bridge.ps1 がハッシュを照合して取り込む）
├── results/      — ビルドごとの結果ファイル (Unity側がポーリング)
├── status/       — VrchatMonitorServiceが10秒間隔で上書きするvrchat-status.json
└── logs/         — VrchatLogServiceが5秒間隔で上書きするvrchat-latest.log（表示専用）

%LOCALAPPDATA%\VRCRemoteTest\state/   — Bridge 専用（共有しない）
├── bridge.lock   — 二重起動の防止用。Bridge が動いている間、開いたままにする
├── processing/   — claim 済みの manifest と artifact の private コピー。.part は書き込み途中で、起動時に削除される
├── archive/      — 処理完了した manifest（冪等性判定に使用）。artifact は Worlds にあるので残さない
└── failed/       — 検証失敗・quarantine。artifact は新しいものから RetainBuilds 件だけ残す
```

旧構成（共有内に `processing`・`archive`・`failed` があった構成）から更新した場合、共有内に残った旧ディレクトリは読み込まず、起動時に警告を出すだけです。移行手順は `docs/setup-windows.md` の手順14を参照してください。

## 手動テスト（Unity不要）

`incoming/`へ手動でファイルを配置してBridgeの動作を確認できます。`tests/VRCRemoteTest.Bridge.Tests/fixtures/sample-manifest.json`を参考にmanifestを作成し、対応する`.vrcw`（ダミーファイルで可、サイズとSHA-256を一致させること）を`incoming/{buildId}.vrcw`として配置後、`incoming/{buildId}.ready.json`を書き込むと処理が始まります。
