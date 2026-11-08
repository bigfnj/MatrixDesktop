# MatrixDesktop Backlog

## Picking this up cold

State for **v1.2.0**, 2026-11-08. ZMatrix, wallpaper and the earlier audits are resolved
below. The burn-down of the 2026-11-07 post-push audit merged at `1956795`; what it left open
is in "Open items". Marketplace, OBS/Spout/NDI and per-monitor configurations remain deferred
by owner decision.

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
| `tests\MatrixDesktop.Tests` (console EXE, exit code is the result) | arguments, storage, media grants, preset migration, guide/catalog/config drift and, with `--verify-published-symbols`, the host wiring in the published `MatrixDesktop.dll` | anything that renders |
| `tests\MatrixDesktop.Native.Tests` (run with `dotnet run`) | GDI font atlases, the native media bridge, desktop host discovery and surface order, the pointer gate, crash dumps and log rotation, startup shortcuts | the page and `MainForm`, which it does not compile |
| `tests\run-gate.ps1` tiers 2 and 3 | the real EXEs launching, rendering, animating, closing cleanly | **layout**: an off-screen footer measures perfectly healthy |
| `tests\web-smoke.py` (headless Chromium, runs in CI) | geometry, theme contrast, scene dependencies, scene placements, paired sliders, whether a flag changes the output, startup notices and recovery after a lost WebGL context | GPU-specific behaviour, real WebView2 |
| `tests\renderer-lifecycle.mjs`, `tests\uc3d-tests.mjs` | resource ownership, resize, compatibility, UC3D timing and input behavior | actual GPU drivers |
| eight more node suites in tier 1 (`zmatrix-tests`, `host-playback-tests`, `host-pointer-tests`, `uc3d-audio-tests`, `native-bootstrap-tests`, `camera-tests`, `renderer-grid-resource-tests`, `lkg-bounds-tests`) | ZMatrix simulation and raster, host pause and pointer forwarding, UC3D audio pause, native bootstrap generations, camera and bitmap ownership, Looking Glass bounds | anything a browser or GPU does: they run in node |
| `tests\uc3d-config-tests.mjs`, `zmatrix-config-tests.mjs`, `effective-defaults-tests.mjs` | the configurator catalog against the web parsers, and untouched editor defaults against what `config.js` draws | the configurator page |
| `tests\uc3d-browser.py`, `tests\uc3d-calibration.py`, `tests\native-uc3d-smoke.py` | actual backend readiness, original slider/stream semantics, picture/depth behavior and published native media delivery | physical Looking Glass hardware |
| `tests\zmatrix-browser.py`, `matrix-playback-browser.py`, `uc3d-audio-browser.py` | ZMatrix and original Matrix rendering, pause, resize, disposal and context loss on real WebGL, and UC3D audio pause | the WebGPU backend: the gate runs both render probes with `--renderer regl` |
| `tests\zmatrix-configurator.py` | the configurator page's controls, imports, focus and preset flows | the C# host: a stub answers in its place |
| `tests\wallpaper-smoke.py`, `screensaver-smoke.py`, `native-zmatrix-configurator.py` (tier 3) | the published wallpaper on the real WorkerW, the screensaver entry points and the published configurator on synthetic stores | the owner's own wallpaper, stores and screensaver registration: each runs in an isolated scope or on copies |
| `tests\gate-lib.tests.ps1` (tier 1, under both shells) | the gate's own verdicts: exit-code classification, dump attribution, native stderr, release, publish-profile and profile-record checks | the product itself |

The layout blind spot is not hypothetical. v1.0.2 shipped with the configurator's entire
command panel below the bottom of the window and every pixel statistic looked fine.

`tests\run-gate.ps1` starts every suite in the table, plus the native smoke helpers' unit
tests, the screensaver focus tests and the UC3D importer tests (`tests/run-gate.ps1:693-695`).
`tests\desktop-snapshot-smoke.py` and `tests\bloom-probe.py` stay outside the gate.

A fourth thing worth knowing: the window icon is read from the `.exe`'s own Win32 resources,
not from a managed resource. `<ApplicationIcon>` cannot be removed, because `CreateAppHost`
builds the apphost's icon by copying the Win32 resources out of the `.dll`.

**Three traps that have each bitten more than once.** `bloomPass`'s crossed blur uniforms
(`width` takes the viewport height, `height` the width) are correct and must not be "fixed":
the worked example is at `MatrixDesktop/web/js/regl/bloomPass.js:61-85`, the v1.0.2 "fix" was
reverted in `430463a`, and `VENDORING.md` repeats the warning. The argument guide's header
version has gone stale twice and now has a gate check (`tests/run-gate.ps1:922-937` runs
`tests/check-release-version.ps1`). And a Python patch script that reads with universal
newlines or writes `utf-8-sig` flips line endings or adds a BOM across a whole file: read and
write bytes, or use `newline=''` with plain `utf-8`, then check `git diff --stat` for
whole-file churn.

**Releasing.** Bump `<Version>` in both `.csproj` and the guide's header line *in the same
commit as the tag*, then push a `v*` tag. `release.yml` first runs
`tests/check-release-version.ps1 -Tag`, which fails unless both csproj versions and the guide
header equal the tag without its `v` (`tests/gate-lib.ps1:133-163`). Then it runs
`tests/check-release-ci.ps1`, which fails unless the newest run of CI's
`Build & Publish Smoke Test` job on the tagged commit succeeded, waiting up to 35 minutes
while that run is queued or running (`.github/workflows/release.yml:43-51`,
`tests/check-release-ci.ps1:21-22`). Only then does it build and stamp the version from the
tag. Stamping from the tag with no such check is exactly what let the repo sit at 1.0.2
through two shipped releases without anyone noticing. The gate runs the same version script
with no tag on every run (`tests/run-gate.ps1:922-937`).

## Open items

The single place to look for "what is left". The per-pass sections further down are a record
of when each item was found and why it was or was not acted on; they are not a to-do list, and
two of them used to share the heading "Worth doing", which is how a v1.0.5 summary came to
claim the backlog was empty when three items were still open. Anything struck through below
has shipped.

2 items are open from the post-push audit of `06809a4` on 2026-11-07, after its burn-down
merged at `1956795`. Each has its disposition and evidence in "Post-push audit, 2026-11-07"
below. A row leaves this table only when its disposition there is FIXED, CLOSED-VERIFIED,
DECLINED-MEASURED or ACCEPTED-RECORDED with checkable evidence. FIXED rows there that say
"Real-app check pending" rest on code, tests and killing mutations while their published-app
checks still run, and AUD-28's release-run check waits for the owner's next tag.

| ID | Severity | Finding |
| --- | --- | --- |
| AUD-26 | medium | native-uc3d-smoke creates a fresh WebView2 profile per launch and nothing prunes them: 132 folders, 1.84 GB and growing |
| AUD-52 | low | Both raw WebGL passes call gl.getError() every frame, a synchronous GPU-process round trip in Chromium |

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

After the burn-down merged, at `1956795` on 2026-11-08, the Windows PowerShell 5.1 full gate
reported 80 PASS, 0 FAIL, 0 UNCHECKED with 179 core cases, and the PowerShell 7 Tier 1 gate
reported 44 PASS, 0 FAIL, 0 UNCHECKED.

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

The burn-down ran as eleven fix lanes and an orchestrator lane and merged at `1956795`. AUD-78
to AUD-80 were found after the audit, one by CI and two by fix lanes, and AUD-81 to AUD-86
during the integration: a merge defect, an unwired check, two test collisions, the
context-loss seam and a pre-existing notice defect. Real-app checks of the host-level items
ran on the published `4951ecc` build.

