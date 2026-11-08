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

# ---------------------------------------------------------------- release version and CI

# A release states its version in three files: both csproj <Version> elements and the guide
# header that --help-full and the configurator's ? button show. At tags v1.0.3 and v1.0.4 all
# three said 1.0.2 while release.yml stamped the binaries from the tag, and a check that only
# compared the files with each other passed at both. With -Tag (one leading 'v' stripped) each
# file must equal the tag; without it the files must agree with each other. Problems name the
# file, so an empty Problems list means consistent.
function Get-ReleaseVersionReport {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [string]$Tag
    )
    $sources = [ordered]@{
        'MatrixDesktop\MatrixDesktop.csproj'                         = '<Version>([^<]+)</Version>'
        'MatrixDesktopConfigurator\MatrixDesktopConfigurator.csproj' = '<Version>([^<]+)</Version>'
        'MatrixDesktop_Argument_Guide.txt'                           = 'ARGUMENT GUIDE \(desktop wrapper v([0-9][^)]*)\)'
    }
    $versions = [ordered]@{}
    $problems = @()
    foreach ($relative in $sources.Keys) {
        $path = Join-Path $RepoRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $problems += "${relative}: not found"; continue }
        $m = [regex]::Match([IO.File]::ReadAllText($path), $sources[$relative])
        if (-not $m.Success) { $problems += "${relative}: no version matches $($sources[$relative])"; continue }
        $versions[$relative] = $m.Groups[1].Value.Trim()
    }
    if ($Tag) {
        $want = $Tag -creplace '^v', ''
        foreach ($relative in $versions.Keys) {
            if ($versions[$relative] -cne $want) { $problems += "${relative}: says $($versions[$relative]), the tag $Tag says $want" }
        }
    } else {
        $values = @($versions.Values)
        if (@($values | Where-Object { $_ -cne $values[0] }).Count -gt 0) {
            $problems += 'the files disagree: ' + (@($versions.Keys | ForEach-Object { "$_ says $($versions[$_])" }) -join ', ')
        }
    }
    return [pscustomobject]@{ Versions = $versions; Problems = $problems }
}

# The tag a version check compares against. An explicit -Tag wins. Otherwise the GitHub ref
# name counts only when GITHUB_REF_TYPE says the run is for a tag: ci.yml runs the gate, and so
# this check, on main pushes and pull requests, where the ref name is 'main' or '<n>/merge'.
function Resolve-ReleaseTag {
    param([string]$Tag)
    if ($Tag) { return $Tag }
    if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME) { return $env:GITHUB_REF_NAME }
    return $null
}

# release.yml must compare the tag with the version files, and require CI's result on the
# tagged commit, before it publishes anything; a check placed after the first publish, or
# commented out, would let a mismatched or red commit ship. Asserts the ORDER of the lines
# with their comments removed, not that the text appears somewhere.
function Get-ReleaseWorkflowOrderProblems {
    param([Parameter(Mandatory = $true)][string]$YamlText)
    $patterns = [ordered]@{
        'tag version check' = 'check-release-version\.ps1\b.*\s-Tag\b'
        'CI result check'   = 'check-release-ci\.ps1\b'
        'publish'           = '\bdotnet\s+publish\b'
    }
    $first = @{}
    $lines = $YamlText -split "`r?`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        # A comment starts at a '#' that opens the line or follows whitespace, and trailing
        # ones count: release.yml's 'checks: read  # check-release-ci.ps1 reads ...' sits
        # above every step, so matching it passed the order check wherever the step went.
        $code = $lines[$i] -replace '(^|\s)#.*$', ''
        foreach ($key in $patterns.Keys) {
            if (-not $first.ContainsKey($key) -and $code -match $patterns[$key]) { $first[$key] = $i + 1 }
        }
    }
    $problems = @()
    if (-not $first.ContainsKey('publish')) { $problems += "release.yml has no 'dotnet publish' line, so the order cannot be checked" }
    foreach ($key in @('tag version check', 'CI result check')) {
        if (-not $first.ContainsKey($key)) {
            $problems += "release.yml never runs the $key"
        } elseif ($first.ContainsKey('publish') -and $first[$key] -gt $first['publish']) {
            $problems += "release.yml runs the $key at line $($first[$key]), after its first 'dotnet publish' at line $($first['publish'])"
        }
    }
    return $problems
}

