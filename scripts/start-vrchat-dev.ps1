<#
.SYNOPSIS
    VRChatを開発/検証用引数で起動する（1台、または検証用に複数台）。

.DESCRIPTION
    PowerShell 7（pwsh.exe）で実行する。Windows PowerShell 5.1 で起動されたときは、
    何もせずに pwsh.exe での起動し直し方を示して終わる。5.1 が日本語を読み違えないよう、
    このファイルは BOM 付きの UTF-8 で保存し、PowerShell 7 にしかない構文は使わない。

    仕様書 §28 の Launch configuration に基づき、--watch-worlds 付きで
    VRChat.exe を直接起動する（Steam経由のlaunch optionsは使わない）。

    Desktop Test: --watch-worlds --no-vr --enable-debug-gui --enable-sdk-log-levels --enable-udon-debug-logging
    VR Test:      --watch-worlds --enable-debug-gui --enable-sdk-log-levels --enable-udon-debug-logging

    -NoWatchWorlds を指定すると --watch-worlds を外す（Phase 0.5 検証スパイクの
    ネガティブベースラインケース用）。

    -Clients / -WorldPath / -Profiles / -LaunchIntervalSeconds は複数クライアント
    検証スパイク（docs/validation/multi-client-spike.md）用。VRChat SDK 3.10.4 の
    Build & Test（VRCWorldAssetExporter.RunWorldTestDesktop の IL で確認）と同じ
    起動のしかたを再現する:
      - roomId として乱数10桁を1つ作り、全台に同じ --url=create?roomId=... を渡す
      - 1台起動するごとに一定時間（SDK は3秒）待つ
      - SDK は --profile を付けない（全台が profile 0、つまり同じアカウント）。
        -Profiles を指定したときだけ、台ごとに --profile=N を付ける

.PARAMETER Mode
    "Desktop" または "VR"。既定は "Desktop"。差分は --no-vr の有無のみ（仕様書§28）。
    VR で複数台起動した場合、HMD を使うのは1台目だけで、2台目以降には --no-vr を付ける。

.PARAMETER VrChatPath
    VRChat.exe への絶対パス。既定は Steam の標準インストール先。

.PARAMETER NoWatchWorlds
    指定すると --watch-worlds を付けずに起動する（ネガティブベースライン検証用）。

.PARAMETER Clients
    起動する台数（1〜8。SDK の Number of Clients と同じ上限）。既定は1。
    -Profiles を指定した場合は省略でき、その要素数が台数になる。

.PARAMETER WorldPath
    起動直後に入るローカルテストワールドの .vrcw。指定すると SDK の Build & Test と同じく
    --url=create?roomId=<乱数10桁>&hidden=true&name=BuildAndRun&url=file:///<パス> を
    全台に渡し、全台を同じローカルインスタンスに入れる。省略時は --url を付けない
    （ホーム画面から起動する）。

.PARAMETER Profiles
    台ごとの VRChat profile 番号（0〜99）。例: -Profiles 0,1。profile ごとに別の
    アカウントで一度ログインしておく必要がある。省略時は --profile を付けない（SDK と同じ）。

.PARAMETER LaunchIntervalSeconds
    1台起動するごとに待つ秒数（0〜60）。既定は3（SDK と同じ）。

.EXAMPLE
    .\start-vrchat-dev.ps1 -Mode Desktop

.EXAMPLE
    .\start-vrchat-dev.ps1 -Mode Desktop -NoWatchWorlds

.EXAMPLE
    # SDK の Number of Clients = 2 と同じ起動（同じアカウントで2台）
    .\start-vrchat-dev.ps1 -Clients 2 -WorldPath C:\VRCRemoteTest\spike\build-A.vrcw

.EXAMPLE
    # 別アカウントで2台（profile 0 と 1）
    .\start-vrchat-dev.ps1 -Profiles 0,1 -WorldPath C:\VRCRemoteTest\spike\build-A.vrcw
#>

[CmdletBinding()]
param(
    [ValidateSet('Desktop', 'VR')]
    [string]$Mode = 'Desktop',

    [string]$VrChatPath = 'C:\Program Files (x86)\Steam\steamapps\common\VRChat\VRChat.exe',

    [switch]$NoWatchWorlds,

    [ValidateRange(1, 8)]
    [int]$Clients,

    [string]$WorldPath,

    [ValidateCount(1, 8)]
    [ValidateRange(0, 99)]
    [int[]]$Profiles,

    [ValidateRange(0, 60)]
    [int]$LaunchIntervalSeconds = 3
)

# 5.1 で起動されたときに示す、pwsh.exe での起動し直し方。-File では配列の引数を
# 渡せない（-Profiles 0,1 が 1 つの数 1 になる）ので、-Command で同じ引数を付けて呼ぶ。
# 版の確認より前に使うので、Windows PowerShell 5.1 でも動く書き方にする。
function Format-PwshRestartCommand {
    param([string]$ScriptPath, [System.Collections.IDictionary]$Parameters)

    $quote = { param($v) "'" + ("$v" -replace "'", "''") + "'" }
    $parts = @('&', (& $quote $ScriptPath))
    foreach ($name in $Parameters.Keys) {
        $value = $Parameters[$name]
        if ($value -is [switch] -or $value -is [bool]) {
            if ($value) {
                $parts += "-$name"
            }
        }
        elseif ($value -is [array]) {
            $parts += "-$name"
            $parts += (@($value | ForEach-Object { & $quote $_ }) -join ',')
        }
        elseif ($null -ne $value) {
            $parts += "-$name"
            $parts += (& $quote $value)
        }
    }

    'pwsh.exe -NoProfile -ExecutionPolicy Bypass -Command "' + ($parts -join ' ') + '"'
}

