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
| 実施日時 | 2026-10-17 12:07〜12:18（ケース1〜3・6・7）、13:05〜13:12（ケース6の profile ごとの設定、同じアカウントでのケース4・5）、13:40〜13:42（別アカウントでのケース4・5） |
| VRChatバージョン（build） | 2026.3.3p2-1921-15c4a824b2-Release（build 1921） |
| Windowsバージョン | Windows 11 Home（build 26300） |
| GPU | NVIDIA GeForce RTX 5080（16GB）。表示は 3840x2160 のモニター |
| PowerShellバージョン | 7.6.7 |
| ビルドに使ったVRChat Worlds SDK | `watch-worlds-spike.md`（2026-09-02）のビルドを流用。build-A＝空のシーン、build-B＝キューブを置いたシーン。SDKのバージョンは未記録（Unity 2022.3.22f1）。同期ボタンは無い |
| Logging設定（ケース6以外） | 完全（Full） |
| アカウント構成（profile 0 / 1） | profile 0 = 普段のアカウント（#1） / profile 1 = 検証用に新規作成したアカウント（#2）。ユーザーIDが違うことをログで確認した。ケース1〜3・6・7は profile 0 だけを使った |

同期ボタンが無いため、同じインスタンスにいるかは、各台の `output_log` の `Destination set` と `OnPlayerJoined` の件数、画面に他の台のアバターが映るかで判断した。ログの `usr_` は伏せてある。

### ケース1: 同じprofileの2台を `--url` で起動

| 項目 | 結果 |
|------|------|
| 実行コマンド | `.\start-vrchat-dev.ps1 -Clients 2 -WorldPath C:\VRCRemoteTest\spike\build-A.vrcw` |
| roomId | 7535294232 |
| PID（1台目 / 2台目） | 7052 / 29764 |
| 同じインスタンスか | はい。2台とも同じ `local:dab8e4…:1` に入り、各台のログに `OnPlayerJoined` が2件ずつ出た。2台目の画面に1台目のアバターが映った |
| Master（owner）になった台 | 2台目（先にロードを終えた台。ログに `I am MASTER`） |
| 観察事項 | roomId はインスタンスIDに使われていない。ログに roomId が出るのは `Arg: --url=...` の行だけで、行き先は `local:<64桁の16進>:1` になる。roomId を変えて起動した計4回（ケース1・6・7）とも、行き先は同じ `local:dab8e4…:1` だった。起動から入室完了まで約16秒 |
| output_log 関連行（取得できた場合） | 下記 |

```
（1台目）12:07:09 [Behaviour] Destination set: local:dab8e405667a4de729f91623db09e31f57071efe70ea4b1ee764bd3563dd5383:1
（1台目）12:07:10 [Behaviour] Joining local:dab8e405667a4de729f91623db09e31f57071efe70ea4b1ee764bd3563dd5383
（1台目）12:07:21 [Behaviour] OnPlayerJoined <表示名> (usr_***)   ← 2件
（2台目）12:07:10 [Behaviour] Destination set: local:dab8e405667a4de729f91623db09e31f57071efe70ea4b1ee764bd3563dd5383:1
（2台目）12:07:20 [Behaviour] I am MASTER
（2台目）12:07:21 / 12:07:23 [Behaviour] OnPlayerJoined <表示名> (usr_***)   ← 2件
```

### ケース2: 同じprofileの2台で配置

| 項目 | 結果 |
|------|------|
| 1回目（build-B）: 2台ともリロードしたか | はい（画面で2台ともキューブのシーンに変わった） |
| 1回目: リロード後も同じインスタンスか | はい。2台とも `local:ce318f…:1` に入り、`OnPlayerJoined` が2件ずつ |
| 1回目: リロード所要時間（概算） | 約8秒（12:08:16 配置 → 12:08:24 `Finished entering world`） |
| 2回目（build-A）: 2台ともリロードしたか | はい |
| 2回目: リロード後も同じインスタンスか | はい（同じ `local:ce318f…:1`、`OnPlayerJoined` 2件ずつ） |
| 2回目: リロード所要時間（概算） | 約2〜4秒（12:08:52 配置 → 12:08:54 / 12:08:56） |
| 観察事項 | watch-worlds のリロード先は、配置したワールドにも起動時の `--url` にも関係なく、常に `local:ce318f…:1` だった。同じ日の 03:03 以降に1台で起動し、Bridge で配置したときのログでも同じIDになっている。Master はリロードのたびに入り直した順で決まり、1回目は1台目、2回目は2台目だった。リロード時に画面に「パブリック インスタンスに入りました」のトーストが出た。同じアカウントの2台なので `Ignoring TrySetOwner attempt on Player Data Object [n] because <表示名> already owner` の Warning が出た |
| output_log 関連行（取得できた場合） | 下記 |

```
（2台とも）12:08:16 World Updated: ...\Worlds\vrc-test-20261017T120816344.vrcw // 1
（2台とも）12:08:16 [Behaviour] Destination set: local:ce318f3e82d614028e1b7fd1c58e2dc69783d28f221338974d3230b42f743c4a:1
（2台とも）12:08:16 [Behaviour] Resetting game flow because "Destination instance set": [SkipLogin, RemainInNetworkRoom]
（2台とも）12:08:17 [Behaviour] Joining local:ce318f3e82d614028e1b7fd1c58e2dc69783d28f221338974d3230b42f743c4a
（1台目）12:08:24 [Behaviour] I am MASTER
（2台とも）12:08:24〜25 [Behaviour] OnPlayerJoined <表示名> (usr_***)   ← 2件
```

### ケース3: 同じprofileの2台を `--url` なしで起動

| 項目 | 結果 |
|------|------|
| 起動直後の各台の状態 | 2台ともホームワールド（private インスタンス、台ごとに別の番号）へ行こうとして `Failed to join instance ... オフラインテストモードでいる間は、オンラインのワールドに移動することはできません` で失敗した。約20秒後に、台ごとに別の Error World（`local:error_<乱数>:1`、各台1人、画面は "WE'RE SORRY! SOMETHING WENT WRONG"）に入った。起動から Error World まで約25秒 |
| 配置後、同じローカルインスタンスに入ったか | はい。build-A の配置から約7秒で、2台とも `local:ce318f…:1` に入り、`OnPlayerJoined` が2件ずつ出た |
| 観察事項 | 同じアカウントなら、`--url` が無くても最初の配置で全台がそろう。`--url` で起動する利点は、インスタンスをそろえることではなく、起動直後からテストワールドに入れて Error World を経由しないこと |

### ケース4: 別profileの2台を `--url` で起動

| 項目 | 結果 |
|------|------|
| 実行コマンド | `.\start-vrchat-dev.ps1 -Profiles 0,1 -WorldPath C:\VRCRemoteTest\spike\build-A.vrcw` |
| roomId | 6265622845 |
| 同じインスタンスか | はい。profile 0（アカウント#1）と profile 1（アカウント#2）が同じ `local:dab8e4…:1` に入り、各台のログに #1 と #2 の `OnPlayerJoined` が出た |
| 観察事項 | `--url` の行き先は、アカウントが違っても同じ `local:dab8e4…:1` だった。2つ目のアカウントは新規作成したもので、アバターは既定のロボット |

### ケース5: 別profileの2台で配置

| 項目 | 結果 |
|------|------|
| 2台ともリロードしたか | はい。build-B（13:40:38）と build-A（13:41:38）の2回とも、約2秒でリロードした |
| 同じインスタンスか / 分かれたか | **分かれた**。2回とも、アカウント#1 は `local:ce318f…:1`、アカウント#2 は `local:ac7be6…:1` に入り、各台が1人きり（両方とも `I am MASTER`、`OnPlayerJoined` は自分だけ） |
| 観察事項 | 報告されている不具合（2025-07-25 から tracked）は build 1921 でも残っている。watch-worlds のリロード先 `local:<hash>:1` はアカウントごとに決まり、同じアカウントなら同じ（ケース2・7）、違えば別になる。そのあと全台を終了し、`-Profiles 0,1 -WorldPath ...\build-B.vrcw` で起動し直すと、2台とも `local:dab8e4…:1` にそろった（起動から2台の入室まで約12秒）。案Cの「配置のたびに共通の `--url` で起動し直す」方式で、別アカウントでもそろえられる |

```
（profile 0 / #1）13:40:38 [Behaviour] Destination set: local:ce318f3e82d614028e1b7fd1c58e2dc69783d28f221338974d3230b42f743c4a:1
（profile 0 / #1）13:40:40 [Behaviour] I am MASTER
（profile 1 / #2）13:40:38 [Behaviour] Destination set: local:ac7be6fd956445855eb8affab7cc0c6e98b8aec533496f648d06b6edfd3888d7:1
（profile 1 / #2）13:40:40 [Behaviour] I am MASTER
```

参考: 先に profile 0・1 とも同じアカウントで同じ手順を行ったときは（13:10〜13:12）、ケース1・2と同じく2台とも `local:ce318f…:1` にそろってリロードし、リロード後30秒間に切断も起きなかった。`--profile` の有無ではなく、アカウントが違うかどうかで分かれる。

### ケース6: Logging設定と黒画面

| Logging設定 | 2台目以降の状態 | 備考 |
|------|------|------|
| None | 黒画面にならず、build-A に入った。2台目の画面に1台目のアバターが映った | `output_log` は2台とも0バイトで、何も書かれなかった |
| Errors Only | 黒画面にならず、build-A に入った | `output_log` は Error の行だけ（25〜31行）。`World Updated`・`Destination set`・`OnPlayerJoined` は出ないので、ログでは同じインスタンスかを判断できない |
| profileごとに別設定か | 別ではない（全profileで共通） | 設定は `HKCU\Software\VRChat\VRChat` の `LOGGING_ENABLED_h120798204` に、変えた時点で保存される（オフ = -1、エラーのみ = 0、完全 = 1）。profile 1 の台で「エラーのみ」に変えると、profile 0 で変えたときと同じこの値が書き換わり、profile 専用の値は増えなかった（確認後「完全」に戻した） |

報告されている黒画面は、build 1921 では None でも再現しなかった。確認後、profile 0 は元の「完全」に戻した。

### ケース7: 3台と4台

| 台数 | 全台が同じインスタンスか | 配置後のリロード | "Can't Travel" や追い出し | 備考 |
|------|------|------|------|------|
| 3 | はい（`local:dab8e4…:1`、各台 `OnPlayerJoined` 3件） | はい。build-B の配置から約8秒で全台が `local:ce318f…:1` に入った | なし | 2台目以降に `VRCNP: Failed to create server: System.IO.IOException: すべてのパイプ インスタンスがビジーです。` が出る（名前付きパイプのサーバーは1台目だけが作れる）。起動から全台の入室まで約16秒 |
| 4 | はい（各台 `OnPlayerJoined` 4件） | はい。約8秒で全台そろった。配置後もしばらく退室は起きなかった | なし | 2〜4台目で `LoadItemsAsync, Exception: "whatever it is that you are doing, you are doing it too quickly. slow down!"`（APIのレート制限）が1台あたり7〜13回出た。入室には影響しなかった。起動から全台の入室まで約21秒 |

### ケース8: Moonlight越しの操作と負荷

負荷は Windows 上で4台（3台）のときに計測した。Moonlight 越しの確認は、2台（同じアカウント、13:50 起動）を左右に並べた状態で、Mac の Claude が Moonlight を自動操作して行った。人が手でマウスを動かした場合の確認はまだ。

| 項目 | 結果 |
|------|------|
| 台数 | 4（3台でも計測） |
| 全台を1画面で見られたか | 見られた。Windows では 3840x2160 のモニターに 2x2 で4台を並べられた。Moonlight 越しでも左右に並べた2台が1画面に収まり、左の台に右の台のアバターが映っているのを確認できた。起動直後は全台が同じ位置・同じ大きさ（前回終了時の 3840x2080 のウィンドウ）で重なるので、検証用に `SetWindowPos` で並べ直した（起動引数は変えていない） |
| 台の切り替え方法と操作感 | マウスだけでは切り替えられないが、Moonlight 越しに Option+Tab で切り替えられる。Windows 上の自動操作では、クリックした台がマウスカーソルをロックし、別の台をクリックしても入力が移らないことがあった（Esc でメニューを開くか、ウィンドウを前面に出すと移った）。Moonlight 越しの自動操作では、クリックもキー入力も常に右の台に入り、Esc でカーソルを解放したあとも左の台へ切り替えられなかった。ただし自動操作はカーソルを絶対位置へ動かす方式で、Moonlight の相対マウスでは Windows 側のカーソルが動いていない可能性がある。別アカウントの2台で、人が Moonlight 越しに手でマウスを動かしても、マウスだけではウィンドウを行き来できなかった。VRChat は前面にある間カーソルを隠してウィンドウ内に閉じ込め、マウスの動きを視点の操作に使うので、Moonlight の相対マウスの動きはすべて前面の台の視点操作になる。台を切り替えるには、ウィンドウを切り替える操作が要る。Moonlight 越しに Mac で Option+Tab を押すと、台を切り替えられた（人の手で確認。別アカウントの2台、PID 8596 / 28192 を左右に並べた状態で、14:10〜14:13 に試した）。Moonlight はウィンドウ表示。Option を押したままにすると Windows の Alt+Tab の一覧が出たので、Mac の Option は Windows に Alt として届いている。VRChat のメニューを開かず、カーソルが閉じ込められた状態からそのまま効いた。切り替えたあと、クリックも WASD での移動もその台に入った。全画面表示の Moonlight と、Cmd+Tab の挙動は未確認。VRChat のログにはフォーカスの変化が出ないので、Windows 側のログでは裏付けられない（結果はユーザーへの確認による）。Windows 上では、PID を指定してウィンドウを前面に出す方法（`WScript.Shell` の `AppActivate`）で毎回切り替えられた |
| GPU使用率 | 3台: 25%（nvidia-smi）、4台: 29%・約100W、VRAM 5.5GB / 16GB。空に近いシーンでの値 |
| 各台のFPS | 未計測（2台のとき、Settings のデバッグ欄で 60） |
| 観察事項 | メモリは4台で約10.8GB（1台あたり2.6〜2.9GB のワーキングセット）。終了時に各台が自分のウィンドウ位置と大きさをレジストリに保存するので、並べ直すと次回の起動時の大きさも変わる（検証後に元へ戻した） |

### オプションテスト結果（実施した場合のみ）

| ケース | 結果 | 備考 |
|--------|------|------|
| A. Bridge経由のデプロイ | 成功。2台とも同じインスタンスへリロードした | 同じアカウントの2台（13:50 に `--url` で起動）の状態で、Mac の Unity から `Remote Build & Test` を実行した。Bridge のログに `Multiple VRChat processes detected (PIDs: 20580, 35768). Selecting the most relevant one.` が出て、13:57:53 に `Build 20261017T045747579Z-c724cc35 deployed`。2台とも `World Updated: ...\vrc-remote-20261017T045747579Z-c724cc35.vrcw` のあと `local:ce318f…:1` に入り、13:58:01 に入室を終えた（約8秒）。Moonlight の画面でも、左の台に右の台のアバターが映った。別件として、Bridge のログに `Unhandled error while polling VRChat process status.`（`UnauthorizedAccessException`）が10秒ごとに出ていたが、スパイクより前の 10:00 から出ており、複数台とは関係ない（VRChat ログ配信のエラーと同じ原因。Mac の Unity が共有の `vrchat-status.json` や `vrchat-latest.log` を開いている間は、Bridge の `File.Move(overwrite: true)` による置き換えが失敗する。別ブランチで修正中）。**別アカウントの2台でも実施した**（14:08 に `-Profiles 0,1` で起動、2台とも同じ `local:dab8e4…:1` に入った状態から）。14:10:10 に `Build 20261017T051006056Z-075d4057 deployed` で配置は成功したが、アカウント#1 は `local:ce318f…:1`、#2 は `local:ac7be6…:1` にリロードして分かれ、どちらも1人きりになった。Moonlight の画面でも、2台とも相手のアバターが見えなくなった。ケース5と同じで、今の Bridge では別アカウントの複数台はそろわない |
| B. 同時起動 | 2台・4台とも、全台が同じインスタンスに入った。固まる台はなかった（2026.3.2 の修正が効いている） | 2台（`-Clients 2 -LaunchIntervalSeconds 0`）: 約14秒で2台とも入室。エラーの数と種類は3秒間隔のときと同じだった。4台（`-Clients 4 -LaunchIntervalSeconds 0`）: 画面では4台とも build-A に入り、互いのアバターが見えた。ただし4台が同じ秒に起動したため、`output_log` のファイル名（秒までの時刻）が重なり、4台が `output_log_2026-10-17_13-46-59.txt` の1ファイルに書き込んだ。ファイルには実質1台分しか残らず、行が途中で別の行とつながっていた。API のレート制限エラーは134回（3秒間隔の4台では1台あたり7〜13回） |
| C. VRとデスクトップ | | |
| D. 同じアカウントで区別できないもの | | |

### 追加検証: Bridge への組み込み前のゲート（2026-10-17 15:41〜15:51）

Bridge に複数台起動を組み込む計画（案B: 同じアカウント、案C: 別アカウント）のレビュー後、実装の前に確かめた。案C は「Bridge が起動した台を終了 → 配置 → `--url` と `--profile=N` で起動し直す（`--watch-worlds` は付けない）」方式で、Bridge は自分が起動した台を PID と作成時刻で記録し、その台だけを終了させる。