# release.yml runs on tags and ci.yml never does, so a tag on an untested or red commit used
# to publish. The newest run of CI's job on that commit decides, as GitHub shows it on the
# commit: 'ok', 'fail', 'pending' (queued or running) or 'missing' (CI never ran there).
function Get-CiCheckVerdict {
    param([AllowEmptyCollection()][object[]]$CheckRuns = @())
    $runs = @($CheckRuns | Where-Object { $null -ne $_ } | Sort-Object -Property { [long]$_.id } -Descending)
    if ($runs.Count -eq 0) { return [pscustomobject]@{ Outcome = 'missing'; Detail = 'no run of that check exists for the commit' } }
    $latest = $runs[0]
    if ($latest.status -ne 'completed') { return [pscustomobject]@{ Outcome = 'pending'; Detail = "run $($latest.id) is $($latest.status)" } }
    if ($latest.conclusion -eq 'success') { return [pscustomobject]@{ Outcome = 'ok'; Detail = "run $($latest.id) concluded success" } }
    return [pscustomobject]@{ Outcome = 'fail'; Detail = "run $($latest.id) concluded $($latest.conclusion)" }
}

# Whether tests\check-release-ci.ps1 polls again. A tag pushed together with its commit starts
# the release while CI is queued or running, so 'pending' waits up to the timeout and
# 'missing' waits a short grace for CI's run to appear. Returns 'pass', 'wait' or 'fail'.
function Get-CiWaitDecision {
    param(
        [Parameter(Mandatory = $true)][string]$Outcome,
        [double]$ElapsedMinutes,
        [double]$TimeoutMinutes,
        [double]$MissingGraceMinutes
    )
    if ($Outcome -eq 'ok') { return 'pass' }
    if ($Outcome -eq 'pending' -and $ElapsedMinutes -lt $TimeoutMinutes) { return 'wait' }
    if ($Outcome -eq 'missing' -and $ElapsedMinutes -lt $MissingGraceMinutes) { return 'wait' }
    return 'fail'
}

# ---------------------------------------------------------------- publish profile parity

# The property elements of every PropertyGroup in an MSBuild file, as Name/Value pairs.
# Parsed as XML, so a property named inside a comment does not count.
function Get-MsBuildProperties {
    param([Parameter(Mandatory = $true)][string]$Path)
    $doc = New-Object Xml.XmlDocument
    $doc.Load($Path)
    return @($doc.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*") |
        Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element } |
        ForEach-Object { [pscustomobject]@{ Name = $_.LocalName; Value = $_.InnerText.Trim() } })
}

# A local publish profile must not change the build beyond what the gate and release.yml
# pass, or the documented local publish differs silently from the shipped one: both
# framework-dependent profiles set InvariantGlobalization, which nothing that ships sets.
# Each profile property needs the same value from a -p:Name=Value flag, from -Implied (what
# the publish command line itself sets: -c, -r, --no-self-contained, and SDK defaults the
# gate relies on) or from the matching csproj. -Ignore holds the properties that only say
# where or how the profile writes. Each problem names the profile.
function Get-PublishProfileParityProblems {
    param(
        [Parameter(Mandatory = $true)][string]$ProfilePath,
        [Parameter(Mandatory = $true)][string]$CsprojPath,
        [AllowEmptyCollection()][string[]]$PublishFlags = @(),
        [hashtable]$Implied = @{},
        [string[]]$Ignore = @('PublishProtocol', 'PublishDir'),
        [string]$Label = $ProfilePath
    )
    $given = @{}
    foreach ($flag in $PublishFlags) {
        $m = [regex]::Match($flag, '^-p:([^=]+)=(.*)$')
        if ($m.Success) { $given[$m.Groups[1].Value] = $m.Groups[2].Value }
    }
    foreach ($name in $Implied.Keys) { if (-not $given.ContainsKey($name)) { $given[$name] = [string]$Implied[$name] } }
    $csproj = @(Get-MsBuildProperties -Path $CsprojPath)
    $problems = @()
    foreach ($property in @(Get-MsBuildProperties -Path $ProfilePath)) {
        if ($Ignore -contains $property.Name) { continue }
        if (@($csproj | Where-Object { $_.Name -eq $property.Name -and $_.Value -eq $property.Value }).Count -gt 0) { continue }
        if ($given.ContainsKey($property.Name)) {
            if ($given[$property.Name] -eq $property.Value) { continue }
            $problems += "${Label}: $($property.Name)=$($property.Value), but the gate and release.yml publish with $($given[$property.Name])"
        } else {
            $problems += "${Label}: $($property.Name)=$($property.Value) is set only here; the gate and release.yml publish without it"
        }
    }
    return $problems
}

# One runtimeOptions.configProperties value from a published runtimeconfig.json, or $null
# when it is absent. Safe under Set-StrictMode, which throws on a missing JSON property.
function Get-RuntimeConfigOption {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )
    $node = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
    foreach ($step in @('runtimeOptions', 'configProperties', $Name)) {
        if ($null -eq $node) { return $null }
        $member = $node.PSObject.Properties[$step]
        if (-not $member) { return $null }
        $node = $member.Value
    }
    return $node
}

