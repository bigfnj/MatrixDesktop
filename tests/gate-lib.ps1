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
