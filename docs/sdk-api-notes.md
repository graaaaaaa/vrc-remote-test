# VRChat SDK Public API 調査ノート

> **対象読者**: このツールのコード自体を開発・改修する人向け。VRC Remote Testを利用するだけであれば読む必要はありません（`README.md`を参照してください）。

**調査日**: 2026-09-01
**調査対象**: VRChat Creator Companion (VCC) キャッシュ済みSDKパッケージ
**方針**: Web上の古い記事は参照せず、実際にインストール/キャッシュされたSDKソースを正とする（仕様書 §9）

## 調査に使用したソース

1. **Worlds SDK 3.7.5** (VCCキャッシュ):
   `~/.local/share/VRChatCreatorCompanion/Repos/com.vrchat.worlds/vrc-get-com.vrchat.worlds-3.7.5.zip`
2. **Base SDK 3.10.4** (既存Avatarsプロジェクトにインストール済み):
   `~/Documents/VRChatProjects/Avatars/Packages/com.vrchat.base/`

VCCリポジトリメタデータ (`vrc-official.json`) 上で確認できる最新安定版は **Worlds SDK 3.10.4** (2026-09-01時点)。キャッシュされている3.7.5から3.10.4まで、Public API (`IVRCSdkWorldBuilderApi`, `IVRCSdkBuilderApi`) のシグネチャは同一アセンブリ構造を維持している。

## `IVRCSdkWorldBuilderApi`

**namespace**: `VRC.SDK3.Editor`
**ファイル**: `Editor/VRCSDK/SDK3/Public SDK API/IVRCSdkWorldBuilderApi.cs`
**継承**: `IVRCSdkBuilderApi`

```csharp
public interface IVRCSdkWorldBuilderApi : IVRCSdkBuilderApi
{
    Task<string> Build();

    Task BuildAndUpload(VRCWorld world, string thumbnailPath = null,
        CancellationToken cancellationToken = default);

    Task BuildAndTest();

    Task TestLastBuild();

    Task UploadLastBuild(VRCWorld world, string thumbnailPath = null,
        CancellationToken cancellationToken = default);
}
```

### 重要な発見

- **`Build()` は `Task<string>` を返し、文字列はビルド済み `.vrcw` バンドルへのパスそのもの**。これにより本ツールのArtifact Resolver（ファイルシステムスキャンによる特定）は不要になり、フォールバック手段としてのみ検討すればよい。
- `Build()` は「現在開いているシーン」に対して動作する。シーンを指定するパラメータはない。有効な`SceneDescriptor`と`PipelineManager`コンポーネントが必要。
- `BuildAndTest()` は**Macでの実行を想定していない**（macOS上でVRChat Clientを起動する設計にはできないため、仕様書 §10の方針通り`Build()`のみを使用する）。
- 例外: `BuilderException`（ビルドプロセスのエラー全般）, `BuildBlockedException`（SDK Callbackによるブロック）, `ValidationException`（コンテンツ検証エラー、`List<string> Errors`プロパティを持つ）。

## `IVRCSdkBuilderApi`（Base SDK、共通基底interface）

**namespace**: `VRC.SDKBase.Editor`
**ファイル**: `Editor/VRCSDK/Dependencies/VRChat/Public SDK API/IVRCSdkBuilderApi.cs`

```csharp
public interface IVRCSdkBuilderApi : IVRCSdkControlPanelBuilder
{
    event EventHandler<object> OnSdkBuildStart;
    event EventHandler<string> OnSdkBuildProgress;
    event EventHandler<string> OnSdkBuildFinish;
    event EventHandler<string> OnSdkBuildSuccess;   // 引数はバンドルパス
    event EventHandler<string> OnSdkBuildError;      // 引数はエラーメッセージ

    event EventHandler<SdkBuildState> OnSdkBuildStateChange;
    SdkBuildState BuildState { get; }

    event EventHandler OnSdkUploadStart;
    event EventHandler<(string status, float percentage)> OnSdkUploadProgress;
    event EventHandler<string> OnSdkUploadFinish;
    event EventHandler<string> OnSdkUploadSuccess;
    event EventHandler<string> OnSdkUploadError;

    event EventHandler<SdkUploadState> OnSdkUploadStateChange;
    SdkUploadState UploadState { get; }

    void CancelUpload();
}

public enum SdkBuildState { Idle, Building, Success, Failure }
public enum SdkUploadState { Idle, Uploading, Success, Failure }
```

`OnSdkBuildSuccess`イベントの引数(`string`)も、`Build()`の戻り値と同じくバンドルパスである。

## ビルダー取得方法

`TryGetBuilder<T>`は`IVRCSdkPanelApi`インターフェースにコメントとして記載されているのみ（C# 7.3の制約でinterfaceにstaticメンバーを定義できないため）。実際の実装は`VRCSdkControlPanel`クラスの静的メソッドとして存在する。