# Runs one child harness with stdout and stderr redirected to $Log and "$Log.err", the way
# every gate case runs, and classifies it. The self-tests call this same function with stub
# scripts, so they exercise the gate's real redirection and verdict path. -Environment sets
# variables for the child only ($null removes one) and restores them afterwards, because
# Start-Process -Environment needs PowerShell 7.4.
function Invoke-GateChildCase {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$Log,
        [string]$Witness,
        [hashtable]$Environment = @{}
    )
    $saved = @{}
    foreach ($name in $Environment.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        Set-ProcessEnvironmentVariable -Name $name -Value $Environment[$name]
    }
    try {
        $run = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -Wait -PassThru -NoNewWindow `
            -RedirectStandardOutput $Log -RedirectStandardError "$Log.err"
    } finally {
        foreach ($name in $saved.Keys) { Set-ProcessEnvironmentVariable -Name $name -Value $saved[$name] }
    }
    return Get-GateCaseVerdict -ExitCode $run.ExitCode -Log $Log -Witness $Witness
}

# $null removes the variable. PowerShell hands a .NET string parameter '' for $null, and
# .NET (pwsh 7) keeps a variable set to '' where .NET Framework (5.1) removed it, so a removal
# has to pass [NullString]::Value; otherwise pwsh leaves every removed variable behind, empty.
function Set-ProcessEnvironmentVariable {
    param([Parameter(Mandatory = $true)][string]$Name, $Value)
    if ($null -eq $Value) { [Environment]::SetEnvironmentVariable($Name, [NullString]::Value, 'Process') }
    else { [Environment]::SetEnvironmentVariable($Name, [string]$Value, 'Process') }
}

# A regression case whose correct outcome IS the cannot-verify exit. uc3d-calibration.py with
# the WebGPU adapter hidden exited 1, a product failure, while README promised unverified
# coverage (AUD-59). So exit 2 with the witness passes, any other exit fails, and a
# Forbidden line (a WebGPU pass with no adapter) fails too.
function Get-ExpectedCannotVerifyVerdict {
    param(
        [Parameter(Mandatory = $true)][int]$ExitCode,
        [Parameter(Mandatory = $true)][string]$Log,
        [Parameter(Mandatory = $true)][string]$Witness,
        [string]$Forbidden
    )
    $text = ''
    foreach ($path in @($Log, "$Log.err")) {
        if (Test-Path -LiteralPath $path -PathType Leaf) { $text += [IO.File]::ReadAllText($path) + "`n" }
    }
    $where = "see $Log and $Log.err"
    if ($ExitCode -ne 2) {
        return [pscustomobject]@{ Outcome = 'fail'; ExitCode = $ExitCode; Detail = "expected the cannot-verify exit 2, observed exit $ExitCode; $where" }
    }
    $m = [regex]::Match($text, $Witness)
    if (-not $m.Success) {
        return [pscustomobject]@{ Outcome = 'fail'; ExitCode = $ExitCode; Detail = "exit 2 without the witness '$Witness'; $where" }
    }
    if ($Forbidden) {
        $bad = [regex]::Match($text, $Forbidden)
        if ($bad.Success) {
            return [pscustomobject]@{ Outcome = 'fail'; ExitCode = $ExitCode; Detail = "printed '$((($text.Substring($bad.Index)) -split "`r?`n")[0].Trim())' as well; $where" }
        }
    }
    return [pscustomobject]@{ Outcome = 'ok'; ExitCode = $ExitCode; Detail = (($text.Substring($m.Index)) -split "`r?`n")[0].Trim() }
}

# One tier-1 Python case: Arguments, optional Environment (for the child only) and optional
# ExpectCannotVerify/Forbidden, which switch the verdict to Get-ExpectedCannotVerifyVerdict.
function Invoke-GatePythonCase {
    param(
        [Parameter(Mandatory = $true)][string]$Python,
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][hashtable]$Case,
        [Parameter(Mandatory = $true)][string]$Log
    )
    $arguments = '"' + $ScriptPath + '"'
    if ($Case.ContainsKey('Arguments')) { $arguments += $Case.Arguments }
    $environment = if ($Case.ContainsKey('Environment')) { $Case.Environment } else { @{} }
    $verdict = Invoke-GateChildCase -FilePath $Python -ArgumentList $arguments -Log $Log -Environment $environment
    if (-not $Case.ContainsKey('ExpectCannotVerify')) { return $verdict }
    $forbidden = if ($Case.ContainsKey('Forbidden')) { $Case.Forbidden } else { '' }
    return Get-ExpectedCannotVerifyVerdict -ExitCode $verdict.ExitCode -Log $Log -Witness $Case.ExpectCannotVerify -Forbidden $forbidden
}
