# MatrixDesktop Smoke Test Plan

## Scope

Validate the Windows x64 framework-dependent artifact enough to catch launch, asset loading, renderer fallback, CLI parsing, windowing, and shutdown regressions.

## Test Environment

- Windows 10 version 1809+ or Windows 11, x64.
- .NET 10 Desktop Runtime installed.
- Microsoft Edge WebView2 Runtime installed.
- Optional: multi-monitor setup, webcam, and a GPU/browser runtime with WebGPU enabled.

## Artifact

Use the zip produced at `publish/MatrixDesktop-win-x64-fd-smoke.zip`. Extract it, then run from the extracted `MatrixDesktop-win-x64-fd-smoke` folder.

Latest local artifact smoke status: click ripples without mirror/camera passed Windows visual smoke testing, including the triangle shape.

Primary executable:

```cmd
MatrixDesktop.exe
```

Run commands from inside the artifact folder so relative paths and `web/` assets are present.
WebView2 profile/cache data is stored under `%LOCALAPPDATA%\MatrixDesktop\WebView2\` by default, not beside the EXE.
Bundled web assets are staged under `%LOCALAPPDATA%\MatrixDesktop\Web\` before WebView2 loads them.

## Smoke Cases

1. Default launch
   - Run: `MatrixDesktop.exe`
   - Expected: borderless Matrix rain appears across displays, no missing asset dialog, black startup background, any physical key exits when the app is foreground.

2. Help dialog
   - Run: `MatrixDesktop.exe --help`
   - Expected: help dialog appears and app exits after closing it.

3. Windowed WebGL renderer
   - Run: `MatrixDesktop.exe --windowed --version classic --renderer regl --no-exit-on-any-key`
   - Expected: normal resizable window opens at a usable size, animation renders, resizing does not blank or crash.

4. Presets and query conversion
   - Run: `MatrixDesktop.exe --windowed --version resurrections --effect palette --numColumns 70 --resolution 0.75`
   - Expected: Resurrections glyph/palette variant renders.
   - Run: `MatrixDesktop.exe "?version=3d&effect=plain&fallSpeed=0.4"`
   - Expected: raw query string is accepted and renders.

5. Parameter hardening
   - Run: `MatrixDesktop.exe --windowed --numColumns 0 --density 0 --resolution 0 --palette ""`
   - Expected: app still renders using clamped/default-safe values; no crash or blank startup.
   - Run: `MatrixDesktop.exe --windowed --numColumns 99999 --density 999 --resolution 999`
   - Expected: app remains responsive; values are clamped instead of causing runaway allocation.

6. WebGPU request and fallback
   - Run: `MatrixDesktop.exe --windowed --renderer webgpu --version 3d`
   - Expected: renders with WebGPU where supported; otherwise falls back to WebGL/REGL without a crash.

7. Mirror effect without camera
   - Run: `MatrixDesktop.exe --windowed --effect mirror --no-exit-on-any-key`
   - Expected: mirror effect renders; mouse clicks create ripples; repeated clicks do not hang the app.

8. Click ripples without mirror/camera
   - Run: `MatrixDesktop.exe --windowed --effect stripes --renderer webgpu --clickRipples true --clickRippleShape triangle --no-exit-on-any-key`
   - Expected: stripes render and mouse clicks create triangle-shaped ripples without enabling `effect=mirror` or camera.
   - Run: `MatrixDesktop.exe --windowed --effect stripes --renderer regl --clickRipples true --clickRippleShape star --no-exit-on-any-key`
   - Expected: WebGL/REGL stripes render and mouse clicks create star-shaped ripples.
   - Run: `MatrixDesktop.exe --windowed --effect palette --clickRipples true --clickRippleShape box --no-exit-on-any-key`
   - Expected: palette render path also supports non-circular click ripples.
   - Run: `MatrixDesktop.exe --windowed --effect stripes --no-exit-on-any-key`
   - Expected: default behavior is unchanged; mouse clicks do not create rain-effect ripples unless `clickRipples=true`.

9. Mirror effect with camera
   - Run: `MatrixDesktop.exe --windowed --effect mirror --camera true --no-exit-on-any-key`
   - Expected: camera permission/runtime succeeds where available, app renders camera-backed mirror effect, closing the app releases camera indicator.

10. Window modes
   - Run: `MatrixDesktop.exe --single-monitor --working-area --no-exit-on-any-key`
   - Expected: app uses primary monitor working area and leaves taskbar visible.
   - If a second monitor exists, run: `MatrixDesktop.exe --monitor 1 --working-area --no-exit-on-any-key`
   - Expected: app opens on monitor index 1 or falls back safely if unavailable.

11. Key-exit controls
   - Run: `MatrixDesktop.exe --windowed --no-exit-on-any-key --exit-on-esc`
   - Expected: ordinary keys do not exit; physical Esc exits.
   - Run: `MatrixDesktop.exe --windowed --no-esc-exit --no-exit-on-any-key`
   - Expected: keyboard does not close the app; close via window chrome.

12. Locked-down options
   - Run: `MatrixDesktop.exe --windowed --hide-cursor --no-devtools --topmost --no-exit-on-any-key`
   - Expected: app opens, cursor is hidden over the app and restored after close; DevTools shortcuts do not open DevTools.

13. WebView2 profile/cache
    - After launch, inspect artifact folder.
    - Expected: no `userdata/` folder is created beside the EXE.
    - Inspect `%LOCALAPPDATA%\MatrixDesktop\WebView2\`.
    - Expected: WebView2 profile/cache files are created there. Deleting that folder resets WebView2 local state.
    - Inspect `%LOCALAPPDATA%\MatrixDesktop\Web\`.
    - Expected: bundled runtime assets are staged there.

## Pass Criteria

- All selected cases launch without unhandled exception dialogs.
- The `web/` folder assets load offline from the artifact folder.
- Closing the app releases keyboard hook, WebView2, camera, and cursor state.
- No obvious unbounded memory growth during 5 minutes of windowed animation and repeated resize.

## Notes

- Runtime smoke testing requires Windows. WSL can validate build, publish output, and JavaScript module syntax, but not WinForms/WebView2 behavior.
- If a test fails, record Windows version, GPU, monitor count/layout, command line used, and whether WebView2/.NET runtimes are installed.
