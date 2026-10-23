# UC3D scene

Choose **UC3D** in the configurator's Theme section, or run:

```cmd
MatrixDesktop.exe --scene uc3d --volumetric true
```

The **UC3D Trilogy** starter preset selects 3D motion, donor code/highlight
colors, random travel, a 60-second picture gap and a 30-second picture episode.
Pictures need a local media folder. A new preset is added once for existing
users without restoring deleted starters or replacing edited presets.

For personal pictures, for example:

```cmd
MatrixDesktop.exe --windowed --no-exit-on-any-key --scene uc3d --volumetric true --mouseRotation true --showPictures true --pictureDirectory "C:\Pictures\Matrix" --pictureSource custom --pictureMonochrome false --pictureDepth 0.5 --showClock true
```

## Controls

Use the existing font, columns, density, fall/forward speed, color, intensity,
resolution, frame-rate and bloom controls alongside the UC3D section. Controls
that apply only to the Matrix scene are disabled while UC3D is selected.

- Enable **Mouse-controlled rotation** in 3D: move the pointer to rotate,
  right-click to stop, left-click to reset, and turn the wheel to roll. After
  20 seconds without movement, automatic motion resumes. Pictures fade away
  during interaction.
- Enable **Form pictures from code**, choose a personal picture folder, and
  select a source. Picture scale preserves aspect ratio. Monochrome uses the
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
the slideshow retains current/next decoded pictures rather than the entire
library. GIF input is sampled as a picture, not played as a movie.

The full flag list and allowed ranges are in the configurator and embedded
argument guide. Useful defaults are `scene=matrix`, `showPictures=false`,
`showClock=false`, `desktopBackground=false`, `sound=off`, `donorGlyphs=false`
and `mouseRotation=false`. Selecting UC3D alone does not opt into desktop
capture, pictures or sound. `fps=0` and `once=true` draw one frame.

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
Existing MatrixDesktop speed/size controls use this compatibility mapping:

| Control | UC3D interpretation |
| --- | --- |
| Columns | Glyph half-size is `12 / baseColumns`, giving native half-size `0.15` at 80 base columns. Portrait windows adjust the base count for aspect ratio; the grid uses at least four base columns. |
| Fall speed | `abs(fallSpeed) * 40 / 0.3` supplies the native-style fall range; default `0.3` yields speeds from 2 to 8.9 native units/second. A negative value reverses direction. |
| Forward speed | With random travel off, forward speed maps to `forwardSpeed * 8` native units/second, scaled by animation speed. Random travel uses native-style target ranges instead. |
| Raindrop length | `0.75` retains the native-style 10 to 39 glyph stream lengths and eight-glyph tail fade; other values scale the length. |
| Picture relief | Sample luminance multiplied by `3 * pictureDepth` supplies the 3D offset; zero keeps the picture flat. |

These mappings are implemented in the shared scene and both shaders. Picture assembly,
the glyph layout and ambient synthesis are modern implementations; a byte-
identical native rebuild and exact visual equivalence are not claimed. The
native random-number sequence and fixed stream population are not reproduced.

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
and disposal state. `ready` becomes true only after drawing a frame.

Run `tests\run-gate.ps1` for the build, native regression harness, renderer
lifecycle tests, importer tests, WebGL smoke and actual application smoke.
Hardware WebGPU browser coverage is available with
`python tests\uc3d-browser.py --renderer both --channel msedge`; a missing
adapter is an unverified result. `tests\native-uc3d-smoke.py` checks the published
WebView2 runtime and configurator, including local-media grant boundaries and
the preview bootstrap. The snapshot probe uses only a synthetic desktop cover.
See [smoke and regression commands](../SMOKE_TEST_PLAN.md) for prerequisites,
artifact locations and physical hardware coverage limits.
