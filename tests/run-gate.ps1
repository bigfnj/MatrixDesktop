#requires -Version 5
<#
.SYNOPSIS
    Run the full local verification gate: build, unit harness, web asset integrity, publish
    payload assertions, and runtime smoke of both executables with real render assertions.

.DESCRIPTION
    One command so the gate runs the same way every time, and so a check cannot quietly
    report success without having run. That second point is the whole design. This script
    has four outcomes, not two:

        0  GATE PASSED     every selected check ran and passed
        1  GATE FAILED     at least one definite negative
        2  CANNOT VERIFY   nothing failed, but at least one check could not run
        3  HARNESS ERROR   the gate itself broke, which is never an app verdict

    Separating 3 from 1 matters: "my harness broke" must never masquerade as "your change
    broke it". Separating 2 from 0 matters more: you cannot get a green gate by having
    checks quietly not run.

    Tier 1 is deterministic and runs anywhere including CI. Tier 2 and 3 launch the real
    executables and need an attached interactive session, so CI passes -Tier1Only. That
    makes omission an explicit caller decision, never a runtime fallback this script picks.

.NOTES
    CALIBRATION, measured on this box 2026-09-09 against live captures. The reference PNGs
    in docs/images are NOT comparable: their mean is 0.06 where a live capture of the same
    default config is 0.09, because those were produced differently.

    Healthy, five configurations:  luma mean 0.040-0.107, luma std 0.127-0.184
    Pure black frame:              luma std 0.000, non-black 0.000
    Uniform fill, 1-2s post-launch: luma std 0.000, non-black 1.000

    That uniform-fill frame is why standard deviation is the primary discriminator. A
    brightness threshold and a non-black-fraction threshold both PASS it. Only std rejects
    it. The floor of 0.04 sits about 3x below the lowest healthy observation.

    Two ImageMagick details that produced wrong answers during calibration:
      - captures are RGBA, so '-alpha off' is required or a black opaque frame reads 0.25
      - '%[fx:mean]' is required, not '%[mean]', which returns a raw quantum on this Q16 build

    Two win32 details, both verified:
      - PrintWindow flag 0 returns an all-black bitmap for GPU-composited Chromium content.
        Flag 2, PW_RENDERFULLCONTENT, returns real pixels and works on occluded windows.
      - PrintWindow draws the whole window including chrome, so the bitmap is window-sized
        and cropped to the client rect. Skipping the crop moved the observed mean 0.09 -> 0.32.

.PARAMETER Tier1Only
    Run only the environment-independent checks. Used by CI, which has no attached session.

.PARAMETER SkipBuild
    Reuse existing build output. Faster re-run when only scripts changed.

.EXAMPLE
    .\tests\run-gate.ps1
.EXAMPLE
    .\tests\run-gate.ps1 -Tier1Only
#>
[CmdletBinding()]
param(
    [switch]$Tier1Only,
    [switch]$SkipBuild,
    [string]$RepoRoot,
    [int]$SettleMs = 6000
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Resolved in the body, never as a param() default: under Windows PowerShell 5.1 a
# [CmdletBinding()] script gets an EMPTY $PSScriptRoot inside the param block, which then
# fails silently into every Join-Path downstream.
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }
if (-not $RepoRoot) { throw 'Cannot resolve RepoRoot; pass it explicitly with -RepoRoot.' }
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)

$script:Pass = 0
$script:Fail = 0
$script:Unchecked = 0
$script:Failures = New-Object 'Collections.Generic.List[string]'
$script:Uncheckables = New-Object 'Collections.Generic.List[string]'

function Test-Hdr { param([string]$Msg) Write-Host ''; Write-Host "=== $Msg" -ForegroundColor Cyan }
function Test-Ok { param([string]$Msg) $script:Pass++; Write-Host "  ok         $Msg" -ForegroundColor DarkGray }
function Test-Fail {
    param([string]$Msg, [string]$Detail)
    $script:Fail++
    $script:Failures.Add($Msg)
    Write-Host "  FAIL       $Msg" -ForegroundColor Red
    if ($Detail) { Write-Host "               $Detail" -ForegroundColor Red }
}
function Test-Unchecked {
    param([string]$Msg, [string]$Why)
    $script:Unchecked++
    $script:Uncheckables.Add("$Msg ($Why)")
    Write-Host "  UNCHECKED  $Msg" -ForegroundColor Yellow
    Write-Host "               $Why" -ForegroundColor Yellow
}
function Stop-Harness {
    param([string]$Why)
    Write-Host ''
    Write-Host "HARNESS ERROR: $Why" -ForegroundColor Magenta
    Write-Host 'This is a defect in the gate, not a verdict on the application.' -ForegroundColor Magenta
    exit 3
}

