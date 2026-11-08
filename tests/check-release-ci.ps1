#requires -Version 5
<#
.SYNOPSIS
    Fails unless CI's job passed on the commit a release is about to publish.

.DESCRIPTION
    release.yml runs on tags and ci.yml never does, so a tag on an untested or failing commit
    used to publish a release. This reads the commit's check runs for CI's job, takes the newest
    (gate-lib.ps1, Get-CiCheckVerdict) and waits while it is queued or running, because a tag
    pushed together with its commit starts the release before CI finishes.

    Exit 0 with a "PASS CI result" line, 1 with a "FAIL CI result" line.

.PARAMETER CheckName
    ci.yml's job name. GitHub names each job's check run after the job.
#>
[CmdletBinding()]
param(
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$Sha,
    [string]$CheckName = 'Build & Publish Smoke Test',
    [double]$TimeoutMinutes = 35,
    [double]$MissingGraceMinutes = 3,
    [int]$PollSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'gate-lib.ps1')

if (-not $Repository -or -not $Sha) {
    Write-Host 'FAIL CI result: pass -Repository owner/name and -Sha <commit>'
    exit 1
}
# Windows PowerShell 5.1 may not offer TLS 1.2 by default, and api.github.com requires it.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$headers = @{ 'Accept' = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)" }
$uri = "https://api.github.com/repos/$Repository/commits/$Sha/check-runs?check_name=$([uri]::EscapeDataString($CheckName))&filter=all&per_page=100"
$subject = "'$CheckName' on $Sha"

$clock = [Diagnostics.Stopwatch]::StartNew()
while ($true) {
    $response = Invoke-RestMethod -Uri $uri -Headers $headers
    $verdict = Get-CiCheckVerdict -CheckRuns @($response.check_runs)
    $decision = Get-CiWaitDecision -Outcome $verdict.Outcome -ElapsedMinutes $clock.Elapsed.TotalMinutes `
        -TimeoutMinutes $TimeoutMinutes -MissingGraceMinutes $MissingGraceMinutes
    if ($decision -eq 'pass') {
        Write-Host "PASS CI result: $subject, $($verdict.Detail)"
        exit 0
    }
    if ($decision -eq 'fail') {
        $minutes = '{0:N1}' -f $clock.Elapsed.TotalMinutes
        Write-Host ("FAIL CI result: $subject is $($verdict.Outcome) ($($verdict.Detail)) after $minutes min. " +
            'CI runs on pushes to main and on pull requests; release a commit it passed on, or re-run this workflow once it has.')
        exit 1
    }
    Write-Host "waiting: $subject is $($verdict.Outcome) ($($verdict.Detail))"
    Start-Sleep -Seconds $PollSeconds
}
