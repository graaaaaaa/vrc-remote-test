#Requires -Version 7.0
<#
.SYNOPSIS
    Bridge (VRCRemoteTest.Bridge.exe) を、信頼できる SHA-256 と照合したうえで
    Windows Task Scheduler に登録し、ログオン時に自動起動させる（仕様書§21）。

.DESCRIPTION
    Trigger "At log on" / Principal "Interactive, Limited"（非elevated）で
    タスクを登録する。BridgeはWindows Serviceにしない設計（仕様書§20）の
    ため、対話ユーザーセッション上でのみ動作させる必要がある。

    既定の配置先はSMB共有（StagingDirectory）の外側にある
    %LOCALAPPDATA%\Programs\VRCRemoteTest\Bridge\ 。SMB共有内に実行体を
    置くと、共有への書き込み権限を持つ者が実行体を差し替えることで
    ログオン時に自動実行されるコードを乗っ取れてしまうため
    （Codex計画レビューで検出）、意図的にSMB共有の外に配置する。

    -ExpectedSha256 は必須。-SourcePath（例: SMB共有内の一時drop位置
    C:\VRCRemoteTest\_bridge-drop\VRCRemoteTest.Bridge.exe）は共有への
    書き込み権限を持つ者が差し替えられるため、そのまま信用しない。
    配置先ディレクトリ内に新しい候補ファイルを作ってコピーし、その候補の
    SHA-256 を照合してから、同じディレクトリ内の rename で ExePath に置き換える。
    照合後にコピー元を読み直すことはない（セキュリティレビュー S8）。

    期待するハッシュは、共有以外の経路で用意すること。共有内の .sha256 や、
    侵害されている可能性のある端末が出した値では真正性の保証にならない。
    出所の選び方は docs/setup-windows.md の手順1を参照。

    配置先は %LOCALAPPDATA% の下に限り、現在のユーザー・SYSTEM・Administrators
    以外が書き込めないディレクトリであることを検査する。シンボリックリンクや
    ジャンクションを含むパスは拒否する。

    稼働中の Bridge を自動で停止はしない。置き換えが必要なのに稼働中の場合は、
    何も変更せずに中断する。既存タスクが無効化されていれば、再登録後も無効の
    まま残す（移行作業中に勝手に起動しないようにするため）。

    再実行しても安全（冪等）: 配置済みの実行体が期待するハッシュと一致すれば
    -SourcePath は読まずに置き換えを省略する。同名タスクは、本スクリプトが
    登録したものと判別できたときのみ削除・再登録する。

.PARAMETER ExpectedSha256
    配置する実行体の SHA-256（16進64文字、大文字小文字は不問）。必須。

.PARAMETER ExePath
    Bridge実行体の配置先（登録するタスクのアクションが指すパス）。%LOCALAPPDATA% の下に限る。
    既定は %LOCALAPPDATA%\Programs\VRCRemoteTest\Bridge\VRCRemoteTest.Bridge.exe 。

.PARAMETER SourcePath
    複写元の実行体パス（省略可）。配置済みの実行体が期待するハッシュと
    一致しない場合に、ここから検証付きで置き換える。

.PARAMETER TaskName
    登録するタスク名。既定は "VRCRemoteTestBridge"。

.PARAMETER StartNow
    指定すると、登録直後にタスクを起動する。同じ実行体が既に稼働中の
    場合は二重起動を避けるためスキップする。

.EXAMPLE
    .\install-bridge.ps1 -ExpectedSha256 <64桁のハッシュ>

.EXAMPLE
    .\install-bridge.ps1 -ExpectedSha256 <64桁のハッシュ> -SourcePath C:\VRCRemoteTest\_bridge-drop\VRCRemoteTest.Bridge.exe -StartNow
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedSha256,

    [string]$ExePath = (Join-Path $env:LOCALAPPDATA 'Programs\VRCRemoteTest\Bridge\VRCRemoteTest.Bridge.exe'),

    [string]$SourcePath,

    [string]$TaskName = 'VRCRemoteTestBridge',

    [switch]$StartNow
)

$ErrorActionPreference = 'Stop'

