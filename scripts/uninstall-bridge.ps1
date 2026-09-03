#Requires -Version 7.0
<#
.SYNOPSIS
    install-bridge.ps1 で登録した Task Scheduler タスクを解除する（仕様書§21）。

.DESCRIPTION
    タスクが本スクリプト群の管理下にあると判別できた場合のみ削除する。
    無関係な同名タスクは誤って削除しない。

    タスクが登録されていない状態（一度もinstall-bridge.ps1を実行して
    いない、または既に削除済み）で実行しても正常終了する（冪等）。

.PARAMETER ExePath
    -StopRunning 使用時、タスクからアクションのパスが取得できなかった
    場合のフォールバック。既定は install-bridge.ps1 と同じ配置先。

.PARAMETER TaskName
    削除するタスク名。既定は "VRCRemoteTestBridge"。

.PARAMETER StopRunning
    指定すると、稼働中の Bridge プロセス（実行体のフルパス一致）も停止する。

.EXAMPLE
    .\uninstall-bridge.ps1

.EXAMPLE
    .\uninstall-bridge.ps1 -StopRunning
#>

[CmdletBinding()]
param(
    [string]$ExePath = (Join-Path $env:LOCALAPPDATA 'Programs\VRCRemoteTest\Bridge\VRCRemoteTest.Bridge.exe'),

    [string]$TaskName = 'VRCRemoteTestBridge',

    [switch]$StopRunning
)

$ErrorActionPreference = 'Stop'

$ExpectedExeName = 'VRCRemoteTest.Bridge.exe'
$TaskPathRoot = '\'
$OwnershipDescription = 'VRC Remote Test Bridge auto-start (managed by vrc-remote-test install-bridge.ps1)'

# -StopRunning 用の停止対象パス。タスクのアクションから取得できればそちらを
# 優先する（-ExePath と異なるカスタム配置で登録されていた場合でも取りこぼさないため）。
$stopExePath = $ExePath

$existingTask = Get-ScheduledTask -TaskName $TaskName -TaskPath $TaskPathRoot -ErrorAction SilentlyContinue

if (-not $existingTask) {
    Write-Warning "タスク '$TaskName' は登録されていません（インストール済みでないか、既に削除済みです）。"
}
else {
    $isOwned = $false
    try {
        if ($existingTask.Description -like "$OwnershipDescription*") {
            $actions = @($existingTask.Actions)
            if ($actions.Count -eq 1 -and $actions[0].Execute) {
                $actionLeaf = Split-Path -Path $actions[0].Execute -Leaf
                if ($actionLeaf -eq $ExpectedExeName -and [string]::IsNullOrEmpty($actions[0].Arguments)) {
                    $isOwned = $true
                    $stopExePath = $actions[0].Execute
                }
            }
        }
    }
    catch {
        $isOwned = $false
    }

    if (-not $isOwned) {
        throw "タスク '$TaskName' は存在しますが、本スクリプトが管理するタスクとは判別できませんでした。`nGet-ScheduledTask -TaskName '$TaskName' -TaskPath '$TaskPathRoot' で確認の上、手動で削除してください。"
    }

    Unregister-ScheduledTask -TaskName $TaskName -TaskPath $TaskPathRoot -Confirm:$false
    Write-Host "タスク '$TaskName' を削除しました。" -ForegroundColor Green
}

if ($StopRunning) {
    if (-not (Test-Path -LiteralPath $stopExePath -PathType Leaf)) {
        Write-Warning "停止対象の実行体が見つかりません（既に削除済みの可能性）: $stopExePath"
    }
    else {
        $resolvedStopExePath = (Resolve-Path -LiteralPath $stopExePath).Path
        $processName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedStopExePath)

        $candidates = Get-Process -Name $processName -ErrorAction SilentlyContinue
        $running = $candidates | Where-Object {
            try { $_.Path -eq $resolvedStopExePath } catch { $false }
        }

        if (-not $running) {
            Write-Host "稼働中の $resolvedStopExePath は見つかりませんでした。" -ForegroundColor Yellow
        }
        else {
            foreach ($p in $running) {
                Write-Host "停止: PID $($p.Id) ($resolvedStopExePath)"
                Stop-Process -Id $p.Id -Force
            }
            Write-Host "停止しました。" -ForegroundColor Green
        }
    }
}