**ファイル**: `Editor/VRCSDK/Dependencies/VRChat/ControlPanel/VRCSdkControlPanelBuilder.cs`

```csharp
public static bool TryGetBuilder<T>(out T builder) where T : IVRCSdkBuilderApi
```

**重要な制約**: このメソッドは`VRCSdkControlPanel.window`が`null`でないことを要求する。つまり **SDK Control Panelウィンドウが開いている必要がある**。`window`は`VRCSdkControlPanel`のコンストラクタで`public static VRCSdkControlPanel window`にセットされる。

使用例:
```csharp
if (VRCSdkControlPanel.TryGetBuilder<IVRCSdkWorldBuilderApi>(out var worldBuilder))
{
    string bundlePath = await worldBuilder.Build();
}
```

`VrcSdkBuildAdapter`実装では、`TryGetBuilder`が失敗した場合に`EditorWindow.GetWindow(typeof(VRCSdkControlPanel))`でパネルを自動的に開いてから再試行する必要がある。

## 例外型一覧

**ファイル**: `Editor/VRCSDK/Dependencies/VRChat/VRCSdkBuilderExceptions.cs`
**namespace**: `VRC.SDKBase.Editor`

| 例外 | 意味 |
|------|------|
| `BuilderException` | SDK内部ビルダーエラー |
| `BuildBlockedException` | SDK Callbackによりビルドがブロックされた |
| `ValidationException` | コンテンツにバリデーションエラーあり (`List<string> Errors`) |
| `OwnershipException` | 現在のユーザーが対象コンテンツを所有していない（アップロード時のみ関係） |
| `UploadException` | アップロード処理のエラー（本ツールでは使用しない） |
| `BundleExistsException` | バンドルが既にアップロード済み |

## Assembly構成

### Base SDK — `VRC.SDKBase.Editor.asmdef`
```json
{
    "name": "VRC.SDKBase.Editor",
    "references": [
        "VRC.SDKBase", "VRC.Enums.Validation.Performance", "UniTask",
        "Unity.Postprocessing.Runtime", "Unity.XR.Management",
        "Unity.XR.Management.Editor", "Unity.XR.Oculus"
    ],
    "includePlatforms": ["Editor"]
}
```

### Worlds SDK — `VRC.SDK3.Editor.asmdef`
```json
{
    "name": "VRC.SDK3.Editor",
    "references": [
        "VRC.SDK3", "VRC.SDKBase.Editor", "VRC.Udon", "VRC.SDKBase",
        "UniTask", "Unity.TextMeshPro", "Unity.Postprocessing.Runtime",
        "Unity.TextMeshPro.Editor"
    ],
    "includePlatforms": ["Editor"],
    "defineConstraints": ["UDON"]
}
```

**本パッケージ (`VRCRemoteTest.Editor.asmdef`) が参照すべきアセンブリは `VRC.SDKBase.Editor` と `VRC.SDK3.Editor` のみ**。`VRC.Udon`, `VRC.SDK3`, ランタイム系アセンブリへの直接参照は不要（`IVRCSdkWorldBuilderApi`と`VRCSdkControlPanel`はいずれもEditor層のAPIであり、Udonランタイムには依存しない）。

## VPMパッケージ依存関係

`com.vrchat.worlds` の `package.json` (3.7.5時点):

```json
{
  "vpmDependencies": {
    "com.vrchat.base": "3.7.5"
  }
}
```

本ツールの `vpmDependencies` は `com.vrchat.worlds` に対して `>=3.7.5` とする。3.7.5から確認したPublic API（`Build()`が`Task<string>`を返す等）は3.10.4時点でも変更されていない（アセンブリ構造・パッケージメタデータの一貫性から確認）。上限バージョンは指定しない。将来のメジャーバージョンアップでPublic APIに破壊的変更が入った場合は、このドキュメントを更新した上で`VrcSdkBuildAdapter`のみを変更して対応する（仕様書 §8の方針）。

## 未検証事項

- **Win64 ビルドターゲット**: 2026-10-25 の Unity 2022.3.22f1 / Worlds SDK 3.10.5 バッチ試験では、ターゲット未指定だとアダプターが `INVALID_BUILD_TARGET` を返した。`-buildTarget Win64` を指定した試験は成功した。アダプターは Unity のターゲットを途中で切り替えず、事前の一致を検査する。
- **`TryGetBuilder` の初期化**: 同日の実機試験で、SDK パネルを開いても Builder のシーン一覧が未初期化のまま返る場合があった。`IsValidBuilder` で一覧を更新し、リトライは Unity の同期コンテキスト上で続ける。バッチ試験ではメインスレッド外のシーン検索が失敗することを確認し、修正後のビルドは成功した。詳細は `docs/validation/lan-direct-2026-10-25.md`。
- **VRChatの`--watch-worlds`が実際にファイル配置でリロードするか**はSDK APIの範囲外であり、Windows実機での検証が必要（`docs/validation/watch-worlds-spike.md`で別途記録、Phase 0.5参照）。
