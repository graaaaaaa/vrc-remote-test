# VRC Remote Test

macOS上のUnityでVRChat Worldを開発し、Windows上の実VRChatクライアントへローカルWorld Buildを自動転送してテストするための開発ツールです。

Windowsマシンを「Unity開発機」ではなく「VRChat実機テストターゲット」として扱います。

```
Mac / Unity
[ Remote Build & Test ]
      │
      ├─ VRChat SDKでWindows向け.vrcwをBuild
      ├─ SHA-256計算
      ├─ Windowsへ転送 (認証付き HTTPS)
      ├─ Windows Bridgeが検証・配置
      └─ VRChat --watch-worlds がreload
```

## インストール

ALCOMで以下のリポジトリリスティングURLを追加してください（`Settings > Packages > Add Repository`）:

```
https://raw.githubusercontent.com/graaaaaaa/vrc-remote-test/main/index.json
```

追加後、`VRC Remote Test`パッケージがVCC/ALCOMのパッケージ一覧に表示されます。Windows側の初期セットアップ（Bridgeの配置、LAN Directペアリング、VRChat起動設定）は [`docs/setup-windows.md`](./docs/setup-windows.md) を参照してください。

Windows Bridge の実行体（`VRCRemoteTest.Bridge.exe`）は、[Releases](https://github.com/graaaaaaa/vrc-remote-test/releases) に GitHub Actions でビルドされたものが、SHA-256 とビルドの来歴（artifact attestation）付きで添付されています。Windows 上で直接ダウンロードし、来歴を確認してからインストールしてください（[`docs/setup-windows.md`](./docs/setup-windows.md) の手順1-A）。

## 使い方

セットアップ完了後、Unity上で `VRChat SDK > VRC Remote Test` からウィンドウを開きます。

- **Remote Build & Test**: WindowsビルドしてWindows実機へ転送・配置。VRChatが`--watch-worlds`付きで起動していれば自動でリロードされる
- **Deploy Last Build**: 直前のビルドを再配置（コード変更なしでVRChat側だけ再テストしたい場合）
- **Preflight**: Bridge接続・SDK・VRChatの状態を常時表示
- **VRChat Log**: WindowsのVRChatログをリアルタイム表示・カテゴリフィルタ。台ごとのログと、ワールドが出すマーカー行（`[VRCRT]` などで始まる行）を台を切り替えて表示
- **VRChat Clients**（Bridge 0.8.0 以降）: Bridge が起動する VRChat の台数とアカウントを選び、`Apply to Bridge` で Windows の config.json に書く。Reload（同じアカウントで1〜4台）か、Relaunch（profile ごとに別のアカウント、例 `0, 1, 2, 3` で4台）を選ぶ。profile ごとに最後にログインしたアカウントも表示する。Bridge は起動し直して設定を確かめ、次の配置から新しい台で起動する。モードを変えると Bridge が起動した台を閉じる（手で起動した VRChat があれば断る）。`Close Bridge's VRChat` で Bridge が起動した台を閉じられる
- **VRChat Clients の台の表**（パッケージ 0.11.0 以降、Bridge 0.10.0 以降、Relaunch）: 台ごとの PID・profile・状態・インスタンス・ログイン・エラーワールド・世代を出し、`Close`／`Launch` でその1台だけを閉じる・起動し直す。Bridge は要求が届いた時点で記録にある台に働きかけるので、一覧を読んだあとの変化は止められない（確認の画面にも出る）
- **Deploy and Collect**（パッケージ 0.11.0 以降）: `Run` 1つで、配備の前の状態を保存し、ビルド（または前のビルドの再配置）を配り、指定した全 slot が準備できるのを待ち、台・世代ごとに目印と全文ログを集めて `run.json` に記録する。結果は deploy・launch・clientReadiness・worldBuildMatch・evidence に分けて PASS／FAIL／UNKNOWN／CANCELED で出し、試験の合否は判定しない。runner が run を引き継いだあと（`run.json` の state が `running`）は、Editor を閉じても最後まで進む（ビルドの最中は Editor が要る）。記録は `~/Library/Application Support/VRCRemoteTest/runs/`（git と iCloud の外）
- **Moonlight連携**: リモートデスクトップでWindows画面を見ている場合、ボタン一つでMoonlightを前面表示

詳しい機能一覧は [`package/README.md`](./package/README.md) を参照してください。

### Mac のスクリプト（`scripts/vrcrt.sh`）

本体はパッケージの中の `package/Tools~/vrcrt.sh` で（0.11.0 から。Unity の利用者にも届くように）、`scripts/vrcrt.sh` は同じ引数・出力・終了コードで渡すだけの入口です。今までの呼び方はそのまま使えます。

エージェントやターミナルからは、認証付き HTTPS で台の状態とマーカー行を取得できます。接続先と証明書指紋は `VRCRT_BRIDGE_URL`、`VRCRT_CERT_SHA256`、トークンは macOS Keychain または `VRCRT_TOKEN` で設定します。

| コマンド | 内容 |
|---|---|
| `scripts/vrcrt.sh status [--json]` | 台の一覧（slot、PID、状態、インスタンス、人数、Master、Logging など）。15秒より古ければ警告 |
| `scripts/vrcrt.sh result <build-id>` | Bridge の配置結果を取得し、`deployed` 以外は失敗として返す |
| `scripts/vrcrt.sh log <slot> [--markers] [--lines N]` | 台のログかマーカー行。制御文字とタブはエスケープして出す |
| `scripts/vrcrt.sh log all --markers` | 全台のマーカー行を時刻と seq の順にまとめて出す（`slot<TAB>seq<TAB>行`） |
| `scripts/vrcrt.sh export-log all <保存先ディレクトリ>` | 稼働中の全台の全文ログを、世代・セッション照合と SHA-256 検査の後に保存。`all` の代わりに slot 番号も指定できる |
| `scripts/vrcrt.sh cursor` | 今の位置（台ごとの世代・observationEpoch・seq）を JSON で出す。読み終えていない台があれば失敗する |
| `scripts/vrcrt.sh wait-marker <slot\|all> <正規表現> <秒> [--after <cursor>] [--any-generation]` | カーソルより後の、同じ世代・同じ epoch のマーカー行が正規表現に一致するまで待つ。`all` はカーソルの時点で動いていた全台での一致を待つ |
| `scripts/vrcrt.sh wait-same-instance <秒> [--slots 0,1] [--after <cursor>]` | 指定した台がすべて joined で同じインスタンスにいるまで待つ。`--after` なら、カーソルより後に入り直したことも求める |
| `scripts/vrcrt.sh doctor` | Bridge 0.8.0 以降: 版と実行体の SHA-256、いま効いている設定、注意、最後の設定の変更の結果、profile ごとのアカウント、台ごとのログイン状態、Bridge のログの末尾 |
| `scripts/vrcrt.sh config get` | 変えられる設定の今の値と、`VrchatExecutable` の候補 |
| `scripts/vrcrt.sh config set KEY=VALUE…` | 設定を変え、Bridge が起動し直して効いたかを待つ（最大90秒）。`KEY=null` で項目を消す |
| `scripts/vrcrt.sh restart` | Bridge を起動し直し、新しい host が立つのを待つ |
| `scripts/vrcrt.sh close-clients` | Bridge が起動した VRChat を閉じる。閉じたと確かめられない台が残れば終了コード 1 |
| `scripts/vrcrt.sh close-client <slot>` | Bridge 0.10.0 以降、Relaunch: その slot の台だけを閉じる。ほかの台はそのまま。閉じたと確かめられないか記録を直せなければ終了コード 1。答えを最大45秒待ち、答えが無ければ終了コード 3 |
| `scripts/vrcrt.sh launch-client <slot>` | Bridge 0.10.0 以降、Relaunch: その slot の台を、最後の配置と同じ profile・ワールド・インスタンスで起動し直す。動いていれば断る。Bridge の答えを最大90秒待ち、答えが無ければ終了コード 3（起動したかは `status --json` で確かめる） |
| `scripts/vrcrt.sh close-client <slot> --expect @FILE [--operation-id ID] [--json]`（`launch-client` も同じ） | Bridge 0.12.0 以降: FILE の前提（`docs/plans/r1b-contract.md` の §1.3 の7つの値）が今の Bridge と台に合うときだけ閉じる・起動する。合わなければ Bridge は何もせず終了コード 1。前提の形の誤りは 2（何も送らない）、Bridge が前提に対応していなければ 3（何も送らない）。`--json` は答えの JSON を出す |
| `scripts/vrcrt.sh profiles` | profile ごとに、最後にログインしたアカウント |
| `scripts/vrcrt.sh bridge --json` | 0.11.0 以降: Bridge の `GET /api/v1/bridge` を JSON で（版、実行体の SHA-256、プロセスの起動時刻、host の世代、設定の etag、空き容量） |
| `scripts/vrcrt.sh log <slot> --markers --json` | 0.11.0 以降: マーカーのヘッダー（世代・epoch・catchingUp・捨てた seq）と各行を JSON で。Bridge が切った行と伏せた行に印を付ける |
| `scripts/vrcrt.sh export-log <slot> <保存先> --generation G --epoch E` | 0.11.0 以降: その slot がまだその台・その読み方のときだけ全文ログを保存する。違えば何もせず終了コード 3 |

待つ前に `cursor` で位置を取り、操作のあとに `--after` で渡すと、操作より前の行を新しい出来事と取り違えません。

```bash
cursor="$(scripts/vrcrt.sh cursor)"
```

```bash
scripts/vrcrt.sh wait-marker 1 'PLAYER_RESTORED' 60 --after "$cursor"
```

終了コードは 0 が成功、1 が時間切れ（管理のコマンドでは、Bridge が断った、設定の変更が戻された・失敗した、も含む）、2 が使い方の誤り（正規表現の誤りと、2秒を超えた正規表現を含む）、3 が観測では答えられないとき（Bridgeの観測情報が無いか古い、Bridge の再起動などで読み直した、Logging が Full でない、マーカーが上限で捨てられた、管理のコマンドでは Bridge に届かないか 0.8.0 より古い。`close-client` と `launch-client` は 0.10.0 より古い）。管理のコマンドの詳しい使い方は `docs/setup-windows.md` の4。`wait-marker` の正規表現は、Bridge がエスケープしたあとの行に当てます。同じインスタンスでも同じビルドとは限りません。動作の確認は `bash scripts/test-vrcrt.sh`。

ビルドから結果・全文ログまで一度に保存する場合は、対話用 Unity Editor で対象プロジェクトを閉じ、保存済みシーンを指定して実行します。保存先は新しいディレクトリにしてください。ペアリングトークンは Keychain から読みます。`-quit` は付けず、ビルド完了後に拡張が Unity を終了します。

```bash
export VRCRT_BRIDGE_URL=https://<Windows-LAN-IP>:38443
export VRCRT_CERT_SHA256=<pairで表示された証明書SHA-256>
python3 scripts/vrcrt-run.py \
  --unity /Applications/Unity/Hub/Editor/2022.3.22f1/Unity.app/Contents/MacOS/Unity \
  --project /path/to/UnityProject \
  --scene Assets/Scenes/YourWorld.unity \
  --output-dir /path/to/new-test-evidence
```

`vrcrt-run.py` は Windows64 ビルド、Bridge 配置結果の照合、台の状態、稼働中の各台の全文ログ保存を行います。保存先が git の作業ツリーや iCloud の中なら断ります（0.11.0 から）。`--watch-worlds` なしのクライアントは配置後に自動再読み込みしないため、その場合は警告を出します。ワールドの動作まで判定する場合は `--expect-marker <正規表現>` を指定し、ビルド前のカーソル以降に出たマーカーを待てます。

### runner（`package/Tools~/vrcrt_runner.py`、0.11.0 以降）

決まった手順（「通常の複数台の準備と証拠の回収」、0.12.0 からは「宣言した slot の退出と起動し直し」、0.13.0 からは確かめる前の「人の段」）を、`vrcrt.sh` を順に呼んで行います。Python 3.9 以上が要ります（macOS なら Command Line Tools の `/usr/bin/python3`、ほかは `VRCRT_PYTHON`）。Unity の `Run` ボタンも同じものを使います。対話用の Editor を閉じたうえで、ターミナルから1行でも行えます。

```bash
python3 package/Tools~/vrcrt_runner.py run --build headless \
  --unity /Applications/Unity/Hub/Editor/2022.3.22f1/Unity.app/Contents/MacOS/Unity \
  --project /path/to/UnityProject --scene Assets/Scenes/YourWorld.unity --slots 0,1
```

- 開いている Editor から自分のコードで使うときは、`prepare --slots 0,1` が出す `runDir` と `worldBuildTag` を `RemoteBuildApi` に渡して建て、そのあと `continue --run <runDir> --claim-token <token>` を呼びます。
- 終了コードは 0 PASS（準備と回収ができた。試験の合格ではない）、1 FAIL、2 使い方の誤り、3 UNKNOWN、4 CANCELED。結果と理由は `run.json` の `result`・`reason`・`results` にあります。
- ワールドが読み込み時に `vrcrt.build=<tag>` を含む目印（Bridge の `LogMarkerPrefixes` の行）を出せば、各台がこの回のビルドに入ったかを照らします（`worldBuildMatch`）。出さないワールドでは UNKNOWN のままで、ほかは使えます。そのときは `--world-build-wait 0`（Unity では `Wait for the build tag` を外す）で、120 s の待ちを1回見るだけにできます。
- 止めるには run のフォルダーに `cancel.request` を作るか、Ctrl-C。runner が落ちた run は `recover --run <runDir>` で UNKNOWN に確定できます。古い run は `prune --older-than-days 30` で消します（自動では消しません）。
- 試験は `bash scripts/test-vrcrt.sh`（runner と `vrcrt-run.py` の試験も呼びます）。

#### 人の段（`--procedure manual-steps`、0.13.0 以降、まだ `--unvalidated` のときだけ）

配備と台の準備のあと、確認の項目（`vrcrt.checklist` の JSON。形は `docs/plans/r3-contract.md` §1）を1つずつ出して、人の答え（ok・ng・skip とメモ）を待ちます。答えるたびに、時刻と各台のログの位置と目印を記録します。答えは Unity のウィンドウ（待っている run があれば出る）からも、ターミナルからも入れられます。

```bash
python3 package/Tools~/vrcrt_runner.py waiting --run <runDir>
python3 package/Tools~/vrcrt_runner.py answer --run <runDir> --wait-id <waitId> --item <id> ok --memo "線は見えた"
```

- `--leave-rejoin-after <id> --target-slot N` で、その項目の答えのあとに slot N を前提つきで閉じて起動し直します（代表の件 A の「持ち主が退出・途中参加」）。
- 答えは人の言ったとおりに `results.manualSteps` に残し、道具は試験の合否を決めません（`testVerdict` は `notJudged`）。待っているあいだに台や Bridge が変われば UNKNOWN で止めます。
- VR 台（slot 0）とデスクトップの観戦台（slot 1）は、Bridge の設定 `VrchatMode: VR`・`VrchatProfiles: [0,1]` で起動できますが、ヘッドセットでの確かめはまだです。

## 構成

- `package/` — Unity Editor用VPMパッケージ (`com.github.graaaaaaa.vrc-remote-test`)
- `bridge/` — Windows Bridge（.NET 10、Windows側で受信・検証・配置を行うコンソールアプリ）
- `package/Tools~/` — Mac の CLI（`vrcrt.sh`）と runner（`vrcrt_runner.py`）。Unity は `~` で終わるフォルダーを取り込まない
- `scripts/` — Windows側セットアップ/起動用PowerShellスクリプトと、Mac の CLI の互換の入口（`vrcrt.sh`、`vrcrt-run.py`）と試験
- `docs/` — セットアップ手順（ユーザー向け）と開発資料（後述）

## 開発者向け情報

このツール自体の開発・改修に関わる場合は以下を参照してください（利用のみであれば読む必要はありません）。

- `VRC Remote Test — 実装仕様書.md`（リポジトリルート）— 設計仕様書
- [`docs/sdk-api-notes.md`](./docs/sdk-api-notes.md) — VRChat SDK Public APIの調査ノート
- [`docs/validation/watch-worlds-spike.md`](./docs/validation/watch-worlds-spike.md) — `--watch-worlds`挙動の実機検証記録
- [`docs/validation/multi-client-spike.md`](./docs/validation/multi-client-spike.md) — 複数クライアント（Number of Clients相当）の実機検証手順と記録
- [`docs/validation/smb-late-empty-manifest.md`](./docs/validation/smb-late-empty-manifest.md) — 配置のあとに空の manifest が現れた件の調査と確認手順
- [`docs/plans/r1-automation.md`](./docs/plans/r1-automation.md) — 手作業の確認を減らす追加開発の計画（R1a・R1b と候補）
- [`docs/validation/baseline-manual-ops.md`](./docs/validation/baseline-manual-ops.md) — 手作業の回数と時間の基準
- `bridge/README.md`・`package/CHANGELOG.md` — 各コンポーネントのビルド方法・変更履歴

## ライセンス

[LICENSE](./LICENSE) を参照してください。
