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
