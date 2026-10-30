<#
.SYNOPSIS
    Bridge (VRCRemoteTest.Bridge.exe) を、信頼できる SHA-256 と照合したうえで
    Windows Task Scheduler に登録し、ログオン時に自動起動させる（仕様書§21）。

.DESCRIPTION
    PowerShell 7 で、実行ポリシーに止められない形で起動する:
      pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\install-bridge.ps1 -ExpectedSha256 <SHA-256> -SourcePath <exe> -StartNow
    Windows PowerShell 5.1（powershell.exe）で起動されたときは、何も変更せずに
    上の形の起動し直し方を示して止まる。5.1 が日本語を読み違えて、その案内より
    前に構文エラーで止まらないよう、このファイルは BOM 付きの UTF-8 で保存する。
    5.1 でも読めるよう、PowerShell 7 にしかない構文（?? や三項演算子など）は使わない。

    Trigger "At log on" / Principal "Interactive, Limited"（非elevated）で
    タスクを登録する。BridgeはWindows Serviceにしない設計（仕様書§20）の
    ため、対話ユーザーセッション上でのみ動作させる必要がある。

    既定の配置先は %LOCALAPPDATA%\Programs\VRCRemoteTest\Bridge\ 。
    -ExpectedSha256 は必須。-SourcePath からコピーした実行体を
    配置先で検証してから置き換える。

    配置先ディレクトリ内に新しい候補ファイルを作ってコピーし、その候補の
    SHA-256 を照合してから、同じディレクトリ内の rename で ExePath に置き換える。
    照合後にコピー元を読み直すことはない（セキュリティレビュー S8）。
    rename がアクセス拒否か共有違反で失敗したときは、照合済みの同じ候補で
    0.5 秒おきに最大 10 回まで入れ直す（Bridge を止めた直後は、exe の置き換えが
    しばらく拒否されることがあるため）。

    期待するハッシュは、公開された信頼できる経路で確認すること。
    出所の選び方は docs/setup-windows.md の手順1を参照。

    配置先は %LOCALAPPDATA% の下に限り、現在のユーザー・SYSTEM・Administrators
    以外が書き込めないディレクトリであることを検査する。シンボリックリンクや
    ジャンクションを含むパスは拒否する。

    稼働中の Bridge を自動で停止はしない。置き換えが必要なのに稼働中の場合は、
    何も変更せずに中断する。既存タスクが無効化されていれば、再登録後も無効の
    まま残す（移行作業中に勝手に起動しないようにするため）。

    再実行しても安全（冪等）: 配置済みの実行体が期待するハッシュと一致すれば
    -SourcePath は読まずに置き換えを省略する。同名タスクは、本スクリプトが
    登録したものと判別できたときのみ扱う。その定義（Action、Trigger、Principal、
    Settings、Description）が登録したい定義と同じなら、削除も再登録もせずに残す。
    違うときだけ削除・再登録し、削除できなければ、何をすれば直るかを示して止まる
    （v0.7.1 から v0.8.0 への入れ替えで、Unregister-ScheduledTask が「アクセスが
    拒否されました」で失敗した。2026-10-31）。タスクの記述子から削除できないと
    分かるときは、実行体を置き換える前に止まる。タスクの段階で止まったときは、
    実行体とタスクがどの状態で残ったかと、やり直すコマンドを表示する。

    管理者として実行されたときは、何も変更せずに止まる。管理者の権限で登録した
    タスクは所有者が Administrators になり、本人が削除できなくなるため（上の失敗の原因）。

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
    pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\install-bridge.ps1 -ExpectedSha256 <64桁のハッシュ>

.EXAMPLE
    pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\install-bridge.ps1 -ExpectedSha256 <64桁のハッシュ> -SourcePath C:\Users\USER\Downloads\VRCRemoteTest.Bridge.exe -StartNow
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

