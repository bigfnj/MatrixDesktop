# MatrixDesktop smoke and regression checks

Run a smoke check after each substantial change, then the relevant regression
tests. Before committing or releasing, run the full gate and verify the published
application on Windows. A headless browser render alone does not verify WinForms,
WebView2 integration, native media grants or application shutdown.

## Environment and artifacts

The supported framework-dependent package needs Windows x64, the .NET 10 Desktop
Runtime and Microsoft Edge WebView2 Runtime. Browser probes use a Python
interpreter with Playwright and Pillow. The gate accepts `MD_GATE_PYTHON` to
select that interpreter. Its browser smoke also needs Playwright Chromium.

The browser suites launch Chromium through `tests\browser_launch.py`, which disables
`TcpPortRandomizationWin` merged into Playwright's own `--disable-features` and
verifies the launched browser's argv on every launch. A launch whose argv fails
that check raises an error and closes that browser, so no check runs on it. The
web smoke, the ZMatrix configurator suite, `uc3d-browser.py`,
`uc3d-audio-browser.py`, `uc3d-calibration.py`, `matrix-playback-browser.py`
and `zmatrix-browser.py` report that error as a harness error and exit 3; the
helper's own self-test, `tests\browser-launch.test.py`, counts it as a failed
check. To build the merged switch, the helper first reads
Playwright's own switch from a probe launch's argv. When
that argv cannot be read, the suite's browser launches with its own arguments
and `TcpPortRandomizationWin` is not disabled. When only the merged launch's
argv cannot be read, the switch was requested but is not confirmed. Either way
the launch goes ahead and prints a `DEGRADED` line each time, which the gate
reports as `UNCHECKED` beside that case's own verdict. The one captured
web smoke renderer stall was a module request to its own loopback origin that
failed with `net::ERR_NO_BUFFER_SPACE`, which is how Chromium reports Windows
error 10055. Windows can return 10055 when a socket cannot get a local port, and
Chromium's randomized local port pick on Windows (`TcpPortRandomizationWin`;
microsoft/playwright#43545, #43605) can return it while ports are free. A
bounded A/B on one machine strongly supports that feature as the cause; it is
not proven. The favicon checks in the web smoke and the ZMatrix configurator
suite need the full Chromium build (`python -m playwright install chromium`),
because the headless shell never requests a favicon. Without that build each
prints an `UNVERIFIED` line instead of failing, so a run with nothing else
wrong exits 2.

