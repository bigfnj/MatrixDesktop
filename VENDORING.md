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

## Locally modified files (16)

Every other file under `MatrixDesktop/web/` is byte-identical to what `927eb26` imported.
These 16 are not, and each one is a hand-merge cost on any upstream update:

| File | Changed by |
| --- | --- |
| `MatrixDesktop/web/js/camera.js` | `bd24373` |
| `MatrixDesktop/web/js/clickRipples.js` | `1d61ace` (**added locally**, does not exist upstream) |
| `MatrixDesktop/web/js/config.js` | `bd24373`, `1d61ace` |
| `MatrixDesktop/web/js/main.js` | `bd24373` |
| `MatrixDesktop/web/js/regl/bloomPass.js` | `bd24373` |
| `MatrixDesktop/web/js/regl/main.js` | `bd24373`, `1d61ace` |
| `MatrixDesktop/web/js/regl/mirrorPass.js` | `bd24373` |
| `MatrixDesktop/web/js/regl/rainPass.js` | `1d61ace` |
| `MatrixDesktop/web/js/regl/utils.js` | `bd24373` |
| `MatrixDesktop/web/js/webgpu/bloomPass.js` | `bd24373` |
| `MatrixDesktop/web/js/webgpu/main.js` | `bd24373`, `1d61ace` |
| `MatrixDesktop/web/js/webgpu/mirrorPass.js` | `bd24373` |
| `MatrixDesktop/web/js/webgpu/rainPass.js` | `1d61ace` |
| `MatrixDesktop/web/js/webgpu/utils.js` | `bd24373` |
| `MatrixDesktop/web/shaders/glsl/rainPass.effect.frag.glsl` | `1d61ace` |
| `MatrixDesktop/web/shaders/wgsl/rainPass.wgsl` | `1d61ace` |

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
   `MatrixDesktop/web/js/regl/palettePass.js` are both untouched locally, which makes them
   good probes.

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