$ExpectedExeName = 'VRCRemoteTest.Bridge.exe'
$TaskPathRoot = '\'
$OwnershipDescription = 'VRC Remote Test Bridge auto-start (managed by vrc-remote-test install-bridge.ps1)'

# 自己完結型の単一ファイル実行体は 100MB 程度。上限は悪意あるコピー元への備え。
$MaxExeBytes = 1GB

function Assert-ExeLeafName {
    param([string]$Path, [string]$ParamName)

    $leaf = Split-Path -Path $Path -Leaf
    if ($leaf -ne $ExpectedExeName) {
        throw "$ParamName のファイル名は $ExpectedExeName である必要があります: $Path"
    }
}

function Assert-LocalAbsolutePath {
    param([string]$Path, [string]$ParamName)

    if ($Path.StartsWith('\\') -or $Path.StartsWith('//')) {
        throw "$ParamName にネットワーク（UNC）パスは指定できません: $Path"
    }

    if (-not [System.IO.Path]::IsPathFullyQualified($Path)) {
        throw "$ParamName は絶対パスで指定してください: $Path"
    }
}

# ドライブルートまで遡って検査する。配置先より上にジャンクションやシンボリック
# リンクがあると、実行体が他人の書き込める場所へ振り向けられ得るため。
function Assert-NoReparsePoint {
    param([string]$Path)

    $current = [System.IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                throw "シンボリックリンクまたはジャンクションを含むパスには配置できません: $current"
            }
        }

        $current = [System.IO.Path]::GetDirectoryName($current)
    }
}

# 検証済みの候補はこのディレクトリ内で rename され、Task Scheduler がログオンの
# たびに実行する。現在のユーザー・SYSTEM・Administrators 以外が書き込み・権限変更
# できてはならない（セキュリティレビュー S8）。継承専用の ACE も、ここに作られる
# ファイルに適用されるため検査対象に含める。
function Assert-ProtectedItem {
    param([string]$Path)

    $currentUserSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $trustedOwners = @($currentUserSid, 'S-1-5-18', 'S-1-5-32-544')
    # CREATOR OWNER / OWNER RIGHTS は所有者（上記で信頼済み）に解決される。
    $trustedGrantees = $trustedOwners + @('S-1-3-0', 'S-1-3-4')

    $fsr = [System.Security.AccessControl.FileSystemRights]
    $writeMask = [int64]($fsr::WriteData -bor $fsr::AppendData -bor $fsr::WriteExtendedAttributes -bor
        $fsr::WriteAttributes -bor $fsr::DeleteSubdirectoriesAndFiles -bor $fsr::Delete -bor
        $fsr::ChangePermissions -bor $fsr::TakeOwnership)
    # 継承専用 ACE には GENERIC_WRITE / GENERIC_ALL が生のビットのまま現れることがある。
    $genericWriteMask = [int64](0x40000000 -bor 0x10000000)

    $acl = Get-Acl -LiteralPath $Path

    # null DACL は ACE が無くても全員に全アクセスを許す。解釈しない種類の ACE
    # （callback ACE など）も、黙って無視せずに中断する。
    $raw = [System.Security.AccessControl.RawSecurityDescriptor]::new($acl.GetSecurityDescriptorBinaryForm(), 0)
    if ((($raw.ControlFlags -band [System.Security.AccessControl.ControlFlags]::DiscretionaryAclPresent) -eq 0) -or
        ($null -eq $raw.DiscretionaryAcl)) {
        throw "配置先に DACL がありません（全員がアクセス可能）: $Path"
    }

    $supportedAceTypes = @([System.Security.AccessControl.AceType]::AccessAllowed, [System.Security.AccessControl.AceType]::AccessDenied)
    foreach ($ace in $raw.DiscretionaryAcl) {
        if ($ace.AceType -notin $supportedAceTypes) {
            throw "配置先に検査できない種類の ACE（$($ace.AceType)）があります: $Path"
        }
    }

    $ownerSid = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
    if ($ownerSid -notin $trustedOwners) {
        throw "配置先の所有者が想定外です（$ownerSid）: $Path"
    }

    foreach ($rule in $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) {
            continue
        }

        # FileSystemRights は Int32 なので、GENERIC_READ などの上位ビットで負になる。
        $rights = ([int64]$rule.FileSystemRights) -band 0xFFFFFFFFL
        if ((($rights -band $writeMask) -eq 0) -and (($rights -band $genericWriteMask) -eq 0)) {
            continue
        }

        $sid = $rule.IdentityReference.Value
        if ($sid -notin $trustedGrantees) {
            throw "配置先に、現在のユーザー・SYSTEM・Administrators 以外（$sid）の書き込み権限があります: $Path`n" +
                "ほかのユーザーが書き込めない場所を -ExePath に指定してください。"
        }
    }
}

