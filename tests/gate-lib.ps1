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
    # Exit 3 is the child's own harness error only with its 'HARNESS ERROR' line: a suite whose
    # browser launch failed argument verification (browser_launch.LaunchArgumentsError) prints
    # one and exits 3 (MDST-05). A bare exit 3 stays a FAIL, because it has other sources, such
    # as abort() in a native extension. So does an exit 3 that also printed a FAIL line: a FAIL
    # outranks a harness error (as in web-smoke, R3-26), and zmatrix-configurator.py reaches it
    # when its last check's full-Chromium launch raises after earlier checks failed.
    if ($ExitCode -eq 3) {
        $m = [regex]::Match($text, '(?m)^[ \t]*HARNESS ERROR\b.*$')
        if ($m.Success) {
            $failed = [regex]::Match($text, '(?m)^[ \t]*FAIL\b.*$')
            if ($failed.Success) {
                return [pscustomobject]@{ Outcome = 'fail'; ExitCode = $ExitCode; Detail = "exit 3, a FAIL beside a harness error: $($failed.Value.Trim()) | $($m.Value.Trim()); $where" }
            }
            return [pscustomobject]@{ Outcome = 'harness'; ExitCode = $ExitCode; Detail = "$($m.Value.Trim()); $where" }
        }
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

# A release states its version in four files: both csproj <Version> elements, the guide
# header that --help-full and the configurator's ? button show, and README's 'Current source
# version' line, which every release zip ships. At tags v1.0.3 and v1.0.4 all four said 1.0.2
# while release.yml stamped the binaries from the tag, and a check that only compared the files
# with each other passed at both. With -Tag (one leading 'v' stripped) each file must equal the
# tag; without it the files must agree with each other. Problems name the file, so an empty
# Problems list means consistent. README's 'source' is optional: it read 'Current version'
# before 1.2.0.
function Get-ReleaseVersionReport {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [string]$Tag
    )
    $sources = [ordered]@{
        'MatrixDesktop\MatrixDesktop.csproj'                         = '<Version>([^<]+)</Version>'
        'MatrixDesktopConfigurator\MatrixDesktopConfigurator.csproj' = '<Version>([^<]+)</Version>'
        'MatrixDesktop_Argument_Guide.txt'                           = 'ARGUMENT GUIDE \(desktop wrapper v([0-9][^)]*)\)'
        'README.md'                                                  = 'Current (?:source )?version: \*\*([0-9][^*]*)\*\*'
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

# ---------------------------------------------------------------- WebView2 profile

# The gate gives every case a private WEBVIEW2_USER_DATA_FOLDER, which WebView2 uses in place
# of the folder MainForm requests, so nothing on disk shows where the product would put its
# profile (AUD-54). MainForm logs 'WebView2 profile requested=<path> effective=<path>' once per
# initialisation. A case needs exactly one such INFO record for its pid, requesting $Requested
# and running on $Effective; paths compare case-insensitively after normalisation.
function Get-WebViewProfileVerdict {
    param(
        [AllowEmptyCollection()][object[]]$Records = @(),
        [Parameter(Mandatory = $true)][string]$Requested,
        [Parameter(Mandatory = $true)][string]$Effective
    )
    $pattern = '^WebView2 profile requested=(?<requested>.*?) effective=(?<effective>.*)$'
    $found = @($Records | Where-Object { $null -ne $_ -and $_.Level -eq 'INFO' -and $_.Msg -match $pattern })
    if ($found.Count -ne 1) {
        return [pscustomobject]@{ Outcome = 'fail'; Detail = "expected one 'WebView2 profile requested=... effective=...' record for the case pid, found $($found.Count)" }
    }
    $m = [regex]::Match($found[0].Msg, $pattern)
    $problems = @()
    if (-not (Test-SamePath $m.Groups['requested'].Value $Requested)) { $problems += "requested '$($m.Groups['requested'].Value)', expected '$Requested'" }
    if (-not (Test-SamePath $m.Groups['effective'].Value $Effective)) { $problems += "effective '$($m.Groups['effective'].Value)', expected '$Effective'" }
    if ($problems.Count -gt 0) { return [pscustomobject]@{ Outcome = 'fail'; Detail = $problems -join '; ' } }
    return [pscustomobject]@{ Outcome = 'ok'; Detail = "requested $Requested, effective $Effective" }
}

function Test-SamePath {
    param([string]$A, [string]$B)
    if ([string]::IsNullOrWhiteSpace($A) -or [string]::IsNullOrWhiteSpace($B)) { return $false }
    try {
        $fullA = [IO.Path]::GetFullPath($A).TrimEnd('\', '/')
        $fullB = [IO.Path]::GetFullPath($B).TrimEnd('\', '/')
    } catch { return $false }
    return [string]::Equals($fullA, $fullB, [StringComparison]::OrdinalIgnoreCase)
}

# native-uc3d-smoke.py, and the tier-3 scripts that load it, delete each private WebView2 profile
# after its host exits; one they cannot delete is named in a 'LEAKED n WebView2 profile(s): ...'
# line at exit, after the WARN lines that explain it, and the exit code is left alone (R2-37).
# The gate decided those cases by exit code alone, so a run that leaked profiles was green and
# silent (R3-15). Returns those lines from $Log and "$Log.err", LEAKED lines first; none for a
# clean run.
function Get-ProfileLeakLines {
    param([Parameter(Mandatory = $true)][string]$Log)
    $pattern = '^(?:LEAKED \d+ WebView2 profile|WARN could not remove WebView2 profile|WARN could not wait for the processes started by pid|WARN \d+ process\(es\) started by pid .* still running after)'
    $lines = @()
    foreach ($path in @($Log, "$Log.err")) {
        if (Test-Path -LiteralPath $path -PathType Leaf) { $lines += @([IO.File]::ReadAllLines($path) | Where-Object { $_ -match $pattern }) }
    }
    return @(@($lines | Where-Object { $_ -like 'LEAKED *' }) + @($lines | Where-Object { $_ -notlike 'LEAKED *' }))
}

# tests\browser_launch.py prints a line starting 'DEGRADED ' for each launch whose argv it could
# not read, so TcpPortRandomizationWin was not disabled or not verified, and the suite then
# passes or fails on its own checks. The exit code cannot carry that, so a degraded launch read
# exactly like a verified one at the gate (MDST-02). Returns every such line from $Log and
# "$Log.err"; only column 0 counts, as the helper prints it.
function Get-DegradedLaunchLines {
    param([Parameter(Mandatory = $true)][string]$Log)
    $lines = @()
    foreach ($path in @($Log, "$Log.err")) {
        if (Test-Path -LiteralPath $path -PathType Leaf) { $lines += @([IO.File]::ReadAllLines($path) | Where-Object { $_ -match '^DEGRADED ' }) }
    }
    return $lines
}

# Top-level folders under $Path that are not in the $Before snapshot, sorted. Replaces a
# Test-Path for one literal 'userdata' name: any folder the app creates beside the exe counts.
function Get-NewTopLevelDirectories {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [AllowEmptyCollection()][string[]]$Before = @()
    )
    return @(Get-ChildItem -LiteralPath $Path -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $Before -notcontains $_.Name } | ForEach-Object { $_.Name } | Sort-Object)
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
    # A harness error says nothing about the cannot-verify path either (MDST-05).
    if (-not $Case.ContainsKey('ExpectCannotVerify') -or $verdict.Outcome -eq 'harness') { return $verdict }
    $forbidden = if ($Case.ContainsKey('Forbidden')) { $Case.Forbidden } else { '' }
    return Get-ExpectedCannotVerifyVerdict -ExitCode $verdict.ExitCode -Log $Log -Witness $Case.ExpectCannotVerify -Forbidden $forbidden
}

# ---------------------------------------------------------------- build, then run what was built

# Builds a project and runs the program it produced, the way the gate runs the native suite,
# whose project is not in MatrixDesktopApp.sln. The build uses the call operator in a child
# scope, like the solution build in run-gate.ps1, never Start-Process -Wait: -Wait also waits
# for every descendant, and a build that compiles anything starts the compiler server
# (VBCSCompiler), which stays alive until it idles out minutes later. 'dotnet run' under -Wait
# held the gate that long after any native-only edit (R2-30). Only the built program runs
# under Start-Process, redirected and classified like every gate case. A failed build is a
# FAIL naming its log, "$Log.build".
function Invoke-GateBuildAndRun {
    param(
        [Parameter(Mandatory = $true)][string]$Dotnet,
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$Program,
        [Parameter(Mandatory = $true)][string]$Log,
        [string]$Witness
    )
    $buildLog = "$Log.build"
    # A run log left by an earlier run must not be read as this one's.
    Remove-Item -LiteralPath $Log, "$Log.err" -Force -ErrorAction SilentlyContinue
    $global:LASTEXITCODE = -1
    & { $ErrorActionPreference = 'Continue'
        & $Dotnet build $Project -c Release --nologo -v:minimal *>&1 | Out-File -LiteralPath $buildLog -Encoding utf8 }
    $buildExit = $global:LASTEXITCODE
    if ($buildExit -ne 0) {
        return [pscustomobject]@{ Outcome = 'fail'; ExitCode = $buildExit; Detail = "dotnet build exited $buildExit; see $buildLog" }
    }
    if (-not (Test-Path -LiteralPath $Program -PathType Leaf)) {
        return [pscustomobject]@{ Outcome = 'fail'; ExitCode = $buildExit; Detail = "the build passed but did not produce $Program; see $buildLog" }
    }
    # 'dotnet run' gives the apphost DOTNET_ROOT when it is unset, so the program finds the
    # runtime of the dotnet that built it; do the same.
    $savedRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Process')
    if (-not $savedRoot) { Set-ProcessEnvironmentVariable -Name 'DOTNET_ROOT' -Value (Split-Path -Parent $Dotnet) }
    try {
        $run = Start-Process -FilePath $Program -Wait -PassThru -NoNewWindow -RedirectStandardOutput $Log -RedirectStandardError "$Log.err"
    } finally {
        Set-ProcessEnvironmentVariable -Name 'DOTNET_ROOT' -Value $savedRoot
    }
    return Get-GateCaseVerdict -ExitCode $run.ExitCode -Log $Log -Witness $Witness
}

# ---------------------------------------------------------------- loadState fixture suites

# The node suites that read the loadState fixture need node and the fixture the built harness
# writes, never Python. They used to run only inside the web smoke's innermost branch, so with
# no Python or no harness they vanished from the report without an UNCHECKED line (R2-31).
# Takes no Python input on purpose. -FixtureExit is the harness's --dump-state exit code, or
# $null when the harness was not built. Returns one verdict per name, in order: Outcome 'run',
# or 'unchecked' with the reason in Detail.
function Get-FixtureSuiteVerdicts {
    param(
        [Parameter(Mandatory = $true)][string[]]$Names,
        [bool]$NodeFound,
        [AllowNull()][object]$FixtureExit
    )
    $why = ''
    if (-not $NodeFound) { $why = 'node is not on PATH' }
    elseif ($null -eq $FixtureExit) { $why = 'the test harness has not been built, so the loadState fixture cannot be generated' }
    elseif ([int]$FixtureExit -ne 0) { $why = "the loadState fixture could not be generated (--dump-state exited $FixtureExit)" }
    return @($Names | ForEach-Object {
        [pscustomobject]@{ Name = $_; Outcome = $(if ($why) { 'unchecked' } else { 'run' }); Detail = $why }
    })
}

# ---------------------------------------------------------------- release publish parity

# release.yml publishes each executable with its own 'dotnet publish' command, and the gate
# signs off on what its own publish builds, so the two must build the same thing. Checking that
# each gate flag appears somewhere in each step passed a property set only in release.yml, a
# later override of a gate flag (MSBuild keeps the last value), --self-contained and a flag left
# only in a comment (R2-33). This compares the EFFECTIVE properties both ways instead. -Ignore
# holds what may differ: the output folder and the version stamps taken from the tag. Problems
# name the step; an empty list means parity.
function Get-ReleasePublishParityProblems {
    param(
        [Parameter(Mandatory = $true)][string]$YamlText,
        [Parameter(Mandatory = $true)][string[]]$GateArguments,
        [string[]]$Ignore = @('PublishDir', 'Version', 'AssemblyVersion', 'FileVersion'),
        [int]$StepCount = 2
    )
    $problems = @()
    $expected = ConvertTo-PublishProperties -Arguments $GateArguments
    foreach ($token in $expected.Unknown) { $problems += "the gate's own publish passes '$token', which this check does not understand" }
    $commands = @(Get-PublishCommands -YamlText $YamlText)
    if ($commands.Count -ne $StepCount) {
        return @($problems + "expected $StepCount 'dotnet publish' commands in release.yml, found $($commands.Count); the parity check cannot be trusted")
    }
    for ($n = 0; $n -lt $commands.Count; $n++) {
        $step = "publish step $($n + 1) (release.yml line $($commands[$n].Line))"
        $actual = ConvertTo-PublishProperties -Arguments $commands[$n].Arguments
        foreach ($token in $actual.Unknown) { $problems += "$step passes '$token', which this check does not understand" }
        foreach ($name in @($expected.Properties.Keys | Sort-Object)) {
            if ($Ignore -contains $name) { continue }
            $want = $expected.Properties[$name]
            if (-not $actual.Properties.ContainsKey($name)) { $problems += "$step does not set $name; the gate publishes with $name=$want" }
            elseif ($actual.Properties[$name] -ne $want) { $problems += "$step sets $name=$($actual.Properties[$name]); the gate publishes with $name=$want" }
        }
        foreach ($name in @($actual.Properties.Keys | Sort-Object)) {
            if ($Ignore -contains $name -or $expected.Properties.ContainsKey($name)) { continue }
            if ($name -eq 'PublishProfile') { $problems += "$step loads the publish profile $($actual.Properties[$name]), whose properties the gate does not publish with" }
            else { $problems += "$step sets $name=$($actual.Properties[$name]), which only release.yml sets; the gate publishes without it" }
        }
    }
    return $problems
}

# Each 'dotnet publish' command in a workflow: its line number and its argument tokens after
# 'publish'. A comment starts at a '#' that opens a line or follows whitespace, as in pwsh and
# YAML, and a line ending in a backtick continues on the next, so a flag commented out at the
# end of the chain ends the command there, as it does in pwsh.
function Get-PublishCommands {
    param([Parameter(Mandatory = $true)][string]$YamlText)
    $lines = @($YamlText -split "`r?`n" | ForEach-Object { ($_ -replace '(^|\s)#.*$', '').TrimEnd() })
    $commands = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*(-\s+)?name\s*:') { continue }
        $m = [regex]::Match($lines[$i], '\bdotnet\s+publish\b(?<rest>.*)$')
        if (-not $m.Success) { continue }
        $start = $i + 1
        $text = $m.Groups['rest'].Value
        while ($text.EndsWith('`') -and $i + 1 -lt $lines.Count) {
            $i++
            $text = $text.Substring(0, $text.Length - 1) + ' ' + $lines[$i]
        }
        $commands += [pscustomobject]@{ Line = $start; Arguments = @(Split-PublishArguments -Text $text) }
    }
    return $commands
}

# Splits a command line on whitespace, keeping quoted text and each GitHub '${{ ... }}'
# expression in one token, and removes the quotes.
function Split-PublishArguments {
    param([AllowEmptyString()][string]$Text)
    $flat = [regex]::Replace($Text, '\$\{\{.*?\}\}', { param($m) $m.Value -replace '\s+', '' })
    return @([regex]::Matches($flat, '(?:"[^"]*"|''[^'']*''|[^\s"''])+') | ForEach-Object { $_.Value -replace '["'']', '' })
}

# The MSBuild global properties a 'dotnet publish' argument list sets, the last value winning
# as in MSBuild: -p:, -property:, --property:, /p: and /property: (several per flag, split on
# ';' and on ',' as MSBuild splits them; in a pwsh step an unquoted ';' ends the statement, so
# ',' is the separator that works there, and splitting on ';' alone hid a property behind an
# ignored one such as FileVersion, R3-14),
# -c and -r and their long forms, --self-contained [true|false] or --sc, --no-self-contained.
# --nologo and -v change console output only. The first bare token is the project. Anything
# else lands in Unknown, so a switch this function was never taught fails the parity check.
function ConvertTo-PublishProperties {
    param([AllowEmptyCollection()][string[]]$Arguments = @())
    $properties = @{}
    $unknown = @()
    $project = $null
    for ($i = 0; $i -lt $Arguments.Count; $i++) {
        $token = $Arguments[$i]
        $m = [regex]::Match($token, '^(?:--?property|-p|/p(?:roperty)?):(?<list>.*)$', 'IgnoreCase')
        if ($m.Success) {
            foreach ($pair in @($m.Groups['list'].Value -split '[;,]' | Where-Object { $_ })) {
                $eq = $pair.IndexOf('=')
                if ($eq -lt 1) { $unknown += $token; continue }
                $properties[$pair.Substring(0, $eq).Trim()] = $pair.Substring($eq + 1).Trim()
            }
            continue
        }
        $m = [regex]::Match($token, '^(?<switch>-c|--configuration|-r|--runtime|-v|--verbosity)(?:[:=](?<value>.*))?$', 'IgnoreCase')
        if ($m.Success) {
            if ($m.Groups['value'].Success) { $value = $m.Groups['value'].Value }
            elseif ($i + 1 -lt $Arguments.Count) { $i++; $value = $Arguments[$i] }
            else { $unknown += $token; continue }
            $switch = $m.Groups['switch'].Value.ToLowerInvariant()
            if ($switch -eq '-c' -or $switch -eq '--configuration') { $properties['Configuration'] = $value }
            elseif ($switch -eq '-r' -or $switch -eq '--runtime') { $properties['RuntimeIdentifier'] = $value }
            continue
        }
        $m = [regex]::Match($token, '^(?:--self-contained|--sc)(?:[:=](?<value>.*))?$', 'IgnoreCase')
        if ($m.Success) {
            $value = 'true'
            if ($m.Groups['value'].Success) { $value = $m.Groups['value'].Value }
            elseif ($i + 1 -lt $Arguments.Count -and $Arguments[$i + 1] -match '^(?:true|false)$') { $i++; $value = $Arguments[$i] }
            $properties['SelfContained'] = $value.ToLowerInvariant()
            continue
        }
        if ($token -eq '--no-self-contained') { $properties['SelfContained'] = 'false'; continue }
        if ($token -eq '--nologo') { continue }
        if ($null -eq $project -and $token -notmatch '^[-/]') { $project = $token; continue }
        $unknown += $token
    }
    return [pscustomobject]@{ Project = $project; Properties = $properties; Unknown = $unknown }
}

# ---------------------------------------------------------------- display, session and power state

# Every MatrixDesktop host hides and suspends its WebView when the console display powers off,
# the session locks or disconnects, or the system sleeps (PauseReason DisplayOff, Session,
# Suspend; MainForm.TrySuspendWebViewAsync), and logs it with the reason. An unattended full
# gate photographed that black or frozen window and reported a product FAIL (R2-36). Returns
# the first such record's message, or '' when none of $Records is one.
function Find-HostSuspension {
    param([AllowEmptyCollection()][object[]]$Records = @())
    $pattern = "^(?:WebView suspended|TrySuspendAsync declined|Failed to suspend WebView) \(reason='[^']*\b(?:DisplayOff|Session|Suspend)\b"
    foreach ($record in $Records) {
        if ($null -ne $record -and $record.Msg -match $pattern) { return [string]$record.Msg }
    }
    return ''
}

# The render and animation verdicts for one tier-2 frame pair. A frame under a floor is
# UNCHECKED when the case's pid logged a host suspension (Find-HostSuspension), FAIL otherwise;
# the same holds for a static frame. -Changed is the moved fraction between the two frames.
# Returns one verdict per check that applies, in order: 'rendered content', then 'animation is
# running' once the frame rendered.
function Get-RenderVerdict {
    param(
        [Parameter(Mandatory = $true)][object]$Stats,
        [Parameter(Mandatory = $true)][double]$Changed,
        [AllowEmptyCollection()][object[]]$Records = @(),
        [string]$Detail = '',
        [double]$StdFloor = 0.04,
        [double]$NonBlackFloor = 0.02,
        [double]$ChangedFloor = 0.005
    )
    $suspension = Find-HostSuspension -Records $Records
    $outcome = if ($suspension) { 'unchecked' } else { 'fail' }
    $cause = if ($suspension) { "; the case logged '$suspension', so the host hid its renderer for the display, session or power state" } else { '' }
    $floor = ''
    if ($Stats.Std -lt $StdFloor) { $floor = "std floor $StdFloor; a black or uniform frame measures like this" }
    elseif ($Stats.NonBlack -lt $NonBlackFloor) { $floor = "non-black floor $NonBlackFloor" }
    if ($floor) { return @([pscustomobject]@{ Check = 'rendered content'; Outcome = $outcome; Detail = "$Detail ($floor)$cause" }) }
    $verdicts = @([pscustomobject]@{ Check = 'rendered content'; Outcome = 'ok'; Detail = $Detail })
    if ($Changed -lt $ChangedFloor) {
        $verdicts += [pscustomobject]@{ Check = 'animation is running'; Outcome = $outcome; Detail = "$Detail (moved floor $ChangedFloor; the frame is static)$cause" }
    } else {
        $verdicts += [pscustomobject]@{ Check = 'animation is running'; Outcome = 'ok'; Detail = ('moved={0:N5}' -f $Changed) }
    }
    return $verdicts
}

# SetThreadExecutionState flags: ES_CONTINUOUS, ES_SYSTEM_REQUIRED, ES_DISPLAY_REQUIRED.
function Get-GatePowerApi {
    if (-not ('GatePowerApi' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class GatePowerApi {
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
}
'@
    }
    return [GatePowerApi]
}

# Keeps the display and the system on while the gate's GUI tiers run, so an idle timeout cannot
# power the console display off under them. The request belongs to the calling thread and ends
# with the process; Exit-GateDisplayHold clears it sooner. Returns $true when Windows took it.
function Enter-GateDisplayHold {
    $api = Get-GatePowerApi
    return ($api::SetThreadExecutionState([uint32]2147483651) -ne 0)
}

function Exit-GateDisplayHold {
    $api = Get-GatePowerApi
    return ($api::SetThreadExecutionState([uint32]2147483648) -ne 0)
}
