# 配置のあとに現れた空の manifest（開発時の調査記録）

> **対象読者**: このツールのコード自体を開発・改修する人向けの調査記録です。VRC Remote Testを利用するだけであれば読む必要はありません（`README.md`を参照してください）。

**目的**: v0.5.0 のリリース後の通しの確認（`multi-client-spike.md` の「v0.5.0 のリリース後の通しの確認」の気づいたこと1）で見つかった、配置が終わったあとに同じ名前の空の manifest が `incoming` に現れ、Bridge が成功の結果ファイルを `MANIFEST_INVALID` で上書きした件について、原因を調べ、Bridge 側で防ぐ。

---

## 起きたこと

2026-10-18 01:17、Relaunch・2台の2回目（build `20261017T161729471Z-c44b3bfe`）。時刻は Windows の JST。

| 時刻 | できごと |
|------|----------|
| 01:17:29.471 | Unity が buildId を作った（buildId の時刻。Mac の時計）。このあと `.vrcw` と manifest を共有に書く |
| 29.897 | Bridge が manifest を取り込んだ（`state\archive` の manifest の最終書き込み時刻。下の注） |
| 29.922 | `Closing 2 VRChat client(s) ...` |
| 34.489 | `deployed` の結果ファイルを書いた。Unity はそれを読んで `[Succeeded]` |
| 39.493 | Bridge が次のスキャンで、中身が空（0 バイト）の `incoming\<buildId>.ready.json` を取り込んだ（`state\failed` の空のファイルの時刻） |
| 39.508 | `JsonException: The input does not contain any JSON tokens` で空のファイルを `state\failed` に移し、結果ファイルを `{"status":"failed","errorCode":"MANIFEST_INVALID","errorMessage":"Manifest could not be parsed."}` で上書きした |

正しい manifest（274 バイト）は `state\archive` にあり、配置したワールドと起動し直した2台はそのままだった。Unity は先に `deployed` を読んでいたので、Unity からは気づけない。その日のほかの `ready.json`（v0.5.0 の確認だけで 8 件。Reload・Relaunch、成功・失敗の両方）では起きていない。Relaunch の2回目のあと約 50 秒見ていた S4b でも現れなかった。

Unity のパッケージ（`SmbRemoteTransport.UploadBuildAsync` → `AtomicFile.WriteAllText`）は、`<buildId>.ready.json.part` に `File.WriteAllText` で書き、`File.Exists` → `File.Move` で1回 rename するだけで、そのあと同じ名前には触らない。

**時刻の注**: Bridge は共有の manifest を移さず、バイトを読んで `state\processing` に新しいファイルとして書く（その後 archive か failed に移す）。なので `state` にある manifest の最終書き込み時刻は、Mac が書いた時刻ではなく、Bridge が取り込んだ時刻になる。`multi-client-spike.md` の「v0.5.0 のリリース後の通しの確認」の「manifest が届いた」も、どの回も Bridge の最初のログの 18〜83 ms 前で、5 秒ごとのスキャンなのにばらついていないので、取り込んだ時刻とみられる。ここから2つのことが言える。

- 空のファイルが現れた時刻は分からない。分かるのは、Bridge が元の manifest を消した 29.9 秒台から、次のスキャンの 39.49 秒までの間ということだけ。Bridge はビルドを1件ずつ処理し、`deployed` を書いた 34.489 の 5 秒後に次のスキャンをしたので、「約 10 秒後」はスキャンの間隔から来ている
- この回は、Unity が buildId を作ってから Bridge が取り込むまでが 0.43 秒で、その日のほかの 8 件（0.84〜5.2 秒）より短かった（Mac と Windows の時計のずれは確かめていない）。Bridge は取り込んだ直後に共有の manifest を消すので、この回は Mac が rename してから1秒もたたないうちに消したことになる

## 原因の調べ（2026-10-18）

