# LAN Direct 実装検証（2026-10-25）

## 範囲

SMB を使う送信・結果・状態・ログ経路を廃止し、認証付き HTTPS の LAN Direct に置き換えた。Bridge と Unity パッケージを一組で更新する破壊的変更。旧 Bridge や `SharePath` へのフォールバックはない。

## 自動試験とビルド

| 環境 | 実施内容 | 結果 |
| --- | --- | --- |
| macOS / .NET 10 | Bridge テスト。認証、証明書不一致、サイズ超過、同一 build ID の再試行と競合、ログのセッション照合・伏せ字を含む | 669 件成功、0 件失敗 |
| macOS / bash | `scripts/test-vrcrt.sh`。CLI の HTTPS API 観測コマンド | 33 チェック成功 |
| Unity 2022.3.22f1 / macOS の一時プロジェクト | パッケージ単体の C# コンパイル | 成功 |
| Unity 2022.3.22f1 / macOS の一時プロジェクト | Editor テスト | 100 件成功、0 件失敗 |
| macOS / .NET 10 SDK | Bridge を `win-x64`、self-contained、single-file で publish | 成功 |

Unity の一時プロジェクトには Worlds SDK の依存関係を入れた。`vrchat-commander` の旧試作 Editor コードは削除した SMB API を参照しているため、このパッケージ単体の検証には含めていない。同プロジェクトへの組み込み時には試作コードの移行が必要。フルプロジェクトの最初のテスト実行は LightVolumes の OpenCL ベイクでクラッシュしたため、上記の Editor テスト結果とは区別する。

## Windows 実機

Moonlight の Desktop 接続は一度切断が続いたが、2026-10-25 に復旧し、Windows PowerShell と画面を操作できた。初期確認時、Windows では旧 Bridge が稼働中で、新 HTTPS ポート `192.168.0.21:38443` は接続拒否だった。PowerShell 7 は使用可能。既存 `%LOCALAPPDATA%\VRCRemoteTest\config.json` には旧 `StagingDirectory` があり、`LanBindAddress` はまだなかった。この時点では設定や稼働中 Bridge を変更していない。

新 Bridge の win-x64 self-contained 単一実行体（SHA-256 `710af88a0abb7c6ea677cf8a065cb7142e331b64e289a2a5f063a11a2f857bcd`）を Mac で生成し、LAN の一時 HTTP サーバーから Windows Chrome で取得を試した。Chrome が「安全でないダウンロード」として保存を保留した。ユーザーが警告の保存操作を行い、Windows の Downloads に約100MBの `VRCRemoteTest.Bridge (3).exe` があることを画面で確認した。この段階では Windows 側の SHA-256 と Bridge 更新は未確認だった。上記の模擬・一時環境の成功を Windows 実機の合格とは扱わない。

15:52 時点で Windows ホスト `192.168.0.21` は ICMP に応答したが、22/5985/5986/3389/38443/8080 の TCP 接続はいずれもタイムアウトした。Moonlight 画面では Explorer のキーボード操作が可能だった一方、PowerShell の起動操作はコンピューター操作の自動承認で却下された。Windows 側のファイル照合・Bridge 更新は行っていない。

16:12 にユーザーの操作許可を受け、Windows PowerShell 7.6.7 で `VRCRemoteTest.Bridge (3).exe` を SHA-256 照合した。結果は `710AF88A0ABB7C6EA677CF8A065CB7142E331B64E289A2A5F063A11A2F857BCD` で、Mac で生成した実行体と一致した。Bridge の配置・起動およびファイアウォール変更はまだ行っていない。

16:33 以降、ユーザーの許可を受けて導入スクリプトを Windows の Downloads に取得し、SHA-256 `2D8D06087BFD2A6DCE5AEE13E6F48FA229FC45960DAD380C28EFA7D3CC94B8F9` を Mac 側と照合した。旧 Bridge を停止し、`config.json` から旧 `StagingDirectory` を削除して `LanBindAddress=192.168.0.21` と `LanPort=38443` を設定した。元の設定は `config.json.bak` に保存した。照合済み実行体を `scripts/install-bridge.ps1 -StartNow` で配置し、配置先でも SHA-256 が一致した。ログオン時のタスク `VRCRemoteTestBridge` を再登録し、Bridge が `https://192.168.0.21:38443` で待ち受けることを Windows の起動ログで確認した。

Windows Defender Firewall には Bridge 実行体向けの自動生成された広い Private 受信許可が TCP と UDP の2件あった。管理者 PowerShell で `VRC-Remote-Test-LAN-Direct` を Private/TCP、ローカル `192.168.0.21:38443`、接続元 `192.168.0.16` に限定して追加し、自動生成2件を無効化した。`Get-NetFirewallRule` で限定規則が有効、自動生成2件が無効であることを確認した。Mac から `192.168.0.21:38443` への TCP 接続は成功した。Mac の `openssl s_client` で取得した証明書の SHA-256 は `b41b31f55d50892fcb5c3e066a49a7c42db91f26289b4371e1bd0b3b4f23ac03` で、Windows の `pair` 出力と一致した。認証ヘッダーなしの `/api/v1/capabilities` は HTTP 401 を返した。実機 Bridge に対する `vrcrt.sh status` は、誤った証明書指紋で接続を拒否し、正しい指紋とダミートークンでは `/api/v1/clients` の HTTP 401 を返した。Windows の専用 `state/status/vrchat-clients.json` の生成も確認した。ペアリングトークンは検証ログに記録しない。

