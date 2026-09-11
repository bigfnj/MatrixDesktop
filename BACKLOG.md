# MatrixDesktop Backlog

## Picking this up cold

State as of **v1.0.5**, 2026-09-11. `main` is clean, tagged, CI and Release green.

**Verify before you trust anything.** One command, and it is the whole story:

```pwsh
pwsh -File tests\run-gate.ps1              # 43 checks, needs a desktop session
pwsh -File tests\run-gate.ps1 -Tier1Only   # 14 checks, what CI runs, no session needed
```

Exit `0` pass, `1` fail, `2` could not verify, `3` the gate itself broke. **`2` is not a pass.**

**The one convention that matters here: mutation-test every new check.** Break the thing it
guards, confirm exactly one failure naming the right file, restore, and put the mutation result
in the commit message rather than the green run. This is not ceremony. Three checks in this repo
passed vacuously until someone tried to break them, and one mutation caught a bug in the check
being added that same hour. An unmutated gate is decoration.

**Where the coverage is, and what each layer cannot see:**

| Layer | Catches | Blind to |
| --- | --- | --- |
| `tests\MatrixDesktop.Tests` (86 tests, console EXE, exit code is the result) | argument parsing, command building, colour maths, storage, and drift between the guide, `ArgumentCatalog` and `config.js` | anything that renders |
| `tests\run-gate.ps1` tiers 2 and 3 | the real EXEs launching, rendering, animating, closing cleanly | **layout**: an off-screen footer measures perfectly healthy |
| `tests\web-smoke.py` (13 checks, headless Chromium, runs in CI) | geometry, theme contrast, whether a flag changes the output | GPU-specific behaviour, real WebView2 |

A fourth thing worth knowing: the window icon is read from the `.exe`'s own Win32 resources,
not from a managed resource. `<ApplicationIcon>` cannot be removed, because `CreateAppHost`
builds the apphost's icon by copying the Win32 resources out of the `.dll`.

That middle blind spot is not hypothetical. v1.0.2 shipped with the configurator's entire
command panel below the bottom of the window and every pixel statistic looked fine.

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

### Worth doing

- ~~**Embedded live preview renders at the wrong scale.**~~ **Fixed in v1.0.3.** The
  diagnosis above was wrong about the mechanism: the backing store was not tiny and CSS was
  not upscaling it. `.app-shell` used `min-height: 100%` instead of `height: 100%`, which
  leaves the block size indefinite, so the `1fr` grid track sized to max-content and the
  workspace grew to the full unscrolled height of every field. The shell measured 5569px in
  an 860px viewport and the preview pane came out 512x5140, aspect 0.100. `numColumns` sets
  the cell count along the *longer* axis, so the renderer drew about seven columns instead
  of eighty. The same one line was also pushing the entire command panel off the bottom of
  the window, which nobody had noticed. See `tests/web-smoke.py`.
- **Release symbols are stripped, so crash diagnostics lost line numbers.** `DebugType=none`
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
- **CI and the gate now duplicate work.** `ci.yml` builds and publishes, and then the gate
  builds and publishes again. Consolidating means deciding whether CI keeps its own payload
  assertions or defers entirely to `tests/run-gate.ps1`.
- ~~**Generate the guide's flag reference from `ArgumentCatalog`.**~~ **Done in v1.0.5**, as a
  cross-check rather than generation. See the v1.0.3 section below for why.
- **The two boolean parsers disagree.** A wrapper flag treats any value containing "true" as
  true (`Shared/FlagNormalization.ParseBool`), while a web flag requires exactly "true"
  (`web/js/config.js:402`). So `--topmost truthy` is true and `--camera truthy` is false.
  Harmless for realistic input, but it is a real inconsistency now that wrapper flags honour
  explicit values.

### Upstream, reportable to Rezmason/matrix

Neither has a user-visible effect on a supported MatrixDesktop path, which is why they were
left alone under the vendoring policy in `VENDORING.md`.

- `shaders/wgsl/bloomCombine.wgsl` samples mip levels 1 to 4 from textures created with a
  single mip, left over from a superseded pyramid design, so the weighting is not what the
  shader intends.
- `js/regl/bloomPass.js` builds its no-bloom placeholder FBO without `config.useHalfFloat`
  and never resizes or destroys it, so it stays a 1x1 uint8 target whose format can mismatch
  the primary.

### Renderer parity, larger scope

- **No `quiltPass` in the WebGPU pipeline.** `--version holoplay --renderer webgpu` silently
  drops the entire Looking Glass feature that `--renderer regl` engages. Fixing it means
  porting the quilt pass; documenting it may be the better answer.
- **`version=holoplay` on ordinary hardware renders garbage.** `lkgHelper` falls back to a
  hardcoded recorded device, so the lenticular interlace is drawn onto a normal monitor. The
  three-second timeout added in the v1.0.2 pass stops it hanging, but the fallback itself is
  still wrong.
- **Per-pass GPU disposal is incomplete.** `cleanup()` is now reachable (it runs on
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

### Worth doing

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

- **WebGPU reads `canvas.clientWidth` every frame.** `webgpu/main.js:172` recomputes the
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