# 案内に出す起動コマンド。値は cmd.exe からも PowerShell からも同じに渡る二重引用符で囲む。
# 版の確認より前に使うので、Windows PowerShell 5.1 でも動く書き方にする。
function Format-PwshCommand {
    param([string]$ScriptPath, [System.Collections.IDictionary]$Parameters)

    if (-not $ScriptPath) {
        $ScriptPath = '.\install-bridge.ps1'
    }

    $parts = @('pwsh.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $ScriptPath + '"'))
    foreach ($name in $Parameters.Keys) {
        $value = $Parameters[$name]
        if ($value -is [switch] -or $value -is [bool]) {
            if ($value) {
                $parts += "-$name"
            }
        }
        elseif ($null -ne $value -and "$value" -ne '') {
            $parts += "-$name"
            $parts += ('"' + $value + '"')
        }
    }

    $parts -join ' '
}

# 指定された引数を、案内のコマンドに並べる順で返す。
function Get-InvocationParameter {
    param([System.Collections.IDictionary]$Bound, [switch]$WithoutSource)

    $ordered = [ordered]@{}
    foreach ($name in @('ExpectedSha256', 'SourcePath', 'ExePath', 'TaskName', 'StartNow')) {
        if ($WithoutSource -and $name -eq 'SourcePath') {
            continue
        }

        # $PSBoundParameters には Contains が無く、[ordered] には ContainsKey が無い。
        if ($Bound.Keys -contains $name) {
            $ordered[$name] = $Bound[$name]
        }
    }

    $ordered
}

# #Requires -Version 7.0 は使わない。5.1 はそれを英語の一文で断るだけで、
# どう起動し直せばよいかが分からない（2026-10-31 の入れ替えで 5.1 から起動された）。
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Host "このスクリプトは PowerShell 7（pwsh.exe）で実行してください。いまは Windows PowerShell $($PSVersionTable.PSVersion) で動いています。何も変更していません。" -ForegroundColor Red
    Write-Host "次のコマンドで起動し直してください:" -ForegroundColor Red
    Write-Host ("  " + (Format-PwshCommand -ScriptPath $PSCommandPath -Parameters (Get-InvocationParameter -Bound $PSBoundParameters)))
    Write-Host "PowerShell 7 が無ければ、先に入れてください（docs/setup-windows.md の手順1）。"
    exit 1
}

$ErrorActionPreference = 'Stop'

# 止まったときに示す、やり直しのコマンド。実行体の置き換えが済んだあとは
# -SourcePath が要らないので、それを除いたものも用意する。
$RerunCommand = Format-PwshCommand -ScriptPath $PSCommandPath -Parameters (Get-InvocationParameter -Bound $PSBoundParameters)
$RerunWithoutSourceCommand = Format-PwshCommand -ScriptPath $PSCommandPath -Parameters (Get-InvocationParameter -Bound $PSBoundParameters -WithoutSource)

$ExpectedExeName = 'VRCRemoteTest.Bridge.exe'
$TaskPathRoot = '\'
$OwnershipDescription = 'VRC Remote Test Bridge auto-start (managed by vrc-remote-test install-bridge.ps1)'

# 自己完結型の単一ファイル実行体は 100MB 程度。上限は悪意あるコピー元への備え。
$MaxExeBytes = 1GB

# 置き換えの試行は最初の1回を含めて最大10回、間の待ちは合計 4.5 秒。
$ReplaceMaxAttempts = 10
$ReplaceRetryDelayMs = 500

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

# Bridge のプロセスが終わった直後は、Windows がまだ exe を閉じきっていない、または
# ウイルス対策ソフトが調べているため、置き換えが一時的に拒否されることがある
# （2026-10-18 の実機確認）。アクセス拒否と共有違反だけを一時的な失敗とみなす。
function Test-TransientReplaceError {
    param([System.Exception]$Exception)

    if ($Exception -is [System.UnauthorizedAccessException]) {
        return $true
    }

    # IOException の HResult は 0x8007xxxx で、下位16ビットが Win32 のエラーコード
    # （32 = ERROR_SHARING_VIOLATION）。
    return ($Exception -is [System.IO.IOException]) -and (($Exception.HResult -band 0xFFFF) -eq 32)
}

