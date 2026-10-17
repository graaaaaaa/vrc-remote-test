# 配置のあとに現れた空の manifest（開発時の調査記録）

> **対象読者**: このツールのコード自体を開発・改修する人向けの調査記録です。VRC Remote Testを利用するだけであれば読む必要はありません（`README.md`を参照してください）。

**目的**: v0.5.0 のリリース後の通しの確認（`multi-client-spike.md` の「v0.5.0 のリリース後の通しの確認」の気づいたこと1）で見つかった、配置が終わったあとに同じ名前の空の manifest が `incoming` に現れ、Bridge が成功の結果ファイルを `MANIFEST_INVALID` で上書きした件について、原因を調べ、Bridge 側で防ぐ。

---

## 起きたこと

2026-10-18 01:17、Relaunch・2台の2回目（build `20261017T161729471Z-c44b3bfe`）。時刻は Windows の JST。

| 時刻 | できごと |
|------|----------|
| 01:17:29.471 | Unity が buildId を作った（buildId の時刻。Mac の時計）。このあと `.vrcw` と manifest を共有に書く |
| 29.896 | Mac が `<buildId>.ready.json.part` を `<buildId>.ready.json` に rename した（Windows の変更ジャーナル。以下「USN」） |
| 29.897 | Bridge が manifest を `state\processing` に写した（`state\archive` の manifest の最終書き込み時刻。下の注） |
| 29.901 | Bridge が共有の `.ready.json` を消した（USN）。29.904 に `.vrcw` も消した |
| 29.906〜29.907 | 同じ名前の `.ready.json` ができた（USN）。作成と名前付きストリームの追加・書き込み、時刻の変更、閉じる、の記録だけで、主ストリームへの書き込みは無い。rename の 10 ms 後、Bridge が消した 5 ms 後 |
| 29.922 | `Closing 2 VRChat client(s) ...` |
| 34.489 | `deployed` の結果ファイルを書いた。Unity はそれを読んで `[Succeeded]` |
| 39.493 | Bridge が次のスキャンで、中身が空（0 バイト）の `incoming\<buildId>.ready.json` を取り込んだ（`state\failed` の空のファイルの時刻） |
| 39.508 | `JsonException: The input does not contain any JSON tokens` で空のファイルを `state\failed` に移し、結果ファイルを `{"status":"failed","errorCode":"MANIFEST_INVALID","errorMessage":"Manifest could not be parsed."}` で上書きした |

正しい manifest（274 バイト）は `state\archive` にあり、配置したワールドと起動し直した2台はそのままだった。Unity は先に `deployed` を読んでいたので、Unity からは気づけない。その日のほかの `ready.json`（v0.5.0 の確認だけで 8 件。Reload・Relaunch、成功・失敗の両方）では起きていない。Relaunch の2回目のあと約 50 秒見ていた S4b でも現れなかった。

Unity のパッケージ（`SmbRemoteTransport.UploadBuildAsync` → `AtomicFile.WriteAllText`）は、`<buildId>.ready.json.part` に `File.WriteAllText` で書き、`File.Exists` → `File.Move` で1回 rename するだけで、そのあと同じ名前には触らない。

**時刻の注**: Bridge は共有の manifest を移さず、バイトを読んで `state\processing` に新しいファイルとして書く（その後 archive か failed に移す）。なので `state` にある manifest の最終書き込み時刻は、Mac が書いた時刻ではなく、Bridge が取り込んだ時刻になる。`multi-client-spike.md` の「v0.5.0 のリリース後の通しの確認」の「manifest が届いた」も、取り込んだ時刻だった（Windows で確かめた）。Mac が共有に置いた（rename した）時刻は、変更ジャーナルにしか残っていない。

最初は、空のファイルが「配置の約 10 秒後」に現れたとみていた。変更ジャーナルで見ると、できたのは Bridge が元を消した 5 ms 後で、約 10 秒後の 39.493 は、Bridge が次のスキャンで気づいた時刻だった。Bridge はビルドを1件ずつ処理し、`deployed` を書いた 34.489 の 5 秒後に次のスキャンをした。