#### 案C ゲート1: 起動で返る PID はゲーム本体か — 合格

`VRChat.exe` を `start-vrchat-dev.ps1` と同じ引数・作業ディレクトリで `Start-Process -PassThru` で起動した（`--url`、`--watch-worlds`、`--profile=0`）。

| 時点 | 返った PID 31832 | ウィンドウ（`MainWindowHandle`） | ほかのプロセス |
|------|------|------|------|
| 起動直後（t+0.1秒） | 生きている。`StartTime` 15:41:07.3505842 | 0 | `UnityCrashHandler64.exe`（親は 31832、引数 `--attach 31832 ...`） |
| 5秒後 | 同じ PID・同じ `StartTime` | あり（31832 が持つ） | 同上 |
| 15秒後・30秒後 | 同上 | 同上 | 同上 |
| 入室後（入室は t+9秒） | 同上 | 同上 | 同上 |

- `Get-Process` と `Win32_Process` の両方で、PID・作成時刻・パス（Steam の `VRChat.exe`）・コマンドライン（`--url=...`）が最初から最後まで同じだった。親プロセスは起動した PowerShell。
- EasyAntiCheat や `start_protected_game` のプロセスは現れなかった。子プロセスは `UnityCrashHandler64.exe` だけで、VRChat の終了と一緒に終わった（`CloseMainWindow` で確認）。

#### 案C ゲート2: `--watch-worlds` なしで、終了 → 配置 → 起動し直す — 合格

別アカウントの2台（profile 0 = #1、profile 1 = #2）で、`start-vrchat-dev.ps1 -Profiles 0,1 -NoWatchWorlds -WorldPath <ワールド>` → 2台に `CloseMainWindow` → `deploy-test.ps1` で次のビルドを配置 → 配置したファイル（`Worlds\vrc-test-*.vrcw`）を `-WorldPath` にして起動し直す、をくり返した。

| 回 | ワールド | 行き先（2台とも） | お互いの `OnPlayerJoined` | 起動から2台の入室まで | 終了にかかった時間 |
|------|------|------|------|------|------|
| 0 | build-A（spike のファイル） | `local:dab8e4…:1` | #1・#2 とも2件 | 9秒 | 3.8秒 |
| 1 | build-B（配置したファイル） | `local:dab8e4…:1` | 同上 | 15秒 | 1.5秒 |
| 2 | build-A（配置したファイル） | `local:dab8e4…:1` | 同上 | 14秒 | 2.3秒 |
| 3 | build-B（配置したファイル） | `local:dab8e4…:1` | 同上 | 14秒 | 1.9秒 |

- 毎回そろい、インスタンスIDも毎回同じだった（roomId は毎回違う）。
- 画面は見ていない。読み込んだワールドが指定したファイルであることは、13:42 に build-B を指定して起動し直したときに画面で確かめている。

#### 案C ゲート3: `CloseMainWindow` と強制終了をくり返した影響

`CloseMainWindow`（profile 0 の1台）:

| 状態 | 戻り値 | 終了まで |
|------|------|------|
| 入室後 | True | 1.5秒 |
| 最小化した状態（入室後に `ShowWindow` で最小化） | True | 2.7秒 |
| 起動から1.0秒（すでにウィンドウがある） | True | 起動から3.6秒で終了（1回送るだけで足りた） |
| 起動から2.0秒 | True | 起動から3.5秒で終了 |
| 起動から0.3秒（ウィンドウがまだ無い） | False | 終了しない（40秒待って強制終了した）。ウィンドウができたあとに送り直す必要がある |

- ゲート2の2台の終了も1.5〜3.8秒だった。10秒の猶予があれば、ウィンドウがある台は正常終了が間に合う。
- ウィンドウができるのは起動から1秒前後（1.0秒の時点ですでにあった）。

`Stop-Process -Force` を3回くり返した（1回ごとに入室まで起動し直して確認）:

- 3回とも、次の起動はそのまま約8秒で入室した。ログインし直しは求められず（`User Authenticated` が出て、ログアウトや資格情報の行は無し）、ウィンドウの位置と大きさ（-7,0〜3849,2119）、Logging（完全）も変わらなかった。
- 強制終了そのもので変わった設定値（`HKCU\Software\VRChat\VRChat`、709件）は無かった。起動のたびに `unity.player_session_count`・`unity.player_sessionid` と、名前がハッシュの値2件（資格情報に関するものと見られる。中身は見ていない）が変わる。正常終了したときだけ `VRC_TRACKING_GRACEFUL_QUIT` と `LocationContext`・`LocationContext_World` が書き換わる。強制終了ではこれらが書かれないが、次の起動に影響は見られなかった。

#### 案B: `--url` で起動した直後、何秒後の配置から `--watch-worlds` が拾うか

同じアカウントの2台を `-Clients 2 -WorldPath build-A`（3秒間隔）で起動し、1台目の起動から N 秒後に build-B を配置した（N ごとに起動し直す）。

| N | 1台目 / 2台目の最初の入室 | 配置後 | お互いの `OnPlayerJoined` |
|------|------|------|------|
| 5 | t+9 / t+10（配置を拾った1台目の入り直しを含む） | **分かれた**。1台目は `local:ce318f…:1`、2台目は `--url` の `local:dab8e4…:1` に入った | 各台1件（自分だけ） |
| 10 | 2台とも `--url` のワールドへの入室前に配置を拾った | 2台とも `local:ce318f…:1`、配置から8秒で入室 | 各台2件 |
| 15 | t+9 / t+12 | 2台とも `local:ce318f…:1`、7〜8秒 | 各台2件 |
| 20 | t+8 / t+11 | 同上 | 各台2件 |
| 25 | t+8 / t+11 | 同上 | 各台2件 |
| 30 | t+7 / t+11 | 同上 | 各台2件 |

- N = 5 で分かれた理由: 2台目（配置の2秒前に起動）は、ログインの前（`User Authenticated` より前）に `World Updated` を受け取り、その後のログインで `--url` の行き先に入った。1台目はログインのあとに受け取ったので、リロード先に移った。**配置を拾うのは、ログインが済んだ台だけ**。
- 1回目の N = 20 は、スクリプトが入室直後に台を閉じたため相手の入室を記録できず、やり直した（上の表はやり直しの結果）。
- 今の Bridge の「起動から30秒は配置を待つ」は、`--url` で起動する場合は下げられる。目安は「全台のログに `User Authenticated` が出てから配置する」。ログを使わず固定の時間にするなら、最後の台の起動から10秒以上（今回は7秒後でそろい、2秒後では分かれた）。ただし `User Authenticated` は Debug の行なので、Logging が「完全」でないとログに出ない。

#### 任意: 昇格した VRChat の SessionId と StartTime

未実施。管理者として VRChat を起動するには、Windows の前で UAC の確認に応える必要がある。

### Bridge への組み込み後の確認（PR #2）（2026-10-17 17:49〜18:10）

PR #2（同じアカウントの N 台起動、`VrchatClientCount`）を Windows 実機で確かめた。

**試したビルド**: PR #2 の先頭 `752bdae` に、PR #1（`d39650f`、共有ファイルの置き換えの修正）を手元でマージした `f91ff0e`（push していない）。PR #2 だけでは、Mac が共有のファイルを開いている間 Log Viewer と状態の更新が止まる既知の不具合が残り、項目6・13 の確認が濁るため。両者に競合は無かった。Windows（.NET SDK 10.0.400）で `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` で作った exe（SHA-256 `9F74993C…AB14B59`）を、PR #2 のチェックアウトの `install-bridge.ps1` で入れ替えた。

**ビルドの送り方**: Mac の Remote Build & Test は使わず、Windows 上で同じ形のアップロードを模擬した（`incoming` に `<buildId>.vrcw.part` を書いて rename、続けて `<buildId>.ready.json.part` を書いて rename。中身は Unity の `SmbRemoteTransport` と同じ manifest）。結果は `results\<buildId>.json` を 120 秒まで待って読んだ（Unity の Result Timeout と同じ）。ワールドは build-A / build-B。同じインスタンスにいるかは、スパイクと同じく各台の output_log の行き先（`local:…`）と `OnPlayerJoined` で確かめた。ゲーム内の Logging は「完全」。

#### テスト（Windows）

| 対象 | 結果 |
|------|------|
| PR #2（`752bdae`） | 296 件中 293 件成功 |
| PR #2 ＋ PR #1（`f91ff0e`） | 303 件中 300 件成功 |

どちらも、落ちたのはシンボリックリンクの作成権限が要る3件（`Fails_when_StateDirectory_is_a_symbolic_link`、`A_symbolic_link_manifest_is_never_ingested`、`A_symbolic_link_artifact_is_never_copied`）だけ。

#### 項目ごとの結果

| 項目 | 結果 | 記録 |
|------|------|------|
| 1. VRChat 0 台からの送信（`VrchatClientCount: 2`） | 合格 | 送信から 7.4 秒で `deployed`（Bridge が拾うまでの約4秒を含む）。`Launched VRChat client 1/2`（17:52:17.653）と `2/2`（17:52:20.671）が 3.0 秒差・同じ roomId。2台とも `local:dab8e4…:1` に入り、お互いの `OnPlayerJoined` が出た。1台目の起動の 0.1 秒後に `VRChat log not published: Could not match ...` が1回出て、5秒後に1台目のログに固定された（下の「気づいたこと」1） |
| 2. 続けてもう一度送る | 合格 | 最後の台の起動から 30.4 秒後に送ったので待ちは無く、5.7 秒で配置。2台とも `local:ce318f…:1` へそろってリロードした。30 秒以内に送ったときの待ちは項目8の settle 30 で確かめた（30.1 秒後に配置） |
| 3. `VrchatClientCount: 1` で 0 台から送る | 合格 | 3.5 秒で `deployed`、`Launched VRChat client 1/1`、起動から約9秒で入室 |
| 4. 2台のうち1台を閉じて送る（2回） | 合格 | 1回目に `VRChat clients changed: deploying to 1 of 2 VRChat client(s); ...` が1回だけ出て成功し、残りの台がリロードした。2回目は警告なしで成功 |
| 5. `-NoWatchWorlds` の台だけ | 合格 | `VRCHAT_WATCH_WORLDS_MISSING`（2.2 秒）。何も起動しなかった |
| 5. `--watch-worlds` 付きと混在 | 合格 | `... deploying to 1 of 2 ...; 1 VRChat client(s) without --watch-worlds will not reload.` の警告が1回。付きの台が起動した直後だったので `Waiting for 1 VRChat client(s) ... = 30` のあと 30 秒で配置。付きの台だけがリロードし、無しの台のログには配置したファイルの `World Updated` が出なかった |
| 6. Unity v0.3.0 の Preflight と Log Viewer | 合格（Unity での表示は 19:25〜19:28 に確認、下の「項目6: Unity での表示」） | 共有の `status\vrchat-status.json` は 10 秒ごと、`logs\vrchat-latest.log` は 5 秒ごとに更新され続けた（PR #1 の修正が効いている）。Log Viewer の写しは1台目（最初に起動した台。混在のときは `--watch-worlds` 無しの台）のログに固定され、status の代表は `--watch-worlds` 付きの最も古い台だった。`start-vrchat-dev.ps1 -Clients 2 -LaunchIntervalSeconds 0`（13ms 差で起動）では、写しが `Could not match an output_log_*.txt file to the first VRChat client ...` の1行になった（このときのログ名は `17-56-44` と `17-56-45` に分かれていたが、2台とも同じ秒に起動しているので対応付けを拒否した）。Unity での表示は、あとで `50ad181` のビルドで確かめた |
| 7. `VrchatMode: VR` で2台 | 未実施 | HMD が要る |
| 8. settle 15・20・25・30 でコールドスタート直後に次を送る | 合格 | どの値でも `Waiting for 2 VRChat client(s) to finish starting before deploying (VrchatStartupSettleDelaySeconds = N)` のあと、最後の台の起動から 15.6 / 20.8 / 26.0 / 30.1 秒で配置し、2台とも `local:ce318f…:1` へそろってリロードした（各台 `OnPlayerJoined` 2件） |
| 9. 昇格した VRChat の数え方 | 未実施 | UAC の操作が要る |
| 10. 4台のコールドスタート中に Bridge を強制終了 | 合格 | `Launched VRChat client 2/4` の直後（18:08:46）に `Stop-Process -Force`。起動済みの2台はそのまま残り、Bridge を起動し直しても配置し直しも追加の起動もしなかった。そのビルドの結果ファイルは書かれず（Unity では RESULT_TIMEOUT になる）、manifest は `state\archive` に入った。次のビルドは `deploying to 2 of 4 ...` の警告1回のあと残りの2台に配置され、2台ともそろってリロードした |
| 11. Bridge の二重起動 | 合格 | タスクの Bridge が動いている状態で exe を手で起動すると、`[FTL] Another VRC Remote Test Bridge is already running (lock file ...\state\bridge.lock is in use); refusing to start.` を出して終了コード 1。拒否された側は、ログを別名の `bridge-20261017_001.log` に書いた |
| 12. ログ名の遅れ | 負荷なしは合格。**CPU 負荷が高いと前提が崩れる** | 下の表 |
| 13. 4台の起動中の WMI と更新 | 負荷なしは合格 | PR #2 を入れてからの Bridge のログに `Failed to query VRChat command lines via WMI` は0件、`[ERR]` も0件。4台の起動中も status は 10 秒ごと、Log Viewer の写しは 5 秒ごとに更新された。CPU 負荷が高いときは status の間隔が 16〜21 秒、写しは 30 秒で3回に延びた（止まりはしない） |

#### 項目12: ログ名の遅れ（`VrchatClientCount: 4`、3秒間隔）

負荷なし（3回、計12台）: どの台も、ログファイルは起動の 0.70〜0.84 秒後に作られ、名前の時刻は起動の時刻を秒に切り捨てた値か、その次の秒だった（名前 − 起動 = −0.29〜+0.27 秒）。1秒を超えて遅れた台は無い。Bridge は毎回1台目のログに固定した（1台目の起動から 1.3〜5.6 秒後に `Showing ...`）。

CPU 負荷あり（16 個の論理 CPU すべてを `pwsh` の空回しで使い切り、CPU 使用率約100%、2回）:

| 回 | 起動（Bridge のログの作成時刻） | ログファイル | 結果 |
|------|------|------|------|
| 1 | 1台目 18:03:59.922、2台目 18:04:07.608（間隔が 7.7 秒に延びた）、3台目 18:04:13.077、4台目 18:04:16.093 | `18-04-10` と `18-04-11`（どちらも 18:04:11 に作成）、`18-04-13`（+0.74 秒）、`18-04-16`（+0.76 秒） | 1台目と2台目のログができたのは 18:04:11 で、1台目は起動から約 11 秒遅れた。名前が1台目の範囲（18:03:58〜18:04:07）に入らず、Bridge は `Could not match ...` を出したまま固定しなかった |
| 2 | 1台目 18:05:02.222、2台目 18:05:07.280、3台目 18:05:14.659、4台目 18:05:22.254 | `18-05-24` の1つだけ（18:05:24.442 に作成） | **4台すべてが同じファイルに書き込んだ**（`User Authenticated` 2行、`Arg: --watch-worlds` 1行、`OnPlayerJoined` 4行が混ざっていた）。1台目のログは起動から約 22 秒遅れ。Bridge は `Could not match ...` を出したまま固定しなかった |

- 負荷が高いと、VRChat がログファイルを作るのが起動から最大 20 秒以上遅れ、3秒以上あけて起動した台どうしでもファイル名の秒が重なる。スパイクのオプションB（同じ秒に起動）と同じく、ログが1つのファイルに混ざる。
- どちらの回も、Bridge は別の台のログを1台目のものとして表示せず、診断の1行を出し続けた（安全側に倒れている）。ただし、その間 Log Viewer にログは出ない。

固定した台を閉じたとき（負荷なし、4台）: 1台目（固定していた台）を閉じると、Bridge は2台目のログ（`18-06-37`）に固定し直した（`Showing ... as the log of VRChat client 26336`）。閉じた台のログを残りの台のものとして表示することは無かった。固定していない4台目を閉じても、固定は変わらなかった。

#### 気づいたこと（不具合ではないもの、判断が要るもの）

1. コールドスタートのたびに、1台目の起動から 0.1〜0.6 秒後の観測で `VRChat log not published: Could not match ...` が出る。まだログファイルができていない（作られるのは起動の約 0.7〜0.8 秒後）ためで、次の観測（約5秒後）で固定される。その間 Log Viewer の写しは診断の1行になる。
2. CPU 負荷が高いと、ログ名の前提（起動から約1秒）が崩れ、Log Viewer が固定できないままになる（項目12）。
3. status の `startTimeUtc` は `"2026-10-17T08:55:00.5442545+00:00"` の形だった（UTC だが `Z` ではない）。PR #1 の書き出しと合わせたビルドでの値で、Unity は `DateTimeOffset` で読むので互換。
4. CPU 負荷が高いと、Bridge が台を起動する間隔が3秒より延びる（`Start-Process` 自体が遅くなる）。roomId は同じで、全台が起動した。

#### 確認後の後片付け

