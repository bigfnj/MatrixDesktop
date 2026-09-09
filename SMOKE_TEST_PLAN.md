# MatrixDesktop Smoke Test Plan

## Scope

Validate the Windows x64 framework-dependent artifact enough to catch launch, asset loading,
renderer fallback, CLI parsing, windowing, and shutdown regressions.

**Read the next section before running anything by hand.** Most of the original case list is
now checked automatically by `tests\run-gate.ps1`, and re-running those by hand is wasted
effort. What is left is a short list of things a script genuinely cannot do.

## What is automated, and what is not

Run the gate first:

```pwsh
pwsh -File tests\run-gate.ps1
```

Exit `0` pass, `1` real failure, `2` something could not be verified (not a pass — the list
is printed), `3` the gate itself broke. `-Tier1Only` skips the runtime tiers and is what CI
uses, because tiers 2 and 3 need an attached interactive desktop session to create and
capture real windows.

### Automated by the gate — do not re-test by hand

Tier 2 launches `MatrixDesktop.exe` from the publish output in three configurations
(`default`, `stripes-3d`, `regl-plain`, all with `--windowed --no-exit-on-any-key
suppressWarnings=true`) and asserts, per configuration:

- the process starts and stays running, and opens no modal dialog
- it has a live message loop (`WaitForInputIdle`)
- it creates a visible window, and reports how long that took
- it **actually rendered**, from a `PrintWindow` capture measured against explicit
  thresholds: pixel std dev >= 0.04 and non-black fraction >= 0.02. A black or uniform frame
  fails here rather than passing as "it launched"
- the **animation is running**, from the measured change between two captures >= 0.005
- no new `ERROR` in the log, and the log is *attributable*: the check first proves there is
  a start record for the launched pid, so a missing log cannot make the ERROR check pass
  vacuously
