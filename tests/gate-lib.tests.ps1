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
        param([string]$Name, [string]$Matrix, [string]$Configurator, [string]$Guide, [string]$Readme)
        if (-not $Readme) { $Readme = $Matrix }
        foreach ($dir in 'MatrixDesktop', 'MatrixDesktopConfigurator') {
            New-Item -ItemType Directory -Force -Path (Join-Path (Join-Path $scratch $Name) $dir) | Out-Null
        }
        [void](New-StubFile "$Name\MatrixDesktop\MatrixDesktop.csproj" "<Project>`n  <PropertyGroup>`n    <Version>$Matrix</Version>`n  </PropertyGroup>`n</Project>`n")
        [void](New-StubFile "$Name\MatrixDesktopConfigurator\MatrixDesktopConfigurator.csproj" "<Project>`n  <PropertyGroup>`n    <Version>$Configurator</Version>`n  </PropertyGroup>`n</Project>`n")
        [void](New-StubFile "$Name\MatrixDesktop_Argument_Guide.txt" "MATRIXDESKTOP ARGUMENT GUIDE (desktop wrapper v$Guide)`n=====`n")
        [void](New-StubFile "$Name\README.md" "# MatrixDesktop`n`nCurrent source version: **$Readme**. Tagged releases are listed on the releases page.`n")
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
        Assert-Equal 4 ([regex]::Matches($tagged.Text, '(?m)^FAIL release version: .+: says 1\.0\.2, the tag v1\.0\.4 says 1\.0\.4')).Count "each of the four files is named ($($tagged.Text))"
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

    # README ships in every release zip, and at v1.0.3 and v1.0.4 it said 1.0.2 as well (R2-34).
    Invoke-TestCase 'AUD-28 (i) a README left behind fails the tag check' {
        $root = New-VersionTree 'version-i' '1.3.0' '1.3.0' '1.3.0' -Readme '1.2.0'
        $tagged = Invoke-VersionCheck -Root $root -Tag 'v1.3.0' -Label 'i-tag'
        Assert-Equal 1 $tagged.ExitCode "README still says 1.2.0 ($($tagged.Text))"
        $fails = @([regex]::Matches($tagged.Text, '(?m)^FAIL release version: .*$') | ForEach-Object { $_.Value.Trim() })
        Assert-Equal 1 $fails.Count "only README is behind the tag ($($tagged.Text))"
        if ($fails[0] -notmatch '^FAIL release version: README\.md: says 1\.2\.0, the tag v1\.3\.0 says 1\.3\.0') { throw "the failure does not name README.md ($($fails[0]))" }
        $files = Invoke-VersionCheck -Root $root -Label 'i-files'
        Assert-Equal 1 $files.ExitCode "without a tag the files disagree ($($files.Text))"
        if ($files.Text -notmatch 'README\.md says 1\.2\.0') { throw "the disagreement does not name README.md ($($files.Text))" }
        # Before 1.2.0 the line read 'Current version: **x**', so an old tree still parses.
        [void](New-StubFile 'version-i\README.md' "# MatrixDesktop`n`nCurrent version: **1.3.0** - see the release notes.`n")
        $old = Invoke-VersionCheck -Root $root -Tag 'v1.3.0' -Label 'i-old-wording'
        Assert-Equal 'ok' $old.Outcome "the wording without 'source' parses ($($old.Text))"
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

    # ------------------------------------------------------------ AUD-59 expected cannot-verify case

    # Stand-ins for uc3d-calibration.py: the fixed script (honours the hidden-adapter hook) and
    # the script as it was (no probe, so the backend assertion raises and Python exits 1).
    $fixedCalibration = New-StubFile 'calibration_fixed.py' ("import os`n" +
        "if os.environ.get('MD_UC3D_HIDE_WEBGPU_ADAPTER') == '1':`n" +
        "    print('CANNOT VERIFY WebGPU: browser has no adapter', flush=True)`n" +
        "    raise SystemExit(2)`n" +
        "print('PASS webgpu amount-500: synthetic adapter', flush=True)`n")
    $oldCalibration = New-StubFile 'calibration_old.py' ("import sys`n" +
        "print('Traceback (most recent call last):', file=sys.stderr)`n" +
        "print(`"AssertionError: expected the webgpu backend, observed 'regl'`", file=sys.stderr)`n" +
        "raise SystemExit(1)`n")
    $hiddenCase = @{ Arguments = ' --renderer webgpu --only amount-500'; Environment = @{ MD_UC3D_HIDE_WEBGPU_ADAPTER = '1' }
        ExpectCannotVerify = '(?m)^\s*CANNOT VERIFY WebGPU'; Forbidden = '(?m)^\s*PASS webgpu' }

    Invoke-TestCase 'AUD-59 (a) an expected cannot-verify case passes only on exit 2 with the witness and no WebGPU pass' {
        if (-not $Python) { return 'CANNOT VERIFY AUD-59 (a): no Python interpreter for the stubs' }
        $fixed = Invoke-GatePythonCase -Python $Python -ScriptPath $fixedCalibration -Case $hiddenCase -Log (New-LogPath 'aud59-fixed')
        Assert-Equal 'ok' $fixed.Outcome "the fixed script with the adapter hidden ($($fixed.Detail))"
        Assert-Equal 'CANNOT VERIFY WebGPU: browser has no adapter' $fixed.Detail 'the pass quotes the witness'
        $old = Invoke-GatePythonCase -Python $Python -ScriptPath $oldCalibration -Case $hiddenCase -Log (New-LogPath 'aud59-old')
        Assert-Equal 'fail' $old.Outcome 'the old script exits 1, a product failure'
        if ($old.Detail -notlike 'expected the cannot-verify exit 2, observed exit 1*') { throw "the failure does not say what was expected ($($old.Detail))" }
        $both = New-StubFile 'calibration_both.py' ("print('CANNOT VERIFY WebGPU: browser has no adapter')`nprint('PASS webgpu amount-500: 500 streams')`nraise SystemExit(2)`n")
        $mixed = Invoke-GatePythonCase -Python $Python -ScriptPath $both -Case $hiddenCase -Log (New-LogPath 'aud59-both')
        Assert-Equal 'fail' $mixed.Outcome 'a WebGPU pass printed with the adapter hidden'
        $usage = New-StubFile 'calibration_usage.py' ("import argparse`nargparse.ArgumentParser().parse_args()`n")
        $usageVerdict = Invoke-GatePythonCase -Python $Python -ScriptPath $usage -Case $hiddenCase -Log (New-LogPath 'aud59-usage')
        Assert-Equal 'fail' $usageVerdict.Outcome 'an argparse exit 2 has no witness'
        $plain = Invoke-GatePythonCase -Python $Python -ScriptPath $fixedCalibration -Case @{ Arguments = '' } -Log (New-LogPath 'aud59-plain')
        Assert-Equal 'ok' $plain.Outcome 'a case without ExpectCannotVerify keeps the ordinary verdict (exit 0 passes)'
    }

    Invoke-TestCase 'AUD-59 (b) a case Environment reaches the child only' {
        if (-not $Python) { return 'CANNOT VERIFY AUD-59 (b): no Python interpreter for the stubs' }
        Assert-Equal $null ([Environment]::GetEnvironmentVariable('MD_UC3D_HIDE_WEBGPU_ADAPTER', 'Process')) 'the test starts without the hook set'
        $without = Invoke-GatePythonCase -Python $Python -ScriptPath $fixedCalibration -Case @{ Arguments = ''; ExpectCannotVerify = $hiddenCase.ExpectCannotVerify } -Log (New-LogPath 'aud59-noenv')
        Assert-Equal 'fail' $without.Outcome 'without the Environment entry the stand-in finds an adapter and exits 0'
        $with = Invoke-GatePythonCase -Python $Python -ScriptPath $fixedCalibration -Case $hiddenCase -Log (New-LogPath 'aud59-env')
        Assert-Equal 'ok' $with.Outcome 'the Environment entry reaches the child'
        Assert-Equal $null ([Environment]::GetEnvironmentVariable('MD_UC3D_HIDE_WEBGPU_ADAPTER', 'Process')) 'the hook is removed from this process again'
    }

    Invoke-TestCase 'AUD-59 (c) the registered tier-1 case hides the adapter and expects the cannot-verify exit' {
        if (-not $Python) { return 'CANNOT VERIFY AUD-59 (c): no Python interpreter for the stubs' }
        $gateAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $here 'run-gate.ps1'), [ref]$null, [ref]$null)
        $registered = @()
        foreach ($node in @($gateAst.FindAll({ param($n) $n -is [Management.Automation.Language.HashtableAst] }, $true))) {
            try { $value = $node.SafeGetValue() } catch { continue }
            if ($value -is [hashtable] -and $value.ContainsKey('Script') -and $value['Script'] -eq 'tests\uc3d-calibration.py' -and "$($value['Arguments'])" -match '--renderer webgpu') { $registered += $value }
        }
        Assert-Equal 1 $registered.Count 'run-gate.ps1 registers one webgpu calibration case'
        $case = $registered[0]
        $fixed = Invoke-GatePythonCase -Python $Python -ScriptPath $fixedCalibration -Case $case -Log (New-LogPath 'aud59-registered-fixed')
        Assert-Equal 'ok' $fixed.Outcome "the registered case against the fixed stand-in ($($fixed.Detail))"
        $old = Invoke-GatePythonCase -Python $Python -ScriptPath $oldCalibration -Case $case -Log (New-LogPath 'aud59-registered-old')
        Assert-Equal 'fail' $old.Outcome 'the registered case against the old stand-in'
    }

    # uc3d-calibration.py writes <output>\report.json at exit, so two cases sharing a folder
    # leave only the last one's summary: the hidden-adapter case replaced the regl case's 22
    # reports with an empty one on every gate run (R2-32).
    Invoke-TestCase 'AUD-59 (d) calibration cases write distinct output folders' {
        $repo = Split-Path -Parent $here
        $gateAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $here 'run-gate.ps1'), [ref]$null, [ref]$null)
        $folders = @()
        foreach ($node in @($gateAst.FindAll({ param($n) $n -is [Management.Automation.Language.HashtableAst] }, $true))) {
            try { $value = $node.SafeGetValue() } catch { continue }
            if (-not ($value -is [hashtable] -and $value.ContainsKey('Script') -and $value['Script'] -eq 'tests\uc3d-calibration.py')) { continue }
            # The script's default; a relative --output resolves against the repo, where the gate runs.
            $output = 'artifacts\uc3d-calibration'
            $m = [regex]::Match("$($value['Arguments'])", '(?:^|\s)--output(?:=|\s+)(?:"(?<q>[^"]*)"|(?<p>\S+))')
            if ($m.Success) { $output = if ($m.Groups['q'].Success) { $m.Groups['q'].Value } else { $m.Groups['p'].Value } }
            $folders += [IO.Path]::GetFullPath((Join-Path $repo $output)).TrimEnd('\', '/').ToLowerInvariant()
        }
        if ($folders.Count -lt 2) { throw "run-gate.ps1 registers $($folders.Count) uc3d-calibration.py case(s), expected the regl and the hidden-adapter cases" }
        $shared = @($folders | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { "$($_.Name) ($($_.Count) cases)" })
        Assert-Equal 0 $shared.Count "every calibration case needs its own report folder; shared: $($shared -join ', ')"
    }

    # ------------------------------------------------------------ AUD-54 WebView2 profile record

    function New-Record { param([string]$Level, [string]$Msg) [pscustomobject]@{ Level = $Level; Msg = $Msg } }

    Invoke-TestCase 'AUD-54 (a) exactly one profile record, requesting LocalAppData and running on the gate override' {
        $want = 'C:\Users\Someone\AppData\Local\MatrixDesktop\WebView2'
        $override = 'D:\repo with space\artifacts\gate\webview-profile'
        $start = New-Record 'INFO' 'MatrixDesktop starting'
        $good = New-Record 'INFO' "WebView2 profile requested=$want effective=$override"
        Assert-Equal 'ok' (Get-WebViewProfileVerdict -Records @($start, $good) -Requested $want -Effective $override).Outcome 'the correct record'
        $spelled = New-Record 'INFO' "WebView2 profile requested=$($want.ToLowerInvariant())\ effective=$override\"
        Assert-Equal 'ok' (Get-WebViewProfileVerdict -Records @($spelled) -Requested $want -Effective $override).Outcome 'case and a trailing separator do not matter'
        $none = Get-WebViewProfileVerdict -Records @($start) -Requested $want -Effective $override
        Assert-Equal 'fail' $none.Outcome 'no record is a FAIL, not a pass'
        if ($none.Detail -notlike '*found 0') { throw "the failure does not say no record was found ($($none.Detail))" }
        Assert-Equal 'fail' (Get-WebViewProfileVerdict -Records @() -Requested $want -Effective $override).Outcome 'an empty log'
        Assert-Equal 'fail' (Get-WebViewProfileVerdict -Records @($good, $good) -Requested $want -Effective $override).Outcome 'two initialisations in one case'
        Assert-Equal 'fail' (Get-WebViewProfileVerdict -Records @((New-Record 'WARN' $good.Msg)) -Requested $want -Effective $override).Outcome 'only an INFO record counts'
        $beside = Get-WebViewProfileVerdict -Records @((New-Record 'INFO' "WebView2 profile requested=D:\publish\userdata effective=$override")) -Requested $want -Effective $override
        Assert-Equal 'fail' $beside.Outcome 'a profile requested beside the exe (mutation A)'
        if ($beside.Detail -notlike "requested 'D:\publish\userdata', expected*") { throw "the failure does not name the request ($($beside.Detail))" }
        Assert-Equal 'fail' (Get-WebViewProfileVerdict -Records @((New-Record 'INFO' "WebView2 profile requested= effective=$override")) -Requested $want -Effective $override).Outcome 'an empty request (mutation B)'
        Assert-Equal 'fail' (Get-WebViewProfileVerdict -Records @((New-Record 'INFO' "WebView2 profile requested=$want effective=$want")) -Requested $want -Effective $override).Outcome 'the override was not in effect'
    }

    Invoke-TestCase 'AUD-54 (b) any new top-level folder beside the exe is reported, existing ones and files are not' {
        $publish = Join-Path $scratch 'publish'
        foreach ($dir in 'web', 'configurator') { New-Item -ItemType Directory -Force -Path (Join-Path $publish $dir) | Out-Null }
        $before = @(Get-ChildItem -LiteralPath $publish -Directory -Force | ForEach-Object { $_.Name })
        Assert-Equal 0 @(Get-NewTopLevelDirectories -Path $publish -Before $before).Count 'nothing changed'
        [void](New-StubFile 'publish\MatrixDesktop.log' "a file, not a folder`n")
        New-Item -ItemType Directory -Force -Path (Join-Path $publish 'web\cache') | Out-Null
        Assert-Equal 0 @(Get-NewTopLevelDirectories -Path $publish -Before $before).Count 'a new file or a nested folder is not a top-level folder'
        foreach ($dir in 'userdata', 'MatrixDesktop.exe.WebView2') { New-Item -ItemType Directory -Force -Path (Join-Path $publish $dir) | Out-Null }
        Assert-Equal 'MatrixDesktop.exe.WebView2,userdata' ((Get-NewTopLevelDirectories -Path $publish -Before $before) -join ',') 'both new folders, whatever their names'
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

    # ------------------------------------------------------------ R2-30 build, then run what was built

    # Processes serving one private compiler-server pipe, found by the pipe in their command line.
    function Get-PipeProcesses {
        param([string]$Pipe)
        return @(Get-CimInstance Win32_Process -Filter "Name='VBCSCompiler.exe' OR Name='dotnet.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and $_.CommandLine -like "*$Pipe*" })
    }

    Invoke-TestCase 'R2-30 the build-and-run launcher returns while the compiler server its build started is still alive' {
        $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $dotnetCmd) { return 'CANNOT VERIFY R2-30: dotnet is not on PATH' }
        $fixture = Join-Path $scratch 'build-fixture'
        New-Item -ItemType Directory -Path $fixture | Out-Null
        $project = New-StubFile 'build-fixture\GateFixture.csproj' ("<Project Sdk=`"Microsoft.NET.Sdk`">`n  <PropertyGroup>`n" +
            "    <OutputType>Exe</OutputType>`n    <TargetFramework>net10.0</TargetFramework>`n  </PropertyGroup>`n</Project>`n")
        [void](New-StubFile 'build-fixture\Program.cs' "System.Console.WriteLine(`"PASS gate fixture ran`");`n")
        $driver = New-StubFile 'build-fixture-driver.ps1' (@(
            'param([string]$GateLib, [string]$Pipe, [string]$Dotnet, [string]$Project, [string]$Program, [string]$Log, [string]$Result)'
            '$ErrorActionPreference = ''Stop'''
            'Set-StrictMode -Version Latest'
            '. $GateLib'
            '$env:SharedCompilationId = $Pipe'
            '$env:MSBUILDDISABLENODEREUSE = ''1'''
            '$env:DOTNET_CLI_TELEMETRY_OPTOUT = ''1'''
            '$env:DOTNET_NOLOGO = ''1'''
            '$verdict = Invoke-GateBuildAndRun -Dotnet $Dotnet -Project $Project -Program $Program -Log $Log -Witness ''(?m)^PASS gate fixture ran'''
            '[IO.File]::WriteAllText($Result, $verdict.Outcome + ''|'' + $verdict.Detail)'
        ) -join "`n")
        # A private compiler-server pipe: the fixture's build starts its own server, and no other
        # session's server can serve it or be touched. Without node reuse the MSBuild nodes exit
        # with the build, so only the compiler server outlives it.
        $pipe = 'mdgatelib' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
        $result = Join-Path $scratch 'r2-30-result.txt'
        $values = [ordered]@{ GateLib = (Join-Path $here 'gate-lib.ps1'); Pipe = $pipe; Dotnet = $dotnetCmd.Source; Project = $project
            Program = (Join-Path $fixture 'bin\Release\net10.0\GateFixture.exe'); Log = (New-LogPath 'r2-30-fixture'); Result = $result }
        $psi = New-Object Diagnostics.ProcessStartInfo
        $psi.FileName = $shellExe
        $psi.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $driver + '"' + (@($values.Keys | ForEach-Object { " -$_ `"$($values[$_])`"" }) -join '')
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $child = [Diagnostics.Process]::Start($psi)
        try {
            # Process.WaitForExit with a timeout waits for this one process, not its descendants.
            $returned = $child.WaitForExit(90000)
            $servers = @(Get-PipeProcesses $pipe | Where-Object { $_.CommandLine -like '*VBCSCompiler*' })
            if (-not $returned) { throw "the launcher was still waiting 90 s after it started, with $($servers.Count) private compiler server(s) alive" }
            if (-not (Test-Path -LiteralPath $result -PathType Leaf)) { throw "the driver exited $($child.ExitCode) without writing a result" }
            $outcome = [IO.File]::ReadAllText($result)
            if ($outcome -notlike 'ok|*') { throw "the fixture did not build and run: $outcome" }
            if ($servers.Count -eq 0) { return "CANNOT VERIFY R2-30: the fixture build started no compiler server on pipe $pipe, so the wait was not exercised" }
            Write-Host ("  R2-30 witness: the launcher returned after {0:N1} s with private compiler server pid {1} still alive" -f $clock.Elapsed.TotalSeconds, $servers[0].ProcessId)
        } finally {
            foreach ($p in @(Get-PipeProcesses $pipe)) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
            if (-not $child.HasExited -and -not $child.WaitForExit(20000)) { $child.Kill() }
        }
    }

    Invoke-TestCase 'R2-30 (b) a project that does not build is a FAIL naming the build log, and an earlier run log is not read' {
        $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $dotnetCmd) { return 'CANNOT VERIFY R2-30 (b): dotnet is not on PATH' }
        New-Item -ItemType Directory -Path (Join-Path $scratch 'broken-fixture') | Out-Null
        # An unknown framework fails at restore, before anything compiles or starts a server.
        $project = New-StubFile 'broken-fixture\Broken.csproj' ("<Project Sdk=`"Microsoft.NET.Sdk`">`n  <PropertyGroup>`n" +
            "    <OutputType>Exe</OutputType>`n    <TargetFramework>net0.0-gatefixture</TargetFramework>`n  </PropertyGroup>`n</Project>`n")
        $log = New-LogPath 'r2-30-broken'
        [IO.File]::WriteAllText($log, "PASS gate fixture ran`n")
        $verdict = Invoke-GateBuildAndRun -Dotnet $dotnetCmd.Source -Project $project -Program (Join-Path $scratch 'broken-fixture\bin\Broken.exe') -Log $log -Witness '(?m)^PASS gate fixture ran'
        Assert-Equal 'fail' $verdict.Outcome "a failed build ($($verdict.Detail))"
        if ($verdict.Detail -notlike "dotnet build exited *; see $log.build") { throw "the failure does not name the build log ($($verdict.Detail))" }
        if (-not (Test-Path -LiteralPath "$log.build" -PathType Leaf)) { throw "no build log at $log.build" }
        if (Test-Path -LiteralPath $log) { throw "the earlier run log is still at $log, where it reads as this run's" }
    }

    # ------------------------------------------------------------ R2-31 loadState fixture suites

    Invoke-TestCase 'R2-31 (a) each fixture suite is UNCHECKED by name for a missing input, and runs only with node and the fixture' {
        $names = @('first suite', 'second suite', 'third suite')
        $legs = @(
            @{ Label = 'node missing'; NodeFound = $false; FixtureExit = 0; Want = 'node is not on PATH' }
            @{ Label = 'harness not built'; NodeFound = $true; FixtureExit = $null; Want = 'the test harness has not been built, so the loadState fixture cannot be generated' }
            @{ Label = 'fixture failed'; NodeFound = $true; FixtureExit = 3; Want = 'the loadState fixture could not be generated (--dump-state exited 3)' }
        )
        foreach ($leg in $legs) {
            $verdicts = @(Get-FixtureSuiteVerdicts -Names $names -NodeFound $leg.NodeFound -FixtureExit $leg.FixtureExit)
            Assert-Equal ($names -join ',') (@($verdicts | ForEach-Object { $_.Name }) -join ',') "$($leg.Label): one verdict per suite, in order"
            $wrong = @($verdicts | Where-Object { $_.Outcome -ne 'unchecked' -or $_.Detail -ne $leg.Want } | ForEach-Object { "$($_.Name)=$($_.Outcome) '$($_.Detail)'" })
            Assert-Equal 0 $wrong.Count "$($leg.Label): every suite UNCHECKED with '$($leg.Want)'; wrong: $($wrong -join '; ')"
        }
        $ready = @(Get-FixtureSuiteVerdicts -Names $names -NodeFound $true -FixtureExit 0)
        Assert-Equal 'run,run,run' (@($ready | ForEach-Object { $_.Outcome }) -join ',') 'node and a fixture: every suite runs'
    }

    Invoke-TestCase 'R2-31 (b) with no Python and no built harness the gate still reports each fixture suite by name' {
        # The real block text from run-gate.ps1, from the web smoke's header to the next one, run
        # against recording stubs; nothing in it launches a process when the harness is absent.
        $gatePath = Join-Path $here 'run-gate.ps1'
        $gateAst = [Management.Automation.Language.Parser]::ParseFile($gatePath, [ref]$null, [ref]$null)
        $headers = @($gateAst.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Test-Hdr' }, $true))
        $from = @($headers | Where-Object { $_.Extent.Text -like "*'tier 1: headless web smoke'*" })
        $to = @($headers | Where-Object { $_.Extent.Text -like "*'tier 1: UC3D importer and WebGL smoke'*" })
        Assert-Equal '1,1' "$($from.Count),$($to.Count)" 'run-gate.ps1 has one web smoke header and one importer header'
        $text = $gateAst.Extent.Text.Substring($from[0].Extent.EndOffset, $to[0].Extent.StartOffset - $from[0].Extent.EndOffset)
        $block = [scriptblock]::Create($text)
        $script:Reported = New-Object 'Collections.Generic.List[string]'
        function Test-Ok { param([string]$Msg) $script:Reported.Add("ok|$Msg") }
        function Test-Fail { param([string]$Msg, [string]$Detail) $script:Reported.Add("FAIL|$Msg|$Detail") }
        function Test-Unchecked { param([string]$Msg, [string]$Why) $script:Reported.Add("UNCHECKED|$Msg|$Why") }
        function Find-GatePython { param([string[]]$Candidates) return $null }
        $RepoRoot = Join-Path $scratch 'no-harness-root'
        New-Item -ItemType Directory -Path (Join-Path $RepoRoot 'tests') | Out-Null
        # Truthy, and never started: without the harness there is no fixture to give it.
        $node = [pscustomobject]@{ Source = (Join-Path $scratch 'no-such-node.exe') }
        . $block
        $all = @($script:Reported)
        $harnessWhy = 'the test harness has not been built, so the loadState fixture cannot be generated'
        foreach ($name in 'UC3D rebuilt slider metadata and web parsers agree', 'ZMatrix rebuilt metadata and structured web parsers agree',
            'configurator effective defaults match the renderer for every version and effect') {
            $lines = @($all | Where-Object { $_ -like "*|$name|*" -or $_ -like "*|$name" })
            Assert-Equal 1 $lines.Count "'$name' is reported exactly once (reported: $($all -join ' / '))"
            Assert-Equal "UNCHECKED|$name|$harnessWhy" $lines[0] "'$name' names the missing harness"
        }
        $witness = @($all | Where-Object { $_ -like 'UNCHECKED|headless web smoke|no python on PATH*' })
        Assert-Equal 1 $witness.Count "the block ran to the Python check (reported: $($all -join ' / '))"
        Assert-Equal 0 @($all | Where-Object { $_ -like 'FAIL|*' }).Count "nothing failed (reported: $($all -join ' / '))"
    }

    # ------------------------------------------------------------ R2-35 headless web smoke exit 2

    Invoke-TestCase 'R2-35 (a) the gate counts the web smoke''s exit 2 as UNCHECKED only with a witness line, naming the logs' {
        # The real exit-2 clause of run-gate.ps1's switch on the web smoke's exit code.
        $gateAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $here 'run-gate.ps1'), [ref]$null, [ref]$null)
        $switches = @($gateAst.FindAll({ param($n) $n -is [Management.Automation.Language.SwitchStatementAst] -and $n.Condition.Extent.Text -eq '$smokeExit' }, $true))
        Assert-Equal 1 $switches.Count 'run-gate.ps1 has one switch on $smokeExit'
        $clauses = @($switches[0].Clauses | Where-Object { $_.Item1.Extent.Text -eq '2' })
        Assert-Equal 1 $clauses.Count 'that switch has one clause for exit 2'
        $body = $clauses[0].Item2.Extent.Text
        $block = [scriptblock]::Create($body.Substring(1, $body.Length - 2))
        $script:Reported = New-Object 'Collections.Generic.List[string]'
        function Test-Ok { param([string]$Msg) $script:Reported.Add("ok|$Msg") }
        function Test-Fail { param([string]$Msg, [string]$Detail) $script:Reported.Add("FAIL|$Msg|$Detail") }
        function Test-Unchecked { param([string]$Msg, [string]$Why) $script:Reported.Add("UNCHECKED|$Msg|$Why") }
        $legs = @(
            # web-smoke.py's message before R2-35: no witness, so a harness failure.
            @{ Label = 'no-witness'; Err = "  chromium is unavailable: x`n"; Want = 'FAIL|headless web smoke|harness error: exit 2 without a cannot-verify witness; see *' }
            @{ Label = 'witness'; Err = "UNVERIFIED: chromium is unavailable: x`n"; Want = 'UNCHECKED|headless web smoke|UNVERIFIED: chromium is unavailable: x; see *' }
        )
        foreach ($leg in $legs) {
            $script:Reported.Clear()
            $smokeLog = New-LogPath ('r2-35-' + $leg.Label)
            [IO.File]::WriteAllText($smokeLog, "=== web smoke: harness ===`n")
            [IO.File]::WriteAllText("$smokeLog.err", $leg.Err)
            $smokeExit = 2
            $smokeOut = "=== web smoke: harness ===`n" + $leg.Err
            . $block
            $all = @($script:Reported)
            Assert-Equal 1 $all.Count "$($leg.Label): one report line (reported: $($all -join ' / '))"
            if ($all[0] -notlike $leg.Want) { throw "$($leg.Label): reported '$($all[0])', expected '$($leg.Want)'" }
            if ($all[0] -notlike "*$smokeLog.err*") { throw "$($leg.Label): the reason does not name $smokeLog.err ($($all[0]))" }
        }
    }

    # A copy of the real web-smoke.py beside a stub fixture: the script exits 3 before its imports
    # when its repo has no loadState fixture.
    function New-WebSmokeCopy {
        $root = Join-Path $scratch 'websmoke'
        $copy = Join-Path $root 'tests\web-smoke.py'
        if (-not (Test-Path -LiteralPath $copy -PathType Leaf)) {
            foreach ($dir in 'tests', 'artifacts\gate') { New-Item -ItemType Directory -Force -Path (Join-Path $root $dir) | Out-Null }
            Copy-Item -LiteralPath (Join-Path $here 'web-smoke.py') -Destination $copy
            [void](New-StubFile 'websmoke\artifacts\gate\loadState.json' '{}')
        }
        return $copy
    }

    Invoke-TestCase 'R2-35 (b) web-smoke.py without its dependencies exits 2 with an UNVERIFIED line, which the gate counts as UNCHECKED' {
        if (-not $Python) { return 'CANNOT VERIFY R2-35 (b): no Python interpreter' }
        $log = New-LogPath 'r2-35-dependency'
        # -S leaves site-packages off sys.path, so the Playwright import fails.
        $verdict = Invoke-GateChildCase -FilePath $Python -ArgumentList ('-S "' + (New-WebSmokeCopy) + '"') -Log $log
        Assert-Equal 2 $verdict.ExitCode "a missing dependency is the cannot-verify exit ($($verdict.Detail))"
        Assert-Equal 'unchecked' $verdict.Outcome "the exit 2 carries its witness ($($verdict.Detail))"
        if ($verdict.Detail -notlike 'UNVERIFIED: a dependency is missing for this interpreter: *') { throw "the reason does not name the missing dependency ($($verdict.Detail))" }
    }

    Invoke-TestCase 'R2-35 (c) web-smoke.py exits 2 with an UNVERIFIED line when chromium cannot start' {
        if (-not $Python) { return 'CANNOT VERIFY R2-35 (c): no Python interpreter' }
        if ((Invoke-NativeQuiet -FilePath $Python -ArgumentList @('-c', 'import playwright, PIL')) -ne 0) { return 'CANNOT VERIFY R2-35 (c): this Python lacks Playwright or Pillow' }
        $browsers = Join-Path $scratch 'no-browsers'
        New-Item -ItemType Directory -Force -Path $browsers | Out-Null
        $log = New-LogPath 'r2-35-chromium'
        # An empty browser folder: Playwright imports, and launching chromium fails.
        $verdict = Invoke-GateChildCase -FilePath $Python -ArgumentList ('"' + (New-WebSmokeCopy) + '"') -Log $log -Environment @{ PLAYWRIGHT_BROWSERS_PATH = $browsers }
        Assert-Equal 2 $verdict.ExitCode "chromium that cannot start is the cannot-verify exit ($($verdict.Detail))"
        Assert-Equal 'unchecked' $verdict.Outcome "the exit 2 carries its witness ($($verdict.Detail))"
        if ($verdict.Detail -notlike 'UNVERIFIED: chromium is unavailable: *') { throw "the reason does not say chromium is unavailable ($($verdict.Detail))" }
    }

    # ------------------------------------------------------------ R2-33 release publish parity

    # The gate's own publish arguments, read back from run-gate.ps1 as AUD-63 (b) reads them.
    function Get-GatePublishArguments {
        $gateAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $here 'run-gate.ps1'), [ref]$null, [ref]$null)
        $assignments = @($gateAst.FindAll({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] }, $true))
        $arguments = @()
        foreach ($name in '$PublishArguments', '$PublishFlags') {
            $found = @($assignments | Where-Object { $_.Left.Extent.Text -eq $name })
            Assert-Equal 1 $found.Count "run-gate.ps1 assigns $name once"
            $arguments += [string[]]@($found[0].Right.Expression.SafeGetValue())
        }
        return $arguments
    }

    # Every sub-assertion runs, so a mutation shows exactly which ones it breaks.
    function Assert-ParityVariants {
        param([string[]]$GateArguments, [object[]]$Variants)
        $wrong = @()
        foreach ($variant in $Variants) {
            $problems = @(Get-ReleasePublishParityProblems -YamlText $variant.Text -GateArguments $GateArguments)
            $want = @($variant.Want)
            $ok = $problems.Count -eq $want.Count
            for ($k = 0; $ok -and $k -lt $want.Count; $k++) { $ok = $problems[$k] -like $want[$k] }
            if (-not $ok) { $wrong += "$($variant.Label): expected $($want.Count) problem(s) like '$($want -join ''' / ''')', got $($problems.Count): '$($problems -join ''' / ''')'" }
        }
        Assert-Equal 0 $wrong.Count ($wrong -join ' || ')
    }

    Invoke-TestCase 'R2-33 (a) release publish parity is two-way and last-wins, over the real release.yml' {
        $gateArguments = @(Get-GatePublishArguments)
        $lines = @([IO.File]::ReadAllText((Join-Path (Split-Path -Parent $here) '.github\workflows\release.yml')) -split "`r?`n")
        function Find-Lines { param([string]$Pattern) @(for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match $Pattern) { $i } }) }
        # The text with some lines replaced (a replacement may hold "`n" for added lines) or removed.
        function Edit-Lines {
            param([hashtable]$Replace = @{}, [int[]]$Remove = @())
            $out = for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($Remove -contains $i) { continue }
                if ($Replace.ContainsKey($i)) { $Replace[$i] } else { $lines[$i] }
            }
            return (@($out) -join "`n")
        }
        $r2r = @(Find-Lines '^\s*-p:PublishReadyToRun=true `$')
        $symbols = @(Find-Lines '^\s*-p:DebugSymbols=true `$')
        $runtime = @(Find-Lines '^\s*--no-self-contained `$')
        $last = @(Find-Lines '^\s*-p:FileVersion=[^`]*$')
        Assert-Equal '2,2,2,2' "$($r2r.Count),$($symbols.Count),$($runtime.Count),$($last.Count)" 'release.yml has two publish steps with the lines the variants edit'
        $indent = $lines[$symbols[0]] -replace '\S.*$', ''
        Assert-ParityVariants -GateArguments $gateArguments -Variants @(
            @{ Label = 'as committed'; Text = (Edit-Lines); Want = @() }
            @{ Label = '+InvariantGlobalization in step 1'; Text = (Edit-Lines -Replace @{ $symbols[0] = $lines[$symbols[0]] + "`n" + $indent + '-p:InvariantGlobalization=true `' })
                Want = 'publish step 1 (release.yml line *) sets InvariantGlobalization=true, which only release.yml sets*' }
            @{ Label = '+PublishReadyToRun=false as the last argument of step 2'; Text = (Edit-Lines -Replace @{ $last[1] = $lines[$last[1]] + ' `' + "`n" + $indent + '-p:PublishReadyToRun=false' })
                Want = 'publish step 2 (release.yml line *) sets PublishReadyToRun=false; the gate publishes with PublishReadyToRun=true' }
            @{ Label = '--self-contained in step 1'; Text = (Edit-Lines -Replace @{ $runtime[0] = $lines[$runtime[0]] -replace '--no-self-contained', '--self-contained' })
                Want = 'publish step 1 (release.yml line *) sets SelfContained=true; the gate publishes with SelfContained=false' }
            # pwsh ends the command at a comment line, even after a backtick.
            @{ Label = 'step 2 ReadyToRun only in a comment'; Text = (Edit-Lines -Remove @($r2r[1]) -Replace @{ $last[1] = $lines[$last[1]] + ' `' + "`n" + $indent + '# -p:PublishReadyToRun=true' })
                Want = 'publish step 2 (release.yml line *) does not set PublishReadyToRun; the gate publishes with PublishReadyToRun=true' }
            @{ Label = 'control: step 2 ReadyToRun removed'; Text = (Edit-Lines -Remove @($r2r[1]))
                Want = 'publish step 2 (release.yml line *) does not set PublishReadyToRun; the gate publishes with PublishReadyToRun=true' }
        )
    }

    Invoke-TestCase 'R2-33 (b) the parity parser reads every property form, and names a profile, an unknown switch and a third publish' {
        $gateArguments = @('-c', 'Release', '-r', 'win-x64', '--no-self-contained', '-p:PublishReadyToRun=true', '-p:DebugType=embedded')
        $one = @('      - name: Publish one (dotnet publish in a step name is not a command)', '        shell: pwsh', '        run: |',
            '          dotnet publish A.csproj /p:PublishReadyToRun=true;AssemblyVersion=1.2.3.0 -c:Release `',
            '            --runtime "win-x64" --self-contained false -property:DebugType=embedded `',
            '            -p:Version=${{ steps.version.outputs.version }} -p:PublishDir="bin\out dir\" --nologo -v minimal') -join "`n"
        $two = "      - name: Publish two`n        run: dotnet publish B.csproj -c Release -r win-x64 --no-self-contained -p:PublishReadyToRun=true -p:DebugType=embedded"
        Assert-ParityVariants -GateArguments $gateArguments -Variants @(
            @{ Label = 'every form'; Text = "$one`n$two"; Want = @() }
            @{ Label = 'a publish profile'; Text = "$one`n$two -p:PublishProfile=Portable"; Want = 'publish step 2 (release.yml line 8) loads the publish profile Portable*' }
            @{ Label = 'an unknown switch'; Text = "$one`n$two --use-current-runtime"; Want = "publish step 2 (release.yml line 8) passes '--use-current-runtime', which this check does not understand" }
            @{ Label = 'a third publish'; Text = "$one`n$two`n$two"; Want = "expected 2 'dotnet publish' commands in release.yml, found 3*" }
        )
    }

    # ------------------------------------------------------------ R2-36 render verdict and display hold

    Invoke-TestCase 'R2-36 (a) a black frame is UNCHECKED when the case logged a display-off suspension, and a FAIL without one' {
        $black = [pscustomobject]@{ Std = 0.0; Mean = 0.0; NonBlack = 0.0 }
        $start = New-Record 'INFO' 'CrashDumpWriter installed for process 4242'
        $displayOff = New-Record 'INFO' "WebView suspended (reason='DisplayOff')."
        $with = @(Get-RenderVerdict -Stats $black -Changed 0 -Records @($start, $displayOff) -Detail 'std=0.00000')
        Assert-Equal 'rendered content|unchecked' (@($with | ForEach-Object { "$($_.Check)|$($_.Outcome)" }) -join ',') 'a black frame after a display-off suspension'
        if ($with[0].Detail -notlike "std=0.00000 (std floor 0.04*)*WebView suspended (reason='DisplayOff').*") { throw "the reason does not quote the suspension ($($with[0].Detail))" }
        $without = @(Get-RenderVerdict -Stats $black -Changed 0 -Records @($start) -Detail 'std=0.00000')
        Assert-Equal 'rendered content|fail' (@($without | ForEach-Object { "$($_.Check)|$($_.Outcome)" }) -join ',') 'the same black frame without a suspension record'
        $healthy = [pscustomobject]@{ Std = 0.17; Mean = 0.09; NonBlack = 0.3 }
        $frozen = @(Get-RenderVerdict -Stats $healthy -Changed 0 -Records @($displayOff))
        Assert-Equal 'rendered content|ok,animation is running|unchecked' (@($frozen | ForEach-Object { "$($_.Check)|$($_.Outcome)" }) -join ',') 'a static frame after a suspension'
        $static = @(Get-RenderVerdict -Stats $healthy -Changed 0 -Records @())
        Assert-Equal 'rendered content|ok,animation is running|fail' (@($static | ForEach-Object { "$($_.Check)|$($_.Outcome)" }) -join ',') 'a static frame without one'
        $live = @(Get-RenderVerdict -Stats $healthy -Changed 0.3 -Records @())
        Assert-Equal 'rendered content|ok,animation is running|ok' (@($live | ForEach-Object { "$($_.Check)|$($_.Outcome)" }) -join ',') 'a healthy, moving frame'
        Assert-Equal 'moved=0.30000' $live[1].Detail 'the animation pass reports the moved fraction'
        $dim = @(Get-RenderVerdict -Stats ([pscustomobject]@{ Std = 0.09; Mean = 0.01; NonBlack = 0.01 }) -Changed 0.3 -Records @())
        Assert-Equal 'rendered content|fail' (@($dim | ForEach-Object { "$($_.Check)|$($_.Outcome)" }) -join ',') 'a frame under the non-black floor'
    }

    Invoke-TestCase 'R2-36 (b) only a display, session or power suspension record counts' {
        foreach ($message in "WebView suspended (reason='DisplayOff').", "WebView suspended (reason='Session').", "WebView suspended (reason='Manual, Suspend').",
            "TrySuspendAsync declined (reason='DisplayOff'); restored visibility.", "Failed to suspend WebView (reason='Session'): gone") {
            Assert-Equal $message (Find-HostSuspension -Records @((New-Record 'INFO' 'MatrixDesktop starting'), (New-Record 'WARN' $message))) "'$message' is a host suspension"
        }
        foreach ($message in "WebView suspended (reason='ShellRecovery').", "WebView suspended (reason='Manual').", "Could not apply wallpaper playback state: DisplayOff",
            "WebView2 profile requested=a effective=b") {
            Assert-Equal '' (Find-HostSuspension -Records @((New-Record 'INFO' $message))) "'$message' is not a display, session or power suspension"
        }
        Assert-Equal '' (Find-HostSuspension -Records @()) 'no records'
    }

    Invoke-TestCase 'R2-36 (c) the display hold asks Windows to keep the display and the system on, and lets go' {
        $api = Get-GatePowerApi
        Assert-Equal $true (Enter-GateDisplayHold) 'SetThreadExecutionState took the request'
        try {
            # ES_CONTINUOUS alone clears this thread's request and returns the one it replaced.
            $held = $api::SetThreadExecutionState([uint32]2147483648)
            Assert-Equal 3 ($held -band 3) ('the thread asked for ES_SYSTEM_REQUIRED and ES_DISPLAY_REQUIRED (state 0x{0:X8})' -f $held)
            [void](Enter-GateDisplayHold)
            Assert-Equal $true (Exit-GateDisplayHold) 'the release was taken'
            $after = $api::SetThreadExecutionState([uint32]2147483648)
            Assert-Equal 0 ($after -band 3) ('Exit-GateDisplayHold cleared both requests (state 0x{0:X8})' -f $after)
        } finally {
            [void](Exit-GateDisplayHold)
        }
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
