#requires -Version 5
<#
.SYNOPSIS
    Self-tests for tests\gate-lib.ps1. The gate's tier 1 runs this file under both
    powershell.exe 5.1 and pwsh 7, because the gate itself is documented to run under both.

.DESCRIPTION
    Exit 0 with a "GATE-LIB SELF-TEST PASSED" line, 1 when any case failed, 2 with a
    "CANNOT VERIFY" line when a case could not run (for example, no Python for the stubs).

.PARAMETER Python
    Interpreter for the Python stub cases. The gate passes the one it found.

.PARAMETER Only
    Run only the cases whose name contains this text.
#>
[CmdletBinding()]
param(
    [string]$Python,
    [string]$Only
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'gate-lib.ps1')

$script:Passed = 0
$script:Failed = 0
$script:Unverified = 0
$shell = "$($PSVersionTable.PSEdition) $($PSVersionTable.PSVersion)"
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('md-gate-lib-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null

if (-not $Python) {
    $candidate = Get-Command python -ErrorAction SilentlyContinue
    if ($candidate) { $Python = $candidate.Source }
}

function Invoke-TestCase {
    param([string]$Name, [scriptblock]$Body)
    if ($Only -and $Name -notlike "*$Only*") { return }
    try {
        $result = @(& $Body)
        if ($result.Count -gt 0 -and "$($result[-1])" -like 'CANNOT VERIFY*') {
            $script:Unverified++
            Write-Host "$($result[-1])"
            return
        }
        $script:Passed++
        Write-Host "PASS $Name"
    } catch {
        $script:Failed++
        Write-Host "FAIL $Name :: $($_.Exception.Message)"
    }
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Because)
    if ($Expected -ne $Actual) { throw "$Because (expected '$Expected', observed '$Actual')" }
}

function New-StubFile {
    param([string]$Name, [string]$Text)
    $path = Join-Path $scratch $Name
    [IO.File]::WriteAllText($path, $Text, (New-Object Text.UTF8Encoding($false)))
    return $path
}

function New-LogPath {
    param([string]$Name)
    return Join-Path $scratch ($Name + '.log')
}

