#Requires -Version 7.0
<#
.SYNOPSIS
    Phase 0.5 検証スパイク用: .vrcw をVRChat Worldsディレクトリへatomicに配置する。

.DESCRIPTION
    Windows Bridge (bridge/src/VRCRemoteTest.Bridge) を経由せず、
    WorldInstaller のatomicデプロイ手順（tempファイル作成 → rename）のみを
    手動で模擬する検証用ヘルパー。Bridgeの正式なファイル名前空間
    (vrc-remote-*.vrcw) とは意図的に分離し、Bridgeの CleanupService が
    誤って検証用ファイルを対象にしないようにしている。

    tempファイルは必ずWorldsディレクトリ内（配置先と同一ボリューム）に
    作成する。異なるボリューム間の File.Move はコピー+削除になり
    atomicにならないため。

.PARAMETER SourcePath
    配置元の .vrcw ファイルへのパス。

.PARAMETER WorldsDirectory
    VRChatのWorldsディレクトリ。既定は仕様書§25のデフォルト候補。

.PARAMETER Cleanup
    指定すると、配置を行わずWorldsディレクトリ内の vrc-test-*.vrcw を
    全て削除して終了する。

.EXAMPLE
    .\deploy-test.ps1 -SourcePath C:\VRCRemoteTest\spike\build1.vrcw

.EXAMPLE
    .\deploy-test.ps1 -Cleanup
#>

[CmdletBinding()]
param(
    [string]$SourcePath,

    [string]$WorldsDirectory = (Join-Path $env:USERPROFILE 'AppData\LocalLow\VRChat\VRChat\Worlds'),

    [switch]$Cleanup
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $WorldsDirectory -PathType Container)) {
    throw "VRChat Worldsディレクトリが見つかりません: $WorldsDirectory`n先にVRChatを一度起動してWorldを読み込ませるか、-WorldsDirectory で正しいパスを指定してください。"
}

if ($Cleanup) {
    $targets = @(
        Get-ChildItem -LiteralPath $WorldsDirectory -Filter 'vrc-test-*.vrcw' -File -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $WorldsDirectory -Filter '.vrc-test-*.vrcw.tmp' -File -Force -ErrorAction SilentlyContinue
    )
    if (-not $targets) {
        Write-Host "削除対象の vrc-test-*.vrcw / .vrc-test-*.vrcw.tmp は見つかりませんでした。" -ForegroundColor Yellow
        return
    }
    foreach ($f in $targets) {
        Remove-Item -LiteralPath $f.FullName -Force
        Write-Host "削除: $($f.Name)"
    }
    Write-Host "クリーンアップ完了（$($targets.Count)件削除）。" -ForegroundColor Green
    return
}

if (-not $SourcePath) {
    throw "-SourcePath を指定してください（または -Cleanup でクリーンアップのみ実行）。"
}

if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
    throw "配置元ファイルが見つかりません: $SourcePath"
}

if ([System.IO.Path]::GetExtension($SourcePath) -ne '.vrcw') {
    throw "配置元ファイルは .vrcw 拡張子である必要があります: $SourcePath"
}

$buildId = Get-Date -Format 'yyyyMMddTHHmmssfff'
$finalName = "vrc-test-$buildId.vrcw"
$tempName = ".vrc-test-$buildId.vrcw.tmp"

# tempファイルは配置先と同一ディレクトリ（＝同一ボリューム）に作成する。
# これによりRename-Itemが真にatomicになることを保証する。
$tempPath = Join-Path $WorldsDirectory $tempName
$finalPath = Join-Path $WorldsDirectory $finalName

Write-Host "Source: $SourcePath"
Write-Host "Temp:   $tempPath"
Write-Host "Final:  $finalPath"

Copy-Item -LiteralPath $SourcePath -Destination $tempPath -Force

# atomic rename
Rename-Item -LiteralPath $tempPath -NewName $finalName

Write-Host "配置完了: $finalName" -ForegroundColor Green
Write-Host "VRChatの画面を確認し、Worldがリロードされるか観察してください。"
