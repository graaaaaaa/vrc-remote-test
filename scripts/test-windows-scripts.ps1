# Checks the Windows PowerShell scripts without Windows. Each one must stay
# readable by Windows PowerShell 5.1 (UTF-8 with a BOM, no PowerShell 7-only
# syntax) so that 5.1 reaches the "run me with pwsh.exe" message instead of a
# parse error, and that message must carry the same arguments. The
# task-definition comparison, stop-state report and rerun commands of
# install-bridge.ps1 are exercised with stand-in objects. Task Scheduler,
# VRChat and the file-system checks need Windows and are not covered here.
# Run from anywhere: pwsh -NoProfile -File scripts/test-windows-scripts.ps1

$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'install-bridge.ps1'
$failures = 0

function Check {
    param([string]$Name, [bool]$Condition, [string]$Detail)

    if ($Condition) {
        Write-Host "ok   $Name"
    }
    else {
        Write-Host "FAIL $Name"
        if ($Detail) {
            $Detail -split "`n" | ForEach-Object { Write-Host "     $_" }
        }
        $script:failures++
    }
}

function Read-Script {
    param([string]$Path)

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    [pscustomobject]@{ Ast = $ast; Tokens = $tokens; Errors = $parseErrors }
}

function Get-GateIndex {
    param($Ast)

    $statements = @($Ast.EndBlock.Statements)
    for ($i = 0; $i -lt $statements.Count; $i++) {
        if ($statements[$i].Extent.Text -match '^if \(\$PSVersionTable\.PSVersion\.Major -lt 7\)') {
            return $i
        }
    }
    -1
}

# Runs everything up to the version check in a child pwsh that pretends to be
# 5.1. -Command, because -File cannot pass an array argument (-Profiles 0,1).
function Invoke-As51 {
    param([string]$Path, $Ast, [string]$Arguments, [string]$Directory)

    $gateEnd = @($Ast.EndBlock.Statements)[(Get-GateIndex $Ast)].Extent.EndOffset
    $text = [System.IO.File]::ReadAllText($Path).Substring(0, $gateEnd)
    $paramEnd = $Ast.ParamBlock.Extent.EndOffset
    $fake = $text.Substring(0, $paramEnd) + "`n`$FakeVersionTable = @{ PSVersion = [version]'5.1.22621.4391' }`n" +
        $text.Substring($paramEnd).Replace('$PSVersionTable', '$FakeVersionTable') + "`nWrite-Host 'PAST THE GATE'`n"
    $fakePath = Join-Path $Directory (Split-Path -Path $Path -Leaf)
    [System.IO.File]::WriteAllText($fakePath, $fake, [System.Text.UTF8Encoding]::new($true))

    $output = & pwsh -NoProfile -Command "& '$fakePath' $Arguments; exit `$LASTEXITCODE" 2>&1 | Out-String
    [pscustomobject]@{ Path = $fakePath; Output = $output; ExitCode = $LASTEXITCODE }
}

# Runs the -Command that the 5.1 message printed against a stand-in with the
# same param block, and returns what it bound as "Name=value" lines.
function Get-BoundByPrintedCommand {
    param([string]$Output, [string]$FakePath, [string]$Path, $Ast, [string]$Directory)

    if ($Output -notmatch 'pwsh\.exe -NoProfile -ExecutionPolicy Bypass -Command "(.*)"') {
        return @('<no -Command in the output>')
    }

    $command = $Matches[1]
    $stubPath = Join-Path $Directory ('stub-' + (Split-Path -Path $Path -Leaf))
    $stub = [System.IO.File]::ReadAllText($Path).Substring(0, $Ast.ParamBlock.Extent.EndOffset) +
        "`n`$PSBoundParameters.Keys | Sort-Object | ForEach-Object { `"`$_=`$(@(`$PSBoundParameters[`$_]) -join '|')`" }`n"
    [System.IO.File]::WriteAllText($stubPath, $stub, [System.Text.UTF8Encoding]::new($true))

    @(& pwsh -NoProfile -Command $command.Replace("'$FakePath'", "'$stubPath'") 2>&1 | ForEach-Object { "$_" })
}

# --- Every script as Windows PowerShell 5.1 reads it ---

$ps7Kinds = @('AndAnd', 'OrOr', 'QuestionMark', 'QuestionQuestionEquals', 'QuestionQuestion', 'QuestionDot', 'QuestionLBracket')