$GatePublishDir = Join-Path $RepoRoot 'artifacts\gate\win-x64-fd'
$ShotDir = Join-Path $RepoRoot 'artifacts\gate\shots'
$AppLocalRoot = Join-Path $env:LOCALAPPDATA 'MatrixDesktop'
$LogPath = Join-Path $AppLocalRoot 'MatrixDesktop.log'
$DumpDir = Join-Path $AppLocalRoot 'dumps'

# The one log line guaranteed once per launch, from CrashDumpWriter.Install. Without
# asserting it, "no new ERROR lines" passes vacuously whenever logging is broken or the
# pid attribution is wrong, which is a check that cannot fail.
$StartMarker = "CrashDumpWriter installed for process"

$RenderStdFloor = 0.04
$RenderNonBlackFloor = 0.02
$RenderChangedFloor = 0.005

# ---------------------------------------------------------------- win32

try {
    Add-Type -AssemblyName System.Drawing
} catch {
    Stop-Harness "System.Drawing is unavailable: $($_.Exception.Message)"
}

$GateWinSig = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class GateWin {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll", SetLastError=true)] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool EnumWindows(EnumProc f, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    public static string Title(IntPtr h) { var sb = new StringBuilder(512); GetWindowTextW(h, sb, 512); return sb.ToString(); }
    public static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }

    public static IntPtr[] WindowsForPid(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid) list.Add(h);
            return true; }, IntPtr.Zero);
        return list.ToArray();
    }
}
'@
if (-not ('GateWin' -as [type])) {
    try { Add-Type -TypeDefinition $GateWinSig } catch { Stop-Harness "Add-Type failed: $($_.Exception.Message)" }
}

# PowerShell is DPI-unaware by default. GetWindowRect returns physical pixels, so on a
# scaled display an unaware process would crop against virtualised coordinates. -4 is
# PER_MONITOR_AWARE_V2. Must run before any DC or window work.
[void][GateWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

function Get-WindowCapture {
    param([IntPtr]$Hwnd, [string]$Path)

    if (-not [GateWin]::IsWindowVisible($Hwnd)) { return $null }
    if ([GateWin]::IsIconic($Hwnd)) { return $null }

    $wr = New-Object GateWin+RECT
    $cr = New-Object GateWin+RECT
    $og = New-Object GateWin+POINT
    [void][GateWin]::GetWindowRect($Hwnd, [ref]$wr)
    [void][GateWin]::GetClientRect($Hwnd, [ref]$cr)
    [void][GateWin]::ClientToScreen($Hwnd, [ref]$og)

    $ww = $wr.R - $wr.L
    $wh = $wr.B - $wr.T
    $cw = $cr.R - $cr.L
    $ch = $cr.B - $cr.T
    if ($ww -le 0 -or $wh -le 0 -or $cw -lt 64 -or $ch -lt 64) { return $null }

    $full = New-Object System.Drawing.Bitmap $ww, $wh
    try {
        $g = [System.Drawing.Graphics]::FromImage($full)
        $dc = $g.GetHdc()
        $ok = [GateWin]::PrintWindow($Hwnd, $dc, 2)
        $g.ReleaseHdc($dc)
        $g.Dispose()
        if (-not $ok) { return $null }
        $rect = New-Object System.Drawing.Rectangle ($og.X - $wr.L), ($og.Y - $wr.T), $cw, $ch
        $client = $full.Clone($rect, $full.PixelFormat)
        try { $client.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $client.Dispose() }
    } finally {
        $full.Dispose()
    }
    return "${cw}x${ch}"
}

function Get-RenderStats {
    param([string]$Path)
    $raw = & magick $Path -alpha off -colorspace Gray -format "%[fx:mean]|%[fx:standard_deviation]" info:
    if ($LASTEXITCODE -ne 0) { Stop-Harness "magick failed reading $Path" }
    $nonBlack = & magick $Path -alpha off -colorspace Gray -threshold 5% -format "%[fx:mean]" info:
    if ($LASTEXITCODE -ne 0) { Stop-Harness "magick threshold failed on $Path" }
    $parts = $raw -split '\|'
    [pscustomobject]@{ Mean = [double]$parts[0]; Std = [double]$parts[1]; NonBlack = [double]$nonBlack }
}

# Fraction of pixels whose luminance moved between two frames. This is the only check
# that proves the animation loop is RUNNING: a frozen frame with content passes every
# single-frame statistic.
function Get-ChangedFraction {
    param([string]$PathA, [string]$PathB)
    $v = & magick $PathA $PathB -alpha off -colorspace Gray -compose difference -composite -threshold 5% -format "%[fx:mean]" info:
    if ($LASTEXITCODE -ne 0) { Stop-Harness 'magick difference failed' }
    return [double]$v
}

# ---------------------------------------------------------------- process

function Assert-NoStaleInstance {
    param([string]$Exe)
    $resolved = [IO.Path]::GetFullPath($Exe)
    $running = @(
        Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($resolved)) -ErrorAction SilentlyContinue |
            Where-Object { try { [string]::Equals($_.Path, $resolved, [StringComparison]::OrdinalIgnoreCase) } catch { $false } }
    )
    if ($running.Count -gt 0) {
        Stop-Harness "$resolved is already running (pid $($running[0].Id)). Close it and re-run."
    }
}