Mac のソース（macOS の SMB クライアント、Unity の Mono）と資料を読み、この Mac のローカルのファイルを調べた。共有（`/Volumes/VRCRemoteTest`）は、調査に使ったシェルからは macOS のネットワークボリュームの権限で読めず、Windows 側も触れないので、共有での再現はしていない。

### 確かめたこと

1. **Unity（Mono）は rename のあと何もしない**。Unity-Technologies/mono（unity-2022.3-mbe）の corefx で、`File.WriteAllText` は `open(O_WRONLY|O_CREAT|O_TRUNC)` → write → close（fsync なし）。`File.Move` は `FileSystem.Unix.cs` の `MoveFile` で、lstat（元）→ lstat（先）→ `rename(2)`。`File.Exists` は stat、`File.Delete` は unlink。close や rename のあとに、拡張属性や時刻を書くコードは無い。
2. **Unity が書くファイルには `com.apple.provenance` が付く**。この Mac の Unity（2022.3.22f1）が書いた `~/Library/Logs/Unity/Editor.log` やプロジェクトの `Library` のファイルに、`Unity.app` と同じ値の `com.apple.provenance`（11 バイト、`01 02 00 27 AD F6 AF 3C 5E 0B 86`）が付いていた。provenance を持つアプリがファイルを作る・書き込み用に開くと、カーネルが付ける（[Howard Oakley の解説](https://eclecticlight.co/2025/12/05/quarantine-macl-and-provenance-what-are-they-up-to/)）。付けているのは非公開のカーネル拡張で、いつ付くか（作った時だけか、遅れて付くことがあるか）を書いた資料は見つからなかった。ローカル（APFS）では、作った直後に付いていた。`com.apple.quarantine` は付いていない。
3. **macOS の SMB クライアントは、拡張属性を名前付きストリームとして、パスを指定して書く**。apple-oss-distributions/SMBClient（SMBClient-538.100.12）の `smbfs_vnop_setxattr`（`kernel/smbfs/smbfs_vnops.c`）は、`<ファイル名>:<属性名>` を、普通の属性なら FILE_OVERWRITE_IF（Finder 情報とリソースフォークは FILE_OPEN_IF）で CREATE し、WRITE・SET_INFO（最終書き込み時刻を手元の値に戻す）・CLOSE を1つの要求で送る（`smb2fs_smb_cmpd_create_write_xattr`、`smbfs_smb_2.c`）。rename したあと、手元のファイル（vnode）の名前は新しい名前になるので、あとから出る操作は新しい名前、つまり `<buildId>.ready.json` に向かう。
4. **Windows（NTFS）は、無いファイルの名前付きストリームを OVERWRITE_IF・OPEN_IF で開くと、空の主ストリームを持つファイルごと作る**（[MS-FSA] [2.1.5.1](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fsa/8ada5fbe-db4e-49fd-aef6-20d54b748e40)、[2.1.5.1.1](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fsa/498a5eeb-afc9-445f-99ed-a7e4b73c648b)）。主ストリームは 0 バイトなので、今回の空のファイルと形が合う。
5. **SMB クライアントのほかの仕組みは、消えたファイルを作らない**。handle lease を持つファイルの最後の close を最大 30 秒遅らせる仕組み（deferred close、`smbfs_close`）はあるが、rename の前に閉じる（`CloseDeferredFileRefs(fvp, "vnop_rename")`）。lease break の処理、durable handle の再接続、属性の書き戻し、ディレクトリ lease の処理は、どれも FILE_OPEN か、閉じるだけ。10 秒前後のタイマーは無い。
6. **この Mac に、ファイルを監視するセキュリティソフトは入っていない**。system extension はネットワーク系（VPN、Tailscale など）とカメラだけで、Endpoint Security のものは無い。
7. 同じ症状のネット上の報告は見つからなかった。近いものとして、SMB 上のファイルに provenance が付く例や、SMB での削除に quarantine と provenance が関わりうるという Apple Developer Forums の話（[thread 768884](https://developer.apple.com/forums/thread/768884)）がある。

### 見立て

確からしい順に並べる。

1. **Mac から、`<buildId>.ready.json` への拡張属性（`com.apple.provenance` など）の書き込みが、Bridge が消したあとに届き、その CREATE がファイルを作り直した**。上の 2〜4 で、空のファイルができる経路がそろう。上の「時刻の注」のとおり、この回だけ Bridge が rename から1秒もたたないうちに manifest を消していて、ふだんは Bridge が読む前に終わっている Mac の書き込みと、この回だけぶつかった、と考えると1回だけ起きたことも説明できる。分からないのは、その書き込みを出したのが何か（provenance を付けるカーネル拡張か、ほかのものか）と、rename のあとに出る理由。
2. **Mac の別のプロセスが、その名前を O_CREAT で開いた**。Unity のコードには無く、セキュリティソフトも入っていないので、考えにくい。
3. **Windows の別のプロセスが作った**。Bridge は `incoming` にファイルを作らない。Windows のウイルス対策や同期ソフトが作ったかどうかは確かめていない。

見分けるには、空のファイルに名前付きストリーム（`com.apple.provenance` など）があるか、作られた時刻はいつか、作ったのが SMB 経由（System）かローカルのプロセスかを Windows で見る（下の「Windows で確かめること」の B）。

## Mac 側で防げるか

Unity のパッケージは変えない。

- 作り直しは、Unity のコードではなく、カーネル（provenance）と SMB クライアントの中で起きているとみられる。provenance は SIP で守られた仕組みで、Unity から止める手立てが無い
- `.part` に書いて rename する今の手順、毎回ちがう名前にする、fsync してから rename する、のどれでも、あとから出る書き込みが最後の名前に向かうことは変わらない
- Mac の `nsmb.conf` の `streams=no` は、拡張属性を名前付きストリームの代わりに AppleDouble のファイル（`._<buildId>.ready.json`）に書かせるだけ。これも `*.ready.json` に一致するので、Bridge は新しいビルドとして読み、`results\._<buildId>.json` に `MANIFEST_INVALID` を書く。悪くなる
- SMB クライアントの隠し設定 `file_def_close_off`（deferred close を切る）と `dir_lease_off` は、上の経路に関わらない

Windows で見立て1が確かめられ、どの属性かが分かったら、改めて考える。

## Bridge 側の修正

処理済みの buildId の名前で届いたものでは、結果ファイルを書き換えない（仕様書 §24）。

- manifest から buildId が分からない（空、JSON として読めない、buildId が無い・許されない文字、64 KiB を超える）とき、ファイル名から推した buildId が処理済み（`state\archive` にある、または結果ファイルがある）なら、結果を書かず、`state\failed` にも移さずに消す。ログは `[WRN] Discarding ...\state\processing\<buildId>.ready.json (0 bytes): it could not be parsed, and build <buildId> was already processed, so its result is kept.`
- 読める manifest で、buildId が `state\archive` にあるもの（重複）は、これまでどおり共有から消費する。結果ファイルがあれば書き換えず、`[WRN] Build <buildId> was already processed; consumed the duplicate upload and kept its result.` を出す。結果ファイルが無いとき（コールドスタートの起動中に Bridge が止まった、results を掃除した）だけ `BUILD_ALREADY_PROCESSED` を書く。v0.5.0 までは、結果ファイルがあっても `BUILD_ALREADY_PROCESSED` で上書きしていた
- 重複の確認は、buildId を確かめた直後、ほかの確認で結果を書くより前に行う。v0.5.0 までは artifact のファイル名の確認より後だったので、ファイル名の許されない重複でも結果が上書きされた
- 処理済みでない buildId の空・壊れた manifest は、これまでどおり `state\failed` に移して `MANIFEST_INVALID` を書く

0 バイトの manifest を「書き込み途中」とみなして次のスキャンまで待つ案は採らなかった。Unity は rename で置くので、正しい送り手の manifest が最後の名前で 0 バイトに見えることはない。待つようにすると、空のまま残ったファイルが `incoming` に溜まり、送り手の誤りが `MANIFEST_INVALID` ではなく `RESULT_TIMEOUT` になる。今回の空のファイルは、上の処理済みの判定で扱える（Bridge は1件ずつ順に処理するので、遅れて現れたものを読むのは、元のビルドが archive か結果ファイルまで進んだあと）。

## Windows で確かめること

### A. Bridge の修正

このブランチのビルド（`docs/setup-windows.md` の手順1-B と手順6）で行う。`$state = "$env:LOCALAPPDATA\VRCRemoteTest\state"`、`<id>` は直前に送ったビルドの buildId。空のファイルや写しは、Bridge のユーザー（U）が PowerShell で置いてよい（Mac の Terminal から `/Volumes/VRCRemoteTest/incoming` に置いても同じ）。

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
10. ふだんの使い方で、Bridge のログの `Discarding ... (0 bytes)` を数える。出たら、その時刻、直前の配置の時刻とモード、下の B-2 の記録を残す。

### B. 原因（見立て1か）

1. **NTFS の変更ジャーナル（すぐできる）**。2026-10-18 01:17 の記録がまだ残っていれば、空のファイルがいつ、どう作られたかが分かる。管理者の PowerShell で:

   ```powershell
   fsutil usn readjournal C: csv | Select-String 'c44b3bfe' | Out-File "$env:USERPROFILE\Desktop\usn-c44b3bfe.csv"
   ```

   `incoming` の `20261017T161729471Z-c44b3bfe.ready.json` の記録（`state` の写しとは親ディレクトリが違う）を時刻順に見る。Reason の値は、ファイルの作成 `0x00000100`、主ストリームへの書き込み `0x00000001`/`0x00000002`、名前付きストリームの書き込み `0x00000010`/`0x00000020`、名前付きストリームの追加・削除 `0x00200000`、時刻などの変更 `0x00008000`、削除 `0x00000200`、閉じる `0x80000000` の組み合わせ。
   - 元の manifest の削除（Bridge、29.9 秒台）のあとに、作成の記録があり、名前付きストリームの追加（`0x00200000`）や書き込み（`0x00000020`）を伴っていて、主ストリームへの書き込み（`0x00000002`）が無ければ、見立て1
   - 名前付きストリームを伴わない作成だけなら、見立て2か3
   - あわせて、ふだんの manifest（`.ready.json.part` として作られたもの）に名前付きストリームの追加が付いているかを見る。付いていれば、Mac が書くたびに `com.apple.provenance` などのストリームを作っている
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

   空のファイルが捕まり、`com.apple.provenance(11)=01 02 00 27 AD F6 AF 3C 5E 0B 86`（Mac の Unity と同じ値）があれば、見立て1でほぼ決まり。`written` が元の manifest と同じ時刻なら、Mac の SMB クライアントの属性の書き込み（最終書き込み時刻を手元の値に戻す）とも合う。`created` は、NTFS が同じ名前の作り直しで元の作成時刻を引き継ぐことがある（15 秒以内）ので、当てにしない。このスクリプトがファイルを読む瞬間は、Bridge の削除が1回失敗して次のスキャンに回ることがある（結果は変わらない）。
3. **Process Monitor（空のファイルを再現できそうなとき）**。Path に `.ready.json` を含み、Operation が `CreateFile` の条件で記録する。見立て1なら、Process が `System`（SMB サーバー）、Path が `...\<id>.ready.json:com.apple.provenance` のような名前付きストリーム、Detail の Disposition が `OverwriteIf` か `OpenIf`、その直後に `SetBasicInformationFile`、と並ぶ。
4. （任意）Mac 側で、そのパスに触ったプロセスを見る。ユーザーの Terminal で `sudo fs_usage -w -f filesys | grep -i 'ready.json'` を流しておき、Unity から送る。カーネルの中から出る属性の書き込みは、ここに出ないこともある。
