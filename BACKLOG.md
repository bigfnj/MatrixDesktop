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
| `tests\web-smoke.py` (17 checks, headless Chromium, runs in CI) | geometry, theme contrast, scene dependencies, paired sliders and whether a flag changes the output | GPU-specific behaviour, real WebView2 |
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

No active items remain. The Windows PowerShell 5.1 full gate reports
70 PASS, 0 FAIL, 0 UNCHECKED, with its actual Windows process exit observed as 0.
The PowerShell 7 Tier 1 gate reports 37 PASS, 0 FAIL, 0 UNCHECKED.
These include 139 core cases and 22 native cases inside aggregate checks.
Sixteen subsequent native and regression phases each exit 0, covering both
renderers and repeating regressions after wallpaper, configurator and UC3D smoke.
The screensaver preview and fullscreen input-exit probes pass when launched as the
signed-in owner in the connected RDP session. The agent execution account could not
deliver input there; that context is correctly reported as UNCHECKED rather than passed.
Explorer recovery and native configurator refresh pass their application checks.
Every prior UC3D calibration item is retained with its disposition below.

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
| Configurator import split raw JSON at equals or ampersands | FIXED | Apply the same literal structured-value distinction before tokenizing imported commands. Three production-importer cases and three freshly rebuilt mutants verify raw JSON, explicit flags and real query controls. |
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