# 照合済みの候補を Destination に rename し、成功した試行の回数を返す。一時的な
# 失敗では同じ候補で入れ直す（コピー元を読み直したり、候補を作り直したりはしない）。
function Move-VerifiedCandidate {
    param([string]$Candidate, [string]$Destination)

    for ($attempt = 1; ; $attempt++) {
        try {
            [System.IO.File]::Move($Candidate, $Destination, $true)
            return $attempt
        }
        catch {
            # .NET のメソッドの例外は MethodInvocationException に包まれて届く。
            $failure = $_.Exception
            if ($failure -is [System.Management.Automation.MethodInvocationException] -and $failure.InnerException) {
                $failure = $failure.InnerException
            }

            if (-not (Test-TransientReplaceError -Exception $failure)) {
                throw
            }

            if ($attempt -ge $ReplaceMaxAttempts) {
                throw "実行体の置き換えが $attempt 回とも拒否されました: $($failure.Message)`n" +
                    "配置済みの実行体とタスクは変更していません。Bridge が止まっていること（Get-Process -Name VRCRemoteTest.Bridge）を確かめ、" +
                    "動いていれば Stop-ScheduledTask -TaskName '$TaskName' -TaskPath '$TaskPathRoot' で止めてから、もう一度実行してください:`n" +
                    "  $RerunCommand"
            }

            if ($attempt -eq 1) {
                Write-Host "置き換えが拒否されました（$($failure.Message)）。$($ReplaceRetryDelayMs / 1000) 秒おきに、最大 $ReplaceMaxAttempts 回まで入れ直します..." -ForegroundColor Yellow
            }

            Start-Sleep -Milliseconds $ReplaceRetryDelayMs
        }
    }
}

