# VRChat SDK Public API 調査ノート

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

- **`Build()`実行中にUnityのビルドターゲットが`StandaloneWindows64`でない場合の挙動**: SDK側で自動的にターゲット切替を行うか、それとも呼び出し側が事前に切り替えておく必要があるかは未確認。安全側に倒し、`VrcSdkBuildAdapter`側で明示的に`EditorUserBuildSettings.activeBuildTarget`を確認・切替する設計とする。
- **`TryGetBuilder`がSDKパネルを開いた直後、非同期的なタイミングでどの程度安定して成功するか**は未検証。Codexレビューでも指摘されたため、リトライ・バックオフの実装を検討する。
- **VRChatの`--watch-worlds`が実際にファイル配置でリロードするか**はSDK APIの範囲外であり、Windows実機での検証が必要（`docs/validation/watch-worlds-spike.md`で別途記録、Phase 0.5参照）。
