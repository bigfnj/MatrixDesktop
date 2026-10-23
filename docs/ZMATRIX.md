# ZMatrix and desktop wallpaper

MatrixDesktop's ZMatrix scene recreates the functional stream and raster behavior
reviewed in [bigfnj/zmatrix-modernized](https://github.com/bigfnj/zmatrix-modernized)
at revision `7eefa2919ef2ac976fd8a566176e20a6541f996d`. It is newly written C# and
JavaScript integrated with the existing offline renderer. The original C++ sources
and executable are not bundled. The Matrix Code Font has its own freeware notice
under `web/assets/zmatrix/matrix-code.LICENSE`.

## Running it

Select **ZMatrix** in the configurator or use `--scene zmatrix`. The **ZMatrix
Desktop** starter chooses wallpaper mode. Window mode is independent of scene,
so Matrix and UC3D also work behind desktop icons:

```cmd
MatrixDesktop.exe --wallpaper --scene zmatrix
MatrixDesktop.exe --wallpaper --scene matrix
MatrixDesktop.exe --wallpaper --scene uc3d
```

Wallpaper spans the Windows virtual desktop. It is parented to a shell wallpaper
window and does not take keyboard focus. The host supports the traditional
top-level WorkerW layout and the nested Progman/WorkerW layout observed on current
Windows. It does not change icon list styles or the saved Windows wallpaper.
Explorer recovery rediscovers the shell host. A missing host is reported instead
of opening a fullscreen window over the owner's applications.

The tray menu controls pause, settings, exit and startup. Startup is an explicit
per-user choice. Fullscreen pause defaults on; battery pause defaults off. Pause
reasons combine, so resuming manually does not override a session lock or another
automatic pause. Renderers use active time and exclude paused intervals.
`--process-priority auto` uses Idle for wallpaper and Normal otherwise. This
setting applies to the host process; it does not promise a WebView2 GPU priority.

## Behavior mapping

| Original capability | MatrixDesktop behavior |
| --- | --- |
| Maximum streams | Independent stream allocation, with at most one new stream starting each simulation tick. |
| Speed variance and refresh | A stream assigned wait N advances every N+1 ticks. Refresh is measured in milliseconds and stays independent of presentation FPS. |
| Bright head and dim trail | Previous heads are replaced with randomized dim glyphs. Pixels persist until overwritten or cleaned. There is no automatic finite-tail fade. |
| Monotonous cleanup / BackTrace | Restore one glyph cell at a fixed distance behind the head. |
| Randomized cleanup / Leading / SpacePad | Restore a cell at Leading plus a random distance from zero through SpacePad. These are cleanup distances, not horizontal spacing. Both cleanup modes may run together. |
| Special strings | Eligible streams occasionally emit a phrase vertically, preserving character order and their separate font metrics. An empty phrase list stays empty. |
| Bitmap background | Native PaintDesktop capture includes wallpaper and excludes desktop icons and application windows. Background changes replace the resource generation. |
| Solid background | Use the selected byte RGB color. Cleanup restores that color. |
| XOR, AND and OR | Bytewise RGB raster operations. Canvas2D's Porter-Duff xor operation is not used. Transparent bitmap AND/OR follow the donor's direct-overlay path; color background ignores blend. |
| Foreground and fade colors | Separate normal and special colors, speed-linked dimming, then RGB gain/offset and byte clamping. |
| Windows fonts | Separate LOGFONT descriptors, GDI rasterized glyphs, actual ink bounds and resolved-face reporting. RGB coverage is retained for subpixel antialiasing. |
| Continuous desktop operation | Available as the common wallpaper window mode for every scene. |
| Screensaver | Genuine `.scr` configuration, fullscreen and child-preview entrypoints using the same renderer. |

The implementation shares one deterministic simulation and persistent raster
between the WebGL and WebGPU presentation passes. A requested backend that fails
initialization reports the failure; it does not silently certify the other
backend. The standalone browser fallback uses browser fonts and a fallback
background, with explicit approximation warnings. Native WebView2 resources use
exact resource grants rather than a filesystem path API.

## Configurator and importing

The configurator offers sliders with numeric inputs, byte RGB editors, a
character editor, phrase lists and structured editors for every supported
LOGFONT member. It retains values from inactive scenes while emitting only the
selected scene's visual parameters. Switching scene preserves the selected
window mode. **Test Argument** uses a normal window.

Use **Import** to paste a ZMatrix `.cfg` or choose its file. Imports retain explicitly
selected launch controls and report conversions or unsupported donor fields.
Bounds and finite-number validation prevent a malformed file from allocating
unbounded streams, glyphs or atlases. Normal characters are unique BMP characters;
phrase strings also use BMP text, matching the donor's character model.
Native and browser font raster work is limited to 67,108,864 scanned pixels per
font request; excessive font-size/repertoire combinations report the limit.

The [argument guide](../MatrixDesktop_Argument_Guide.txt) documents every setting
and its units. Export `.ps1` when using structured font/list settings from
PowerShell. The generated Windows command string uses Windows argument quoting;
PowerShell has its own quote interpretation. Exported scripts pass an ASCII-safe
UTF-8 argument payload directly to Windows, preserving JSON under PowerShell 5.1
and 7, including quotes, equals, backslashes and Unicode.

Existing preset migrations retain their historical field sets. Adding ZMatrix
does not make an untouched old UC3D starter look edited, or recreate a previously
deleted starter. Storage schema remains version 1; starter seed version is 6.

## Intentional compatibility choices

| Donor detail | Decision |
| --- | --- |
| Foreground/fade alpha values are unused | Preserve imported metadata and report that these fields do not alter rendering. |
| Normal background alpha is a threshold | Map values below 128 to transparent and values at least 128 to opaque. Do not imply continuous opacity. |
| Special background and alpha transforms are unused | Report their inactive behavior rather than offer controls with no effect. |
| DPI reload scales cleanup counts, then rendering multiplies by font height | Keep cleanup counts in glyph units. Font raster resolution follows DPI without multiplying the cleanup distance twice. |
| Unbounded unsigned numeric fields | Clamp valid numeric values to documented finite bounds. Report invalid fields and DPI conversions; ordinary bound clamping does not produce a separate import note. |
| Empty phrase list reloads the default | Preserve the requested empty list. |
| Donor screensaver IPC has disconnected handlers and preview is a no-op | Implement independent Windows screensaver entrypoints. |
| RGB coefficient setters exist without an automatic foreground sender | Expose the functional gain/offset controls directly. No donor focus-dimming policy exists to reproduce. |
| Donor changes icon list styles and has incomplete failure restoration | Leave icon styles untouched. |
| GPL C++ source and separately permitted font | Use fresh source for this implementation and retain the font's distribution notice. |

## Windows screensaver

Keep `MatrixDesktop.scr` beside `MatrixDesktop.dll`, its runtime/dependency files,
the configurator and web assets. The `.scr` is an apphost for the same managed
application. `/s` runs fullscreen on top, retries foreground activation after
navigation and exits on input. `/c` opens the configurator.
`/p HWND` creates a child preview, follows the parent's client bounds and exits
when that parent disappears. The preview does not create wallpaper tray/startup
state or acquire global keyboard focus.

**Use for screensaver** saves the current draft to
`%LOCALAPPDATA%\MatrixDesktop\screensaver-profile.json` and explicitly selects the
published `.scr` for the current user. Registration backs up the previous choice.
**Restore previous screensaver** restores it, unless the owner has since chosen
a different screensaver outside MatrixDesktop. Normal startup and previews do
not register a screensaver or enable autostart.

For portable profiles and isolated verification, the process environment variable
`MATRIXDESKTOP_SCREENSAVER_PROFILE` can point to another argv-array JSON file.
The smoke tests use a synthetic file under ignored artifacts, without loading
personal media or changing the owner's saved screensaver profile.

## Verification

`tests/zmatrix-tests.mjs` checks deterministic simulation and literal raster
results. `tests/host-playback-tests.mjs` checks paused-time exclusion, including
pause/resume between rendered frames. `tests/zmatrix-browser.py` exercises actual
WebGL/WebGPU presentation, animation, pause/resume, resize and disposal.
Windows host tests additionally verify shell parenting and native resource use.
The normal gate retains existing Matrix/UC3D regressions and publishes the complete
application before runtime verification. Environment-dependent checks report
unavailable results explicitly.

Exact pixel parity with every historical GDI driver, unavailable fonts and every
Windows shell version is not claimed. Physical Looking Glass hardware remains
outside this feature's verification scope.
