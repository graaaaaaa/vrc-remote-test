# Bridge の Mac からの管理（v0.8.0）の検証（2026-10-31）

## 範囲

Windows の前に人がいなくても、Bridge の確認・設定の変更・起動し直し・VRChat を閉じる操作・ログイン名の確認を Mac から行えるようにした（PR #34）。Bridge の自己更新は入れていない。

## 自動試験とビルド

| 環境 | 実施内容 | 結果 |
| --- | --- | --- |
| macOS / .NET 10 | Bridge テスト。本番と同じ host の起動経路での設定の変更・前の版への戻し・同じポートでの待ち受け直し、門、mode の切り替え、413 の本文、アカウントの digest、新しいログインの行を含む | 742 件成功、0 件失敗 |
| macOS / bash | `scripts/test-vrcrt.sh`。手元の HTTPS サーバーでの Connection: close と、少しずつ届く応答に対する期限を含む | 87 チェック成功 |
| Unity 2022.3.22f1 の Roslyn と参照アセンブリ（.NET Standard 2.1、VRChat Worlds SDK） | パッケージの Editor と Editor テストの C# コンパイル | 成功 |
| GitHub Actions（PR #34） | test、publish、artifact-handoff、action-pins、CodeQL | すべて成功 |

Unity Editor の中での Editor テストの実行はしていない。

## Windows 実機（ローカル Build & Test、Windows の VRChat 2台）

実行体は PR #34 の CI が作ったもの（コミット `df08c53` を main に重ねたマージ用のコミット、版 `0.8.0+b3fa5fce…`、SHA-256 `8068a1605eb69feab4405c8c167f21cfee7ba715e1219186a8757f2a0b7cbb7e`）。Mac から LAN の一時 HTTP サーバーで渡し、Windows で SHA-256 を照合し、ユーザーが PowerShell 7 で `install-bridge.ps1` を実行して入れ替えた。`config.json`、状態のフォルダー、ペアリングはそのまま使われ、証明書の SHA-1 拇印は前と同じだった。

以下はすべて Mac の `scripts/vrcrt.sh` から行った（2026-10-31 00:11〜00:20 JST）。

| 確かめたこと | 結果 |
| --- | --- |
| `doctor` | 版、実行体の SHA-256、いま効いている設定（パスのユーザー名は伏せ字）、Steam の VRChat.exe の候補、Bridge 自身のログ（IP とユーザー名は伏せ字）を表示 |
| ログイン名 | 2台とも `authenticated`。profile 0 は graaaaa、profile 1 は graaaaaaa2 で、`idDigest` も別 |
| Relaunch のまま `AutoLaunchVrchat=false` | 422 で拒否（`AutoLaunchVrchat must be true when VrchatClientMode is 'Relaunch'.`）、終了コード1 |
| Reload への切り替え（`VrchatProfiles=null VrchatClientCount=2`） | Bridge が記録していた2台を閉じてから書き込み、約5秒で `applied` |
| Reload で `AutoLaunchVrchat=false` にして配備 | 配備は成功し、結果は `launch: {attempted: false, outcome: disabled}`。VRChat は起動されなかった |
| Relaunch と profile [0, 1] に戻す | `applied`。設定の値は試験の前と同じ |
| 起動し直し | 設定の変更の直後は 429（10秒以内のため、設計どおり）。そのあと2回とも2〜3秒で同じポートに待ち受け直し、プロセスの起動時刻は変わらない（host は6世代目まで） |
| 席の試験（VRChat Commander の `prt001_deploy_run.py --seat-test`、場面16） | PASS。結果は `launch: {attempted: true, outcome: launched, requested: 2, started: 2}` |

## 見つかって直したこと

v0.7.1 の Bridge を Mac から見たとき、ログインしている2台に「ログインが切れたかもしれない」と警告が出ていた。今の VRChat は `User Authenticated: …` を書かず、`[Authentication - <ランダム>] Preforming post login checks for <名前> (usr_…)` を書くためだった。両方を読むよう直し（`df08c53`）、上の実機の確認で効いていることを確かめた。

## 確かめていないこと

- Unity Editor の中での Editor テストの実行と、Remote Test のウィンドウの新しい表示。
- Windows での `config.json` の置き換えの共有違反（起きなかった）。
- リリースの実行体（タグから改めて組み立てる）での確認は、入れ替えのあとに行う。
