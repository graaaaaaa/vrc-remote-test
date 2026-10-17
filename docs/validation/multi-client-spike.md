# 複数クライアント検証スパイク（開発時の実機検証記録）

> **対象読者**: このツールのコード自体を開発・改修する人向けの検証記録です。VRC Remote Testを利用するだけであれば読む必要はありません（`README.md`を参照してください）。

**目的**: 1台のWindows機でVRChatを複数台起動し、vrc-remote-testのデプロイ（Worldsディレクトリへの `.vrcw` 配置）で全台が同じローカルインスタンスへリロードされるかを実機で確かめる。結果で、Bridgeへの実装方針（下記の案B・案C）を決める。

**位置づけ**: 使い捨てのスパイクである。Bridgeは変更せず、`scripts/start-vrchat-dev.ps1` の複数台起動と `scripts/deploy-test.ps1` の手動配置だけで確かめる。`watch-worlds-spike.md` と同じく、Bridge実装の前の前提確認として行う。

---

## 背景

### SDKの Number of Clients がしていること

vrc-remote-testはMac側でSDKの `Build()` だけを呼ぶので、SDKパネルの Number of Clients は効かない。SDKがWindowsで行う起動処理は、インストール済みSDK 3.10.4 の `VRCSDK3-Editor.dll`（`VRCWorldAssetExporter.RunWorldTestDesktop`）のILを読んで次のとおり確認した（2026-10-17）。

- roomIdとして乱数10桁を1つ作る
- `--url=create?roomId=<10桁>&hidden=true&name=BuildAndRun&url=file:///<エンコードした.vrcwのパス>` に、設定に応じて `--no-vr`・`--watch-worlds`、さらに `--enable-debug-gui --enable-sdk-log-levels --enable-udon-debug-logging` を付ける
- 同じ引数で `VRChat.exe` をN回起動し、1台ごとに3秒待つ（`Thread.Sleep(3000)`）
- `--profile` は付けない。全台がprofile 0、つまり同じアカウントで入る
- Nは0〜8に丸められる。0のときは起動せず、`--watch-worlds` 付きで起動済みのクライアントのリロードに任せる（Build & Reload）

`start-vrchat-dev.ps1` の `-Clients`・`-WorldPath`・`-LaunchIntervalSeconds` は、この起動を再現する。`-Profiles` はSDKにない追加で、台ごとに `--profile=N` を付ける。

### 2026-10時点で報告されている問題

| 問題 | 状況 | このスパイクで見ること |
|---|---|---|
| 2台目以降が黒画面のまま固まる | 2026-08-16にも未修正と報告され、2026-08-20に「次回リリースで修正予定」になった。ゲーム内の Settings > Debug > Logging が None のとき起きやすく、Errors Only か Full で避けられるという報告が複数ある | ケース6 |
| 複数を同時に起動すると固まる | VRChat 2026.3.2（build 1901、2026-09-01）で修正済み | オプションB |
| 4台以上 | 1台目が "Can't Travel" になる、3台目より後が前の台を追い出す、という報告。tracked、2026-05にも再発報告 | ケース7 |
| 別profileとwatch-worldsの組み合わせ | 別アカウントの複数台でBuild & Reloadすると、台ごとに別のインスタンスへ入る。2025-07-25からtracked | ケース5 |

出典:

