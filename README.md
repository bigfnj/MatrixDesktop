# MatrixDesktop (Visual Studio solution)

This is a minimal Windows desktop wrapper around the original **Rezmason/matrix** web project.

It uses **WinForms + WebView2** to load the included `web/` folder and runs fully offline.

Current version: **1.0.1** — see [release notes](https://github.com/bigfnj/MatrixDesktop/releases/latest).

## Visual examples

These screenshots are rendered by the same bundled offline web app that MatrixDesktop hosts in WebView2.

<table>
  <tr>
    <td><img src="docs/images/default-rain.png" alt="Classic green Matrix digital rain" width="420"></td>
    <td><img src="docs/images/paradise-theme.png" alt="Warm orange Paradise Matrix preset" width="420"></td>
    <td><img src="docs/images/pride-stripes.png" alt="Rainbow pride stripe Matrix effect" width="420"></td>
  </tr>
  <tr>
    <td><strong>Classic rain</strong><br><code>MatrixDesktop.exe --version classic</code></td>
    <td><strong>Paradise preset</strong><br><code>MatrixDesktop.exe --version paradise</code></td>
    <td><strong>Pride stripes</strong><br><code>MatrixDesktop.exe --effect pride</code></td>
  </tr>
</table>

## v1.0 highlights

- **Live preview in the configurator.** An embedded preview pane shows the rain effect
  and refreshes as you tweak fields, change presets, import, or randomize (debounced
  250ms). Toggle it from the toolbar.
- **Argument reference embedded.** `MatrixDesktop.exe --help-full` opens a scrollable
  reference dialog with the full argument guide. The configurator's `?` button shows
  the same content. No external file required.
- **Sleep/lock aware.** When the workstation locks or the machine sleeps, the WebView
  suspends (no GL / no audio / no timers). Resumes automatically on unlock / wake.
- **Crash diagnostics.** Both EXEs install an unhandled-exception handler that writes
  a `.dmp` to `%LOCALAPPDATA%\MatrixDesktop\dumps\` and surfaces the path in the error
  dialog.
- **Configurator polish.**
  - Dark/light theme toggle (persisted).
  - Out-of-range numeric inputs show a red border + allowed-range hint inline.
  - "Export .ps1" button copies the current command as a runnable PowerShell script.
- **Automated releases.** Push a `v*` tag and GitHub Actions builds, zips, and
  publishes a Release with both EXEs + assets + SHA256.

## Build warning cleanup (NETSDK1137 / MSB3277)

Some Visual Studio setups (especially those configured to treat warnings as errors) will balk at:

- **NETSDK1137** — using `Microsoft.NET.Sdk.WindowsDesktop` is unnecessary for .NET 5+.
- **MSB3277** — `Microsoft.Web.WebView2` can pull in a WPF control assembly reference
  (`Microsoft.Web.WebView2.Wpf.dll`) even for WinForms-only apps, which may trigger a
  `WindowsBase` 4.0/5.0 conflict warning.

This solution addresses both:

- The project uses `<Project Sdk="Microsoft.NET.Sdk">` with `<UseWindowsForms>true</UseWindowsForms>`.
- The project removes the WebView2 WPF reference during build (WinForms-only), and also suppresses
  MSB3277 as a safety net.

## Optimization pass (what changed vs earlier drops)

This version focuses on *portable distribution hygiene* and *leaner publish output* without touching the upstream visualizer logic.

### AppData WebView2 user data

WebView2 keeps a browser profile/cache (“user data folder”). By default, that lives under the user’s profile in `%LOCALAPPDATA%`.

This wrapper stores WebView2 user data under AppData instead of beside the EXE:

1. Primary: `%LOCALAPPDATA%\MatrixDesktop\WebView2\`
2. Fallback: `%APPDATA%\MatrixDesktop\WebView2\`
3. Last-resort fallback: `%TEMP%\MatrixDesktop\WebView2\`

This avoids WebView2 startup failures when the app is launched from read-only folders, network-style paths, or WSL-mounted locations such as `Z:\home\...`.

The bundled `web\` runtime assets are also staged into AppData before WebView2 maps them:

1. Primary: `%LOCALAPPDATA%\MatrixDesktop\Web\`
2. Fallback: `%APPDATA%\MatrixDesktop\Web\`
3. Last-resort fallback: `%TEMP%\MatrixDesktop\Web\`

This keeps WebView2 asset loading on a normal local Windows path even when the EXE itself is launched from a WSL-mounted folder.

### Smaller web payload in build/publish output

The non-runtime upstream content is **gone from the repository**, not merely excluded from
the build. Commit `bd24373` deleted `web/playdate/**`, `web/svg sources/**`,
`web/screenshot.png` (3.5 MB), `web/lib/regl.js` (unminified; the runtime loads
`regl.min.js`), and the upstream notes (`README.md`, `TODO.txt`, `glyph order.txt`,
`prettier_command.txt`, `webgpu_notes.txt`, `assets/msdf_command.txt`). See
[`VENDORING.md`](VENDORING.md).

What ships today is all of `MatrixDesktop/web/`: `index.html`, `js/`, `shaders/`, `lib/`,
`assets/`, and `LICENSE` — 69 tracked files.

The `Exclude` list on the `web\**\*` Content glob in `MatrixDesktop.csproj` still names
those deleted paths, plus `web\msdfgen\**\*` which was never vendored at all. Those clauses
match nothing on disk now and are kept only as a guard against re-import; `.gitignore` lines
43-49 guard the same paths on the commit side. Two clauses are still live rules rather than
history:

- `web\**\*.md` and `web\**\*.txt` — these are the reason **not to add a `.md` or `.txt`
  file under `MatrixDesktop/web/`**. It would be dropped from every build and publish output,
  and `.gitignore` would refuse to track it in the first place. `web/LICENSE` survives only
  because it has no file extension.
- `web\.gitmodules`, `web\.gitignore`, `web\.gitattributes` — likewise defensive; the
  vendored copy is not a submodule.

### Smaller publish output via invariant globalization

Publish profiles now set:

- `InvariantGlobalization=true`

This reduces self-contained output size by omitting ICU/culture data.

Tradeoff: if you later add code that depends on full culture-aware operations (rare for this app), revisit this setting.

## Runtime configuration (EXE arguments)

The upstream project is configured via **URL query parameters** (e.g., `?version=3d&effect=mirror`).

This desktop wrapper supports the same configuration by accepting command-line arguments and
appending them to the internal `index.html` URL.

## Standalone argument configurator

The solution includes `MatrixDesktopConfigurator.exe`, a companion app for building MatrixDesktop launch arguments without hand-writing the command line.

It provides grouped controls for wrapper flags and visualizer settings, color/palette/stripe editors, named presets, a live generated command, randomization controls, an `Import` button for pasted MatrixDesktop commands, and a `Test Argument` button. Test launches are kept windowed and use `--no-exit-on-any-key` by default so a generated argument can be checked without taking over the desktop.

### Configurator UI

The configurator UI (WinForms shell hosting a WebView2 page) presents all settings on a
single scrollable surface rather than tabbed pages:

- **Single-screen layout.** Every group (Launch, Theme, Effects, Colors, Motion, Layout,
  Advanced) is shown at once under sticky section headers. A left-hand jump list scrolls to
  a section, and a filter box narrows the visible fields by name.
- **Custom color picker.** Color, palette-stop, and stripe swatches open an in-app popover
  with a draggable saturation/value square, a hue bar, and precise hex / 0–1 RGB entry — no
  OS color dialog, full float precision, and palette/stripe rows are directly editable.
- **Embedded live preview.** A preview pane renders the matrix effect (served to an iframe
  from the bundled `web/` assets) and refreshes on any change — field edits, preset
  selection, import, or randomize. The preview query mirrors the actual launch command, so
  what you see matches what `MatrixDesktop.exe` would render.
- **Range enforcement.** Out-of-range numeric inputs clamp to their allowed min/max on
  blur, and cleared fields revert to their default.

The importer accepts full `MatrixDesktop.exe ...` commands, raw argument lines, query strings, and simple `start "" /min ...` batch lines. Recognized settings populate the current draft, which can then be saved as a preset. Stripe colors are only editable and emitted when the selected effect is stripe-based: `stripes`, `customStripes`, `pride`, `trans`, or `transPride`.

The randomizer has three scopes: `Visual preset`, `Colors only`, and `Motion/Layout`. It keeps launch/window controls untouched, avoids camera/image/mirror/debug effects, caps generated `stripeColors` at 2–8 colors, and caps generated palettes at 3–6 stops.

On first launch, the configurator seeds three starter presets into the normal preset list: `rainbow-haze`, `paradise`, and `stripe effects`. They are regular user presets, so they can be edited, renamed, or deleted.

Preset storage is portable-first:

- If the publish folder is writable, presets are stored beside the configurator as `MatrixDesktopConfigurator.presets.json`.
- If the publish folder is not writable, presets are stored under `%LOCALAPPDATA%\MatrixDesktop\Configurator\`.

In framework-dependent publish output, run:

- `MatrixDesktopConfigurator.exe` to build/test/save arguments.
- `MatrixDesktop.exe` to run a generated command directly.

### Desktop wrapper defaults

By default, the WinForms wrapper launches in **borderless fullscreen across all attached monitors** and **any physical key press closes the app while MatrixDesktop is the foreground app** (software-injected key events are ignored).

Wrapper-level flags are consumed by the desktop shell itself and are **not** forwarded to the embedded web app.

### Wrapper-level flags (windowing / app behavior)

| Flag | Aliases | Summary |
| --- | --- | --- |
| `--windowed` | - | Opens in a normal resizable window instead of fullscreen. |
| `--borderless` | `--fullscreen`, `--span`, `--span-all`, `--spanall` | Borderless fullscreen across all monitors. This is the default launch mode. |
| `--single-monitor` | `--singlemonitor` | Borderless fullscreen on the primary monitor only. |
| `--monitor N` | - | Borderless fullscreen on monitor index `N` (0-based). This implies single-monitor mode. |
| `--working-area` | `--workingarea` | Uses monitor working areas so taskbars stay visible. Applies to borderless modes. |
| `--topmost` | - | Keeps the window above other windows. |
| `--no-topmost` | `--notopmost` | Turns off always-on-top behavior. |
| `--exit-on-esc` | `--esc-exit`, `--exitonesc`, `--escexit` | Enables physical ESC-to-exit (useful if any-key exit is disabled). |
| `--no-esc-exit` | `--noesc-exit`, `--no-esc` | Disables ESC-to-exit. |
| `--exit-on-any-key` | `--exit-on-anykey`, `--anykey-exit` | Closes the app on any physical key press (default). |
| `--no-exit-on-any-key` | `--no-anykey-exit` | Disables any-key exit (ESC may still exit if enabled). |
| `--foreground-key-exit` | `--require-foreground-key-exit`, `--foregroundkey-exit`, `--requireforeground-key-exit`, `--no-global-key-exit`, `--noglobal-key-exit` | Only exit on keypress when MatrixDesktop is the foreground app (default). |
| `--global-key-exit` | `--background-key-exit`, `--global-exit-on-key`, `--globalkey-exit` | Exit on keypress even when MatrixDesktop is not focused (use with caution). |
| `--hide-cursor` | `--hidecursor` | Hides the mouse cursor while the app is running. |
| `--show-cursor` | `--showcursor` | Explicitly keeps the cursor visible. |
| `--no-devtools` | `--nodevtools` | Disables WebView2 DevTools. Handy for a more locked-down distribution build. |
| `--devtools` | - | Explicitly leaves WebView2 DevTools enabled. |
| `--help` | `-h`, `/?`, `help` | Shows the built-in help dialog: a short summary of these wrapper flags. Modal. |
| `--help-full` | `--help-arguments` | Shows the whole embedded argument guide in a scrollable dialog. No sibling `.txt` needed. Checked before `--help`, so it wins if both are passed. Modal. |

Practical examples:

- Default launch (borderless across all monitors):
  - `MatrixDesktop.exe`
- Global key exit (any physical key closes even when not focused):
  - `MatrixDesktop.exe --global-key-exit`
- Windowed debug run:
  - `MatrixDesktop.exe --windowed --version 3d`
- Borderless on the primary display only:
  - `MatrixDesktop.exe --single-monitor --effect mirror`
- Borderless on monitor index 1 while keeping the taskbar visible:
  - `MatrixDesktop.exe --monitor 1 --working-area --effect mirror`
- Kiosk-style launch:
  - `MatrixDesktop.exe --borderless --topmost --hide-cursor --no-devtools`
- Disable any-key exit (ESC only):
  - `MatrixDesktop.exe --no-exit-on-any-key --version classic`
- Show the built-in help dialog:
  - `MatrixDesktop.exe --help`
- Show the full embedded argument reference:
  - `MatrixDesktop.exe --help-full`

### Example launcher script

[`RunMatrixDesktop_ARGS.bat`](RunMatrixDesktop_ARGS.bat) is a worked example of a full
argument line. Copy it into `publish\win-x64-fd\` (or an extracted release zip) beside
`MatrixDesktop.exe` and run it. Its comments cover the three ways a hand-written argument
line usually goes wrong: spaces inside comma-separated values, passing both halves of an
alias pair such as `raindropLength`/`dropLength`, and expecting `start /min` to stick when
the app un-minimises itself on startup.

For anything beyond a quick edit of that file, use `MatrixDesktopConfigurator.exe`, which
generates and validates the command for you.

Supported input forms:

- Raw query string:
  - `MatrixDesktop.exe "?version=3d&effect=mirror"`
- Key/value pairs:
  - `MatrixDesktop.exe version=3d effect=mirror camera=true`
- GNU-style flags:
  - `MatrixDesktop.exe --version=3d --effect=mirror --camera=true`
- Space-separated:
  - `MatrixDesktop.exe --version 3d --fallSpeed -0.1`

Notes:

- Values containing spaces or special shell characters should be quoted.
- Key-exit uses a Windows low-level keyboard hook (WH_KEYBOARD_LL) and filters injected events (LLKHF_INJECTED / LLKHF_LOWER_IL_INJECTED), so typical software-injected keystrokes (SendInput/keybd_event) do not trigger exit.
- NOTE: input generated via a virtual HID keyboard driver is often indistinguishable from physical hardware in user-mode; if you need to block those too, you would need device allowlisting logic.
- By default, key-exit only triggers when MatrixDesktop is the foreground app; use `--global-key-exit` to make it close on keypress even when not focused.
- If both an enable and a disable flag are supplied for the same wrapper feature, the **last one wins**.
- Boolean flags accept `true/false`, `1/0`, `y/n`, `yes/no`, and `on/off`, case-insensitively.
- Bare boolean web flags such as `--camera`, `--volumetric`, `--clickRipples`, or `--skipIntro` are treated as `true`, and so is an explicitly empty value (`--camera=`).
- Any other value is passed through and the web app only accepts the exact text `true`, so `--camera maybe` is silently `false`.
- Never put a space inside a comma-separated value unless you quote the whole value. `--stripeColors 1,0,0, 1,1,0` is split by the shell and then silently dropped as malformed; `--stripeColors 1,0,0,1,1,0` or `--stripeColors "1,0,0, 1,1,0"` both work.
- Monitor indices are 0-based and come from `Screen.AllScreens`; their order can change if displays are rearranged in Windows.
- Any unknown parameters are passed through but will be ignored by the web app.

### Allowed arguments

The following arguments are recognized by the upstream `web/js/config.js` URL parameter parser.

#### Core selection

- `version` (string) — Matrix variant preset.
  - Examples: `classic`, `3d`, `operator`, `megacity`, `nightmare`, `paradise`, `resurrections`, `trinity`, `morpheus`, `bugs`, `palimpsest`, `twilight`, `holoplay`, `neomatrixology`.
  - Aliases also exist in the codebase (e.g., `updated`, `throwback`, `1999`, `2003`, `2021`).
- `font` (string) — glyph set.
  - Examples: `matrixcode`, `resurrections`, `gothic`, `coptic`, `huberfishA`, `huberfishD`, `gtarg_tenretniolleh`, `gtarg_alientext`, `neomatrixology`, `megacity`.
- `effect` (string) — post-process effect.
  - Examples: `palette` (default), `plain`, `none`, `stripes`, `customStripes`, `pride`, `trans`, `transPride`, `image`, `mirror`.
- `renderer` (string) — graphics backend.
  - `regl` (default WebGL) or `webgpu` (if supported on the machine).

#### Animation and layout

- `numColumns` (int) — size of the glyph grid, clamped to 1–256.
- `width` (int) — alias of `numColumns`.
- `density` (number) — volumetric density multiplier, clamped to 0.01–4.
- `resolution` (number) — render resolution scale factor, clamped to 0.05–2.
- `animationSpeed` (number) — global animation multiplier.
- `forwardSpeed` (number) — forward motion speed in volumetric mode.
- `cycleSpeed` (number) — glyph cycling speed.
- `fallSpeed` (number) — falling speed.
- `raindropLength` (number) — raindrop length / spacing control.
- `dropLength` (number) — alias of `raindropLength`.
- `slant` (number) — slant angle in **degrees**.
- `angle` (number) — alias of `slant`.

#### Quality / performance knobs

- `fps` (number) — target FPS (0–60).
- `bloomSize` (number) — bloom size (0–1).
- `bloomStrength` (number) — bloom strength (0–1).
- `ditherMagnitude` (number) — dithering amount (0–1).

#### Feature toggles and click controls

- `camera` (bool) — enables webcam input (used by the mirror effect).
- `clickRipples` (bool) — enables click-triggered ripples in non-mirror rain effects such as `stripes`, `palette`, or `image`.
- `clickRippleShape` (string) — click ripple shape: `circle` (default), `box`, `triangle`, or `star`. Invalid values fall back to `circle`.
- `volumetric` (bool) — enables volumetric/3D rendering.
- `glyphFlip` (bool) — flips glyphs horizontally.
- `loops` (bool) — loop mode (WIP).
- `once` (bool) — render a single frame then stop.
- `skipIntro` (bool) — when `false`, starts from the intro/blank-screen sequence (web default is `true`).
- `suppressWarnings` (bool) — suppresses startup notices (e.g., hardware acceleration warning).
- `isometric` (bool) — experimental mode toggle.

#### Glyph / color controls

- `glyphRotation` (number) — rotate glyphs (degrees).
- `cursorIntensity` (number) — cursor glow intensity (>= 0).
- `glyphIntensity` (number) — **accepted but inert in this build.** `web/js/config.js` parses
  and clamps it (>= 0) and defaults it to 1, but no rendering pass reads it: it appears
  nowhere under `web/js/regl`, `web/js/webgpu`, or `web/shaders`. Passing it has no visible
  effect. The argument guide says the same.

Color values are typically provided as comma-separated triples. RGB values are usually in the 0–1 range.

- `backgroundColor` (R,G,B)
- `backgroundRGB` (R,G,B) — alias of `backgroundColor`
- `backgroundHSL` (H,S,L)

- `cursorColor` (R,G,B)
- `cursorRGB` (R,G,B) — alias of `cursorColor`
- `cursorHSL` (H,S,L)

- `glintColor` (R,G,B)
- `glintRGB` (R,G,B) — alias of `glintColor`
- `glintHSL` (H,S,L)

Palettes and stripes are lists:

- `palette` (R,G,B,at,R,G,B,at,...) — where `at` is a stop position (typically 0–1).
- `paletteRGB` — alias of `palette`
- `paletteHSL` (H,S,L,at,H,S,L,at,...)

- `stripeColors` (R,G,B,R,G,B,...) — stripe color sequence.
- `stripeRGB` — alias of `stripeColors`
- `stripeHSL` (H,S,L,H,S,L,...)
- `colors` — alias of `stripeColors`

`stripeColors` is used by stripe-based effects only: `stripes`, `customStripes`, `pride`, `trans`, and `transPride`.

#### Image effect

- `url` (string) — image URL to load when `effect=image`.

#### Advanced / debug

- `testFix` (string) — internal compatibility/debug switch used by the upstream project.

## Build (Visual Studio)

1. Open `MatrixDesktopApp.sln` in **Visual Studio 2022** (or newer).
2. Restore NuGet packages (Visual Studio will usually prompt you automatically).
3. Build and run:
   - **Debug**: `F5`
   - **Release**: `Build > Build Solution`

The output EXE will be in:

- `MatrixDesktop\bin\Debug\net10.0-windows\MatrixDesktop.exe`
- `MatrixDesktop\bin\Release\net10.0-windows\MatrixDesktop.exe`
- `MatrixDesktopConfigurator\bin\Debug\net10.0-windows\MatrixDesktopConfigurator.exe`
- `MatrixDesktopConfigurator\bin\Release\net10.0-windows\MatrixDesktopConfigurator.exe`

## Publish (portable, single-folder output)

Publishing produces a folder you can copy to another machine. There are exactly **two**
publish paths, and only one of them ships the configurator.

### Option A (recommended): framework-dependent portable folder

The supported path. **This is the only publish output that contains both executables.**

- Visual Studio publish profile: **Portable-win-x64-framework-dependent** (both projects have
  a profile with this name, and both target the same output folder on purpose)

- Or, from a terminal at the repository root:

```bat
publish-portable-win-x64-fd.cmd
```

Output: `publish\win-x64-fd\`, containing `MatrixDesktop.exe`,
`MatrixDesktopConfigurator.exe`, the `web\` and `configurator\` assets, and the WebView2
dependencies.

Requires the **.NET 10 Desktop Runtime** on the target machine. Publish both projects
together and at the same version — the script does this, and
`Portable-win-x64-framework-dependent.pubxml` explains why it matters.

### Option B (experimental): trimmed self-contained folder

A size optimization only. It publishes **`MatrixDesktop.exe` alone — no configurator** — and
IL trimming can break code that relies on reflection or COM activation in ways that only
show up at runtime. Validate on your target machines before shipping it.

- Visual Studio publish profile: **Portable-win-x64-trimmed-experimental**

- Or run:

```bat
publish-portable-win-x64-trimmed-experimental.cmd
```

Output: `publish\win-x64-trimmed\`. Self-contained, so no .NET runtime is needed on the
target, but it is the larger folder of the two.

There is no single-file publish. `publish-singlefile-fd.cmd` and its `SingleFile-FD` profile
were removed after being verified non-functional: the profile used item metadata in a
property condition (MSB4190) and requested single-file compression on a framework-dependent
publish (NETSDK1176), so it never produced an executable.

## Verification gate

`tests\run-gate.ps1` is the pre-release check. It builds, runs the regression harness,
parses every web module, asserts the embedded resources, publishes both executables, and
optionally launches them and asserts a real render.

```pwsh
pwsh -File tests\run-gate.ps1 -Tier1Only   # what CI runs
pwsh -File tests\run-gate.ps1              # adds the runtime smoke tiers
```

`-Tier1Only` is the flag CI uses. It covers everything that does not need an interactive
desktop session: build, unit tests, web bundle integrity, embedded resources, and publish
payload contents. The runtime tiers create real windows and capture frames, so they need an
attached interactive session and cannot run on a headless runner.

Exit codes:

| Code | Meaning |
| --- | --- |
| `0` | Pass. Every check that ran, passed. |
| `1` | A real failure. At least one check failed and the cause is in the code or the repo. |
| `2` | Something could not be verified — a check was skipped for lack of an interactive session, missing build output, or similar. Not a pass; the skipped list is printed. |
| `3` | The gate itself broke. Treat as a harness bug, not a product failure. |

Exit 2 is worth treating as acceptable in CI while still printing the not-verified list,
which is what `ci.yml` does. Exit 1 and 3 fail the build.

## Notes / prerequisites

- This project relies on the **Microsoft Edge WebView2 Runtime** being installed.
  - On many modern Windows systems it is already present (Evergreen runtime).
  - If not installed, the app will show an error message on startup.

- Web assets:
  - The `web/` folder is copied to both **build output** and **publish output**.
  - Some upstream folders and docs are excluded from build/publish output to keep the portable folder smaller.

- WebView2 user data:
  - The wrapper stores WebView2 profile/cache data in `%LOCALAPPDATA%\MatrixDesktop\WebView2\`.
  - If LocalAppData is not writable, it falls back to `%APPDATA%\MatrixDesktop\WebView2\`, then `%TEMP%\MatrixDesktop\WebView2\`.

- WebView2 web assets:
  - The wrapper stages the bundled `web\` folder into `%LOCALAPPDATA%\MatrixDesktop\Web\`.
  - If LocalAppData is not writable, it falls back to `%APPDATA%\MatrixDesktop\Web\`, then `%TEMP%\MatrixDesktop\Web\`.

## Attribution / License

The `MatrixDesktop` wrapper code in this repository is licensed under the **MIT License** — see
[`LICENSE`](LICENSE). That covers `MatrixDesktop/`, `MatrixDesktopConfigurator/`, `Shared/`, and
`tests/`.

`MatrixDesktop/web/` is a vendored fork of **[Rezmason/matrix](https://github.com/Rezmason/matrix)**,
which is also MIT licensed, copyright (c) 2018 Rezmason. Its license text is at
`MatrixDesktop/web/LICENSE` and is reproduced at the bottom of the root `LICENSE`. Both licenses
are MIT, so the combined work distributes under MIT with both copyright notices retained.

It is a fork, not a pristine copy: 16 files under `web/` carry local changes.
See [`VENDORING.md`](VENDORING.md) for the exact list, the fork point, and how to diff against
upstream.