| ID | Finding | Disposition | Implementation and checkable evidence |
| --- | --- | --- | --- |
| AUD-01 | A second configurator instance silently overwrites presets saved by the first (whole-state last-writer-wins, no instance guard) | FIXED | `4d87b6e`: `Program.Main` takes a session-local named mutex keyed on the store path before the form is built; a second configurator on the same store brings the owner's window forward and exits 0. Core `A second configurator on the same store is refused`: making `TryAcquire` always succeed fails only it. Real-app check pending (`second_instance_case` in `tests/native-zmatrix-configurator.py`). |
| AUD-02 | Rename overwrites the preset's saved values with the unsaved working draft | FIXED | `4071a5c` (host) adds a `renamePreset` route that changes only the name; `4407b1e` (page) calls it instead of saving the draft into the preset. Core `Renaming a preset changes only its name` fails when Rename copies the draft into the saved values ("expected 'palette', got 'stripes'"), and DOM `Rename changes only the preset name and keeps its saved values` fails on the pre-fix `app.js`. Real-app check pending. |
| AUD-03 | A greyed-out Camera toggle still opens the webcam: camera=true is emitted for every effect and both renderers honour it | FIXED | `50a1439` leaves `camera` out of the copied command, preview, Test and the screensaver profile unless the effect is mirror (the draft keeps it); `b3b2ed8` makes `config.js` ignore `camera=true` for any other effect. C# `Camera is omitted unless the effect is mirror` and `renderer-lifecycle.mjs` `camera is requested only by the mirror effect` each fail alone when their guard is deleted. Real-app check pending. |
| AUD-04 | Non-wallpaper surface failure calls ExitThread() inside the constructor, so Application.Run never returns and the process hangs invisibly | FIXED | `9236f6e`: `Program.Main` runs every host through `HostLoop.Run`, which disposes a context that exited in its constructor instead of starting `Application.Run`. Native `Native host loop ends a context that exited while it was constructed`: removing the `ExitRequested` check fails only it. Real app: PASS, `/p` with a destroyed parent and `/p 12345` each exit 0 (3.75 s and 0.23 s) with no dialog, WebView2 child or dump. |
| AUD-05 | Wallpaper mode retries a WebView2 startup failure every 500 ms forever, re-showing the modal error dialog each time it is dismissed | FIXED | `e2f4b18`: every `MainForm` startup error goes through `FailStartup`, which marks the surface failed before its one dialog, and `SurfaceClosePolicy` stops a failed wallpaper from being rebuilt. Core `Host wallpaper does not rebuild a renderer that failed to start` and the published-dll check `OnSurfaceClosed decides through SurfaceClosePolicy with StartupFailed` each fail alone under their mutation (policy ignoring the failure; the old unconditional 500 ms retry). Real app: PASS, one WebView2-missing dialog, then exit 0 in 0.22 s with no rebuilt surface; a healthy surface closed by `WM_CLOSE` is still rebuilt in 0.72 s. |
| AUD-06 | Pausing the wallpaper (tray Pause, battery, fullscreen on another monitor) hides the WebView, so the desktop shows black or the static wallpaper instead of the frozen frame | FIXED | `85fccd0`: `PlaybackPolicy.SuspendsRenderer` is true only for session lock, sleep and display-off, so tray Pause, battery and fullscreen freeze the page and leave the WebView visible. Core `Host pause keeps the frozen frame visible unless lock, sleep or display-off took it away`: suspending for every reason fails only it ("renderer suspension for Manual"). Real app: PASS, a tray-paused wallpaper stays visible and two captures 0.5 s apart are byte-identical and not blank. |
| AUD-07 | No CoreWebView2.ProcessFailed handler: a renderer or browser process failure leaves a long-running wallpaper blank until Explorer restarts or the display changes | FIXED | `f612281`: `MainForm` handles `CoreWebView2.ProcessFailed`, and `RendererFailurePolicy` reloads a lost renderer (3 times per 10 minutes), rebuilds the wallpaper surface after a lost browser process and exits other hosts. Five mutations each fail exactly one check, either core `Host renderer process failure reloads, rebuilds or exits as WebView2 requires` or the published-dll check `WebView2 process failures reach RendererFailurePolicy` (for example `RenderProcessExited` mapped to log-only, or no `ProcessFailed` subscription). Real app: PASS, a crashed renderer reloads on the same surface in 3.02 s, a killed browser gets a new surface in 0.84 s, and a fullscreen launch exits 0. |
| AUD-08 | UC3D desktop snapshot in wallpaper (and preview) mode copies the live screen, baking other applications' windows into the wallpaper; Refresh desktop cannot replace it | FIXED | `88a6932`: wallpaper and screensaver-preview hosts take the UC3D desktop from the wallpaper (`PaintDesktop`), never a screen copy; fullscreen and `/s` keep the copy taken before the window shows. `44cf68c` corrects the `desktopBackground` help. Native `Native bridge takes the UC3D desktop from the wallpaper for wallpaper and preview hosts` fails alone when the screen is always captured, and core `The desktop snapshot help says wallpaper hosts use the wallpaper only` fails on the old text. Real app: PASS, with a palette window over the desktop the wallpaper's snapshot held 0 of 8,640,000 palette pixels and the fullscreen control 100%. |
| AUD-09 | Every WM_SETTINGCHANGE and WM_THEMECHANGED regenerates all native resources on the UI thread, unfiltered and uncoalesced, and wipes the persistent ZMatrix raster; for Matrix and UC3D the refresh does nothing | FIXED | `c757e46` filters setting broadcasts by message and `wParam`, sends them only to a refreshable (ZMatrix) bridge, coalesces them for 400 ms, defers them while paused and publishes only changed inputs; `d08758c` moves the capture and atlas builds off the UI thread. Core `Native refresh ignores unrelated setting broadcasts`, native `Native bridge refresh keeps the generation when nothing changed` and `Native bridge builds edit and refresh work off the UI thread` each fail alone under their mutations. Real app: PASS, an unrelated broadcast cost no CPU and kept the raster, a real change published once, and the UI thread answered every 50 ms probe (max 63.6 ms). |
| AUD-10 | Positional picture IDs are re-keyed by every host refresh while UC3D keeps its startup caption list, so pictures show their neighbour's caption (or 404) | FIXED | `7496cdb`: picture ids are `image-` plus a SHA-256 prefix of the group and full path, so a refresh cannot re-point one; `6571649` makes the native smoke require an empty default image list instead of probing `image-0`. Core `A picture resource id never re-points to another file across catalog rebuilds` fails when positional ids return, and `test_a_hashed_picture_grant_fails_although_image_0_is_404` fails when the image-list assertion is dropped. Real app: PASS, `native-uc3d-smoke.py --case defaults` on the published build. |
| AUD-11 | Every settled ZMatrix configurator edit, even colour-only keys, blocks the configurator UI thread 0.6-1 s re-capturing the whole-desktop wallpaper and rebuilding both glyph atlases | FIXED | `c757e46` caches the ZMatrix wallpaper and each glyph atlas on their real inputs, so a colour-only edit rebuilds nothing; `d08758c` builds changed inputs off the UI thread. Native `Native bridge reuses the wallpaper and atlases across a colour-only ZMatrix edit` and `Native bridge rebuilds only the native work whose input changed` each fail alone (for example a cache that never hits, wallpaper=2). Real app: PASS, colour-only round trips took a median 4.4 ms after a cold first build (640.7 ms in the audit), and a font edit kept the window answering every 50 ms probe (max 43.1 ms). |
| AUD-12 | Copied-command quoting claims values become literal in cmd and PowerShell, but %, !, $ and backtick still expand in text values | FIXED | `4514ab4`: a copied text value holding `%`, `!`, `$` or a backtick travels as `json64`, which `MatrixArgs` and the importer decode for text keys only; Test, preview, Export .ps1 and the screensaver stay raw. `Copied commands keep text values literal in batch files and PowerShell` fails when encoding is narrowed back (cmd ate `%20`, PowerShell lost `$old`), and `Shell-captured copied commands reach the renderer query as literal text` and `Copy then Import recovers literal text values` each fail alone without their decoder. Real-app check pending. |
| AUD-13 | Catalog defaults for cursorColor and stripeColors are not what the renderer draws when they are omitted, so the editors show colours that never render and those exact colours cannot be set; the screensaver profile then renders differently again | FIXED | `fd04eb8`: untouched editors show, and commands leave out, what the renderer draws for the scene, version and effect (`EffectiveDefault` over new version and effect tables, mirrored in `app.js`); Use for screensaver saves the preview's arguments; a one-time store migration keeps every stored draft's render. Five C# mutations each fail one named case, for example dropping the version branch fails `Untouched editors show what the renderer draws for each version and effect` (Columns 80 against 108). `tests/effective-defaults-tests.mjs` compares 32,209 values with `config.js`; the gate runs it since AUD-82. Real-app check pending. |
| AUD-14 | Colour popover outlives renderFields and writes through stale closures: palette stops come back after removal or overwrite a neighbour, and the colour saved differs from the editor | FIXED | `553a04c`: `renderFields` closes the colour popover first, and the popover drops an edit whose anchor left the document. DOM `Colour popover closes when the editor re-renders` fails when the close is removed from `renderFields`, and removing the anchor check fails only "a popover whose anchor left the document still saved its edit". |
| AUD-15 | Every gating change and list edit drops keyboard focus to body; the next Tab restarts at the first field | FIXED | `72dc5a7`: `captureFocus` and `restoreFocus` keep focus on the same field, row and index across `renderFields` and the phrase editor's redraw. DOM `Configurator keeps keyboard focus across re-render` fails without `restoreFocus` ("moved focus to BODY") and without the row clamp, one failure each. |
| AUD-16 | 54 field controls have no accessible name and their visible label does not operate them | FIXED | `e4feb06`: every field control has an accessible name, single-control labels operate their control through `htmlFor`, and compound editors are named groups. DOM `Field controls have accessible names and their labels operate them`: dropping `htmlFor` fails it with "53 controls have no accessible name" (55 before the fix). |
| AUD-17 | 'Last draft' cannot bring back the working draft once any preset was selected; unsaved edits are discarded on preset switch | FIXED | `b5ed725` (host) stores a `scratchDraft` beside the last draft; `bfdabf9` (page) keeps the unlinked draft there while no preset is selected and brings it back from 'Last draft'. Core `The scratch draft survives choosing a preset and a relaunch` fails when the host stops storing it, and DOM `Last draft brings back the unlinked draft after a preset was chosen` fails when the sync ignores the selection. Real-app check pending. |
| AUD-18 | 'Plug me in' unhides the canvas only after the renderer finished loading (REGL sizes to 1x1) and has no re-entry guard (a second click starts a second renderer and a bogus error) | FIXED | `15ce0f4`: Plug me in removes the notice and shows the canvas before the renderer loads, and acts on one click only. Web smoke `Plug me in starts a renderer sized to the visible canvas` fails with the unhide back below the await (1x1 backing store), and `Plug me in clicked twice starts exactly one renderer` fails without `{ once: true }`. |
| AUD-19 | After a GPU context or device loss no scene recovers: the default Matrix scene renders black while diagnostics report healthy, and UC3D/ZMatrix tear themselves down permanently with no visible message | FIXED | `bf5ca61` adds `rendererRecovery.js`: a lost WebGL context or WebGPU device reloads the page, 3 times per 5 minutes, then leaves a notice. `85107d7` clears `ready` and stops drawing on loss in both Matrix renderers, `3b8f1ee` makes UC3D and ZMatrix wait for the loss event and keep render failures visible, and `156dbb2` keeps the notice in view; the merge seam is AUD-85. `a lost renderer reloads within a budget and then explains itself` fails alone without the restore listener, and twelve UC3D and ZMatrix mutations each fail one named case such as `REGL UC3D waits for the lost-context event instead of failing a frame`. Real-app check pending. |
| AUD-20 | WebGPU-to-REGL fallback cannot work once webgpu/main.js has taken the canvas's webgpu context, and it reports a misleading regl null dereference | FIXED | `83eb201`: the new `rendererLoader.js` gives REGL a fresh canvas after a Matrix-scene WebGPU failure and keeps the WebGPU reason in `requestedRendererError`. `WebGPU-to-REGL fallback draws on a fresh canvas and keeps the WebGPU error` fails alone when the fresh canvas is discarded. Real-app check pending (it needs a real WebGPU adapter). |
| AUD-21 | effect=image with the configurator's blank Image URL fetches a remote Wikimedia photo and fails the whole renderer when offline | FIXED | `f7cb3ae` (web-startup lane) treats `url=` as no url and draws uncoloured rain with a console warning when the background cannot load; the lane raised the default image to the owner. `4951ecc` (orchestrator decision, the lane's option B) defaults to the bundled `assets/sand.png`, because no owner preset uses `effect=image` and bundling the CC BY-SA 4.0 Commons photo would be a licensing call. The REGL case of `image effect treats an empty url like no url` fails alone with the Wikimedia default restored, and web smoke `effect=image with no url renders its bundled default with no external request` fails naming the request. Real-app check pending. |
| AUD-22 | The software-rendering notice can never be dismissed in wallpaper, screensaver or preview mode, so on SwiftShader those modes show a static box instead of rain | FIXED | `82f6fdd` (host) marks wallpaper, `/s` and `/p` bootstraps non-interactive; `cfbcb82` (page) then starts the renderer with a console warning instead of the blocking SwiftShader notice. Native `Native bridge bootstrap marks only wallpaper, /s and /p hosts non-interactive` and web smoke `a non-interactive host on SwiftShader starts rendering instead of blocking` each fail alone under their mutation. Real app: PASS on forced SwiftShader, wallpaper and `/p` rendered with no notice while a windowed launch showed it. |
| AUD-23 | UC3D sizes its WebGPU canvas and render target with no device-limit clamp, so a wide virtual screen fails on the first frame and the desktop goes black silently | FIXED | `fe798c0`: the UC3D WebGPU pass requests the adapter's `maxTextureDimension2D` and fits canvas and render target inside it with the aspect kept, warning once. `WebGPU UC3D requests the adapter texture limit and fits the canvas and render target to it` fails alone when the pass passes `Infinity` (a 200x50 target past a 64 limit). Real-app check pending. |
| AUD-24 | Legacy UC3D with isometric and mouse rotation snaps 45 degrees yaw and 22.5 degrees pitch on the first drag, because runtime.update mutates the scene's lastState | FIXED | `d26fbba`: `runtime.update` applies the isometric offset to a derived view, not to the object the legacy scene returns by reference. `legacy isometric mouse rotation starts from the displayed pose without a 45-degree snap` fails alone with the in-place offset restored ("first touch moved yaw by 0.785 rad"). |
| AUD-25 | Python exit 2 is overloaded: argparse usage errors become UNCHECKED 'unavailable environment', which CI then tolerates as green | FIXED | `a386fbe`: a Python exit 2 counts as UNCHECKED only with a printed CANNOT VERIFY, UNVERIFIED or UNCHECKED line; a bare exit 2 is a FAIL naming its log. One classifier in `gate-lib.ps1` serves tiers 1 and 3. `gate-lib.tests.ps1` case `AUD-25 (a)` fails alone when the witness clause is dropped (observed 'unchecked'). |
| AUD-26 | native-uc3d-smoke creates a fresh WebView2 profile per launch and nothing prunes them: 132 folders, 1.84 GB and growing | OPEN, reopened | Found at native-uc3d-smoke.py:76, MainForm.cs:1044-1077. `024244d` (gate-ci lane) deletes each launch's profile when the app stops, retrying for 5 s, and prunes `profile-*` folders older than a day. Three of the four `ProfileOwnershipTests` cases in `tests/test_native_smoke.py` have a recorded killing mutation, each observed as exactly one failure: the early return moved ahead of the cleanup fails only the already-exited subtest, a single removal attempt fails the retry case, and the prune without its age filter fails the prune case. The fourth, `test_removal_that_keeps_failing_warns_instead_of_raising`, has no recorded mutation. The real-app check FAILED on the published `4951ecc` build: 7 of 15 launches through these helpers still left a profile under `artifacts/native-uc3d`, four with a WinError 32 warning (the 5 s retry is too short) and three silently, because `remove_profile` returns True on a `FileNotFoundError` for an inner file while the exiting browser still writes. Reopened for round 2. |
| AUD-27 | Tier-2 'wrote no crash dump' compares a folder-wide count that cannot rise once the 10-dump retention cap is reached, and is not attributed to the smoke pid | FIXED | `2bc42be`: tier 2 fails a case only for a dump named with that case's pid and written after its launch (`Get-DumpsForPid`), not on a folder-wide count that stops rising at the retention cap. `gate-lib.tests.ps1` cases `AUD-27 (B)` and `AUD-27 (C)` fail alone when the pid or the time filter is dropped. Real app: PASS, the full gate under Windows PowerShell 5.1 printed all three 'wrote no crash dump' lines and ran on, and the function found real dumps only for their own pids. |
| AUD-28 | release.yml stamps the version from the tag and runs no gate, so the version drift BACKLOG describes can still ship and a red commit can be released | FIXED | `3ca4263`: `release.yml` runs `tests/check-release-version.ps1 -Tag` and `tests/check-release-ci.ps1` before it builds. The first fails unless both csproj versions and the guide header equal the tag, the second unless CI passed on the tagged commit (it waits up to 35 minutes). `AUD-28 (c)` fails alone when branch runs read the ref name, and `AUD-28 (h)` proves the order check fails with the CI step moved below the publish. Real-app check BLOCKED: it needs a release run, and pushing a tag is the owner's decision. Locally `-Tag v9.9.9` fails naming the three files and `-Tag v1.2.0` passes. |
| AUD-29 | Under Windows PowerShell 5.1 (the documented full-gate shell) redirected native stderr is a terminating error, so several gate probes crash the run instead of reporting FAIL or UNCHECKED | FIXED | `c08a486`, `8d17a4d`: `Invoke-NativeQuiet` runs probes with a function-local Continue and returns the exit code, so native stderr under Windows PowerShell 5.1 no longer ends the run, and the top-level try gains a catch (exit 1 with failures recorded, otherwise 3). `AUD-29 native stderr under ErrorActionPreference Stop is an exit code, not a crash` fails alone under 5.1 when the Continue line is deleted. |
| AUD-30 | Guide promises a tray/log report when process priority is rejected; the tray cannot report it and Realtime is downgraded to High with no message | FIXED | `607118c`: `PriorityPolicy.Apply` reads the class back and reports one Windows did not grant through the log and the tray; wallpaper mode applies priority after the tray exists. Core `Host priority reports a class Windows did not grant` fails alone without the comparison. Real app: PASS, an unelevated `--process-priority realtime` wallpaper ran at High, logged 'Process priority RealTime was not granted; Windows applied High.' and showed it as a tray balloon. |
| AUD-31 | MainForm's own SessionSwitch/PowerModeChanged suspend path is unreachable: the public constructor that enables it has no caller | FIXED | `164e5a2` removes MainForm's unreachable public constructor and its session and power path; `MatrixApplicationContext` is the only owner of those events. Published-dll checks `MainForm defines no public instance constructor` and `SystemEvents session and power subscriptions have one owner` each fail alone when the constructor or a second subscription returns, and both fail on the pre-fix dll. |
| AUD-32 | DesktopPointerBridge runs the desktop-foreground probe for every system-wide mouse event before its 30 ms move throttle | FIXED | `f2f92ca`, `13972c0`: `PointerGate.ShouldForward` applies the 30 ms move throttle before the desktop-foreground probe, and the probe reads the window class without allocating. `Pointer gate throttles moves before probing the foreground` fails alone with the probe first (probes=100), and `Desktop foreground probe allocates nothing per call` fails with the old StringBuilder check. Real app: PASS, 173 moves gave 64 forwarded with the shell in front, 5,682 gave none with another window in front, and forwarding resumed afterwards. |
| AUD-33 | Constructor-time ZMatrix preparation is always thrown away, doubling the wallpaper capture and atlas builds per surface | FIXED | `3602c0f`: the `MainForm` constructor takes only the UC3D desktop snapshot before the window shows, and the one full preparation runs after `SetAssetRoot`. Native `Native bridge ZMatrix startup prepares once` fails alone when the before-show step runs the full preparation (captures=2). Real app: PASS, a ZMatrix wallpaper's first `nativeGeneration` is 1 and no generation-2 resource is fetched. |
| AUD-34 | Documented --restore-screensaver refusal is reported as a crash with a minidump | FIXED | `a0410ee`: the three designed refusals throw `ScreensaverRegistrationRefusedException`, which the commands report as a warning with exit code 1; any other exception still reaches the crash handler. `Screensaver registration commands report designed refusals instead of crashing` and `Screensaver restore command lets an unexpected failure reach the crash handler` fail one at a time across six mutations. Real app: PASS, `--register-screensaver` with the `.scr` missing showed one warning box, exited 1, logged WARN and wrote no dump; the registry exports were unchanged. |
| AUD-35 | Log rotation never runs in short-lived processes, so screensaver-only use grows the shared log past the documented 2 MB cap | FIXED | `675b23b`: `Logger` checks the size on each process's first write and every 10 writes after, so a short screensaver run rotates an oversized log. Native `Native diagnostics log rotates on the first write of a short-lived process` fails alone on the old counter ("no .old file was created"). |
| AUD-36 | Failed dump attempts are unbounded, and each one prunes a retained dump and leaves an empty .dmp behind | FIXED | `9437d88`: a failed dump attempt deletes the file it created, and pruning runs only after a successful write and never ranks the new dump. Native `Native crash diagnostics never evict retained dumps on failed writes` fails alone under each of five mutations, for example pruning before the write ("lost Synthetic-retained-00-pid1.dmp"). |
| AUD-37 | The close flush cancels WM_QUERYENDSESSION, so an open configurator is listed as blocking shutdown, restart and sign-out, and exits even when the shutdown is cancelled | FIXED | `dbe0de7`: the configurator answers `WM_QUERYENDSESSION` at once, keeps running when the shutdown is cancelled and tears down on `WM_ENDSESSION(TRUE)`, decided by `DraftCloseFlush.DefersClose`. Core `Configurator close never cancels a session-end query for the snapshot` fails alone when the query is deferred again. Real-app check pending (`session_end_case` in `tests/native-zmatrix-configurator.py`). |
| AUD-38 | A debounced draft save that comes due during a confirm/prompt lands after Delete or Save As and persists a stale or deleted selectedPresetId | FIXED | `f992104`: a draft save pending when Delete or Save As is pressed is posted before the dialog opens (`flushPendingDraftSave`). DOM `A draft save pending at Delete lands before the delete` and `A draft save pending at Save As lands before the new preset` each fail alone without their flush; `Delete with no pending draft save posts no save of its own` is the settled control. |
| AUD-39 | Host replies carry data the page throws away while its text says otherwise: 'Retained ... for review' for command imports, the PowerShell script on clipboard failure, and the test command description | FIXED | `f73ea91`: the import status names dropped settings, Export .ps1 shows the script for manual copy when the clipboard is busy, and the Test status shows the launched command. DOM `Import status names dropped settings and points only retained ones to the report`, `Export .ps1 shows the script for manual copy when the clipboard is busy` and `Test status shows the command the host launched` each fail alone when their page change is reverted. Real-app check pending. |
| AUD-40 | Phrase editor shows and counts duplicate rows that the saved draft silently drops | FIXED | `9f00258`: Add appends a unique 'New phrase N', the hint counts distinct phrases, and repeats are marked invalid. DOM `Phrase editor shows and counts each saved phrase once` fails under each of six mutations, for example the literal 'New phrase' push (3 rows, 2 saved). |
| AUD-41 | app.js re-implements the colour helpers colorPicker.js exports, the copies disagree, and the palette row persists null channels | FIXED | `b20fc21`: `colorPicker.js` exports one strict hex parser that the palette and stripe rows and the popover share; `app.js` loses its drifted copies, and invalid hex is marked and never saved. DOM `Hex boxes refuse invalid colours and read text as the popover does` fails when a null guard is removed ("the row saved the invalid hex 'red'"). |
| AUD-42 | Keyboard focus is not managed for the colour popover, the help modal or the section jump list | FIXED | `70803a8`: the popover opens with focus in HEX, its square and hue bar take arrow keys, Shift+Tab or Escape return to the swatch and Tab moves on past it; help is a modal that restores focus to its opener; a jump focuses the section heading. DOM `Keyboard focus moves into and back out of the popover, help and section jumps` fails alone under each of 20 mutations, for example with the hex focus on open removed ('opening the popover did not move focus into its hex box'). Real-app check pending. |
| AUD-43 | Stop Test stays enabled after the test window exits on its own, and the comment calling it the single source of truth is wrong | FIXED | `391f5a0`, `ae9d412` (host) watch the Test process and call `configHost.testExited(pid)` once when it ends by itself; `a94b715` (page) disables Stop Test only for the running test's pid. Core `A Test host that ends by itself sends one exit notice for the current test only` fails alone without `EnableRaisingEvents`, and DOM `Stop Test follows the host's exit notice for the running test only` fails when the pid comparison is deleted. Real-app check pending (`test_exit_case`). |
| AUD-44 | Every renderFields rebuilds two datalists holding every installed font | FIXED | `d57a268`: one `datalist#installed-font-families` is built once and both font-name boxes use it. DOM `Font-name boxes share one installed-font list across re-renders` fails when the list is rebuilt per render ("150 options, detached"). |
| AUD-45 | Dead code and stale markup in the configurator page, and the preview-unavailable reason is never shown | FIXED | `e6f186a`: the disabled preview button says why, and the dead code goes (`allFields`, `el.groupNav`, the unreachable preview branch, two colour exports and the stale title). DOM `Preview button says why it is disabled without web assets` fails on the old toggle title. |
| AUD-46 | Filtering removes the screensaver buttons from the page, and searching 'screensaver' cannot find them | FIXED | `4e2227b`: the screensaver actions are a searchable item of the Launch section, found by their own text. DOM `Filter finds the screensaver actions by their own text` fails when the skip rule is reverted ("lost the screensaver actions"); 'palette' is the negative control. AUD-84 repaired the check after AUD-61's rename. |
| AUD-47 | fps=0 gives a black screen in the default Matrix scene, while the guide says 'tends to look frozen' and UC3D/ZMatrix draw one frame | FIXED | `6a859d8`: both Matrix renderers treat `fps` 0 as single-frame, so the default scene draws one still frame, as UC3D and ZMatrix do. `check_regl_single_frame` in `tests/matrix-playback-browser.py` fails alone with the old regl rule (0.00% of pixels lit), and `WebGPU Matrix renderer at fps 0 draws one still frame and stops` fails with the old WebGPU rule. Real-app check pending. |
| AUD-48 | canvasSize.js's DPR media-query listener is inert: it only re-arms itself, and DPR changes already work through get() | FIXED | `c2f8aee` deletes the inert DPR media-query listener; DPR is read live in `get()`. `canvas size observes CSS changes and DPR without frame layout reads` fails alone with the listener restored, and its DPR assertion fails if DPR is captured once at construction. |
| AUD-49 | Page-side handlers with no host sender (matrix-host-state web message, chrome.webview resource listener, pointer 'leave'), and the native-bootstrap tests drive production assertions through the dead route | FIXED | `712030e` deletes the bridge's dead `chrome.webview` resource listener and makes `native-bootstrap-tests.mjs` inject through the live routes; `db32a63` drops the dead `matrix-host-state` branch and pointer 'leave'. `initializer takes pushed resources only from the parent frame, never a web message` fails alone with the listener restored, and the `host-playback-tests.mjs` assertion `a matrix-host-state web message has no host sender and must not pause` fails when the branch returns. |
| AUD-50 | Docs promise 'Enable sound' when autoplay is blocked, but the synthesized-audio path awaits a resume() that stays pending, so the button never appears | FIXED | `148cda4`: `uc3d/audio.js` races `resume()` against a 1.5 s grace, so blocked autoplay shows Enable sound and a click or a late resume starts playback. `blocked autoplay offers Enable sound after the grace; a click or a late resume starts playback` fails alone with the bare `await context.resume()` restored. |
| AUD-51 | UC3D's picture-failure warning is pushed again on every slideshow cycle with no dedupe or cap, so the warnings array grows without bound | FIXED | `129716d`: UC3D `addWarning` ignores a message it already holds and keeps at most 32. `a failed slideshow picture warns once however often the slideshow revisits it` fails without the duplicate guard (10 pushes), and `UC3D overlay warnings are capped at 32 distinct entries` fails without the cap. |
| AUD-52 | Both raw WebGL passes call gl.getError() every frame, a synchronous GPU-process round trip in Chromium | OPEN, measured | Found at regl/uc3dPass.js:135, regl/zmatrixPass.js:44, native.js:108, runtime.js:95. Measured on the published `1956795` build (REGL): a fresh `--windowed` process and WebView2 profile per run, ABBA order, 4 runs per variant, CDP `TaskDuration` per rendered frame over 20 s, against a copy with only the per-frame `getError` removed. UC3D at 1500 streams: 3.08 ms median against 2.54 ms (0.54 ms saved, ranges overlap by one sample). ZMatrix: 0.93 ms against 0.55 ms (0.39 ms saved, ranges separated). Both run at 60 fps either way, but that is 23 to 33 ms of renderer main-thread time per second on an all-day wallpaper, so it is not declined. Planned FIXED in round 2: check errors after setup and restore, not every frame. |
| AUD-53 | Dead and duplicated code in the UC3D/ZMatrix modules, including a ZMatrix invalid-setting warning that config.js makes unreachable while the host builds atlases from the raw argv | FIXED | `aaa61ff` applies the page's ZMatrix list rules in `NativeMediaBridge.ReadTextArray`, so the native atlas matches the JS repertoire; `28f2e7d` moves the shared `createProgram` to `js/webglProgram.js`, removes UC3D's duplicate and deletes the dead code, including the unreachable ZMatrix warning. Native `Native bridge replaces an invalid ZMatrix phrase list with the page's default` fails alone without the control-character rule, and `REGL UC3D builds its programs only through the shared createProgram` fails with a renamed local builder. |
| AUD-54 | The gate's private WEBVIEW2_USER_DATA_FOLDER blinds the tier-2 'leaves no userdata beside the exe' check, and no tier checks where the product's profile actually lands | FIXED | `427f8ce` logs one 'WebView2 profile requested=... effective=...' record per initialisation; `57d5bff` makes tier 2 require exactly one such record requesting `LocalAppData\MatrixDesktop\WebView2` and replaces the 'userdata' probe with a before-and-after snapshot of folders beside the exe. Core `Host WebView2 profile record keeps the format the gate parses` and `gate-lib.tests.ps1` cases `AUD-54 (a)` and `AUD-54 (b)` each fail alone under their mutations. Real app: PASS, every case logged one record for `%LOCALAPPDATA%\MatrixDesktop\WebView2`, and the gate's verdict failed once the record was removed. |
| AUD-55 | Page-error collectors are asserted before teardown and before most interactions, so later errors are collected and never checked | FIXED | `f829b12`: web smoke adds `configurator interactions raise no page errors` over errors collected after the load check, and three browser probes assert page errors after teardown. The audit's own `app.js` mutant fails exactly that check (1 of 21), and a teardown mutant fails `matrix-playback-browser.py` at the moved assertion. |
| AUD-56 | web-smoke.py request-log suppression is inert: it sets log_message on a functools.partial, not on the handler class | FIXED | `9286de1`: `serve()` uses a handler class with a no-op `log_message`. `the static server writes nothing to stderr` fails alone with the old partial restored (3 stderr lines for two requests). |
| AUD-57 | Screensaver registration tests leave an empty TestFixtures key under HKCU Software/MatrixDesktop on every run | FIXED | `1c99f5e`: registration fixtures use one flat key per run (`Software\MatrixDesktop.Tests-<guid>`), which `DeleteSubKeyTree` removes whole. `Registry fixtures remove every key they create` fails alone with the nested path restored. The empty `TestFixtures` key that earlier runs left on the owner's box is still there for the orchestrator to remove. |
| AUD-58 | Argument guide's boolean section is stale: the wrapper rewrites unknown values to false and the web parser accepts six spellings, not exactly 'true' | FIXED | `23ded18`: the guide's Booleans section says the wrapper converts any other value to false, names the web parser's spellings and lists all 25 wrapper boolean keys. `The guide's boolean rules match both parsers` fails alone on the old sentence, on a missing `on` spelling and on a dropped key. |
| AUD-59 | README says a missing WebGPU adapter reports unverified coverage, but the calibration command it documents exits 1 instead | FIXED | `8d53713`: with no WebGPU adapter, `uc3d-calibration.py --renderer webgpu` prints 'CANNOT VERIFY WebGPU: browser has no adapter' and exits 2; `4018307` registers that case in tier 1 with the adapter hidden and passes it only on exit 2. `AUD-59 (a)`, `AUD-59 (b)` and `AUD-59 (c)` in `gate-lib.tests.ps1` each fail alone under their mutations, and the script's own mutations (probe disabled, return 0) fail the registered case. Real app: PASS, the 5.1 tier 1 printed 'ok UC3D calibration reports a hidden WebGPU adapter as unverified'. |
| AUD-60 | BACKLOG 'Picking this up cold' points to trap details that do not exist and its coverage map omits every v1.2.0 test layer | FIXED | Documentation only, in the commit that records this burn-down. The bloomPass trap now points at the worked example (`MatrixDesktop/web/js/regl/bloomPass.js:61-85`) and the revert `430463a`, and `VENDORING.md` gains one inventory row saying the crossed blur uniforms are upstream's and deliberate (the import `927eb26` already had them). The patch-script trap states the rule in place instead of pointing at details that never existed. The coverage map gains rows for the native suite, the other tier-1 node, config and browser suites, the tier-3 probes and the gate self-tests, each started from `tests/run-gate.ps1` (`:486`, `:549-556`, `:622-635`, `:702-705`, `:737`, `:1374-1376`). The state line carries the burn-down date and no longer claims every published check passes. The optional automated BACKLOG check from the plan was not added: it needs an owner decision and a core csproj change. |
| AUD-61 | Docs name a 'Restore previous screensaver' control; the configurator button is labelled 'Restore screensaver' | FIXED | `90ca827`: the restore button reads 'Restore previous screensaver', the name README, docs/ZMATRIX.md and the guide use. `Screensaver action labels match the controls the docs name` fails alone with the old label, listing the three docs. |
| AUD-62 | BACKLOG still lists two measured non-defects as 'Upstream, reportable to Rezmason/matrix', plus a fixed per-frame layout read under 'Watching, not acting' | FIXED | The heading now reads "Suspected upstream defects, measured and withdrawn" and carries the probe's numbers (`tests/bloom-probe.py:111,115` asserts zero changed bytes; the 0/8192 and 0/3404 counts are the v1.2.0 row) and the placeholder's current ownership (`web/js/regl/bloomPass.js:20-21`, `:26-30`). The six v1.0.2 bullets fixed in v1.2.0 and the "Watching, not acting" layout read are struck through with a pointer to their FIXED rows; no row was deleted. Documentation only; the optional structural BACKLOG test was not added (decision table). |
| AUD-63 | InvariantGlobalization, presented as part of the portable optimization, reaches only the local pubxml publish, not the release zip or the gate-verified payload | FIXED | `424d13c` removes `InvariantGlobalization` from both framework-dependent publish profiles; tier 1 checks that every profile property matches the gate's publish and that neither published `runtimeconfig.json` sets invariant globalization. `AUD-63 (b)` fails alone with the property re-added, naming the configurator profile. |
| AUD-64 | The renderer clock never rebases, so float32 time degrades a long-running wallpaper: rain motion drops to 32 Hz after about 3 days of active time and 16 Hz after about 6, high speeds lose trail levels within hours, and the WebGPU palette/stripe dither turns into screen-wide blocks within hours | FIXED | `c93c777`: the new `js/shaderTime.js` feeds the Matrix shaders time from an origin that moves forward only when the last frame went unseen (a 60 s gap, a 1 s gap past 1 h, or always past 6 h), restarting at 600 s; click stamps move with it. `shader time stays small over days and moves its origin only when the last frame went unseen` fails alone with the gap rule off, and `check_regl_time_rebase` fails with time uniforms near 345,600. Real-app check pending. |
| AUD-65 | The regl symbol pass compares a float32 frame counter, so after 2^24 active frames (78 h at 60 Hz, 32 h at 144 Hz) Operator and Palimpsest glyphs cycle 3x too fast, then 5x and 8x | FIXED | `75015d6`: `shaderTick` maps the regl frame counter onto a period that is a multiple of the glyph skip and of 2, so it stays exact in float32. `regl tick stays exact in float32 past 2^24 frames and cycles glyphs on the same frames` fails alone with the identity mapping. |
| AUD-66 | UC3D legacy controls upload the unbounded active time as a float32 stream position, so after a few days of active time the streams step in jumps of up to 2% of screen height at 16 Hz | FIXED | `0542bfb`: UC3D legacy time uniforms are reduced exactly on the CPU before the float32 upload, and both shaders read the reduced glyph cycle. `legacy stream heads stay where exact math puts them after days of active time` fails alone with raw time (0.384 world units off at 3 days), and the cycle and depth cases fail on their own slots. Real-app check pending. |
| AUD-67 | Unchecking Skip intro with a zero or negative Fall speed or Animation speed renders nothing forever (or a white-out), while diagnostics report a healthy renderer | FIXED | `0e72fe8`: `config.js` forces `skipIntro` in the Matrix scene when `animationSpeed * fallSpeed <= 0` and logs a console warning. `an intro that cannot progress is skipped instead of rendering nothing` fails alone with the guard disabled (at fallSpeed 0). |
| AUD-68 | UC3D with Original controls and Animation speed 0 (the configurator's minimum for it) renders nothing, animated or single-frame, while Matrix and legacy UC3D show a frozen frame | FIXED | `de584b3`: at Original speed 0, UC3D first runs the ten-second speed-1 stream entry, so the frozen field is populated; nonzero speeds keep their startup ramp. `original speed 0 shows a populated frozen field, animated and single-frame` fails alone with the warm-up scaled by speed (0 of 100 streams), and `original nonzero animation speeds keep their pinned startup ramp and geometry` guards the other side. |
| AUD-69 | The Matrix scene on the WebGPU renderer goes solid black (or freezes on its last frame) once the canvas passes 8192 px, while diagnostics report ready, no errors and advancing frames | FIXED | `9986abc`: the WebGPU Matrix renderer requests the adapter's texture limit, fits the canvas inside it with the aspect kept, decodes oversized textures again, and keeps `ready` false after an uncaptured validation error. `WebGPU Matrix renderer requests the adapter's texture limit and keeps every target inside the device's` fails alone with the clamp bypassed (12000x2880 against 8192). Real-app check pending. |
| AUD-70 | UC3D on REGL (the default renderer) allocates its offscreen target at the unclamped canvas size and tears itself down silently above MAX_TEXTURE_SIZE, while the Matrix scene survives the browser's drawing-buffer clamp | FIXED | `fe798c0`: the UC3D REGL pass fits its render target to the WebGL texture, renderbuffer and viewport limits with the aspect kept. `REGL UC3D fits its render target to the WebGL texture limit with the aspect kept` fails alone with the limit removed ("UC3D WebGL error 1281"). On SwiftShader WebGL a 9000 px request drew at 8192x327. |
| AUD-71 | UC3D on WebGPU uploads the desktop snapshot at full capture size, so desktopBackground=true on a virtual screen wider than 8192 px fails at any resolution; a canvas-only clamp for AUD-23 would not fix it | FIXED | `2d2f7f1`: the UC3D WebGPU pass fits the desktop snapshot to the device's texture limit and resizes it with `createImageBitmap` before upload. `WebGPU UC3D downscales a desktop snapshot wider than the texture limit before upload` fails alone with the full-size upload (9000x600 past 8192). Real-app check pending. |
| AUD-72 | No single-instance guard for wallpaper mode: a second --wallpaper launch stacks another full renderer and tray icon on the same WorkerW, attached underneath the first, so its new settings never show and each tray's Pause, Exit and Start-with-Windows act on one process only | FIXED | `a690adb`, `e680668`: `WallpaperInstanceGuard` lets the newest wallpaper launch take over (waiting up to 10 s), keeps isolated test launches in their own scope, attaches new surfaces on top, refreshes Start with Windows when the tray menu opens, and lets a launch blocked by an elevated holder run unguarded with a warning. Core `Host wallpaper guard lets the newest launch take over and leaves other scopes alone` fails alone when the running instance is ignored, and native `Native host attaches a newer surface above an older one in the same desktop host` fails at HWND_BOTTOM. Real app: PASS, a second launch took over within 0.22 s with one tray icon left, and a launch under another scope was untouched. |
| AUD-73 | Concurrent startups collide on the fixed-name .write_test probe and silently move that instance's WebView2 profile and web root to Roaming AppData (54-58% probe failure measured while two processes overlap) | FIXED | `02ab7ca`: each write probe uses its own `.write_test-<pid>-<guid>` file, so overlapping startups stay in LocalAppData, and any fallback logs a warning. Core `Host write probe stays writable while another instance holds its probe open` fails alone with the fixed name restored, and `Host storage warns when it falls back past the preferred base folder` fails without the warning. |
| AUD-74 | Every MatrixDesktop instance joins the first instance's WebView2 browser process, whose priority class is set by whichever host started first, and Stop Test kills the Test host's whole process tree, which can take that shared browser down with it | FIXED | `553c56b`: Stop Test closes the Test host, waits, then kills only that process, not its tree, so a WebView2 browser it started for other instances survives. Core `Stop Test ends the Test host but not the processes it started` fails alone with a tree kill, both in the injected call order and with a real stand-in child. Real-app check pending. |
| AUD-75 | Text values containing '=' are parsed as new keys: an active UC3D musicFile, pictureDirectory or donorDirectory such as C:/Music/a=b.mp3, or an image URL with a query, is lost on Test, Export and copied-command import | FIXED | `4514ab4`: a text flag takes the next token as its value even when it contains '=', in `MatrixArgs` and in the importer, and a parity case holds `TextKeys` to the catalog. `Text values containing equals survive the native parser` and `Copied local text values containing equals import exactly` each fail alone when their old rule returns. Real-app check pending. |
| AUD-76 | Crash handlers can enter single-threaded DbgHelp concurrently: the UI ThreadException and UnobservedTaskException paths each reach MiniDumpWriteDump, and the reservation counter is a cap, not a lock | FIXED | `aece64f`: a non-reentrant `SemaphoreSlim(1, 1)` with a 10 s bound serializes path choice, the native write and failed-file cleanup, and a same-millisecond name gets a numbered suffix. `Native crash diagnostics let one thread at a time into the dump writer` fails alone with the guard widened ("max 2"), and `Native crash diagnostics bound the wait for another thread's dump` and `Native crash diagnostics keep every same-instant dump under its own name` fail on their own mutations. |
| AUD-77 | An inherited Object property name in the query (constructor, toString, __proto__) passes config.js's parameter filter, has no parser, and throws before main.js's visible error boundary, so the renderer never starts and no notice explains why | FIXED | `106c973`: `config.js` accepts only own parameter and font entries (`Object.hasOwn`), and `main.js` calls `makeConfig` inside the visible startup-error boundary; `156dbb2` keeps that notice in view (AUD-86). `inherited Object property names in the query are ignored like any unknown key` fails alone with the `in` filter back (TypeError), and web smoke `a configuration exception shows the visible startup error` fails with `makeConfig` outside the boundary. |
| AUD-78 | web-smoke's glyphIntensity check samples a fixed 2.5 s after the canvas appears, so on a slow runner the bright render can still be black and CI fails with nothing broken | FIXED | Found by CI run 42468134537 on `fd2358f`, which failed "glyphIntensity changes the stripe render: luma at 0 is 1.60 and at 2 is 0.00"; the same run's smoke process died with a fatal Python stderr-lock error from the static server's request logging, the AUD-56 defect. `5b3f8a5`: the glyphIntensity checks wait for `ready`, 10 frames and 2 s of simulation time (60 s deadline) instead of sleeping 2.5 s. CPU throttling did not reproduce the CI failure; 1000 ms of request latency did, failing the old check on both renders while the new one passes. The palette case of `glyphIntensity changes the {effect} render` fails alone when the uniform is pinned to 1. |
| AUD-79 | Both renderers pick the effect with `config.effect in effects`, which also accepts inherited names, so effect=constructor hands Object to the pipeline as a pass factory instead of falling back to palette | FIXED | Found by the web-startup lane while fixing AUD-77. `15738e8`: both renderers pick the effect with `Object.hasOwn(effects, config.effect)`, and `config.js`'s key filter and font lookup follow the same rule (shared with AUD-77). Web smoke `inherited effect names fall back to the palette render` fails alone with the regl lookup back on `in` (0 frames), and `config ignores inherited Object names as parameter keys and font names` fails with either `config.js` lookup reverted. Real-app check pending (the WebGPU line needs a real adapter). |
| AUD-80 | Core and native test cases that reach Logger append to the owner's real %LOCALAPPDATA%/MatrixDesktop/MatrixDesktop.log, because the log path is a static read-only value with no test seam | FIXED | Found by the shared-diagnostics lane. `bbe9c02`: both harnesses call `HarnessLog.Redirect()` first, pointing `Logger` at a per-run temp log, and a guard case runs last. `Diagnostics logging stays in this run's fixture log` and `Native diagnostics logging stays in this run's fixture log` each fail alone when their redirect is removed; the guard checks the path before writing, so no mutated run touched the owner's log. |
| AUD-81 | Merge defect: two lanes both linked `DesktopWindowHost.cs` and `PlaybackPolicy.cs` into `tests/MatrixDesktop.Native.Tests`, so the merged native suite failed to build (NETSDK1022), which the solution build does not show | FIXED | `23a5fad` removes the duplicate Compile items. The build failure was the failing check; afterwards `dotnet run --project tests/MatrixDesktop.Native.Tests` builds and passes 49/49. Found by the integration smoke. |
| AUD-82 | The AUD-13 drift guard `tests/effective-defaults-tests.mjs` was added but nothing ran it, so it could not fail the gate | FIXED | `23a5fad`: tier 1 runs it with the regenerated loadState fixture as `configurator effective defaults match the renderer for every version and effect`, UNCHECKED without node. A fixture copy with one version default changed fails it with 10 mismatches, and the real fixture passes. |
| AUD-83 | Merge collision in a test: the DOM smoke's stub host answered command requests concurrently, which the real configurator never does, so the AUD-13 check timed out once another lane's fetches moved its first edit past the command debounce | FIXED | `a6022b8`, `f74dbd9`: the stub queues buildCommand, importCommand and testCommand in arrival order; product code is unchanged. `Stub replies in the host's arrival order, so a slow earlier build never replaces the latest command` fails alone on the old concurrent stub and with the queue bypassed. An adversarial verifier accepted it after one repair. |
| AUD-84 | Merge collision in a test: AUD-61 renamed the restore button while AUD-46's filter check hard-coded 'RESTORE SCREEN', a cut of the old label | FIXED | `5b18dfc`: the check cuts its search text from the rendered label, with a witness against degenerate cuts; the product filter is unchanged. `Filter finds the screensaver actions by their own text` fails alone with case-sensitive matching and under AUD-46's own mutation. |
| AUD-85 | Seam between the AUD-19 halves: the web-gpu lane's context-loss check expected `ready` to stay false after a restore, but the web-startup lane's recovery reloads the page, so the check read the reloaded document | FIXED | `1fb10f8` makes `check_regl_context_loss` follow the reload and keep the original assertion past the reload budget; `f6e47b2` clears REGL Matrix `ready` on the first frame that finds the context lost. Without the sticky lost flag the check fails ('ready came back after restore although the restored context draws black'), and without the restore listener the reload leg fails. Real-app check pending. |
| AUD-86 | Pre-existing: `showError` put its notice after the full-viewport canvas while the body hides overflow, so every startup or renderer error notice sat below the fold (top 627 px in a 360 px viewport) | FIXED | `156dbb2`, made inside the web-startup lane because AUD-19 and AUD-77 depended on it: `showError` hides every canvas first. `a configuration exception shows the visible startup error` and `a renderer loss past the reload budget leaves a visible notice` assert the notice is inside the viewport, and removing the hide line fails both on that assertion. |