| Artifact | Producer and location |
| --- | --- |
| Local portable folder | `publish-portable-win-x64-fd.cmd`, producing `publish\win-x64-fd\`. |
| Gate portable folder | `tests\run-gate.ps1`, producing `artifacts\gate\win-x64-fd\`. |
| Tagged release | `.github/workflows/release.yml`, producing `MatrixDesktop-<tag>-win-x64-fd.zip` and a SHA256 file, after checking the tag against both csproj, the guide header and README's current source version and requiring CI success on the tagged commit. |

All supported folders contain both executables, `MatrixDesktop.scr`, and their `web\` and
`configurator\` assets. Test the extracted release zip before announcing a
release. CI calls the tier 1 gate, which owns the Release build and publish;
there is no separate CI publish sequence.

## Full verification gate

From the repository root:

```pwsh
powershell.exe -NoProfile -File tests\run-gate.ps1
pwsh -NoProfile -File tests\run-gate.ps1 -Tier1Only
```

Use Windows PowerShell 5.1 for the first command and PowerShell 7 for the CI
parity check. `-Tier1Only` explicitly omits the interactive application tiers.

| Exit code | Meaning |
| --- | --- |
| `0` | Every selected check ran and passed. |
| `1` | At least one definite failure. |
| `2` | At least one selected check could not be verified; inspect every `UNCHECKED` line. |
| `3` | The verification harness broke, or a check's own harness broke and no check failed; never a verdict on the application. The web smoke's exit 3 (a loopback error on its server's origin, a server that stopped answering, a missing fixture, a crash) is the second kind, and so is another tier 1 Python case's exit 3 with a `HARNESS ERROR` line and no FAIL line: the run continues, and the summary names each check reported as `HARNESS`. A FAIL in the same run keeps exit 1. |

CI surfaces exit 2 as one warning annotation that names every `UNCHECKED` item;
each item's reason stays in the job log, on the line below its `UNCHECKED` line
and in the gate's closing list. Missing coverage is never silently treated as a
pass. Exit 1 and 3 fail the workflow.

Tier 1 builds with no warnings, runs native regressions and JavaScript lifecycle
and UC3D scenarios, checks module syntax and referenced assets, and runs the
configurator DOM/render smoke, UC3D/ZMatrix WebGL browser smoke and original
Matrix pause/resume. It also runs `tests\browser-launch.test.py`, which proves on
real headless launches that an unreadable argv prints `DEGRADED` and, for both
shapes Playwright's own switch can take, that the shared helper's launch passes
argument verification and each of three broken merges is named exactly.
`tests\test_web_smoke_verdict.py` holds a missing full Chromium build to the web
smoke's `UNVERIFIED` exit 2, never a FAIL. Native GDI font tests exercise actual
raster metrics, rotation, decorations, substitution, DPI and owned-handle cleanup
without a shell window. The tier also exercises
the importer's synthetic fixtures, embedded help and icons, version parity,
published embedded source-line symbols and payload rules. Separate `.pdb`
and dependency documentation `.xml` files are excluded from the portable folder.

Tier 1 also holds the configurator's effective defaults for every version and
effect to what `config.js` and both stripe passes render. It runs the UC3D
calibration probe with its WebGPU adapter hidden (`MD_UC3D_HIDE_WEBGPU_ADAPTER=1`)
and passes it only on exit 2 with a `CANNOT VERIFY WebGPU` line and no WebGPU pass.
The symbol check also scans the published `MatrixDesktop.dll` IL for the host's
startup-failure, surface-close, wallpaper single-instance and takeover, WebView2
process-failure, session-end, camera-permission, mouse-hook and tray Start with
Windows wiring. It follows the surface-close, renderer-failure, rebuild-backoff
and wallpaper-ownership decisions, a renderer reload's reset of the
suspended flag, and `WndProc` for every message id from 0 to 0xFFFF, which
must set the session-end flag only while `WM_QUERYENDSESSION` is dispatched,
along every IL path with chosen inputs, so the
check still fails when a call survives behind an inverted condition or a
constant argument. Each WebView2 initialisation must await the helper that
clears a saved camera denial before it navigates, and that helper may reset a
saved permission setting only on
`HostPermissionPolicy.ForgetsSavedState`'s true answer: no reset may be
reachable without passing that question, whether from the helper's entry,
from that answer's false edge or from any exception handler. The walk
follows normal flow only and does not enter the methods it
calls, so these checks guard against regressions rather than model the CLR
exactly. Version parity runs
`tests\check-release-version.ps1`, which `release.yml` runs with the tag, and a
separate check requires
`release.yml` to run its tag and CI checks before its first `dotnet publish`. Both
published `runtimeconfig.json` files must keep ICU globalization, and the local
framework-dependent publish profiles must not change the build beyond what the
release sets. The gate's own decision library runs its self-tests under Windows
PowerShell 5.1 and PowerShell 7.

The runtime tiers launch the published main app in default, stripes/3D and plain
WebGL configurations, then launch the configurator. They check real windows,
rendering, attributable logs, crash dumps and clean `WM_CLOSE` shutdown. Main-app
cases compare successive frames to detect frozen animation and inspect WebView2
child-process cleanup. A private gate profile avoids sharing the owner's live
WebView2 browser process. Because that profile replaces the folder the app
requests, each main-app case reads the app's own log record to confirm it asked
for `%LOCALAPPDATA%\MatrixDesktop\WebView2`, and fails if a new folder appears
beside the exe. A crash dump fails a case only when that case's process wrote it.

`PrintWindow` captures are cropped to the client area. The main-app render
thresholds are a luminance standard deviation of at least 0.04 and a non-black
fraction of at least 0.02; animation requires at least 0.005 changed pixels.
These checks reject blank, uniform and frozen frames. Inspect screenshots too:
pixel statistics cannot establish visual correctness.

Each main-app case's first frame is captured after the 6 s settle (`-SettleMs`)
and again every second until it clears the luminance and non-black floors, for
at most 20 s after the window appears. The tier 3 configurator capture is polled
the same way against the luminance floor alone. WebView2 child cleanup waits up
to 15 s. For
tiers 2 and 3 the gate keeps the display and the system on and prints
`display hold: on`, or `display hold: unavailable` when Windows refuses. A
main-app frame under a floor, or a frozen one, is `UNCHECKED` rather than a
failure when that case's process logged that it suspended, or tried to suspend,
its WebView for display-off, a locked or disconnected session or sleep. A
failing tier 3 probe script is `UNCHECKED` when any MatrixDesktop process
logged such a suspension while it ran.

## ZMatrix, wallpaper and screensaver checks

After each focused smoke passes, retain the existing Matrix/UC3D regressions:

```pwsh
node tests\zmatrix-tests.mjs
node tests\host-playback-tests.mjs
node tests\host-pointer-tests.mjs
node tests\native-bootstrap-tests.mjs
node tests\uc3d-audio-tests.mjs
dotnet run --project tests\MatrixDesktop.Tests -c Release
dotnet run --project tests\MatrixDesktop.Native.Tests -c Release
python tests\zmatrix-browser.py --channel msedge
python tests\matrix-playback-browser.py --channel msedge
python tests\uc3d-audio-browser.py --channel msedge
```

The ZMatrix browser test checks served source bytes, actual selected backends,
visible animation, pause/resume, resize and disposal. On REGL at 30 fps it also
requires at least one `gl.getError` read and at most one every ten frames, not
one every frame, and a recorded WebGL error to still fail the scene with its
notice within 30 frames. Original Matrix
also starts paused before renderer import. Under that pause it must draw exactly
one frame, not a uniform fill, and hold it, and a real WebGL uniform witness must
begin at active shader tick zero so ping-pong initialization cannot be skipped.
Original Matrix at 30 fps, UC3D and ZMatrix must likewise hold one frame when the
host pause arrives before the renderer starts. On REGL, the
original Matrix test also requires the shader time uniform to stay small after
four days of page time while the tick keeps counting, and `fps=0` and `once=true`
to each draw exactly one non-black frame that survives a window resize. A visible
host pause must park the frame loop and resume where it froze, also when a lock
hid the page during the pause and the unlock showed it again before the resume;
a hidden one still restarts shader time, and with `skipIntro=false` that restart
does not replay the intro. At 30 fps, a pause that lands right after a first
frame that skipped rendering must still draw one frame and hold it. Plug me in
must survive a recovery reload. Browser fallback warnings
are expected when no native GDI bridge is attached.

Against a complete published folder:

```pwsh
python tests\wallpaper-smoke.py --app artifacts\gate\win-x64-fd\MatrixDesktop.exe --output artifacts\wallpaper-smoke
python tests\native-zmatrix-configurator.py --publish artifacts\gate\win-x64-fd --renderer both
python tests\screensaver-smoke.py --scr artifacts\gate\win-x64-fd\MatrixDesktop.scr
```

Wallpaper checks cover all three scenes and both graphics backends, the real
WorkerW parent, native fonts and clean shutdown. Run these focus-sensitive
checks sequentially with configurator/UI probes. A concurrently activated
application can invalidate the unchanged-foreground witness. Without `--output`
the wallpaper probe writes its evidence to a new temporary folder and deletes
each case's WebView2 profile there; a folder given with `--output`, such as the
gate's, keeps the profile, which the next run reuses.

Before its configurator cases, the native configurator probe works on private
copies of the published folder with synthetic stores. A second configurator on
the same store must exit 0 and leave the store and its backup unchanged. The
configurator must answer `WM_QUERYENDSESSION`, keep running after a cancelled
shutdown and exit, ending its Test host, when the session really ends. The probe
also launches the published `MatrixDesktop.exe` windowed with its own WebView2
profile; that host must answer `WM_QUERYENDSESSION`, keep rendering after the
shutdown is cancelled and exit 0 on `WM_CLOSE`. A Test window that closes by
itself must be reported to the page once, and `Stop Test` must still end the
current Test host after a replaced one exits.

The screensaver probe supplies an owned synthetic profile and preview parent.
It checks child bounds/resize/parent death, fullscreen coverage despite saved
wallpaper/working-area flags, and input exit. It sends Escape only after proving
the owned saver is foreground. It never registers a screensaver or loads the
owner's saved media. Registration tests use disposable synthetic registry keys;
they cover exact backup/restore, external choices, re-registration and rollback.

Native wallpaper captures may contain the owner's wallpaper. Keep all artifacts
private. Shell-recovery verification requires a separately coordinated Explorer
restart; the ordinary gate does not restart Explorer or change Windows startup,
wallpaper or screensaver preferences. Pause-reason and fullscreen policy cases
include negative-coordinate monitors and independent lock/manual/battery states.

For the coordinated shell check, start an owned wallpaper instance with its own
`WEBVIEW2_USER_DATA_FOLDER` and record its PID; a wallpaper launched in the
owner's single-instance scope would replace the owner's running wallpaper. Pause
it through the tray, restart Explorer, then verify that the same PID
attaches to a WorkerW owned by the new Explorer process. Manual pause must survive;
tray Resume must restore visible animation without runtime errors. Exit through
the tray and verify clean shutdown. Also check fresh wallpaper startup after a
shell restart, since it must create a missing WorkerW without changing icon styles.

For renderer recovery, start an owned wallpaper the same way, with a
remote-debugging port in `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`. Crash its page
with the DevTools Protocol `Page.crash` command: within 10 seconds the same PID
must show the page again, and the log must contain both
`kind=RenderProcessExited` and `Renderer failure RenderProcessExited: Reload.`
for that PID. Then end that wallpaper's `msedgewebview2` browser process: within
10 seconds a new surface must attach to WorkerW in the same PID, with
`Renderer failure BrowserProcessExited: Recreate.` in the log. A fullscreen launch
with its own profile must instead log `Renderer failure BrowserProcessExited: Exit.`
and exit when its browser process ends.

## UC3D backend and native integration probes

Use the installed Edge channel for actual hardware backend coverage:

```pwsh
python tests\uc3d-browser.py --renderer both --channel msedge
python tests\uc3d-calibration.py --renderer both --channel msedge
```

The probe asserts the selected backend rather than accepting silent fallback.
It checks animated 2D rain, photo RGB in the code field, visible relief, literal
caption text, the clock and disposal. The relief check compares a raised and a
flat picture frame, each held only once the picture weight reads 0.999 or more,
and reads that weight back from each held frame. On the REGL backend it also
serves synthetic desktops one pixel over `MAX_TEXTURE_SIZE` on each axis, which must
render downscaled with their aspect kept and the right way up, and one at the
limit, which must upload unchanged. It records the served shader's digest and,
on REGL, the served `regl/uc3dPass.js` digest, and writes synthetic screenshots
and a report to `artifacts\uc3d\`. A missing WebGPU
adapter returns exit 2. A shader/device failure is a failure, not a WebGPU pass.

The calibration probe checks 22 original-control cases per backend: slider
endpoints, captured values, isolated motion, 2D and portrait. It advances a
controlled clock to 20 and 20.25 seconds, inspects the actual running population,
size and stream state, captures frames and verifies disposal. It records served
source digests. A missing WebGPU adapter makes it print `CANNOT VERIFY WebGPU`
and return exit 2; `MD_UC3D_HIDE_WEBGPU_ADAPTER=1` simulates one.
`--only amount-500,flat` selects a focused subset. Optional
`--donor-atlas C:\Private\UC3D-media\glyphs.bmp --output C:\Private\comparison`
uses a local atlas and requires a destination outside the repository. Such images
remain private. The unchanged original-process harness and comparison evidence
are recorded in [UC3D calibration](docs/UC3D-CALIBRATION.md).

After publishing, exercise the native applications and their media bridge:

```pwsh
python tests\native-uc3d-smoke.py
python tests\native-uc3d-smoke.py --case defaults --renderer both
python tests\native-uc3d-smoke.py --publish publish\win-x64-fd --donor-directory C:\Private\UC3D-media
```

The default publish input is `artifacts\gate\win-x64-fd\`. These probes open
test-owned WebView2 debugging profiles and verify both native backends, granted
local image resources, rejection of ungranted URLs, and configurator preview
bootstrap data. Each launch's private profile is deleted when that launch ends,
after the processes the application started exit (it waits up to 30 s). A
profile that still cannot be deleted is named in a `WARN` line and counted in
one `LEAKED` line when the probe exits. The gate reads the native smoke's log and
each tier 3 case's log for those lines, and for a `WARN` that processes the
application started outlived the wait, and reports
`UNCHECKED  WebView2 profile cleanup [<case>]` beside the case's own verdict,
naming the `LEAKED` line when there is one, otherwise the first such `WARN`, and
the log. Profiles older than a day are pruned
when the probe starts. The optional
donor input also exercises the imported manifest,
glyph atlas and explicit sound-test action. Screenshots and reports under
`artifacts\native-uc3d\` can contain donor media; keep that directory private.
Do not run a publish that replaces this folder while its executables are open.

Desktop capture has a separate test:

```pwsh
python tests\desktop-snapshot-smoke.py --self-test
python tests\desktop-snapshot-smoke.py
```

It first covers the virtual desktop with a synthetic test window, checks that
the capture region is unobstructed, then starts UC3D with desktop capture enabled.
It validates every captured pixel against the synthetic palette, capture bounds,
the opt-in media grant and `no-store` response. It also checks that the published
web tree is unchanged. It writes a report, not captured image pixels, and closes
its own fixture and application, then deletes the application's private WebView2
profile under its run folder. `--windowed` additionally requires an
unobstructed full-desktop fixture for the windowed initial-bounds case.

## Focused regression commands

The full gate already runs the first three commands below. Use them for a
focused rerun after a relevant smoke passes:

```pwsh
node tests\renderer-lifecycle.mjs
node tests\uc3d-tests.mjs
python tools\test_import_uc3d_media.py
python tests\bloom-probe.py --hardware --channel msedge
```

The bloom probe compares GPU readbacks for the questioned LOD/texture-format
changes and includes changed-output witnesses. It returns exit 2 if WebGPU
readback is unavailable. Do not infer a visual fix from a suspicious-looking
shader expression without measuring its effect.

For a new check or gate, deliberately introduce the defect it should detect,
confirm the intended named failure, restore the source and rerun. For compiled
tests, verify the artifact was rebuilt and its test witness actually executed.
A probe that exits 2 for an unavailable environment must also print a line that
starts with `CANNOT VERIFY`, `UNVERIFIED` or `UNCHECKED`. In the gate, the headless
web smoke, the Python cases under `tier 1: UC3D importer and WebGL smoke` and the
tier 3 wallpaper, screensaver and configurator cases treat exit 2 without that
line as a failure, because argparse usage errors and missing scripts also exit 2.

A browser launch whose argv could not be read back (`tests\browser_launch.py`
prints a line starting `DEGRADED`, because `TcpPortRandomizationWin` was not
disabled or not verified) is reported as
`UNCHECKED  browser launch verification [<case>]` beside that case's own
verdict. The gate reads both logs of the headless web smoke and of every Python
case under `tier 1: UC3D importer and WebGL smoke` for such lines, counting only
unindented ones, so a degraded launch makes the gate exit 2 even when the suite
passed, unless a check failed or a harness broke. When the web smoke fails a
check or exits 3, the gate also reports its `UNVERIFIED` lines as
`UNCHECKED  headless web smoke checks this environment cannot run`.

`tests\web-smoke.py` gives a check a `HARNESS` verdict for only two causes: a
smoke server that does not answer `HEAD /` (probed directly, never through a
proxy), or a request to its own origin that failed with
`net::ERR_NO_BUFFER_SPACE`. Its `HARNESS` lines name the check, the leg for a
check with several, and the URL and error or the server that did not answer.
Every other renderer failure, including a 404 of a shipped module or a request
to another origin, stays a FAIL. On a page the harness broke, an HTTP error
status, or a failed request to another origin that the check did not block itself, is
still a FAIL beside the `HARNESS` line, and a product failure in another leg of
the same check is still that check's FAIL. The smoke exits 3 when it has
`HARNESS` lines and no FAIL, and also when its loadState fixture is missing or it
stops on an exception. Its exit mapping is itself a checked case
("the exit code is 0 for a pass, 3 for harness errors alone and 1 for any
FAIL"). The gate reports the web smoke's exit 3 as a `HARNESS` line, never a
FAIL, and prints the smoke's `HARNESS` lines beside its ok and FAIL lines.

A Python case under `tier 1: UC3D importer and WebGL smoke` that exits 3 counts
as a harness error (`HARNESS`; the gate ends in exit 3 unless a check failed)
only when it printed a line starting `HARNESS ERROR` and no line starting
`FAIL`. A bare exit 3, or an exit 3 beside a FAIL line, is a FAIL, because a
FAIL outranks a harness error; its detail names the first FAIL line and the
`HARNESS ERROR` line. `uc3d-browser.py`, `uc3d-audio-browser.py`,
`uc3d-calibration.py`, `matrix-playback-browser.py` and `zmatrix-browser.py`
print `HARNESS ERROR: <script>: <error>` and exit 3 when a browser launch fails
argument verification (`browser_launch.LaunchArgumentsError`). The ZMatrix
configurator suite prints `HARNESS ERROR` with the error's type and message on
its error output and exits 3. It can do so after earlier checks printed FAIL
lines, when its last check's full Chromium launch fails verification, and the
gate then reports a FAIL.

## Interactive and hardware checks

The automated probes do not certify the following behavior. Run the rows
affected by a change, using the published application.

| Area | Check |
| --- | --- |
| Icons and help | Both EXEs show the Matrix icon in Explorer and the taskbar. `--help`, `--help-full` and the configurator's `?` show readable help. These dialogs block until dismissed. |
| Multi-monitor and window modes | Default borderless mode spans the intended displays; `--single-monitor`, `--monitor N` and `--working-area` use the expected bounds. Resize a windowed instance and check for blank frames or crashes. |
| Physical key exit | Foreground physical keys close the default app. `--no-exit-on-any-key --exit-on-esc` permits ordinary keys but closes on Esc. Both exit modes disabled permit closing via the window. Injected input is intentionally ignored. |
| Cursor and DevTools | `--hide-cursor --no-devtools --topmost` hides the app cursor, suppresses DevTools shortcuts and maintains the expected z-order. Closing restores cursor state. |
| Webcam | `--effect mirror --camera true` obtains permission, renders camera input and releases the camera after closing. This needs an actual camera. With a fresh WebView2 profile, `--wallpaper --effect mirror --camera true` renders rain at once with no prompt and logs `Answered a Camera request with Deny and no prompt`. `/s` must do the same, but it ignores the rest of its command line and takes its arguments from the screensaver profile: save a draft with Effect Mirror and Camera on through **Use for screensaver**, or point `MATRIXDESKTOP_SCREENSAVER_PROFILE` at a file holding `["--effect","mirror","--camera","true"]`. After a Block at the windowed prompt, the next windowed launch prompts again. |
| Looking Glass | The Matrix scene's Holoplay preset requires real device calibration and uses the WebGL quilt path. No-device and renderer-routing tests do not certify a physical display. UC3D has no quilt implementation. |
| Click ripples | Enable `clickRipples` with palette/stripe effects and test the triangle, star and box shapes in both renderers. Without the option, ordinary rain should not gain click ripples. |
| Presets and launch controls | A fresh store includes `UC3D Trilogy`, `ZMatrix Desktop` and the older starters. Save, rename and delete a preset; reopen and verify persistence. Edited starters stay edited, and deleted starters stay deleted. `Test Argument` replaces its previous instance and `Stop Test` closes it. |
| Import and randomize | Import a full command and a raw query; verify values and output. Stripe colors are emitted only for stripe effects. Randomize respects its selected scope and leaves Launch settings unchanged. |
| UC3D interaction and sound | Enable 3D mouse rotation; move, stop with right-click, reset with left-click and roll with the wheel. After 20 seconds idle, automatic motion resumes. Check personal picture aspect ratio, source selection, captions, clock, optional snapshot and audible sound. Preview sound requires an explicit button action. |
| Lock and resume | Lock/unlock or sleep/wake while running. Check that the WebView suspends and the scene resumes without a blank render, stuck sound or duplicate callbacks. |

Extreme valid settings can produce an unhelpful image. For example, the Matrix
scene clamps `numColumns=0` to one column and `resolution=0` to 0.05; those floors
prevent invalid allocations but do not promise a useful picture. Check that
extreme inputs remain responsive rather than describing them as safe visual
defaults.

## Evidence and failure reporting

Keep the command line, artifact version, Windows/WebView2 versions, actual
backend, GPU and monitor layout with the result. For UC3D, inspect
`window.matrixRuntimeDiagnostics` for readiness, frame count, warnings, errors
and disposal state. Verify rendering with both screenshots and app behavior.

Normal renderer teardown now cancels callbacks, stops camera tracks and releases
owned GPU resources. Regression tests exercise those paths; they do not prove
the absence of every memory leak. If testing a suspected leak, use a bounded
run with a reproducible interaction sequence and compare resource counts after
cleanup. Record unsupported hardware or skipped checks as unverified.
