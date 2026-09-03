#Requires -Version 7.0
<#
.SYNOPSIS
    Bridge (VRCRemoteTest.Bridge.exe) を Windows Task Scheduler に登録し、
    ログオン時に自動起動させる（仕様書§21）。

.DESCRIPTION
    Trigger "At log on" / Principal "Interactive, Limited"（非elevated）で
    タスクを登録する。BridgeはWindows Serviceにしない設計（仕様書§20）の
    ため、対話ユーザーセッション上でのみ動作させる必要がある。

    既定の配置先はSMB共有（StagingDirectory）の外側にある
    %LOCALAPPDATA%\Programs\VRCRemoteTest\Bridge\ 。SMB共有内に実行体を
    置くと、共有への書き込み権限を持つ者が実行体を差し替えることで
    ログオン時に自動実行されるコードを乗っ取れてしまうため
    （Codex計画レビューで検出）、意図的にSMB共有の外に配置する。

    -SourcePath を指定すると、SMB共有内の一時drop位置（例:
    C:\VRCRemoteTest\_bridge-drop\VRCRemoteTest.Bridge.exe）から
    -ExePath へ複写してから登録する。未指定時は -ExePath に実行体が
    既に配置済みという前提で動作する。

    再実行は冪等: 同名タスクが既に存在する場合、本スクリプトが
    登録したものと判別できたときのみ削除・再登録する。判別できない
    場合は無関係なタスクを壊さないよう処理を中断する。

.PARAMETER ExePath
    Bridge実行体の配置先（登録するタスクのアクションが指すパス）。
    既定は %LOCALAPPDATA%\Programs\VRCRemoteTest\Bridge\VRCRemoteTest.Bridge.exe 。

.PARAMETER SourcePath
    複写元の実行体パス（省略可）。指定すると -ExePath へ複写してから登録する。

.PARAMETER TaskName
    登録するタスク名。既定は "VRCRemoteTestBridge"。

.PARAMETER StartNow
    指定すると、登録直後にタスクを起動する。同じ実行体が既に稼働中の
    場合は二重起動を避けるためスキップする。

.EXAMPLE
    .\install-bridge.ps1

.EXAMPLE
    .\install-bridge.ps1 -SourcePath C:\VRCRemoteTest\_bridge-drop\VRCRemoteTest.Bridge.exe -StartNow
#>

[CmdletBinding()]
param(
    [string]$ExePath = (Join-Path $env:LOCALAPPDATA 'Programs\VRCRemoteTest\Bridge\VRCRemoteTest.Bridge.exe'),

    [string]$SourcePath,

    [string]$TaskName = 'VRCRemoteTestBridge',

    [switch]$StartNow
)

$ErrorActionPreference = 'Stop'

$ExpectedExeName = 'VRCRemoteTest.Bridge.exe'
$TaskPathRoot = '\'
$OwnershipDescription = 'VRC Remote Test Bridge auto-start (managed by vrc-remote-test install-bridge.ps1)'

function Assert-ExeLeafName {
    param([string]$Path, [string]$ParamName)

    $leaf = Split-Path -Path $Path -Leaf
    if ($leaf -ne $ExpectedExeName) {
        throw "$ParamName のファイル名は $ExpectedExeName である必要があります: $Path"
    }
}

Assert-ExeLeafName -Path $ExePath -ParamName '-ExePath'

if ($SourcePath) {
    Assert-ExeLeafName -Path $SourcePath -ParamName '-SourcePath'

    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        throw "-SourcePath で指定されたファイルが見つかりません: $SourcePath"
    }

    $destDir = Split-Path -Path $ExePath -Parent
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null

    Write-Host "Copying: $SourcePath -> $ExePath" -ForegroundColor Cyan
    Copy-Item -LiteralPath $SourcePath -Destination $ExePath -Force

    if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
        throw "複写後もファイルが見つかりません: $ExePath"
    }
}

if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw "Bridge実行体が見つかりません: $ExePath`n先に dotnet publish で発行してから -SourcePath で複写元を指定するか、-ExePath で既存の配置先を指定してください。"
}

$resolvedExePath = (Resolve-Path -LiteralPath $ExePath).Path
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name

Write-Host "Exe:      $resolvedExePath"
Write-Host "Identity: $identity"
Write-Host "TaskName: $TaskName"

$existingTask = Get-ScheduledTask -TaskName $TaskName -TaskPath $TaskPathRoot -ErrorAction SilentlyContinue

if ($existingTask) {
    $isOwned = $false
    try {
        if ($existingTask.Description -like "$OwnershipDescription*") {
            $actions = @($existingTask.Actions)
            if ($actions.Count -eq 1 -and $actions[0].Execute) {
                $actionLeaf = Split-Path -Path $actions[0].Execute -Leaf
                if ($actionLeaf -eq $ExpectedExeName -and [string]::IsNullOrEmpty($actions[0].Arguments)) {
                    $isOwned = $true
                }
            }
        }
    }
    catch {
        $isOwned = $false
    }

    if (-not $isOwned) {
        throw "同名のタスク '$TaskName' が既に存在しますが、本スクリプトが管理するタスクとは判別できませんでした。`nGet-ScheduledTask -TaskName '$TaskName' -TaskPath '$TaskPathRoot' で確認の上、無関係なタスクであれば -TaskName で別名を指定してください。"
    }

    Write-Host "既存タスクを削除します（本スクリプト管理下のタスクと確認済み）..." -ForegroundColor Yellow
    Unregister-ScheduledTask -TaskName $TaskName -TaskPath $TaskPathRoot -Confirm:$false
}

$action = New-ScheduledTaskAction -Execute $resolvedExePath -WorkingDirectory (Split-Path -Path $resolvedExePath -Parent)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
$principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable

Register-ScheduledTask -TaskName $TaskName -TaskPath $TaskPathRoot -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Description $OwnershipDescription | Out-Null

Write-Host "タスク '$TaskName' を登録しました（ログオン時に自動起動、非elevated）。" -ForegroundColor Green

if ($StartNow) {
    $processName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedExePath)
    $candidates = Get-Process -Name $processName -ErrorAction SilentlyContinue
    $running = $candidates | Where-Object {
        try { $_.Path -eq $resolvedExePath } catch { $false }
    }

    if ($running) {
        Write-Warning "既に $resolvedExePath を実行中のプロセスがあります（PID: $($running.Id -join ', ')）。二重起動を避けるため Start-ScheduledTask はスキップします。"
    }
    else {
        Start-ScheduledTask -TaskName $TaskName -TaskPath $TaskPathRoot
        Write-Host "タスクを起動しました。" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "起動状態の確認:" -ForegroundColor Cyan
Write-Host "  Get-ScheduledTaskInfo -TaskName '$TaskName' -TaskPath '$TaskPathRoot' | Select-Object LastRunTime, LastTaskResult"
Write-Host "最新ログの確認:" -ForegroundColor Cyan
Write-Host '  Get-ChildItem "$env:LOCALAPPDATA\VRCRemoteTest\logs\bridge-*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content -Tail 80'
