# Vendoring: `MatrixDesktop/web/`

`MatrixDesktop/web/` is a **vendored fork** of the Rezmason/matrix web project, not a
pristine copy and not a submodule. This file records what that means in practice, because
the repository itself does not record it anywhere else.

## Upstream

| | |
| --- | --- |
| Project | [Rezmason/matrix](https://github.com/Rezmason/matrix) |
| License | MIT, copyright (c) 2018 Rezmason — text preserved verbatim at `MatrixDesktop/web/LICENSE` |
| Vendoring mechanism | none; 69 plain tracked files committed directly into this repository |
| Submodule | no — there is no `.gitmodules` in this repo |
| Upstream git remote | no — `git remote -v` lists only `origin https://github.com/bigfnj/MatrixDesktop` |
| Recorded fork point | **none** — see "The missing fork point" below |

## How the copy got here

The whole tree arrived in one squashed drop:

| Commit | Date | What it did to `web/` |
| --- | --- | --- |
| `927eb26` | 2026-05-01 | `Initial commit` — imported the entire upstream tree, already flattened, with no upstream history |
| `bd24373` | 2026-05-04 | `Harden WebView2 startup and trim payload` — WebView2 hardening plus deletion of non-runtime upstream content |
| `1d61ace` | 2026-05-05 | `Add click ripples to rain effects` — new `clickRipples` / `clickRippleShape` feature |

Those three are the **only** commits in the repository's entire history that have ever
touched a path under `MatrixDesktop/web/`. Reproduce with:

```pwsh
git log --oneline -- MatrixDesktop/web
```

## The missing fork point

`927eb26` imported the files but not the upstream commit they came from. There is no
upstream remote, no submodule pin, no vendored `UPSTREAM_SHA`, and no tag. So the question
"what did upstream look like when we forked?" currently has **no answer recorded anywhere**,
and a real `git rebase` onto newer upstream is impossible: there is no merge base.

Until a fork point is established, an upstream refresh is a manual three-way merge by hand,
file by file, against the 16 files listed below. Establishing one is cheap and worth doing
once — see "Re-establishing a fork point".

## Locally modified files (25)

Every other file under `MatrixDesktop/web/` is byte-identical to what `927eb26` imported.
These 25 are not, and each one is a hand-merge cost on any upstream update.

**This table went stale once already.** It said 16 files from `v1.0.1` until `v1.0.4`, while
the `v1.0.2` renderer-defect pass and the `v1.0.3` audit had quietly taken it to 22. Treat the
regeneration command below as the source of truth and this table as a snapshot; if you change
anything under `web/`, regenerate rather than appending by hand.

| File | Changed by |
| --- | --- |
| `js/camera.js` | `bd24373` |
| `js/clickRipples.js` | `1d61ace`, `f9bbba2` (**added locally**, does not exist upstream) |
| `js/config.js` | `bd24373`, `1d61ace`, `v1.0.4` |
| `js/main.js` | `bd24373`, `6d91f2b` |
| `js/regl/bloomPass.js` | `bd24373`, `f9bbba2`, `430463a` |
| `js/regl/lkgHelper.js` | `f9bbba2` |
| `js/regl/main.js` | `bd24373`, `1d61ace`, `f9bbba2`, `v1.0.4` |
| `js/regl/mirrorPass.js` | `bd24373`, `f9bbba2` |
| `js/regl/palettePass.js` | `v1.0.4` |
| `js/regl/rainPass.js` | `1d61ace`, `f9bbba2` |
| `js/regl/stripePass.js` | `v1.0.4` |
| `js/regl/utils.js` | `bd24373` |
| `js/webgpu/bloomPass.js` | `bd24373` |
| `js/webgpu/main.js` | `bd24373`, `1d61ace`, `f9bbba2`, `v1.0.4` |
| `js/webgpu/mirrorPass.js` | `bd24373`, `f9bbba2` |
| `js/webgpu/palettePass.js` | `v1.0.4` |
| `js/webgpu/rainPass.js` | `1d61ace`, `f9bbba2`, `6d91f2b` |
| `js/webgpu/stripePass.js` | `v1.0.4` |
| `js/webgpu/utils.js` | `bd24373` |
| `shaders/glsl/palettePass.frag.glsl` | `v1.0.4` |
| `shaders/glsl/rainPass.effect.frag.glsl` | `1d61ace`, `f9bbba2` |
| `shaders/glsl/stripePass.frag.glsl` | `v1.0.4` |
| `shaders/wgsl/palettePass.wgsl` | `v1.0.4` |
| `shaders/wgsl/rainPass.wgsl` | `1d61ace`, `f9bbba2` |
| `shaders/wgsl/stripePass.wgsl` | `v1.0.4` |

Paths are relative to `MatrixDesktop/web/`. The commits beyond the original three:

| Commit | What it did to `web/` |
| --- | --- |
| `f9bbba2` | `v1.0.2` renderer defects: MD-04, MD-19, MD-25, MD-43 to MD-48 |
| `430463a` | `v1.0.3` reverted the MD-44 bloom "fix", which was itself the regression |
| `6d91f2b` | `v1.0.3` operand ordering on the launch path, cached WebGPU texture views |
| `v1.0.4` | implemented `glyphIntensity`, previously parsed and documented but read by no shader |

Regenerate the list at any time. Any file whose log shows a commit other than `927eb26`
is locally modified:

```pwsh
git diff --name-status 927eb26 HEAD -- MatrixDesktop/web
```

`M` and `A` rows are the live local delta. `D` rows are upstream files deleted by
`bd24373` and are not a merge cost — see below.

## Files deleted from the vendored copy

`bd24373` removed non-runtime upstream content: `web/playdate/**`, `web/svg sources/**`,
`web/screenshot.png` (3.5 MB), `web/lib/regl.js` (the unminified build; the runtime loads
`regl.min.js`), and the upstream docs and notes (`README.md`, `TODO.txt`, `glyph order.txt`,
`prettier_command.txt`, `webgpu_notes.txt`, `assets/msdf_command.txt`).

These are deletions, not modifications. On an upstream refresh they should simply be deleted
again rather than merged, and mostly they cannot come back by accident: `.gitignore` lines
43-49 already ignore `MatrixDesktop/web/playdate/`, `svg sources/`, `screenshot.png`,
`lib/regl.js`, and every `.md` and `.txt` beneath `MatrixDesktop/web/`. Re-importing those
paths would leave them untracked rather than committed.

`MatrixDesktop/MatrixDesktop.csproj` also still carries `Exclude` clauses for these paths in
its `web\**\*` Content glob. Those clauses now match nothing on disk and are retained only
as a second guard on the build side. The clause for `web\msdfgen\**\*` never matched
anything at all — that upstream folder was not part of the `927eb26` import.

Note that `MatrixDesktop/web/LICENSE` has no file extension, so it is not caught by the
`.md`/`.txt` exclusions and does ship into the payload. That is correct and required.

## Third-party builds under `web/lib/`

`lib/` holds upstream's vendored dependencies, which are themselves third-party. Where a
library ships both a development and a production build, this fork takes the production one,
matching what `bd24373` already did for regl:

| File | Library | Build | Bytes |
| --- | --- | --- | --- |
| `lib/regl.min.js` | [regl](https://github.com/regl-project/regl) | minified | 87,062 |
| `lib/gl-matrix.min.js` | [gl-matrix](https://github.com/toji/gl-matrix) 3.4.0 | minified, UMD | 52,494 |
| `lib/gpu-buffer.js` | upstream's own WGSL struct layout helper | source | 8,256 |
| `lib/holoplaycore.module.js` | Looking Glass HoloPlay Core | as shipped | 23,810 |

`gl-matrix` was swapped from the 214,503-byte unminified build in `v1.0.4`. Provenance, so
nobody has to trust that the swap was like for like:

```
source  https://cdn.jsdelivr.net/npm/gl-matrix@3.4.0/dist/gl-matrix-min.js
sha256  c45c1001c99a73ea8b9fb08f1d77759be3191c1e3daefea1213c99e9859693ff
```

The replaced file was verified to be upstream's own `dist/gl-matrix.js` for the same version:
byte-identical after newline normalisation, sha256
`07855fa64096dbc30c3cf9088be433f29c8e344665c87b66c7463443d38c15a7`. The minified build was
then checked to compute bit-identical results across 400 randomised inputs for each of the
ten APIs this project calls (`mat4.create`, `ortho`, `orthoZO`, `perspective`,
`perspectiveZO`, `rotateX`, `rotateY`, `scale`, `translate`, `vec3.fromValues`), including
both infinite-far-plane branches and the composed volumetric camera transform.

The file is renamed, not overwritten, so `lib/gl-matrix.js` no longer exists. Both call sites
(`js/regl/main.js`, `js/webgpu/main.js`) were updated. The gate's "every referenced web asset
resolves" check covers this: reverting one call site to the old name fails it.

## Re-establishing a fork point

Do this once and the next upstream update becomes a normal merge instead of an archaeology
exercise:

1. Add upstream as a read-only remote and fetch it.

   ```pwsh
   git remote add upstream https://github.com/Rezmason/matrix.git
   git fetch upstream
   ```

2. Identify which upstream commit `927eb26` corresponds to. The import is flattened, so
   match on content rather than history: compare an unmodified vendored file against
   upstream revisions of it. `MatrixDesktop/web/index.html` and
   `MatrixDesktop/web/js/regl/imagePass.js` are both untouched locally, which makes them
   good probes.

   Pick a probe from the regeneration command above, never from memory. This paragraph used
   to name `js/regl/palettePass.js`, which stopped being untouched in `v1.0.4`; a probe that
   is silently locally-modified will never match any upstream revision and the search just
   returns nothing, which reads like "no fork point exists" rather than "wrong probe".

   ```pwsh
   git rev-list upstream/master -- index.html | ForEach-Object {
     $u = git show "${_}:index.html"
     $v = Get-Content MatrixDesktop/web/index.html -Raw
     if ($u -join "`n" -eq $v) { "candidate fork point: $_" }
   }
   ```

3. Record the answer. Put the SHA in this file, in a section titled "Fork point", and stop
   guessing. That single line is the whole point of the exercise.

## Diffing against upstream once a fork point exists

Upstream's tree root maps to `MatrixDesktop/web/`, so paths need rewriting. With `<FORK>`
as the recorded fork-point SHA and `<TARGET>` as the upstream revision being evaluated:

```pwsh
# What upstream changed since the fork, restricted to files we actually ship.
git diff <FORK> <TARGET> -- js shaders lib assets index.html

# What we changed, in the same shape, for comparison against the above.
git diff 927eb26 HEAD -- MatrixDesktop/web
```

A file that appears in **both** diffs is a genuine conflict needing a hand merge. A file
that appears only in the upstream diff can be copied straight in. Cross-check against the
16-file table above before assuming a file is clean.

After any refresh, re-run the verification gate before trusting the result:

```pwsh
pwsh -File tests\run-gate.ps1 -Tier1Only
```

Tier 1 parses every first-party web module with `node --check` and asserts that every
shader, asset and lib path referenced from JS or HTML resolves on disk, which is exactly
the class of breakage a sloppy vendor drop introduces.

## Rules for editing `MatrixDesktop/web/`

- Prefer additive, separate files (as `js/clickRipples.js` is) over edits to upstream files.
  Every edited upstream file is a permanent merge tax; a new file is free.
- Add any new local file to the table above in the same commit.
- Do not add `.md` or `.txt` files under `MatrixDesktop/web/`. Both the repository
  `.gitignore` and the `Exclude` list on the csproj `web\**\*` Content glob drop them, so
  the file would be silently absent from every build and publish output.