# Arguments for the 5.1 run, and what the printed command must bind (sorted by name).
$restartCases = [ordered]@{
    'uninstall-bridge.ps1' = @{
        Arguments = "-TaskName 'it''s a task' -StopRunning"
        Bound = @("StopRunning=True", "TaskName=it's a task")
    }
    'deploy-test.ps1' = @{
        Arguments = "-SourcePath 'C:\a b\build.vrcw'"
        Bound = @('SourcePath=C:\a b\build.vrcw')
    }
    'start-vrchat-dev.ps1' = @{
        Arguments = "-Mode VR -Profiles 0,1 -WorldPath 'C:\a b\w.vrcw' -LaunchIntervalSeconds 0 -NoWatchWorlds"
        Bound = @('LaunchIntervalSeconds=0', 'Mode=VR', 'NoWatchWorlds=True', 'Profiles=0|1', 'WorldPath=C:\a b\w.vrcw')
    }
}

$fakeDir = Join-Path ([System.IO.Path]::GetTempPath()) ('windows-scripts-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fakeDir | Out-Null
# Parameter defaults read these, and they are not set outside Windows.
$env:LOCALAPPDATA = $fakeDir
$env:USERPROFILE = $fakeDir
try {
    foreach ($leaf in @('install-bridge.ps1') + @($restartCases.Keys)) {
        $path = Join-Path $PSScriptRoot $leaf
        $bytes = [System.IO.File]::ReadAllBytes($path)
        Check "${leaf}: saved as UTF-8 with a BOM" ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)

        $read = Read-Script $path
        Check "${leaf}: parses without errors" ($read.Errors.Count -eq 0) (($read.Errors | ForEach-Object { "$($_.Extent.StartLineNumber): $($_.Message)" }) -join "`n")
        Check "${leaf}: has no #Requires (5.1 would refuse in English before the pwsh.exe message)" ($null -eq $read.Ast.ScriptRequirements)

        $ps7Tokens = @($read.Tokens | Where-Object { "$($_.Kind)" -in $ps7Kinds })
        Check "${leaf}: uses no PowerShell 7-only operators" ($ps7Tokens.Count -eq 0) (($ps7Tokens | ForEach-Object { "$($_.Extent.StartLineNumber): $($_.Text)" }) -join "`n")

        $gateIndex = Get-GateIndex $read.Ast
        $beforeGate = @(@($read.Ast.EndBlock.Statements) | Select-Object -First ([Math]::Max($gateIndex, 0)))
        Check "${leaf}: the version check comes first, after only function definitions" (
            $gateIndex -ge 0 -and @($beforeGate | Where-Object { $_ -isnot [System.Management.Automation.Language.FunctionDefinitionAst] }).Count -eq 0)
        if ($gateIndex -lt 0) {
            continue
        }

        if ($leaf -eq 'install-bridge.ps1') {
            $hash = 'AB' * 32
            $run = Invoke-As51 -Path $path -Ast $read.Ast -Directory $fakeDir `
                -Arguments "-ExpectedSha256 $hash -SourcePath 'C:\Users\U S\Downloads\VRCRemoteTest.Bridge.exe' -StartNow"
            $expected = "pwsh.exe -NoProfile -ExecutionPolicy Bypass -File `"$($run.Path)`" -ExpectedSha256 `"$hash`" -SourcePath `"C:\Users\U S\Downloads\VRCRemoteTest.Bridge.exe`" -StartNow"
            Check "${leaf}: on 5.1 it prints the pwsh.exe command with the given arguments" ($run.Output.Contains($expected)) $run.Output
        }
        else {
            $case = $restartCases[$leaf]
            $run = Invoke-As51 -Path $path -Ast $read.Ast -Directory $fakeDir -Arguments $case.Arguments
            $bound = Get-BoundByPrintedCommand -Output $run.Output -FakePath $run.Path -Path $path -Ast $read.Ast -Directory $fakeDir
            Check "${leaf}: on 5.1 the printed pwsh.exe command passes the same arguments" (
                ($bound -join "`n") -ceq ($case.Bound -join "`n")) ("printed:`n$($run.Output)`nbound:`n$($bound -join "`n")")
        }

        Check "${leaf}: on 5.1 it stops with exit code 1" ($run.ExitCode -eq 1) $run.Output
        Check "${leaf}: on 5.1 it does not go past the version check" ($run.Output -notmatch 'PAST THE GATE') $run.Output
    }
}
finally {
    Remove-Item -LiteralPath $fakeDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- install-bridge.ps1's functions, loaded from the script without running it ---

$ast = (Read-Script $scriptPath).Ast

foreach ($function in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    . ([scriptblock]::Create($function.Extent.Text))
}

$bound = [ordered]@{ StartNow = [switch]$true; TaskName = 'T'; SourcePath = 'C:\a b\VRCRemoteTest.Bridge.exe'; ExpectedSha256 = 'abc' }
Check 'rerun command keeps the documented argument order' (
    (Format-PwshCommand -ScriptPath 'C:\s\install-bridge.ps1' -Parameters (Get-InvocationParameter -Bound $bound)) -ceq
    'pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "C:\s\install-bridge.ps1" -ExpectedSha256 "abc" -SourcePath "C:\a b\VRCRemoteTest.Bridge.exe" -TaskName "T" -StartNow')
Check 'rerun command after the replacement drops -SourcePath' (
    (Format-PwshCommand -ScriptPath 'C:\s\install-bridge.ps1' -Parameters (Get-InvocationParameter -Bound $bound -WithoutSource)) -ceq
    'pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "C:\s\install-bridge.ps1" -ExpectedSha256 "abc" -TaskName "T" -StartNow')
Check 'rerun command leaves out a switch that is off' (
    (Format-PwshCommand -ScriptPath '' -Parameters ([ordered]@{ ExpectedSha256 = 'abc'; StartNow = [switch]$false })) -ceq
    'pwsh.exe -NoProfile -ExecutionPolicy Bypass -File ".\install-bridge.ps1" -ExpectedSha256 "abc"')

$identity = 'PC\gra'
$sid = 'S-1-5-21-1-2-3-1001'
Check 'account: same name with the computer' (Test-CurrentUserAccount -Value 'pc\GRA' -IdentityName $identity -Sid $sid)
Check 'account: name without the computer' (Test-CurrentUserAccount -Value 'gra' -IdentityName $identity -Sid $sid)
Check 'account: SID' (Test-CurrentUserAccount -Value $sid -IdentityName $identity -Sid $sid)
Check 'account: someone else' (-not (Test-CurrentUserAccount -Value 'PC\other' -IdentityName $identity -Sid $sid))
Check 'account: empty' (-not (Test-CurrentUserAccount -Value '' -IdentityName $identity -Sid $sid))

# A non-elevated token: the user, Users, Authenticated Users, Everyone,
# Interactive. Administrators is deny-only there and not among the SIDs.
$tokenSids = @($sid, 'S-1-5-32-545', 'S-1-5-11', 'S-1-1-0', 'S-1-5-4')
$sddlCases = [ordered]@{
    # The task on the Windows PC on 2026-10-31, registered from an elevated shell.
    "O:BAG:${sid}D:(A;ID;0x1f019f;;;BA)(A;ID;0x1f019f;;;SY)(A;ID;FA;;;BA)(A;;FR;;;$sid)" = 'no'
    # A task the user registered without elevation.
    "O:${sid}G:S-1-5-21-1-2-3-513D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FA;;;$sid)" = 'yes'
    "D:(A;;0x10000;;;$sid)" = 'yes'
    "D:(A;;FRSD;;;$sid)" = 'yes'
    'D:(A;;GA;;;AU)' = 'yes'
    "D:(A;IO;FA;;;$sid)(A;;FR;;;$sid)" = 'no'
    "D:(D;;FA;;;$sid)(A;;FR;;;$sid)" = 'no'
    "O:DAG:DUD:(A;;FR;;;$sid)" = 'no'
    "D:(A;;WD;;;$sid)" = 'unknown'
    'D:(A;;FA;;;XX)' = 'unknown'
    "D:(OA;;FA;;;$sid)" = 'unknown'
    'O:BAD:NO_ACCESS_CONTROL' = 'unknown'
    'not an SDDL' = 'unknown'
    '' = 'unknown'
}
foreach ($sddl in $sddlCases.Keys) {
    $got = Get-SddlDeleteAccess -Sddl $sddl -Sids $tokenSids
    Check "delete right: '$sddl' is $($sddlCases[$sddl])" ($got -eq $sddlCases[$sddl]) "got $got"
}
$elevated = Get-SddlDeleteAccess -Sddl "O:BAG:${sid}D:(A;ID;FA;;;BA)(A;;FR;;;$sid)" -Sids ($tokenSids + 'S-1-5-32-544')
Check 'delete right: an elevated token can delete the elevated task' ($elevated -eq 'yes') "got $elevated"

function New-Cim {
    param([string]$Class, [hashtable]$Properties)

    $Properties['CimClass'] = [pscustomobject]@{ CimClassName = $Class }
    [pscustomobject]$Properties
}

$exe = 'C:\Users\gra\AppData\Local\Programs\VRCRemoteTest\Bridge\VRCRemoteTest.Bridge.exe'
$dir = 'C:\Users\gra\AppData\Local\Programs\VRCRemoteTest\Bridge'
$description = 'VRC Remote Test Bridge auto-start (managed by vrc-remote-test install-bridge.ps1)'
$wantAction = New-Cim 'MSFT_TaskExecAction' @{ Execute = $exe; Arguments = $null; WorkingDirectory = $dir }
$wantTrigger = New-Cim 'MSFT_TaskLogonTrigger' @{ UserId = $identity; Enabled = $true }
$wantPrincipal = New-Cim 'MSFT_TaskPrincipal2' @{ UserId = $identity; LogonType = 'Interactive'; RunLevel = 'Limited' }
$wantSettings = New-Cim 'MSFT_TaskSettings3' @{ MultipleInstances = 'IgnoreNew'; ExecutionTimeLimit = 'PT0S'; StartWhenAvailable = $true; Enabled = $true }

# Shaped like the v0.7.1 task reported on 2026-10-31: the same definition, read
# back with the user name only and the task left enabled or disabled.
function New-ExistingTask {
    param([scriptblock]$Change)

    $task = [pscustomobject]@{
        Description = $description
        Actions = @(New-Cim 'MSFT_TaskExecAction' @{ Execute = $exe; Arguments = ''; WorkingDirectory = $dir + '\' })
        Triggers = @(New-Cim 'MSFT_TaskLogonTrigger' @{ UserId = $identity; Enabled = $true })
        Principal = New-Cim 'MSFT_TaskPrincipal2' @{ UserId = 'gra'; LogonType = 'Interactive'; RunLevel = 'Limited' }
        Settings = New-Cim 'MSFT_TaskSettings3' @{ MultipleInstances = 'IgnoreNew'; ExecutionTimeLimit = 'PT0S'; StartWhenAvailable = $true; Enabled = $false }
    }
    if ($Change) {
        & $Change $task
    }
    $task
}

function Get-Difference {
    param($Existing)

    @(Get-TaskDefinitionDifference -Existing $Existing -Action $wantAction -Trigger $wantTrigger -Principal $wantPrincipal `
            -Settings $wantSettings -Description $description -IdentityName $identity -Sid $sid)
}

$same = Get-Difference (New-ExistingTask)
Check 'task: the same definition has no difference (a disabled task stays as it is)' ($same.Count -eq 0) ($same -join "`n")

$cases = [ordered]@{
    'another executable' = { param($t) $t.Actions[0].Execute = 'C:\elsewhere\VRCRemoteTest.Bridge.exe' }
    'arguments' = { param($t) $t.Actions[0].Arguments = 'pair' }
    'another working directory' = { param($t) $t.Actions[0].WorkingDirectory = 'C:\' }
    'two actions' = { param($t) $t.Actions = @($t.Actions[0], $t.Actions[0]) }
    'a time trigger' = { param($t) $t.Triggers = @(New-Cim 'MSFT_TaskTimeTrigger' @{ UserId = $null; Enabled = $true }) }
    'a trigger for another user' = { param($t) $t.Triggers[0].UserId = 'PC\other' }
    'a disabled trigger' = { param($t) $t.Triggers[0].Enabled = $false }
    'no trigger' = { param($t) $t.Triggers = @() }
    'highest run level' = { param($t) $t.Principal.RunLevel = 'Highest' }
    'password logon' = { param($t) $t.Principal.LogonType = 'Password' }
    'another principal' = { param($t) $t.Principal.UserId = 'SYSTEM' }
    'parallel instances' = { param($t) $t.Settings.MultipleInstances = 'Parallel' }
    'a 72 hour limit' = { param($t) $t.Settings.ExecutionTimeLimit = 'PT72H' }
    'no start when available' = { param($t) $t.Settings.StartWhenAvailable = $false }
    'another description' = { param($t) $t.Description = $description + ' (old)' }
}
foreach ($name in $cases.Keys) {
    $found = Get-Difference (New-ExistingTask $cases[$name])
    # A time trigger also has no user, so it can show up as more than one line.
    Check "task: $name is a difference" ($found.Count -ge 1) ($found -join "`n")
}

$rerun = 'pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "C:\s\install-bridge.ps1" -ExpectedSha256 "abc" -StartNow'
$removed = Format-StopState -ExeReplaced $true -ExePath $exe -TaskState 'removed' -TaskName 'VRCRemoteTestBridge' -TaskPath '\' -RerunCommand $rerun
Check 'stop state: replaced executable and removed task' (
    $removed.Contains('置き換え済み') -and $removed.Contains('登録し直せていない') -and $removed.Contains($rerun) -and -not $removed.Contains('Start-ScheduledTask'))
$original = Format-StopState -ExeReplaced $true -ExePath $exe -TaskState 'original' -TaskName 'VRCRemoteTestBridge' -TaskPath '\' -RerunCommand $rerun
Check 'stop state: original task left, with how to start it now' (
    $original.Contains('元のまま') -and $original.Contains("Start-ScheduledTask -TaskName 'VRCRemoteTestBridge' -TaskPath '\'"))
$kept = Format-StopState -ExeReplaced $false -ExePath $exe -TaskState 'kept' -TaskName 'VRCRemoteTestBridge' -TaskPath '\' -RerunCommand $rerun
Check 'stop state: executable not replaced' ($kept.Contains('置き換えていない') -and $kept.Contains('元のタスクをそのまま残した'))

Write-Host ''
if ($failures -gt 0) {
    Write-Host "$failures check(s) failed."
    exit 1
}
Write-Host 'All checks passed.'