ユーザーが Unity の Save Pairing を実行し、Mac Keychain の `VRCRemoteTest.LanDirect` 項目を秘密値を表示せず確認した。認証付き `vrcrt.sh status` は Windows Bridge から 4 枠（いずれも停止中）の状態を取得し、`cursor` と `log all --markers --lines 5` も成功した。17:14 に一時 Unity プロジェクトで Remote Build & Test を実行したが、VRChat SDK が `SDK builder error: Open the SDK panel to build and upload worlds` を返し、ビルド前に失敗した。SDK 実装では Builder のシーン一覧未初期化時にもこのエラーになる。アダプターに `IsValidBuilder` 呼び出しを追加した。修正版は Unity 2022.3.22f1 の一時コピーのバッチコンパイルで成功し、操作中の検証用 Editor でも再コンパイル成功を確認した。

修正版での再試行では UdonSharp 62 スクリプトのコンパイルが完了したが、SDK は `No AssetBundle has been set for this build`、`AssetBundle was not built`、`Scene export failed` を返し、Bridge への送信前に停止した。Unity は `Save Scene` のファイル選択を表示していたことをユーザーが確認した。検証用プロジェクトの `Assets/Scenes/LANDirectTest.unity` にシーンを保存した後の再試行では、SDK が 86,476 バイトの Windows `.vrcw` を生成し、Unity が SHA-256 `3dd82a8db081c5f948c8c6989ce1992634d36dd0e1c33e44e79c8cacf9c699f0` を記録した。LAN Direct で build ID `20261025T084449565Z-6d4ffb15` を送信し、Bridge の結果 API が `deployed` と配置名 `vrc-remote-20261025T084449565Z-6d4ffb15.vrcw` を返した。結果 API の SHA-256 は Unity の生成物と一致した。未保存シーンが上記失敗の原因だったと判断できる。

同じ Windows Bridge の認証付き HTTPS API からスロット 0 と 1 が同じローカルインスタンスへ `joined` し、2 人として観測された。両スロットとも `loggingLevel=full`、`authenticated=yes`。`vrcrt.sh status` と `vrcrt.sh log 0/1 --lines 4` で状態と各台の抜粋を取得した。ログマーカーはこのシーンで未出力のため、マーカー API は空の正常応答だった。全文ログ API は各スロットの `clientGeneration` と `observationEpoch` を指定して取得し、スロット 0 は 103,856 バイト、スロット 1 は 95,145 バイトで、返却メタデータのサイズと SHA-256 に一致した。Unity の `Save Full Log...` 画面操作は確認待ち。旧共有・アカウントの撤去も新接続の実測後に行う。

手動のスロット選択を不要にするため、`vrcrt.sh export-log all <directory>` を追加した。現在の台一覧から世代・observationEpochを読み、Bridge に全文スナップショットを要求し、ダウンロードのバイト数・SHA-256 を検証してから保存する。実機のスロット 0 と 1 の全文ログ保存に成功した。`vrcrt.sh result <build-id>` も追加した。CLI の fixture 試験は 33 チェック成功。

Unity バッチ入口を非同期 SDK ビルドが完了するまで稼働する形へ変更し、`-quit` を廃止した。保存済みシーンを `VRC_REMOTE_TEST_SCENE_PATH` で選び、`-buildTarget Win64` で起動する。最初のバッチ試験はターゲット未指定で `INVALID_BUILD_TARGET`、次は SDK の再試行がメインスレッド外に移って `FindObjectsOfTypeAll can only be called from the main thread` となった。再試行の Unity コンテキストを維持する修正後、バッチ実行でビルド ID `20261025T085548760Z-8065e669` が Windows Bridge に `deployed` として配置され、Unity は終了コード 0 を返した。

さらに `scripts/vrcrt-run.py` から一括試験を実施した。隔離した Unity プロジェクトの保存済み `LANDirectTest.unity` を Win64 でビルドし、ビルド ID `20261025T085759396Z-fd8d79e5` の配置結果を照合、`clients.json` と稼働中のスロット 0・1 の全文ログを自動保存して終了コード 0。証拠は `/private/tmp/vrcrt-e2e-run-1` に保存した。実機の2クライアントは `--watch-worlds` なしで稼働しており、自動再読み込みや新ビルド内の挙動は未確認。一括スクリプトはこの条件を警告し、配置成功をワールド再読み込みの証明として扱わない。

## 残る受け入れ確認

Unity の `Save Full Log...` 画面操作、`--watch-worlds` 付きクライアントへの自動再読み込み、セッション取り違え時の実機表示、再起動後の継続を確認する。Bridge の実機 API と `vrcrt.sh`、一括スクリプトによる結果・複数台の状態・抜粋・全文ログ取得は成功済み。新接続の実測後に旧共有とアカウントを手動撤去する。