function Get-RunningBridge {
    param([string]$Path)

    $processName = [System.IO.Path]::GetFileNameWithoutExtension($Path)
    Get-Process -Name $processName -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -eq $Path } catch { $false }
    }
}

# 読み取ったバイト数で上限を判定する。コピー中はコピー元への書き込みを共有しない。
function Copy-BoundedFile {
    param([string]$Source, [string]$Destination, [int64]$MaxBytes)

    $src = $null
    $dst = $null
    try {
        $src = [System.IO.File]::Open($Source, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
        $dst = [System.IO.File]::Open($Destination, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)

        $buffer = [byte[]]::new(81920)
        [int64]$total = 0
        while (($read = $src.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($read -gt ($MaxBytes - $total)) {
                throw "コピー元が上限（$MaxBytes バイト）を超えています: $Source"
            }

            $total += $read
            $dst.Write($buffer, 0, $read)
        }

        $dst.Flush($true)
    }
    finally {
        if ($dst) { $dst.Dispose() }
        if ($src) { $src.Dispose() }
    }
}

function Get-Sha256Hex {
    param([string]$Path)

    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

# --- パラメーターと配置先の検査（ここまでは何も変更しない。配置先ディレクトリの作成のみ） ---

Assert-ExeLeafName -Path $ExePath -ParamName '-ExePath'
Assert-LocalAbsolutePath -Path $ExePath -ParamName '-ExePath'
if ($SourcePath) {
    Assert-ExeLeafName -Path $SourcePath -ParamName '-SourcePath'
}

$ExePath = [System.IO.Path]::GetFullPath($ExePath)
$destDir = Split-Path -Path $ExePath -Parent

# 配置先と exe の ACL は検査できるが、上位ディレクトリ（親ごと差し替えられる
# DELETE_CHILD など）までは検査しない。その前提が成り立つ、本人のプロファイル内
# （%LOCALAPPDATA%）に配置先を限定する。
$localAppData = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($env:LOCALAPPDATA))
if (-not $destDir.StartsWith($localAppData + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "-ExePath は %LOCALAPPDATA%（$localAppData）の下に配置してください: $ExePath"
}

Assert-NoReparsePoint -Path $destDir
New-Item -ItemType Directory -Path $destDir -Force | Out-Null
Assert-ProtectedItem -Path $destDir

$exeExists = Test-Path -LiteralPath $ExePath -PathType Leaf
if ($exeExists) {
    Assert-NoReparsePoint -Path $ExePath
    Assert-ProtectedItem -Path $ExePath
}

# 無関係な同名タスクがあれば、実行体に触れる前に中断する。
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
}

# 移行作業などで無効化されたタスクは、再登録しても無効のまま残す。
$wasDisabled = [bool]($existingTask -and (-not $existingTask.Settings.Enabled))
if ($wasDisabled -and $StartNow) {
    throw "タスク '$TaskName' は無効化されています。確認が済んでから Enable-ScheduledTask で有効化し、-StartNow なしで再実行してください。"
}

# 別のパスから手動起動された Bridge は、ExePath の置き換えや停止確認から漏れる。
$otherBridges = @(Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($ExpectedExeName)) -ErrorAction SilentlyContinue |
    Where-Object { try { $_.Path -ne $ExePath } catch { $true } })
if ($otherBridges.Count -gt 0) {
    Write-Warning ("別のパスで動いている Bridge があります（PID: $($otherBridges.Id -join ', ')）。" +
        "同じ共有を二重に処理しないよう、不要なら停止してください。")
}

# --- 実行体の配置 ---

if ($exeExists -and ((Get-Sha256Hex -Path $ExePath) -ieq $ExpectedSha256)) {
    Write-Host "配置済みの実行体は期待する SHA-256 と一致するため、置き換えを省略します: $ExePath" -ForegroundColor Yellow
}
elseif (-not $SourcePath) {
    if ($exeExists) {
        throw "配置済みの実行体の SHA-256 が -ExpectedSha256 と一致しません: $ExePath`n" +
            "正しい実行体を -SourcePath で指定してください。"
    }

    throw "Bridge実行体が見つかりません: $ExePath`n先に dotnet publish で発行してから -SourcePath で複写元を指定してください。"
}
else {
    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        throw "-SourcePath で指定されたファイルが見つかりません: $SourcePath"
    }

    # .NET の File.Open はプロセスのカレントディレクトリで相対パスを解決し、
    # PowerShell の現在位置（Set-Location）とは一致しないため、先に絶対パスにする。
    $SourcePath = (Resolve-Path -LiteralPath $SourcePath -ErrorAction Stop).ProviderPath

    $running = Get-RunningBridge -Path $ExePath
    if ($running) {
        throw "実行体を置き換えるには、稼働中の Bridge（PID: $($running.Id -join ', ')）を先に停止してください。`n" +
            "例: Stop-ScheduledTask -TaskName '$TaskName' -TaskPath '$TaskPathRoot'（手動起動の場合は Stop-Process -Id <PID>）`n" +
            "配置済みの実行体とタスクは変更していません。"
    }

    $candidate = Join-Path $destDir ('.' + [guid]::NewGuid().ToString('N') + '.candidate')
    try {
        Write-Host "Copying: $SourcePath -> $candidate" -ForegroundColor Cyan
        Copy-BoundedFile -Source $SourcePath -Destination $candidate -MaxBytes $MaxExeBytes

        $actualHash = Get-Sha256Hex -Path $candidate
        if ($actualHash -ine $ExpectedSha256) {
            throw "SHA-256 が一致しません。配置済みの実行体とタスクは変更していません。`n" +
                "  期待: $ExpectedSha256`n  実際: $actualHash"
        }

        # 同じディレクトリ内の rename。照合済みの候補そのものが ExePath になる。
        [System.IO.File]::Move($candidate, $ExePath, $true)
        Write-Host "SHA-256 を照合した実行体を配置しました: $ExePath" -ForegroundColor Green
    }
    finally {
        if (Test-Path -LiteralPath $candidate) {
            Remove-Item -LiteralPath $candidate -Force -ErrorAction SilentlyContinue
        }
    }
}