- no crash dump appeared in `%LOCALAPPDATA%\MatrixDesktop\dumps\` during the run
- shutdown completed via `WM_CLOSE`, proving the `OnFormClosing` cleanup path ran rather
  than the process being killed
- no orphaned `msedgewebview2` children survive the exited pid
- no `userdata/` folder was created beside the EXE

Tier 3 does the equivalent for `MatrixDesktopConfigurator.exe` (start, visible window,
render assertion, attributable log, no ERROR, clean `WM_CLOSE`) and additionally parses the
presets file beside the executable, failing on a 0-byte file that `Load()` cannot read.

Tier 1 additionally covers, without launching anything: the Release build at 0 warnings, the
unit harness, `node --check` on every first-party web module, resolution of every shader and
asset path referenced from JS or HTML, the embedded `Matrix.ico` and argument guide in both
assemblies, and the publish payload's required files with no `.pdb` or `.xml`.

### Manual only — a script cannot do these

| Case | Why it stays manual |
| --- | --- |
| 2, help dialog | `--help` and `--help-full` both call `ShowDialog()`. **Never run unattended** — the process blocks until a human dismisses it, and the gate would hang. |
| 9, camera | Needs real webcam hardware and a real OS permission grant. |
| 10, second monitor | Needs a physical multi-monitor layout; also the only test of borderless-across-all-displays, which the gate does not exercise (it runs `--windowed`). |
| 15-18, configurator UI | Every one is a click-and-observe interaction: preset save/rename/delete, `Test Argument` launch and replace, `Import` paste and stripe gating, `Randomize` scopes. The gate proves the configurator starts and renders; it proves nothing about what the buttons do. |
| 8, click ripples | Requires generated mouse clicks on the canvas. |
| 11, 12, key-exit and locked-down flags | The gate must pass `--no-exit-on-any-key` to survive its own driving terminal, so the key-exit paths are deliberately outside its reach. Cursor hiding and DevTools suppression are also visual. |
| 5, degenerate values | See the corrected expectation below; the outcome needs a human eye. |

Cases 1, 3, 4, 6, 7, 13 and 14 are partly or wholly covered above. Run them by hand only
when changing the relevant code.

## Test Environment

- Windows 10 version 1809+ or Windows 11, x64.
- .NET 10 Desktop Runtime installed.
- Microsoft Edge WebView2 Runtime installed.
- Optional: multi-monitor setup, webcam, and a GPU/browser runtime with WebGPU enabled.

## Artifact

There are two real artifacts. Nothing in this repository produces a file named
`MatrixDesktop-win-x64-fd-smoke.zip`, which earlier revisions of this document named.

1. **Release zip.** `release.yml` runs on a `v*` tag and writes
   `MatrixDesktop-<tag>-win-x64-fd.zip` (for example `MatrixDesktop-v1.0.1-win-x64-fd.zip`)
   to the workflow's working directory, alongside a `.sha256`, and attaches both to the
   GitHub Release. Extract it and run from the extracted folder. This is what an end user
   receives, so it is the artifact to test before announcing a release.

2. **Local publish folder.** `publish-portable-win-x64-fd.cmd` produces
   `publish\win-x64-fd\` containing both executables plus the `web\` and `configurator\`
   assets. No zip. This is the fastest way to smoke a local change.

`ci.yml` produces neither; it publishes into each project's
`bin\Publish\win-x64-fd\` and only checks that the two executables exist. The gate publishes
into `artifacts\gate\win-x64-fd\`.

Primary executable:

```cmd
MatrixDesktop.exe
```

Companion configurator:

```cmd
MatrixDesktopConfigurator.exe
```

Run commands from inside the artifact folder so relative paths and `web/` assets are present.
WebView2 profile/cache data is stored under `%LOCALAPPDATA%\MatrixDesktop\WebView2\` by
default, not beside the EXE. Bundled web assets are staged under
`%LOCALAPPDATA%\MatrixDesktop\Web\` before WebView2 loads them.

Icon precheck:

- In Explorer, both `MatrixDesktop.exe` and `MatrixDesktopConfigurator.exe` should show the
  Matrix icon.
- Launch each app and confirm the taskbar button also uses the Matrix icon while the window
  is open.

## Smoke Cases

1. Default launch — *gate covers the windowed variant; the borderless span is manual*
   - Run: `MatrixDesktop.exe`
   - Expected: borderless Matrix rain appears across displays, no missing asset dialog, black startup background, any physical key exits when the app is foreground.

2. Help dialog — **manual only, never unattended**
   - Run: `MatrixDesktop.exe --help`
   - Expected: help dialog appears and app exits after closing it.
   - Run: `MatrixDesktop.exe --help-full`
   - Expected: scrollable argument reference appears and app exits after closing it.
   - Both use `ShowDialog()` and block until dismissed. Do not put either in a script.

3. Windowed WebGL renderer — *gate covers via the `regl-plain` case; resize is manual*
   - Run: `MatrixDesktop.exe --windowed --version classic --renderer regl --no-exit-on-any-key`
   - Expected: normal resizable window opens at a usable size, animation renders, resizing does not blank or crash.

4. Presets and query conversion — *gate covers `--version 3d`; the raw query form is manual*
   - Run: `MatrixDesktop.exe --windowed --version resurrections --effect palette --numColumns 70 --resolution 0.75`
   - Expected: Resurrections glyph/palette variant renders.
   - Run: `MatrixDesktop.exe "?version=3d&effect=plain&fallSpeed=0.4"`
   - Expected: raw query string is accepted and renders.

5. Parameter hardening — **manual; expectation corrected below**
   - Run: `MatrixDesktop.exe --windowed --numColumns 0 --density 0 --resolution 0 --palette ""`
   - Expected: **the app does not crash, and the output is not usable.** Measured behaviour:
     - `--numColumns 0` alone renders a **pure black frame**.
     - `--numColumns 0` combined with `--resolution 0` renders **two enormous white
       rectangles**.
   - Numeric clamping does happen — `web/js/config.js` clamps `numColumns` to `[1, 256]`,
     `density` to `[0.01, 4]` and `resolution` to `[0.05, 2]`, so `0` becomes `1`, `0.01` and
     `0.05` respectively. But **a floor of 1 column is far too low to produce a usable
     render**, which is why the frame is black rather than degraded. The clamp prevents the
     crash and runaway allocation; it does not deliver a sensible picture. Do not record this
     case as "renders with safe defaults".
   - Note this is exactly the shape of failure the gate's render assertion catches: a pure
     black frame measures below the std-dev floor of 0.04 and fails, rather than passing as
     "it launched".
   - Run: `MatrixDesktop.exe --windowed --numColumns 99999 --density 999 --resolution 999`
   - Expected: app remains responsive; values are clamped instead of causing runaway allocation. This half of the case is accurate as written.

6. WebGPU request and fallback — **manual; the gate runs no WebGPU case**
   - Run: `MatrixDesktop.exe --windowed --renderer webgpu --version 3d`
   - Expected: renders with WebGPU where supported; otherwise falls back to WebGL/REGL without a crash.

7. Mirror effect without camera — **manual, needs mouse input**
   - Run: `MatrixDesktop.exe --windowed --effect mirror --no-exit-on-any-key`
   - Expected: mirror effect renders; mouse clicks create ripples; repeated clicks do not hang the app.

8. Click ripples without mirror/camera — **manual, needs mouse input**
   - Run: `MatrixDesktop.exe --windowed --effect stripes --renderer webgpu --clickRipples true --clickRippleShape triangle --no-exit-on-any-key`
   - Expected: stripes render and mouse clicks create triangle-shaped ripples without enabling `effect=mirror` or camera.
   - Run: `MatrixDesktop.exe --windowed --effect stripes --renderer regl --clickRipples true --clickRippleShape star --no-exit-on-any-key`
   - Expected: WebGL/REGL stripes render and mouse clicks create star-shaped ripples.
   - Run: `MatrixDesktop.exe --windowed --effect palette --clickRipples true --clickRippleShape box --no-exit-on-any-key`
   - Expected: palette render path also supports non-circular click ripples.
   - Run: `MatrixDesktop.exe --windowed --effect stripes --no-exit-on-any-key`
   - Expected: default behavior is unchanged; mouse clicks do not create rain-effect ripples unless `clickRipples=true`.

9. Mirror effect with camera — **manual, needs webcam hardware**
   - Run: `MatrixDesktop.exe --windowed --effect mirror --camera true --no-exit-on-any-key`
   - Expected: camera permission/runtime succeeds where available, app renders camera-backed mirror effect, closing the app releases camera indicator.
   - See the note under Pass Criteria: on a normal close the camera is released by process
     teardown, not by any code in the app.

10. Window modes — **manual, needs a physical multi-monitor layout**
    - Run: `MatrixDesktop.exe --single-monitor --working-area --no-exit-on-any-key`
    - Expected: app uses primary monitor working area and leaves taskbar visible.
    - If a second monitor exists, run: `MatrixDesktop.exe --monitor 1 --working-area --no-exit-on-any-key`
    - Expected: app opens on monitor index 1 or falls back safely if unavailable.

11. Key-exit controls — **manual; deliberately outside the gate's reach**
    - Run: `MatrixDesktop.exe --windowed --no-exit-on-any-key --exit-on-esc`
    - Expected: ordinary keys do not exit; physical Esc exits.
    - Run: `MatrixDesktop.exe --windowed --no-esc-exit --no-exit-on-any-key`
    - Expected: keyboard does not close the app; close via window chrome.
    - The gate cannot test these: the app installs a global `WH_KEYBOARD_LL` hook, so any
      keystroke typed into the driving terminal would end an automated run. That is why every
      gate case passes `--no-exit-on-any-key`.

12. Locked-down options — **manual, visual**
    - Run: `MatrixDesktop.exe --windowed --hide-cursor --no-devtools --topmost --no-exit-on-any-key`
    - Expected: app opens, cursor is hidden over the app and restored after close; DevTools shortcuts do not open DevTools.

13. WebView2 profile/cache — *gate covers the "no `userdata/` beside the EXE" half*
    - After launch, inspect artifact folder.
    - Expected: no `userdata/` folder is created beside the EXE.
    - Inspect `%LOCALAPPDATA%\MatrixDesktop\WebView2\`.
    - Expected: WebView2 profile/cache files are created there. Deleting that folder resets WebView2 local state.
    - Inspect `%LOCALAPPDATA%\MatrixDesktop\Web\`.
    - Expected: bundled runtime assets are staged there.

14. Argument configurator launch — *gate tier 3 covers this*
    - Run: `MatrixDesktopConfigurator.exe`
    - Expected: configurator opens with grouped argument controls, named preset dropdown, and a generated command at the bottom.

15. Argument configurator presets — **manual UI interaction**
    - On a fresh configurator preset store, run `MatrixDesktopConfigurator.exe`.
    - Expected: `rainbow-haze`, `paradise`, and `stripe effects` appear in the preset dropdown.
    - In `MatrixDesktopConfigurator.exe`, change `version`, `effect`, and at least one color/palette value.
    - Save as a named preset, close the configurator, and reopen it.
    - Expected: last draft is restored and the named preset is available in the dropdown.
    - Rename and delete the preset, then delete one starter preset.
    - Expected: dropdown updates, the app remains responsive, and the deleted starter preset does not return on the next launch.
    - The gate parses the presets file but exercises none of this.

16. Argument configurator test launch — **manual UI interaction**
    - In `MatrixDesktopConfigurator.exe`, select `effect=stripes`, set custom stripe colors, and click `Test Argument`.
    - Expected: MatrixDesktop launches windowed with the generated visual settings and does not exit on ordinary keypresses.
    - Change the preset and click `Test Argument` again.
    - Expected: previous test instance is replaced by the new one.
    - Click `Stop Test`.
    - Expected: test instance closes.

17. Argument configurator import and stripe gating — **manual UI interaction**
    - In `MatrixDesktopConfigurator.exe`, click `Import` and paste:
      `--hide-cursor font=resurrections fps=30 animationSpeed=0.5 forwardSpeed=0.05 numColumns=220 density=2 effect=stripes renderer=webgpu stripeColors=0.5,0,0.5,0,0,1,0,1,0,0,1,0,0,0,1,0.5,0,0.5 raindropLength=0.5 version=3d`
    - Expected: the draft updates to the pasted values, `Stripe colors` shows six editable rows, and the generated command includes `--stripeColors`.
    - Change `Effect` to `palette`.
    - Expected: `Stripe colors` is disabled and the generated command omits `--stripeColors`.

18. Argument configurator randomize — **manual UI interaction**
    - In `MatrixDesktopConfigurator.exe`, record the current Launch settings.
    - Select `Visual preset` and click `Randomize` 10 times.
    - Expected: version/font/renderer/effect/color/motion/layout settings change, Launch settings remain unchanged, and generated effects never select `mirror`, `image`, or `none`.
    - Select `Colors only` and click `Randomize`.
    - Expected: colors/palette/stripe colors change while motion/layout and Launch settings stay unchanged.
    - Select `Motion/Layout` and click `Randomize`.
    - Expected: FPS, columns, density, bloom, and motion values change while Launch settings stay unchanged.
    - Expected: stripe effects generate 2-8 stripe colors, non-stripe effects omit `--stripeColors`, and palettes stay at 3-6 stops.

## Pass Criteria

- All selected cases launch without unhandled exception dialogs.
- `MatrixDesktopConfigurator.exe` is present in the artifact beside `MatrixDesktop.exe`.
- The `web/` folder assets load offline from the artifact folder.
- The `configurator/` folder assets load offline from the artifact folder.
- Closing the app releases the keyboard hook, WebView2, and cursor state — **and the camera,
  but not by design.** `stopCamera()`, which is what actually stops the `MediaStream` tracks,
  is reachable from exactly two places: the renderer `cleanup()` functions, and those are
  called only inside the `if (config.once)` branch (`web/js/regl/main.js:154`,
  `web/js/webgpu/main.js:171`); and three error paths in `web/js/main.js` when a renderer
  fails to load. On a normal close with `once` unset, **no code releases the camera** — the
  OS reclaims the device when the WebView2 process tree exits. The criterion passes, for the
  wrong reason. Anything that keeps the renderer process alive past window close would break
  it silently, and no test here would notice.
- No obvious unbounded memory growth during 5 minutes of windowed animation and repeated
  resize. **This asserts the absence of something the regl path has no mechanism to prevent.**
  There is not one `.destroy()` call anywhere under `web/js/regl/`, and `regl.destroy()` is
  never invoked; only the WebGPU path disposes its textures. The criterion holds solely
  because resize goes through `fbo.resize(w, h)`, which reuses the existing framebuffer
  handle instead of allocating a new one. Add a code path that creates framebuffers or
  textures per resize instead of resizing in place, and this criterion will start failing
  with nothing in the codebase to stop it.

## Notes

- Runtime smoke testing requires Windows. WSL can validate build, publish output, and
  JavaScript module syntax, but not WinForms/WebView2 behavior.
- If a test fails, record Windows version, GPU, monitor count/layout, command line used, and
  whether WebView2/.NET runtimes are installed.
- Historical status: click ripples without mirror/camera passed Windows visual smoke testing,
  including the triangle shape.
