#requires -Version 5
<#
.SYNOPSIS
    Checks that both csproj <Version> values, the argument guide header and README's current
    source version agree, and with a release tag that they equal it.

.DESCRIPTION
    release.yml runs this with -Tag before it builds anything, and the gate runs it with no tag
    on every run. At tags v1.0.3 and v1.0.4 both csproj files, the guide and README said 1.0.2
    while the release stamped the binaries from the tag; the comparison lives in gate-lib.ps1
    (Get-ReleaseVersionReport) so both callers run the same code.

    Without -Tag the GitHub ref name is used only when GITHUB_REF_TYPE is 'tag': ci.yml runs the
    gate on main pushes and pull requests, where the ref name is 'main' or '<n>/merge'.

    Exit 0 with a "PASS release version" line, 1 with one "FAIL release version" line per problem.

.PARAMETER Tag
    The release tag, for example v1.2.0. One leading 'v' is stripped.

.PARAMETER RepoRoot
    The tree to check. Defaults to this script's repository.
#>
[CmdletBinding()]
param(
    [string]$Tag,
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $here }
. (Join-Path $here 'gate-lib.ps1')

$resolved = Resolve-ReleaseTag -Tag $Tag
$report = Get-ReleaseVersionReport -RepoRoot $RepoRoot -Tag $resolved
if (@($report.Problems).Count -gt 0) {
    foreach ($problem in $report.Problems) { Write-Host "FAIL release version: $problem" }
    exit 1
}
$files = @($report.Versions.Keys) -join ', '
$version = @($report.Versions.Values)[0]
if ($resolved) {
    Write-Host "PASS release version: the tag $resolved matches $version in $files"
} else {
    Write-Host "PASS release version: no release tag; $files all say $version"
}
exit 0