Dismissed by verification, recorded so nobody re-opens them:

- Configurator still disposes WebView2 in place, the pattern the renderer's FIXED dead-controller row replaced. REFUTED by adversarial verification (configurator-cs)
- Renderer lifecycle 'releases owned resources' asserts zero survivors without a positive witness; the WebGPU end pass owns no tracked resource. REFUTED by adversarial verification (tests-gate-ci)
- The staged web root is one fixed folder that every build's launch rewrites and prunes, so builds prune each other and a locked staged TTF fails startup. REFUTED: the colliding copy only runs when the bundled TTF differs, and every copy on this box is identical (20692 bytes, same mtime); pruning is one-directional. Residual: the gate writes the owner's staged folder (MainForm.cs:1098-1105, run-gate.ps1:111-113 isolates only the WebView2 profile).

Leads without an established failure, each checked by the lane that owns the code during the
burn-down and given an outcome here:

- Editor defaults for version-dependent fields may differ from the renderer's per-version defaults beyond colours (cfg-args, AUD-13 class).
  - Outcome: confirmed and fixed inside AUD-13 (`fd04eb8`). Versions also change font, columns, speeds, bloom, slant, density and dither, and `tests/effective-defaults-tests.mjs` now compares 32,209 catalog values with `config.js` for every version and alias.