function Stop-GateProcess {
    param([Diagnostics.Process]$Process)
    try { if ($Process.HasExited) { return 'already-exited' } } catch { return 'gone' }
    # CloseMainWindow posts WM_CLOSE, which runs OnFormClosing, so the WebView2 dispose,
    # keyboard-hook uninstall, SystemEvents detach and Cursor.Show all actually execute.
    # A Kill skips every one of those, which are the things the smoke test cares about.
    try {
        [void]$Process.CloseMainWindow()
        if ($Process.WaitForExit(6000)) { return 'WM_CLOSE' }
    } catch { }
    $taskKill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
    if (Test-Path -LiteralPath $taskKill -PathType Leaf) {
        & $taskKill /PID $Process.Id /T /F 2>&1 | Out-Null
        try { [void]$Process.WaitForExit(5000) } catch { }
    } else {
        try { $Process.Kill(); [void]$Process.WaitForExit(5000) } catch { }
    }
    return 'killed'
}

function Get-WebViewDescendantCount {
    param([int]$RootPid)
    try {
        $all = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" -ErrorAction Stop)
        return @($all | Where-Object { $_.ParentProcessId -eq $RootPid }).Count
    } catch {
        return -1
    }
}

# A visible #32770 owned by our pid is a modal dialog: the WebView2-missing message, or
# the crash box. Without this the gate would capture the dialog and report a confusing
# render failure instead of naming the actual problem.
function Find-OwnedDialog {
    param([int]$ProcessId)
    foreach ($h in [GateWin]::WindowsForPid([uint32]$ProcessId)) {
        if ([GateWin]::Cls($h) -eq '#32770' -and [GateWin]::IsWindowVisible($h)) {
            return [GateWin]::Title($h)
        }
    }
    return $null
}

function Start-GateApp {
    param([string]$Exe, [string]$AppArgs, [int]$TimeoutSeconds = 25)

    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.WorkingDirectory = Split-Path $Exe -Parent
    $psi.UseShellExecute = $false
    $psi.Arguments = $AppArgs
    $proc = [Diagnostics.Process]::Start($psi)

    $res = [pscustomobject]@{ Process = $proc; Hwnd = [IntPtr]::Zero; Responsive = $false; Dialog = $null; MsToWindow = -1 }
    try { $res.Responsive = $proc.WaitForInputIdle($TimeoutSeconds * 1000) } catch { $res.Responsive = $false }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt ($TimeoutSeconds * 1000)) {
        if ($proc.HasExited) { break }
        $dlg = Find-OwnedDialog -ProcessId $proc.Id
        if ($dlg) { $res.Dialog = $dlg; break }
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero -and [GateWin]::IsWindowVisible($proc.MainWindowHandle)) {
            $res.Hwnd = $proc.MainWindowHandle
            $res.MsToWindow = $sw.ElapsedMilliseconds
            break
        }
        Start-Sleep -Milliseconds 120
    }
    return $res
}

# This desktop is an RDP session that disconnects several times a day, and session
# arbitration kills GUI apps with no Application Error and no WER report. 41 and 42 are
# Begin/End session arbitration and were observed firing on this box, so they are included
# alongside the disconnect and reconnect ids.
function Test-SessionChurn {
    param([datetime]$Since)
    $ev = @(Get-WinEvent -FilterHashtable @{
        LogName   = 'Microsoft-Windows-TerminalServices-LocalSessionManager/Operational'
        Id        = 24, 25, 40, 41, 42
        StartTime = $Since.AddSeconds(-10)
    } -ErrorAction SilentlyContinue)
    return $ev.Count -gt 0
}

