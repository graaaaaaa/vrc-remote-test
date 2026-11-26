# 終わりの目印を待つ（deploy-collect の `--end-marker`）の実機の確かめ

`docs/plans/end-marker-contract.md` §6 の手順と記録です。PASS を記録した commit で、runner の `END_MARKER_VALIDATED` を true にして、`--unvalidated` の要求を外します。

## 前提

- Windows の Bridge が 0.12.0、Relaunch、`VrchatProfiles` は `[0,1]`、`LogMarkerPrefixes` に `[VRCRT]`。統合役と時間を合わせる。Bridge の設定は変えない。
- Mac の試験用の Unity プロジェクト（シーン B、`Assets/Scenes/VrcrtTagScene.unity`）の `VrcrtBuildTagProbe` が、読み込みのときに `[VRCRT] probe loaded vrcrt.build=<tag>` を、その 25 s 後に `[VRCRT] probe vrcrt.end=done` を出す。

## 1. 待ってから集める

```bash
python3 package/Tools~/vrcrt_runner.py run --build headless --unity <Unity> --project <試験用のプロジェクト> --scene Assets/Scenes/VrcrtTagScene.unity --slots 0,1 --unvalidated --end-marker 'vrcrt\.end=done' --end-wait 120
```

期待すること: 終了コード 0、`results.endMarker` PASS、`endMarker.matched` に2台の seq、`evidence/end-marker/slot-N/.../markers.json` にその行、そのあとの証拠の全文ログにもその行。待ちは読み込みから 25 s ほど。

## 2. 出ない目印

同じものを `--end-marker 'vrcrt\.end=never' --end-wait 60` で。期待すること: 終了コード 3、UNKNOWN `endMarkerTimeout`、`missing` に 0 と 1、証拠は集まっている。

## 3. Commander の B に使えるか

Commander の `NoteUplink._Log` は `[VRCRT] SPK010 ...` の形で書くので、`OPS_PROBE step=done` の行は今の Bridge の設定（`LogMarkerPrefixes`: `[VRCRT]`, `[SPK003]`、2026-11-26 に `vrcrt.sh config get` で読んだ）で目印になる。B での確かめは Commander の取り込みの段で行う。

## 記録

### 2026-11-26（UTC）、Bridge 0.12.0+4892f91、runner claude/end-marker（b447219）

Mac のエージェントが CLI で確かめた。Windows で人がしたことは無い。統合役の perf のあいだ（A と B4 のあいだ）に、統合役と合意した時間（17:25〜17:29 JST）に、`VrchatProfiles` を `[0,1]` にして行い、終わったら `[0]` に戻した（統合役の頼み）。Bridge の exe とほかの設定は変えていない。

| 項目 | 結果 |
|---|---|
| 環境 | Bridge `0.12.0+4892f91…`（入れ替えなし）、Relaunch、Desktop、`LogMarkerPrefixes` `[VRCRT]`, `[SPK003]`。Unity 2022.3.22f1（Mac、試験用のプロジェクト、シーン B、headless のビルド）。runner の Python は `/usr/bin/python3` |
| 1. 待ってから集める | **PASS**。run `20261126T082557Z-4920a78d`、終了コード 0、results すべて PASS（`endMarker` PASS）。段 `waitEndMarker` は 08:26:49〜08:27:18 UTC（29 s）。目印は各台の読み込み（17:26:40、17:26:42 JST）の 25 s 後（17:27:05、17:27:07）に出て、slot 0 は seq 938、slot 1 は seq 989 で採った（Bridge の観測と一覧の確かめの分、出てから 4〜11 s あと）。`incompleteRounds` 0。`evidence/end-marker/slot-N/…/markers.json` にその行があり、そのあとに集めた全文ログにも両台ともその行がある。証拠 8 件の sha256 が一致 |
| 2. 出ない目印 | **PASS**（期待どおり UNKNOWN）。run `20261126T082735Z-870251ff`、`--end-marker 'vrcrt\.end=never' --end-wait 60`。終了コード 3、UNKNOWN `endMarkerTimeout`、`missing` [0, 1]、`unconfirmed` []、`lastRoundComplete` true。待ちは 65 s（60 s のあと最後の回まで）。そのあとも証拠を集め、最後の判定も PASS。証拠 6 件の sha256 が一致 |
| 終わったあと | 2台とも joined で同じインスタンス。`VrchatProfiles` を `[0]` に戻した（`config get` で確認） |
| 3. Commander の B | 未確認（取り込みの段で）。`OPS_PROBE` の行は `[VRCRT] SPK010 …` の形なので、今の設定で目印になる |

試験用のプロジェクトの `VrcrtBuildTagProbe` に、25 s 後の終わりの目印を足した（試験用のプロジェクトだけの変更。リポジトリの外）。

## 結論

`--end-marker` は、本物の Bridge 0.12.0 と VRChat 2台（デスクトップ）で、ワールドが出した終わりの目印を各台で待ってから証拠を集め、出ないときは UNKNOWN にしたうえで証拠を集めることを確かめた。これをもって `END_MARKER_VALIDATED` を true にし、`--unvalidated` の要求を外した。VR、4台、Commander の B での確かめはまだ。
