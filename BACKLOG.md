# MatrixDesktop Backlog

## Picking this up cold

State for **v1.2.0**, 2026-10-23. ZMatrix, wallpaper and the post-push source
audit findings are resolved below. Published application checks pass. Marketplace,
OBS/Spout/NDI and per-monitor configurations remain deferred by owner decision.

**Verify before you trust anything.** One command, and it is the whole story:

```pwsh
pwsh -File tests\run-gate.ps1              # includes published Windows application checks
pwsh -File tests\run-gate.ps1 -Tier1Only   # what CI runs, no interactive session needed
```

Exit `0` pass, `1` fail, `2` could not verify, `3` the gate itself broke. **`2` is not a pass.**

**The one convention that matters here: mutation-test every new check.** Break the thing it
guards, confirm exactly one failure naming the right file, restore, and put the mutation result
in the commit message rather than the green run. This is not ceremony. Several checks in this
repo passed vacuously until someone tried to break them, and on three separate occasions the
mutation found a bug in the check *being added that same hour* rather than in the code it
guarded. The most recent: an icon assertion compared a 16x16 bitmap's bytes against a 32x32's
and called them "distinct", which can never be equal and so could never fail. An unmutated
gate is decoration.

**Where the coverage is, and what each layer cannot see:**

| Layer | Catches | Blind to |
| --- | --- | --- |
| `tests\MatrixDesktop.Tests` (console EXE, exit code is the result) | arguments, storage, media grants, preset migration and guide/catalog/config drift | anything that renders |
| `tests\run-gate.ps1` tiers 2 and 3 | the real EXEs launching, rendering, animating, closing cleanly | **layout**: an off-screen footer measures perfectly healthy |
| `tests\web-smoke.py` (19 checks, headless Chromium, runs in CI) | geometry, theme contrast, scene dependencies, scene placements, paired sliders and whether a flag changes the output | GPU-specific behaviour, real WebView2 |
| `tests\renderer-lifecycle.mjs`, `tests\uc3d-tests.mjs` | resource ownership, resize, compatibility, UC3D timing and input behavior | actual GPU drivers |
| `tests\uc3d-browser.py`, `tests\uc3d-calibration.py`, `tests\native-uc3d-smoke.py` | actual backend readiness, original slider/stream semantics, picture/depth behavior and published native media delivery | physical Looking Glass hardware |

That middle blind spot is not hypothetical. v1.0.2 shipped with the configurator's entire
command panel below the bottom of the window and every pixel statistic looked fine.

A fourth thing worth knowing: the window icon is read from the `.exe`'s own Win32 resources,
not from a managed resource. `<ApplicationIcon>` cannot be removed, because `CreateAppHost`
builds the apphost's icon by copying the Win32 resources out of the `.dll`.

**Three traps that have each bitten more than once.** Details in the sections below and in
`VENDORING.md`: `bloomPass`'s transposed width/height uniforms are correct and must not be
"fixed"; the argument guide's header version has gone stale twice and now has a gate check;
and a Python patch script that reads with universal newlines or writes `utf-8-sig` will flip
line endings or add a BOM across a whole file, so check `git diff` first-lines after one.

**Releasing.** Push a `v*` tag and `release.yml` does the rest, stamping the version from the
tag. Bump `<Version>` in both `.csproj` and the guide's header line *in the same commit as the
tag*, because `release.yml` overriding from the tag is exactly what let the repo sit at 1.0.2
through two shipped releases without anyone noticing. The gate holds the guide and the two
csproj to each other; it cannot know what tag you are about to push.

## Open items

The single place to look for "what is left". The per-pass sections further down are a record
of when each item was found and why it was or was not acted on; they are not a to-do list, and
two of them used to share the heading "Worth doing", which is how a v1.0.5 summary came to
claim the backlog was empty when three items were still open. Anything struck through below
has shipped.

77 items are open from the post-push audit of `06809a4` on 2026-11-07. Each has its planned
disposition and file and line evidence in "Post-push audit, 2026-11-07" below. A row leaves this
table only when its disposition there is FIXED, CLOSED-VERIFIED, DECLINED-MEASURED or
ACCEPTED-RECORDED with checkable evidence.