# #Requires -Version 7.0 は使わない。5.1 はそれを英語の一文で断るだけで、
# どう起動し直せばよいかが分からない。
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Host "このスクリプトは PowerShell 7（pwsh.exe）で実行してください。いまは Windows PowerShell $($PSVersionTable.PSVersion) で動いています。何もせずに終わります。" -ForegroundColor Red
    Write-Host "次のコマンドで起動し直してください:" -ForegroundColor Red
    Write-Host ("  " + (Format-PwshRestartCommand -ScriptPath $PSCommandPath -Parameters $PSBoundParameters))
    exit 1
}

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $VrChatPath -PathType Leaf)) {
    throw "VRChat.exe が見つかりません: $VrChatPath`n-VrChatPath で正しいパスを指定してください。"
}

# -Profiles 0 は要素が 0 だけの配列になり、if ($Profiles) では偽になるので、
# 指定の有無は $PSBoundParameters で判定する。
$hasProfiles = $PSBoundParameters.ContainsKey('Profiles')

if ($hasProfiles) {
    if ($PSBoundParameters.ContainsKey('Clients') -and $Clients -ne $Profiles.Count) {
        throw "-Clients ($Clients) と -Profiles の要素数 ($($Profiles.Count)) が一致しません。どちらか一方だけの指定でもかまいません。"
    }
    $Clients = $Profiles.Count
}
elseif (-not $PSBoundParameters.ContainsKey('Clients')) {
    $Clients = 1
}

$launchUrl = $null
$roomId = $null

if ($WorldPath) {
    if (-not (Test-Path -LiteralPath $WorldPath -PathType Leaf)) {
        throw "-WorldPath のファイルが見つかりません: $WorldPath"
    }

    $worldFullPath = (Resolve-Path -LiteralPath $WorldPath).ProviderPath

    if ([System.IO.Path]::GetExtension($worldFullPath) -ne '.vrcw') {
        throw "-WorldPath は .vrcw 拡張子である必要があります: $worldFullPath"
    }

    $roomId = -join (1..10 | ForEach-Object { Get-Random -Minimum 0 -Maximum 10 })

    # SDK は UnityWebRequest.EscapeURL でパス全体をエンコードしている（空白は %20 に置換）。
    # EscapeDataString も : \ 空白 & をすべてエンコードするので、引数に空白や & が残らない。
    $escapedWorldPath = [System.Uri]::EscapeDataString($worldFullPath)
    $launchUrl = "--url=create?roomId=$roomId&hidden=true&name=BuildAndRun&url=file:///$escapedWorldPath"
}

$existing = Get-Process -Name 'VRChat' -ErrorAction SilentlyContinue
if ($existing) {
    Write-Warning "VRChatは既に起動中です（PID: $($existing.Id -join ', ')）。この既存プロセスが検証結果を汚染する可能性があります（ネガティブベースラインでは --watch-worlds 付きの既存プロセスがリロードしてしまう、複数台の検証では既存プロセスだけ別のインスタンスに残る、など）。"
}

# SDK と同じく、作業ディレクトリを VRChat.exe のあるディレクトリにする。
$vrchatDirectory = [System.IO.Path]::GetDirectoryName($VrChatPath)

Write-Host "Starting VRChat ($Mode mode$(if ($NoWatchWorlds) { ', --watch-worlds DISABLED (negative baseline)' }), $Clients client(s))..." -ForegroundColor Cyan
Write-Host "Path: $VrChatPath"
if ($roomId) {
    Write-Host "World: $worldFullPath"
    Write-Host "roomId: $roomId"
}

for ($i = 0; $i -lt $Clients; $i++) {
    $launchArgs = [System.Collections.Generic.List[string]]::new()

    if ($launchUrl) {
        $launchArgs.Add($launchUrl)
    }

    if (-not $NoWatchWorlds) {
        $launchArgs.Add('--watch-worlds')
    }

    # HMD を使えるのは1台だけなので、VR モードでも2台目以降はデスクトップで起動する。
    if ($Mode -eq 'Desktop' -or $i -gt 0) {
        $launchArgs.Add('--no-vr')
    }

    $launchArgs.Add('--enable-debug-gui')
    $launchArgs.Add('--enable-sdk-log-levels')
    $launchArgs.Add('--enable-udon-debug-logging')

    if ($hasProfiles) {
        $launchArgs.Add("--profile=$($Profiles[$i])")
    }

    Write-Host "[$($i + 1)/$Clients] Args: $($launchArgs -join ' ')"

    $process = Start-Process -FilePath $VrChatPath -ArgumentList $launchArgs -WorkingDirectory $vrchatDirectory -PassThru
    Write-Host "[$($i + 1)/$Clients] PID: $($process.Id)"

    if ($i -lt $Clients - 1 -and $LaunchIntervalSeconds -gt 0) {
        Start-Sleep -Seconds $LaunchIntervalSeconds
    }
}

if ($roomId) {
    Write-Host "起動しました。全台が同じローカルワールドに入ったか確認してください（output_log で roomId $roomId を検索し、見つかった行を記録してください）。" -ForegroundColor Green
}
else {
    Write-Host "起動しました。VRChatのホーム画面到達を確認してから deploy-test.ps1 を実行してください。" -ForegroundColor Green
}
