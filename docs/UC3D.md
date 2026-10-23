# UC3D scene

Choose **UC3D** in the configurator's Theme section, or run:

```cmd
MatrixDesktop.exe --scene uc3d
```

The UC3D defaults use the owner's installed screensaver settings captured on
2026-10-23: purple code (`#8000FF`), pink highlights (`#FF80FF`), black background,
3D rotation, random forward travel and sideways motion. Pictures, clock, sound,
mouse control and desktop background start off. Explicit arguments override the
profile, including values that equal the ordinary Matrix defaults.

| Installed setting | UC3D default |
| --- | --- |
| 500 lines, glyph size 12 | `uc3dAmount=500`, `uc3dGlyphSize=12`; independent stream count and world half-size 0.12 |
| Fall speed 6 | `uc3dFallSpeed=6`, randomized speeds 2 to 5.5 world units/second |
| Forward speed 6 | `uc3dForwardSpeed=6`, target -5.4 world units/second; random travel is enabled, so this constant applies after random travel is disabled |
| Picture delay 60, original episode timing | `uc3dPictureDelay=60`, `pictureDuration=30`; first picture starts after 20 seconds |
| Green-only pictures disabled | `pictureMonochrome=false` |

New **UC3D Trilogy** starter presets and untouched copies of the old starter
use this profile. Edited presets and deleted starters are preserved. Pictures
need a local media folder. The profile is a fixed
snapshot of these settings; startup does not read or change the screensaver's
registry configuration.

For personal pictures, for example:

```cmd
MatrixDesktop.exe --windowed --no-exit-on-any-key --scene uc3d --volumetric true --mouseRotation true --showPictures true --pictureDirectory "C:\Pictures\Matrix" --pictureSource custom --pictureMonochrome false --pictureDepth 0.5 --showClock true
```

## Controls

**Original controls** provides independent sliders and numeric editors in the
original units. More/Less is 100 to 1500 streams; glyph size is 10 to 40. Their
values do not depend on the viewport, aspect ratio or 2D/3D choice. Streams enter
over ten seconds, and each trail grows from one glyph to its chosen length.
Visible coverage also depends on size, perspective, fading and the current pose.
The fall slider's zero still falls. The forward slider's neutral point is 60.

**Legacy controls** preserves the earlier columns/density/speed mapping for saved
customized presets. Explicit generic layout or speed arguments select that mode;
native-unit arguments select Original controls. An explicit `uc3dControls` wins.
Inactive controls are disabled in the configurator and omitted from commands.
Font, color, intensity, resolution, frame-rate and optional bloom remain shared.
Original controls starts with bloom disabled, matching the native rain composition.

- Enable **Mouse-controlled rotation** in 3D: move the pointer to rotate,
  right-click to stop, left-click to reset, and turn the wheel to roll. After
  20 seconds without movement, automatic motion resumes. Original controls defers
  a new picture while manual control is active; an existing formation continues.
- Enable **Form pictures from code**, choose a personal picture folder, and
  select a source. Original **Picture size: Large / Small** uses the native
  sampling divisor, so higher values make the image smaller. Legacy scale fits
  the image while preserving aspect ratio. Monochrome uses the
  code color; disabling it preserves picture colors. Relief depth changes
  glyph depth in 3D. Captions use imported titles or personal filenames.
- Enable the clock for local 12- or 24-hour time. Clock size is independent
  of glyph size.
- **Desktop snapshot background** captures the desktop once before startup.
  It does not create live wallpaper. The snapshot stays in memory and is not
  written into the staged web assets.
- Sound starts off. Choose code sizzle or ambient music. A local music file
  replaces synthesized ambient music. The preview starts muted; its Test sound
  button is an explicit playback action. If autoplay is blocked, use Enable
  sound. Playback errors offer a retry.

Personal pictures support JPEG, PNG, GIF and BMP. The library allows up to 256
selection entries, with a 32 MiB limit per file. Personal folders are not walked
recursively. Local music supports MP3, WAV, OGG and M4A with the same file limit.
Unavailable folders, missing film groups and decoding failures are reported.
Pictures are decoded at a maximum dimension of 1024 pixels for glyph sampling;
Original controls caps personal pictures at 300 pixels, as the native loader did.
The slideshow retains current/next decoded pictures rather than the entire
library. GIF input is sampled as a picture, not played as a movie.

The full flag list and allowed ranges are in the configurator and embedded
argument guide. Useful defaults are `scene=matrix`, `showPictures=false`,
`showClock=false`, `desktopBackground=false`, `sound=off`, `donorGlyphs=false`
and `mouseRotation=false`. Selecting UC3D alone does not opt into desktop
capture, pictures or sound. `fps=0` and `once=true` draw one frame. Original controls
uses a ten-second simulation snapshot at the selected animation speed for that
frame; at speed 1 the startup ramp is complete.
Original pictures form through newly spawned streams, pan across the sampled
image and retain those streams until they fall clear. The next image and selected
gap begin after that clearance, so total time between pictures exceeds the forming
duration and configured gap.

## Optional original donor media

The application package contains no donor binary, movie pictures, music,
character atlas or decompiled source. A local media importer can recover your
own version 3.4 media from an unpacked Matrix module. The importer uses Python's
standard library and validates the supported source identity before writing.
It supports the unpacked donor version 3.4 module only, limits input to 64 MiB,
and refuses to overwrite an existing output directory.

