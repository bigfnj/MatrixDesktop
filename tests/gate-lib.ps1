#requires -Version 5
<#
.SYNOPSIS
    Decision logic for tests\run-gate.ps1, dot-sourced by the gate and by its self-tests.

.DESCRIPTION
    Functions only: loading this file has no side effects. Everything here runs under both
    Windows PowerShell 5.1 and PowerShell 7, because the gate is documented to run under both
    and tests\gate-lib.tests.ps1 exercises each function in each shell.
#>

# ---------------------------------------------------------------- child harness verdicts

# Python exits 2 for three unrelated reasons: a harness that printed "cannot verify" and
# returned 2 on purpose, an argparse usage error, and a script path that does not exist. Only
# the first is an environment gap. Treating every exit 2 as UNCHECKED let a broken harness
# argument remove a check from CI while CI stayed green, so exit 2 counts as UNCHECKED only
# when the child printed one of the witness words at the start of a line on stdout or stderr.
function Get-GateCaseVerdict {
    param(
        [Parameter(Mandatory = $true)][int]$ExitCode,
        [Parameter(Mandatory = $true)][string]$Log,
        # Optional regex that a passing run must print, so exit 0 cannot pass a child that
        # never ran its cases.
        [string]$Witness
    )
    $errLog = "$Log.err"
    $text = ''
    foreach ($path in @($Log, $errLog)) {
        if (Test-Path -LiteralPath $path -PathType Leaf) { $text += [IO.File]::ReadAllText($path) + "`n" }
    }
    $where = "see $Log and $errLog"
    if ($ExitCode -eq 0) {
        if ($Witness -and $text -notmatch $Witness) {
            return [pscustomobject]@{ Outcome = 'fail'; ExitCode = $ExitCode; Detail = "exit 0 without the execution witness '$Witness'; $where" }
        }
        return [pscustomobject]@{ Outcome = 'ok'; ExitCode = $ExitCode; Detail = '' }
    }
    if ($ExitCode -eq 2) {
        $m = [regex]::Match($text, '(?m)^[ \t]*(?:CANNOT VERIFY|UNVERIFIED|UNCHECKED)\b.*$')
        if ($m.Success) {
            return [pscustomobject]@{ Outcome = 'unchecked'; ExitCode = $ExitCode; Detail = "$($m.Value.Trim()); $where" }
        }
        return [pscustomobject]@{ Outcome = 'fail'; ExitCode = $ExitCode; Detail = "harness error: exit 2 without a cannot-verify witness; $where" }
    }
    return [pscustomobject]@{ Outcome = 'fail'; ExitCode = $ExitCode; Detail = "exit $ExitCode; $where" }
}

# ---------------------------------------------------------------- native commands

# Windows PowerShell 5.1 turns each line a native command writes to a redirected stderr into
# an ErrorRecord, and under the gate's ErrorActionPreference Stop the first one throws
# (NativeCommandError) before the caller can read the exit code. PowerShell 7.2+ never
# applies the preference to native stderr, so only the documented full-gate shell crashed.
# Runs the command with both streams discarded and returns its exit code; -1 if it never ran.
function Invoke-NativeQuiet {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @()
    )
    $ErrorActionPreference = 'Continue'
    $global:LASTEXITCODE = -1
    & $FilePath @ArgumentList 2>$null | Out-Null
    return $global:LASTEXITCODE
}

# The interpreter for the Python cases: the first candidate that imports Playwright, else
# the last one found without it, else $null.
function Find-GatePython {
    param([string[]]$Candidates)
    $withoutPlaywright = $null
    foreach ($candidate in $Candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $found = Get-Command $candidate -ErrorAction SilentlyContinue
        if (-not $found) { continue }
        if ((Invoke-NativeQuiet -FilePath $found.Source -ArgumentList @('-c', 'import playwright')) -eq 0) { return $found.Source }
        $withoutPlaywright = $found.Source
    }
    return $withoutPlaywright
}

# Runs node --check on a .mjs copy of each file (run-gate.ps1 explains why not the .js) and
# returns the names of every file that does not parse, not just the first.
function Get-UnparsableJsModules {
    param(
        [Parameter(Mandatory = $true)][string]$Node,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][IO.FileInfo[]]$Files
    )
    $bad = @()
    $mjsDir = Join-Path ([IO.Path]::GetTempPath()) ('md-gate-mjs-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $mjsDir | Out-Null
    try {
        foreach ($js in $Files) {
            $probe = Join-Path $mjsDir ($js.BaseName + '.mjs')
            Copy-Item -LiteralPath $js.FullName -Destination $probe -Force
            if ((Invoke-NativeQuiet -FilePath $Node -ArgumentList @('--check', $probe)) -ne 0) { $bad += $js.Name }
        }
    } finally {
        Remove-Item -LiteralPath $mjsDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    return $bad
}

# ---------------------------------------------------------------- crash dumps

# CrashDumpWriter names each dump '<label>-<stamp>-pid<ProcessId>.dmp' and prunes the shared
# folder to its newest files before every write, so a folder-wide count stops rising once the
# retention cap is reached: the old "count grew" check could never fail again after ten
# crashes, and it blamed a healthy case for any other MatrixDesktop process crashing
# meanwhile. Dumps are attributed by pid and time instead. Text after the pid stamp (a
# uniqueness suffix) is tolerated; a longer pid with the same prefix is not.
function Get-DumpsForPid {
    param(
        [Parameter(Mandatory = $true)][string]$DumpDir,
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][datetime]$Since
    )
    if (-not (Test-Path -LiteralPath $DumpDir -PathType Container)) { return @() }
    $pattern = '-pid' + $ProcessId + '(?!\d)[^\\/]*\.dmp$'
    $cutoff = $Since.AddSeconds(-3)
    return @(Get-ChildItem -LiteralPath $DumpDir -Filter *.dmp -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match $pattern -and $_.LastWriteTime -ge $cutoff })
}

# Runs one child harness with stdout and stderr redirected to $Log and "$Log.err", the way
# every gate case runs, and classifies it. The self-tests call this same function with stub
# scripts, so they exercise the gate's real redirection and verdict path.
function Invoke-GateChildCase {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$Log,
        [string]$Witness
    )
    $run = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -Wait -PassThru -NoNewWindow `
        -RedirectStandardOutput $Log -RedirectStandardError "$Log.err"
    return Get-GateCaseVerdict -ExitCode $run.ExitCode -Log $Log -Witness $Witness
}
