# Vendoring: `MatrixDesktop/web/`

`MatrixDesktop/web/` is a locally modified copy of
[Rezmason/matrix](https://github.com/Rezmason/matrix). It is committed directly,
not maintained as a submodule. The upstream MIT notice is preserved in
[`MatrixDesktop/web/LICENSE`](MatrixDesktop/web/LICENSE).

## Provenance

Commit `927eb26` imported the flattened upstream tree. It did not record the
upstream commit SHA, so this repository has no recorded upstream merge base.
The import commit is a useful baseline for finding our changes, but it is not
an identified upstream fork point.

Subsequent changes include payload trimming (`bd24373`), click ripples
(`1d61ace`), renderer corrections, glyph intensity, and the UC3D scene and
renderer lifecycle work in version 1.1.0, followed by ZMatrix and wallpaper support
in version 1.2.0. Use history rather than assuming this
list names every commit:

```pwsh
git log --oneline -- MatrixDesktop/web
git diff --name-status 927eb26 HEAD -- MatrixDesktop/web
```

While reviewing an uncommitted refresh, omit `HEAD` from the second command
and also inspect `git ls-files --others --exclude-standard MatrixDesktop/web`.
The latter includes new modules that a tracked-file diff cannot show yet.

## Local change inventory

Paths below are relative to `MatrixDesktop/web/`. This is the version 1.2.0
inventory; regenerate the diff before every upstream refresh.

| Files | Local responsibility |
| --- | --- |
| `index.html` | Declares an empty icon, `<link rel="icon" href="data:," />`, so the browser does not request `/favicon.ico`, which `web/` does not ship. |
| `js/camera.js` | Generation-owned camera setup, cancellation and explicit track release. |
| `js/config.js` | Desktop defaults, accepted parameters, exact boolean parsing and UC3D/ZMatrix configuration. Query keys and the `version` and `font` names match only own table entries, so an inherited `Object` name such as `constructor` is ignored or falls back to the default. `camera=true` is dropped unless the effect is `mirror`, so only the mirror effect opens the webcam. When the Matrix intro cannot progress (`animationSpeed * fallSpeed <= 0`), `skipIntro=false` is overridden with a console warning. |
| `js/main.js` | Renderer selection and visible startup failures: a bootstrap, configuration or renderer error shows a notice, and the notice hides every canvas so it stays in view. Installs `rendererRecovery.js` as `window.matrixRendererLost`; backend loading and the WebGPU-to-REGL fallback are in `rendererLoader.js`. Plug me in removes the software-rendering notice and shows the canvas before the renderer loads, and acts on one click only. It also records the click in `sessionStorage` under `matrixSoftwareRenderingConsent`, with the startup query as the value, and the page reads that before the software rendering check, so a recovery reload or the host's renderer reload in the same session starts the renderer without the notice, while a fresh launch or a changed configuration warns again. When software rendering is detected, wallpaper, screensaver and screensaver preview (/p) hosts skip that notice, log a console warning and keep rendering, because nothing there can press the button: a wallpaper's opt-in pointer forwarding reaches only the canvas. |
| `js/regl/main.js`, `js/webgpu/main.js` | Desktop lifecycle, pause-aware time, click ripples, camera ownership and scene entry points. Both feed the shaders time from `shaderTime.js`, draw one still frame at `fps=0` as `once` does and keep it through a window resize, and use the palette effect when the effect name is not an own entry of their effect table. While the host pauses them they stop requesting animation frames, and on resume a `subscribeHostPlayback` listener, removed in cleanup, restarts the parked loop. On a lost WebGL context or WebGPU device they stop drawing, record the loss in `matrixRuntimeDiagnostics` and call `window.matrixRendererLost`. REGL also takes its wrapped `tick` uniform from `shaderTime.js`. WebGPU requests the adapter's full 2D texture limit, sizes the canvas inside it and keeps `ready` false after an uncaptured validation error. |
| `js/regl/main.js` (frame loop and start-up) | Runs its own `requestAnimationFrame` loop instead of `regl.frame`, with `regl.poll()` at the start of each frame and `gl.flush()` at the end, as `regl.frame` did. The vendored regl's stop passes `cancelAnimationFrame` its callback rather than the request id, so its next request stays pending after a cancel and a loop made again at that moment would run twice per frame. A lost context ends the loop. Once the still frame is shown, the resize handler stops setting the canvas size, because that would clear a frame that is never drawn again. When `createREGL` returns null (no WebGL context, or a required extension missing), the renderer throws `REGL could not start: <reason>` with the reason regl passed to `onDone`. |
| `js/regl/bloomPass.js`, `imagePass.js`, `mirrorPass.js`, `palettePass.js`, `rainPass.js`, `stripePass.js`, `utils.js` | Local renderer behavior and resource ownership. |
| `js/regl/bloomPass.js` (blur `width` and `height`) | Kept crossed as imported: the blur pass feeds the viewport height as `width` and the width as `height`, because `bloomPass.blur.frag.glsl` builds its size vector in the opposite order, so only the crossed pair gives a one-texel step on both axes. A v1.0.2 change that uncrossed them was reverted in `430463a`; the worked example is in the comment above the uniforms. Do not uncross them in a refresh. |
| `js/regl/lkgHelper.js`, `js/regl/quiltPass.js` | Looking Glass calibration, a 1024-view work budget and quilt resource ownership. |
| `js/webgpu/bloomPass.js`, `imagePass.js`, `mirrorPass.js`, `palettePass.js`, `rainPass.js`, `stripePass.js`, `utils.js` | Local renderer behavior, cached views and resource ownership. |
| `js/regl/imagePass.js`, `js/webgpu/imagePass.js` | With no `url`, or `url=`, `effect=image` uses the bundled `assets/sand.png`, a texture from the upstream import, instead of the remote Wikimedia image the imported code fetched, so it needs no network. A background that cannot load is replaced by a white 1x1 texture with a console warning and a `matrixRuntimeDiagnostics.errors` entry, so the rain draws uncoloured instead of the renderer stopping. |
| `js/regl/rainPass.js`, `js/regl/mirrorPass.js`, `js/webgpu/rainPass.js`, `js/webgpu/mirrorPass.js` | Click and touch stamps move back by the same shift when `shaderTime.js` moves the shader time origin forward. |
| `js/webgpu/utils.js` | `loadTexture` decodes an image again at a smaller size, aspect kept, when it exceeds the device's 2D texture limit. |
| `shaders/glsl/palettePass.frag.glsl`, `rainPass.effect.frag.glsl`, `stripePass.frag.glsl` | Glyph intensity and click-ripple behavior. |
| `shaders/wgsl/palettePass.wgsl`, `rainPass.wgsl`, `stripePass.wgsl` | Glyph intensity and click-ripple behavior. The palette and stripe dither hashes `uv + fract(time.seconds)` instead of `uv + time.seconds`; the shader time note below the tables explains why. |
| `lib/gl-matrix.min.js` | Minified replacement for the imported `lib/gl-matrix.js`. |

The following modules and shaders are local additions:

| Files | Purpose |
| --- | --- |
| `js/clickRipples.js` | Shared ripple state; `shiftTime` moves stored stamps back when shader time is rebased. |
| `js/canvasSize.js` | Cached canvas dimensions and resize observation; `fitInside` keeps the WebGPU Matrix canvas and the textures the WebGPU Matrix renderer loads within the device's 2D texture limit. |
| `js/rendererChoice.js` | Looking Glass routing through the WebGL quilt path. |
| `js/rendererLoader.js` | Loads the renderer backend for `main.js`. A WebGPU failure in the Matrix scene falls back to REGL on a fresh canvas; in UC3D and ZMatrix it is shown as an error instead. When the REGL path fails and a detached canvas also gets no WebGL context, the error shown is `rendererRecovery.js`'s restart notice, with the renderer's error as its cause. |
| `js/rendererRecovery.js` | Reloads the page after a lost WebGL context or WebGPU device, at most 3 times in 5 minutes (counted in `sessionStorage`), then shows a notice asking for a restart. The notice comes from the exported `unrecoverable()`, which `rendererLoader.js` also uses when the browser refuses a new WebGL context. |
| `js/resourceOwner.js` | Idempotent resource ownership. |
| `js/shaderTime.js` | Float32-safe shader time origin and frame counter for the Matrix renderers. A gap between frames of a minute or more, or of a second or more once shader time passes an hour, moves the origin forward only when `hostPlayback.js`'s unseen epoch moved during it, so a pause that keeps its frame on screen resumes on the same rain pattern; past the hard limit, six hours by default, the origin moves regardless. Each move sets shader time back to 600 s. With `skipIntro` off, `introSafeLimits` moves that restart point to a minute after the slowest column's intro ends, when that is later, and raises the soft and hard limits by the same amount; both renderers create their shader time with it. |
| `js/hostPlayback.js`, `js/hostPointer.js` | Native pause clocks and opt-in desktop pointer forwarding shared by the scenes. `hostPlayback.js` also exports `unseenEpoch()`, which counts the page's visibility changes until `pagehide`; the Matrix renderers pass it to `shaderTime.js`. |
| `js/renderFailure.js` | Persistent render-failure notice shared by UC3D and ZMatrix. |
| `js/webglProgram.js` | WebGL program builder shared by the UC3D and ZMatrix passes. |
| `js/glErrorCheck.js` | Throttled `gl.getError()` check shared by the REGL UC3D and ZMatrix passes. It reads on the first three frames, on any frame that allocates, and then once every `fps` frames, about once a second; an error still throws `<label> WebGL error <code>` and fails the scene visibly. |
| `js/zmatrix/settings.js`, `scene.js`, `surface.js`, `fonts.js`, `runtime.js` | Fresh ZMatrix feature implementation, persistent bytewise raster and native/browser resources. |
| `js/regl/zmatrixPass.js`, `js/webgpu/zmatrixPass.js` | Actual ZMatrix WebGL/WebGPU presentation. |
| `js/uc3d/audio.js`, `defaults.js`, `media.js`, `native.js`, `overlays.js`, `runtime.js`, `scene.js` | Shared UC3D behavior, local-media consumption and lifecycle. |
| `js/regl/uc3dPass.js`, `js/webgpu/uc3dPass.js` | UC3D backend implementations. |
| `shaders/glsl/uc3d.vert.glsl`, `uc3d.frag.glsl`, `uc3dComposite.frag.glsl` | UC3D WebGL glyph field and composite. |
| `shaders/wgsl/uc3d.wgsl`, `uc3dComposite.wgsl` | UC3D WebGPU glyph field and composite. |

The imported upstream renderers gave the shaders raw seconds since start (regl's
own `time`, and `(now - start) / 1000` on WebGPU) and, on REGL, regl's own frame
counter as `tick`. Both lose float32 precision on a wallpaper that runs for days.
`shaderTime.js`, the two renderer `main.js` files, `clickRipples.js` and the rain
and mirror passes carry the local fix. The WGSL dither in `palettePass.wgsl` and
`stripePass.wgsl` needed a change of its own: it hashed `uv + time.seconds`, and
at a 2880-pixel width float32 gave most neighbouring pixels the same hash input
once time passed about 1437 s, well before the six-hour hard limit. Both files
now hash `uv + fract(time.seconds)`, which stays below 2, so neighbouring pixels
keep distinct inputs at any shader time. Fed raw seconds since start, the
imported dither degraded within its first hour of running, so this change is one
to report upstream.

UC3D is newly written source informed by analysis of the owner's local donor
program. Donor binaries, recovered decompiler output and donor media are not
part of this tree. See [UC3D reconstruction limits](docs/UC3D.md#reconstruction-and-verification).

The ZMatrix functional model was reviewed at
[`bigfnj/zmatrix-modernized@7eefa2919ef2ac976fd8a566176e20a6541f996d`](https://github.com/bigfnj/zmatrix-modernized/tree/7eefa2919ef2ac976fd8a566176e20a6541f996d).
Its C++ code is not imported. The new C#/JavaScript implementation remains under
this repository's MIT license. The separately distributed font is an exception
to the upstream font omissions below:

| Asset | Attribution and permission |
| --- | --- |
| `web/assets/zmatrix/matrix-code.ttf` | Matrix Code Font by e-RBi. The original notice calls it freeware and permits distribution. The notice ships as `matrix-code.LICENSE` beside the font. |

```text
matrix-code.ttf sha256 f0041b1e07195631038a6681f72116536518c2b0170554bdcc1c6c1ea9c405e6
```

The native app registers this font privately for atlas creation and removes that
registration afterward. It does not install a system font. Browser fallback
loads the same bundled asset through FontFace and reports its GDI approximation.

## Deliberate omissions

The runtime copy omits upstream development content, including `playdate/`,
`svg sources/`, `screenshot.png`, the unminified regl build, original TTF files,
and upstream notes. The deletion rows in the baseline diff record the exact
list. Do not merge those files back merely because upstream changes them.

The project's [`Content` glob](MatrixDesktop/MatrixDesktop.csproj) and root
`.gitignore` guard non-runtime paths. In particular, do not put documentation
with a `.md` or `.txt` extension under `web/`: it is excluded from both the
repository and publish output. The extensionless `web/LICENSE` ships normally.

## Third-party runtime libraries

| File | Library |
| --- | --- |
| `lib/regl.min.js` | regl, production build. |
| `lib/gl-matrix.min.js` | gl-matrix 3.4.0, minified UMD build. |
| `lib/gpu-buffer.js` | WGSL structure layout helper imported with the web project. |
| `lib/holoplaycore.module.js` | Looking Glass HoloPlay Core imported with the web project. |

The gl-matrix replacement in version 1.0.4 came from
`https://cdn.jsdelivr.net/npm/gl-matrix@3.4.0/dist/gl-matrix-min.js`:

```text
sha256 c45c1001c99a73ea8b9fb08f1d77759be3191c1e3daefea1213c99e9859693ff
```

The runtime imports this renamed file. The gate checks referenced asset paths,
so a stale reference to the removed unminified filename fails verification.

## Upstream refresh procedure

1. Identify an upstream revision to compare. Establishing the original fork
   point requires content matching, because the import has no upstream history.
   Use an unchanged file such as `js/colorToRGB.js` as a probe after confirming
   it is unchanged in the baseline diff; `index.html` no longer qualifies, since
   it now declares a local empty icon. Do not assume a previously untouched
   renderer module is still untouched.
2. Record any confirmed upstream SHA here. Until then, compare the flattened
   import, the desired upstream tree and the local tree manually. Upstream paths
   map to `MatrixDesktop/web/` in this repository.
3. Review every modified or added path in the inventory. Preserve local modules
   and behavior; keep deliberate development-file omissions. Update this inventory
   in the same change.
4. Run the verification gate, then the actual published applications. For a
   renderer change, include both backend probes described in
   [SMOKE_TEST_PLAN.md](SMOKE_TEST_PLAN.md).

```pwsh
powershell.exe -NoProfile -File tests\run-gate.ps1
pwsh -NoProfile -File tests\run-gate.ps1 -Tier1Only
```

Tier 1 checks module syntax and referenced shader/library/asset paths, as well
as native and browser regressions. A green tier 1 does not replace real-app
verification or physical Looking Glass, webcam and monitor checks.