- `config.json` は確認前のバックアップから元に戻した（`VrchatClientCount` なし、settle 30 秒、timeout 60 秒）。Bridge の exe は `f91ff0e` のビルドのまま（確認前の exe は `%LOCALAPPDATA%\VRCRemoteTest\pr2-test-backup-20261017-175136\` に退避した）。
- ゲーム内の Logging は確認前から「完全」だったので、`setup-windows.md` §9-2 の戻し方（output_log と共有の写しの削除）は行っていない。

#### `a9292a1` の再確認（2026-10-17 19:05〜19:08）

`a9292a1`（1台目の起動から10秒の間は、ログを対応付けできなくても診断を出さない）を、PR #1（`d39650f`）と手元でマージした `50ad181`（push していない）で確かめた。Windows の `dotnet test` は 306 件中 303 件成功（落ちたのはシンボリックリンクの3件だけ）。exe（SHA-256 `861BBD4B…0134F7E`）を `install-bridge.ps1` で入れ替え、`VrchatClientCount: 2` で、上と同じ模擬のアップロードを使った。

| 確かめたこと | 結果 |
|------|------|
| コールドスタートの直後に診断の1行が出ないか（2回） | 出なかった。Bridge のログにも `VRChat log not published` は無く、1台目の起動から 5.1 秒後・2.2 秒後に `Showing ... as the log of VRChat client ...` で固定された |
| 同じ秒に起動したとき（`start-vrchat-dev.ps1 -Clients 2 -LaunchIntervalSeconds 0`、14ms 差）に、これまでどおり診断が出るか | 出た。1台目の起動から 13.4 秒後（10 秒の猶予のあとの次の読み取り）に `VRChat log not published: Could not match ...` が出て、写しが診断の1行になった |

気づいたこと: 対応付けを待つ間（`LogSnapshotResult.Empty()`）は `VrchatLogService` が写しを書かないので、共有の `vrchat-latest.log` には**前のセッションのログ（閉じた台のログ）が残る**。修正の前は、最初の読み取りで診断の1行がこれを上書きしていた。今回は、コールドスタートで固定されるまでの 2〜5 秒間と、同じ秒に起動したときの約 13 秒間、閉じた台の古いログが写しに残っていた。Unity の Log Viewer には、その間この古いログが表示されると見られる（Unity での表示は未確認）。

後片付け: `config.json` は再び確認前のバックアップに戻した。Bridge の exe は `50ad181` のビルドのまま。

#### 項目6: Unity での表示（2026-10-17 19:25〜19:28）

Mac の Unity（Vrc-remote-test プロジェクト、パッケージ v0.3.0）で Preflight と Log Viewer を見た。Windows では `50ad181` のビルドの Bridge のまま（`config.json` は確認前の値）、`start-vrchat-dev.ps1` で VRChat を起動した。Mac 側の観察は Mac のセッションの報告による。

| 手順 | Windows 側 | Mac の Unity | 結果 |
|------|------|------|------|
| 1. `-Clients 2`（`--watch-worlds` 付き、`--url` なし、3秒間隔） | 1台目 PID 25568（19:25:30.244）、2台目 PID 29292（19:25:33.271）。19:25:35.700 に `Showing ...output_log_2026-10-17_19-25-30.txt as the log of VRChat client 25568.` | Preflight は「VRChat: Running」（緑）。Log Viewer に 19:25:55 の `[AssetBundleDownloadManager] [100] Unpacking World (local:error_7484453976886834)` が出た | 合格。2台は `--url` が無いので別々の Error World に入り、1台目のログは `local:error_7484453976886834`、2台目のログは `local:error_2111340501460972` だった。Log Viewer は1台目のログを表示していた |
| 2. `-Clients 2 -LaunchIntervalSeconds 0`（13ms 差） | 1台目 PID 13976（19:27:03.460）、2台目 PID 2300（19:27:03.473）。ログは `19-27-03` と `19-27-04` の2つ。19:27:16.744（1台目の起動から 13.3 秒後）に `VRChat log not published: Could not match an output_log_*.txt file to the first VRChat client (PID 13976, ...)` | Log Viewer に同じ診断の1行が出た | 合格。それまでの約13秒は、手順1の1台目（閉じた台）のログが共有の写しに残っていた（上の「`a9292a1` の再確認」で書いた件） |

### PR #3 の実機確認（2026-10-17 19:49〜20:09）

PR #1〜#3 を main にマージしたあと（#1 `687a8b0`、#2 `77f746a`、#3 `d8b57c0`）、main の `d8b57c0` を Windows で確かめた。PR #3 は「1台目の output_log ができるまで Log Viewer に前のセッションのログを残さない」（`8b3ab2d`）と「コールドスタートで、前の台の output_log ができてから次の台を起動する（合計60秒まで）」（`43bc8eb`）。

**準備**: Windows の `dotnet test` は 325 件中 322 件成功（落ちたのはシンボリックリンクの3件だけ）。手順1-B と同じ `dotnet publish` で作った exe（SHA-256 `01A2D8BA631D5EE576557E076692DFEB8A5EA5E525CE9576ECA39B0213396182`）を `install-bridge.ps1` で入れ替えた（それまでの `50ad181` の exe は `%LOCALAPPDATA%\VRCRemoteTest\pr2-test-backup-20261017-175136\VRCRemoteTest.Bridge.50ad181.exe` に退避）。`config.json` は `AutoLaunchVrchat: true`、`VrchatClientCount: 4`、settle 30 秒、timeout 60 秒。Logging は「完全」。ビルドは PR #2 の確認と同じ模擬のアップロードで送った。写し（`C:\VRCRemoteTest\logs\vrchat-latest.log`）は1秒ごとに読み、どのログのものかを、写しの最後の時刻付きの行を含むファイルで判定した。

負荷は2通りかけた。「Start-Job」は `Start-Job { while ($true) {} }` を論理 CPU の数（16）だけ、「pwsh ループ」は PR #2 の項目12と同じく `pwsh` で浮動小数点の計算を回すプロセスを16個。どちらも CPU 使用率は約100%。

#### 結果

| 確認 | 結果 | 記録 |
|------|------|------|
| A. 負荷なし・4台 | 合格 | `Launched` の間隔は 3.00 / 3.01 / 3.02 秒。`New output_log` が3行で、それぞれ起動の 0.8 / 0.7 / 0.8 秒後。`Waiting for` は出ない。写しは、1台目の起動の 0.2 秒後（19:50:45.851）に `The first VRChat client (PID 9960, ...) has not created its output_log_*.txt file yet. ...` の1行に置き換わり（それまでは起動前に書かれた前のセッションのログ）、5.2 秒後に `Showing ...output_log_2026-10-17_19-50-46.txt as the log of VRChat client 9960`。送信から `deployed` まで 12.2 秒（4台を起動し終えるまでの約9秒を含む。PR #2 項目1の2台では 7.4 秒）。4つのログは別々で、各ファイルの `User Authenticated` と `Arg: --watch-worlds` は1件ずつ。4台とも `local:dab8e4…:1`、`OnPlayerJoined` は4件ずつ |
| B. 負荷あり・4台（Start-Job で3回、pwsh ループで1回） | 4回とも1台目のログが60秒以内にできず、C の動きになった | 下の表。前の台のログを待ってから次を起動する流れ（`Waiting for` → `New output_log` → `Launched`）は、ログが60秒以内にできる負荷を作れず、確かめられなかった。Start-Job を8個（CPU 約93%）にすると、ログは 0.8〜1.0 秒でできて A と同じになった（B4）。送信から `deployed` までは 84〜96 秒（120 秒以内） |
| C. 60秒の上限 | 合格 | B の4回とも、`Waiting for VRChat client 1/4 to create its output_log file before launching the next client (up to 50〜56s more)` のあと、`[WRN] VRChat client 1/4 has not created an output_log file within 60s of the first launch; launching the remaining clients 3s apart. ...` が1回だけ出て、残りの3台が起動した（負荷で `Start-Process` が遅く、間隔は 3.1〜7.5 秒）。写しは `has not created ... yet` から `Could not match ...` に変わり、混ざったログや別の台のログを1台目のものとして出すことは一度もなかった（`Showing` は出なかった） |
| D. 負荷を止めてから1台目を閉じる | 合格 | B4 の4台（ログは別々）で、1台目（PID 11300）を閉じた3秒後に `Showing ...output_log_2026-10-17_20-03-31.txt as the log of VRChat client 8728`（2台目）。閉じた台のログも混ざったログも出なかった |
| E. 負荷ありで次のビルドを送る | 合格 | B4 の状態（Start-Job 8個）で、最後の台の起動から 30.4 秒後に送った。5.5 秒で配置し、4台ともそろって `local:ce318f…:1` へリロードした（`OnPlayerJoined` 4件ずつ）。準備待ち（settle）が効く送り方にはならなかった |
| F. `start-vrchat-dev.ps1 -Clients 4` を負荷ありで | 未実施 | 任意の項目 |
| G. どの PID がどのログを開いているか | 下の「G」 | リソースモニターは管理者権限で動くため使わず、非昇格の PowerShell から Restart Manager（`RmGetList`）で調べた |

B の各回（ログの作成時刻は `CreationTime`、ファイルを開いている PID は Restart Manager の結果）:

| 回 | 負荷 | 1台目の起動 → ログの作成 | 2〜4台目のログ | 写しに前のセッションのログが残った時間（起動から） | `deployed` |
|------|------|------|------|------|------|
| B1 | Start-Job 16 | 19:52:25.844 → `19-53-40`（作成は 19:54:13.848。名前と作成の差が 33 秒） | 別々（`19-54-27`・`19-54-36`・`19-54-38`、それぞれ PID 1つ） | 約15秒（19:52:41 まで） | 86.9 秒 |
| B2 | Start-Job 16 | 19:55:31.245 → `19-56-26`（19:56:48.317） | **3台で1ファイル**（`19-57-33` を PID 14080・21416・26276 が開いていた） | 約10秒（19:55:41 まで） | 84.2 秒 |
| B3 | Start-Job 16 | 19:59:36.402 → `20-00-48`（20:01:19.098） | 2台目は `20-01-33`、**3台目と4台目で1ファイル**（`20-01-41` を PID 18644・18068） | 約17秒（19:59:53 まで） | 96.3 秒 |
| B5 | pwsh ループ 16 | 20:06:07.829 → `20-08-05`（20:08:06.530、負荷が終わるころ） | **3台で1ファイル**（`20-08-06` を PID 24340・26920・4208） | 約12秒（20:06:19 まで） | 93.0 秒 |

どの回も、4台の行き先はすべて `local:dab8e4…:1` だった。

- 今日のこのマシンでは、CPU 使用率が約100%だと、1台目のログの作成は起動から 60 秒を超えた（4回中4回、最長で約2分）。PR #2 の項目12（同じ pwsh ループで 11〜22 秒）より重い。60 秒の上限のあとは、残りの台のログが混ざることが多かった（4回中3回）。
- 負荷が高いと、写しの更新（5秒ごと）も間があき（例: 19:52:16 の次が 19:52:41）、コールドスタートのあと 10〜17 秒は、起動前に書かれた前のセッションのログが写しに残っていた。Bridge が起動後に前のセッションのログを書いたのではない。負荷なしでは、起動の 0.2 秒後に置き換わった。
- 負荷が高いと、送信から結果まで 84〜96 秒かかった。Unity の Result Timeout が 60 秒のままだと RESULT_TIMEOUT になる。

#### G: ログを開いている PID（Restart Manager）

- 負荷なしの4台（A）では、4つのログをそれぞれ1つの VRChat が開いたままにしていて、`RmGetList` は起動順どおりの PID を1つずつ返した（`19-50-46` → 9960、`19-50-49` → 22156、`19-50-52` → 14800、`19-50-55` → 25348）。閉じたセッションのログ（`19-27-03`）を開いているプロセスは無かった。
- 混ざったファイルは、複数の PID が開いていた（B2・B5 は3つ、B3 は2つ）。
- B の4回とも、1台目は自分だけのファイルを持っていた（PID 1つ）。名前の時刻で対応付けられなかった（B1 は名前と作成の差が 33 秒、ほかは名前が1台目の範囲の外）回でも、Restart Manager なら1台目のログを特定できた。PID とログを直接結ぶ改善の材料になる。

#### PR #1 の確認（あとで見ること）

main のビルドを入れてから 20:09 まで、Bridge のログに `held open … replacing it with POSIX semantics` は出なかった。`[ERR]` と `Failed to query VRChat command lines via WMI` も0件。

### PR #5 の実機確認（2026-10-17 21:17〜21:44）

PR #5（Log Viewer が、1台目だけが開いている output_log を Windows の Restart Manager で選ぶ）の `67aa84f` を Windows で確かめた。PR #3 の確認の G を受けた変更。

**準備**: Windows の `dotnet test` は 364 件中 361 件成功。落ちたのはシンボリックリンクの3件だけで、Restart Manager を実際に呼ぶ `RestartManagerFileOwnersTests` の3件（`Reports_this_process_while_it_holds_the_file_open_and_not_after`、`Reports_every_process_that_holds_the_file_with_its_own_name`、`The_reader_shows_the_log_its_client_holds_even_when_the_name_cannot_match`）は成功した。手順1-B と同じ `dotnet publish` で作った exe（SHA-256 `1613550733E4C787C84D30AC5EF5800C3E76A3FBA64FBCD79B8EF5C0639F0774`）を `install-bridge.ps1` で入れた（それまでの main `d8b57c0` の exe と `config.json` は `%LOCALAPPDATA%\VRCRemoteTest\pr5-test-backup-20261017-211954\` に退避）。環境は PR #3 の確認と同じ（`VrchatClientCount: 4`、settle 30 秒、Logging「完全」、模擬のアップロード、負荷は `Start-Job { while ($true) {} }` ×16）。送ったワールドは build-A と同じ `Downloads` の元のファイル。各ログを開いているプロセスは、PR #3 の G と同じく Restart Manager で調べた。

| 手順 | 結果 | 記録 |
|------|------|------|
| 1. テスト | 合格 | 上のとおり |
| 2. 入れ替え | 済み | 上のとおり |
| 3. 負荷なし・4台 | 合格 | `Launched` は 21:20:28.692 / 31.699 / 34.705 / 37.713（3.0 秒間隔）、`New output_log` は 0.8 / 0.7 / 0.8 秒後。写しは1台目の起動の 0.1 秒後に `has not created its output_log_*.txt file yet` の1行になり、5.3 秒後に `Showing ...output_log_2026-10-17_21-20-29.txt as the log of VRChat client 23712 (the only VRChat process that has it open).`。送信から `deployed` まで 13.4 秒（PR #3 の A は 12.2 秒）。4つのログは、それぞれ1つの PID が開いていた（起動順に 23712、31840、6216、32336）。4台とも同じインスタンス |
| 4. 負荷あり・4台 | 合格 | 1台目（21:32:17.742 起動）のログは 60 秒以内にできず、21:33:20.507 に `within 60s` の警告のあと残りの3台を起動した。1台目のログ `21-33-45` ができたのは起動の 111 秒後（21:34:08.926）。それまで写しは `has not created its output_log_*.txt file yet` のままで、`Could not match` にはならなかった。ログができたあとの更新で `Showing ...output_log_2026-10-17_21-33-45.txt as the log of VRChat client 25160 (the only VRChat process that has it open).`（21:34:36.740。負荷で写しの更新が遅れ、ログの作成から 28 秒後）。3台目と4台目のログは1つのファイル `21-34-30` に混ざり（PID 8460・26496）、`...output_log_2026-10-17_21-34-30.txt is open by several VRChat processes; it is not shown as any one client's log.` が1回出た。2台目は自分だけのファイル `21-34-21`。送信から `deployed` まで 90.3 秒（PR #3 では 84〜96 秒） |
| 5. 混ざったファイルが1台目になるとき | 合格（読み替え） | 手順4の回は2台目ではなく3・4台目が混ざったので、起動順に閉じていった。1台目を閉じると、6.5 秒後に `Showing ...21-34-21.txt as the log of VRChat client 22112 (the only VRChat process that has it open).`（2台目の自分だけのファイル）。続けて2台目を閉じると、混ざったファイルを持つ3台目（PID 8460）が1台目になり、5 秒後に `VRChat log not published: output_log_2026-10-17_21-34-30.txt, the output_log_*.txt file of the first VRChat client (PID 8460, ...), is or was also open by other VRChat clients, ...` の1行になった。さらに4台目を閉じて3台目だけにすると、Restart Manager ではそのファイルを開いているのは 8460 だけになったが、写しは同じ1行のままだった（15 秒見て、Bridge のログにも変化なし） |
| 6. VRChat 以外のプロセスが開いているとき | 合格 | 手順3の4台で、pwsh（PID 28280）から1台目のログを `[System.IO.File]::Open(..., 'Open', 'Read', 'ReadWrite, Delete')` で20秒開いたままにした。Restart Manager は 23712（VRChat）と 28280（pwsh）の2つを返したが、写しは5秒ごとの4回とも1台目のログのままで、Bridge のログにも何も出なかった。閉じたあとは 23712 だけに戻った |
| 7. 同じ秒の起動（Bridge は止めない） | 合格 | `start-vrchat-dev.ps1 -Clients 2 -LaunchIntervalSeconds 0`（2台の差は 6〜11ms）を9回。8回は1つのファイルに混ざり（PID 2つ）、そのたびに `... is open by several VRChat processes; ...` と `... is or was also open by other VRChat clients ...` が出て、写しはその1行になった。1回（起動 21:42:22.549 / .559）はログが `21-42-22` と `21-42-23` に分かれ、`Showing ...output_log_2026-10-17_21-42-22.txt as the log of VRChat client 30180 (the only VRChat process that has it open).` になった。写しの中身が `21-42-22`（Restart Manager で 30180 だけが開いているファイル）のものであることも確かめた。以前はこの場合 `Could not match` だった |
| 8. Restart Manager のセッションの閉じ忘れ | 合格 | 手順3の4台を 21:20:28 から 21:31:30 まで（11 分）動かしたまま、`Could not ask Windows Restart Manager` は0件、`[ERR]`・`[WRN]` も0件。Bridge の CPU 使用率は 30 秒ごとの19回で平均 0.22%、最大 0.30%（16 論理 CPU 全体に対して）。ワーキングセット 50MB、ハンドル 515 |
| 9. Unity の Result Timeout 120 秒で負荷ありのコールドスタート | 未実施 | Mac の Unity が要る |