# Reads both the live log and the rotated .old, oldest first, so a rotation mid-run cannot
# hide records. Attribution is by pid, which rotation cannot destroy, with a byte offset
# only used to detect that rotation happened.
function Get-LogRecordsForPid {
    param([int]$ProcessId, [datetime]$Since)
    $out = New-Object 'Collections.Generic.List[object]'
    $header = '^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[pid=(?<pid>\d+) tid=\d+\] (?<lvl>INFO|WARN|ERROR) (?<msg>.*)$'
    foreach ($path in @("$LogPath.old", $LogPath)) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $fs = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $sr = [IO.StreamReader]::new($fs)
            $cur = $null
            while ($null -ne ($line = $sr.ReadLine())) {
                $m = [regex]::Match($line, $header)
                if ($m.Success) {
                    if ($cur) { $out.Add($cur) }
                    $cur = [pscustomobject]@{
                        Ts = [datetime]::ParseExact($m.Groups['ts'].Value, 'yyyy-MM-dd HH:mm:ss.fff', $null)
                        Pid = [int]$m.Groups['pid'].Value
                        Level = $m.Groups['lvl'].Value
                        Msg = $m.Groups['msg'].Value
                        Extra = New-Object 'Collections.Generic.List[string]'
                    }
                } elseif ($cur -and $line.Trim().Length -gt 0) {
                    $cur.Extra.Add($line)
                }
            }
            if ($cur) { $out.Add($cur) }
            $sr.Dispose()
        } finally {
            $fs.Dispose()
        }
    }
    return @($out | Where-Object { $_.Pid -eq $ProcessId -and $_.Ts -ge $Since.AddSeconds(-3) })
}

function Get-DumpCount {
    if (-not (Test-Path -LiteralPath $DumpDir)) { return 0 }
    return @(Get-ChildItem -LiteralPath $DumpDir -Filter *.dmp -ErrorAction SilentlyContinue).Count
}

# ---------------------------------------------------------------- run