function Test-SameTaskPath {
    param([string]$Actual, [string]$Expected)

    $normalize = { param($p) "$p".Trim().Trim('"').TrimEnd('\', '/') }
    [string]::Equals((& $normalize $Actual), (& $normalize $Expected), [System.StringComparison]::OrdinalIgnoreCase)
}

# Task Scheduler はアカウントを登録時の形（PC\user）のほか、ドメイン無しの
# ユーザー名や SID で返すことがある。どれも現在のユーザーなら同じとみなす。
function Test-CurrentUserAccount {
    param([string]$Value, [string]$IdentityName, [string]$Sid)

    if ([string]::IsNullOrEmpty($Value)) {
        return $false
    }

    $leaf = $IdentityName.Substring($IdentityName.LastIndexOf('\') + 1)
    foreach ($candidate in @($IdentityName, $leaf, $Sid)) {
        if ([string]::Equals($Value, $candidate, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    $false
}

function Get-CimClassNameOf {
    param($Item)

    if ($Item -and $Item.CimClass) {
        return $Item.CimClass.CimClassName
    }

    $null
}

# 既存タスクの定義と、登録したい定義（New-ScheduledTask* で作った値）の違いを
# 1行ずつの説明で返す。同じなら空。タスクの有効・無効は比べない（無効のまま残すため）。
function Get-TaskDefinitionDifference {
    param(
        $Existing,
        $Action,
        $Trigger,
        $Principal,
        $Settings,
        [string]$Description,
        [string]$IdentityName,
        [string]$Sid
    )

    $differences = @()

    if ($Existing.Description -ne $Description) {
        $differences += "Description: '$($Existing.Description)'（登録したい値: '$Description'）"
    }

    $actions = @($Existing.Actions)
    if ($actions.Count -ne 1) {
        $differences += "Action の数: $($actions.Count)（登録したい数: 1）"
    }
    else {
        $actual = $actions[0]
        if ((Get-CimClassNameOf $actual) -ne (Get-CimClassNameOf $Action)) {
            $differences += "Action の種類: $(Get-CimClassNameOf $actual)（登録したい種類: $(Get-CimClassNameOf $Action)）"
        }
        if (-not (Test-SameTaskPath -Actual $actual.Execute -Expected $Action.Execute)) {
            $differences += "Action の実行体: '$($actual.Execute)'（登録したい値: '$($Action.Execute)'）"
        }
        if (-not [string]::IsNullOrEmpty($actual.Arguments)) {
            $differences += "Action の引数: '$($actual.Arguments)'（登録したい値: なし）"
        }
        if (-not (Test-SameTaskPath -Actual $actual.WorkingDirectory -Expected $Action.WorkingDirectory)) {
            $differences += "Action の作業ディレクトリ: '$($actual.WorkingDirectory)'（登録したい値: '$($Action.WorkingDirectory)'）"
        }
    }

    $triggers = @($Existing.Triggers)
    if ($triggers.Count -ne 1) {
        $differences += "Trigger の数: $($triggers.Count)（登録したい数: 1）"
    }
    else {
        $actual = $triggers[0]
        if ((Get-CimClassNameOf $actual) -ne (Get-CimClassNameOf $Trigger)) {
            $differences += "Trigger の種類: $(Get-CimClassNameOf $actual)（登録したい種類: $(Get-CimClassNameOf $Trigger)、ログオン時）"
        }
        if (-not (Test-CurrentUserAccount -Value $actual.UserId -IdentityName $IdentityName -Sid $Sid)) {
            $differences += "Trigger のユーザー: '$($actual.UserId)'（登録したい値: '$IdentityName'）"
        }
        if ($actual.Enabled -eq $false) {
            $differences += "Trigger が無効になっています"
        }
    }

    $actualPrincipal = $Existing.Principal
    if (-not (Test-CurrentUserAccount -Value $actualPrincipal.UserId -IdentityName $IdentityName -Sid $Sid)) {
        $differences += "Principal のユーザー: '$($actualPrincipal.UserId)'（登録したい値: '$IdentityName'）"
    }
    foreach ($name in @('LogonType', 'RunLevel')) {
        if ("$($actualPrincipal.$name)" -ne "$($Principal.$name)") {
            $differences += "Principal の ${name}: '$($actualPrincipal.$name)'（登録したい値: '$($Principal.$name)'）"
        }
    }

    foreach ($name in @('MultipleInstances', 'ExecutionTimeLimit', 'StartWhenAvailable')) {
        if ("$($Existing.Settings.$name)" -ne "$($Settings.$name)") {
            $differences += "Settings の ${name}: '$($Existing.Settings.$name)'（登録したい値: '$($Settings.$name)'）"
        }
    }

    # 1行ずつ出す。呼び出し側は @() で受ける（, で包むと、@() の中で
    # 「空の配列を1つ持つ配列」になり、同じ定義でも違うと判定される。2026-10-31）。
    $differences
}

# タスクの段階で止まったときに、実行体とタスクがどの状態で残ったかを説明する。
# TaskState: none（もともと無い）/ original（元のタスクに触れていない）/
# kept（定義が同じなので元のタスクを残した）/ removed（削除して未登録）/ registered（登録し直した）
function Format-StopState {
    param(
        [bool]$ExeReplaced,
        [string]$ExePath,
        [string]$TaskState,
        [string]$TaskName,
        [string]$TaskPath,
        [string]$RerunCommand
    )

    $lines = @('--- どの状態で止まったか ---')

    if ($ExeReplaced) {
        $lines += "実行体: SHA-256 を照合した新しい実行体に置き換え済み（$ExePath）"
    }
    else {
        $lines += "実行体: 置き換えていない。配置済みの実行体は期待する SHA-256 と一致している（$ExePath）"
    }

    switch ($TaskState) {
        'none' { $lines += "タスク '$TaskName': 登録されていない。ログオン時に Bridge は起動しない" }
        'original' { $lines += "タスク '$TaskName': 元のまま残っている（削除も再登録もしていない）" }
        'kept' { $lines += "タスク '$TaskName': 定義が同じなので、元のタスクをそのまま残した" }
        'removed' { $lines += "タスク '$TaskName': 削除したが、登録し直せていない。ログオン時に Bridge は起動しない" }
        'registered' { $lines += "タスク '$TaskName': 登録し直した" }
        default { $lines += "タスク '$TaskName': 状態不明（$TaskState）" }
    }

    $lines += "Bridge: このスクリプトは起動していない"

    if ($TaskState -in @('original', 'kept', 'registered')) {
        $lines += "今すぐ Bridge を動かすだけなら: Start-ScheduledTask -TaskName '$TaskName' -TaskPath '$TaskPath'"
    }

    $lines += "原因を直したら、自分で開いた PowerShell で次を実行してやり直す（実行体は一致しているので、置き換えは省略される）:"
    $lines += "  $RerunCommand"

    $lines -join [Environment]::NewLine
}

# Task Scheduler の SDDL（例: O:BAG:…D:(A;ID;0x1f019f;;;BA)(A;;FR;;;S-1-5-21-…)）で、
# Sids のどれかに DELETE を許す ACE があるかを 'yes' / 'no' / 'unknown' で返す。
# 'no' のときだけ先に止めるので、読み違えても止まらない側に倒す（Deny の ACE は見ず、
# 書き換えの権利や知らない書き方は 'unknown' にする）。
function Get-SddlDeleteAccess {
    param([string]$Sddl, [string[]]$Sids)

    $pattern = '^(?:O:(?:S-[0-9-]+|[A-Z]{2}))?(?:G:(?:S-[0-9-]+|[A-Z]{2}))?D:(?<flags>[A-Z_]*)(?<aces>(?:\([^)]*\))*)'
    if ([string]::IsNullOrEmpty($Sddl) -or $Sddl -cnotmatch $pattern) {
        return 'unknown'
    }

    # NO_ACCESS_CONTROL（null DACL）は全員に全部を許す。
    if ($Matches['flags'].Contains('NO_ACCESS_CONTROL')) {
        return 'unknown'
    }

    $aces = @([regex]::Matches($Matches['aces'], '\(([^)]*)\)') | ForEach-Object { $_.Groups[1].Value })
    $aliases = @{
        BA = 'S-1-5-32-544'; BU = 'S-1-5-32-545'; PU = 'S-1-5-32-547'; SY = 'S-1-5-18'; LS = 'S-1-5-19'; NS = 'S-1-5-20'
        AU = 'S-1-5-11'; IU = 'S-1-5-4'; WD = 'S-1-1-0'; CO = 'S-1-3-0'; OW = 'S-1-3-4'
    }
    $grantCodes = @('GA', 'SD', 'FA', 'KA')
    $maybeCodes = @('GW', 'WD', 'WO')
    # DELETE と GENERIC_ALL / GENERIC_WRITE、WRITE_DAC、WRITE_OWNER。
    $grantBits = [int64](0x10000 -bor 0x10000000)
    $maybeBits = [int64](0x40000000 -bor 0x40000 -bor 0x80000)

    $unknown = $false
    foreach ($ace in $aces) {
        $fields = $ace.Split(';')
        if ($fields.Count -lt 6) {
            $unknown = $true
            continue
        }

        $type = $fields[0]
        $flags = $fields[1]
        $rights = $fields[2]
        $sid = $fields[5]

        if ($type -eq 'D') {
            continue
        }
        # IO（継承専用）の ACE はこの物自体には効かない。フラグは2文字ずつ並ぶ。
        if ($flags -cmatch '^(?:..)*IO') {
            continue
        }

        $grants = $false
        $maybe = $false
        if ($rights -match '^0[xX][0-9A-Fa-f]+$') {
            $mask = [Convert]::ToInt64($rights.Substring(2), 16)
            $grants = ($mask -band $grantBits) -ne 0
            $maybe = ($mask -band $maybeBits) -ne 0
        }
        elseif ($rights.Length % 2 -eq 0) {
            $codes = for ($i = 0; $i -lt $rights.Length; $i += 2) { $rights.Substring($i, 2) }
            $grants = @($codes | Where-Object { $_ -in $grantCodes }).Count -gt 0
            $maybe = @($codes | Where-Object { $_ -in $maybeCodes }).Count -gt 0
        }
        else {
            $unknown = $true
            continue
        }

        if (-not ($grants -or $maybe)) {
            continue
        }

        if ($sid -notmatch '^S-') {
            if ($aliases.ContainsKey($sid)) {
                $sid = $aliases[$sid]
            }
            else {
                $unknown = $true
                continue
            }
        }

        if ($Sids -notcontains $sid) {
            continue
        }

        if ($type -ne 'A' -or -not $grants) {
            $unknown = $true
            continue
        }

        return 'yes'
    }

    if ($unknown) {
        return 'unknown'
    }

    'no'
}

# 読めなければ $null（判断しない）。
function Get-TaskSddl {
    param([string]$TaskName, [string]$TaskPath)

    try {
        $service = New-Object -ComObject 'Schedule.Service'
        $service.Connect()
        # OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION
        $service.GetFolder($TaskPath).GetTask($TaskName).GetSecurityDescriptor(0x5)
    }
    catch {
        $null
    }
}

# --- パラメーターと配置先の検査（ここまでは何も変更しない。配置先ディレクトリの作成のみ） ---

# 管理者の権限で登録したタスクは所有者が Administrators になり、本人には読み取りしか
# 許されない。すると次の入れ替えで本人がタスクを削除できず、Unregister-ScheduledTask が
# 「アクセスが拒否されました」で止まる（2026-10-31、v0.7.1 から v0.8.0 への入れ替え。
# タスクの記述子は O:BA で、本人の ACE は FR だけだった）。
$currentIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$identity = $currentIdentity.Name
$identitySid = $currentIdentity.User.Value
if (([System.Security.Principal.WindowsPrincipal]::new($currentIdentity)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "管理者として実行しないでください。何も変更していません。`n" +
        "管理者の権限で登録したタスクは、あとでこのユーザーが削除できなくなり、次の入れ替えで止まります。" +
        "管理者でない PowerShell を開いて、次を実行してください:`n  $RerunCommand"
}

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

# --- タスクの定義と削除の権利の確認（何も変更しない） ---

$action = New-ScheduledTaskAction -Execute $ExePath -WorkingDirectory $destDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
$principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable -Disable:$wasDisabled

$differences = @()
if ($existingTask) {
    $differences = @(Get-TaskDefinitionDifference -Existing $existingTask -Action $action -Trigger $trigger `
            -Principal $principal -Settings $settings -Description $OwnershipDescription -IdentityName $identity -Sid $identitySid)

    # 削除できないと分かっていれば、実行体を置き換える前に止まる。
    $tokenSids = @($identitySid) + @($currentIdentity.Groups | ForEach-Object { $_.Value })
    $deleteAccess = Get-SddlDeleteAccess -Sddl (Get-TaskSddl -TaskName $TaskName -TaskPath $TaskPathRoot) -Sids $tokenSids
    if ($deleteAccess -eq 'no') {
        $unregisterCommand = "Unregister-ScheduledTask -TaskName '$TaskName' -TaskPath '$TaskPathRoot' -Confirm:`$false"
        if ($differences.Count -gt 0) {
            throw "既存タスク '$TaskName' の定義が違うため登録し直す必要がありますが、このユーザーにはタスクを削除する権利がありません" +
                "（管理者の権限で登録されたタスクと見られます）。実行体とタスクは変更していません。`n" +
                "違い:`n" + (($differences | ForEach-Object { "  $_" }) -join "`n") + "`n" +
                "直し方: 管理者の PowerShell で次を実行してタスクを削除してから、管理者でない PowerShell で、もう一度実行してください。`n" +
                "  $unregisterCommand`n  $RerunCommand"
        }

        Write-Host ("既存タスク '$TaskName' は管理者の権限で登録されたと見られ、このユーザーには削除できません。" +
            "今回は定義が同じなので残しますが、定義が変わる入れ替えでは止まります。" +
            "直しておくには、管理者の PowerShell で $unregisterCommand を実行してから、管理者でない PowerShell でこのスクリプトをもう一度実行してください。") -ForegroundColor Yellow
    }
}

# --- 実行体の配置 ---

$exeReplaced = $false

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
            "配置済みの実行体とタスクは変更していません。止めたら、もう一度実行してください:`n" +
            "  $RerunCommand"
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
        $attempts = Move-VerifiedCandidate -Candidate $candidate -Destination $ExePath
        $exeReplaced = $true
        if ($attempts -gt 1) {
            Write-Host "SHA-256 を照合した実行体を、$attempts 回目の置き換えで配置しました: $ExePath" -ForegroundColor Green
        }
        else {
            Write-Host "SHA-256 を照合した実行体を配置しました: $ExePath" -ForegroundColor Green
        }
    }
    finally {
        if (Test-Path -LiteralPath $candidate) {
            Remove-Item -LiteralPath $candidate -Force -ErrorAction SilentlyContinue
        }
    }
}

# --- タスク登録 ---

Write-Host "Exe:      $ExePath"
Write-Host "Identity: $identity"
Write-Host "TaskName: $TaskName"

$taskState = 'none'
if ($existingTask) {
    $taskState = 'original'
}

# ここから先で止まったときは、実行体とタスクがどの状態で残ったかを必ず示す。
try {
    $needsRegister = $true
    if ($existingTask) {
        if ($differences.Count -eq 0) {
            $needsRegister = $false
            $taskState = 'kept'
            Write-Host "既存タスク '$TaskName' は登録したい定義と同じなので、削除も再登録もせずに残します。" -ForegroundColor Yellow
        }
        else {
            Write-Host "既存タスク '$TaskName' の定義が違うため、削除して登録し直します（本スクリプト管理下のタスクと確認済み）:" -ForegroundColor Yellow
            $differences | ForEach-Object { Write-Host "  $_" }

            try {
                Unregister-ScheduledTask -TaskName $TaskName -TaskPath $TaskPathRoot -Confirm:$false
            }
            catch {
                throw "既存タスク '$TaskName' を削除できませんでした: $($_.Exception.Message)`n" +
                    "直し方: タスク スケジューラ（taskschd.msc）でタスク '$TaskName' を削除してください。" +
                    "削除できなければ、管理者の PowerShell で Unregister-ScheduledTask -TaskName '$TaskName' -TaskPath '$TaskPathRoot' -Confirm:`$false を実行してください。" +
                    "そのあとのやり直しは、管理者でない PowerShell で行ってください。"
            }

            $taskState = 'removed'
        }
    }

    if ($needsRegister) {
        Register-ScheduledTask -TaskName $TaskName -TaskPath $TaskPathRoot -Action $action -Trigger $trigger `
            -Principal $principal -Settings $settings -Description $OwnershipDescription | Out-Null
        $taskState = 'registered'

        if ($wasDisabled) {
            Write-Host "タスク '$TaskName' を再登録しました（無効化された状態を維持）。" -ForegroundColor Yellow
        }
        else {
            Write-Host "タスク '$TaskName' を登録しました（ログオン時に自動起動、非elevated）。" -ForegroundColor Green
        }
    }
    elseif ($wasDisabled) {
        Write-Host "タスク '$TaskName' は無効化された状態のままです。" -ForegroundColor Yellow
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
}
catch {
    Write-Host ""
    Write-Host "失敗しました: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host (Format-StopState -ExeReplaced $exeReplaced -ExePath $ExePath -TaskState $taskState -TaskName $TaskName `
            -TaskPath $TaskPathRoot -RerunCommand $RerunWithoutSourceCommand) -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "起動状態の確認:" -ForegroundColor Cyan
Write-Host "  Get-ScheduledTaskInfo -TaskName '$TaskName' -TaskPath '$TaskPathRoot' | Select-Object LastRunTime, LastTaskResult"
Write-Host "最新ログの確認:" -ForegroundColor Cyan
Write-Host '  Get-ChildItem "$env:LOCALAPPDATA\VRCRemoteTest\logs\bridge-*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content -Tail 80'