気づいたこと:

1. 同じ秒に起動した2台のログが分かれるかどうかは、起動の時刻で決まっていた。ログ名の時刻は、起動のおよそ 0.45〜0.55 秒後の時刻になるようで、それが秒の境目をまたいだときだけ2つのファイルに分かれた（起動を秒の 0.22〜0.30 秒・0.46〜0.48 秒にそろえた6回のうち、分かれたのは 0.549 / 0.559 秒に起動した1回だけ）。
2. 負荷が高いときは、PR #3 の確認と同じく、写しの更新が遅れる。手順4では、起動前に書かれた前のセッションのログが、1台目の起動から約 17 秒（21:32:34.883 まで）写しに残っていた。PR #5 の変更とは関係しない。
3. 手順4の Bridge のログの警告は、`within 60s` の1件だけだった（想定どおり）。

後片付け: `config.json` は確認前のバックアップに戻した。Bridge の exe は PR #5 のビルドのまま。

### 案C の Bridge への組み込み後の確認（2026-10-17 21:49〜22:04）

案C（`VrchatClientMode: Relaunch`。別アカウントの N 台を、配置のたびに終了させてから `--url` と `--profile` で起動し直す）の実装 `93141d9`（main の v0.4.0 `6e01786` を取り込み済み。PR #5 は含まない）を Windows で確かめた。

**準備**: Windows の `dotnet test` は 449 件中 446 件成功（落ちたのはシンボリックリンクの3件だけ）。手順1-B と同じ `dotnet publish` で作った exe（SHA-256 `B41E9720DC05691B6BDD7307BC000799F7945EF87D296FE0A4F01BCA2464E005`）を、このチェックアウトの `install-bridge.ps1` で入れた（それまでの PR #5 の exe と `config.json` は `%LOCALAPPDATA%\VRCRemoteTest\planc-test-backup-20261017-215242\` に退避し、確認のあと戻した）。`config.json` は `AutoLaunchVrchat: true`、`VrchatClientMode: "Relaunch"`、`VrchatProfiles: [0, 1]`（`VrchatClientCount` なし）。profile 0 はアカウント#1、profile 1 はアカウント#2 でログイン済みで、各回のログの `User Authenticated` で確かめた。Logging は「完全」。ビルドは前回までと同じ模擬のアップロードで送り（ワールドは `Downloads` の build-A / build-B の元のファイル）、結果は 120 秒まで待った。

| 確認 | 結果 | 記録 |
|------|------|------|
| 1. 初回の配置（VRChat 0 台） | 合格 | `Launched VRChat client 1/2 (profile 0, PID 28376, ...)` と `2/2 (profile 1, PID 20512, ...)` が 3.0 秒間隔・同じ roomId。`managed-clients.json` に2台（PID、`creationTimeUtc`、`sessionId: 1`、profile、`launchOrder`）が入り、`creationTimeUtc` は `Get-Process` の `StartTime.ToUniversalTime()` と小数7桁まで一致した（例: `12:53:15.8047612`）。送信から `deployed` まで 4.8 秒、2台の入室まで 13 秒。2台とも `local:dab8e4…:1` で、#1 と #2 がお互いの `OnPlayerJoined` を出した |
| 2. 連続した配置（4回） | 合格 | 毎回 `Closing 2 VRChat client(s) launched by the Bridge (PID …)` → `Closed 2 VRChat client(s) in …s (0 forced)` → 配置 → 起動し直しで、毎回 #1 と #2 が `local:dab8e4…:1` にそろった。終了・`deployed`・入室まで: 1.5 秒・5.3 秒・（未計測）／ 6.6 秒・14.3 秒・20 秒（前の結果の 0.6 秒後に送った回。閉じた台は起動から1〜4秒）／ 3.1 秒・7.5 秒・15 秒／ 2.0 秒・9.6 秒・21 秒。`forcing it to exit` は出なかった。`Closed` のあとの最初の起動まで 0.02〜0.04 秒 |
| 3a. 手で起動した台だけ | 合格 | `VRCHAT_UNMANAGED_RUNNING`（0.7 秒）: `... (1 VRChat client(s) that the Bridge did not launch (PID 24076)). Nothing was closed or deployed. ...`。何も閉じず、配置もしなかった |
| 3b. Bridge の2台＋手で1台 | 合格 | `VRCHAT_UNMANAGED_RUNNING`（PID 24540）。3台とも残り、配置もしなかった |
| 3c. Bridge の起動の間に手で1台 | 合格 | 1台目の起動（21:56:44.720）のあと、21:56:44.956 に手で1台起動した。結果は `VRCHAT_UNMANAGED_RUNNING`: `Launched 1 of 2 VRChat client(s), then stopped: 1 VRChat client(s) that the Bridge did not launch appeared (PID 8436). Nothing was closed; ...`。ワールドは配置済み・manifest は archive 済みで、手で起動した台は閉じられず、記録は1台目だけ |
| 4. 起動途中の強制終了 | 合格 | `Launched VRChat client 1/2` の直後に `Stop-Process -Force`。結果ファイルは書かれず、manifest は `state\archive` に入り、記録は1台（PID 28012）。Bridge を起動し直しても配置し直さなかった。次のビルドは `Closing 1 VRChat client(s) ... (PID 28012)` → `Closed ... in 2.6s (0 forced)` → 2台で起動し直した |
| 4. 終了途中の強制終了 | 合格 | `Closing 2 VRChat client(s)`（21:57:57.757）の 0.08 秒後に `Stop-Process -Force`。ビルドは `state\processing` に残った。Bridge を起動し直すと `Recovering unfinished build left in processing/` のあと、そのビルドを配置して2台を起動した（`deployed`）。古い2台は、強制終了の前に送られていた終了の要求で 0.6 秒後に終わっていた（ログの最後の書き込みが 21:57:58.4）ので、起動し直したあとの `Closing` は出なかった |
| 5a. 最小化した台 | 合格 | 1台を最小化して送った。`Closed 2 VRChat client(s) in 2.0s (0 forced)` |
| 5b. ウィンドウがまだ無い台 | 合格 | ビルドを2つ続けて送った。2つ目の `Closing` は2台目の起動の 0.02 秒後で、`Closed 2 VRChat client(s) in 8.7s (0 forced)`（ウィンドウができてから求め直して閉じた）。2つ目も `deployed` |
| 5c. 応答しない台 | 未実施 | リソースモニターは管理者権限で動くため操作できず、ゲームのプロセスの中断も避けた |
| 6. 2台が動いている状態で Bridge を再起動 | 合格 | タスクを止めて起動し直してから送った。`Closing 2 VRChat client(s) launched by the Bridge (PID 8732, 33964)`（記録と照合できた2台）→ 2.0 秒で閉じて起動し直した |
| 7a. Relaunch の台を残して Reload に | 合格 | `VRCHAT_WATCH_WORLDS_MISSING`。2台とも残った |
| 7b. Reload の台を残して Relaunch に | 合格 | `VRCHAT_UNMANAGED_RUNNING`（PID 29628）。何も閉じなかった |
| 8a. 記録を壊す | 合格 | 台が動いている状態で `managed-clients.json` を途中で切れた JSON にした。`[WRN] Ignoring ...managed-clients.json: it is not a valid record (...)` と `VRCHAT_UNMANAGED_RUNNING`（メッセージに記録が壊れていることも出る）。何も閉じなかった。全台を手で閉じて送ると配置され、記録は正しい内容に書き直された |
| 8b. 記録の読み取りを ACL で拒否 | 合格 | `icacls /deny <ユーザー>:(R)`。`UNKNOWN_ERROR`: `The Bridge's record of the VRChat clients it launched (...managed-clients.json) could not be read: Access to the path ... is denied.`。何も閉じなかった。確認後に拒否を外した |
| 9. Log Viewer | 合格 | 起動し直しのたびに、`Showing ... as the log of VRChat client <profile 0 の PID>` になった（1台目の起動から 1〜5 秒）。ログができる前に読み取りが来た回は、先に `has not created its output_log_*.txt file yet` の1行になった。1回は 0.5 秒ごとに写しを読んで測った: 起動し直しの前の読み取り（22:03:05.984、`Closing` の最中）で書かれた前のセッションの1台目のログが、新しい1台目の起動（22:03:07.425）から次の読み取り（22:03:11.033）まで 3.6 秒残り、そこで新しい1台目のログになった。Bridge が起動後に前のセッションのログを書くことは無かった。手で起動した台と同じ秒に起動した 3c と、終了の最中（5b）は `Could not match` だった（このビルドは PR #5 を含まない） |
| 10. 起動し直しをくり返したあと | 合格 | 21:53〜22:03 に Relaunch の起動（`Launched VRChat client 1/2`）を 17 回。Logging は「完全」（`LOGGING_ENABLED_h120798204 = 1`）、ウィンドウの位置と大きさは変わらず（レジストリは 0,0・3840x2080、実際のウィンドウは (-7,0)-(3849,2119)）。この間の output_log 35 個のうち 34 個に `User Authenticated` があり（残りの1つ `21-59-08` は、5b で起動の 0.02 秒後に終了を求めた台で、ログインの前に閉じた）、ログアウトや資格情報の行は0 |
| 11. 昇格した VRChat、VR モード | 未実施 | UAC の操作と HMD が要る |

**実装上の点**: この間の Bridge のログに `Could not open VRChat process`、`Could not read the creation time`、`Win32 error`、`forcing it to exit` は0件。`Closed` は11回で、どれも `0 forced`（1.5〜8.7 秒）。`[ERR]` の6件は、3a・3b・3c・7b・8a・8b で意図して起こしたもの。

**アンチチート**: Bridge が起動した VRChat のログには、毎回 `[EOSManager] [Info][LogEOSAntiCheat] [AntiCheatClient] Anti-cheat client not available. Verify that the game was started using the anti-cheat bootstrapper ...` が1行出る。これは `VRChat.exe` を直接起動したときに必ず出る行で、スパイクの `start-vrchat-dev.ps1` や PR #2 の Bridge で起動したときも同じだった（Steam から起動したときは出ず、アンチチートの行が別に出る）。直接起動した VRChat ではアンチチートのクライアントが動いておらず、Bridge がプロセスを開いたり終了させたりしても反応するものが無い。EasyAntiCheat のプロセスも現れなかった。

**後片付け**: VRChat を閉じ、`config.json` と Bridge の exe を確認前のもの（PR #5 のビルド、SHA-256 `16135507…`）に戻した。`managed-clients.json` は最後の2台（終了済み）の記録のまま残っている。記録の台はもう動いていないので、Relaunch に戻したときもそれらを閉じようとはしない。

### PR #19 の実機確認（2026-10-17 23:38〜23:44）

PR #19（Relaunch の起動し直しのあいだ、Log Viewer の写しを「The Bridge is relaunching its VRChat clients ...」の1行に置き換える）の `a316877` を Windows で確かめた。上の案C の確認の項目9（前のセッションの1台目のログが、新しい1台目の起動から 3.6 秒残った）を受けた変更。このビルドは PR #5 と案C（PR #7）を含む。

**準備**: Windows の `dotnet test` は 505 件中 501 件成功。落ちたのはシンボリックリンクの3件と、下の「気づいたこと」1のテスト。手順1-B と同じ `dotnet publish` で作った exe（SHA-256 `09D970410BFE6EA0B4B7D7BBB62C08AA3AA874C3B6AFF9185BBDB65BA35E930C`）を入れた。`config.json` は案C の確認と同じ（`AutoLaunchVrchat: true`、`VrchatClientMode: "Relaunch"`、`VrchatProfiles: [0, 1]`）。Logging は「完全」。ビルドは前回までと同じ模擬のアップロードで送った。写しは別のプロセスで 0.25 秒ごとに読み、中身が変わった時刻を記録した。Unity の Log Viewer は見ていない（Mac が要る）。

| 確認 | 結果 | 記録 |
|------|------|------|
| 0. 初回の配置（閉じる台なし） | 合格（1行は出ない） | `Launched VRChat client 1/2`（23:38:28.367）の 0.08 秒後に `has not created ...` の1行になり、`Showing ...`（23:38:33.627）で1台目のログになった |
| 1. 起動し直し | 合格 | `Closing`（23:38:56.467）と同時（56.461）に写しが1行になった。`Closed`（57.989）から `Launched VRChat client 1/2`（58.009）まで1行のまま。起動の 0.04 秒後（58.047）に `has not created ... PID 3264`、`Showing ... client 3264 (the only VRChat process ...)`（23:39:03.172）でそのログになった。前のセッションのログは一度も出なかった |
| 2. 続けて送る | 合格 | 3回とも 1. と同じ順だった。前の結果の直後に送った回は、`Closing` 23:39:26.101 と写しの書き換え 26.101 が同時で、`Launched` 27.645 の 0.05 秒後に `has not created`、32.836 に `Showing`。2つ続けて送った回の1つ目は、`Closing` 40.723 と書き換え 40.724、`Launched` 42.797 の 0.05 秒後に `has not created`。2つ目は、`Closing` 45.847（起動の 0.02 秒後の台）と書き換えが同時で、`Closed` までの 8.7 秒（0 forced）も1行のまま、`Launched` 54.530 の 0.07 秒後に `has not created`、59.761 に `Showing` |
| 3a. 手で起動した台だけ | 合格 | `VRCHAT_UNMANAGED_RUNNING`。1行は出ず、写しは手で起動した台のログ（`Showing ... client 31492, the only ...`）のままだった |
| 3b. Bridge の起動の間に手で1台（案C の 3c） | 合格 | `Closing`（23:41:15.765）で1行、`Launched 1/2`（18.327）の 0.05 秒後に `has not created`、21.337 に `Launched 1 of 2 ... then stopped ... (PID 33044)`。23:41:23.484 に写しが `output_log_2026-10-17_23-41-18.txt, the output_log_*.txt file of the first VRChat client (PID 2412 ...) is or was also open by other VRChat clients ...` になり、1行は残らなかった（手で起動した台が1台目の 0.17 秒後に起動し、同じファイルに書いた） |
| 3c. 閉じられない台 | 未実施 | プロセスの中断にはリソースモニター（昇格）が要り、ゲームのプロセスを中断するのも避けた |
| 4. 終了の途中の強制終了 | 一部不合格 | `Closing`（23:42:21.547）の 0.14 秒後に `Stop-Process -Force`。写しは1行のまま残った（想定どおり）。Bridge を起動し直すと（23:42:30.090）、0.14 秒後（30.228）に写しが今の状態に戻り、`Recovering unfinished build` のあと、やり直した配置は `deployed` になった。ただし古い2台は、強制終了の前に送られていた終了の要求で、起動し直す前に終わっていた。やり直しの配置には閉じる台が無く、`a316877` の規則（閉じる台が無ければ出さない）どおり1行は出なかった。その結果、30.228 に書かれた前のセッションのログ（閉じた台の `23-42-11`）が、新しい1台目の起動（30.294）から `Showing`（35.444）まで 5.2 秒残った |
| 5. 写しを書けないとき | 合格 | `C:\VRCRemoteTest\logs` に `icacls /deny <ユーザー>:(OI)(CI)(W)`。`Unhandled error while polling the VRChat log.`（`UnauthorizedAccessException`: `...\vrchat-latest.log.tmp` is denied）が5回出て、うち1回は `Closing` の時刻のもの。それでも `deployed` になり、2台が起動し直した。拒否を外すと ACL は元に戻り、次の読み取りで新しい1台目のログになった |
| 6. Reload に戻す（`VrchatClientCount: 2`） | 合格（1行は出ない） | コールドスタートでは写しに前のログが残り、1台目の起動（23:43:52.720）から 5.15 秒後（57.872）の `Showing` でそのログになった。起動済みの台への配置は、settle の 30 秒を待って `deployed`。1行は出なかった |

気づいたこと:

1. `StagingWatcherRelaunchTests.The_share_shows_the_relaunch_notice_from_closing_until_the_first_new_client_is_recorded` は、Windows で全体を流すと6回中3回落ちた（`Expected noticeWhileClosing to be True ...`、5秒の待ちの時間切れ）。このクラスだけを流すと3回とも通った。原因はテストの読み方と見ている。テストは写しを `File.ReadAllText`（削除を共有しない `FileShare.Read`）で途切れなく読む。Windows では、そのハンドルがあると `SharedFileWriter` の置き換えが POSIX の置き換えでも共有違反になり（`DestinationBusy`）、書き込みを見送る。テストは読み取りの間隔を1時間にしているので書き直されない。負荷が高いと、ちょうど読んでいる最中に書くことが増える。実機では、Mac の SMB クライアントは削除も共有して開いており（PR #1）、見送っても5秒後に書き直されるので、1・2 の結果に遅れは出なかった。
2. 4. の経路（記録の台がもう終了している配置）では、直す前と同じ症状が残る。記録に台が載っていれば、動いていなくても最新のログはその台のもの。

これを受けて、次のように直した（`cbcb9c3`・`a1690c0`・`d2ae3ee`。Windows での再確認は下の「修正後の再確認」）:

- 1行を出す条件を「閉じる台がある」から「管理下でない台が無いと確かめて、記録に台が載っている」に変えた。記録が空の配置（初回など）では今までどおり出さず、管理下でない台のために何もせず止まる配置では一瞬も出さない（3a のとおり）
- テストは、写しを `FileShare.ReadWrite | FileShare.Delete` で読む（Mac の SMB クライアントと同じ共有）。4. の経路の結合テストも足した
- Reload のコールドスタートの「次の更新（5秒以内）」は、6. の 5.15 秒に合わせて「約5秒後」にした

後片付け: VRChat を全部閉じ、写しの観察も止めた。`config.json` は確認前の値（Reload、`VrchatClientCount` なし）に戻した。Bridge の exe は、確認前の PR #5 のビルドには戻さず、main `c9b1cd1` のビルド（SHA-256 `210804F9C8839A7B85104B15021EFF8E328CCFEFD844119A3175634C40B4F68D`、Windows の `dotnet test` は 488 件中 485 件成功）にした。PR #5 と案C が main に入ったら main のビルドに入れ替える、というユーザーの決定による。

#### 修正後の再確認（2026-10-17 23:59〜2026-10-18 00:02）

`d2ae3ee` を同じ手順で確かめた。exe の SHA-256 は `86E2C7F8107C6F29ECC7BD5FAC0602EF68DDCF8EE55245D46084C7BBCA8822FB`。`config.json` と Logging は上と同じ。写しは 0.25 秒ごと、4. と 3a は 0.05 秒ごとに別のプロセスで読んだ。

| 確認 | 結果 | 記録 |
|------|------|------|
| `dotnet test`（全体で6回） | 合格 | 毎回 507 件中 504 件成功で、落ちたのはシンボリックリンクの3件だけ。`The_share_shows_the_relaunch_notice_from_closing_until_the_first_new_client_is_recorded` は6回とも成功（35〜43 ms。直す前は5秒の時間切れで落ちていた）。足した `A_recovered_build_whose_recorded_clients_already_exited_shows_the_notice_until_the_first_client_is_recorded` も6回とも成功（54〜63 ms） |
| 記録の2台がもう終了している配置（新しい条件） | 合格 | 写しが1行になったのは 23:59:10.755 で、`Launched VRChat client 1/2`（10.777）の 22 ms 前。10.875 に `has not created ... PID 32128`、16.030 に `Showing ... client 32128 (the only ...)`。起動のあとに前のログは出なかった（それまでは VRChat が1台も無いので最新のファイルだった） |
| 1. 起動し直し | 合格 | `Closing`（23:59:33.871）に対して1行の書き込みは 33.864。`Closed`（35.394）から `Launched 1/2`（35.433）まで1行のまま。35.475（起動の 0.04 秒後）に `has not created ... PID 27764`、40.610 に `Showing ... client 27764`。前のセッションのログは出なかった |
| 4. 終了の途中の強制終了 | 合格（3回） | どの回も `Closing` の 0.04〜0.07 秒後に強制終了し、起動し直す時点で VRChat は0台だった。1回目（0.25 秒ごと）: `Closing` 00:00:13.683 で1行、起動し直し 22.123、`Recovering` 22.216、`Launched 1/2` 22.275、写しは1行のまま 22.381 に `has not created`、27.577 に `Showing ... client 20716`。2回目（0.05 秒ごと）: `Bridge starting` 00:01:28.938、`Recovering` 29.029、29.067 に1行、`Launched 1/2` 29.089、29.173 に `has not created`、34.354 に `Showing ... client 13952`。3回目（0.05 秒ごと）: `Bridge starting` 00:02:05.685、`Recovering` 05.776、05.812 に1行、`Launched 1/2` 05.835、05.933 に `has not created`、11.099 に `Showing ... client 6456`。起動し直したあとに前のログは一度も書かれなかった。Bridge の起動直後に VrchatLogService の最初の読み取りが回復より先に走り、前のログを書く可能性も考えていたが、3回とも起きなかった（0.05 秒ごとの観察で前のログは見えず、最初に書かれたのは1行で、`Recovering` の 36〜38 ms 後、`Launched 1/2` の 22〜23 ms 前だった） |
| 3a. 手で起動した台だけ（記録には終了済みの2台） | 合格 | `VRCHAT_UNMANAGED_RUNNING`（PID 9076）。0.05 秒ごとの観察で1行は一度も出ず、写しは手で起動した台のログのままだった |

後片付け: VRChat と観察用のプロセスを止め、`config.json` は確認前の値に戻し、Bridge は確認前の main `c9b1cd1` のビルド（SHA-256 `210804F9…`）に戻した。戻すとき、タスクを止めて Bridge のプロセスが消えたのを確かめた直後に、`install-bridge.ps1` の最後の置き換えが1回 `"Move" ... Access to the path is denied.` で失敗した。入っていた exe もタスクも変わっておらず、すぐに入れ直すと成功した。プロセスが終わった直後は、exe の置き換えがまだ拒否されることがあるらしい。

#### Unity の Log Viewer と残りの確認（2026-10-18 00:36〜00:46）

PR #19 のマージ後、main `b09d3f3` のビルド（SHA-256 `19CB492F6BA8674EA319FE74BC99DC22AA7C4E18137EB2147010C26AAFC2EB80`）で、Windows だけでは見られなかった Unity の表示と、残りの失敗の経路を確かめた。`config.json` は `VrchatClientMode: "Relaunch"`、`VrchatProfiles: [0, 1]`。Windows の写しは 0.25 秒ごとに読み、変わり目を記録した（時刻は Windows の JST）。Mac は Unity 2022.3.22f1 の Vrc-remote-test プロジェクト（パッケージ v0.4.0）で、VRC Remote Test ウィンドウの VRChat Log（フィルター All、Auto Refresh オン）を、画面を操作して見た。

| 確認 | 結果 | 記録 |
|------|------|------|
| 準備の配置（記録の台はもう終了していた） | 合格 | 00:36:31.133 に写しが1行になり、`Launched 1/2` は 31.156。前の確認の「記録の2台がもう終了している配置」と同じ順 |
| 台を閉じたあとに配置が失敗したとき | 合格 | Worlds への Bridge のユーザーの書き込みを ACL で拒否して送った（00:36:50.773）。`Closing`（00:36:54.255）と同時（54.248）に写しが1行になり、`Closed`（56.301、2.0 秒、0 forced）のあと `DEPLOY_FAILED`（`Access to the path '...Worlds\.vrc-remote-20261017T153650777Z-1051c583.vrcw.tmp' is denied.`、送ってから 5.5 秒）。`[ERR] Deploy failed`（56.316）の 0.011 秒後（56.327）に、写しは1行から最新のファイル（`output_log_2026-10-18_00-36-34.txt`、VRChat は1台も動いていない）に戻った |
| その次の配置（記録が空） | 想定どおり | 拒否を外して送り直すと（00:37:08.778）、5.7 秒で `deployed`。`Launched 1/2` 00:37:11.385（PID 27960）、`2/2` 14.518、`Showing` 16.507。失敗の前に記録を `[]` に書き直しているので、この配置は記録が空のコールドスタートで、1行は出ず、前のログが1台目の起動から 5.1 秒残った（仕様書 §35.3 の「記録が空の配置」） |
| Windows からの模擬アップロード（3回） | 合格 | 3回とも `deployed`。写しは `Closing` と同時に1行 →（1.5〜2.0 秒後）`Launched 1/2` の 0.03〜0.05 秒後に `has not created ...` → `Showing` でそのログ。1回目は `Closing` 00:41:19.885、`Launched 1/2` 21.947（PID 17104）、`Showing` 27.104。2回目は 00:41:55.133、56.675（PID 28100）、00:42:01.845。3回目は 00:42:29.786、31.322（PID 22604）、36.494。Unity では、1回目と3回目で、前のログ → `The first VRChat client (PID 17104 / 22604, ...) has not created its output_log_*.txt file yet ...` → 新しい1台目のログ、の順に変わるのを見た（2回目は観察の切れ目）。1行そのものは Unity に出なかった（下の「気づいたこと」1） |
| Unity の Remote Build & Test | 合格 | `VRCDefaultWorldScene` をビルドして送った（build `20261017T154501009Z-ee70bdf1`）。manifest が共有に届いたのは 00:45:04.534、`Closing` 04.552 と同時（04.553）に写しが1行、`Closed` 06.588（2.0 秒、0 forced）、`Launched 1/2` 06.606（PID 11416）、`has not created` 06.641、`Launched 2/2` 09.629、`deployed` 09.630、`Showing` 11.772。Unity では「Waiting for Bridge result...」の間に、VRChat Log に `The Bridge is relaunching its VRChat clients with the new build (VrchatClientMode: Relaunch): they are closed, the build is installed, and they are started again. The first client's log is shown once it has started.` の1行が出て、そのあと新しい1台目のログ（00:45:11〜、`local:dab8e4…` への入室）になった。Unity の Last Deployment は `Deployed as vrc-remote-20261017T154501009Z-ee70bdf1.vrcw` |
| Unity の Deploy Last Build | 合格 | 1回目の結果の 28.6 秒後に送った（build `20261017T154538203Z-b3eed4d9`）。`Closing` 00:45:39.722 と同時に1行、`Closed` 41.249（1.5 秒、0 forced）、`Launched 1/2` 41.267（PID 5476）、`has not created` 41.307、`deployed` 44.286、`Showing` 46.422。Unity は `Deployed as vrc-remote-20261017T154538203Z-b3eed4d9.vrcw` になり、そのあと新しい1台目のログだった（途中の画面は、画面の取得に失敗して見ていない） |
| 3c. 閉じられない台 | 未実施 | 前回と同じく、プロセスの中断に昇格したリソースモニターが要る |

気づいたこと:

1. Unity の Log Viewer は、写しを5秒ごとにしか読み直さない（`RemoteTestWindow.LogRefreshIntervalSeconds`）。写しが1行なのは、閉じ始めてから1台目の起動まで（ふつう 1.5〜2 秒）なので、その間に読み直しが当たった回だけ Unity に1行が出る。今回は Remote Build & Test の1回で出た。どの回でも、Unity に前のセッションのログが出ることはなかった。起動直後のウィンドウの無い台を閉じる配置（案C の 5b、約 8.7 秒）なら1行は長く出るが、Unity の操作では前の結果から1秒以内に送れないので確かめていない。
2. 台を閉じたあとに配置が失敗すると、記録は空になる（閉じたあとに `[]` に書き直すため）。次の配置は記録が空のコールドスタートなので、1行は出ず、前のログが1台目の起動から約5秒残る。初回の配置や Reload のコールドスタートと同じ扱いで、仕様書 §35.3 どおり。
3. Unity の Remote Build & Test は、VRChat SDK にログインしていないときと、ログインしたあとでも開いているシーンに VRCSceneDescriptor が無いとき（空の「Untitled」）に、`SDK_BUILD_FAILED: SDK builder error: Open the SDK panel to build and upload worlds` になった。どちらもビルドの前で止まり、共有には何も送られない。
4. 起動し直しを1分ほどの間に続けると、新しい台の VRChat のログに API の 429（`whatever it is that you are doing, you are doing it too quickly. slow down!`）が出ていた（Unity の VRChat Log で見た。Windows 側では確かめていない）。入室とビルドの結果には影響しなかった。

PR #20（`install-bridge.ps1` の置き換えの入れ直し）の Windows での確認も、このとき同じ exe の入れ替えで行った。結果は PR #20 に書く。

後片付け: VRChat を閉じ、`config.json` を Reload（`VrchatClientMode`・`VrchatProfiles`・`VrchatClientCount` なし）に戻して Bridge を起動し直した。Bridge の exe は main `b09d3f3` のビルドのまま。Worlds の ACL は継承だけに戻した。VRChat の画面の設定（ScreenManager）は確認前の値に戻した。Windows の `dotnet test`（main `b09d3f3`）は 507 件中 504 件成功（落ちたのはシンボリックリンクの3件）。Mac の Unity では、確認のために `VRCDefaultWorldScene` を開き、VRC Remote Test のタブを広げた。

### v0.5.0 のリリース後の通しの確認（2026-10-18 01:05〜01:26）

v0.5.0（タグ `79afe3d`。PR #5・#7・#19・#20 を含む）を、Release の exe で Windows に入れ、Mac の Unity（パッケージ v0.5.0）から送って、Reload と Relaunch の流れを通しで確かめた。Unity の操作と表示の確認は Mac が行った。Windows では、結果ファイル、Bridge のログ、各台の起動引数・インスタンス・アカウント（各台のログの `User Authenticated` と `OnPlayerJoined`）を記録した。写しは別のプロセスで 0.25 秒ごとに読み、変わった時刻を記録した。時刻は Windows の JST。「manifest が届いた」は `ready.json` の最終書き込み時刻（Mac が共有に書いた時刻。取り込んで移しても変わらない）。Logging は「完全」。

**S0. 入れ替え**（`docs/setup-windows.md` の手順1-A と手順6）:

