#Requires -Version 7.0
<#
.SYNOPSIS
    Phase 0.5 検証スパイク用: VRChatを開発/検証用引数で起動する。

.DESCRIPTION
    仕様書 §28 の Launch configuration に基づき、--watch-worlds 付きで
    VRChat.exe を直接起動する（Steam経由のlaunch optionsは使わない）。

    Desktop Test: --watch-worlds --no-vr --enable-debug-gui --enable-sdk-log-levels --enable-udon-debug-logging
    VR Test:      --watch-worlds --enable-debug-gui --enable-sdk-log-levels --enable-udon-debug-logging

    -NoWatchWorlds を指定すると --watch-worlds を外す（Phase 0.5 検証スパイクの
    ネガティブベースラインケース用）。

.PARAMETER Mode
    "Desktop" または "VR"。既定は "Desktop"。差分は --no-vr の有無のみ（仕様書§28）。

.PARAMETER VrChatPath
    VRChat.exe への絶対パス。既定は Steam の標準インストール先。

.PARAMETER NoWatchWorlds
    指定すると --watch-worlds を付けずに起動する（ネガティブベースライン検証用）。

.EXAMPLE
    .\start-vrchat-dev.ps1 -Mode Desktop

.EXAMPLE
    .\start-vrchat-dev.ps1 -Mode Desktop -NoWatchWorlds
#>

[CmdletBinding()]
param(
    [ValidateSet('Desktop', 'VR')]
    [string]$Mode = 'Desktop',

    [string]$VrChatPath = 'C:\Program Files (x86)\Steam\steamapps\common\VRChat\VRChat.exe',

    [switch]$NoWatchWorlds
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $VrChatPath -PathType Leaf)) {
    throw "VRChat.exe が見つかりません: $VrChatPath`n-VrChatPath で正しいパスを指定してください。"
}

$launchArgs = [System.Collections.Generic.List[string]]::new()

if (-not $NoWatchWorlds) {
    $launchArgs.Add('--watch-worlds')
}

if ($Mode -eq 'Desktop') {
    $launchArgs.Add('--no-vr')
}

$launchArgs.Add('--enable-debug-gui')
$launchArgs.Add('--enable-sdk-log-levels')
$launchArgs.Add('--enable-udon-debug-logging')

Write-Host "Starting VRChat ($Mode mode$(if ($NoWatchWorlds) { ', --watch-worlds DISABLED (negative baseline)' }))..." -ForegroundColor Cyan
Write-Host "Path: $VrChatPath"
Write-Host "Args: $($launchArgs -join ' ')"

Start-Process -LiteralPath $VrChatPath -ArgumentList $launchArgs

Write-Host "起動しました。VRChatのホーム画面到達を確認してから deploy-test.ps1 を実行してください。" -ForegroundColor Green
