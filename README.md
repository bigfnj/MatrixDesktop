# MatrixDesktop (Visual Studio solution)

This is a minimal Windows desktop wrapper around the original **Rezmason/matrix** web project.

It uses **WinForms + WebView2** to load the included `web/` folder and runs fully offline.

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

### Portable-friendly WebView2 user data

WebView2 keeps a browser profile/cache (“user data folder”). By default, that lives under the user’s profile in `%LOCALAPPDATA%`.

For a truly portable single-folder build, this wrapper now:

1. Tries to store WebView2 user data beside the EXE in: `userdata\`
2. If that folder is not writable, it falls back to: `%LOCALAPPDATA%\MatrixDesktop\userdata\`

Tradeoff: a portable folder can now accumulate cache files next to the EXE (expected for a portable app). Delete `userdata\` to reset.

### Smaller web payload in build/publish output

The project file excludes upstream content that is not needed at runtime:

- `web/playdate/**` (non-runtime)
- `web/svg sources/**` (non-runtime)
- `web/screenshot.png` (large)
- `web/lib/regl.js` (unminified; runtime uses `regl.min.js`)
- all `web/**/*.md` and `web/**/*.txt` (docs/notes)

This reduces build/publish copy work and slightly shrinks the portable folder.

### Smaller publish output via invariant globalization

Publish profiles now set:

- `InvariantGlobalization=true`

This reduces self-contained output size by omitting ICU/culture data.

Tradeoff: if you later add code that depends on full culture-aware operations (rare for this app), revisit this setting.

## Runtime configuration (EXE arguments)

The upstream project is configured via **URL query parameters** (e.g., `?version=3d&effect=mirror`).

This desktop wrapper supports the same configuration by accepting command-line arguments and
appending them to the internal `index.html` URL.

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
| `--exit-on-esc` | `--esc-exit` | Enables physical ESC-to-exit (useful if any-key exit is disabled). |
| `--no-esc-exit` | `--noesc-exit`, `--no-esc` | Disables ESC-to-exit. |
| `--exit-on-any-key` | `--exit-on-anykey`, `--anykey-exit` | Closes the app on any physical key press (default). |
| `--no-exit-on-any-key` | `--no-anykey-exit` | Disables any-key exit (ESC may still exit if enabled). |
| `--foreground-key-exit` | `--require-foreground-key-exit` | Only exit on keypress when MatrixDesktop is the foreground app (default). |
| `--global-key-exit` | `--background-key-exit`, `--global-exit-on-key` | Exit on keypress even when MatrixDesktop is not focused (use with caution). |
| `--hide-cursor` | `--hidecursor` | Hides the mouse cursor while the app is running. |
| `--show-cursor` | `--showcursor` | Explicitly keeps the cursor visible. |
| `--no-devtools` | `--nodevtools` | Disables WebView2 DevTools. Handy for a more locked-down distribution build. |
| `--devtools` | - | Explicitly leaves WebView2 DevTools enabled. |
| `--help` | `-h`, `/?`, `help` | Shows the built-in help dialog. |

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
- Boolean flags accept common forms like `true/false`, `1/0`, `yes/no`, `on/off`.
- Bare boolean web flags such as `--camera`, `--volumetric`, or `--skipIntro` are treated as `true`.
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

- `numColumns` (int) — size of the glyph grid.
- `width` (int) — alias of `numColumns`.
- `density` (number) — volumetric density multiplier (>= 0).
- `resolution` (number) — render resolution scale factor.
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

#### Feature toggles (booleans)

- `camera` (bool) — enables webcam input (used by the mirror effect).
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
- `glyphIntensity` (number) — glyph intensity multiplier (>= 0).

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

- `MatrixDesktop\bin\Debug\net8.0-windows\MatrixDesktop.exe`
- `MatrixDesktop\bin\Release\net8.0-windows\MatrixDesktop.exe`

## Publish (portable, single-folder output)

Publishing produces a folder you can copy to another machine.

### Option A (recommended): self-contained portable folder (no .NET install needed)

- In **Visual Studio**:
  1. Right-click the `MatrixDesktop` project
  2. **Publish...**
  3. Select the profile: **Portable-win-x64**

- Or from a terminal at the solution root:

```bat
publish-portable-win-x64.cmd
```

Output:

- `publish\win-x64\`

### Option B: smaller portable folder (requires .NET Desktop Runtime)

- Visual Studio publish profile: **Portable-win-x64-framework-dependent**

- Or run:

```bat
publish-portable-win-x64-fd.cmd
```

Output:

- `publish\win-x64-fd\`

### Option C (experimental): smaller self-contained output using IL trimming

This is a size optimization and can break apps that rely on reflection/COM activation in surprising ways.
Validate on your targets.

- Visual Studio publish profile: **Portable-win-x64-trimmed-experimental**

- Or run:

```bat
publish-portable-win-x64-trimmed-experimental.cmd
```

Output:

- `publish\win-x64-trimmed\`

## Notes / prerequisites

- This project relies on the **Microsoft Edge WebView2 Runtime** being installed.
  - On many modern Windows systems it is already present (Evergreen runtime).
  - If not installed, the app will show an error message on startup.

- Web assets:
  - The `web/` folder is copied to both **build output** and **publish output**.
  - Some upstream folders and docs are excluded from build/publish output to keep the portable folder smaller.

- WebView2 user data:
  - The wrapper will try to create `userdata\` next to the EXE.
  - If that isn't writable, it will fall back to `%LOCALAPPDATA%\MatrixDesktop\userdata\`.

## Attribution / License

The `MatrixDesktop` wrapper code in this solution is provided as-is.

The included `web/` folder is the upstream **matrix** project and remains under its original MIT license (see `web/LICENSE`).