- 退避: それまでの `config.json` と exe（main `b09d3f3` のビルド、`19CB492F…`）は `%LOCALAPPDATA%\VRCRemoteTest\v050-test-backup-20261018-010546\` に退避した。
- gh の更新: Windows の GitHub CLI は 2.92.0 で、手順書の前提（2.102.0 以上）を満たしていなかったので、winget で 2.102.0 にした。
- 取得と検証: `gh release download` で exe を `%LOCALAPPDATA%\VRCRemoteTest\release\v0.5.0\` に取った。Release の本文の `gh attestation verify ... --source-ref refs/tags/v0.5.0 --source-digest 79afe3d79b7b8e66f29d1c811b899e0a8d725153 --deny-self-hosted-runners` は終了コード 0 で、結果は1件だった。
  - subject の sha256 は `2339c8a4…bbae0`。predicateType は SLSA provenance v1
  - buildSignerURI は `…/release-package.yml@refs/tags/v0.5.0`、runner は `github-hosted`、run は `39047525914/attempts/1`
  - exe の SHA-256 は `2339C8A43D9AC784DA28C30E641E5998B9933CE90BC7D69A690618A4822BBAE0` で、Release の値と一致した
- スクリプト: 手順1-A の `source.zip` の代わりに、ローカルの git でタグ `v0.5.0` を取り出したチェックアウト（同じコミット `79afe3d`）の `install-bridge.ps1` を使った。
- 置き換え: `schtasks /end` で Bridge のプロセスが消えた直後（止め始めてから 1.06 秒）に、`-ExpectedSha256 2339c8a4…`（全桁）`-StartNow` で流した。1回目の置き換えで「SHA-256 を照合した実行体を配置しました」になり（1567 ms）、PR #20 の入れ直しは起きなかった。
- 起動: Bridge は 01:08:15 に起動した。ログは `starting` と `Application started` だけで、設定エラーは無かった。exe のファイルバージョンは `0.5.0.0`、ProductVersion は `0.5.0+79afe3d79b7b8e66f29d1c811b899e0a8d725153`。

| 確認 | 結果 | 記録 |
|------|------|------|
| S1. Reload・1台のコールドスタート（`AutoLaunchVrchat: true`、`VrchatClientCount`・`VrchatClientMode` なし、VRChat 0 台） | 合格 | Unity の VRChat SDK > Remote Build で送った（build `20261017T160949968Z-67097f75`、作成 01:09:49.968）。manifest が届いたのは 01:09:50.812。`Launched VRChat client 1/1 (PID 12008)` は 50.859、`deployed` は 50.865（作成から 0.9 秒）。起動引数は `--url=create?roomId=…&hidden=true&name=BuildAndRun&url=file:///…vrcw --watch-worlds --no-vr …`。写しは 51.184 に `has not created ... (PID 12008)`、56.285 に `Showing output_log_2026-10-18_01-09-51.txt as the log of VRChat client 12008 (the only VRChat process that has it open)`。台はアカウント#1 で、01:09:55 に `Joining local:dab8e4…:1`、01:09:59 に入室した。Unity のコンソールは Building → Uploading → Waiting for Bridge result → `[Succeeded] Deployed as vrc-remote-20261017T160949968Z-67097f75.vrcw`。Preflight は Remote Share Reachable・VRChat SDK Available・VRChat Running が緑。VRChat Log（Auto Refresh）はその台のログになった |
| S2. 同じ設定でもう一度送る | 合格 | build `20261017T161113502Z-ba3f5064`（作成 01:11:13.503）。manifest が届いたのは 16.013、`deployed` は 17.140。Bridge のログはこの1行だけで、起動も準備待ちも無かった（台は起動から 86 秒で、settle の 30 秒を過ぎていた）。台は 01:11:17 に `World Updated: ...vrc-remote-20261017T161113502Z-ba3f5064.vrcw // 1` → `Destination set: local:ce318f…:1`、01:11:18 に `Joining local:ce318f…:1`、01:11:19 に入室した（ダウンロード 58.9 ms、読み込み 1,101.8 ms）。写しは同じ台のログのまま。Unity は `[Succeeded]`、Preflight は Running のまま、VRChat Log は同じ台のログ。Deploy Last Build は、Mac の画面で別のウィンドウがボタンに重なっていて送れなかった（下の S4b で送った） |
| S3. Reload・2台（`VrchatClientCount: 2`、VRChat 0 台から） | 合格 | 1回目（build `20261017T161320810Z-9eb7f0f4`、作成 01:13:20.810）は、manifest が届いたのが 25.709。`Launched 1/2 (PID 6248)` は 25.764、`2/2 (PID 27512)` は 28.770（3.0 秒間隔、同じ roomId）、`deployed` は 28.774。2台とも起動引数は `--url ... --watch-worlds`（`--profile` なし、アカウント#1）。写しは 25.875 に `has not created ... (PID 6248)`、31.042 に `Showing output_log_2026-10-18_01-13-26.txt ... client 6248 (the only ...)`。2台とも `local:dab8e4…:1` に入り（01:13:34・01:13:37）、2台とも `OnPlayerJoined` を2回出した。Unity の VRChat Log には `[Behaviour] Initialize ThreePoint Avatar VRCPlayer[Remote] 2` が出た。2回目は1回目の結果の約 35 秒後に送った（build `20261017T161435088Z-625a7034`）。manifest が届いたのは 01:14:38.883、`deployed` は 40.025 で、起動・準備待ちは無かった。2台とも 01:14:40 に `World Updated` → `Destination set: local:ce318f…:1`、01:14:41 に `Joining local:ce318f…:1`、01:14:47 に入室し、01:14:48 までに2台とも `OnPlayerJoined` を2回出した。写しは1台目のログのまま。Unity は2回とも `[Succeeded]` |
| S4. Relaunch・2台（`VrchatClientMode: "Relaunch"`、`VrchatProfiles: [0, 1]`、VRChat 0 台、記録なし） | 合格（下の「気づいたこと」1） | 1回目（build `20261017T161629884Z-2741d110`、作成 01:16:29.884）は、manifest が届いたのが 31.721。`Closing` は出ず、`Launched 1/2 (profile 0, PID 7788)` は 31.798、`2/2 (profile 1, PID 18776)` は 34.821（同じ roomId）、`deployed` は 34.826。起動引数は `--url ... --no-vr ... --profile=0` / `--profile=1`（`--watch-worlds` なし）。写しは 31.936 に `has not created ... (PID 7788)`、37.119 に `Showing output_log_2026-10-18_01-16-32.txt ... client 7788`。アカウント#1 の台は 01:16:35、#2 の台は 01:16:38 に `Joining local:dab8e4…:1` で、2台とも #1 と #2 の `OnPlayerJoined` を出した。Unity の Preflight は Running と、想定どおりの `VRChat is running without --watch-worlds — builds will deploy but won't auto-reload.`。VRChat Log には `VRCPlayer[Remote] 2`。2回目は1回目の結果の約 20 秒後に送った（build `20261017T161729471Z-c44b3bfe`、作成 01:17:29.471）。manifest が届いたのは 29.897。`Closing 2 VRChat client(s) launched by the Bridge (PID 7788, 18776)` は 29.922、写しの1行の書き込みは 29.916。`Closed 2 VRChat client(s) in 1.5s (0 forced)` は 31.433。`Launched 1/2 (profile 0, PID 32736)` は 31.454、写しの `has not created` は 31.504。`New output_log ... (0.8s after VRChat client 1/2 was launched); launching the next client.` は 34.461、`Launched 2/2 (profile 1, PID 10432)` は 34.487、`deployed` は 34.489。写しの `Showing output_log_2026-10-18_01-17-31.txt ... client 32736` は 36.634。1行が出ていたのは 1.6 秒で、前のセッションのログは一度も写しに出なかった。#1 と #2 の台は `local:dab8e4…:1` にそろった（01:17:35・01:17:38 に `Joining`）。Unity は2回とも `[Succeeded]`。VRChat Log は新しいセッションのログ（01:17:43〜、`VRCPlayer[Remote] 2`）に替わった。1行は Unity の5秒ごとの読み直しの間に終わったので、Unity には出なかった（上の「Unity の Log Viewer と残りの確認」の「気づいたこと」1 と同じ）。前のセッションのログが Unity に出ることも無かった |
| S5. Relaunch でのエラー（S4 の2台に加えて、`start-vrchat-dev.ps1 -Profiles 0` で手で1台） | 合格 | 手で起動した台は 01:19:11.770 に起動した（PID 25456、`--watch-worlds --no-vr ... --profile=0`、アカウント#1）。Unity のメニューから送った（build `20261017T162001616Z-a9525483`、作成 01:20:01.616）。manifest が届いたのは 04.758。04.776 に `[ERR] VRChat is running that the Bridge did not launch or could not check (1 VRChat client(s) that the Bridge did not launch (PID 25456)). Nothing was closed or deployed. Close it manually, then send the build again.` が出て、04.778 に結果ファイル（`VRCHAT_UNMANAGED_RUNNING`）が書かれた。`Closing` は出ず、3台とも動いたまま。記録は PID 32736・10432 のまま。manifest と `.vrcw` は `state\failed` に移り、Worlds には何も配置されなかった。写しは S4 の1台目のログのまま。Unity のコンソールは `[Failed] VRCHAT_UNMANAGED_RUNNING: ...`（上と同じ文）。メニューから送ったので、ウィンドウの案内文（ErrorGuidance）は出ない |
| S5b. 同じ状態で、ウィンドウの Remote Build & Test から送る | 合格 | ここからは、ユーザーがウィンドウのボタンを押した。build `20261017T162309895Z-78864c10`（作成 01:23:09.895）。manifest が届いたのは 15.079、`[ERR] ... (PID 25456)). Nothing was closed or deployed. ...` は 15.162、結果ファイルは 15.164。S5 と同じく、`Closing` は出ず、3台とも残り、記録は変わらず、manifest と `.vrcw` は `state\failed` に移り、写しは S4 の1台目のログのまま。Unity の Last Deployment（When 2026-10-18 01:23:32）は赤で `VRCHAT_UNMANAGED_RUNNING: ...`（同じ文）。その下に案内文 `A VRChat the Bridge could not check is running, so nothing was launched or closed. Close that VRChat manually on the Windows machine, then Remote Build & Test again.` が出た。Preflight は Running で、Relaunch の警告は出なかった（手で起動した `--watch-worlds` 付きの台が代表になったため）。終わったあと、手で起動した台を閉じた（01:23:47） |
| S4b. Relaunch の2台に、ウィンドウの Deploy Last Build | 合格 | S2 で送れなかった Deploy Last Build を、Relaunch の2台（PID 32736・10432）に送った（build `20261017T162436714Z-5b4e69a5`、作成 01:24:36.714）。manifest が届いたのは 40.279。`Closing 2 VRChat client(s) launched by the Bridge (PID 32736, 10432)` は 40.297、写しの1行の書き込みは 40.296。`Closed 2 VRChat client(s) in 2.0s (0 forced)` は 42.316。`Launched 1/2 (profile 0, PID 34252)` は 42.339、写しの `has not created` は 42.379。`New output_log ... (0.8s after ...)` は 45.343、`Launched 2/2 (profile 1, PID 12776)` は 45.357、`deployed` は 45.359。写しの `Showing output_log_2026-10-18_01-24-42.txt ... client 34252` は 47.504（1行は 2.1 秒）。#1 と #2 の台は `local:dab8e4…:1` にそろった（01:24:46・01:24:50 に `Joining`、01:24:53 までに2台とも #1 と #2 の `OnPlayerJoined`）。Unity の Last Deployment は When 01:24:46 で `Deployed as vrc-remote-20261017T162436714Z-5b4e69a5.vrcw`。Preflight は Running と Relaunch の警告。VRChat Log は新しいセッションのログ（01:24:54〜、`VRCPlayer[Remote] 2`）。このあと約 50 秒見たが、空の manifest は現れず、結果ファイルは `deployed` のままだった |

S4 の準備: `state\managed-clients.json` に、前の節の Unity の確認（00:45）で起動して、もう終わっている2台（PID 5476・23492）が残っていた。Reload の S1〜S3 の間は書き換わらない。そのまま送ると「記録の台がもう終了している配置」になるので、消さずに退避先へ移し（`managed-clients.before-S4.json`）、記録なしにしてから S4 を始めた。

気づいたこと:

1. S4 の2回目で、`deployed` の結果を書いた 5 秒後（01:17:39.493）に、同じ名前で中身が空（0 バイト）の `incoming\20261017T161729471Z-c44b3bfe.ready.json` が現れた。Bridge はそれを取り込み、次のように処理した。
   - `[ERR] Failed to parse manifest ...\state\processing\20261017T161729471Z-c44b3bfe.ready.json`（`JsonException: The input does not contain any JSON tokens`、`StagingWatcher.ProcessClaimedManifestAsync`）を出し、空のファイルを `state\failed` へ移した
   - 01:17:39.508 に、結果ファイルを `{"status":"failed","errorCode":"MANIFEST_INVALID","errorMessage":"Manifest could not be parsed."}` で上書きした

   正しい manifest（274 バイト）は `state\archive` にあり、配置したワールドと起動し直した2台はそのまま。Unity は先に `deployed` を読んで `[Succeeded]` を出していたので、Unity では気づけない。今日の `ready.json` で空のものはこの1件だけ。Unity のパッケージは `<buildId>.ready.json.part` に書いて1回 rename するだけで、そのあと同じ名前には触らない。なので Mac は、macOS の SMB クライアントがファイルを閉じるのを遅らせていて、Bridge が取り込んで消したあとに空のファイルを作り直した、と見ている。v0.5.0 で変わった部分ではないので、別に調べる。Bridge の側にも、すでに `archive` にある buildId の manifest を受け取ると結果ファイルを上書きする、という点がある。この結果ファイルと空のファイルは調べるために残してある。

   追記（2026-10-18）: 調べた結果と Bridge の修正は `smb-late-empty-manifest.md`。Windows の変更ジャーナルで、Mac が rename した 01:17:29.896 の 5 ms 後に Bridge が manifest を消し、その 5 ms 後（29.906）に、名前付きストリームの書き込みを伴って空のファイルができていた。Mac の SMB クライアントが拡張属性を名前付きストリームとして書き、消えていたのでファイルごと作り直された。39.493 は Bridge が次のスキャンで気づいた時刻。ほかの回は rename から Bridge の削除まで 434 ms〜4.8 秒で、作り直しは無い。また、この節の「manifest が届いた」は、Bridge が manifest を新しいファイルにコピーするので、`state` の写しの時刻、つまり Bridge が取り込んだ時刻だった（Windows で確認。共有に置かれた時刻は変更ジャーナルの rename の時刻）。
2. Unity の VRChat SDK のメニューから送ったビルドは、VRC Remote Test ウィンドウの Last Deployment に反映されず、エラーの案内文（ErrorGuidance）も出ない（どちらもウィンドウのボタンから送ったときだけ）。結果は Unity のコンソールで見た。
3. `VRCHAT_UNMANAGED_RUNNING` の案内文（v0.4.0 で足したもの）は「Bridge が確かめられない VRChat」の書き方で、Relaunch で「Bridge が起動していない VRChat」が動いている場合とは少しずれる。すべきこと（その VRChat を手で閉じて送り直す）は同じなので、困りはしない。
4. 記録が空のコールドスタート（S1、S3 の1回目、S4 の1回目）では、1台目の起動の 0.33・0.11・0.14 秒後に写しが `has not created ...` になった。前のセッションのログが写しに残ったのはその間だけ。
5. この間の Bridge のログの `[ERR]`・`[WRN]` は、1. の1件と、S5・S5b で意図して起こしたものだけ。`Closed` は2回とも `0 forced`（1.5 秒・2.0 秒）。10個の output_log に API の 429 は無かった。

後片付け（S6、01:25〜01:26）: 写しの観察を止めた。VRChat を全部閉じ、`config.json` を確認前のもの（退避先のもの。Reload、`AutoLaunchVrchat: true`、`VrchatClientMode`・`VrchatProfiles`・`VrchatClientCount` なし）に戻して、Bridge を起動し直した。Bridge の exe は v0.5.0（`2339C8A4…`、ファイルバージョン `0.5.0.0`）のままで、これが入れ替えた後の状態。`managed-clients.json` は、閉じた2台（PID 34252・12776）が載ったままにした。S4 の前に退避した記録は戻していない。1. の結果ファイルと `state\failed` の空のファイルは残した。VRChat の画面の設定（ScreenManager）は確認前の値に戻した。

### S5a: ログの行の形・Logging ごとの出方・Udon の出力の形・4台のログ量（2026-10-18 13:34〜13:52）

台ごとの観測（`.claude/docs/research/client-observation-control-plan.md` の F1〜F3）を実装する前に、状態の判定に使う行と、読み取りの上限を決めるための確認（計画 v3 の §5 S5）。Windows のセッションが行い、生のメモは Windows の `%LOCALAPPDATA%\VRCRemoteTest\s5\s5-notes.md` にある。ワールドは SPK-003 のプローブ（`vrc-remote-20261018T022005192Z-ed77b640.vrcw`、133,672 バイト、SHA-256 `2B0BF148…C76C`）。起動は `start-vrchat-dev.ps1` で、Bridge（v0.5.1、Relaunch、`VrchatProfiles: [0, 1]`）は動かしたまま、配置はしていない。以下の行の表示名は `<表示名#1>`（profile 0）・`<表示名#2>`（profile 1）、`usr_…` は `usr_***`、Windows のユーザー名は `<user>` に伏せた。

**準備**: Bridge の Relaunch で 11:20 に起動していた SPK-003 の2台（PID 20800 `--profile=0`、8148 `--profile=1`）を、ユーザーの了承を得て閉じた。Logging（`LOGGING_ENABLED_h120798204`）は 1（完全）だった。

#### A1. 入室の行の形（Logging「完全」、profile 0・1 を `--url` で起動、`--watch-worlds` なし）

profile 0 の台（Master）:

```
2026.10.18 13:34:37 Debug      -  User Authenticated: <表示名#1> (usr_***)
2026.10.18 13:34:37 Debug      -  [Behaviour] Destination requested: local:dab8e405667a4de729f91623db09e31f57071efe70ea4b1ee764bd3563dd5383
2026.10.18 13:34:37 Debug      -  [Behaviour] Destination fetching: local:dab8e4…5383:1
2026.10.18 13:34:37 Debug      -  [Behaviour] Destination set: local:dab8e4…5383:1
2026.10.18 13:34:39 Debug      -  [Behaviour] OnConnectedToMaster
2026.10.18 13:34:39 Debug      -  [Behaviour] Entering Room: Local Test
2026.10.18 13:34:39 Debug      -  [Behaviour] Joining local:dab8e405667a4de729f91623db09e31f57071efe70ea4b1ee764bd3563dd5383
2026.10.18 13:34:39 Debug      -  [Behaviour] Joining or Creating Room: Local Test
2026.10.18 13:34:39 Debug      -  [Behaviour] Successfully joined room
2026.10.18 13:34:41 Debug      -  [Behaviour] Waiting to discover master client.
2026.10.18 13:34:43 Debug      -  [Behaviour] I am MASTER
2026.10.18 13:34:43 Debug      -  [Behaviour] Finished entering world.
2026.10.18 13:34:43 Debug      -  [Behaviour] OnPlayerJoined <表示名#1> (usr_***)
2026.10.18 13:34:47 Debug      -  [Behaviour] OnPlayerJoined <表示名#2> (usr_***)
```

profile 1 の台も同じ順（13:34:40 `User Authenticated` → 13:34:42 `Joining` → 13:34:46 `I am *NOT* MASTER` → 13:34:46 `Finished entering world.` → 13:34:47 `OnPlayerJoined` #1・#2）。

- `Joining` の local ID の末尾に `:1` は無い。`:1` が付くのは `Destination fetching` と `Destination set` だけで、初回の `Destination requested` にも付かない。
- `OnJoinedRoom` という行は無い。部屋に入ったことを示すのは `Entering Room: <名前>` → `Joining or Creating Room: <名前>` → `Successfully joined room`。
- `I am MASTER` / `I am *NOT* MASTER` は `Finished entering world.` の直前に1回だけ出る。`OnPlayerJoined`（自分を含む）はそのあと。
- `Finished entering world.` は、A1・A3・A4 の読み込み直し・A6 の入り直しの、どの入室でも1回ずつ出た。ただし Error World に入ったとき（A5）にも出るので、直前の `Joining local:…` と組で見ないと、狙ったワールドに入れたとは言えない。

#### A2. Master の台を閉じたとき

Master（profile 0、PID 26704）を `CloseMainWindow` で閉じた（13:35:36.390）。残った台:

```
2026.10.18 13:35:36 Debug      -  [Behaviour] OnMasterClientSwitched
2026.10.18 13:35:36 Debug      -  [NetworkProcessing] Transferred ownership of Dispatcher to 0
（同じ形で SceneEventHandlerAndInstantiator …、VRCWorld、SPK003_PlayerDataProbe）
2026.10.18 13:35:36 Debug      -  [Behaviour] OnPlayerLeftRoom
2026.10.18 13:35:36 Debug      -  [Behaviour] OnPlayerLeft <表示名#1> (usr_***)
2026.10.18 13:35:36 Debug      -  [SPK003] PLAYER_LEFT id=1 local=False
```

- Master が移ったことを示すのは `OnMasterClientSwitched`。`I am MASTER` は新たに出ない（その台の `I am` は最初の `I am *NOT* MASTER` の1回だけ）。誰が Master になったかは、この行からは分からない。
- そのあと、その台の RPC SanityCheck の行の末尾が `(local non-master non-owner)` から `(local master …)` に変わった。
- 閉じた台のログの最後は `[Behaviour] Destroying <表示名#2>` と `Error - [Behaviour] Attempted to execute RPC _DestroyObjects … when local player is invalid.`。

#### A3. 違う profile の構成で、1台だけ起動し直す（S1 の前半）

閉じた台を A1 とまったく同じ引数（同じ roomId）で起動し直した（13:36:01）。その台は 13:36:04 に `Destination set: local:dab8e4…:1`、13:36:05 に `Joining local:dab8e4…`、13:36:09 に `I am *NOT* MASTER` → `Finished entering world.` → `OnPlayerJoined` #2・#1。残った台には 13:36:10 に `OnPlayerJoined <表示名#1>`、`[SPK003] PLAYER_JOINED id=3 local=False`、`PLAYER_RESTORED id=3` が出た。**残った台と同じ `local:dab8e4…` に入った。**

#### A4. 同じ profile の構成で、読み込み直したあとに 1台だけ起動し直す（S1 の後半）

`-Clients 2 -WorldPath` で起動した（`-NoWatchWorlds` を付けなければ `--watch-worlds` が付く。`--profile` は付かないので2台とも profile 0）。2台とも `local:dab8e4…` に入った。13:37:47 に `Worlds\vrc-remote-s5-20261018T133747.vrcw` をコピーすると、2台とも:

```
2026.10.18 13:37:47 Debug      -  World Updated: C:\Users\<user>\AppData\LocalLow\VRChat\VRChat\Worlds\vrc-remote-s5-20261018T133747.vrcw // 1
2026.10.18 13:37:47 Debug      -  [Behaviour] Destination set: local:ce318f3e82d614028e1b7fd1c58e2dc69783d28f221338974d3230b42f743c4a:1
2026.10.18 13:37:47 Debug      -  [Behaviour] OnPlayerLeft <表示名#1> (usr_***)  ×2
2026.10.18 13:37:48 Debug      -  [Behaviour] Joining local:ce318f…
2026.10.18 13:37:54 Debug      -  [Behaviour] I am MASTER（2台目。1台目は *NOT*）→ Finished entering world. → OnPlayerJoined ×2
```

2台とも `local:ce318f…:1` にそろい、Master が入れ替わった。1台目を 13:38:18 に閉じ、同じ引数（`--url` と `--watch-worlds`）で起動し直すと、その台は `Destination set: local:dab8e4…:1` → `Joining local:dab8e4…` → `I am MASTER` → `Finished entering world.` と進み、`OnPlayerJoined` は自分の1件だけだった。残った台には `OnPlayerLeftRoom` と `OnPlayerLeft` が出ただけで、そのあと `OnPlayerJoined` は出なかった。**起動し直した台は、残った台（`ce318f`）とは別のインスタンス（`dab8e4`）に入った。** コピーしたワールドは消した。

#### A5. `--url` なしで起動（ケース3の再確認）

```
2026.10.18 13:39:19 Debug      -  [Behaviour] Going to Home Location: wrld_…
2026.10.18 13:39:20 Debug      -  [Behaviour] Going to current users Home world
2026.10.18 13:39:22 Warning    -  [Behaviour] Failed to join instance 'wrld_…:40635~private(usr_***)~region(jp)' due to 'オフラインテストモードでいる間は、オンラインのワールドに移動することはできません。
System.Exception: You are in offline testing mode, and cannot travel to online worlds. / If you are trying to use VRChat normally, please ensure you start VRChat by starting launch.exe. …
2026.10.18 13:39:42 Error      -  [Behaviour] Going to Error World
System.Exception: Going to Error World
2026.10.18 13:39:42 Debug      -  [Behaviour] Destination requested: local:error_4327609248367914
2026.10.18 13:39:42 Debug      -  [Behaviour] Destination set: local:error_4327609248367914:1
2026.10.18 13:39:43 Debug      -  [Behaviour] Entering Room: Error World
2026.10.18 13:39:43 Debug      -  [Behaviour] Joining local:error_4327609248367914
2026.10.18 13:39:43 Debug      -  [Behaviour] Successfully joined room
2026.10.18 13:39:45 Debug      -  [Behaviour] I am MASTER
2026.10.18 13:39:45 Debug      -  [Behaviour] Finished entering world.
2026.10.18 13:39:45 Debug      -  [Behaviour] OnPlayerJoined <表示名#1> (usr_***)
```

（`System.Exception` の行は頭の無い続きの行。）ホームへの移動の失敗から `Going to Error World` まで約 20 秒。Error World でも `Finished entering world.` が出る。

#### A6. Quick Menu からの入り直し（プロセスを終了させない再入場）

ユーザーの了承を得て、Windows 上で直接、1台（profile 0、`--url`、`local:dab8e4…`）の Quick Menu を操作した。Esc → 下のバーの「現在地」→ ワールドアクションの「ワールドに入り直す」→「本当にこのワールドのインスタンスに入り直しますか？」に「はい」。

```
2026.10.18 13:41:39 Debug      -  [Behaviour] Rejoining local world: local:dab8e4…5383:1
2026.10.18 13:41:39 Debug      -  [Behaviour] Destination requested: local:dab8e4…5383:1（このときは :1 付き）
2026.10.18 13:41:39 Debug      -  [Behaviour] Destination fetching: …:1 / Destination set: …:1
2026.10.18 13:41:39 Debug      -  [Behaviour] <color=red>Resetting game flow because "Destination instance set": [SkipLogin, RemainInNetworkRoom]</color>
2026.10.18 13:41:40 Debug      -  [Behaviour] OnLeftRoom
2026.10.18 13:41:40 Debug      -  [Behaviour] OnPlayerLeft <表示名#1> (usr_***)
2026.10.18 13:41:40 Debug      -  [SPK003] PLAYER_LEFT id=1 local=True
2026.10.18 13:41:40 Debug      -  [Behaviour] Joining local:dab8e4…5383 → Successfully joined room
2026.10.18 13:41:41 Debug      -  [Behaviour] I am MASTER → Finished entering world. → OnPlayerJoined <表示名#1> → [SPK003] PLAYER_JOINED id=1 local=True; … → PLAYER_RESTORED id=1 local=True
```

- 入り直しは `Rejoining local world:` で始まる。抜けるときの行は、自分のときは `OnLeftRoom`、ほかの台が抜けるときは `OnPlayerLeftRoom`。
- 途中に、頭の無い JSON の行（`{"runningTime": …}` や `  'onLoadingSceneActive': 0,`）が入る。
- プロセスは同じまま、入り直しのあとにプローブの `PLAYER_RESTORED` が出た。

#### B. Logging の設定ごとの出方

- **0（エラーのみ）**: 全部閉じてから 0 にし、profile 0・1 の2台を `--url` で起動して、40 秒後に1台を閉じた。2つのログ（7,070 バイト・51 行、4,865 バイト・31 行）で、`Joining`・`Finished entering world`・`OnPlayerJoined`・`OnPlayerLeft`・`I am (*NOT*) MASTER`・`User Authenticated`・`Destination set`・Udon の `[SPK003]`・頭が `Debug` の行・頭が `Warning` の行は、どれも 0 件。出るのは頭が `Error` の行（30・26 行）と、その続きの行（スタックトレースや JSON）だけ（例: `… Error      -  StyleEngine: Missing resource reference:Atlas`、`… Error      -  LSM: Failed to read state for 1 from …-objects`、閉じたときの `… Error      -  Safe handle has been closed`）。入室も人数も判定できない。
- **-1（オフ）**: output_log は2つとも作られたが、最後まで 0 バイトだった（作成時刻と最終書き込み時刻が同じ）。「ファイルはあるが 0 バイトのまま」で見分けられる。
- 終わったあと 1 に戻し、`reg query` で `0x1` を確かめた。

#### C. Udon の出力の形

- プローブの `Debug.Log` の行は、`[Behaviour]` の行と頭がまったく同じ。頭は `YYYY.MM.DD HH:MM:SS` ＋ 空白 ＋ 空白で 11 文字にしたレベル（`Debug` ＋ 空白6、`Warning` ＋ 空白4、`Error` ＋ 空白6）＋ `-  `（ハイフンと空白2つ）。（S5b で訂正。初めは「10 文字に詰めた」と書いていた）例: `2026.10.18 11:20:26 Debug      -  [SPK003] PLAYER_JOINED id=1 local=True; no PlayerData read yet`。VRChat が付ける接頭辞は無く、`[SPK003]` はワールド自身の文字列。
- 頭に出るレベルは `Debug`・`Warning`・`Error` の3つだけ。
- SPK-003 のログの `[SPK003]` は 26 行（START、PLAYER_JOINED、PLAYER_RESTORED、SLOT_MISSING、SLOT_READ、WRITE、DATA_UPDATED、FIRST_JOIN_SEEDED_A、PEER_CHECK、PEER_OBSERVATION_WRITTEN / READ。S5 の中では PLAYER_LEFT も出た）。
- Udon の例外は `… Error      -  [UdonBehaviour] An exception occurred during Udon execution, this UdonBehaviour will be halted.` の1行のあとに、単独の LF と、頭の無い続きの行（`VRC.Udon.VM.UdonVMException: …`、`  at VRC.Udon.Wrapper.Modules.…`）が続く。
- 複数行のメッセージは、本文の中の改行がそのまま書かれ、2行目以降に頭は付かない。行の終わりは CRLF だが、本文の中の改行は LF のことも CRLF のこともある。
- 10-17 以降の空でない output_log 268 個（頭のある行 533,717 行）で、頭の直前が単独の LF だったのは、3台が同じファイルに書いて混ざったログ（`output_log_2026-10-18_03-30-17.txt`）の1件だけ。1台だけが書いたログでは、頭の直前は必ず CRLF（かファイルの先頭）だった。本文の中の単独の LF は 25,763 個あった。
- 判断: ワールドが `Debug.Log("[Behaviour] Joining …")` を出すと、頭もレベルも本物と同じなので、行の形では見分けられない。「改行のあとに偽の日時の頭」は、LF で改行したものなら、頭の直前が単独の LF になるので、1台だけが書くログでは本物と見分けられそう。CRLF で改行されると見分けられない。Udon の文字列の LF・CRLF がそのまま書かれるかは、S5b（専用の試験ワールド）で確かめる。

#### D. 4台のログの量

`-Clients 4 -WorldPath` で起動した（4台とも profile 0）。4台とも、起動の引数に `--url=… --watch-worlds --no-vr --enable-debug-gui --enable-sdk-log-levels --enable-udon-debug-logging` があることを確かめた。13:46:33・36・39・42 に起動し、4台とも `local:dab8e4…` に入った（どの台も `OnPlayerJoined` が4回）。起動から 316 秒まで、約6秒ごとに 51 回サイズを記録した（最初の記録は起動から 14 秒後）。

| 台 | 入室の完了 | 完了までのサイズ | 間隔あたりの増加（最大 / 平均） | 5 分後のサイズ |
|---|---|---|---|---|
| 1 | 13:46:42 | 86,053 B | 5,879 B / 354 B | 126,740 B |
| 2 | 13:46:45 | 90,795 B | 9,024 B / 439 B | 143,190 B |
| 3 | 13:46:53 | 119,323 B | 31,041 B / 960 B | 143,897 B |
| 4 | 13:46:55 | 143,015 B | 67,654 B / 1,713 B | 165,955 B |

- 4台の合計の、間隔あたりの増加は最大 107,913 B、平均 3,466 B。各ログは最後に 1,147〜1,505 行で、`Error` の行は 26〜101、`Warning` の行は 70〜153。
- CPU は全体の平均 18.8%（最大 50.7%、起動直後）。VRChat 4台の合計は、論理 16 コアに対して平均 13.4%（最大 52.2%）。
- 判断: 計画の読み取りの上限（1台 4 MiB / 5 秒、全体 16 MiB / 5 秒）は、見えた最大（1台 68 KB、全体 108 KB）の約 60 倍あり、十分に追いつける。ただし、このワールドは Udon の出力が少ない。毎フレーム `Debug.Log` するワールドや、エラーが続くワールドでは増える。

#### 後片付け（13:52）

VRChat はすべて閉じ、コピーしたワールド（`vrc-remote-s5-…`）は消した。Logging は `0x1`（完全）で、始める前と同じ。`config.json` と `managed-clients.json` は、始める前とハッシュが同じ。Bridge は PID 29812 のまま動いており、S5 の間に Bridge での配置は無い（results の更新は 0 件）。SPK-003 で動いていた2台は閉じたまま（SPK-003 の側で配置し直す）。

### S5b: 偽の行の試験ワールド（2026-10-18 14:21〜14:30）

S5a の C で残った「Udon の文字列の改行がそのままログに書かれるか」を、専用の試験ワールドで確かめた。Mac の Unity の試験用プロジェクト（`~/Documents/VRChatProjects/Vrc-remote-test`）に UdonSharp の `S5LogForgeProbe`（`Assets/S5LogProbe/`）を置き、Remote Build & Test で送った。プローブとシーンに置くためのエディタースクリプトの写しは `docs/validation/s5b-log-probe/` にある（Unity のプロジェクトの `Assets/S5LogProbe/` と `Assets/S5LogProbe/Editor/` に置き、`Tools > VRC Remote Test S5 > Add Log Probe` を実行すると再現できる）。確認のあと、試験用プロジェクトからは取り除いた。プローブは入室から 15 秒ごとに 6 回、偽の `[Behaviour]` の行、LF と CRLF のあとに偽の日時の頭を入れた複数行、`Warning` と `Error`、タブと ESC、日本語と絵文字、4,096 文字と 65,536 文字の行を `Debug.Log` で出す。偽の行には必ず `s5forged` か `S5Fake` が入る。Windows のセッションが、2台の output_log をバイト単位で調べた（全体の出力は Windows の `%LOCALAPPDATA%\VRCRemoteTest\s5\s5b-analysis.txt`）。2台の結果は同じで、下の 16 進は profile 0 の台（`output_log_2026-10-18_14-21-40.txt`、182,254 バイト）のもの。

**配置**: build `20261018T052136873Z-108899a4`。Bridge（v0.5.1、Relaunch）の取り込みは 14:21:40.280。記録の台（S5a で閉じた PID 20800・8148）はもう終わっていたので `Closing` は出ず、`Launched VRChat client 1/2 (profile 0, PID 11676)` は 14:21:40.322、`2/2 (profile 1, PID 23268)` は 14:21:43.343、`deployed` は 14:21:43.345。Unity の `Deployed as …` は 14:22:09。2台とも `local:dab8e4…` に入り、お互いの `OnPlayerJoined` を出した。`[VRCRT] S5 done after 6 rounds` は profile 0 が 14:23:04、profile 1 が 14:23:06。

