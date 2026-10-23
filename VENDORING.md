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
renderer lifecycle work in version 1.1.0. Use history rather than assuming this
list names every commit:

```pwsh
git log --oneline -- MatrixDesktop/web
git diff --name-status 927eb26 HEAD -- MatrixDesktop/web
```

While reviewing an uncommitted refresh, omit `HEAD` from the second command
and also inspect `git ls-files --others --exclude-standard MatrixDesktop/web`.
The latter includes new modules that a tracked-file diff cannot show yet.

## Local change inventory

Paths below are relative to `MatrixDesktop/web/`. This is the version 1.1.0
inventory; regenerate the diff before every upstream refresh.

| Files | Local responsibility |
| --- | --- |
| `js/camera.js` | Camera setup and explicit track release. |
| `js/config.js` | Desktop defaults, accepted parameters, exact boolean parsing and UC3D configuration. |
| `js/main.js` | Renderer selection, compatibility fallback and visible initialization failures. |
| `js/regl/main.js`, `js/webgpu/main.js` | Desktop lifecycle, click ripples, camera ownership and the UC3D entry point. |
| `js/regl/bloomPass.js`, `imagePass.js`, `mirrorPass.js`, `palettePass.js`, `rainPass.js`, `stripePass.js`, `utils.js` | Local renderer behavior and resource ownership. |
| `js/regl/lkgHelper.js`, `js/regl/quiltPass.js` | Looking Glass calibration and quilt resource ownership. |
| `js/webgpu/bloomPass.js`, `imagePass.js`, `mirrorPass.js`, `palettePass.js`, `rainPass.js`, `stripePass.js`, `utils.js` | Local renderer behavior, cached views and resource ownership. |
| `shaders/glsl/palettePass.frag.glsl`, `rainPass.effect.frag.glsl`, `stripePass.frag.glsl` | Glyph intensity and click-ripple behavior. |
| `shaders/wgsl/palettePass.wgsl`, `rainPass.wgsl`, `stripePass.wgsl` | Glyph intensity and click-ripple behavior. |
| `lib/gl-matrix.min.js` | Minified replacement for the imported `lib/gl-matrix.js`. |

The following modules and shaders are local additions:

| Files | Purpose |
| --- | --- |
| `js/clickRipples.js` | Shared ripple state. |
| `js/canvasSize.js` | Cached canvas dimensions and resize observation. |
| `js/rendererChoice.js` | Looking Glass routing through the WebGL quilt path. |
| `js/resourceOwner.js` | Idempotent resource ownership. |
| `js/uc3d/audio.js`, `media.js`, `overlays.js`, `runtime.js`, `scene.js` | Shared UC3D behavior, local-media consumption and lifecycle. |
| `js/regl/uc3dPass.js`, `js/webgpu/uc3dPass.js` | UC3D backend implementations. |
| `shaders/glsl/uc3d.vert.glsl`, `uc3d.frag.glsl`, `uc3dComposite.frag.glsl` | UC3D WebGL glyph field and composite. |
| `shaders/wgsl/uc3d.wgsl`, `uc3dComposite.wgsl` | UC3D WebGPU glyph field and composite. |

UC3D is newly written source informed by analysis of the owner's local donor
program. Donor binaries, recovered decompiler output and donor media are not
part of this tree. See [UC3D reconstruction limits](docs/UC3D.md#reconstruction-and-verification).

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
   Use an unchanged file such as `index.html` as a probe after confirming it is
   unchanged in the baseline diff. Do not assume a previously untouched renderer
   module is still untouched.
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
