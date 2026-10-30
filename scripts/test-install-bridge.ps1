# Checks scripts/install-bridge.ps1 without Windows. The file must stay readable
# by Windows PowerShell 5.1 (UTF-8 with a BOM, no PowerShell 7-only syntax) so
# that 5.1 reaches the "run me with pwsh.exe" message instead of a parse error,
# and its task-definition comparison, stop-state report and rerun commands are
# exercised with stand-in objects. Task Scheduler and the file-system checks
# need Windows and are not covered here.
# Run from anywhere: pwsh -NoProfile -File scripts/test-install-bridge.ps1

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

# --- The file as Windows PowerShell 5.1 reads it ---

$bytes = [System.IO.File]::ReadAllBytes($scriptPath)
Check 'saved as UTF-8 with a BOM' ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)

$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
Check 'parses without errors' ($parseErrors.Count -eq 0) (($parseErrors | ForEach-Object { "$($_.Extent.StartLineNumber): $($_.Message)" }) -join "`n")

Check 'has no #Requires (5.1 would refuse in English before the pwsh.exe message)' ($null -eq $ast.ScriptRequirements)

$ps7Kinds = @('AndAnd', 'OrOr', 'QuestionMark', 'QuestionQuestionEquals', 'QuestionQuestion', 'QuestionDot', 'QuestionLBracket')
$ps7Tokens = @($tokens | Where-Object { "$($_.Kind)" -in $ps7Kinds })
Check 'uses no PowerShell 7-only operators' ($ps7Tokens.Count -eq 0) (($ps7Tokens | ForEach-Object { "$($_.Extent.StartLineNumber): $($_.Text)" }) -join "`n")

$statements = @($ast.EndBlock.Statements)
$gateIndex = -1
for ($i = 0; $i -lt $statements.Count; $i++) {
    if ($statements[$i].Extent.Text -match '^if \(\$PSVersionTable\.PSVersion\.Major -lt 7\)') {
        $gateIndex = $i
        break
    }
}
$beforeGate = @($statements | Select-Object -First ([Math]::Max($gateIndex, 0)))
Check 'the version check comes first, after only function definitions' (
    $gateIndex -ge 0 -and @($beforeGate | Where-Object { $_ -isnot [System.Management.Automation.Language.FunctionDefinitionAst] }).Count -eq 0)

# Runs everything up to the version check in a child pwsh that pretends to be 5.1.
$fakeDir = Join-Path ([System.IO.Path]::GetTempPath()) ('install-bridge-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fakeDir | Out-Null
try {
    $gateEnd = $statements[$gateIndex].Extent.EndOffset
    $text = [System.IO.File]::ReadAllText($scriptPath).Substring(0, $gateEnd)
    $paramEnd = $ast.ParamBlock.Extent.EndOffset
    $fake = $text.Substring(0, $paramEnd) + "`n`$FakeVersionTable = @{ PSVersion = [version]'5.1.22621.4391' }`n" +
        $text.Substring($paramEnd).Replace('$PSVersionTable', '$FakeVersionTable') + "`nWrite-Host 'PAST THE GATE'`n"
    $fakePath = Join-Path $fakeDir 'install-bridge.ps1'
    [System.IO.File]::WriteAllText($fakePath, $fake, [System.Text.UTF8Encoding]::new($true))

    $env:LOCALAPPDATA = $fakeDir
    $hash = 'AB' * 32
    $output = & pwsh -NoProfile -File $fakePath -ExpectedSha256 $hash -SourcePath 'C:\Users\U S\Downloads\VRCRemoteTest.Bridge.exe' -StartNow 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    Check 'on 5.1 it stops with exit code 1' ($exitCode -eq 1) $output
    Check 'on 5.1 it does not go past the version check' ($output -notmatch 'PAST THE GATE') $output
    $expected = "pwsh.exe -NoProfile -ExecutionPolicy Bypass -File `"$fakePath`" -ExpectedSha256 `"$hash`" -SourcePath `"C:\Users\U S\Downloads\VRCRemoteTest.Bridge.exe`" -StartNow"
    Check 'on 5.1 it prints the pwsh.exe command with the given arguments' ($output.Contains($expected)) $output
}
finally {
    Remove-Item -LiteralPath $fakeDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- The functions, loaded from the script without running it ---

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