| 確認 | 結果 |
|---|---|
| A. 偽の頭の直前のバイト | LF のあとに書いた偽の頭は、直前が `74 0A`（単独の LF）。CRLF のあとに書いた偽の頭は、直前が `0D 0A` で、本物の行の頭とまったく同じ。6 回とも同じ。頭のある行（偽を含む）850 行（profile 1 は 781 行）のうち、直前が CRLF は 843（774）、単独の LF は 6（6、LF の偽の 6 回とちょうど一致）、ファイルの先頭が 1。**Udon の文字列の LF・CRLF は置き換えられずにそのまま書かれる。** S5a の C の判断（単独の LF のあとの頭は続きの行とみなす）はこのワールドでも成り立ち、LF の偽はすべて除ける。CRLF の偽はバイトでは本物と区別できない（偽の日時 `2026.01.01` は今回たまたま違うだけで、ワールドが今の時刻を書けば区別できない） |
| B. 1 行の偽装 | `[Behaviour] Joining local:s5forged1` は、直前の `0D 0A`、日時、`Debug` と空白 6 つ、`-  [Behaviour] ` まで本物と同じ。`OnPlayerLeft S5Fake` と `Finished entering world. s5forged1` も同じ |
| B'. 偽の行が入る位置 | round 1 の偽の行は、ワールドの `Start` から出たので、本物の `Finished entering world.` より前に書かれた。本物の `Joining local:dab8e4…` は 0xB6CA（14:21:44）、偽の `Joining` / `OnPlayerLeft` / `Finished entering world. s5forged1` は 0x13C23〜（14:21:49）、本物の `Finished entering world.` は 0x260E5（14:21:49）。「`Joining` のあと最初の `Finished entering world`」を入室の完了とみなすと、ワールドの `Start` が出した偽の行に先を越される |
| C. `Warning` と `Error` | 頭は `Warning    -  `（`57 61 72 6E 69 6E 67 20 20 20 20 2D 20 20`）と `Error      -  `（`45 72 72 6F 72 20 20 20 20 20 20 2D 20 20`）。Udon の `Debug.LogError` には、スタックトレースなどの続きの行が付かない（`0D 0A` で 1 行で終わり、次はすぐ次の頭）。レベルは空白で 11 文字にしてから `-  ` が付く（S5a の C の記述を訂正した） |
| D. タブと ESC | どちらも置き換えられずに書かれる（`74 61 62 09 68 65 72 65`、`1B 5B 33 31 6D`） |
| E. 日本語と絵文字 | UTF-8 のまま書かれる（BOM や置き換えは無い。`🎮` は `F0 9F 8E AE` の 4 バイト）。Unity の Log Viewer では `🎮` が表示されなかったが、ファイルには書かれている |
| F. 長い行 | long4k は 4,152 バイト、long64k は 65,593 バイトで、どちらも切り詰められずに 1 行で書かれた（頭 34 バイト ＋ マーカー ＋ 本文 ＋ ` end` ＋ `0D 0A`）。続きの行は無い |
| G. Bridge の写し（`logs\vrchat-latest.log`） | 今の写しは、ログの最後の 256 KiB を読み、`\n` で分けて行末の `\r` を落とし、最後の 200 行を取り、512 文字を超える行を 497 文字＋`... [truncated]` にして LF でつなぐ（`VrchatLogReader.ReadTailLines`・`BuildBoundedOutput`）。14:23 の写しは 21,318 バイト、200 行で、CR は 0 個。**写しでは CRLF と LF の区別が無くなり**、LF の偽の頭（`S5FakeNewline`）も CRLF の偽の頭（`s5forgedcrlf`）も、続きの行（`multiline third`）も、それぞれ独立した 1 行に見える。Unity の Log Viewer でも、`2026.01.01 00:00:00 Debug - [Behaviour] Joining local:s5forgedcrlf6` が独立した 1 行として表示された。タブ・ESC・日本語・絵文字は写しでもそのまま。写しに入っていたのは round 2〜6 だけで、round 1 の長い行は最後の 200 行より前だった |

判断:

- 単独の LF のあとの日時の頭を続きの行として扱えば、LF で改行した偽の行は除ける。CRLF で改行した偽の行と、1 行の偽装は、ログのバイトでは本物と区別できない。
- ワールドの `Start` の出力は、本物の `Joining` と `Finished entering world.` の間に入る。ワールドが `[Behaviour] Finished entering world.` と同じ文字列を出せば、入室の完了を先に偽装できる。
- この判断は、Bridge が元の output_log のバイトで行う必要がある。今の写しは CRLF と LF の区別を落とすので、写しを読む側（Unity、CLI）では使えない。
- 1 行が 64 KiB を超えることがある（ワールドが長い文字列を出せば、そのまま 1 行で書かれる）。

**状態**: 2台（PID 11676・23268）は S5b の試験ワールドのまま動かしてある。Logging（`0x1`）と config.json は触っていない。SPK-003 の側が次に配置すれば、Relaunch で入れ替わる。

### P1 の実機確認: 台ごとのログ・マーカー行・台の一覧（2026-10-18 16:00〜16:12）

計画 `.claude/docs/research/client-observation-control-plan.md` の P1 の Bridge（PR #27、`0cb5e64`）を、§7「実機（Windows）P1」の項目で確かめた。Windows のセッションが行い、写しと JSON は Windows の `%LOCALAPPDATA%\VRCRemoteTest\p1\` に項目ごとのフォルダーで残してある。表示名は `<表示名#n>` に伏せた。

**準備**: `0cb5e64` を Windows でビルドした（SDK 10.0.400、`--vulnerable` で脆弱なパッケージなし、exe の SHA-256 `2D55F541…30FB1`、ProductVersion `1.0.0+0cb5e64…`）。`install-bridge.ps1` で入れ替え、v0.5.1 の exe（`03BFEEDE…`）と config.json・managed-clients.json は `%LOCALAPPDATA%\VRCRemoteTest\p1-test-backup-20261018-160000` に退避した。`dotnet test` は 660 件中 655 件が成功した。失敗の5件は、シンボリックリンクの3件（従来どおり）と、VRChat が動いていたための `VrchatProcessMonitorTests` の2件で、VRChat を閉じてそのクラスだけを流すと32件とも成功した。config.json に `"LogMarkerPrefixes": ["[VRCRT]", "[SPK003]"]` を足した。Logging は「完全」。

**入れた直後（S5b の試験ワールドの2台が動いていた）**: 2台を slot 0・1 として読み始めた（epoch `1792306821855`・`1792306821876`）。
- 単独の LF のあとの偽の頭（`S5FakeNewline`）は、マーカー行の中で `\n` にエスケープされて前の記録の続きになり、出来事にも数えられなかった。
- CRLF のあとの偽（`Joining local:s5forgedcrlf`）と1行の偽装（`Joining local:s5forged<n>`、`OnPlayerLeft S5Fake`）は出来事に数えられ、state は unknown になった（見分けられないことは計画 C6 と手順11-1のとおり）。`Finished entering world. s5forged<n>` は完全一致しないので使われなかった。
- タブと ESC はマーカー行で `\t`・`\u001b` になり、日本語と絵文字はそのまま。続きの行は `slot-n.log` で先頭に空白4つが付いた。
- long4k のマーカー行は 4,096 バイトで切られて ` [truncated]` が付き、long64k は `701<TAB>…[VRCRT] S5 long64k [truncated]`（本文は残らない。64 KiB を超えた行は最後の空白まで残すため）。

| 項目 | 結果 |
|---|---|
| A. 2台（Relaunch、`VrchatProfiles [0,1]`、SPK-003 のプローブのワールド） | **isMaster 以外は合格**。16:01:25 に送り、Bridge は2台を閉じて（1.5 s、強制終了 0）profile 0（PID 25868、16:01:28.189）と profile 1（PID 14732、16:01:31.198）を起動した。`slot-0.log` と `slot-1.log` の最後の5行はそれぞれの台の output_log（`16-01-28`・`16-01-31`）にだけあり、混ざっていない。`vrchat-latest.log` は `slot-0.log` と同じ。`vrchat-clients.json` は2台とも joined、instance は同じ `local:dab8e4…5383`、参加者2人、loggingLevel full、authenticated true、catchingUp false。**isMaster は2台とも null**（下の不具合）。`slot-0.markers.log` は先頭がヘッダー（`clientGeneration 3929843941225208`、`observationEpoch 1792306888247`）で、`689<TAB>… [SPK003] START revision=playerdata-probe-v2 …`、`747 … PLAYER_JOINED id=1 local=True …`、`785 … PLAYER_RESTORED id=1 local=True` など slot 0 は27記録、slot 1 は26記録。前の S5b の台のマーカー行は `slot-0/1.markers.prev.log` に移った。Bridge のログに WRN・ERR なし |
| B. 4台（Reload、`VrchatClientCount 4`） | **isMaster 以外は合格**。16:04:59 に送り、PID 8508・6196・26756・23284 を約3秒おきに起動した（各台の `New output_log …` も出た）。slot は起動順に 0〜3 で、各 `slot-n.log` の最後の5行はそれぞれの台の output_log にだけあった。5秒ごとに90秒見て、`observedThroughSeq` は4台とも増え続け、`catchingUp` は一度も true にならなかった。state は starting → joining → joined と進み、+28 s で4台とも joined・参加者4人・watchWorlds true。CPU は全体で 11〜41%、Bridge のプロセスは論理16コアに対して 0.21〜0.52%。`Reading … (observation epoch …)` は各台1回。そのあと Reload の配置を2回送り、4台とも `local:ce318f…` に移って joined に戻った |
| C. 結合の材料 | B の4台のマーカー行は 191 記録（slot 0〜3 が 49・47・48・47、16:05:09〜16:05:24）。時刻順の表は Windows の `p1\C-markers-merged.csv`。時刻は秒までで seq は台ごとの番号なので、**同じ秒に複数の slot の記録がある組（7つ）は、時刻と seq だけでは台をまたいだ前後が決まらない**。Unity と `vrcrt.sh` の結合は、その場合 slot の番号順に並べる（前後関係の証明ではない） |
| D. Bridge の再起動のあとの slot と epoch | **合格**。(1) A の2台が動いたまま 16:03:40.974 に再起動: slot と clientGeneration は変わらず、epoch は `1792306888247 → 1792307022948`、`1792306893434 → 1792307022975` に増えた（再起動の時刻の Unix ミリ秒は約 `1792307020974`）。markers.log は 16:03:42 に、同じ記録（27・26件、seq も同じ、重複なし）で epoch だけ新しいヘッダーに置き換わった。.prev は作られなかった。(2) B の slot 0 の台（PID 8508）だけを閉じて再起動: 残りの台は slot 1〜3 のまま、slot 0 は running false・state left・logStatus notRunning、`slot-0.log` は `The VRChat client in slot 0 (PID 8508) is not running.`、`slot-0.markers.log` は残った。(3) 同じ引数で1台を手で起動（PID 15300）: slot 0 に入り、`slot-0.markers.prev.log` は閉じた台（clientGeneration `4113320489605551`）の markers.log とハッシュが一致し、新しい `slot-0.markers.log` は新しい台のヘッダー（clientGeneration `4460553457177052`）で始まった |
| E. Logging「エラーのみ」 | **合格**。空いていた slot 0 に入った台（PID 26012）は、30秒後は loggingLevel unknown・state starting、70秒後は errorsOnly・state null で、warnings と `slot-0.log` の先頭に `# VRChat's Logging is errors only for this client, …` が出た。ログインの警告は出なかった（authenticated は空）。終わったあと Logging を 1 に戻した |
| F. 秘匿化 | **合格**。共有の `logs\` と `status\` の13ファイル（147 KB）に、`usr_***` 以外の usr_ の ID、実際の `C:\Users\<名前>`（URL エンコードを含む）、IPv4・IPv6、Windows のユーザー名は0件。`usr_***` が147件、IP は `answer from <ip>:5353` の形。リロード直後の `World Updated:` の行は `C:\Users\<user>\AppData\…` になっていた（元の output_log では実際のユーザー名）。token・cookie・authorization を含む行は写しの範囲に無かった |

**不具合: isMaster が本物のログでは常に null だった**。VRChat の `[Behaviour] I am MASTER` と `I am *NOT* MASTER` は1行ではなく、単独の LF と `Actor Nr: <n>` が続くエントリだった（`… I am MASTER` のあとが `0A`、その直後に `Actor Nr: 1` と `0D 0A`）。10-17 以降のログの341件すべてがこの形で、CRLF で終わる1行の形は0件。Bridge は単独の LF を含む行を出来事に使わないので、Master の行に届かなかった。S5a の A1 でこの行を1行として書いていたのは、行を LF で分けて読んでいたため（S5a の記述の訂正は別の PR で行う）。PR #27 で、この2つの行だけ「1行目の本文が完全一致し、続きが `Actor Nr: <数字>` の1行だけ」のときに使うように直した（計画 C6・F1 の規則も同じに更新された）。修正後の実機の確認はまだ。

**後片付け**: VRChat はすべて閉じた。Logging は `0x1`。config.json は確認前のもの（Relaunch、`VrchatProfiles [0,1]`、`LogMarkerPrefixes` なし）に戻し、Bridge を起動し直した（16:12:07、WRN・ERR なし）。Bridge の exe は P1 のビルド（`2D55F541…`）のまま。v0.5.1 に戻すときは退避先の exe（`03BFEEDE…`）で入れ替える。

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
判定: 案B Go（同じアカウントのN台）。別アカウントを混ぜる場合は案Cが必要
理由:
  ケース1で2台が同じローカルインスタンスに入り、ケース2の2回の配置で、
  2台ともそろって同じインスタンスにリロードした（約8秒・約2〜4秒）。
  ケース7で3台・4台でも同じ結果になり、"Can't Travel" や追い出しは起きなかった。
  ケース6では、None でも黒画面は再現しなかった。
  ケース5で、別アカウントの2台は配置のたびに別々のインスタンスへリロードした
  （2回とも再現）。全台を共通の --url で起動し直すと、別アカウントでもそろった。

Bridge実装への申し送り事項:
  - 同じアカウントのN台は、watch-worlds のリロードで常に同じ固定のインスタンス
    （local:ce318f…:1）に入る。配置したワールドや起動時の --url によらない。
    そのため、初回の配置だけで全台がそろう（ケース3）。
  - それでも初回は --url で起動するのがよい。--url が無いと、各台がホームワールドへ
    行こうとして失敗し、約25秒後に台ごとに別の Error World に入る。--url なら
    起動直後からテストワールドに入れる。
  - --url の roomId はインスタンスIDに使われない（roomId を変えても local:dab8e4…:1）。
    インスタンスの同一性を roomId で確かめる設計にしない。
  - ログで入室を確かめるなら Logging は「完全」が必要。None では output_log が空、
    Errors Only では Destination set・OnPlayerJoined・World Updated が出ない。
    今の readiness 判定は output_log を使わないので影響しないが、Log Viewer も
    ほぼ空になる。setup-windows.md には「完全」を勧めると書く。
  - 起動間隔は1秒以上あける（SDK と同じ3秒でよい）。同じ秒に起動すると output_log の
    ファイル名が重なり、複数台が1つのファイルに書き込んで、ログを台ごとに分けられなく
    なる（オプションB）。output_log には PID が出ないので、台とログの対応は起動順と
    ファイル名の時刻で取る。同時起動だと API のレート制限エラーも大きく増える。
  - 2台目以降は VRCNP の名前付きパイプを作れない。外部ツールとの連携は1台目だけになる。
  - 全台が前回終了時と同じウィンドウ位置・大きさで重なって開く。Bridge で並べるか、
    ウィンドウサイズの起動引数を付けるかを検討する。
  - Logging 設定は profile ごとではなく全 profile で共通（ケース6）。
  - watch-worlds のリロード先はアカウントごとに決まる。別アカウントが混ざると、
    配置のたびに台が分かれる（ケース5）。分かれるかどうかは --profile ではなく
    アカウントで決まり、同じアカウントなら別 profile でもそろう。
  - 案C: 別アカウントを混ぜる構成では、配置のたびに全台を終了し、共通の --url で
    起動し直す。--url の行き先はアカウント・roomId・ワールドのパスによらず同じ
    local:dab8e4…:1 だった。起動し直すと入室まで約12秒かかり、リロード（約2〜8秒）
    より遅い。
  - 案C で --watch-worlds を付けたままにすると、配置した瞬間に各台が別々の
    インスタンスへリロードしてから、終了・起動し直しになる。--url だけで起動して
    --watch-worlds を外すか、配置の前に終了するかを検討する（未検証）。
  - 各台がどのアカウントかは、profile 番号だけでは分からない（今回も一時、profile 0・1
    が同じアカウントになっていた）。Bridge が知るには output_log の
    User Authenticated を読む必要がある。案B/案C の切り替えは設定で明示させるのが確実。
  - profile ごとのログインは手作業になる。一度ログインすれば次からは自動で入る
    （13:40 の起動では入力なし）。--profile なしで起動した台（Steam から起動した場合も）
    でアカウントを切り替えると profile 0 のアカウントが変わるので、setup-windows.md に
    「--profile=N を付けて起動してログインする」手順を書く。
  - 今の Bridge のままでも、同じアカウントで手動起動した複数台への配置は成功し、
    全台がリロードする（オプションA）。
  - Moonlight 越しには、マウスだけでは台を切り替えられない（自動操作でも人の手でも
    同じ）。Bridge に「指定した台を前面に出す」機能を付け、Unity のウィンドウから
    台ごとに呼べるようにする。Bridge は台ごとの PID と起動順を知っているので、
    Moonlight のキー設定に左右されない。それまでは、Moonlight 越しに Mac で
    Option+Tab を押して切り替える（ケース8。ウィンドウ表示の Moonlight で、VRChat の
    メニューを開かずに効き、切り替えたあとの入力もその台に入ることを確認した。
    全画面表示の Moonlight と Cmd+Tab は未確認）。
  - 別アカウントの複数台に今の Bridge で配置すると、配置は成功するが台が分かれる
    （オプションA を別アカウントでも実施）。案C を実装するまでは、別アカウントの
    構成では使えない。
  - 追加検証（組み込み前のゲート）の結果:
    - `Start-Process -PassThru` で返る PID はそのままゲーム本体で、入室後も同じ PID・
      同じ作成時刻。子プロセスは UnityCrashHandler64.exe だけ（ゲート1 合格）。
    - 案C の「--watch-worlds なしで終了 → 配置 → 起動し直す」は、3回くり返して毎回
      別アカウントの2台が同じインスタンスにそろった（ゲート2 合格）。
    - CloseMainWindow はウィンドウがあれば 1.5〜3.8秒で終わる。起動から1秒未満で
      ウィンドウが無いと False が返り、終了しない。強制終了をくり返しても設定や
      ログイン状態は変わらなかった（ゲート3）。
    - 案B で --url 起動の直後に配置する場合は、全台のログインが済むまで待つ
      （最後の台の起動から2秒後の配置では1台が取りこぼした）。
  - 未実施: オプションC・D、昇格した VRChat の確認。
```