Preserve the original installer/module. Use UPX on a working copy to produce an
unpacked module, then choose a new private output directory:

```cmd
upx -d -o "C:\Private\MatrixReloaded.unpacked.dll" "C:\Private\MatrixReloaded.uc3d"
python tools\import-uc3d-media.py "C:\Private\MatrixReloaded.unpacked.dll" --output "C:\Private\UC3D-media"
MatrixDesktop.exe --scene uc3d --volumetric true --donorDirectory "C:\Private\UC3D-media" --donorGlyphs true --showPictures true
```

The importer recovers captions and film membership from the supplied module.
Its manifest retains repeated selections and the original overlapping Reloaded
and Revolutions groups. The original strip contains 96 tiles, but the native
renderer used the first 79; the imported atlas is cropped accordingly.

Select that output in **Local donor media folder**. **Use local donor glyphs**
selects the imported character atlas. Code-sizzle mode uses the imported sound
when available. These options also work with picture display disabled. Custom
music remains an explicit separate file selection.

Without a manifest, the donor folder's direct images and optional `matrix`,
`reloaded` and `revolutions` subfolders provide simple film groups. Personal
pictures belong to `custom`; `all` includes every available selection. A
manifest preserves repeated native selections and can assign more than one
film group to an image. An empty selected group shows a warning instead of
silently substituting a different film.

Only selected catalog entries are granted to the native renderer. Files stay
in their chosen folders; media is not copied into the staged runtime web tree.
The bridge exposes exact resource URLs, not a filesystem browser. Folder/file
links are unsupported. Keep the imported library and any native-analysis
exports outside a public checkout.

## Reconstruction and verification

This is fresh renderer source informed by the native program and its documented
behavior. Native camera velocity ranges, mouse timeout, stream length, tail
fade, shading range, world wrap and luminance relief informed the implementation.
Original controls uses a shared CPU stream simulation and instanced glyphs on
both renderers. It reconstructs the 45-degree view, source-alpha composition,
distance and tail fades, cell-sized glyph movement, growing trails, integrated
and eased travel, automatic camera rest/move phases and mouse rotation. The CRT
random generator is reproduced, but random call order and stream scheduling
are modern implementations, so identical random frames are not claimed.

Legacy controls retains the earlier compatibility mapping:

| Control | UC3D interpretation |
| --- | --- |
| Columns | Glyph half-size is `12 / baseColumns`, giving native half-size `0.15` at 80 base columns. Portrait windows adjust the base count for aspect ratio; the grid uses at least four base columns. |
| Fall speed | `abs(fallSpeed) * 40 / 0.3` supplies the native-style fall range; default `0.3` yields speeds from 2 to 8.9 native units/second. A negative value reverses direction. |
| Forward speed | With random travel off, forward speed maps to `forwardSpeed * 8` native units/second, scaled by animation speed. Random travel uses native-style target ranges instead. |
| Raindrop length | `0.75` retains the native-style 10 to 39 glyph stream lengths and eight-glyph tail fade; other values scale the length. |
| Picture relief | Sample luminance multiplied by `3 * pictureDepth` supplies the 3D offset; zero keeps the picture flat. |

These legacy mappings are implemented in the shared scene and both shaders.
Picture assembly, glyph layout and ambient synthesis remain reconstructed
implementations; a byte-identical native rebuild is not claimed. Original-mode
stream count stays independent of size and viewport. Legacy count still scales
with aspect ratio and columns/density. The bundled font remains
the default; choose a local donor atlas for the original character set.

Both WebGL and WebGPU have a UC3D implementation. Browser tests assert the actual
backend, picture colors and relief, motion, captions and teardown. The ordinary
Matrix scene remains the default. Its Holoplay mode requires real Looking Glass
hardware and routes through WebGL. UC3D and WebGPU have no quilt path. Missing
device calibration is reported rather than replaced with a fictional device.

If WebGPU is unavailable, the scene reports that it is using WebGL. Once a
WebGPU backend starts initialization, shader/device failures remain visible
rather than being hidden by a second backend. Initialization failures show
an error notice and console error. An initialized runtime exposes
`window.matrixRuntimeDiagnostics` (also `window.__matrixRuntime`) with the
actual backend, readiness, frame count, picture/audio state, warnings, errors
and disposal state. Original mode also exposes actual active/target stream counts,
glyph instance count, motion and `inspect()` stream/camera samples. `ready` becomes
true only after drawing a frame. Following a long sleep or lock, original mode
advances at most one simulation second on a resumed frame and drops the gap.

Run `tests\run-gate.ps1` for the build, native regression harness, renderer
lifecycle tests, importer tests, WebGL smoke and actual application smoke.
Hardware WebGPU browser coverage is available with
`python tests\uc3d-browser.py --renderer both --channel msedge`; a missing
adapter is an unverified result. `tests\native-uc3d-smoke.py` checks the published
WebView2 runtime and configurator, including local-media grant boundaries and
the preview bootstrap. The snapshot probe uses only a synthetic desktop cover.
The [calibration record](UC3D-CALIBRATION.md) documents the original-runtime
comparison, settings matrix and visual limits. Run
`python tests\uc3d-calibration.py --channel msedge` for the original-unit cases
on both backends, using bundled glyphs. See [smoke and regression commands](../SMOKE_TEST_PLAN.md) for prerequisites,
artifact locations and physical hardware coverage limits.
