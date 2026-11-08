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

    # ------------------------------------------------------------ AUD-29 native stderr

    Invoke-TestCase 'AUD-29 native stderr under ErrorActionPreference Stop is an exit code, not a crash' {
        $nodeCmd = Get-Command node -ErrorAction SilentlyContinue
        if (-not $nodeCmd) { return 'CANNOT VERIFY AUD-29: node is not on PATH for the stderr stand-in' }
        Assert-Equal 'Stop' $ErrorActionPreference 'the self-test runs under the gate''s preference'
        # node stands in for a Python without Playwright: '-c import playwright' makes it write
        # to stderr and exit 1, which crashed 5.1 before the fallback could be reached.
        Assert-Equal $nodeCmd.Source (Find-GatePython -Candidates @('', 'no-such-python-md-gate', $nodeCmd.Source)) `
            'a candidate that writes stderr falls through to the without-Playwright fallback'
        $jsDir = Join-Path $scratch 'js'
        New-Item -ItemType Directory -Path $jsDir | Out-Null
        [void](New-StubFile 'js\good.js' "export const ok = 1;`n")
        [void](New-StubFile 'js\broken-one.js' "export function ( {`n")
        [void](New-StubFile 'js\broken-two.js' "const = ;`n")
        $bad = @(Get-UnparsableJsModules -Node $nodeCmd.Source -Files @(Get-ChildItem -LiteralPath $jsDir -Filter *.js -File))
        Assert-Equal 'broken-one.js,broken-two.js' (($bad | Sort-Object) -join ',') 'node --check names every broken module'
        Assert-Equal 0 @(Get-UnparsableJsModules -Node $nodeCmd.Source -Files @()).Count 'no modules is zero failures, not a binding error'
        $unused = 99996
        while (Get-Process -Id $unused -ErrorAction SilentlyContinue) { $unused += 4 }
        $taskKill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
        Assert-Equal 128 (Invoke-NativeQuiet -FilePath $taskKill -ArgumentList @('/PID', "$unused", '/T', '/F')) `
            'taskkill reports a missing process by its exit code'
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

    # ------------------------------------------------------------ AUD-28 release version and CI

    $shellExe = (Get-Process -Id $PID).Path
    $versionScript = Join-Path $here 'check-release-version.ps1'

    function New-VersionTree {
        param([string]$Name, [string]$Matrix, [string]$Configurator, [string]$Guide)
        foreach ($dir in 'MatrixDesktop', 'MatrixDesktopConfigurator') {
            New-Item -ItemType Directory -Force -Path (Join-Path (Join-Path $scratch $Name) $dir) | Out-Null
        }
        [void](New-StubFile "$Name\MatrixDesktop\MatrixDesktop.csproj" "<Project>`n  <PropertyGroup>`n    <Version>$Matrix</Version>`n  </PropertyGroup>`n</Project>`n")
        [void](New-StubFile "$Name\MatrixDesktopConfigurator\MatrixDesktopConfigurator.csproj" "<Project>`n  <PropertyGroup>`n    <Version>$Configurator</Version>`n  </PropertyGroup>`n</Project>`n")
        [void](New-StubFile "$Name\MatrixDesktop_Argument_Guide.txt" "MATRIXDESKTOP ARGUMENT GUIDE (desktop wrapper v$Guide)`n=====`n")
        return (Join-Path $scratch $Name)
    }

    # Runs check-release-version.ps1 the way release.yml and the gate do, in a child of this
    # shell, with only the given GitHub variables set (CI's own are removed for the child).
    function Invoke-VersionCheck {
        param([string]$Root, [string]$Tag, [hashtable]$GitHub = @{}, [string]$Label)
        $environment = @{ GITHUB_REF_TYPE = $null; GITHUB_REF_NAME = $null }
        foreach ($key in $GitHub.Keys) { $environment[$key] = $GitHub[$key] }
        $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $versionScript + '" -RepoRoot "' + $Root + '"'
        if ($Tag) { $arguments += ' -Tag ' + $Tag }
        $log = New-LogPath ('version-' + $Label)
        $verdict = Invoke-GateChildCase -FilePath $shellExe -ArgumentList $arguments -Log $log -Environment $environment -Witness '(?m)^PASS release version'
        $text = @(Get-Content -LiteralPath $log, "$log.err" -ErrorAction SilentlyContinue) -join "`n"
        return [pscustomobject]@{ ExitCode = $verdict.ExitCode; Outcome = $verdict.Outcome; Text = $text }
    }

    Invoke-TestCase 'AUD-28 (a) explicit tags: v1.2.0 passes, v9.9.9 names both csproj files and the guide' {
        $root = New-VersionTree 'version-a' '1.2.0' '1.2.0' '1.2.0'
        $match = Invoke-VersionCheck -Root $root -Tag 'v1.2.0' -Label 'a-match'
        Assert-Equal 'ok' $match.Outcome "the matching tag passes ($($match.Text))"
        $wrong = Invoke-VersionCheck -Root $root -Tag 'v9.9.9' -Label 'a-wrong'
        Assert-Equal 1 $wrong.ExitCode "a tag that no file carries fails ($($wrong.Text))"
        foreach ($file in 'MatrixDesktop\MatrixDesktop.csproj', 'MatrixDesktopConfigurator\MatrixDesktopConfigurator.csproj', 'MatrixDesktop_Argument_Guide.txt') {
            if ($wrong.Text -notmatch ('(?m)^FAIL release version: ' + [regex]::Escape($file) + ': says 1\.2\.0, the tag v9\.9\.9 says 9\.9\.9')) {
                throw "the failure does not name $file ($($wrong.Text))"
            }
        }
    }

    Invoke-TestCase 'AUD-28 (b) the v1.0.4 replay: files that agree at 1.0.2 pass alone and fail against the tag' {
        $root = New-VersionTree 'version-b' '1.0.2' '1.0.2' '1.0.2'
        $files = Invoke-VersionCheck -Root $root -Label 'b-files'
        Assert-Equal 'ok' $files.Outcome "the files agree with each other, all the old check asked ($($files.Text))"
        $tagged = Invoke-VersionCheck -Root $root -Tag 'v1.0.4' -Label 'b-tag'
        Assert-Equal 1 $tagged.ExitCode "the release tag v1.0.4 does not match 1.0.2 ($($tagged.Text))"
        Assert-Equal 3 ([regex]::Matches($tagged.Text, '(?m)^FAIL release version: .+: says 1\.0\.2, the tag v1\.0\.4 says 1\.0\.4')).Count "each of the three files is named ($($tagged.Text))"
    }

    Invoke-TestCase 'AUD-28 (c) branch and pull-request runs compare the files only; a tag run compares the tag' {
        $root = New-VersionTree 'version-c' '1.2.0' '1.2.0' '1.2.0'
        $branch = Invoke-VersionCheck -Root $root -GitHub @{ GITHUB_REF_TYPE = 'branch'; GITHUB_REF_NAME = 'main' } -Label 'c-branch'
        Assert-Equal 'ok' $branch.Outcome "a main push is not a release ($($branch.Text))"
        $pr = Invoke-VersionCheck -Root $root -GitHub @{ GITHUB_REF_TYPE = 'branch'; GITHUB_REF_NAME = '17/merge' } -Label 'c-pr'
        Assert-Equal 'ok' $pr.Outcome "a pull request is not a release ($($pr.Text))"
        $tagRun = Invoke-VersionCheck -Root $root -GitHub @{ GITHUB_REF_TYPE = 'tag'; GITHUB_REF_NAME = 'v9.9.9' } -Label 'c-tag'
        Assert-Equal 1 $tagRun.ExitCode "a tag run without -Tag still compares its tag ($($tagRun.Text))"
        $goodTagRun = Invoke-VersionCheck -Root $root -GitHub @{ GITHUB_REF_TYPE = 'tag'; GITHUB_REF_NAME = 'v1.2.0' } -Label 'c-tag-ok'
        Assert-Equal 'ok' $goodTagRun.Outcome "a matching tag run passes ($($goodTagRun.Text))"
    }

    Invoke-TestCase 'AUD-28 (d) files that disagree fail without a tag, naming each version' {
        $root = New-VersionTree 'version-d' '1.2.0' '1.2.0' '1.1.0'
        $result = Invoke-VersionCheck -Root $root -Label 'd'
        Assert-Equal 1 $result.ExitCode "the guide lags the csproj files ($($result.Text))"
        if ($result.Text -notmatch 'MatrixDesktop_Argument_Guide\.txt says 1\.1\.0') { throw "the guide's version is not named ($($result.Text))" }
    }

    Invoke-TestCase 'AUD-28 (e) release.yml must run both checks, the version one with -Tag, before its first publish' {
        $checks = "      - run: ./tests/check-release-version.ps1 -Tag `$env:GITHUB_REF_NAME`n      - run: ./tests/check-release-ci.ps1 -Repository x -Sha y`n"
        $publish = "      - run: |`n          dotnet publish MatrixDesktop\MatrixDesktop.csproj -c Release`n"
        Assert-Equal 0 @(Get-ReleaseWorkflowOrderProblems -YamlText ($checks + $publish)).Count 'both checks ahead of the publish'
        $late = @(Get-ReleaseWorkflowOrderProblems -YamlText ($publish + $checks))
        Assert-Equal 2 $late.Count "both checks after the publish ($($late -join '; '))"
        $commented = @(Get-ReleaseWorkflowOrderProblems -YamlText (($checks -replace '(?m)^(\s*)- run: \./tests/check-release-version', '$1# - run: ./tests/check-release-version') + $publish))
        Assert-Equal 1 $commented.Count "a commented-out version check does not count ($($commented -join '; '))"
        $untagged = @(Get-ReleaseWorkflowOrderProblems -YamlText (($checks -replace ' -Tag \$env:GITHUB_REF_NAME', '') + $publish))
        Assert-Equal 1 $untagged.Count "a version check without -Tag compares nothing with the tag ($($untagged -join '; '))"
        Assert-Equal 1 @(Get-ReleaseWorkflowOrderProblems -YamlText $checks).Count 'no publish line means the order cannot be checked'
        $header = "    permissions:`n      checks: read      # check-release-ci.ps1 reads CI's result`n"
        $versionStep = "      - run: ./tests/check-release-version.ps1 -Tag `$env:GITHUB_REF_NAME`n"
        $ciStep = "      - run: ./tests/check-release-ci.ps1 -Repository x -Sha y`n"
        $trailing = @(Get-ReleaseWorkflowOrderProblems -YamlText ($header + $versionStep + $publish + $ciStep))
        Assert-Equal 1 $trailing.Count "a trailing comment that names the CI script is not the step ($($trailing -join '; '))"
    }

    Invoke-TestCase 'AUD-28 (h) the real release.yml passes the order check, and fails it with the CI step moved below the publish' {
        $releaseYml = Join-Path (Split-Path -Parent $here) '.github\workflows\release.yml'
        $lines = @([IO.File]::ReadAllText($releaseYml) -split "`r?`n")
        Assert-Equal 0 @(Get-ReleaseWorkflowOrderProblems -YamlText ($lines -join "`n")).Count 'release.yml as committed'
        $starts = @(for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s{6}- name:') { $i } })
        $block = $null
        for ($k = 0; $k -lt $starts.Count; $k++) {
            $end = if ($k + 1 -lt $starts.Count) { $starts[$k + 1] - 1 } else { $lines.Count - 1 }
            if (@($lines[$starts[$k]..$end] | Where-Object { $_ -match '^\s*run:.*check-release-ci\.ps1' }).Count -gt 0) { $block = @($starts[$k], $end) }
        }
        if (-not $block) { throw 'release.yml has no step that runs check-release-ci.ps1' }
        $rest = @(for ($i = 0; $i -lt $lines.Count; $i++) { if ($i -lt $block[0] -or $i -gt $block[1]) { $lines[$i] } })
        $moved = @(Get-ReleaseWorkflowOrderProblems -YamlText (($rest + $lines[$block[0]..$block[1]]) -join "`n"))
        Assert-Equal 1 $moved.Count "the CI step moved to the end ($($moved -join '; '))"
        if ($moved[0] -notmatch '^release\.yml runs the CI result check at line \d+, after its first') { throw "the problem does not name the CI result check ($($moved[0]))" }
    }

    # ------------------------------------------------------------ AUD-63 publish profile parity

    New-Item -ItemType Directory -Force -Path (Join-Path $scratch 'profiles') | Out-Null
    $stubFlags = @('-p:PublishReadyToRun=true', '-p:DebugType=embedded')
    $stubImplied = @{ Configuration = 'Release'; RuntimeIdentifier = 'win-x64' }
    $stubCsproj = New-StubFile 'profiles\Stub.csproj' ("<Project Sdk=`"Microsoft.NET.Sdk`">`n  <!-- <InvariantGlobalization>true</InvariantGlobalization> in a comment is not a property -->`n" +
        "  <PropertyGroup>`n    <TargetFramework>net10.0-windows</TargetFramework>`n  </PropertyGroup>`n</Project>`n")
    function New-StubProfile {
        param([string]$Name, [string]$Body)
        return New-StubFile "profiles\$Name.pubxml" ("<Project>`n  <PropertyGroup>`n    <PublishProtocol>FileSystem</PublishProtocol>`n" +
            "    <PublishDir>bin\out\</PublishDir>`n$Body  </PropertyGroup>`n</Project>`n")
    }
    function Get-StubProblems {
        param([string]$ProfilePath)
        return @(Get-PublishProfileParityProblems -ProfilePath $ProfilePath -CsprojPath $stubCsproj -PublishFlags $stubFlags -Implied $stubImplied -Label 'Stub.pubxml')
    }

    Invoke-TestCase 'AUD-63 (a) a profile property must come from a flag, the publish command line or the csproj, with the same value' {
        $covered = New-StubProfile 'covered' ("    <Configuration>Release</Configuration>`n    <RuntimeIdentifier>win-x64</RuntimeIdentifier>`n" +
            "    <TargetFramework>net10.0-windows</TargetFramework>`n    <PublishReadyToRun>True</PublishReadyToRun>`n")
        $problems = @(Get-StubProblems $covered)
        Assert-Equal 0 $problems.Count "every property is given the same value elsewhere ($($problems -join '; '))"
        $invariant = @(Get-StubProblems (New-StubProfile 'invariant' "    <InvariantGlobalization>true</InvariantGlobalization>`n"))
        Assert-Equal 1 $invariant.Count "a property only the profile sets ($($invariant -join '; '))"
        Assert-Equal 'Stub.pubxml: InvariantGlobalization=true is set only here; the gate and release.yml publish without it' $invariant[0] 'the problem names the profile and the property'
        $r2r = Get-StubProblems (New-StubProfile 'r2r' "    <PublishReadyToRun>false</PublishReadyToRun>`n")
        Assert-Equal 'Stub.pubxml: PublishReadyToRun=false, but the gate and release.yml publish with true' "$r2r" 'a flag with another value'
        $rid = Get-StubProblems (New-StubProfile 'rid' "    <RuntimeIdentifier>win-arm64</RuntimeIdentifier>`n")
        Assert-Equal 'Stub.pubxml: RuntimeIdentifier=win-arm64, but the gate and release.yml publish with win-x64' "$rid" 'the command line sets another value'
        $framework = @(Get-StubProblems (New-StubProfile 'framework' "    <TargetFramework>net9.0-windows</TargetFramework>`n"))
        Assert-Equal 1 $framework.Count "the csproj sets another framework ($($framework -join '; '))"
    }

    Invoke-TestCase 'AUD-63 (b) the real framework-dependent profiles match the gate''s publish, and re-adding InvariantGlobalization fails naming the file' {
        $repo = Split-Path -Parent $here
        $gateAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $here 'run-gate.ps1'), [ref]$null, [ref]$null)
        $assignments = @($gateAst.FindAll({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] }, $true))
        $values = @{}
        foreach ($name in '$PublishFlags', '$PublishImplied') {
            $found = @($assignments | Where-Object { $_.Left.Extent.Text -eq $name })
            Assert-Equal 1 $found.Count "run-gate.ps1 assigns $name once"
            $values[$name] = $found[0].Right.Expression.SafeGetValue()
        }
        $flags = [string[]]@($values['$PublishFlags'])
        if ($flags.Count -lt 5 -or @($flags | Where-Object { $_ -notlike '-p:*=*' }).Count -gt 0) { throw "run-gate.ps1's publish flags did not read back ($($flags -join ' '))" }
        foreach ($project in 'MatrixDesktop', 'MatrixDesktopConfigurator') {
            $relative = "$project\Properties\PublishProfiles\Portable-win-x64-framework-dependent.pubxml"
            $problems = @(Get-PublishProfileParityProblems -ProfilePath (Join-Path $repo $relative) -CsprojPath (Join-Path $repo "$project\$project.csproj") `
                -PublishFlags $flags -Implied $values['$PublishImplied'] -Label $relative)
            Assert-Equal 0 $problems.Count "$relative as committed ($($problems -join '; '))"
        }
        $relative = 'MatrixDesktopConfigurator\Properties\PublishProfiles\Portable-win-x64-framework-dependent.pubxml'
        $text = [IO.File]::ReadAllText((Join-Path $repo $relative))
        $readded = New-StubFile 'profiles\readded.pubxml' ($text -replace '(\r?\n)(\s*)<DebugType>', '$1$2<InvariantGlobalization>true</InvariantGlobalization>$1$2<DebugType>')
        $problems = @(Get-PublishProfileParityProblems -ProfilePath $readded -CsprojPath (Join-Path $repo 'MatrixDesktopConfigurator\MatrixDesktopConfigurator.csproj') `
            -PublishFlags $flags -Implied $values['$PublishImplied'] -Label $relative)
        Assert-Equal 1 $problems.Count "InvariantGlobalization re-added to the configurator profile ($($problems -join '; '))"
        if ($problems[0] -notlike "$relative`: InvariantGlobalization=true is set only here*") { throw "the problem does not name the configurator profile ($($problems[0]))" }
    }

    Invoke-TestCase 'AUD-63 (c) a runtimeconfig.json option reads back, and an absent one is null under strict mode' {
        $invariantConfig = New-StubFile 'profiles\invariant.runtimeconfig.json' ('{ "runtimeOptions": { "tfm": "net10.0", "configProperties": ' +
            '{ "System.Globalization.Invariant": true, "System.Runtime.TieredPGO": false } } }')
        Assert-Equal 'True' "$(Get-RuntimeConfigOption -Path $invariantConfig -Name 'System.Globalization.Invariant')" 'the option is read'
        $icuConfig = New-StubFile 'profiles\icu.runtimeconfig.json' '{ "runtimeOptions": { "tfm": "net10.0", "configProperties": { "System.Runtime.TieredPGO": false } } }'
        Assert-Equal $null (Get-RuntimeConfigOption -Path $icuConfig -Name 'System.Globalization.Invariant') 'an absent option'
        $bare = New-StubFile 'profiles\bare.runtimeconfig.json' '{ "runtimeOptions": { "tfm": "net10.0" } }'
        Assert-Equal $null (Get-RuntimeConfigOption -Path $bare -Name 'System.Globalization.Invariant') 'no configProperties at all'
    }

    Invoke-TestCase 'AUD-28 (f) the newest CI run on the tagged commit decides, and no run is a failure' {
        function New-Run { param([long]$Id, [string]$Status, [string]$Conclusion) [pscustomobject]@{ id = $Id; status = $Status; conclusion = $Conclusion } }
        Assert-Equal 'missing' (Get-CiCheckVerdict -CheckRuns @()).Outcome 'CI never ran on the commit'
        Assert-Equal 'ok' (Get-CiCheckVerdict -CheckRuns @(New-Run 5 'completed' 'success')).Outcome 'a passing run'
        Assert-Equal 'fail' (Get-CiCheckVerdict -CheckRuns @(New-Run 5 'completed' 'failure')).Outcome 'a failing run'
        Assert-Equal 'fail' (Get-CiCheckVerdict -CheckRuns @(New-Run 5 'completed' 'cancelled')).Outcome 'a cancelled run is not a pass'
        Assert-Equal 'pending' (Get-CiCheckVerdict -CheckRuns @(New-Run 5 'in_progress' '')).Outcome 'a running job'
        Assert-Equal 'ok' (Get-CiCheckVerdict -CheckRuns @((New-Run 5 'completed' 'failure'), (New-Run 9 'completed' 'success'))).Outcome 'a re-run that passed supersedes the failure'
        Assert-Equal 'fail' (Get-CiCheckVerdict -CheckRuns @((New-Run 9 'completed' 'failure'), (New-Run 5 'completed' 'success'))).Outcome 'a newer failure supersedes an older pass, in any order'
        Assert-Equal 'wait' (Get-CiWaitDecision -Outcome 'pending' -ElapsedMinutes 5 -TimeoutMinutes 35 -MissingGraceMinutes 3) 'CI still running inside the timeout'
        Assert-Equal 'fail' (Get-CiWaitDecision -Outcome 'pending' -ElapsedMinutes 35 -TimeoutMinutes 35 -MissingGraceMinutes 3) 'CI still running at the timeout'
        Assert-Equal 'wait' (Get-CiWaitDecision -Outcome 'missing' -ElapsedMinutes 1 -TimeoutMinutes 35 -MissingGraceMinutes 3) 'CI may not have queued yet'
        Assert-Equal 'fail' (Get-CiWaitDecision -Outcome 'missing' -ElapsedMinutes 3 -TimeoutMinutes 35 -MissingGraceMinutes 3) 'CI never ran on the commit'
        Assert-Equal 'fail' (Get-CiWaitDecision -Outcome 'fail' -ElapsedMinutes 0 -TimeoutMinutes 35 -MissingGraceMinutes 3) 'a red commit fails at once'
        Assert-Equal 'pass' (Get-CiWaitDecision -Outcome 'ok' -ElapsedMinutes 0 -TimeoutMinutes 35 -MissingGraceMinutes 3) 'a green commit passes'
    }

    Invoke-TestCase 'AUD-28 (g) the release scripts parse, and the CI check fails closed without a commit' {
        foreach ($name in 'check-release-ci.ps1', 'check-release-version.ps1') {
            $parseErrors = $null
            [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $here $name), [ref]$null, [ref]$parseErrors)
            Assert-Equal 0 @($parseErrors).Count "$name parses ($(@($parseErrors) -join '; '))"
        }
        $log = New-LogPath 'ci-no-commit'
        $verdict = Invoke-GateChildCase -FilePath $shellExe -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $here 'check-release-ci.ps1') + '"') `
            -Log $log -Environment @{ GITHUB_REPOSITORY = $null }
        Assert-Equal 1 $verdict.ExitCode 'no repository or commit to ask about'
        if (([IO.File]::ReadAllText($log)) -notmatch '(?m)^FAIL CI result: ') { throw "no FAIL CI result line ($([IO.File]::ReadAllText($log)))" }
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