| ID | Severity | Finding |
| --- | --- | --- |
| AUD-01 | high | A second configurator instance silently overwrites presets saved by the first (whole-state last-writer-wins, no instance guard) |
| AUD-02 | high | Rename overwrites the preset's saved values with the unsaved working draft |
| AUD-03 | high | A greyed-out Camera toggle still opens the webcam: camera=true is emitted for every effect and both renderers honour it |
| AUD-04 | medium | Non-wallpaper surface failure calls ExitThread() inside the constructor, so Application.Run never returns and the process hangs invisibly |
| AUD-05 | medium | Wallpaper mode retries a WebView2 startup failure every 500 ms forever, re-showing the modal error dialog each time it is dismissed |
| AUD-06 | medium | Pausing the wallpaper (tray Pause, battery, fullscreen on another monitor) hides the WebView, so the desktop shows black or the static wallpaper instead of the frozen frame |
| AUD-07 | medium | No CoreWebView2.ProcessFailed handler: a renderer or browser process failure leaves a long-running wallpaper blank until Explorer restarts or the display changes |
| AUD-08 | medium | UC3D desktop snapshot in wallpaper (and preview) mode copies the live screen, baking other applications' windows into the wallpaper; Refresh desktop cannot replace it |
| AUD-09 | medium | Every WM_SETTINGCHANGE and WM_THEMECHANGED regenerates all native resources on the UI thread, unfiltered and uncoalesced, and wipes the persistent ZMatrix raster; for Matrix and UC3D the refresh does nothing |
| AUD-10 | medium | Positional picture IDs are re-keyed by every host refresh while UC3D keeps its startup caption list, so pictures show their neighbour's caption (or 404) |
| AUD-11 | medium | Every settled ZMatrix configurator edit, even colour-only keys, blocks the configurator UI thread 0.6-1 s re-capturing the whole-desktop wallpaper and rebuilding both glyph atlases |
| AUD-12 | medium | Copied-command quoting claims values become literal in cmd and PowerShell, but %, !, $ and backtick still expand in text values |
| AUD-13 | medium | Catalog defaults for cursorColor and stripeColors are not what the renderer draws when they are omitted, so the editors show colours that never render and those exact colours cannot be set; the screensaver profile then renders differently again |
| AUD-14 | medium | Colour popover outlives renderFields and writes through stale closures: palette stops come back after removal or overwrite a neighbour, and the colour saved differs from the editor |
| AUD-15 | medium | Every gating change and list edit drops keyboard focus to body; the next Tab restarts at the first field |
| AUD-16 | medium | 54 field controls have no accessible name and their visible label does not operate them |
| AUD-17 | medium | 'Last draft' cannot bring back the working draft once any preset was selected; unsaved edits are discarded on preset switch |
| AUD-18 | medium | 'Plug me in' unhides the canvas only after the renderer finished loading (REGL sizes to 1x1) and has no re-entry guard (a second click starts a second renderer and a bogus error) |
| AUD-19 | medium | After a GPU context or device loss no scene recovers: the default Matrix scene renders black while diagnostics report healthy, and UC3D/ZMatrix tear themselves down permanently with no visible message |
| AUD-20 | medium | WebGPU-to-REGL fallback cannot work once webgpu/main.js has taken the canvas's webgpu context, and it reports a misleading regl null dereference |
| AUD-21 | medium | effect=image with the configurator's blank Image URL fetches a remote Wikimedia photo and fails the whole renderer when offline |
| AUD-22 | medium | The software-rendering notice can never be dismissed in wallpaper, screensaver or preview mode, so on SwiftShader those modes show a static box instead of rain |
| AUD-23 | medium | UC3D sizes its WebGPU canvas and render target with no device-limit clamp, so a wide virtual screen fails on the first frame and the desktop goes black silently |
| AUD-24 | medium | Legacy UC3D with isometric and mouse rotation snaps 45 degrees yaw and 22.5 degrees pitch on the first drag, because runtime.update mutates the scene's lastState |
| AUD-25 | medium | Python exit 2 is overloaded: argparse usage errors become UNCHECKED 'unavailable environment', which CI then tolerates as green |
| AUD-26 | medium | native-uc3d-smoke creates a fresh WebView2 profile per launch and nothing prunes them: 132 folders, 1.84 GB and growing |
| AUD-27 | medium | Tier-2 'wrote no crash dump' compares a folder-wide count that cannot rise once the 10-dump retention cap is reached, and is not attributed to the smoke pid |
| AUD-28 | medium | release.yml stamps the version from the tag and runs no gate, so the version drift BACKLOG describes can still ship and a red commit can be released |
| AUD-29 | medium | Under Windows PowerShell 5.1 (the documented full-gate shell) redirected native stderr is a terminating error, so several gate probes crash the run instead of reporting FAIL or UNCHECKED |
| AUD-30 | medium | Guide promises a tray/log report when process priority is rejected; the tray cannot report it and Realtime is downgraded to High with no message |
| AUD-31 | low | MainForm's own SessionSwitch/PowerModeChanged suspend path is unreachable: the public constructor that enables it has no caller |
| AUD-32 | low | DesktopPointerBridge runs the desktop-foreground probe for every system-wide mouse event before its 30 ms move throttle |
| AUD-33 | low | Constructor-time ZMatrix preparation is always thrown away, doubling the wallpaper capture and atlas builds per surface |
| AUD-34 | low | Documented --restore-screensaver refusal is reported as a crash with a minidump |
| AUD-35 | low | Log rotation never runs in short-lived processes, so screensaver-only use grows the shared log past the documented 2 MB cap |
| AUD-36 | low | Failed dump attempts are unbounded, and each one prunes a retained dump and leaves an empty .dmp behind |
| AUD-37 | low | The close flush cancels WM_QUERYENDSESSION, so an open configurator is listed as blocking shutdown, restart and sign-out, and exits even when the shutdown is cancelled |
| AUD-38 | low | A debounced draft save that comes due during a confirm/prompt lands after Delete or Save As and persists a stale or deleted selectedPresetId |
| AUD-39 | low | Host replies carry data the page throws away while its text says otherwise: 'Retained ... for review' for command imports, the PowerShell script on clipboard failure, and the test command description |
| AUD-40 | low | Phrase editor shows and counts duplicate rows that the saved draft silently drops |
| AUD-41 | low | app.js re-implements the colour helpers colorPicker.js exports, the copies disagree, and the palette row persists null channels |
| AUD-42 | low | Keyboard focus is not managed for the colour popover, the help modal or the section jump list |
| AUD-43 | low | Stop Test stays enabled after the test window exits on its own, and the comment calling it the single source of truth is wrong |
| AUD-44 | low | Every renderFields rebuilds two datalists holding every installed font |
| AUD-45 | low | Dead code and stale markup in the configurator page, and the preview-unavailable reason is never shown |
| AUD-46 | low | Filtering removes the screensaver buttons from the page, and searching 'screensaver' cannot find them |
| AUD-47 | low | fps=0 gives a black screen in the default Matrix scene, while the guide says 'tends to look frozen' and UC3D/ZMatrix draw one frame |
| AUD-48 | low | canvasSize.js's DPR media-query listener is inert: it only re-arms itself, and DPR changes already work through get() |
| AUD-49 | low | Page-side handlers with no host sender (matrix-host-state web message, chrome.webview resource listener, pointer 'leave'), and the native-bootstrap tests drive production assertions through the dead route |
| AUD-50 | low | Docs promise 'Enable sound' when autoplay is blocked, but the synthesized-audio path awaits a resume() that stays pending, so the button never appears |
| AUD-51 | low | UC3D's picture-failure warning is pushed again on every slideshow cycle with no dedupe or cap, so the warnings array grows without bound |
| AUD-52 | low | Both raw WebGL passes call gl.getError() every frame, a synchronous GPU-process round trip in Chromium |
| AUD-53 | low | Dead and duplicated code in the UC3D/ZMatrix modules, including a ZMatrix invalid-setting warning that config.js makes unreachable while the host builds atlases from the raw argv |
| AUD-54 | low | The gate's private WEBVIEW2_USER_DATA_FOLDER blinds the tier-2 'leaves no userdata beside the exe' check, and no tier checks where the product's profile actually lands |
| AUD-55 | low | Page-error collectors are asserted before teardown and before most interactions, so later errors are collected and never checked |
| AUD-56 | low | web-smoke.py request-log suppression is inert: it sets log_message on a functools.partial, not on the handler class |
| AUD-57 | low | Screensaver registration tests leave an empty TestFixtures key under HKCU Software/MatrixDesktop on every run |
| AUD-58 | low | Argument guide's boolean section is stale: the wrapper rewrites unknown values to false and the web parser accepts six spellings, not exactly 'true' |
| AUD-59 | low | README says a missing WebGPU adapter reports unverified coverage, but the calibration command it documents exits 1 instead |
| AUD-60 | low | BACKLOG 'Picking this up cold' points to trap details that do not exist and its coverage map omits every v1.2.0 test layer |
| AUD-61 | low | Docs name a 'Restore previous screensaver' control; the configurator button is labelled 'Restore screensaver' |
| AUD-63 | info | InvariantGlobalization, presented as part of the portable optimization, reaches only the local pubxml publish, not the release zip or the gate-verified payload |
| AUD-64 | medium | The renderer clock never rebases, so float32 time degrades a long-running wallpaper: rain motion drops to 32 Hz after about 3 days of active time and 16 Hz after about 6, high speeds lose trail levels within hours, and the WebGPU palette/stripe dither turns into screen-wide blocks within hours |
| AUD-65 | low | The regl symbol pass compares a float32 frame counter, so after 2^24 active frames (78 h at 60 Hz, 32 h at 144 Hz) Operator and Palimpsest glyphs cycle 3x too fast, then 5x and 8x |
| AUD-66 | low | UC3D legacy controls upload the unbounded active time as a float32 stream position, so after a few days of active time the streams step in jumps of up to 2% of screen height at 16 Hz |
| AUD-67 | medium | Unchecking Skip intro with a zero or negative Fall speed or Animation speed renders nothing forever (or a white-out), while diagnostics report a healthy renderer |
| AUD-68 | medium | UC3D with Original controls and Animation speed 0 (the configurator's minimum for it) renders nothing, animated or single-frame, while Matrix and legacy UC3D show a frozen frame |
| AUD-69 | high | The Matrix scene on the WebGPU renderer goes solid black (or freezes on its last frame) once the canvas passes 8192 px, while diagnostics report ready, no errors and advancing frames |
| AUD-70 | medium | UC3D on REGL (the default renderer) allocates its offscreen target at the unclamped canvas size and tears itself down silently above MAX_TEXTURE_SIZE, while the Matrix scene survives the browser's drawing-buffer clamp |
| AUD-71 | medium | UC3D on WebGPU uploads the desktop snapshot at full capture size, so desktopBackground=true on a virtual screen wider than 8192 px fails at any resolution; a canvas-only clamp for AUD-23 would not fix it |
| AUD-72 | medium | No single-instance guard for wallpaper mode: a second --wallpaper launch stacks another full renderer and tray icon on the same WorkerW, attached underneath the first, so its new settings never show and each tray's Pause, Exit and Start-with-Windows act on one process only |
| AUD-73 | low | Concurrent startups collide on the fixed-name .write_test probe and silently move that instance's WebView2 profile and web root to Roaming AppData (54-58% probe failure measured while two processes overlap) |
| AUD-74 | low | Every MatrixDesktop instance joins the first instance's WebView2 browser process, whose priority class is set by whichever host started first, and Stop Test kills the Test host's whole process tree, which can take that shared browser down with it |
| AUD-75 | medium | Text values containing '=' are parsed as new keys: an active UC3D musicFile, pictureDirectory or donorDirectory such as C:/Music/a=b.mp3, or an image URL with a query, is lost on Test, Export and copied-command import |
| AUD-76 | medium | Crash handlers can enter single-threaded DbgHelp concurrently: the UI ThreadException and UnobservedTaskException paths each reach MiniDumpWriteDump, and the reservation counter is a cap, not a lock |
| AUD-77 | low | An inherited Object property name in the query (constructor, toString, __proto__) passes config.js's parameter filter, has no parser, and throws before main.js's visible error boundary, so the renderer never starts and no notice explains why |

Before that audit, at `06809a4` on 2026-11-07, the Windows PowerShell 5.1 full gate reported
70 PASS, 0 FAIL, 0 UNCHECKED, with its actual Windows process exit observed as 0, and
the PowerShell 7 Tier 1 gate reported 37 PASS, 0 FAIL, 0 UNCHECKED.
These include 140 core cases and 22 native cases inside aggregate checks.
Sixteen subsequent native and regression phases each exit 0, covering both
renderers and repeating regressions after wallpaper, configurator and UC3D smoke.
The screensaver preview and fullscreen input-exit probes pass when launched as the
signed-in owner in the connected RDP session. The agent execution account could not
deliver input there; that context is correctly reported as UNCHECKED rather than passed.
Explorer recovery and native configurator refresh pass their application checks.
Every prior UC3D calibration item is retained with its disposition below.

### Post-push audit, 2026-11-07

The full audit ran after `06809a4` reached main. Eleven area finders (host app, shared native
code, configurator C#, configurator page, web core, UC3D and ZMatrix, tests and gate, docs
claims, cross-boundary contracts, BACKLOG regressions, and a dynamic leak soak of the published
app) reported 76 findings. Adversarial verifiers confirmed 74 and refuted 2, and synthesis
merged the 74 into 63 items. A completeness critic named four areas nobody had covered:
renderer clock precision over long uptime, settings combinations the configurator can reach,
WebGPU device limits, and concurrent host instances. The gap round confirmed 12 of 13 findings
there. Codex audited the same commit independently and reported 7 findings, of which 4 were
already present and 3 are new. Static leads came from ESLint, Ruff, Vulture and the .NET
analyzers at AnalysisMode=All; they were leads for the finders, not findings. 77 items in all.

| ID | Finding | Disposition | Implementation and checkable evidence |
| --- | --- | --- | --- |
| AUD-01 | A second configurator instance silently overwrites presets saved by the first (whole-state last-writer-wins, no instance guard) | OPEN, planned FIXED | Found at Program.cs:10-29, ConfiguratorForm.cs:64, MatrixApplicationContext.cs:64-68, ScreensaverProtocol.cs:12-27, StorageService.cs:200-203. |
| AUD-02 | Rename overwrites the preset's saved values with the unsaved working draft | OPEN, planned FIXED | Found at app.js:1143-1152, app.js:1154-1157, ConfiguratorForm.cs:312-322, app.js:188-206, app.js:1405-1407, StorageService.cs:102-142. |
| AUD-03 | A greyed-out Camera toggle still opens the webcam: camera=true is emitted for every effect and both renderers honour it | OPEN, planned FIXED | Found at app.js:527-529, app.js:558, app.js:229, ConfiguratorForm.cs:606, ScreensaverProtocol.cs:39-48, config.js:553. |
| AUD-04 | Non-wallpaper surface failure calls ExitThread() inside the constructor, so Application.Run never returns and the process hangs invisibly | OPEN, planned FIXED | Found at Program.cs:53-58, MatrixApplicationContext.cs:51, ScreensaverProtocol.cs:45-48. |
| AUD-05 | Wallpaper mode retries a WebView2 startup failure every 500 ms forever, re-showing the modal error dialog each time it is dismissed | OPEN, planned FIXED | Found at MainForm.cs:749-756, MatrixApplicationContext.cs:120-126, MainForm.cs:99, AutostartService.cs:9. |
| AUD-06 | Pausing the wallpaper (tray Pause, battery, fullscreen on another monitor) hides the WebView, so the desktop shows black or the static wallpaper instead of the frozen frame | OPEN, planned FIXED | Found at MainForm.cs:114-125, hostPlayback.js:8-13, MatrixApplicationContext.cs:184, wallpaper-smoke.py:69. |
| AUD-07 | No CoreWebView2.ProcessFailed handler: a renderer or browser process failure leaves a long-running wallpaper blank until Explorer restarts or the display changes | OPEN, planned FIXED | Found at MainForm.cs:823, MatrixApplicationContext.cs:157-161. |
| AUD-08 | UC3D desktop snapshot in wallpaper (and preview) mode copies the live screen, baking other applications' windows into the wallpaper; Refresh desktop cannot replace it | OPEN, planned FIXED | Found at NativeMediaBridge.cs:100-111, MainForm.cs:25-26, MatrixApplicationContext.cs:129-141, WallpaperBackground.cs:7, README.md:33, BACKLOG.md:107. |
| AUD-09 | Every WM_SETTINGCHANGE and WM_THEMECHANGED regenerates all native resources on the UI thread, unfiltered and uncoalesced, and wipes the persistent ZMatrix raster; for Matrix and UC3D the refresh does nothing | OPEN, planned FIXED | Found at MatrixApplicationContext.cs:222, NativeMediaBridge.cs:195, WallpaperBackground.cs:17-30, NativeMediaBridge.cs:138-141, zmatrix/runtime.js:77, surface.js:46-61. |
| AUD-10 | Positional picture IDs are re-keyed by every host refresh while UC3D keeps its startup caption list, so pictures show their neighbour's caption (or 404) | OPEN, planned FIXED | Found at MediaCatalog.cs:115, NativeMediaBridge.cs:57, uc3d/runtime.js:30-31, zmatrix/runtime.js:138, media.js:13-39, media.js:31. |
| AUD-11 | Every settled ZMatrix configurator edit, even colour-only keys, blocks the configurator UI thread 0.6-1 s re-capturing the whole-desktop wallpaper and rebuilding both glyph atlases | OPEN, planned FIXED | Found at app.js:252-258, ConfiguratorForm.cs:239-244, NativeMediaBridge.cs:177-187, CommandBuilder.cs:462-479, runtime.js:36, BACKLOG.md:123. |
| AUD-12 | Copied-command quoting claims values become literal in cmd and PowerShell, but %, !, $ and backtick still expand in text values | OPEN, planned FIXED | Found at CommandBuilder.cs:420-423, BACKLOG.md:112, README.md:362-363, regl/utils.js:52-57. |
| AUD-13 | Catalog defaults for cursorColor and stripeColors are not what the renderer draws when they are omitted, so the editors show colours that never render and those exact colours cannot be set; the screensaver profile then renders differently again | OPEN, planned FIXED | Found at ArgumentCatalog.cs:167, config.js:652-655, regl/stripePass.js:39, webgpu/stripePass.js:45, PresetSeeder.cs:32, ConfiguratorForm.cs:546. |
| AUD-14 | Colour popover outlives renderFields and writes through stale closures: palette stops come back after removal or overwrite a neighbour, and the colour saved differs from the editor | OPEN, planned FIXED | Found at app.js:1, colorPicker.js:235-245, app.js:397-400, app.js:974-977, app.js:899-908, app.js:229. |
| AUD-15 | Every gating change and list edit drops keyboard focus to body; the next Tab restarts at the first field | OPEN, planned FIXED | Found at app.js:400, app.js:165. |
| AUD-16 | 54 field controls have no accessible name and their visible label does not operate them | OPEN, planned FIXED | Found at app.js:541-543, app.js:554-568, index.html:54, app.js:969-971, BACKLOG.md:88. |
| AUD-17 | 'Last draft' cannot bring back the working draft once any preset was selected; unsaved edits are discarded on preset switch | OPEN, planned FIXED | Found at app.js:1255-1264, app.js:197, ConfiguratorForm.cs:429-434, app.js:302, app.js:1175, StorageService.cs:123-130. |
| AUD-18 | 'Plug me in' unhides the canvas only after the renderer finished loading (REGL sizes to 1x1) and has no re-entry guard (a second click starts a second renderer and a bogus error) | OPEN, planned FIXED | Found at main.js:98, main.js:105, regl/main.js:100, index.html:16-29, run-gate.ps1:1054-1059, web-smoke.py:653. |
| AUD-19 | After a GPU context or device loss no scene recovers: the default Matrix scene renders black while diagnostics report healthy, and UC3D/ZMatrix tear themselves down permanently with no visible message | OPEN, planned FIXED | Found at regl/main.js:130, utils.js:65, palettePass.js:62, rainPass.js:159, regl/main.js:189-191, regl/uc3dPass.js:45. |
| AUD-20 | WebGPU-to-REGL fallback cannot work once webgpu/main.js has taken the canvas's webgpu context, and it reports a misleading regl null dereference | OPEN, planned FIXED | Found at main.js:5, main.js:42, webgpu/main.js:123, palettePass.js:92, rainPass.js:141-157, endPass.js:29. |
| AUD-21 | effect=image with the configurator's blank Image URL fetches a remote Wikimedia photo and fails the whole renderer when offline | OPEN, planned FIXED | Found at regl/imagePass.js:6, webgpu/imagePass.js:7, ArgumentCatalog.cs:139, MainForm.cs:826-842, README.md:8. |
| AUD-22 | The software-rendering notice can never be dismissed in wallpaper, screensaver or preview mode, so on SwiftShader those modes show a static box instead of rain | OPEN, planned FIXED | Found at main.js:90-111, DesktopWindowHost.cs:75, hostPointer.js:23-24, MatrixApplicationContext.cs:146-149, ScreensaverWindowPolicy.cs:6, MainForm.cs:93. |
| AUD-23 | UC3D sizes its WebGPU canvas and render target with no device-limit clamp, so a wide virtual screen fails on the first frame and the desktop goes black silently | OPEN, planned FIXED | Found at uc3d/runtime.js:144-146, webgpu/uc3dPass.js:85-86, AppCli.cs:27, MainForm.cs:576-580, DesktopWindowHost.cs:82-83, ScreensaverWindowPolicy.cs:6. |
| AUD-24 | Legacy UC3D with isometric and mouse rotation snaps 45 degrees yaw and 22.5 degrees pitch on the first drag, because runtime.update mutates the scene's lastState | OPEN, planned FIXED | Found at scene.js:97-106, runtime.js:86, scene.js:108-111, config.js:645-650, app.js:496, uc3d-tests.mjs:260. |
| AUD-25 | Python exit 2 is overloaded: argparse usage errors become UNCHECKED 'unavailable environment', which CI then tolerates as green | OPEN, planned FIXED | Found at run-gate.ps1:715-718, ci.yml:95-108. |
| AUD-26 | native-uc3d-smoke creates a fresh WebView2 profile per launch and nothing prunes them: 132 folders, 1.84 GB and growing | OPEN, planned FIXED | Found at native-uc3d-smoke.py:76, MainForm.cs:1044-1077. |
| AUD-27 | Tier-2 'wrote no crash dump' compares a folder-wide count that cannot rise once the 10-dump retention cap is reached, and is not attributed to the smoke pid | OPEN, planned FIXED | Found at run-gate.ps1:397-400. |
| AUD-28 | release.yml stamps the version from the tag and runs no gate, so the version drift BACKLOG describes can still ship and a red commit can be released | OPEN, planned FIXED | Found at release.yml:43-52, run-gate.ps1:882-921, README.md:10. |
| AUD-29 | Under Windows PowerShell 5.1 (the documented full-gate shell) redirected native stderr is a terminating error, so several gate probes crash the run instead of reporting FAIL or UNCHECKED | OPEN, planned FIXED | Found at run-gate.ps1:67, SMOKE_TEST_PLAN.md:31-35. |
| AUD-30 | Guide promises a tray/log report when process priority is rejected; the tray cannot report it and Realtime is downgraded to High with no message | OPEN, planned FIXED | Found at MatrixApplicationContext.cs:37, ArgumentCatalog.cs:77-80, AppCli.cs:126, ZMatrixCfgImporter.cs:94. |
| AUD-31 | MainForm's own SessionSwitch/PowerModeChanged suspend path is unreachable: the public constructor that enables it has no caller | OPEN, planned FIXED | Found at MainForm.cs:50, MatrixApplicationContext.cs:92, Program.cs:53-58, MatrixApplicationContext.cs:38-39, BACKLOG.md:250. |
| AUD-32 | DesktopPointerBridge runs the desktop-foreground probe for every system-wide mouse event before its 30 ms move throttle | OPEN, planned FIXED | Found at DesktopPointerBridge.cs:32, DesktopWindowHost.cs:48-49, MainForm.cs:130-140. |
| AUD-33 | Constructor-time ZMatrix preparation is always thrown away, doubling the wallpaper capture and atlas builds per surface | OPEN, planned FIXED | Found at MainForm.cs:95, NativeMediaBridge.cs:204, MainForm.cs:1098-1105. |
| AUD-34 | Documented --restore-screensaver refusal is reported as a crash with a minidump | OPEN, planned FIXED | Found at ScreensaverRegistration.cs:84-85, MatrixDesktop_Argument_Guide.txt:1014-1016, ScreensaverProtocol.cs:34-37, Program.cs:53-57, CrashDumpWriter.cs:125-170, ConfiguratorForm.cs:304-307. |
| AUD-35 | Log rotation never runs in short-lived processes, so screensaver-only use grows the shared log past the documented 2 MB cap | OPEN, planned FIXED | Found at Logger.cs:18, CrashDumpWriter.cs:117, MatrixApplicationContext.cs:107, CrashDumpWriter.cs:192, MainForm.cs:1053. |
| AUD-36 | Failed dump attempts are unbounded, and each one prunes a retained dump and leaves an empty .dmp behind | OPEN, planned FIXED | Found at CrashDumpWriter.cs:195-202. |
| AUD-37 | The close flush cancels WM_QUERYENDSESSION, so an open configurator is listed as blocking shutdown, restart and sign-out, and exits even when the shutdown is cancelled | OPEN, planned FIXED | Found at OnFormClosing sets e.Cancel = true at :124 for every CloseReason before its first await (:129) and closes again from the continuation (:134). WinForms raises FormClosing for WM_QUERYENDSESSION with CloseReason.WindowsShutDown and returns !e.Cancel (Form.WmClose), so the configurator answers FALSE an. |
| AUD-38 | A debounced draft save that comes due during a confirm/prompt lands after Delete or Save As and persists a stale or deleted selectedPresetId | OPEN, planned FIXED | Found at app.js:188-206, ConfiguratorForm.cs:429-434, app.js:821-831. |
| AUD-39 | Host replies carry data the page throws away while its text says otherwise: 'Retained ... for review' for command imports, the PowerShell script on clipboard failure, and the test command description | OPEN, planned FIXED | Found at app.js:1212-1215, ArgumentImporter.cs:53-112, ConfiguratorForm.cs:514-516, app.js:429-451, ConfiguratorForm.cs:357-360, app.js:1342-1347. |
| AUD-40 | Phrase editor shows and counts duplicate rows that the saved draft silently drops | OPEN, planned FIXED | Found at app.js:758, zmatrix/settings.js:78, ZMatrixControlProfile.cs:115. |
| AUD-41 | app.js re-implements the colour helpers colorPicker.js exports, the copies disagree, and the palette row persists null channels | OPEN, planned FIXED | Found at app.js:97-126, colorPicker.js:9-62, app.js:1, app.js:112, app.js:990-994, StorageService.cs:165-170. |
| AUD-42 | Keyboard focus is not managed for the colour popover, the help modal or the section jump list | OPEN, planned FIXED | Found at colorPicker.js:226-246, index.html:84, app.js:1377-1395, app.js:320-325, BACKLOG.md:88. |
| AUD-43 | Stop Test stays enabled after the test window exits on its own, and the comment calling it the single source of truth is wrong | OPEN, planned FIXED | Found at app.js:1320, ConfiguratorForm.cs:610-631, app.js:77-86, CommandBuilder.cs:82-85, MainForm.cs:503-508, AppCli.cs:31. |
| AUD-44 | Every renderFields rebuilds two datalists holding every installed font | OPEN, planned FIXED | Found at app.js:810-812. |
| AUD-45 | Dead code and stale markup in the configurator page, and the preview-unavailable reason is never shown | OPEN, planned FIXED | Found at app.js:149, colorPicker.js:229, app.js:1368-1371, index.html:27, ConfiguratorForm.cs:271, BACKLOG.md:250. |
| AUD-46 | Filtering removes the screensaver buttons from the page, and searching 'screensaver' cannot find them | OPEN, planned FIXED | Found at app.js:425-428, ArgumentCatalog.cs:58-84. |
| AUD-47 | fps=0 gives a black screen in the default Matrix scene, while the guide says 'tends to look frozen' and UC3D/ZMatrix draw one frame | OPEN, planned FIXED | Found at config.js:619, ArgumentCatalog.cs:209, CommandBuilder.cs:172, regl/main.js:148, webgpu/main.js:178, utils.js:1-11. |
| AUD-48 | canvasSize.js's DPR media-query listener is inert: it only re-arms itself, and DPR changes already work through get() | OPEN, planned FIXED | Found at canvasSize.js:12-17, webgpu/main.js:206, VENDORING.md:53, renderer-lifecycle.mjs:75. |
| AUD-49 | Page-side handlers with no host sender (matrix-host-state web message, chrome.webview resource listener, pointer 'leave'), and the native-bootstrap tests drive production assertions through the dead route | OPEN, planned FIXED | Found at MainForm.cs:135, hostPlayback.js:15, MainForm.cs:121, hostPlayback.js:24-26, NativeMediaBridge.cs:233, hostPointer.js:13. |
| AUD-50 | Docs promise 'Enable sound' when autoplay is blocked, but the synthesized-audio path awaits a resume() that stays pending, so the button never appears | OPEN, planned FIXED | Found at audio.js:55-57, docs/UC3D.md:74-75, uc3d-audio-tests.mjs:35-41, BACKLOG.md:128. |
| AUD-51 | UC3D's picture-failure warning is pushed again on every slideshow cycle with no dedupe or cap, so the warnings array grows without bound | OPEN, planned FIXED | Found at runtime.js:55-59, media.js:12, overlays.js:23, runtime.js:32, MediaCatalog.cs:17-21, NativeMediaBridge.cs:262-277. |
| AUD-52 | Both raw WebGL passes call gl.getError() every frame, a synchronous GPU-process round trip in Chromium | OPEN, planned FIXED | Found at regl/uc3dPass.js:135, regl/zmatrixPass.js:44, native.js:108, runtime.js:95. |
| AUD-53 | Dead and duplicated code in the UC3D/ZMatrix modules, including a ZMatrix invalid-setting warning that config.js makes unreachable while the host builds atlases from the raw argv | OPEN, planned FIXED | Found at runtime.js:110, media.js:43-44, native.js:118, audio.js:4, zmatrix/scene.js:19, zmatrix/gpu.js:1-21. |
| AUD-54 | The gate's private WEBVIEW2_USER_DATA_FOLDER blinds the tier-2 'leaves no userdata beside the exe' check, and no tier checks where the product's profile actually lands | OPEN, planned FIXED | Found at run-gate.ps1:113, MainForm.cs:97, MainForm.cs:1242-1249. |
| AUD-55 | Page-error collectors are asserted before teardown and before most interactions, so later errors are collected and never checked | OPEN, planned FIXED | Found at app.js:229. |
| AUD-56 | web-smoke.py request-log suppression is inert: it sets log_message on a functools.partial, not on the handler class | OPEN, planned FIXED | Found at web-smoke.py:60-61, run-gate.ps1:656. |
| AUD-57 | Screensaver registration tests leave an empty TestFixtures key under HKCU Software/MatrixDesktop on every run | OPEN, planned FIXED | Found at ScreensaverRegistrationTests.cs:18, Program.cs:87, run-gate.ps1:440-476, ScreensaverRegistration.cs:14, SMOKE_TEST_PLAN.md:111. |
| AUD-58 | Argument guide's boolean section is stale: the wrapper rewrites unknown values to false and the web parser accepts six spellings, not exactly 'true' | OPEN, planned FIXED | Found at MatrixArgs.cs:174-177, FlagNormalization.cs:22-27, config.js:446, BACKLOG.md:206, ParityTests.cs:162-166, renderer-lifecycle.mjs:85-88. |
| AUD-59 | README says a missing WebGPU adapter reports unverified coverage, but the calibration command it documents exits 1 instead | OPEN, planned FIXED | Found at README.md:605-617, main.js:81, uc3d-browser.py:82-88. |
| AUD-60 | BACKLOG 'Picking this up cold' points to trap details that do not exist and its coverage map omits every v1.2.0 test layer | OPEN, planned FIXED | Found at BACKLOG.md:44-48, bloomPass.js:61-85, run-gate.ps1:481-494, SMOKE_TEST_PLAN.md:71-125. |
| AUD-61 | Docs name a 'Restore previous screensaver' control; the configurator button is labelled 'Restore screensaver' | OPEN, planned FIXED | Found at README.md:41, MatrixDesktop_Argument_Guide.txt:1014, docs/ZMATRIX.md:116, index.html:68, app.js:51, BACKLOG.md:87. |
| AUD-62 | BACKLOG still lists two measured non-defects as 'Upstream, reportable to Rezmason/matrix', plus a fixed per-frame layout read under 'Watching, not acting' | FIXED | The heading now reads "Suspected upstream defects, measured and withdrawn" and carries the probe's numbers (`tests/bloom-probe.py:111,115` asserts zero changed bytes; the 0/8192 and 0/3404 counts are the v1.2.0 row) and the placeholder's current ownership (`web/js/regl/bloomPass.js:20-21`, `:26-30`). The six v1.0.2 bullets fixed in v1.2.0 and the "Watching, not acting" layout read are struck through with a pointer to their FIXED rows; no row was deleted. Documentation only; the optional structural BACKLOG test was not added (decision table). |
| AUD-63 | InvariantGlobalization, presented as part of the portable optimization, reaches only the local pubxml publish, not the release zip or the gate-verified payload | OPEN, planned FIXED | Found at run-gate.ps1:934-947, README.md:237-243, FlagNormalization.cs:34, ZMatrixCfgImporter.cs:49. |
| AUD-64 | The renderer clock never rebases, so float32 time degrades a long-running wallpaper: rain motion drops to 32 Hz after about 3 days of active time and 16 Hz after about 6, high speeds lose trail levels within hours, and the WebGPU palette/stripe dither turns into screen-wide blocks within hours | OPEN, planned FIXED | Found at hostPlayback.js:45-55, regl/main.js:139, webgpu/main.js:136,220, rainPass.raindrop.frag.glsl:21,53-58, rainPass.wgsl:55,165-176, palettePass.wgsl:62,67. |
| AUD-65 | The regl symbol pass compares a float32 frame counter, so after 2^24 active frames (78 h at 60 Hz, 32 h at 144 Hz) Operator and Palimpsest glyphs cycle 3x too fast, then 5x and 8x | OPEN, planned FIXED | Found at rainPass.symbol.frag.glsl:14,44, regl/main.js:138-139,154,183-189, regl/utils.js:121-125, rainPass.js:111-120,324, config.js:207,373. |
| AUD-66 | UC3D legacy controls upload the unbounded active time as a float32 stream position, so after a few days of active time the streams step in jumps of up to 2% of screen height at 16 Hz | OPEN, planned FIXED | Found at runtime.js:142,152,100, scene.js:70,98, uc3d.vert.glsl:61-63,88, uc3d.wgsl:61-64,88, hostPlayback.js:45-55, uc3d-tests.mjs:354. |
| AUD-67 | Unchecking Skip intro with a zero or negative Fall speed or Animation speed renders nothing forever (or a white-out), while diagnostics report a healthy renderer | OPEN, planned FIXED | Found at rainPass.intro.frag.glsl:46-54,61, rainPass.raindrop.frag.glsl:67,70, rainPass.frag.glsl:68,101, rainPass.wgsl:311,322,326,522,555, config.js:176,557-566, ArgumentCatalog.cs:190-191,271. |
| AUD-68 | UC3D with Original controls and Animation speed 0 (the configurator's minimum for it) renders nothing, animated or single-frame, while Matrix and legacy UC3D show a frozen frame | OPEN, planned FIXED | Found at native.js:119,121,187,203,207,209,222,283, runtime.js:151, config.js:645-649,692, app.js:589-590. |
| AUD-69 | The Matrix scene on the WebGPU renderer goes solid black (or freezes on its last frame) once the canvas passes 8192 px, while diagnostics report ready, no errors and advancing frames | OPEN, planned FIXED | Found at webgpu/main.js:122, canvasSize.js:23-25, rainPass.js:222,226, palettePass.js:118, endPass.js:57. |
| AUD-70 | UC3D on REGL (the default renderer) allocates its offscreen target at the unclamped canvas size and tears itself down silently above MAX_TEXTURE_SIZE, while the Matrix scene survives the browser's drawing-buffer clamp | OPEN, planned FIXED | Found at uc3d/runtime.js:144-146,154-155, regl/uc3dPass.js:56,72,90-93,135, config.js:556, ArgumentCatalog.cs:208. |
| AUD-71 | UC3D on WebGPU uploads the desktop snapshot at full capture size, so desktopBackground=true on a virtual screen wider than 8192 px fails at any resolution; a canvas-only clamp for AUD-23 would not fix it | OPEN, planned FIXED | Found at MainForm.cs:26, NativeMediaBridge.cs:104-110, uc3d/runtime.js:75, webgpu/uc3dPass.js:6,20-24,73,76-79. |
| AUD-72 | No single-instance guard for wallpaper mode: a second --wallpaper launch stacks another full renderer and tray icon on the same WorkerW, attached underneath the first, so its new settings never show and each tray's Pause, Exit and Start-with-Windows act on one process only | OPEN, planned FIXED | Found at Program.cs:53-58, MatrixApplicationContext.cs:40-46,58-62,70,73,78,205,217-220, DesktopWindowHost.cs:16-42,83, MainForm.cs:63,735,740, tests/run-gate.ps1:252. |
| AUD-73 | Concurrent startups collide on the fixed-name .write_test probe and silently move that instance's WebView2 profile and web root to Roaming AppData (54-58% probe failure measured while two processes overlap) | OPEN, planned FIXED | Found at MainForm.cs:97,1031-1042,1098,1108-1125,1127-1135,1247,1252-1255, Logger.cs:67-72, README.md:200,208,643,647. |
| AUD-74 | Every MatrixDesktop instance joins the first instance's WebView2 browser process, whose priority class is set by whichever host started first, and Stop Test kills the Test host's whole process tree, which can take that shared browser down with it | OPEN, planned FIXED | Found at MainForm.cs:97,710-722,810,1031-1042, MatrixApplicationContext.cs:37,231-245, PlaybackPolicy.cs:22-23, CommandBuilder.cs:82-85,146, ConfiguratorForm.cs:140,608,659,663-703. |
| AUD-75 | Text values containing '=' are parsed as new keys: an active UC3D musicFile, pictureDirectory or donorDirectory such as C:/Music/a=b.mp3, or an image URL with a query, is lost on Test, Export and copied-command import | OPEN, planned FIXED | Found at CommandBuilder.cs:185, MatrixArgs.cs:287, ArgumentImporter.cs:512, ArgumentImporter.cs:305, AppCli.cs:269. |
| AUD-76 | Crash handlers can enter single-threaded DbgHelp concurrently: the UI ThreadException and UnobservedTaskException paths each reach MiniDumpWriteDump, and the reservation counter is a cap, not a lock | OPEN, planned FIXED | Found at CrashDumpWriter.cs:113,115, BACKLOG.md:152. |
| AUD-77 | An inherited Object property name in the query (constructor, toString, __proto__) passes config.js's parameter filter, has no parser, and throws before main.js's visible error boundary, so the renderer never starts and no notice explains why | OPEN, planned FIXED | Found at config.js:641, main.js:76. |

Dismissed by verification, recorded so nobody re-opens them:

- Configurator still disposes WebView2 in place, the pattern the renderer's FIXED dead-controller row replaced. REFUTED by adversarial verification (configurator-cs)
- Renderer lifecycle 'releases owned resources' asserts zero survivors without a positive witness; the WebGPU end pass owns no tracked resource. REFUTED by adversarial verification (tests-gate-ci)
- The staged web root is one fixed folder that every build's launch rewrites and prunes, so builds prune each other and a locked staged TTF fails startup. REFUTED: the colliding copy only runs when the bundled TTF differs, and every copy on this box is identical (20692 bytes, same mtime); pruning is one-directional. Residual: the gate writes the owner's staged folder (MainForm.cs:1098-1105, run-gate.ps1:111-113 isolates only the WebView2 profile).

Leads without an established failure, each checked by the lane that owns the code during the
burn-down and given an outcome here:

- Editor defaults for version-dependent fields may differ from the renderer's per-version defaults beyond colours (cfg-args, AUD-13 class).
- UC3D attachMouse may keep a stale 'previous' point when the desktop loses foreground mid-gesture (web-uc3d-zmatrix; AUD-49 adds no host 'leave').
- README.md:195 places WebView2's default profile under LOCALAPPDATA, while MS Learn says the non-UWP default sits beside the exe (final docs pass).
- BACKLOG.md:404-406 says the SwiftShader notice is likely on RDP and VMs; current runtimes may fail WebGL instead (final docs pass, with AUD-22).
- PlaybackPolicy's fullscreen test uses GetWindowRect, whose invisible borders may make a maximized window on a monitor without a taskbar count as fullscreen (host-lifecycle, with AUD-06).
- ScreensaverRegistration Restore() leaves an empty Software/MatrixDesktop key, arguably intended for a product key (shared-diagnostics, with AUD-57).
- Optional culture hardening at FlagNormalization.cs:34 and ZMatrixCfgImporter.cs:49 (gate-ci, with AUD-63).

### Owner report and dialog parity, 2026-11-06

The owner reported the UC3D highlight colour missing from the configurator and supplied a
screenshot of the original screensaver's settings dialog. A registry-backed parity audit
(`HKCU\Software\UselessCreations\Matrix3D`, read only) compared every dialog control with the
UC3D profile: every saved value with a counterpart matches, including the purple, pink and black
colours. The defects were presentation only.

| Item or finding | Disposition | Implementation and checkable evidence |
| --- | --- | --- |
| UC3D highlight and background colours were not shown with Normal code color | FIXED | The pink highlight (`cursorColor`) rendered as "Cursor" under the generic Colors section and background beside it, while the UC3D section held only Normal code color; the original dialog groups all three, and searching "highlight" found nothing. `ArgumentCatalog.ScenePlacements` moves the shared editors into the UC3D section after Normal code color as Highlight code color and Background color, and names Cursor intensity Highlight intensity, while UC3D is selected. Keys, commands, imports, presets and the renderer are unchanged. `ScenePlacementsResolve` (C#), the two scene-placement web checks and the published configurator trio assertion cover it; eleven mutations each fail exactly one named check, and serving the pre-change `app.js` from the publish fails the native assertion. |
| UC3D original sliders used names the owner's v3.4 dialog does not, while README called them original | FIXED | Four of seven differed (Line Density, Glyph Size, Forward/Back Speed, Picture Scale) and Picture Delay reads Shorter / Longer there. The sliders now carry the dialog's names, which also ends two fields sharing the label "Picture delay (seconds)". The web check and the native slider loop assert the names; reverting one fails the named web check. |
| Colour editors announced a generic "Edit color" and had unnamed inputs | FIXED | Swatches and channel, hex and position inputs carry the field name, as the byte-RGB editor already did. The UC3D web check asserts the Highlight names; removing the channel name fails it. |
| OBS/Spout/NDI output, a preset marketplace, per-monitor overrides | ACCEPTED-RECORDED | Deferred by the owner's scope selection on 2026-10-23 (see Future Features); not built in this pass. |

Not changed, recorded for the owner: "Use Filenames As Captions" has no toggle because the
behaviour is always on, which equals the saved value; Forward/back speed stays greyed while
random travel is on, the calibrated behaviour, although the original dialog leaves it enabled;
the saved picture path is the Start Menu Programs folder with My Pictures off, so it is not
copied; the old host's Primary Only and CPU Priority values are app-wide launch choices; the
countdown feature is off in the saved settings and absent here.

### ZMatrix and wallpaper, v1.2.0

The owner activated the previously deferred ZMatrix feature source on 2026-10-23.
The implementation uses fresh source and retains the separately permitted font.

| Item or finding | Disposition | Implementation and checkable evidence |
| --- | --- | --- |
| Continuous wallpaper for every scene | FIXED | WorkerW hosting is independent of scene. Actual Matrix/UC3D/ZMatrix probes render both backends without focus changes. New nested Progman/WorkerW discovery supports this workstation's shell layout. `wallpaper-smoke.py` checks the real parent, rendering and orderly shutdown. |
| Functional ZMatrix streams, persistence, cleanup, phrases and blends | FIXED | Shared seeded simulation and bytewise persistent surface, real WebGL/WebGPU presentation. `zmatrix-tests.mjs` has literal raster and timing cases; thirteen private source mutations fail their named assertions. Actual browser animation/pause/resize/disposal checks pass both backends. |
| Native Windows font descriptors and wallpaper | FIXED | GDI atlases retain real metrics, RGB coverage, rotated ink bounds and substitution warnings. PaintDesktop excludes app windows/icons. Eleven native GDI cases and nine rebuilt mutations cover font behavior and handle cleanup. Native scene probes consume both native fonts without warnings. |
| Configurator settings and original CFG import | FIXED | All 27 controls, structured font/phrase/character editors, byte RGB, preserved inactive metadata and scene filtering. C# and ten DOM cases verify real importer/builder behavior. Twelve original private mutations prove validation, migration, DPI conversion, export and request-order guards. Actual configurator checks pass both backends. |
| Existing UC3D starters misclassified after adding fields | FIXED | Historical field sets are frozen and seed 6 adds only the new starter. Literal historical fixtures retain edited, renamed and deleted starters. |
| Paused original REGL scene could skip initialization or read stale ping-pong state | FIXED | Active time and active shader tick replace elapsed wall time/raw RAF tick. Actual GPU uniform witness starts at tick 0 after an initial pause. An isolated served raw-tick mutation fails the initialization assertion. |
| Independent pause reasons and any-monitor fullscreen detection | FIXED | Clearing one reason retains all other reasons. A fullscreen window on either monitor pauses the wallpaper. Three rebuilt policy mutants fail their intended tests. |
| Copied JSON arguments break on shell characters | FIXED | Copied font/list values use bounded UTF-8 base64url JSON. Direct argv, live query and PowerShell export retain raw JSON. Importer, renderer and native font bridge accept both; command length errors are visible. |
| Screensaver preview/fullscreen entrypoints | FIXED | A real `.scr` apphost supports `/p`, `/s`, `/c`. Owned preview probe checks parent, bounds, focus, resize and parent death; fullscreen exits on input. Saved wallpaper/working-area flags cannot reduce fullscreen coverage. |
| Screensaver tests inherit personal profile/media | FIXED | Explicit synthetic argv profile via `MATRIXDESKTOP_SCREENSAVER_PROFILE`; no registration or personal profile changes in the probe. |
| Screensaver restore could overwrite later choices or retain stale backup state | FIXED | Re-registering after an external choice rebases the backup; failed activation rolls back state. Four synthetic registry cases pass. Owner guard, stale backup and rollback mutations each fail one named test with rebuilt DLL timestamps and executed witnesses. |
| Reused preview iframe retained expired native resource URLs after font edits | FIXED | A current-bootstrap endpoint and readiness promise reconcile document initialization with newer pushed generations. Extracted-initializer cases and source mutations cover generation/query ordering, origins and failed fetches. Actual normal/special font edits produce new GDI generations and visible output on both backends. |
| A preview received native atlases prepared for another draft | FIXED | Bootstrap snapshots carry their exact query identity. Cached, pushed and fetched resources must match the document query; a mismatched current fetch rejects readiness visibly. Named extracted-initializer cases and four identity mutations prove each boundary. |
| Application and configurator query builders use different leading-question-mark conventions | FIXED | Normalize the serialized resource identity to an empty string or one leading question mark. The bootstrap suite checks empty, prefixed, unprefixed and escaped values; native wallpaper startup verifies the application path, and native configurator checks verify the preview path. |
| Late preview replies navigated over newer edits | FIXED | Preview request versions discard obsolete replies. The DOM suite includes a delayed old reply; removing the guard fails that case. |
| A valid large direct Test launched before copied-command formatting threw | FIXED | Validate and prepare the command description before launch, with a direct-launch description when only the copied representation is too long. Rebuilt focused tests, its mutation and both native backends exercise large Unicode values. |
| Native resource change during initial atlas decode was missed | FIXED | Subscribe before loading and await the latest generation before declaring the runtime ready. The 38-case ZMatrix suite exercises a delayed first decode and a newer update. Late subscription and skipped latest-generation wait each fail the named startup regression. |
| Desktop pointer coordinates used physical pixels as CSS pixels | FIXED | Explicit viewport-to-canvas scaling, paired clicks and drag suppression. Three isolated input mutants cover DPI scaling, unpaired clicks and strict decimal parsing, with unchanged original source witnesses. |
| Repeated refresh could retain old native resources | CLOSED-VERIFIED | Twelve sequential native generations retain only current font/wallpaper IDs at a constant catalog count; forced host GC/GdiFlush leaves GDI objects at 59 before and after. Playback remains manually paused while the browser briefly resumes decoding between generations. |
| Fresh Explorer did not create WorkerW with the legacy request | FIXED | Request the modern 0x052C/0xD/1 creation only when no host exists, then use bounded legacy fallback. Native tests cover existing, modern, legacy and unavailable hosts. Both omitted-request mutations fail the named test; a real Explorer restart preserves the same product process and reconnects its wallpaper. |
| Dead native controller was disposed again through the Form's child collection | FIXED | Detach the owned WebView before its guarded disposal and use the same cleanup for direct Form disposal. An injected dead controller proves one disposal attempt and unrelated errors still propagate. Leaving the child parented fails the named rebuilt regression. The real restart now preserves pause, resumes rendering and exits without a dialog. |
| Valid large fonts and repertoires could perform billions of raster scans | FIXED | Reject native requests above 67,108,864 scanned raster pixels before glyph rendering. Boundary and actual GDI rejection cases are included in the native suite. Guard-removal mutation fails before the expensive fixture, with rebuilt assembly/hash witnesses. The resource bridge reports the limit explicitly. |
| Startup shortcut scripting wrapper mishandled Unicode paths | FIXED | Use Windows' Unicode IShellLinkW/IPersistFile APIs. A real STA .lnk roundtrip verifies target, working directory, JSON/Unicode/empty/trailing-backslash argv and isolated disable cleanup. Four rebuilt mutations fail the named test. The native suite touches only synthetic shortcut paths, with no owner Startup changes. |
| UC3D audio ignored host pause when WebView suspension was declined | FIXED | Audio observes host playback and document visibility, silences pending operations and blocks stale play/resume completions. Controlled HTMLAudio/Web Audio cases and source mutations cover initial pause, gestures, asynchronous completion and disposal. Volume-zero real-browser checks prove physical AudioContext suspension and resume; removing host-pause awareness fails that check. Fresh-profile native wallpaper starts the real context without a gesture or autoplay override. |
| Screensaver smoke required guaranteed foreground ownership | FIXED | Observe best-effort focus for a bounded 3.5 seconds. Escape requires exact root HWND and both owner-PID checks. When focus is refused, only a different hit-tested point on the owned saver may receive one mouse move. Session/desktop availability is checked again immediately before input. Twenty-one portable cases and seventeen executed-source mutations cover timing, ownership, both input branches and unavailable desktops. A pass requires responsive UI, a delivered LastInputInfo event and actual exit 0. |
| Screensaver profiles could retain non-topmost flags and lose startup focus retries | FIXED | Fullscreen overrides follow the saved profile, force complete virtual-screen bounds and topmost display, and retain bounded retries after navigation. Native policy cases and five rebuilt mutations verify the scope, ordering and retry policy. Preview retains its no-activation, non-topmost behavior. |
| Foreground helper joined a foreign input queue | FIXED | Remove AttachThreadInput while retaining independent native activation attempts. Two compiled-callgraph/order cases and three rebuilt mutations prove the reachable native path. Current owned UI responsiveness is verified; the earlier timeout did not measure the old UI's responsiveness, so it does not prove an old hang. |
| Screensaver input is not delivered from the agent execution account | CLOSED-VERIFIED | Accepted SendInput does not advance LastInputInfo from the agent account despite WTSActive and matching Default desktops. A private RunAS Helper/WTS launcher runs the exact same published probe as the signed-in owner in RDP session 1. Preview passes; fullscreen acquires focus and exits 0 after a real Escape event. The user need not disconnect RDP or log in locally. No session state or helper authorization policy was changed. |
| README promised complete WebView2 suspension | FIXED | Qualify suspension as a request that WebView2 may decline. Scene clocks and UC3D audio still honor host pause; controlled audio cases and real browser suspension checks cover that fallback. |

### Post-push v1.2.0 audit

The full source audit began after implementation commit `1afb6aa` reached main.
The following findings are resolved and application verification is complete.
Thirty-three focused source/assembly mutations fail their named assertions.
A deployed null closing-snapshot mutation also fails the native stored-value
assertion with ordinary autosave blocked. Exact preset and backup bytes are restored.
The old backlog was reread against the full diff; no old item was deleted.

| Finding | Disposition | Implementation and checkable evidence |
| --- | --- | --- |
| A late camera stream could restart after renderer cleanup | FIXED | Generation-owned acquisition stops obsolete streams and checks ownership again after video.play. Six controlled cases and four source mutations cover late streams, overlapping setup, late failures and exactly-once release. Native synthetic-video verification is included in the final pass. |
| Valid minimum density and column settings created an empty GPU grid | FIXED | Both rain passes retain at least one effective column. Real pass-factory cases include the minimum endpoints and ordinary nonzero controls; each backend's removed clamp causes its named failure. |
| WebGPU image uploads retained ImageBitmap sources | FIXED | Close the source in finally after success, texture-allocation failure or upload failure. The real loader is exercised with owned sources; removing close fails the named ownership assertion. |
| Choose .cfg file replaced the selected text with the current command | FIXED | Show the import panel before assigning the returned file contents. The actual owned Windows dialog imports a UTF-16 BOM fixture with native text decoding. The DOM regression kills the original ordering mutation; the native helper waits for the exact owned filename control. |
| Exported PowerShell scripts lost raw JSON quotes under Windows PowerShell 5.1 | FIXED | Use documented ProcessStartInfo.Arguments with Windows argument quoting and an ASCII base64 transport for UTF-8 content. Real generated scripts pass argv-capture execution under PowerShell 5.1 and 7, including equals, quotes, backslashes, Unicode and a large valid list. An emptied-arguments mutation fails the named PowerShell 5.1 case. |
| A late disabled color-picker callback mutated saved draft arrays | FIXED | Copy editor values at the update boundary so displayed arrays do not alias stored draft data. The actual late callback is checked against saved state; removing the copy fails its named DOM assertion. |
| Crash-dump failures corrupted the three-dump reservation cap | FIXED | Release only an owned reservation, roll back rejected reservations and preserve the native error before handle cleanup. Two synthetic native cases and five freshly rebuilt mutations verify pre-reservation failure, failed writers, the cap and rejection accounting without writing real dumps. |
| Raw JSON containing equals or ampersands became bogus query keys | FIXED | Preserve structured flag/value pairs and distinguish literal explicit flags or standalone JSON from actual raw queries. Thirty-eight isolated CLI/parser cases pass; three fresh assembly variants each fail exactly one named regression. |
| Configurator import split raw JSON at equals or ampersands | FIXED | Apply the same literal structured-value distinction when classifying imported command tokens. Three production-importer cases and three freshly rebuilt mutants verify raw JSON, explicit flags and real query controls. |
| Short help omitted wallpaper discovery and mode diagnostics claimed false semantics | FIXED | Advertise --wallpaper and --help-full. Explain that value-free switches ignore supplied values without calling window modes false. The final native help-dialog probe verifies the displayed text. |
| Looking Glass calibration could request a billion views or override validated quilt fields | FIXED | Limit the quilt to 1024 views and reject zero-size derived tiles; merge validated quilt dimensions after external calibration fields. Eight cases and seven source mutations cover limits, ordinary valid controls and collisions for all four quilt dimensions. Unsupported calibration reports fallback explicitly. Physical Looking Glass hardware remains unavailable. |
| Closing within the draft debounce interval could lose the last edit | FIXED | Capture one final frontend snapshot before disposing WebView2, bounded to 1.5 seconds. Repeated close requests share the operation; malformed, unavailable and late snapshots retain the last received draft, and late faults are observed. Two controlled cases, the actual immediate edit-close-reopen check and five mutations cover these boundaries. |
| Obsolete JsonNode extension was shadowed by the runtime instance method | CLOSED-VERIFIED | Calls already bind to the .NET 10 instance method, so the helper had no reachable effect. Remove that unused source. The 139-case core harness, including real PowerShell export executions, passes against the runtime methods. |
| Argument guide understated the density maximum | CLOSED-VERIFIED | The parser and catalog already support 0.01-8. Correct the guide's 0.01-4 prose to match config.js, ArgumentCatalog and README. Executing makeConfig with 0, 4, 8 and 100 yields 0.01, 4, 8 and 8. |

The native configurator font-edit refresh is closed by its deployed WebView2
probes on both backends. Explorer recovery is closed by the second actual shell
restart, which exercises the fixes for the missing host and disposed controller.

| Calibration item | Disposition | Implementation and checkable evidence |
| --- | --- | --- |
| Independent original sliders and complete amount range | FIXED | Seven native-unit sliders plus numeric editors; amount 100 to 1500 independent of size/view/2D; compatibility preserves custom drafts. C# command/migration tests, rebuilt metadata parity and actual UI synchronization checks. Disabled export/synchronization guards cause their named failures. |
| Native stream population, size and visible coverage | FIXED | Bounded instanced stream simulation, startup/growth, 45-degree view, native 2D sizing, source-alpha/tail/distance fade and replaced leaders. 44 actual-backend calibration cases plus inspected original/backend triptychs. Coupled amount, instant startup, wrong FOV and zero shader alpha mutations are killed. |
| Native motion and camera behavior | FIXED | Integrated/eased/wrapped travel, zero fall-slider movement, rest/move phases, neutral returns and quaternion input. Equation tests kill frozen fall/nonintegrated travel/no-rest mutations. Published apps verify move, stop, reset, roll and idle return on both backends. |
| Original-runtime comparison evidence | CLOSED-VERIFIED | 23 original-process cases, nine captures each; four 25-frame motion recordings. Binary hashes and both registry branches unchanged. Accelerated versus unaccelerated control has byte-identical images and state at all nine timestamps. Comparison method and measured limitations are recorded in `docs/UC3D-CALIBRATION.md`. |

The concrete implementation and comparison plan is in [UC3D calibration](docs/UC3D-CALIBRATION.md).

| Calibration audit finding | Disposition | Evidence |
| --- | --- | --- |
| Inactive units, cycle timing, isometric and original 2D sideways controls remained operable | FIXED | Mode-aware UI gating and active-unit command export, preserving alternate saved values. Explicit old cycle/isometric commands select legacy unless native controls or an explicit mode override them; order and command/preview round trips are checked. Control-lane mutations fail their intended checks, including the 2D sideways guard. |
| Original animation accepted reverse values without reversing | FIXED | Original multiplier is 0 to 4 in import, UI and native simulation. Legacy reverse values remain. Import/UI/direct-simulation mutation witnesses cover all paths. |
| Sleep/resume could simulate the entire missed interval | FIXED | Original render loop caps resumed work at one second and drops debt. Cadence/resume test kills the uncapped-time mutation. |
| Disposed runtime retained packed geometry and picture references | FIXED | Runtime clears grid/photo/desktop references; native scene frees its packed buffer and streams. Actual-backend disposal checks stop frames and remove overlays. |
| Once and FPS-zero mode captured empty startup | FIXED | Original single-frame modes render at ten seconds of selected animation speed. Named test kills time-zero mutation; four published-app cases show visible code with exactly one frame. |
| Picture formation colored old rain and used fitted scale/fixed timing | FIXED | Tagged-only image sampling, inverse native size, sinusoidal grid and wait-for-clear countdown; original personal decode limit 300 pixels. Native tests kill missing tag, reversed scale, static pan, early image switch and wrong decode cap. Both published backends render granted red/green pictures. |
| Grayscale atlas mask appeared to be applied twice | CLOSED-VERIFIED | Native loader preserves grayscale RGB and sets alpha to max RGB, then uses source-alpha blending. Double intensity is intentional for the original bitmap atlas. |
| Calibration speed assertions accepted an empty stream witness | FIXED | Actual browser reports must contain a nonempty stream sample before speed-range assertions. A private real-browser empty-sample injection fails exactly `missing actual stream samples: amount-500/regl`; source hashes remain unchanged by the negative control. |
| Published native smoke printed JavaScript errors without failing | FIXED | Runtime and both configurator paths collect page errors and require a present, empty runtime error array. Controlled runtime/configurator page errors and a runtime error entry each fail their named assertion; absent diagnostics fail and clean arrays pass. Failure cleanup and preset-preservation regressions also pass. |

Existing Frida, Ghidra, UPX, FFmpeg and the toolbox Python were sufficient. No new
tool installation or scripts-utilities catalog addition was needed. Original
assets and analysis/capture data remain private; no donor payload was added here.

| Post-push application audit finding | Disposition | Evidence |
| --- | --- | --- |
| Nonfinite generic numeric imports broke draft JSON serialization | FIXED | `ArgumentImporter` rejects all nonfinite numbers while retaining valid options. The named importer regression serializes the resulting draft; an actual published import verifies storage. Removing the guard fails exactly that rebuilt test. |
| Null preset list records or value containers crashed startup | FIXED | `StorageService` removes null slots and repairs only absent values, preserving valid data and backup bytes. Synthetic live/backup/load/seed/save tests kill missing-filter and missing-repair mutations independently. |
| Hosted actions declared retired Node 20 runtimes | FIXED | CI and release now pin Node 24 releases of checkout 7.0.1, setup-dotnet 6.0.0, setup-python 7.1.0 and action-gh-release 3.0.3 by full commit SHA. Upstream `action.yml` files confirm the runtime and supported existing inputs; SDK/Python versions, permissions and credential settings are preserved. Release publication requires a tag and was not triggered by this maintenance change. |
| README omitted calibrated controls and retained stale behavior/build claims | FIXED | Added the seven catalog ranges/defaults, mode compatibility and calibration command/results link. Source audit corrected preview, color precision, argument and intensity scope claims plus SDK/gate prerequisites. Running the installed SDK's `_CheckForUnsupportedAppHostUsage` target with the checked-in trimmed profile reproduces NETSDK1175; the README now labels that profile unsupported and directs users to the shared framework-dependent publish. |

Both application fixes were verified in the published configurator with a
synthetic portable store and an actual command import. Valid values survived;
the preset and backup files were restored byte for byte. The follow-up full gate
passed 52/0/0 with 110 C# cases.

| Former Open item | Disposition | Implementation and checkable evidence |
| --- | --- | --- |
| Release builds carry no line numbers | FIXED | Embedded symbols in both projects, publish profiles, release and gate. Published exception stacks must contain source lines (`PublishedSymbols.cs`). Clean ReadyToRun comparison added 36,864 bytes across both assemblies. |
| CI and gate duplicate build/publish | FIXED | `ci.yml` delegates build, tests and publish to the gate; independent browser setup remains. Gate nonzero results propagate. |
| Boolean parsers disagree | FIXED | Both accept exact true/false aliases; unknown values are false. `untrue` was the actual substring defect. `renderer-lifecycle.mjs` and `ParityTests.cs`; substring mutation fails the named parity case. |
| WebGPU lacks a quilt pass | FIXED | Holoplay requests route to REGL before the adapter probe and report that choice. `Holoplay uses REGL before any WebGPU adapter probe` kills an inverted selection mutation. Physical display certification remains unavailable. |
| Ordinary hardware gets fake Holoplay calibration | FIXED | `lkgHelper.js` disables lenticular output on missing/invalid/late calibration. Lifecycle test exercises failures and valid calibration; fake-device mutation fails it. |
| GPU disposal incomplete | FIXED | Explicit owners, per-pass cleanup, canceled loops and partial-init cleanup in both legacy renderers. All pass families tested across resize/double cleanup; missing cleanup mutation fails the matching pass. |
| Two upstream shader/FBO defects | CLOSED-VERIFIED | `bloom-probe.py`: changing WGSL LOD to zero changed 0/8192 bytes; a halved-contribution witness changed 6719. REGL black placeholder format changed 0/3404 bytes; nonblack witness changed 3404. Placeholder disposal is fixed under GPU ownership; no upstream defect claim remains. |
| WebGPU reads layout every frame | FIXED | `canvasSize.js` caches CSS dimensions with ResizeObserver and handles DPR changes. Test covers stable frames, CSS-only resize, hidden/restored canvas; per-frame-read mutation fails it. |
| Features from other screensavers | FIXED / ACCEPTED-RECORDED | UC3D behavior is reconstructed as a separate scene with local importer, pictures, relief, motion, glyphs, clock, sound and snapshot options. Tests in `uc3d-tests.mjs`, `uc3d-browser.py`, `MediaCatalogTests.cs` and importer suite. Owner explicitly deferred ZMatrix to another session on 2026-10-23; source below retained. |

### Findings resolved during UC3D integration

| Finding | Disposition | Evidence |
| --- | --- | --- |
| Imported donor directory disabled unless pictures enabled | FIXED | Catalog dependency now permits glyph/audio-only imports. Native smoke exercises donor glyphs and effects. |
| Preview granted no audio for its explicit Test sound action | FIXED | Audio resources are available while preview playback stays muted until requested. Native configurator smoke clicks Test sound. |
| Native media handlers never receive mapped asset-host requests | FIXED | Granted resources use a separate intercepted local origin, with page-origin CORS and exact resource IDs. Native smoke fetches real bytes and rejects ungranted IDs. |
| Preview iframe requests bypass the deprecated WebView2 request filter | FIXED | Request-source kinds explicitly include child frames. Published configurator test loads original glyphs, images and the selected sound. |
| Fractional or oversized manifest schema crashes startup | FIXED | Numeric kind and TryGetInt32 validation reject invalid versions with a warning/folder fallback. Existing malformed-manifest regression includes fractional, oversized, string and null schemas. |
| Fragment compilation failure leaves an untracked WebGL program and vertex shader | FIXED | `createProgram` releases shaders on every outcome and releases failed programs. Fake-GL regression varies both shader failures, linking failure and success; removing shader cleanup fails exactly that named case. |
| Gate child-process checks share an existing user's WebView profile | FIXED | Gate uses an isolated test profile; full gate verifies each owned application's WebView children are reaped. |
| New preset migration would resurrect deleted starters | FIXED | Seed version 3 introduces only the new UC3D preset to existing users. A reverted migration guard fails the named migration test. |
| UC3D behavior differs from the original native renderer | ACCEPTED-RECORDED | `docs/UC3D.md` describes the original implementation and compatibility limits. Decompiled exports and donor media remain private; exact visual parity is not claimed. |

At the UC3D burn-down, ZMatrix, marketplace, OBS/Spout/NDI and per-monitor
configurations were deferred by the owner's scope selection. ZMatrix is now
activated above. The other product features remain deferred.

### Post-push audit, 2026-10-23

The audit re-read native/media/importer code, both renderer families, configurator
dependencies, test/gate failure paths and managed tooling after the initial main push.
Every confirmed additional finding is recorded here and resolved in the follow-up commit.

| Finding | Disposition | Evidence |
| --- | --- | --- |
| Slant stays enabled although UC3D does not consume it | FIXED | Scene dependency disables the legacy-only control. The grouped headless DOM check fails when slant suppression is removed. |
| Greyed color/palette fields still accept edits | FIXED | Compound fields disable native descendants and reject late callbacks after a scene switch. Native-disable and callback-guard mutations each fail the grouped DOM check. |
| Failed native test CDP connection can orphan its app | FIXED | Startup failures terminate and wait, escalating to kill on timeout; successful handoff stays live. Three mocked regressions, a cleanup-removal mutation and actual failed/successful host launches verify ownership cleanup. |
| Null WebGPU context or failed unconfigure can skip device destruction | FIXED | Initialization rejects missing contexts explicitly and cleanup always destroys its owned device. Fault injection and a device-destruction mutation cover the failure path. |
| Hosted Windows checkout fails the served-shader witness | FIXED | Fetch text retains CRLF while Python text reads normalize it. Witnesses now compare fetched bytes with file bytes; the failure reproduced on a clean Windows checkout and the corrected check retains exact content validation. Failed Python probes also print their logs in the gate. |
| Slow CI screenshot processing races the slideshow caption lifetime | FIXED | Browser probes hold the displayed RAF timestamp while inspecting picture pixels and captions. They resume and prove frames advance before teardown assertions, preserving a live-loop failure witness. Production slideshow timing is unchanged. |
| UC3D defaults differ from the owner's installed screensaver settings | FIXED | Scene profile captures purple/pink colors, native glyph/fall settings, 500 landscape streams and 3D/random/sideways motion. Command import/export and preview share scene defaults while preserving explicit overrides. An old-green color mutation fails the named bare-UC3D scenario. Exact untouched starter migration preserves edited/deleted presets. Published native defaults smoke verifies both backends and the configurator. |
| Native preview checks can observe an empty URL or a stale saved scene | FIXED | URL waits tolerate the initial empty frame and require the fresh bare-profile query or exact assigned media-preview target. A deployed edit-tracking mutation reaches the expected explicit-override failure after preview navigation; the original assets are restored and checked again. |
| Native configurator smoke leaves its temporary draft in the published preset store | FIXED | Both configurator cases preserve exact portable preset and backup bytes around startup and GUI edits, including failure paths. The published build is left on the requested UC3D profile with its saved presets retained. Focused preservation tests and native GUI smoke verify the restoration. |

No additional confirmed dead code, inert function or call to a missing target remained in
the audited paths. Shader/device behavior was also exercised on this workstation; physical
Looking Glass hardware and exact native visual equivalence were not certified.

Outside this repo: both EXEs are unsigned, so every download trips SmartScreen. That is a
cross-repo code-signing decision, not a MatrixDesktop task.

## Feature sources

Features the owner wants offered as options here, taken from other Matrix screensavers. Added
2026-10-22. Reviewed behavior is implemented as fresh source under `web/`, with
third-party asset permissions and attribution recorded in `VENDORING.md`.

### ZMatrix, via bigfnj/zmatrix-modernized

Activated after the earlier UC3D scope deferral (2026-10-23). Implementation and
verification dispositions are recorded in the v1.2.0 section above.

[bigfnj/zmatrix-modernized](https://github.com/bigfnj/zmatrix-modernized) is the owner's Windows
11 / VS 2026 x64 build of ZMatrix (copyright 2002 Z. Shaker), a C++ GDI renderer that draws on
the desktop. The reviewed source carries **GPL v2 or later** (`src/LICENSE.TXT`,
`src/SourceCodeReadme.txt`). This implementation uses newly written C#/JavaScript
and the separately permitted font. The retained original feature request was:

- Rain drawn on the desktop itself, behind the icons, by parenting to the `WorkerW` host across
  the whole virtual screen. This became the common wallpaper mode for every scene.
- Blending with the existing wallpaper instead of covering it (`BGMode=bgmodeBitmap`,
  `BlendMode=blendmodeOR`), with the background captured via `PaintDesktop`.
- "Special string" streams: a phrase list (default `The matrix has you`) that falls as its own
  stream at `SpecialStringStreamProbability=0.1`, with its own font and colours.
- Per-stream speed variance (`SpeedVariance`), a separate fade colour, and a user-chosen font and
  character set.
- Trail cleanup modes (`MonotonousCleanupEnabled`, `RandomizedCleanupEnabled`, `BackTrace`).
  Source review confirmed fixed and randomized glyph-cell restoration distances;
  both modes are implemented and tested.

Its bundled "Matrix Code Font" by e-RBi is described as freeware, free to distribute, in
`src/readme.txt`.

## v1.0 — Completed

These features shipped in v1.0.0:

- GitHub Actions CI + Release workflows (`.github/workflows/ci.yml`, `release.yml`).
- `--help-full` opens an embedded argument reference (`<EmbeddedResource>` in both EXEs).
- Configurator live preview — debounced refresh of the bundled `web/` app as the draft
  changes. Shipped first as a second top-level WebView2 window, then replaced in commit
  `808262a` by an in-page iframe. The window implementation was left in the tree but was
  unreachable (nothing sent `openPreview`), and it was removed in the v1.0.2 pass.
- `SystemEvents.SessionSwitch` + `PowerModeChanged` → WebView2 `TrySuspendAsync`/`Resume`.
- Dark/light theme toggle in the configurator (persisted in `ConfiguratorState.UiTheme`).
- Out-of-range numeric input validation feedback (red border + allowed-range hint).
- "Export .ps1" button → `CommandBuilder.BuildPowerShellScript` → clipboard.
- Crash dump writer: `Shared/CrashDumpWriter.cs`, MiniDumpWriteDump + unhandled-exception
  handlers, writes to `%LOCALAPPDATA%\MatrixDesktop\dumps\`.

## Future Features (deferred)

These were considered for v1.0 but moved to a future release:

- **Hot-reload window icon** — declined; the window icon is rarely visible since
  the app runs borderless-fullscreen.
- **First-run wizard** — declined; the configurator's discoverability story is
  already covered by the new `?` button + help modal.
- **Gist sync of presets** — dropped from v1.0 (OAuth/token storage too much
  scope for an initial v1). Revisit with a Personal-Access-Token approach if
  user demand surfaces.
- **Multi-config "profile" support** (`--profile work`/`--profile home`) — declined.
- **Native rendering path** — declined; WebView2 stays the canonical renderer.
- **OBS/Spout/NDI integration** — backlogged for a future release.
- **Community preset marketplace** — backlogged.
- **Per-monitor argument overrides** — backlogged. Would either ship as a
  configurator-generated `.bat` (multi-process) or require the wrapper to
  spawn its own child instances; non-trivial scope.

## Deferred from the v1.0.2 hardening pass

Found and verified during that pass, deliberately not fixed. Each says why.

### Items from this pass, with outcomes

- ~~**Embedded live preview renders at the wrong scale.**~~ **Fixed in v1.0.3.** The
  diagnosis above was wrong about the mechanism: the backing store was not tiny and CSS was
  not upscaling it. `.app-shell` used `min-height: 100%` instead of `height: 100%`, which
  leaves the block size indefinite, so the `1fr` grid track sized to max-content and the
  workspace grew to the full unscrolled height of every field. The shell measured 5569px in
  an 860px viewport and the preview pane came out 512x5140, aspect 0.100. `numColumns` sets
  the cell count along the *longer* axis, so the renderer drew about seven columns instead
  of eighty. The same one line was also pushing the entire command panel off the bottom of
  the window, which nobody had noticed. See `tests/web-smoke.py`.
- ~~**Release symbols are stripped, so crash diagnostics lost line numbers.**~~ **Fixed in
  v1.2.0**: see "Release builds carry no line numbers" under Post-push v1.2.0 audit. `DebugType=none`
  in Release keeps the payload lean, but `CrashDumpWriter` writes `ex.ToString()` into the
  log and that only carries line numbers when debug info is present at runtime. Recommended
  fix is `DebugType=embedded`, which emits no separate `.pdb` (so the payload stays clean and
  the gate still passes) while keeping stack traces symbolised. Not landed because two
  attempts to measure the size delta hit MSBuild incremental-build caching.
- ~~**`glyphIntensity` is a flag that cannot do anything.**~~ **Implemented in v1.0.4**, as the
  glyph brightness multiplier its two siblings already were: `brightness.r` is the glyph
  channel, `.g` the cursor, `.b` the glint, and only the glyph channel had no intensity
  uniform. Applies to the palette and stripe effects in both renderers, exactly where
  `cursorIntensity` and `glintIntensity` apply. Default 1 is a bare multiply with no clamp
  added, so `x * 1.0` leaves every existing render bit-identical. Measured: palette luma
  1.6 at 0, 22.7 at 1, 40.5 at 2. `tests/web-smoke.py` now asserts the flag changes the
  output, which is the check whose absence let it ship inert through all of v1.0.x.

  One consequence worth knowing: `DraftRandomizer` has always set `glyphIntensity` to
  0.8 to 2.4, so a preset saved from Randomize before v1.0.4 carries a value that did
  nothing then and does something now.
- ~~**CI and the gate now duplicate work.**~~ **Fixed in v1.2.0**: see "CI and gate duplicate
  build/publish" under Post-push v1.2.0 audit. `ci.yml` builds and publishes, and then the gate
  builds and publishes again. Consolidating means deciding whether CI keeps its own payload
  assertions or defers entirely to `tests/run-gate.ps1`.
- ~~**Generate the guide's flag reference from `ArgumentCatalog`.**~~ **Done in v1.0.5**, as a
  cross-check rather than generation. See the v1.0.3 section below for why.
- ~~**The two boolean parsers disagree.**~~ **Fixed in v1.2.0**: see "Boolean parsers
  disagree" under Post-push v1.2.0 audit. A wrapper flag treats any value containing "true" as
  true (`Shared/FlagNormalization.ParseBool`), while a web flag requires exactly "true"
  (`web/js/config.js:402`). So `--topmost truthy` is true and `--camera truthy` is false.
  Harmless for realistic input, but it is a real inconsistency now that wrapper flags honour
  explicit values.

### Suspected upstream defects, measured and withdrawn

Both were suspected here and measured in v1.2.0; neither changes a rendered byte, so there is
nothing to report upstream. `tests/bloom-probe.py` forces the WGSL LOD to zero and changes 0 of
8192 bytes (a halved-contribution witness changes 6719), and changes the REGL placeholder's
format for 0 of 3404 bytes (a non-black witness changes 3404); see "Two upstream shader/FBO
defects" under Post-push v1.2.0 audit. The placeholder FBO is now created with
`config.useHalfFloat` and released by the pass's resource owner
(`web/js/regl/bloomPass.js:20-21`, `:26-30`).

- ~~`shaders/wgsl/bloomCombine.wgsl` samples mip levels 1 to 4 from textures created with a
  single mip, left over from a superseded pyramid design, so the weighting is not what the
  shader intends.~~ Measured: no effect on output.
- ~~`js/regl/bloomPass.js` builds its no-bloom placeholder FBO without `config.useHalfFloat`
  and never resizes or destroys it, so it stays a 1x1 uint8 target whose format can mismatch
  the primary.~~ Measured: no effect on output; the placeholder now honours `useHalfFloat` and
  is owned.

### Renderer parity, larger scope

- ~~**No `quiltPass` in the WebGPU pipeline.**~~ **Fixed in v1.2.0**: see "WebGPU lacks a quilt
  pass" under Post-push v1.2.0 audit (Holoplay routes to REGL before any WebGPU adapter probe).
  `--version holoplay --renderer webgpu` silently
  drops the entire Looking Glass feature that `--renderer regl` engages. Fixing it means
  porting the quilt pass; documenting it may be the better answer.
- ~~**`version=holoplay` on ordinary hardware renders garbage.**~~ **Fixed in v1.2.0**: see
  "Ordinary hardware gets fake Holoplay calibration" under Post-push v1.2.0 audit. `lkgHelper` falls back to a
  hardcoded recorded device, so the lenticular interlace is drawn onto a normal monitor. The
  three-second timeout added in the v1.0.2 pass stops it hanging, but the fallback itself is
  still wrong.
- ~~**Per-pass GPU disposal is incomplete.**~~ **Fixed in v1.2.0**: see "GPU disposal
  incomplete" under Post-push v1.2.0 audit. `cleanup()` is now reachable (it runs on
  `pagehide`), which makes the existing listener and camera teardown effective, but several
  WebGPU one-shot allocations still pass no cleanup callback and there is no `.destroy()`
  anywhere under `js/regl/`. The browser reclaims these on context loss, so this is hygiene
  rather than a leak users can hit.

### Deliberately declined

- **Live preview without a reload.** Each settled change reassigns `iframe.src`, so the whole
  page reloads and the renderer reinitialises. Patching config in place would need a message
  channel into the vendored app and real divergence from upstream. Reviewed and kept.
- **Relaxing the preview iframe `sandbox`.** It blocks `getUserMedia`, so the preview cannot
  show the camera-backed mirror effect. Loosening a security boundary for one niche preview
  is the wrong trade; documented instead.
- **`suppressWarnings` on by default.** On software rendering the app shows a notice instead
  of rain until dismissed, which is likely on RDP and in VMs. Defaulting the notice away would
  hide a real hardware-acceleration problem from the people who need to know about it.

## Deferred from the v1.0.3 audit pass

### Items from this pass, with outcomes

- ~~**`gl-matrix.js` is the unminified 214,503-byte development build.**~~ **Done in v1.0.4.**
  Replaced with upstream's own `dist/gl-matrix-min.js` for the same version 3.4.0, renamed to
  `lib/gl-matrix.min.js` to match the existing `regl.min.js`. 214,503 to 52,494 bytes, down
  75.5%. Provenance, hashes and the equivalence check are recorded in `VENDORING.md`.
- ~~**The Win32 icon resource inside each `.dll` is dead weight.**~~ **Resolved in v1.0.5**,
  though not the way this entry assumed. The post-build resource edit is still impossible, and
  now the reason is recorded: `CreateAppHost` takes `IntermediateAssembly` and no icon
  parameter, so the apphost gets its icon by copying the Win32 resources out of the `.dll`.
  Clearing `<ApplicationIcon>` therefore blanks the `.exe` too. What could go was the *other*
  duplicate: the managed `EmbeddedResource`, which was copying bytes already present in the
  `.exe`. `AppWindowIcon` now reads the executable's own icon via `PrivateExtractIcons`.
  114,688 bytes, no PE surgery, no build machinery.
- ~~**The command panel takes about a third of the configurator's height.**~~ **Fixed in
  v1.0.5.** Two-column action grid with the scope select spanning both: 42% of the window down
  to 27%. The guard is `tests/web-smoke.py`, which fails above 30%.
- ~~**Generate the guide's flag reference from `ArgumentCatalog`.**~~ **Goal met in v1.0.5 by
  a different mechanism, deliberately.** `tests/MatrixDesktop.Tests/GuideTests.cs` holds the
  guide, `ArgumentCatalog` and `config.js` to each other in five directions instead of
  generating one from another. Generating would have replaced 660 lines of hand-written prose
  (per-flag meaning, performance notes, recommended ranges, worked examples) with a table
  derived from an id, a default and a one-line help string. Cross-checking makes drift
  impossible and keeps the document worth reading.

### Watching, not acting

- ~~**WebGPU reads `canvas.clientWidth` every frame.**~~ **Fixed in v1.2.0**: see "WebGPU reads
  layout every frame" under Post-push v1.2.0 audit (`canvasSize.js` caches CSS dimensions with a
  ResizeObserver). `webgpu/main.js:172` recomputes the
  canvas size per frame where `regl/main.js:42` correctly does it on a `resize` event. Real,
  but it is the non-default renderer, the read is cheap while layout is clean, and rewiring
  the frame loop's resize path risks more than it saves. Revisit if WebGPU becomes default.

### Investigated and dismissed

Recorded so nobody re-opens them. Both were reported by the audit and neither survived
reading the code.

- **"Mirror passes still use the unfixed click-ripple clock."** They cannot. Both renderers
  construct ripples as `config.clickRipples && config.effect !== "mirror"`, so ripples are
  disabled outright in mirror mode. MD-04 does not reach it.
- **"`pagehide` is registered before `pipeline` is assigned."** Harmless. `cleanup` reads the
  closure variable at call time and uses `pipeline?.cleanup?.()`, so an early `pagehide`
  short-circuits on `null` rather than throwing.

## Historical entries (kept for context)

### Standalone Argument Configurator

Status: Implemented; needs Windows runtime smoke testing

## Summary

Build a second executable, `MatrixDesktopConfigurator.exe`, that opens a WebView-based configuration UI for MatrixDesktop arguments. The configurator exposes desktop wrapper flags and visualizer web flags, groups controls by practical theme, shows the generated launch command live, and launches MatrixDesktop with the current draft through a safe `Test Argument` button.

The configurator supports named presets in a dropdown and restores the last draft on relaunch. Presets are stored in a portable JSON file beside the executable when possible, with an AppData fallback.

## Key Changes

- Add a separate WinForms/WebView2 project that publishes beside `MatrixDesktop.exe`.
- Add argument metadata and command generation for app flags, versions, effects, layout, motion, colors, palettes, stripes, image, mirror/camera, click ripples, and advanced toggles.
- Generate concise commands by default, containing only values changed from defaults.
- Add named preset actions: New, Save, Save As, Rename, Delete.
- Add guarded randomization scopes: Visual preset, Colors only, and Motion/Layout.
- Add command import so an existing MatrixDesktop command or argument line can populate the draft before saving as a preset.
- Disable stripe color editing and suppress `stripeColors` output unless the selected effect is stripe-based.
- Seed first-launch starter presets: `rainbow-haze`, `paradise`, and `stripe effects`.
- Add a safe `Test Argument` flow that launches MatrixDesktop windowed with `--no-exit-on-any-key` unless the draft already chooses a conflicting test mode.
- Replace an existing test process when a new test run starts.

## Test Plan

- Build the full solution in Release with Windows targeting enabled.
- Run a JS module syntax check for the configurator UI.
- Publish the framework-dependent Windows x64 folder and confirm both `MatrixDesktop.exe` and `MatrixDesktopConfigurator.exe` are present.
- Smoke test creating, saving, renaming, deleting, and reloading a named preset.
- Smoke test randomizing all three scopes and confirm launch/window controls are unchanged.
- Smoke test the starter presets appear once and remain deleted if the user removes them.
- Smoke test importing an existing command line and saving the imported draft as a preset.
- Smoke test generated commands for palette, stripes, image, mirror/camera, click ripples, windowed, monitor, and topmost settings.
- Smoke test `stripeColors` is disabled and omitted for non-stripe effects, then enabled for `stripes`, `customStripes`, `pride`, `trans`, and `transPride`.
- Smoke test repeated randomization keeps `stripeColors` to 2-8 colors and palettes to 3-6 stops.
- Smoke test the `Test Argument` button replacing the previous test instance.

## Current Verification

- Release solution build passes with 0 warnings and 0 errors.
- Configurator JavaScript syntax check passes.
- Framework-dependent Windows x64 publish includes both `MatrixDesktop.exe` and `MatrixDesktopConfigurator.exe`.
- ~~Smoke zip integrity check passes.~~ This named a zip nothing in the repository
  produces. Superseded by `tests/run-gate.ps1`, which publishes into
  `artifacts\gate\win-x64-fd\` and asserts the payload contents directly. See
  SMOKE_TEST_PLAN.md for which cases are automated and which stay manual.

## Follow-up UX Notes

- Implemented launch-control clarification in the configurator:
  - `Borderless all monitors` already spans every display.
  - `Monitor index` is a 0-based target monitor number, not the number of monitors connected.
  - `Monitor index` is disabled unless `Single monitor` is selected.
  - `Use working area` is explained as taskbar-safe bounds and disabled for `Windowed` mode.
- Implemented stripe color dependency:
  - `Stripe colors` is disabled unless the selected effect is stripe-based.
  - Command generation skips `stripeColors` when the effect cannot use it.
- Implemented command import:
  - Paste a full `MatrixDesktop.exe ...` command, raw argument line, query string, or simple batch `start` line.
  - Recognized app/web flags populate the draft and can then be saved as a preset.
  - Command generation ignores monitor and working-area values when their selected window mode cannot use them.

### Add Click Ripples To Rain Effects

Status: Completed; Windows runtime smoke passed

## Summary

Added `clickRipples=true` as a web/query flag so click-triggered ripples can layer onto normal rain effects like `effect=stripes` without using `effect=mirror`. Default click ripple shape is circular, with box, triangle, and star options; the feature is wired for both WebGPU and REGL/WebGL renderers.

Windows visual smoke testing confirmed the click ripple feature works after the triangle ripple was corrected to render as a straight-sided triangle.

Example target command:

```cmd
start "" /min "%EXE%" --hide-cursor font=resurrections fps=30 animationSpeed=0.5 forwardSpeed=0.05 numColumns=220 density=2 effect=stripes renderer=webgpu clickRipples=true stripeColors=0.5,0,0.5,0,0,1,0,1,0,0,1,0,0,0,1,0.5,0,0.5 raindropLength=0.5 version=3d
```

## Key Changes

- Add web config support for:
  - `clickRipples=true|false`, default `false`.
  - `clickRippleShape=circle|box|triangle|star`, default `circle`; invalid values fall back to `circle`.
- Keep `effect=mirror` behavior unchanged.
- Add click tracking to the rain pass, not the mirror pass, so the ripple brightness feeds into existing downstream effects: stripes, palette, image, etc.
- Track the most recent 5 clicks on the canvas, storing normalized coordinates and elapsed age.
- Add cleanup so click event listeners are removed when the renderer pipeline is destroyed.
- Implement matching shader behavior in:
  - REGL path: `rainPass.effect.frag.glsl`
  - WebGPU path: `rainPass.wgsl`

## Implementation Notes

- Add a small shared JS helper, for example `web/js/clickRipples.js`, that:
  - Attaches a `click` listener to the canvas only when `config.clickRipples` is true.
  - Converts click coordinates to normalized `0..1` canvas space.
  - Exposes fixed-size ripple data for shaders as 5 entries.
  - Provides `cleanup()`.
- Pass `canvas` into both renderer contexts so rain passes can attach canvas-local click handlers.
- In the rain shaders, compute click ripple brightness as an added effect, alongside existing thunder/random ripple logic.
- Use existing ripple defaults for initial tuning:
  - `rippleScale`
  - `rippleSpeed`
  - `rippleThickness`
- Do not expose extra tuning flags in this first pass beyond `clickRipples` and `clickRippleShape`.

## Test Plan

- Build/publish Windows x64 framework-dependent artifact.
- Verify this command renders stripes and produces click ripples without mirror/camera:
  ```cmd
  MatrixDesktop.exe --windowed effect=stripes renderer=webgpu clickRipples=true version=3d
  ```
- Verify WebGPU fallback still works:
  ```cmd
  MatrixDesktop.exe --windowed effect=stripes renderer=webgpu clickRipples=true
  ```
  Expected: WebGPU renders if available; otherwise REGL/WebGL fallback still renders and click ripples work.
- Verify default behavior is unchanged:
  ```cmd
  MatrixDesktop.exe --windowed effect=stripes
  ```
  Expected: no click ripples unless `clickRipples=true`.
- Verify `effect=mirror` still produces its existing click ripple behavior.
- Run static checks already used for this repo: `dotnet build`, JS module parse check.
  (The "zip integrity check" named here never existed; `tests/run-gate.ps1` is the
  current equivalent and checks the publish payload rather than a zip.)

## Assumptions

- Click ripples should apply to all non-mirror rain effects, not only stripes.
- Circular ripples are the default.
- Existing random/time-based preset ripples remain unchanged.
- `v0.1.0` remains as-is; this feature should land in a new commit and can be tagged separately if released.