## 原因の調べ（2026-10-18）

Mac のソース（macOS の SMB クライアント、Unity の Mono）と資料を読み、この Mac のローカルのファイルを調べた。そのうえで、Windows の NTFS の変更ジャーナル（USN ジャーナル）で、その日の記録を読んだ。共有（`/Volumes/VRCRemoteTest`）は、調査に使った Mac のシェルからは macOS のネットワークボリュームの権限で読めないので、Mac から共有での再現はしていない。

### Mac のソースと資料で確かめたこと

1. **Unity（Mono）は rename のあと何もしない**。Unity-Technologies/mono（unity-2022.3-mbe）の corefx で、`File.WriteAllText` は `open(O_WRONLY|O_CREAT|O_TRUNC)` → write → close（fsync なし）。`File.Move` は `FileSystem.Unix.cs` の `MoveFile` で、lstat（元）→ lstat（先）→ `rename(2)`。`File.Exists` は stat、`File.Delete` は unlink。close や rename のあとに、拡張属性や時刻を書くコードは無い。
2. **Unity が書くファイルには `com.apple.provenance` が付く**。この Mac の Unity（2022.3.22f1）が書いた `~/Library/Logs/Unity/Editor.log` やプロジェクトの `Library` のファイルに、`Unity.app` と同じ値の `com.apple.provenance`（11 バイト、`01 02 00 27 AD F6 AF 3C 5E 0B 86`）が付いていた。provenance を持つアプリがファイルを作る・書き込み用に開くと、カーネルが付ける（[Howard Oakley の解説](https://eclecticlight.co/2025/12/05/quarantine-macl-and-provenance-what-are-they-up-to/)）。付けているのは非公開のカーネル拡張で、いつ付くか（作った時だけか、遅れて付くことがあるか）を書いた資料は見つからなかった。ローカル（APFS）では、作った直後に付いていた。`com.apple.quarantine` は付いていない。
3. **macOS の SMB クライアントは、拡張属性を名前付きストリームとして、パスを指定して書く**。apple-oss-distributions/SMBClient（SMBClient-538.100.12）の `smbfs_vnop_setxattr`（`kernel/smbfs/smbfs_vnops.c`）は、`<ファイル名>:<属性名>` を、普通の属性なら FILE_OVERWRITE_IF（Finder 情報とリソースフォークは FILE_OPEN_IF）で CREATE し、WRITE・SET_INFO（最終書き込み時刻を手元の値に戻す）・CLOSE を1つの要求で送る（`smb2fs_smb_cmpd_create_write_xattr`、`smbfs_smb_2.c`）。rename したあと、手元のファイル（vnode）の名前は新しい名前になるので、あとから出る操作は新しい名前、つまり `<buildId>.ready.json` に向かう。
4. **Windows（NTFS）は、無いファイルの名前付きストリームを OVERWRITE_IF・OPEN_IF で開くと、空の主ストリームを持つファイルごと作る**（[MS-FSA] [2.1.5.1](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fsa/8ada5fbe-db4e-49fd-aef6-20d54b748e40)、[2.1.5.1.1](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fsa/498a5eeb-afc9-445f-99ed-a7e4b73c648b)）。主ストリームは 0 バイトなので、今回の空のファイルと形が合う。
5. **SMB クライアントのほかの仕組みは、消えたファイルを作らない**。handle lease を持つファイルの最後の close を最大 30 秒遅らせる仕組み（deferred close、`smbfs_close`）はあるが、rename の前に閉じる（`CloseDeferredFileRefs(fvp, "vnop_rename")`）。lease break の処理、durable handle の再接続、属性の書き戻し、ディレクトリ lease の処理は、どれも FILE_OPEN か、閉じるだけ。10 秒前後のタイマーは無い。
6. **この Mac に、ファイルを監視するセキュリティソフトは入っていない**。system extension はネットワーク系（VPN、Tailscale など）とカメラだけで、Endpoint Security のものは無い。
7. 同じ症状のネット上の報告は見つからなかった。近いものとして、SMB 上のファイルに provenance が付く例や、SMB での削除に quarantine と provenance が関わりうるという Apple Developer Forums の話（[thread 768884](https://developer.apple.com/forums/thread/768884)）がある。

### Windows の変更ジャーナルで確かめたこと

2026-10-18 03:00 すぎに、管理者で `fsutil usn readjournal C: csv` と `FSCTL_READ_USN_JOURNAL`（100 ns の時刻つき）で読んだ。00:30〜01:30 の記録（56,332 件）が残っていた。見たのは、Unity から送った 11 回（PR #19 の確認の 00:45 の2回と、v0.5.0 の確認の S1〜S5b・S4b）と、Windows で模擬した 6 回（00:36〜00:42）。記録の並べ方と表は Windows の作業用のフォルダーに残してある（`usn-window.csv`、`usn-analysis.csv`、`usn-sequences.txt`、`usn-c44b3bfe.csv`）。

1. **Unity が書くファイルには、作ったときに名前付きストリームが1つ書かれる**。どの回も、`.ready.json.part` と `.vrcw.part` の記録は、作成 → 作成＋名前付きストリームの追加（`0x200100`）→ ＋名前付きストリームへの書き込み（`0x200120`）→ ＋時刻などの変更（`0x208120`）→ 主ストリームへの書き込み → 閉じる、の順で、作成から 5〜15 ms のうちに名前付きストリームが書かれていた。Windows で模擬した回の `.ready.json.part` には無い（Windows で直接書いたため）。
2. **空のファイルは、Bridge が消した 5 ms 後に、名前付きストリームを書く形でできた**。c44b3bfe の回は、rename 29.896、Bridge の削除 29.901（`.vrcw` は 29.904）、そして 29.906〜29.907 に同じ名前で `0x200100` → `0x200120` → `0x208120` → 閉じる（`0x80208120`）。主ストリームへの書き込み（`0x1`・`0x2`）は無い。これは `.part` を作ったときの、主ストリームへの書き込みより前の記録と同じ形で、Mac の SMB クライアントが拡張属性を名前付きストリームとして書く要求（作成・書き込み・時刻の設定・閉じる）が、無いファイルに届いたときの形と合う。ストリームの名前は記録に出ない。
3. **ふだんの回には、rename のあとの書き込みが無い**。ほかの 10 回のどれも、rename から Bridge の削除までの間に、そのファイルへの記録は1件も無かった。`.vrcw` も同じで、c44b3bfe の `.vrcw`（rename から削除まで 77 ms）も作り直されていない。
4. **c44b3bfe の回だけ、Bridge が rename の直後に消していた**。`.ready.json` の rename から Bridge の削除まで:

   | build | rename | 削除 | 間隔 |
   |---|---|---|---|
   | ee70bdf1 | 00:45:01.392 | 00:45:04.540 | 3148 ms |
   | b3eed4d9 | 00:45:38.622 | 00:45:39.707 | 1085 ms |
   | 67097f75（S1） | 01:09:50.378 | 01:09:50.812 | 434 ms |
   | ba3f5064（S2） | 01:11:13.938 | 01:11:16.020 | 2082 ms |
   | 9eb7f0f4（S3） | 01:13:21.221 | 01:13:25.709 | 4488 ms |
   | 625a7034（S3） | 01:14:35.507 | 01:14:38.888 | 3381 ms |
   | 2741d110（S4） | 01:16:30.295 | 01:16:31.725 | 1430 ms |
   | **c44b3bfe（S4）** | **01:17:29.896** | **01:17:29.901** | **5 ms** |
   | a9525483（S5） | 01:20:02.066 | 01:20:04.763 | 2697 ms |
   | 78864c10（S5b） | 01:23:10.333 | 01:23:15.149 | 4816 ms |
   | 5b4e69a5（S4b） | 01:24:37.163 | 01:24:40.283 | 3120 ms |

   ほかの回は 434 ms〜4.8 秒（Windows で模擬した回は 605 ms〜3.7 秒）で、c44b3bfe の 5 ms は2桁短い。Bridge は 5 秒ごとに見るので、rename の直後にスキャンが重なったのはこの1回だけだった。

### 分かったことと、分かっていないこと

空のファイルは、Mac の SMB クライアントが `<buildId>.ready.json` に拡張属性（名前付きストリーム）を書く要求が、Bridge が消した直後に届き、NTFS がファイルごと作ったもの、とみてよい（上の「Mac のソースと資料」の 3・4 と、変更ジャーナルの 2）。Mac のほかのプロセスが O_CREAT で開いたのなら、名前付きストリームを伴わない作成になるので、合わない。Windows のプロセスが作った可能性は、作ったプロセスが記録に出ないので消しきれないが、Mac が `.part` を作るときと同じ形なので考えにくい。

起きる条件は、Bridge が rename から数 ms のうちに manifest を消すこと。ふだんは rename のあとに書き込みが出ないので、Mac は rename の直後に、ファイルへ何かを確かめに来ていて、ファイルが消えていたときだけ書いた、と考えられる。次のどちらかで、記録からは見分けられない。

- Mac（provenance を付けるカーネルの仕組みか SMB クライアント）が rename の直後に拡張属性があるかを確かめ、ふだんはあるので書かずに済み、この回は消えていたので書いた
- Bridge が開いて消したこと（lease の破棄など）に Mac の SMB クライアントが反応して、拡張属性を書き直した

書いたストリームが `com.apple.provenance` かどうかも、記録からは分からない。

## Mac 側で防げるか

Unity のパッケージは変えない。

- 作り直しは、Unity のコードではなく、カーネル（provenance）と SMB クライアントの中で起きている。provenance は SIP で守られた仕組みで、Unity から止める手立てが無い
- `.part` に書いて rename する今の手順、毎回ちがう名前にする、fsync してから rename する、のどれでも、rename の直後に出る書き込みが最後の名前に向かうことは変わらない
- Mac の `nsmb.conf` の `streams=no` は、拡張属性を名前付きストリームの代わりに AppleDouble のファイル（`._<buildId>.ready.json`）に書かせるだけ。これも `*.ready.json` に一致するので、Bridge は新しいビルドとして読み、`results\._<buildId>.json` に `MANIFEST_INVALID` を書く。悪くなる
- SMB クライアントの隠し設定 `file_def_close_off`（deferred close を切る）と `dir_lease_off` は、上の経路に関わらない

Windows の変更ジャーナルでは、作り直しは Bridge が rename の 5 ms 後に消した回だけで起き、0.4 秒以上あとに消したほかの回では起きていない。Bridge の側で、見つけた manifest を少し（例えば1秒）待ってから読んで消すようにすれば、作り直しそのものは起きなくなる見込みがある。ただ、配置のたびにその分遅くなるうえ、作り直されても結果ファイルは変わらなくなった（下の「Bridge 側の修正」）ので、今は入れない。

## Bridge 側の修正

処理済みの buildId の名前で届いたものでは、結果ファイルを書き換えない（仕様書 §24）。

- manifest から buildId が分からない（空、JSON として読めない、buildId が無い・許されない文字、64 KiB を超える）とき、ファイル名から推した buildId が処理済み（`state\archive` にある、または結果ファイルがある）なら、結果を書かず、`state\failed` にも移さずに消す。ログは `[WRN] Discarding ...\state\processing\<buildId>.ready.json (0 bytes): it could not be parsed, and build <buildId> was already processed, so its result is kept.`
- 読める manifest で、buildId が `state\archive` にあるもの（重複）は、これまでどおり共有から消費する。結果ファイルがあれば書き換えず、`[WRN] Build <buildId> was already processed; consumed the duplicate upload and kept its result.` を出す。結果ファイルが無いとき（コールドスタートの起動中に Bridge が止まった、results を掃除した）だけ `BUILD_ALREADY_PROCESSED` を書く。v0.5.0 までは、結果ファイルがあっても `BUILD_ALREADY_PROCESSED` で上書きしていた
- 重複の確認は、buildId を確かめた直後、ほかの確認で結果を書くより前に行う。v0.5.0 までは artifact のファイル名の確認より後だったので、ファイル名の許されない重複でも結果が上書きされた
- 処理済みでない buildId の空・壊れた manifest は、これまでどおり `state\failed` に移して `MANIFEST_INVALID` を書く

0 バイトの manifest を「書き込み途中」とみなして次のスキャンまで待つ案は採らなかった。Unity は rename で置くので、正しい送り手の manifest が最後の名前で 0 バイトに見えることはない。待つようにすると、空のまま残ったファイルが `incoming` に溜まり、送り手の誤りが `MANIFEST_INVALID` ではなく `RESULT_TIMEOUT` になる。今回の空のファイルは、上の処理済みの判定で扱える（Bridge は1件ずつ順に処理するので、遅れて現れたものを読むのは、元のビルドが archive か結果ファイルまで進んだあと）。

## Windows で確かめること

2026-10-18 に行った結果は、下の「Windows での確認」。

### A. Bridge の修正

PR #23 を含むビルド（リリース前なら `docs/setup-windows.md` の手順1-B と手順6）で行う。`$state = "$env:LOCALAPPDATA\VRCRemoteTest\state"`、`<id>` は直前に送ったビルドの buildId。空のファイルや写しは、Bridge のユーザー（U）が PowerShell で置いてよい（Mac の Terminal から `/Volumes/VRCRemoteTest/incoming` に置いても同じ）。

1. Windows で `dotnet test bridge/VRCRemoteTest.Bridge.sln` が通る（シンボリックリンクの3件は従来どおり権限で落ちる）。
2. Reload で Unity から Remote Build & Test を送り、`deployed` になる。
3. その build の空の manifest を置く: `New-Item -ItemType File C:\VRCRemoteTest\incoming\<id>.ready.json`。5 秒以内に、Bridge のログに `[WRN] Discarding ...\state\processing\<id>.ready.json (0 bytes): it could not be parsed, and build <id> was already processed, so its result is kept.` が出て、`[ERR]` は出ない。`results\<id>.json` は `deployed` のまま（中身も更新時刻も変わらない）、`incoming` は空、`$state\failed` に新しいファイルは増えない。
4. 壊れた JSON でも同じ: `Set-Content C:\VRCRemoteTest\incoming\<id>.ready.json '{'`。
5. 読める重複: `Copy-Item "$state\archive\<id>.ready.json" C:\VRCRemoteTest\incoming\`。`[WRN] Build <id> was already processed; consumed the duplicate upload and kept its result.` が出て、結果は `deployed` のまま。
6. 結果の無い重複: `results\<id>.json` を別の場所に移してから、5. と同じく写しを置く。`results\<id>.json` が `BUILD_ALREADY_PROCESSED` で書かれる。終わったら、移した結果ファイルを戻す。
7. 失敗したビルドのあとの空のファイル: ハッシュの合わないビルドを手で置く（`bridge/README.md` の手動確認と同じ要領）。

   ```powershell
   $in = 'C:\VRCRemoteTest\incoming'; $id = 'manual-hash-' + (Get-Date -Format 'HHmmss')
   Set-Content "$in\$id.vrcw" 'dummy' -NoNewline
   $m = @{ protocolVersion = 1; buildId = $id; fileName = "$id.vrcw"; size = 5; sha256 = ('0' * 64); createdAtUtc = (Get-Date).ToUniversalTime().ToString('o') }
   $m | ConvertTo-Json | Set-Content "$in\$id.ready.json.part"; Move-Item "$in\$id.ready.json.part" "$in\$id.ready.json"
   # HASH_MISMATCH になったのを確かめてから
   New-Item -ItemType File "$in\$id.ready.json"
   ```

   結果は `HASH_MISMATCH` のまま、`$state\failed\$id.ready.json` は元の manifest のまま（0 バイトにならない）。
8. 新しい buildId の空の manifest は従来どおり: `New-Item -ItemType File C:\VRCRemoteTest\incoming\manual-empty-1.ready.json` で、`results\manual-empty-1.json` が `MANIFEST_INVALID`、`$state\failed\manual-empty-1.ready.json` ができる。
9. 通しの回帰: Reload 1台・Reload 2台・Relaunch 2台（`multi-client-spike.md` の S1・S3・S4）を各1回送り、すべて `deployed`。
10. ふだんの使い方で、Bridge のログの `Discarding ... (0 bytes)` を数える。出たら、その時刻、直前の配置の時刻とモード、B-1・B-2 の記録を残す。

### B. 原因

1. **NTFS の変更ジャーナル**（2026-10-18 に行った。結果は上の「Windows の変更ジャーナルで確かめたこと」）。記録が残っていれば、空のファイルがいつ、どう作られたかが分かる。古い記録から消えていくので、起きたらすぐ読む。管理者の PowerShell で:

   ```powershell
   fsutil usn readjournal C: csv | Select-String 'c44b3bfe' | Out-File "$env:USERPROFILE\Desktop\usn-c44b3bfe.csv"
   ```

   `incoming` の `20261017T161729471Z-c44b3bfe.ready.json` の記録（`state` の写しとは親ディレクトリが違う）を時刻順に見る。Reason の値は、ファイルの作成 `0x00000100`、主ストリームへの書き込み `0x00000001`/`0x00000002`、名前付きストリームの書き込み `0x00000010`/`0x00000020`、名前付きストリームの追加・削除 `0x00200000`、時刻などの変更 `0x00008000`、削除 `0x00000200`、閉じる `0x80000000` の組み合わせ。
   - 元の manifest の削除のあとに、作成の記録があり、名前付きストリームの追加（`0x00200000`）や書き込み（`0x00000020`）を伴っていて、主ストリームへの書き込み（`0x00000002`）が無ければ、Mac の拡張属性の書き込みで作り直された（2026-10-18 の件はこれだった）
   - 名前付きストリームを伴わない作成だけなら、別のプロセスが作った
   - rename（`0x00002000`）から Bridge の削除までの間隔もあわせて見る。2026-10-18 の件は 5 ms だった
   - 時刻を 100 ns まで見たいときは、`fsutil` の csv ではなく `FSCTL_READ_USN_JOURNAL` で読む
   - 記録が無い（ジャーナルが一巡した）ときは、2. と 3. で捕まえる
2. **送られてくる manifest を、Bridge が消す前に記録する**。Bridge は 5 秒ごとに見るので、0.2 秒ごとに見れば、たいてい先に見られる。管理者でなくてよい（PowerShell 7）。ふだんの manifest にどんなストリームが付いているかも分かる。

   ```powershell
   $dir = 'C:\VRCRemoteTest\incoming'
   $log = "$env:LOCALAPPDATA\VRCRemoteTest\manifest-watch.log"
   $seen = @{}
   while ($true) {
       foreach ($f in Get-ChildItem -LiteralPath $dir -Filter '*.ready.json' -File -ErrorAction SilentlyContinue) {
           $key = '{0}|{1}|{2}' -f $f.Name, $f.Length, $f.CreationTimeUtc.Ticks
           if ($seen.ContainsKey($key)) { continue }
           $seen[$key] = $true
           $streams = @(Get-Item -LiteralPath $f.FullName -Stream * -ErrorAction SilentlyContinue |
               Where-Object Stream -ne ':$DATA' | ForEach-Object {
                   $bytes = Get-Content -LiteralPath $f.FullName -Stream $_.Stream -AsByteStream -TotalCount 64 -ErrorAction SilentlyContinue
                   '{0}({1})={2}' -f $_.Stream, $_.Length, (($bytes | ForEach-Object { $_.ToString('X2') }) -join ' ')
               }) -join '; '
           '{0:HH:mm:ss.fff} {1} len={2} created={3:HH:mm:ss.fff} written={4:HH:mm:ss.fff} streams=[{5}]' -f `
               (Get-Date), $f.Name, $f.Length, $f.CreationTime, $f.LastWriteTime, $streams |
               Tee-Object -FilePath $log -Append
       }
       Start-Sleep -Milliseconds 200
   }
   ```

   空のファイルが捕まれば、ストリームの名前と中身が分かる。`com.apple.provenance(11)=01 02 00 27 AD F6 AF 3C 5E 0B 86`（Mac の Unity と同じ値）なら、書いたのは provenance。作り直しは Bridge が rename の数 ms 後に消したときだけ起きるので、めったに捕まらない。`written` が元の manifest と同じ時刻なら、Mac の SMB クライアントの属性の書き込み（最終書き込み時刻を手元の値に戻す）とも合う。`created` は、NTFS が同じ名前の作り直しで元の作成時刻を引き継ぐことがある（15 秒以内）ので、当てにしない。このスクリプトがファイルを読む瞬間は、Bridge の削除が1回失敗して次のスキャンに回ることがある（結果は変わらない）。
3. **Process Monitor（空のファイルを再現できそうなとき）**。Path に `.ready.json` を含み、Operation が `CreateFile` の条件で記録する。Mac の拡張属性の書き込みなら、Process が `System`（SMB サーバー）、Path が `...\<id>.ready.json:com.apple.provenance` のような名前付きストリーム、Detail の Disposition が `OverwriteIf` か `OpenIf`、その直後に `SetBasicInformationFile`、と並ぶ。
4. （任意）Mac 側で、そのパスに触ったプロセスを見る。ユーザーの Terminal で `sudo fs_usage -w -f filesys | grep -i 'ready.json'` を流しておき、Unity から送る。カーネルの中から出る属性の書き込みは、ここに出ないこともある。

## Windows での確認（2026-10-18 03:05〜03:18）

PR #23 のマージ後の main `870893e` を Windows の dotnet publish（SDK 10.0.400）でビルドし（SHA-256 `0557CD0EC2DBC2DC446EEEFAAE1695BEE04BC5007BC55AA7F65D01F80C3F9CCE`、ProductVersion `1.0.0+870893efe3fae308c27f37a9e2b7dedfc92bcdd4`）、`install-bridge.ps1` で入れた（1回目の置き換えで配置）。v0.5.0 の exe と config.json は `%LOCALAPPDATA%\VRCRemoteTest\pr23-test-backup-20261018-030525\` に退避した。Unity の操作は Mac が、ファイルの置き方とログ・結果ファイルの確認は Windows が行った。時刻は Windows の JST（Unity の時刻は Mac）。

| 確認 | 結果 | 記録 |
|------|------|------|
| A-1. `dotnet test` | 合格 | Windows で 517 件中 514 件成功。落ちたのはシンボリックリンクの3件（特権が無い）だけで、従来どおり。`dotnet list package --vulnerable` で脆弱なパッケージは無し |
| A-2. Reload・1台のコールドスタート | 合格 | build `20261017T180804910Z-37fb939f`。Bridge の取り込み 03:08:07.474、`Launched VRChat client 1/1`（PID 23624）07.528、`deployed` 07.532。台はアカウント#1 で 03:08:17 に入室。Unity は 03:08:23 に `Deployed as vrc-remote-20261017T180804910Z-37fb939f.vrcw` |
| A-3. 処理済みのビルドの空の manifest | 合格 | 03:11:01.717 に置き、02.754 に `[WRN] Discarding ...\state\processing\20261017T180804910Z-37fb939f.ready.json (0 bytes): it could not be parsed, and build 20261017T180804910Z-37fb939f was already processed, so its result is kept.`。`[ERR]` 無し。結果は `deployed` のまま（中身も更新時刻 03:08:07.531 も同じ）。`incoming` は空、`state\failed` は増えていない |
| A-4. 壊れた JSON（`{` と改行、3 バイト） | 合格 | 03:11:03.920 に置き、07.776 に `Discarding ... (3 bytes)`。ほかは A-3 と同じ |
| A-5. 読める重複（archive の写し） | 合格 | 03:11:08.650 に置き、12.802 に `[WRN] Build 20261017T180804910Z-37fb939f was already processed; consumed the duplicate upload and kept its result.`。結果はそのまま、`incoming` は空 |
| A-6. 結果の無い重複 | 合格 | 結果ファイルを退避して 03:11:13.636 に写しを置き、17.821 に `[INF] Build ... was already processed, skipping.`。結果ファイルは `BUILD_ALREADY_PROCESSED` で書かれた。退避した `deployed` の結果ファイルは戻した |
| A-7. 失敗したビルドのあとの空の manifest | 合格 | 手で置いた `manual-hash-031118` は 03:11:22.857 に `HASH_MISMATCH`（`state\failed` の manifest は 254 バイト）。03:11:23.763 に同じ名前の空の manifest を置き、27.869 に `Discarding ... (0 bytes)`。結果は `HASH_MISMATCH` のまま（中身も時刻も同じ）、`state\failed` の manifest も 254 バイトのまま（ハッシュも同じ） |
| A-8. 新しい buildId の空の manifest | 合格（従来どおり） | 03:11:28.782 に `manual-empty-1.ready.json` を置き、32.880 に `[ERR] Failed to parse manifest ...`。結果は `MANIFEST_INVALID`、`state\failed\manual-empty-1.ready.json`（0 バイト）ができた |
| A-9. Reload・1台 | 合格 | build `20261017T181240607Z-3f5ab5f2`。取り込み 03:12:42.999、`Launched 1/1`（PID 32656）43.028、`deployed` 43.030。03:12:51 に入室。Unity は 03:12:48 に `Deployed as ...` |
| A-9. Reload・2台（`VrchatClientCount: 2`） | 合格 | build `20261017T181354407Z-f64fee44`。取り込み 03:13:55.260、`Launched 1/2`（PID 29100）55.310、`2/2`（PID 25160）58.323、`deployed` 58.326。2台ともアカウント#1 で `local:dab8e4…:1` に入り、どちらも `OnPlayerJoined` を2回出した。Unity は 03:14:02 に `Deployed as ...` |
| A-9. Relaunch・2台（`VrchatProfiles: [0, 1]`） | 合格 | build `20261017T181625664Z-aaabeffc`。記録には終わった2台（S4b の PID 34252・12776）が残っていたので、`Closing` は出ずに起動した。取り込み 03:16:28.615、`Launched 1/2`（profile 0、PID 34996）28.727、`New output_log ... (0.8s after ...)` 31.731、`2/2`（profile 1、PID 34960）31.760、`deployed` 31.764。#1 と #2 の台が `local:dab8e4…:1` にそろった。Unity は 03:16:43 に `Deployed as ...` と、想定どおりの `without --watch-worlds` の警告 |

- A-3〜A-8 で、Bridge が置いたファイルに気づくまでは 1.0〜4.2 秒だった（5 秒ごとのスキャン）
- A-2 以降、意図しない `Discarding` は 0 件。Relaunch・2台のあとも 69 秒見て、`Discarding`・`[WRN]`・`[ERR]` は無かった
- 後片付け: VRChat を閉じ、config.json を確認前のもの（Reload、`AutoLaunchVrchat: true`）に戻して、Bridge を 03:17:54 に起動し直した。exe は main `870893e` のビルドのまま。A-7・A-8 で置いたファイルと、c44b3bfe の空のファイルと `MANIFEST_INVALID` の結果ファイルは消した（写しは Windows の作業用のフォルダーにある）
