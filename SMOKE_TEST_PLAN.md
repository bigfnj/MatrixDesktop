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

| Artifact | Producer and location |
| --- | --- |
| Local portable folder | `publish-portable-win-x64-fd.cmd`, producing `publish\win-x64-fd\`. |
| Gate portable folder | `tests\run-gate.ps1`, producing `artifacts\gate\win-x64-fd\`. |
| Tagged release | `.github/workflows/release.yml`, producing `MatrixDesktop-<tag>-win-x64-fd.zip` and a SHA256 file. |

All supported folders contain both executables and their `web\` and
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
| `3` | The verification harness broke. |

CI surfaces exit 2 as warning annotations rather than silently treating missing
coverage as a pass. Exit 1 and 3 fail the workflow.

Tier 1 builds with no warnings, runs native regressions and JavaScript lifecycle
and UC3D scenarios, checks module syntax and referenced assets, and runs the
configurator DOM/render smoke and UC3D WebGL browser smoke. It also exercises
the importer's synthetic fixtures, embedded help and icons, version parity,
published embedded source-line symbols and payload rules. Separate `.pdb`
and dependency documentation `.xml` files are excluded from the portable folder.

The runtime tiers launch the published main app in default, stripes/3D and plain
WebGL configurations, then launch the configurator. They check real windows,
rendering, attributable logs, crash dumps and clean `WM_CLOSE` shutdown. Main-app
cases compare successive frames to detect frozen animation and inspect WebView2
child-process cleanup. A private gate profile avoids sharing the owner's live
WebView2 browser process.

`PrintWindow` captures are cropped to the client area. The main-app render
thresholds are a luminance standard deviation of at least 0.04 and a non-black
fraction of at least 0.02; animation requires at least 0.005 changed pixels.
These checks reject blank, uniform and frozen frames. Inspect screenshots too:
pixel statistics cannot establish visual correctness.

## UC3D backend and native integration probes

Use the installed Edge channel for actual hardware backend coverage:

```pwsh
python tests\uc3d-browser.py --renderer both --channel msedge
```

The probe asserts the selected backend rather than accepting silent fallback.
It checks animated 2D rain, photo RGB in the code field, visible relief, literal
caption text, the clock and disposal. It records the served shader's digest and
writes synthetic screenshots and a report to `artifacts\uc3d\`. A missing WebGPU
adapter returns exit 2. A shader/device failure is a failure, not a WebGPU pass.

After publishing, exercise the native applications and their media bridge:

```pwsh
python tests\native-uc3d-smoke.py
python tests\native-uc3d-smoke.py --publish publish\win-x64-fd --donor-directory C:\Private\UC3D-media
```

The default publish input is `artifacts\gate\win-x64-fd\`. These probes open
test-owned WebView2 debugging profiles and verify both native backends, granted
local image resources, rejection of ungranted URLs, and configurator preview
bootstrap data. The optional donor input also exercises the imported manifest,
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
its own fixture and application. `--windowed` additionally requires an
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

## Interactive and hardware checks

The automated probes do not certify the following behavior. Run the rows
affected by a change, using the published application.

| Area | Check |
| --- | --- |
| Icons and help | Both EXEs show the Matrix icon in Explorer and the taskbar. `--help`, `--help-full` and the configurator's `?` show readable help. These dialogs block until dismissed. |
| Multi-monitor and window modes | Default borderless mode spans the intended displays; `--single-monitor`, `--monitor N` and `--working-area` use the expected bounds. Resize a windowed instance and check for blank frames or crashes. |
| Physical key exit | Foreground physical keys close the default app. `--no-exit-on-any-key --exit-on-esc` permits ordinary keys but closes on Esc. Both exit modes disabled permit closing via the window. Injected input is intentionally ignored. |
| Cursor and DevTools | `--hide-cursor --no-devtools --topmost` hides the app cursor, suppresses DevTools shortcuts and maintains the expected z-order. Closing restores cursor state. |
| Webcam | `--effect mirror --camera true` obtains permission, renders camera input and releases the camera after closing. This needs an actual camera. |
| Looking Glass | The Matrix scene's Holoplay preset requires real device calibration and uses the WebGL quilt path. No-device and renderer-routing tests do not certify a physical display. UC3D has no quilt implementation. |
| Click ripples | Enable `clickRipples` with palette/stripe effects and test the triangle, star and box shapes in both renderers. Without the option, ordinary rain should not gain click ripples. |
| Presets and launch controls | A fresh store includes `UC3D Trilogy` and the older starters. Save, rename and delete a preset; reopen and verify persistence. Edited starters stay edited, and deleted starters stay deleted. `Test Argument` replaces its previous instance and `Stop Test` closes it. |
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