try {
    # ------------------------------------------------------------ AUD-25 verdicts

    Invoke-TestCase 'AUD-25 (a) a bare Python exit 2 (argparse usage error or missing script) is a harness FAIL' {
        if (-not $Python) { return 'CANNOT VERIFY AUD-25 (a): no Python interpreter for the stubs' }
        $stub = New-StubFile 'argparse_stub.py' ("import argparse`n" +
            "parser = argparse.ArgumentParser()`n" +
            "parser.add_argument('--renderer', choices=['regl'])`n" +
            "parser.parse_args()`n" +
            "print('PASS stub ran')`n")
        $legs = @(
            @{ Name = 'unknown-option'; Arguments = '"' + $stub + '" --renderr regl' },
            # The usage text quotes the bad value, so a witness word inside an argparse error
            # must not count: the witness has to start its own line.
            @{ Name = 'witness-word-in-usage-error'; Arguments = '"' + $stub + '" --renderer UNCHECKED' },
            @{ Name = 'missing-script'; Arguments = '"' + (Join-Path $scratch 'no-such-harness.py') + '"' }
        )
        foreach ($leg in $legs) {
            $log = New-LogPath ('a-' + $leg.Name)
            $verdict = Invoke-GateChildCase -FilePath $Python -ArgumentList $leg.Arguments -Log $log
            Assert-Equal 2 $verdict.ExitCode "$($leg.Name): Python should exit 2"
            Assert-Equal 'fail' $verdict.Outcome "$($leg.Name): an exit 2 without a cannot-verify witness must be a FAIL"
            if ($verdict.Detail -notlike "*$log.err*") { throw "$($leg.Name): the verdict does not name $log.err ($($verdict.Detail))" }
        }
    }

    Invoke-TestCase 'AUD-25 (b) CANNOT VERIFY on stdout plus exit 2 is UNCHECKED' {
        if (-not $Python) { return 'CANNOT VERIFY AUD-25 (b): no Python interpreter for the stubs' }
        $stub = New-StubFile 'witness_stdout.py' ("print('CANNOT VERIFY WebGPU: synthetic stub has no adapter', flush=True)`n" +
            "raise SystemExit(2)`n")
        $log = New-LogPath 'b-stdout'
        $verdict = Invoke-GateChildCase -FilePath $Python -ArgumentList ('"' + $stub + '"') -Log $log
        Assert-Equal 'unchecked' $verdict.Outcome 'a printed CANNOT VERIFY witness with exit 2 is an environment gap'
        if ($verdict.Detail -notlike 'CANNOT VERIFY WebGPU*' -or $verdict.Detail -notlike "*$log.err*") {
            throw "the UNCHECKED reason must quote the witness and name $log.err ($($verdict.Detail))"
        }
    }

    Invoke-TestCase "AUD-25 (b') the same witness on stderr is UNCHECKED" {
        if (-not $Python) { return "CANNOT VERIFY AUD-25 (b'): no Python interpreter for the stubs" }
        $stub = New-StubFile 'witness_stderr.py' ("import sys`n" +
            "print('CANNOT VERIFY WebGPU: synthetic stub has no adapter', file=sys.stderr, flush=True)`n" +
            "raise SystemExit(2)`n")
        $log = New-LogPath 'b-stderr'
        $verdict = Invoke-GateChildCase -FilePath $Python -ArgumentList ('"' + $stub + '"') -Log $log
        Assert-Equal 'unchecked' $verdict.Outcome 'a witness printed on stderr counts as well'
    }

    Invoke-TestCase 'AUD-25 (c) exit 0 is ok' {
        if (-not $Python) { return 'CANNOT VERIFY AUD-25 (c): no Python interpreter for the stubs' }
        $stub = New-StubFile 'passing.py' "print('PASS synthetic stub')`n"
        $log = New-LogPath 'c-ok'
        $verdict = Invoke-GateChildCase -FilePath $Python -ArgumentList ('"' + $stub + '"') -Log $log
        Assert-Equal 'ok' $verdict.Outcome 'exit 0 is a pass'
    }

    Invoke-TestCase 'AUD-25 (d) exit 0 without a required execution witness is a FAIL' {
        $log = New-LogPath 'd-witness'
        [IO.File]::WriteAllText($log, "nothing ran`n")
        [IO.File]::WriteAllText("$log.err", '')
        $missing = Get-GateCaseVerdict -ExitCode 0 -Log $log -Witness '(?m)^SELF-TEST PASSED'
        Assert-Equal 'fail' $missing.Outcome 'exit 0 without the witness proves nothing ran'
        [IO.File]::WriteAllText($log, "SELF-TEST PASSED (3 cases)`n")
        $present = Get-GateCaseVerdict -ExitCode 0 -Log $log -Witness '(?m)^SELF-TEST PASSED'
        Assert-Equal 'ok' $present.Outcome 'exit 0 with the witness is a pass'
    }

    # ------------------------------------------------------------ AUD-27 crash dumps

    function New-DumpFolder {
        param([string]$Name, [datetime]$LaunchedAt, [int]$OldCount)
        $dir = Join-Path $scratch $Name
        New-Item -ItemType Directory -Path $dir | Out-Null
        # Old dumps from other processes, written well before the case launched.
        for ($i = 0; $i -lt $OldCount; $i++) {
            $old = Join-Path $dir ("MatrixDesktop-20261101-0000{0:D2}-pid{1}.dmp" -f $i, (7000 + $i))
            [IO.File]::WriteAllBytes($old, [byte[]](1, 2, 3))
            (Get-Item -LiteralPath $old).LastWriteTime = $LaunchedAt.AddHours(-1).AddMinutes($i)
        }
        return $dir
    }

    function Add-Dump {
        param([string]$Dir, [string]$Name, [datetime]$WrittenAt)
        $path = Join-Path $Dir $Name
        [IO.File]::WriteAllBytes($path, [byte[]](1, 2, 3))
        (Get-Item -LiteralPath $path).LastWriteTime = $WrittenAt
        return $path
    }

    Invoke-TestCase 'AUD-27 (A) the smoke pid''s dump is found when the retention cap keeps the folder count at 10' {
        $launchedAt = Get-Date
        $dir = New-DumpFolder 'dumps-a' $launchedAt 10
        # The writer prunes the oldest before writing, so the folder-wide count stays at 10.
        Get-ChildItem -LiteralPath $dir -Filter *.dmp | Sort-Object LastWriteTime | Select-Object -First 1 | Remove-Item
        $written = Add-Dump $dir 'MatrixDesktop-20261108-120000-pid4242.dmp' $launchedAt.AddSeconds(4)
        Assert-Equal 10 @(Get-ChildItem -LiteralPath $dir -Filter *.dmp).Count 'the simulated writer keeps the folder at the cap'
        $found = @(Get-DumpsForPid -DumpDir $dir -ProcessId 4242 -Since $launchedAt)
        Assert-Equal 1 $found.Count 'exactly the smoke pid''s new dump is attributed'
        Assert-Equal $written $found[0].FullName 'the attributed dump is the one the smoke pid wrote'
    }

    Invoke-TestCase 'AUD-27 (B) a new dump from another pid, or a longer pid with the same digits, is not the smoke pid''s' {
        $launchedAt = Get-Date
        $dir = New-DumpFolder 'dumps-b' $launchedAt 3
        [void](Add-Dump $dir 'MatrixDesktop-20261108-120000-pid9999.dmp' $launchedAt.AddSeconds(2))
        [void](Add-Dump $dir 'MatrixDesktopConfigurator-20261108-120001-pid42420.dmp' $launchedAt.AddSeconds(3))
        Assert-Equal 0 @(Get-DumpsForPid -DumpDir $dir -ProcessId 4242 -Since $launchedAt).Count 'another process''s crash must not fail this case'
    }

    Invoke-TestCase 'AUD-27 (C) an old dump that a previous process with the same pid wrote is ignored' {
        $launchedAt = Get-Date
        $dir = New-DumpFolder 'dumps-c' $launchedAt 2
        [void](Add-Dump $dir 'MatrixDesktop-20261101-090000-pid4242.dmp' $launchedAt.AddHours(-2))
        Assert-Equal 0 @(Get-DumpsForPid -DumpDir $dir -ProcessId 4242 -Since $launchedAt).Count 'a dump older than the launch belongs to an earlier process'
        $suffixed = Add-Dump $dir 'MatrixDesktop-20261108-120000-pid4242-2.dmp' $launchedAt.AddSeconds(-1)
        Assert-Equal $suffixed (@(Get-DumpsForPid -DumpDir $dir -ProcessId 4242 -Since $launchedAt))[0].FullName 'a uniqueness suffix after the pid stamp, inside the 3 s clock allowance, still counts'
    }

    Invoke-TestCase 'AUD-27 (D) a missing dump folder means no dump' {
        Assert-Equal 0 @(Get-DumpsForPid -DumpDir (Join-Path $scratch 'no-dumps-here') -ProcessId 4242 -Since (Get-Date)).Count 'no folder, no dump'
    }
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

$total = $script:Passed + $script:Failed + $script:Unverified
if ($total -eq 0) {
    Write-Host "GATE-LIB SELF-TEST SELECTED NO CASES under $shell"
    exit 1
}
if ($script:Failed -gt 0) {
    Write-Host "GATE-LIB SELF-TEST FAILED: $($script:Failed) of $total case(s) under $shell"
    exit 1
}
if ($script:Unverified -gt 0) {
    Write-Host "CANNOT VERIFY gate-lib self-test: $($script:Unverified) of $total case(s) could not run under $shell"
    exit 2
}
Write-Host "GATE-LIB SELF-TEST PASSED ($($script:Passed) cases under $shell)"
exit 0