- UC3D attachMouse may keep a stale 'previous' point when the desktop loses foreground mid-gesture (web-uc3d-zmatrix; AUD-49 adds no host 'leave').
  - Outcome: confirmed by reading the code, not fixed. `attachMouse` clears its previous point only on pointerleave (`MatrixDesktop/web/js/uc3d/scene.js:136-149`) and the host forwards only move, down, up and wheel (`MatrixDesktop/DesktopPointerBridge.cs:37`), so the first move after the desktop regains the foreground turns the whole excursion into one delta. Passed to the round-2 audit.
- README.md:195 places WebView2's default profile under LOCALAPPDATA, while MS Learn says the non-UWP default sits beside the exe (final docs pass).
  - Outcome: no lane took it up. `README.md:222` still says the default profile lives under `%LOCALAPPDATA%`. The app always requests its own folder (AUD-54's profile record shows `%LOCALAPPDATA%\MatrixDesktop\WebView2`), so only that sentence about WebView2's own default is in question. Still open as a docs lead.
- BACKLOG.md:404-406 says the SwiftShader notice is likely on RDP and VMs; current runtimes may fail WebGL instead (final docs pass, with AUD-22).
  - Outcome: measured in part by AUD-22's real-app check. On WebView2 157.0.4341.42, `--disable-gpu` renders through WARP (Microsoft Basic Render Driver), which the notice's detector does not flag because it matches 'swiftshader' only (`MatrixDesktop/web/js/main.js:25-34`), so no notice appears; SwiftShader needed `--enable-unsafe-swiftshader`. RDP and VM sessions were not measured, and whether WARP should raise the notice is a new lead.
- PlaybackPolicy's fullscreen test uses GetWindowRect, whose invisible borders may make a maximized window on a monitor without a taskbar count as fullscreen (host-lifecycle, with AUD-06).
  - Outcome: measured, not a defect (host-lifecycle). On both monitors here a maximized window's `GetWindowRect` is the work area plus 8 px and never covers the monitor, because the taskbar reservation is larger; on a monitor with no taskbar the visible frame covers it too, so extended frame bounds would give the same verdict. No AUD-06b item.
- ScreensaverRegistration Restore() leaves an empty Software/MatrixDesktop key, arguably intended for a product key (shared-diagnostics, with AUD-57).
  - Outcome: confirmed and kept (shared-diagnostics). Nothing else reads or writes `Software\MatrixDesktop`, keeping a product's own key is normal, and deleting the parent would race a concurrent Register. On the owner's box the key holds only AUD-57's old test litter.
- Optional culture hardening at FlagNormalization.cs:34 and ZMatrixCfgImporter.cs:49 (gate-ci, with AUD-63).
  - Outcome: read, not changed (gate-ci). `Shared/FlagNormalization.cs:34` parses with the current culture and no failing input was found; `MatrixDesktopConfigurator/ZMatrixCfgImporter.cs:49` matches section headers with `IgnoreCase` and no `CultureInvariant`, so an all-caps `[SPECIALTEXT]` header is missed under tr-TR or az. The recommendation is `CultureInfo.InvariantCulture` and `RegexOptions.CultureInvariant`; passed to the round-2 audit.

Round 2 is in progress. A second audit is verifying the leads gathered during the integration:
a Codex review of the burn-down merge, the fix lanes' out-of-lane notes, two seams the
integration fixes left (UC3D counts one frame that drew nothing during a context loss; WebGPU
scenes keep `ready` until `device.lost` settles) and findings from the real-app checks. They
sit in the orchestrator's round-2 leads list and become rows here only once verified. AUD-26
and AUD-52 stay open for that round.

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
The 2026-11-07 audit later found unreachable code in these paths: MainForm's session and
power path (AUD-31, removed in `164e5a2`) and dead configurator page code (AUD-45, removed in
`e6f186a`).

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
- **`suppressWarnings` on by default.** On software rendering (SwiftShader) windowed and
  fullscreen launches show a notice instead of rain until dismissed, which is likely on RDP and
  in VMs (not measured; see the SwiftShader lead under the 2026-11-07 audit). Wallpaper,
  screensaver (/s) and preview (/p) take no input, so they log a console warning and render
  anyway (AUD-22). Defaulting the notice away would hide a real hardware-acceleration problem
  from the people who need to know about it.

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