Push-Location $RepoRoot
try {
    Write-Host 'MatrixDesktop verification gate' -ForegroundColor White
    Write-Host "repo    : $RepoRoot"
    Write-Host "shell   : $($PSVersionTable.PSEdition) $($PSVersionTable.PSVersion)"
    Write-Host "mode    : $(if ($Tier1Only) { 'tier 1 only (caller decision)' } else { 'all tiers' })"
    if (-not $Tier1Only) {
        $competing = @()
        try { $competing = @(& qwinsta 2>$null | Select-Object -Skip 1 | Where-Object { $_ -match '\S' }) } catch { }
        Write-Host "session : $((Get-Process -Id $PID).SessionId), $($competing.Count) session row(s) reported by qwinsta"
        if (Test-SessionChurn -Since (Get-Date).AddHours(-24)) {
            Write-Host 'NOTE    : RDP session events in the last 24h. A tier 2 UNCHECKED may be environmental.' -ForegroundColor Yellow
        }
    }

    # ------------------------------------------------------------ tier 1

    Test-Hdr 'tier 1: build'
    if ($SkipBuild) {
        Test-Unchecked 'release build' 'skipped by -SkipBuild'
    } else {
        $buildLog = Join-Path $env:TEMP 'md-gate-build.log'
        & dotnet build (Join-Path $RepoRoot 'MatrixDesktopApp.sln') -c Release --nologo -v:minimal *>&1 |
            Tee-Object -FilePath $buildLog | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Test-Fail 'release build' "dotnet build exited $LASTEXITCODE; see $buildLog"
        } else {
            $warnings = @(Select-String -LiteralPath $buildLog -Pattern 'warning [A-Z]+[0-9]+')
            if ($warnings.Count -gt 0) {
                Test-Fail 'release build has 0 warnings' "$($warnings.Count) warning(s); see $buildLog"
            } else {
                Test-Ok 'release build, 0 warnings'
            }
        }
    }

    Test-Hdr 'tier 1: unit harness'
    $testProj = Join-Path $RepoRoot 'tests\MatrixDesktop.Tests\MatrixDesktop.Tests.csproj'
    if (-not (Test-Path -LiteralPath $testProj)) {
        Test-Unchecked 'unit harness' 'tests\MatrixDesktop.Tests does not exist yet'
    } else {
        & dotnet build $testProj -c Release --nologo -v:minimal | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Test-Fail 'unit harness build'
        } else {
            $harness = Join-Path $RepoRoot 'tests\MatrixDesktop.Tests\bin\Release\MatrixDesktop.Tests.exe'
            if (-not (Test-Path -LiteralPath $harness)) {
                Test-Fail 'unit harness executable' "missing: $harness"
            } else {
                $hlog = Join-Path $env:TEMP 'md-gate-tests.log'
                $hp = Start-Process -FilePath $harness -Wait -PassThru -NoNewWindow `
                    -RedirectStandardOutput $hlog -RedirectStandardError "$hlog.err"
                $summary = ''
                if (Test-Path -LiteralPath $hlog) {
                    $summary = @(Get-Content -LiteralPath $hlog | Where-Object { $_ -match 'tests passed' }) | Select-Object -Last 1
                }
                if ($hp.ExitCode -ne 0) {
                    Test-Fail 'unit harness' "exit $($hp.ExitCode). $summary"
                    foreach ($p in @($hlog, "$hlog.err")) {
                        if (Test-Path -LiteralPath $p) {
                            Get-Content -LiteralPath $p | Where-Object { $_ -match '^FAIL' } |
                                Select-Object -First 15 | ForEach-Object { Write-Host "               $_" -ForegroundColor Red }
                        }
                    }
                } else {
                    Test-Ok "unit harness: $summary"
                }
            }
        }
    }

    # Web asset integrity. This is the only automated coverage the vendored web/ bundle has,
    # and unlike the runtime tiers it needs no session, so it runs in CI too.
    Test-Hdr 'tier 1: web bundle integrity'
    $webRoot = Join-Path $RepoRoot 'MatrixDesktop\web'
    $node = Get-Command node -ErrorAction SilentlyContinue
    if (-not $node) {
        Test-Unchecked 'web javascript parses' 'node is not on PATH'
    } else {
        $jsFiles = @(Get-ChildItem -LiteralPath $webRoot -Recurse -Filter *.js -File |
            Where-Object { $_.FullName -notmatch '\\lib\\' })
        $badJs = @()
        # Each file is copied to a .mjs before checking. This is not cosmetic: verified on
        # node v24.16.0, 'node --check broken.js' exits 0 and silently passes a file whose
        # last line is 'function ( {', because the CommonJS-then-ESM detection path
        # swallows the SyntaxError. The identical content as .mjs exits 1 and names the
        # line. Checking these as .js would be a gate that cannot fail.
        $mjsDir = Join-Path $env:TEMP ('md-gate-mjs-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $mjsDir | Out-Null
        try {
            foreach ($js in $jsFiles) {
                $probe = Join-Path $mjsDir ($js.BaseName + '.mjs')
                Copy-Item -LiteralPath $js.FullName -Destination $probe -Force
                & node --check $probe 2>$null
                if ($LASTEXITCODE -ne 0) { $badJs += $js.Name }
            }
        } finally {
            Remove-Item -LiteralPath $mjsDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        if ($badJs.Count -gt 0) {
            Test-Fail 'web javascript parses' "$($badJs.Count) file(s) failed node --check: $($badJs -join ', ')"
        } else {
            Test-Ok "web javascript parses ($($jsFiles.Count) first-party modules)"
        }
    }

    # Every shader, asset and lib path referenced from JS or HTML must resolve. A renamed
    # or deleted asset is otherwise a runtime-only failure that no build catches.
    $refPattern = '["'']((?:\.\./)*(?:shaders|assets|lib|js)/[A-Za-z0-9_./-]+\.(?:glsl|wgsl|png|ttf|js))["'']'
    $refs = New-Object 'Collections.Generic.HashSet[string]'
    foreach ($src in @(Get-ChildItem -LiteralPath $webRoot -Recurse -Include *.js, *.html -File)) {
        foreach ($m in [regex]::Matches((Get-Content -LiteralPath $src.FullName -Raw), $refPattern)) {
            $rel = $m.Groups[1].Value
            $candidate = [IO.Path]::GetFullPath((Join-Path $src.DirectoryName $rel))
            if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
                $alt = [IO.Path]::GetFullPath((Join-Path $webRoot $rel))
                if (-not (Test-Path -LiteralPath $alt -PathType Leaf)) { [void]$refs.Add($rel) }
            }
        }
    }
    if ($refs.Count -gt 0) {
        Test-Fail 'every referenced web asset resolves' "unresolved: $(@($refs) -join ', ')"
    } else {
        Test-Ok 'every referenced web asset resolves'
    }

    Test-Hdr 'tier 1: publish payload'
    if (Test-Path -LiteralPath $GatePublishDir) { Remove-Item -LiteralPath $GatePublishDir -Recurse -Force }
    $publishOk = $true
    foreach ($proj in 'MatrixDesktop\MatrixDesktop.csproj', 'MatrixDesktopConfigurator\MatrixDesktopConfigurator.csproj') {
        & dotnet publish (Join-Path $RepoRoot $proj) -c Release -r win-x64 --no-self-contained `
            -p:PublishSingleFile=false -p:PublishDir="$GatePublishDir\" --nologo -v:minimal | Out-Null
        if ($LASTEXITCODE -ne 0) { $publishOk = $false; Test-Fail "publish $proj" }
    }
    if ($publishOk) {
        Test-Ok 'publish both executables'
        $required = @(
            'MatrixDesktop.exe', 'MatrixDesktop.dll', 'MatrixDesktop.deps.json', 'MatrixDesktop.runtimeconfig.json',
            'MatrixDesktopConfigurator.exe', 'MatrixDesktopConfigurator.dll',
            'MatrixDesktopConfigurator.deps.json', 'MatrixDesktopConfigurator.runtimeconfig.json',
            'web\index.html', 'configurator\index.html'
        )
        $missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $GatePublishDir $_) -PathType Leaf) })
        if ($missing.Count -gt 0) {
            Test-Fail 'payload contains every required file' "missing: $($missing -join ', ')"
        } else {
            Test-Ok 'payload contains every required file'
        }

        $junk = @(Get-ChildItem -LiteralPath $GatePublishDir -Recurse -File -Include *.pdb, *.xml -ErrorAction SilentlyContinue)
        if ($junk.Count -gt 0) {
            $bytes = ($junk | Measure-Object -Property Length -Sum).Sum
            Test-Fail 'payload carries no .pdb or .xml' ("{0} file(s), {1:N0} bytes: {2}" -f $junk.Count, $bytes, (($junk | Select-Object -First 4 | ForEach-Object { $_.Name }) -join ', '))
        } else {
            Test-Ok 'payload carries no .pdb or .xml'
        }

        if (Test-Path -LiteralPath (Join-Path $RepoRoot 'LICENSE') -PathType Leaf) {
            Test-Ok 'repository has a LICENSE'
        } else {
            Test-Fail 'repository has a LICENSE' 'no LICENSE at the repo root, and this is a public repo'
        }
    }

    if ($Tier1Only) {
        Test-Hdr 'tier 2 and 3: not requested'
        Write-Host '  runtime smoke omitted by -Tier1Only, an explicit caller decision' -ForegroundColor DarkGray
    } else {

        # -------------------------------------------------------- tier 2

        Test-Hdr 'tier 2: MatrixDesktop runtime smoke'
        if (-not [Environment]::UserInteractive -or [GateWin]::GetForegroundWindow() -eq [IntPtr]::Zero) {
            Test-Unchecked 'MatrixDesktop runtime smoke' 'no attached interactive session, so no window can be created or captured'
        } else {
            $exe = Join-Path $GatePublishDir 'MatrixDesktop.exe'
            if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
                Test-Fail 'MatrixDesktop runtime smoke' "publish output missing: $exe"
            } else {
                Assert-NoStaleInstance $exe
                New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null

                # suppressWarnings=true because the software-rendering notice is also green
                # on black, so a render assertion could otherwise pass on the notice.
                # --no-exit-on-any-key is mandatory: the app installs a global
                # WH_KEYBOARD_LL hook and exits on ANY key, so a keystroke in the driving
                # terminal would end the run.
                $base = '--windowed --no-exit-on-any-key suppressWarnings=true'
                $cases = [ordered]@{
                    'default'    = $base
                    'stripes-3d' = "$base --effect stripes --version 3d"
                    'regl-plain' = "$base --renderer regl --effect plain"
                }

                foreach ($caseName in $cases.Keys) {
                    $launchedAt = Get-Date
                    $dumpsBefore = Get-DumpCount
                    $started = Start-GateApp -Exe $exe -AppArgs $cases[$caseName]
                    $proc = $started.Process
                    $closeMethod = 'not-attempted'
                    try {
                        if ($started.Dialog) {
                            Test-Fail "smoke [$caseName] starts without a dialog" "app opened a modal dialog titled '$($started.Dialog)'"
                            continue
                        }
                        if ($proc.HasExited) {
                            if (Test-SessionChurn -Since $launchedAt) {
                                Test-Unchecked "smoke [$caseName]" 'process exited during an RDP session event, so this is environmental rather than attributable'
                            } else {
                                Test-Fail "smoke [$caseName] starts and stays running" "exited with code $($proc.ExitCode), and no RDP session event explains it"
                            }
                            continue
                        }
                        if (-not $started.Responsive) {
                            Test-Fail "smoke [$caseName] has a live message loop" 'WaitForInputIdle timed out'
                            continue
                        }
                        if ($started.Hwnd -eq [IntPtr]::Zero) {
                            Test-Fail "smoke [$caseName] creates a visible window" 'MainWindowHandle never became a visible window'
                            continue
                        }
                        Test-Ok "smoke [$caseName] window appeared in $($started.MsToWindow) ms"

                        # Calibrated: black at 500 ms, a uniform fill at 1-2 s, real content
                        # by 3 s. Two frames 700 ms apart so the animation can be proven live.
                        Start-Sleep -Milliseconds $SettleMs
                        $shotA = Join-Path $ShotDir "$caseName-a.png"
                        $shotB = Join-Path $ShotDir "$caseName-b.png"
                        $sizeA = Get-WindowCapture -Hwnd $started.Hwnd -Path $shotA
                        Start-Sleep -Milliseconds 700
                        $sizeB = Get-WindowCapture -Hwnd $started.Hwnd -Path $shotB

                        if (-not $sizeA -or -not $sizeB) {
                            Test-Unchecked "smoke [$caseName] render" 'PrintWindow returned no bitmap, so rendering cannot be judged either way'
                        } elseif ($sizeA -ne $sizeB) {
                            Test-Unchecked "smoke [$caseName] render" "client rect changed between frames ($sizeA then $sizeB), so the two are not comparable"
                        } else {
                            $stats = Get-RenderStats $shotA
                            $changed = Get-ChangedFraction -PathA $shotA -PathB $shotB
                            $detail = "std={0:N5} mean={1:N5} nonBlack={2:N5} moved={3:N5} {4}" -f $stats.Std, $stats.Mean, $stats.NonBlack, $changed, $sizeA
                            if ($stats.Std -lt $RenderStdFloor) {
                                Test-Fail "smoke [$caseName] rendered content" "$detail (std floor $RenderStdFloor; a black or uniform frame measures like this)"
                            } elseif ($stats.NonBlack -lt $RenderNonBlackFloor) {
                                Test-Fail "smoke [$caseName] rendered content" "$detail (non-black floor $RenderNonBlackFloor)"
                            } else {
                                Test-Ok "smoke [$caseName] rendered content ($detail)"
                                if ($changed -lt $RenderChangedFloor) {
                                    Test-Fail "smoke [$caseName] animation is running" "$detail (moved floor $RenderChangedFloor; the frame is static)"
                                } else {
                                    Test-Ok ("smoke [$caseName] animation is running (moved={0:N5})" -f $changed)
                                }
                            }
                        }

                        $userData = Join-Path (Split-Path $exe -Parent) 'userdata'
                        if (Test-Path -LiteralPath $userData) {
                            Test-Fail "smoke [$caseName] leaves no userdata beside the exe" "found $userData"
                        } else {
                            Test-Ok "smoke [$caseName] leaves no userdata beside the exe"
                        }

                        $webviewKids = Get-WebViewDescendantCount -RootPid $proc.Id
                    } finally {
                        $closeMethod = Stop-GateProcess $proc
                    }

                    # The log assertion has to prove it looked at something. Without the
                    # start marker, "no new ERROR lines" passes vacuously whenever logging
                    # is broken or the pid attribution is wrong.
                    $records = Get-LogRecordsForPid -ProcessId $proc.Id -Since $launchedAt
                    $marker = @($records | Where-Object { $_.Level -eq 'INFO' -and $_.Msg -like "$StartMarker*" })
                    if ($marker.Count -eq 0) {
                        Test-Fail "smoke [$caseName] log is attributable" "no '$StartMarker' record for pid $($proc.Id) in $LogPath, so an ERROR check would be vacuous"
                    } else {
                        $errors = @($records | Where-Object { $_.Level -eq 'ERROR' })
                        if ($errors.Count -gt 0) {
                            Test-Fail "smoke [$caseName] log has no ERROR" ($errors[0].Msg)
                        } else {
                            Test-Ok "smoke [$caseName] log clean, $($records.Count) record(s) for pid $($proc.Id)"
                        }
                    }

                    if ($closeMethod -eq 'WM_CLOSE') {
                        Test-Ok "smoke [$caseName] shut down via WM_CLOSE, so the cleanup path ran"
                    } else {
                        Test-Fail "smoke [$caseName] shuts down cleanly" "needed '$closeMethod', so OnFormClosing cleanup did not complete"
                    }

                    $survivors = Get-WebViewDescendantCount -RootPid $proc.Id
                    if ($survivors -lt 0) {
                        Test-Unchecked "smoke [$caseName] webview children reaped" 'Win32_Process query unavailable'
                    } elseif ($survivors -gt 0) {
                        Test-Fail "smoke [$caseName] webview children reaped" "$survivors msedgewebview2 process(es) still parented to the exited pid"
                    } else {
                        Test-Ok "smoke [$caseName] webview children reaped"
                    }

                    if ((Get-DumpCount) -gt $dumpsBefore) {
                        Test-Fail "smoke [$caseName] wrote no crash dump" "new dump in $DumpDir"
                    } else {
                        Test-Ok "smoke [$caseName] wrote no crash dump"
                    }
                    Start-Sleep -Milliseconds 700
                }
            }
        }

        # -------------------------------------------------------- tier 3

        Test-Hdr 'tier 3: MatrixDesktopConfigurator runtime smoke'
        if (-not [Environment]::UserInteractive) {
            Test-Unchecked 'configurator runtime smoke' 'no attached interactive session'
        } else {
            $cfgExe = Join-Path $GatePublishDir 'MatrixDesktopConfigurator.exe'
            if (-not (Test-Path -LiteralPath $cfgExe -PathType Leaf)) {
                Test-Fail 'configurator runtime smoke' "publish output missing: $cfgExe"
            } else {
                Assert-NoStaleInstance $cfgExe
                $launchedAt = Get-Date
                $started = Start-GateApp -Exe $cfgExe -AppArgs ''
                $proc = $started.Process
                $closeMethod = 'not-attempted'
                try {
                    if ($started.Dialog) {
                        Test-Fail 'configurator starts without a dialog' "modal dialog titled '$($started.Dialog)'"
                    } elseif ($proc.HasExited) {
                        if (Test-SessionChurn -Since $launchedAt) {
                            Test-Unchecked 'configurator starts' 'exited during an RDP session event'
                        } else {
                            Test-Fail 'configurator starts and stays running' "exited with code $($proc.ExitCode)"
                        }
                    } elseif ($started.Hwnd -eq [IntPtr]::Zero) {
                        Test-Fail 'configurator creates a visible window' 'MainWindowHandle never became visible'
                    } else {
                        Test-Ok "configurator window appeared in $($started.MsToWindow) ms"
                        Start-Sleep -Milliseconds $SettleMs
                        $shot = Join-Path $ShotDir 'configurator.png'
                        $size = Get-WindowCapture -Hwnd $started.Hwnd -Path $shot
                        if (-not $size) {
                            Test-Unchecked 'configurator rendered content' 'PrintWindow returned no bitmap'
                        } else {
                            $stats = Get-RenderStats $shot
                            $detail = "std={0:N5} mean={1:N5} {2}" -f $stats.Std, $stats.Mean, $size
                            if ($stats.Std -lt $RenderStdFloor) {
                                Test-Fail 'configurator rendered content' "$detail (std floor $RenderStdFloor)"
                            } else {
                                Test-Ok "configurator rendered content ($detail)"
                            }
                        }

                        $presets = Join-Path (Split-Path $cfgExe -Parent) 'MatrixDesktopConfigurator.presets.json'
                        if (-not (Test-Path -LiteralPath $presets -PathType Leaf)) {
                            Test-Unchecked 'configurator preset store parses' 'no presets file beside the exe; it may have fallen back to AppData'
                        } elseif ((Get-Item -LiteralPath $presets).Length -eq 0) {
                            Test-Fail 'configurator preset store parses' 'the presets file is 0 bytes, which Load() cannot parse (MD-06)'
                        } else {
                            try {
                                [void](Get-Content -LiteralPath $presets -Raw | ConvertFrom-Json)
                                Test-Ok 'configurator preset store parses'
                            } catch {
                                Test-Fail 'configurator preset store parses' $_.Exception.Message
                            }
                        }
                    }
                } finally {
                    $closeMethod = Stop-GateProcess $proc
                }

                $records = Get-LogRecordsForPid -ProcessId $proc.Id -Since $launchedAt
                $marker = @($records | Where-Object { $_.Level -eq 'INFO' -and $_.Msg -like "$StartMarker*" })
                if ($marker.Count -eq 0) {
                    Test-Fail 'configurator log is attributable' "no '$StartMarker' record for pid $($proc.Id)"
                } else {
                    $errors = @($records | Where-Object { $_.Level -eq 'ERROR' })
                    if ($errors.Count -gt 0) {
                        Test-Fail 'configurator log has no ERROR' ($errors[0].Msg)
                    } else {
                        Test-Ok "configurator log clean, $($records.Count) record(s)"
                    }
                }

                if ($closeMethod -eq 'WM_CLOSE') {
                    Test-Ok 'configurator shut down via WM_CLOSE'
                } else {
                    Test-Fail 'configurator shuts down cleanly' "needed '$closeMethod'"
                }
            }
        }
    }

    # ------------------------------------------------------------ summary

    Write-Host ''
    Write-Host ('=' * 74)
    Write-Host (" PASS {0}    FAIL {1}    UNCHECKED {2}" -f $script:Pass, $script:Fail, $script:Unchecked)
    Write-Host ('=' * 74)

    if ($script:Fail -gt 0) {
        Write-Host ''
        Write-Host 'GATE FAILED:' -ForegroundColor Red
        foreach ($f in $script:Failures) { Write-Host "  - $f" -ForegroundColor Red }
        if ($script:Unchecked -gt 0) {
            Write-Host 'and could not verify:' -ForegroundColor Yellow
            foreach ($u in $script:Uncheckables) { Write-Host "  - $u" -ForegroundColor Yellow }
        }
        exit 1
    }

    if ($script:Unchecked -gt 0) {
        Write-Host ''
        Write-Host 'CANNOT VERIFY:' -ForegroundColor Yellow
        foreach ($u in $script:Uncheckables) { Write-Host "  - $u" -ForegroundColor Yellow }
        Write-Host ''
        Write-Host 'Nothing failed, but this is not a pass. Resolve the above, or pass -Tier1Only' -ForegroundColor Yellow
        Write-Host 'if the omission is deliberate.' -ForegroundColor Yellow
        exit 2
    }

    Write-Host ''
    Write-Host ("GATE PASSED ({0} checks)" -f $script:Pass) -ForegroundColor Green
    exit 0
} finally {
    Pop-Location
}