- [Using Build & Test](https://creators.vrchat.com/worlds/udon/using-build-test/)
- [Can't Build & Test multiple clients any more](https://feedback.vrchat.com/sdk-bug-reports/p/cant-build-test-multiple-clients-any-more)
- [VRChat 2026.3.2](https://docs.vrchat.com/docs/vrchat-202632)
- [When launching more than four clients in a local test…](https://feedback.vrchat.com/bug-reports/p/when-launching-more-than-four-clients-in-a-local-test-of-the-world-the-first-cli)
- [Using Build & Reload with Multiple Profiles Disconnects Clients after Reload](https://feedback.vrchat.com/bug-reports/p/using-build-reload-with-multiple-profiles-disconnects-clients-after-reload)

### 検証後に選ぶ実装案

- **案B**: 同じprofileでN台。初回はBridgeが `.vrcw` を配置したあと、共通のroomIdを付けた `--url` で全台を起動する。2回目以降は配置だけにして、`--watch-worlds` で全台をリロードさせる（SDKの Build & Test → Build & Reload と同じ流れ）
- **案C**: 別profile（別アカウント）でN台。watch-worldsのリロードで別インスタンスに分かれるなら、デプロイのたびに全台を共通のroomIdで起動し直す

---

## 前提条件

- [ ] `watch-worlds-spike.md` の前提条件（Windows 11、Steam版VRChat、PowerShell 7、視覚的に区別できる `build-A.vrcw` と `build-B.vrcw`）がそろっている
- [ ] `build-A.vrcw` と `build-B.vrcw` を `C:\VRCRemoteTest\spike\` に置いてある
- [ ] 同期の確認に使えるUdonがワールドにある（例: 押すと色が変わる同期ボタン、ownerを移すボタン）。同じインスタンスにいるかを判断する材料になる
- [ ] ケース4・5用に、2つ目のVRChatアカウントでprofile 1にログイン済み（`.\start-vrchat-dev.ps1 -Profiles 1` で1台だけ起動し、手でログインする。パスワードをスクリプトや設定ファイルに書かない）
- [ ] 各profileのゲーム内設定 Settings > Debug > Logging を Errors Only か Full にしてある（ケース6だけ None にする）
- [ ] 各ケースの開始前にVRChatがすべて終了している（タスクマネージャーで `VRChat.exe` が残っていないこと）
- [ ] Sunshine/Moonlight で Windows の画面を見られる（ケース8）

---

## 使用スクリプト

- `scripts/start-vrchat-dev.ps1` — VRChatを検証用引数で起動する。複数台の起動は `-Clients`・`-WorldPath`・`-Profiles`・`-LaunchIntervalSeconds`
- `scripts/deploy-test.ps1` — `.vrcw` をWorldsディレクトリへatomicに配置する（Bridgeを介さない手動模擬）

`start-vrchat-dev.ps1` は起動した台ごとに引数とPIDを表示し、`-WorldPath` を指定したときはroomIdも表示する。結果記録に転記する。

---

## 同じインスタンスにいるかの確認方法

- **一次確認（必須）**: 各台の画面に、他の台のアバターとネームプレートが見えるか。同期ボタンを1台で押して、他の台に反映されるか
- **補助確認（任意）**: 各台の `output_log_*.txt`（`%USERPROFILE%\AppData\LocalLow\VRChat\VRChat\`）で、スクリプトが表示したroomIdを検索し、出てきた行を記録する。台ごとに別のログファイルができるはずなので、作成時刻とPIDの起動順で対応を取る。ログの書式はクライアントのバージョンによって違うので、事前に決め打ちせず実際の出力を転記する

---

## 必須テストケース（案Bの Go/No-Go ゲート）

### ケース1: 同じprofileの2台を `--url` で起動

1. `.\start-vrchat-dev.ps1 -Clients 2 -WorldPath C:\VRCRemoteTest\spike\build-A.vrcw` を実行する
2. 2台ともbuild-Aに入るまで待つ
3. 2台が同じインスタンスにいるかを確認する
4. 1台目と2台目のどちらがMasterになったか（同期オブジェクトのowner）を記録する

### ケース2: ケース1の状態で配置して、2台とも同じインスタンスへリロードされるか

案Bが成り立つかを決める本命のケース。

1. ケース1の2台を起動したまま、`.\deploy-test.ps1 -SourcePath C:\VRCRemoteTest\spike\build-B.vrcw` を実行する
2. 2台ともbuild-Bにリロードされるか、リロード後も同じインスタンスにいるかを確認する
3. 続けて `.\deploy-test.ps1 -SourcePath C:\VRCRemoteTest\spike\build-A.vrcw` を実行し、もう一度同じことを確認する
4. 各回のリロード所要時間（概算）を記録する

### ケース3: 同じprofileの2台を `--url` なしで起動

1. 全台を終了し、`.\start-vrchat-dev.ps1 -Clients 2` を実行する（`--url` なし、`--watch-worlds` あり）
2. 2台がどこで起動したか（ホームワールドか、オンラインか、片方がエラーになったか）を記録する
3. `.\deploy-test.ps1 -SourcePath C:\VRCRemoteTest\spike\build-A.vrcw` を実行し、2台が同じローカルインスタンスに入るかを確認する

今のBridgeの自動起動と同じく `--url` を付けずに複数台を起動した場合に、何が起きるかを確かめる。案Bで初回だけ `--url` で起動する理由が、ここで裏付けられるか崩れるかが決まる。

---

## 案Cの判断用ケース

### ケース4: 別profileの2台を `--url` で起動

1. 全台を終了し、`.\start-vrchat-dev.ps1 -Profiles 0,1 -WorldPath C:\VRCRemoteTest\spike\build-A.vrcw` を実行する
2. 2台が別のアカウント名で、同じインスタンスにいるかを確認する

### ケース5: ケース4の状態で配置して、同じインスタンスに残るか

1. ケース4の2台を起動したまま、`.\deploy-test.ps1 -SourcePath C:\VRCRemoteTest\spike\build-B.vrcw` を実行する
2. 2台が同じインスタンスにリロードされるか、別々のインスタンスに分かれるかを記録する

分かれた場合は、報告されている不具合（2025-07-25からtracked）が残っていることになる。

---

## 環境条件のケース

### ケース6: Logging設定と黒画面

1. 各profileのLoggingを None にする
2. ケース1と同じ手順で2台を起動し、2台目以降が黒画面で止まるかを記録する
3. Loggingを Errors Only に戻して同じことを行い、結果を比べる
4. Logging設定がprofileごとに分かれているか（profile 0で変えるとprofile 1も変わるか）も記録する

### ケース7: 3台と4台

1. `.\start-vrchat-dev.ps1 -Clients 3 -WorldPath C:\VRCRemoteTest\spike\build-A.vrcw` で3台起動し、全台が同じインスタンスにいるかと、ケース2と同じ配置でのリロードを確認する
2. 全台を終了し、`-Clients 4` で同じことを行う
3. "Can't Travel" の表示や、ほかの台が追い出される挙動が出るかを記録する

### ケース8: Moonlight越しの操作と負荷

1. ケース1と同じ2台（ケース7で3台が使えたなら3台も）を起動した状態で、Moonlightから画面を見る
2. ウィンドウを並べて全台が1つの画面に収まるか、台を切り替えて操作できるかを記録する
3. タスクマネージャーでGPU使用率と、各台のFPS（デバッグGUIで見られれば）を記録する

---

## オプションテスト（必須ケースの後、余力があれば）

- [ ] **A. Bridge経由のデプロイ**: ケース1の2台を起動した状態で、MacのUnityから `Remote Build & Test` を実行する。Bridgeのログに `Multiple VRChat processes detected` の警告が出ること、デプロイが成功して2台ともリロードされることを確認する
- [ ] **B. 同時起動**: `-Clients 2 -LaunchIntervalSeconds 0` で起動し、3秒あけた場合との違いを確認する（2026.3.2の修正が効いているか）
- [ ] **C. VRとデスクトップの組み合わせ**: `-Mode VR -Clients 2 -WorldPath ...` で、1台目がHMD、2台目がデスクトップで同じインスタンスに入るかを確認する
- [ ] **D. 同じアカウントで区別できないもの**: 同じprofileの2台で、表示名やプレイヤー単位の保存データ（PlayerData など）を使う処理がどう見えるかを確認する。案Cが必要な場面の判断材料にする

---

## 結果記録

> **注記**: output_logの内容を転記する際は、ローカルファイルパスやユーザー名、アカウント名等の機微情報を伏せ字にしてから記録すること。必要最小限の関連行のみを転記する。

### 環境情報

| 項目 | 値 |
|------|-----|
| 実施日時 | |
| VRChatバージョン（build） | |
| Windowsバージョン | |
| GPU | |
| PowerShellバージョン | |
| ビルドに使ったVRChat Worlds SDK | |
| Logging設定（ケース6以外） | |
| アカウント構成（profile 0 / 1） | |

### ケース1: 同じprofileの2台を `--url` で起動

| 項目 | 結果 |
|------|------|
| 実行コマンド | |
| roomId | |
| PID（1台目 / 2台目） | |
| 同じインスタンスか | |
| Master（owner）になった台 | |
| 観察事項 | |
| output_log 関連行（取得できた場合） | |

### ケース2: 同じprofileの2台で配置

| 項目 | 結果 |
|------|------|
| 1回目（build-B）: 2台ともリロードしたか | |
| 1回目: リロード後も同じインスタンスか | |
| 1回目: リロード所要時間（概算） | |
| 2回目（build-A）: 2台ともリロードしたか | |
| 2回目: リロード後も同じインスタンスか | |
| 2回目: リロード所要時間（概算） | |
| 観察事項 | |
| output_log 関連行（取得できた場合） | |

### ケース3: 同じprofileの2台を `--url` なしで起動

| 項目 | 結果 |
|------|------|
| 起動直後の各台の状態 | |
| 配置後、同じローカルインスタンスに入ったか | |
| 観察事項 | |

### ケース4: 別profileの2台を `--url` で起動

| 項目 | 結果 |
|------|------|
| 実行コマンド | |
| roomId | |
| 同じインスタンスか | |
| 観察事項 | |

### ケース5: 別profileの2台で配置

| 項目 | 結果 |
|------|------|
| 2台ともリロードしたか | |
| 同じインスタンスか / 分かれたか | |
| 観察事項 | |

### ケース6: Logging設定と黒画面

| Logging設定 | 2台目以降の状態 | 備考 |
|------|------|------|
| None | | |
| Errors Only | | |
| profileごとに別設定か | | |

### ケース7: 3台と4台

| 台数 | 全台が同じインスタンスか | 配置後のリロード | "Can't Travel" や追い出し | 備考 |
|------|------|------|------|------|
| 3 | | | | |
| 4 | | | | |

### ケース8: Moonlight越しの操作と負荷

| 項目 | 結果 |
|------|------|
| 台数 | |
| 全台を1画面で見られたか | |
| 台の切り替え方法と操作感 | |
| GPU使用率 | |
| 各台のFPS | |
| 観察事項 | |

### オプションテスト結果（実施した場合のみ）

| ケース | 結果 | 備考 |
|--------|------|------|
| A. Bridge経由のデプロイ | | |
| B. 同時起動 | | |
| C. VRとデスクトップ | | |
| D. 同じアカウントで区別できないもの | | |

---

## Go/No-Go 判定テーブル

| 観測結果 | 判定 | 次の作業 |
|---------|------|---------|
| ケース1・2で、2台が同じインスタンスに入り、配置のたびにそろってリロードする | **案B Go** | Bridgeに同じprofileのN台起動を実装する（初回は配置してから `--url` で起動、以降は配置のみ） |
| ケース1はそろうが、ケース2のリロードで分かれる | 案Bのリロード部分は No-Go | 同じprofileでも、デプロイのたびに共通のroomIdで起動し直す（案Cの方式） |
| ケース1で同じインスタンスに入らない | **スパイク自体を見直す** | 引数の再現が誤っている可能性がある。VRC Quick Launcher で同じ構成を起動し、`Win32_Process` のコマンドラインを比べる |
| ケース4・5で、別profileでもそろってリロードする | 案Cは不要 | 不具合が直っている。別profileも案Bと同じ流れで扱う |
| ケース5で分かれる | 案Cが必要 | 別profileのときだけ、デプロイのたびに起動し直す |
| ケース6で、None のときだけ黒画面になる | 手順書に追記 | `docs/setup-windows.md` にLogging設定を書く |
| ケース7で、4台のときだけ失敗する | 上限を3台にする | Bridgeの設定で台数の上限を3にする |
| ケース8で、操作や負荷に無理がある | 台数か表示方法を見直す | ウィンドウサイズやFPS上限（`--fps=`）を起動引数に加えるかを検討する |

判定結果:

```
判定:
理由:

Bridge実装への申し送り事項:
```