# --- タスク登録 ---

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name

Write-Host "Exe:      $ExePath"
Write-Host "Identity: $identity"
Write-Host "TaskName: $TaskName"

if ($existingTask) {
    Write-Host "既存タスクを削除します（本スクリプト管理下のタスクと確認済み）..." -ForegroundColor Yellow
    Unregister-ScheduledTask -TaskName $TaskName -TaskPath $TaskPathRoot -Confirm:$false
}

$action = New-ScheduledTaskAction -Execute $ExePath -WorkingDirectory $destDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
$principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable -Disable:$wasDisabled

Register-ScheduledTask -TaskName $TaskName -TaskPath $TaskPathRoot -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Description $OwnershipDescription | Out-Null

if ($wasDisabled) {
    Write-Host "タスク '$TaskName' を再登録しました（無効化された状態を維持）。" -ForegroundColor Yellow
}
else {
    Write-Host "タスク '$TaskName' を登録しました（ログオン時に自動起動、非elevated）。" -ForegroundColor Green
}

if ($StartNow) {
    $running = Get-RunningBridge -Path $ExePath
    if ($running) {
        Write-Warning "既に $ExePath を実行中のプロセスがあります（PID: $($running.Id -join ', ')）。二重起動を避けるため Start-ScheduledTask はスキップします。"
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
