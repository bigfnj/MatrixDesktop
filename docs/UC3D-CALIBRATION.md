# UC3D control and movement calibration

## Goal

Reproduce the original screensaver's control meanings and rain/movement behavior,
then compare the installed original with both MatrixDesktop renderers at matching
settings. A configured stream count alone is not proof of matching visible coverage.

## Implementation plan

1. Add an Original controls mode with independent native-unit sliders: amount
   100 to 1500, glyph size 10 to 40, fall 0 to 150, forward 0 to 120 (60 neutral),
   picture size divisor 100 to 300, picture gap 0 to 120 seconds, and clock size
   50 to 170. Keep a compatibility mode for edited older presets and CLI commands.
   Migrate only the exact untouched starter. Preserve original stored custom values.
2. Implement bounded CPU stream state shared by WebGL and WebGPU: independent
   population and size, a ten-second startup ramp, 10 to 39 character strings,
   growing trails, cell-sized fall steps, respawning, integrated travel and wrap.
   Use instanced glyph geometry to avoid uploading six copies of every glyph state.
3. Reproduce the native 45-degree perspective and camera view, native 2D sizing,
   distance/tail alpha, far-to-near source-alpha composition, and distinct leader
   colors. Correct sideways/forward easing and integration, automatic rest/move
   phases, neutral returns and mouse control.
4. Build a private original-process comparison harness. Run the unchanged installed
   screensaver in an owned child window, supply test settings inside that process,
   capture only its OpenGL framebuffer, and record actual settings, stream counts,
   elapsed simulation time and movement. Do not modify the owner's registry or
   original binaries. Keep donor media, instrumented runtime data and captures private.
5. Compare original, WebGL and WebGPU with the same viewport, elapsed time and local
   donor atlas. Inspect triptychs and motion sequences. Back visual inspection with
   runtime witnesses, endpoint tests and equation checks; do not infer parity from
   a single image statistic or demand identical random pictures.
6. Smoke-test each major phase, then run regression tests. Mutation-test new checks.
   Verify the actual published applications, preserve preset/backup bytes and reap
   owned processes. Commit/push main, run hosted CI and audit the committed result.

## Comparison cases

| Axis | Cases |
| --- | --- |
| Amount | 100, captured 500, midpoint 800, maximum 1500 |
| Glyph size | 10, captured 12, midpoint 25, maximum 40 |
| Fall | 0, captured 6, midpoint 75, maximum 150 |
| Constant forward | 0, captured 6, neutral 60, maximum 120; random travel disabled |
| Movement | Native captures isolate sideways, random forward and auto rotation; manual control has equation/input tests |
| Viewport | Matching landscape, representative portrait and 2D |
| Other sliders | Numeric endpoint and preview checks for picture scale/gap and clock size |

## Decisions

| Decision | Reason |
| --- | --- |
| Original controls are the new UC3D default | They reproduce the requested independent sliders and native units. |
| Older customized presets retain compatibility mode | Silent clamping or converting their generic values would change saved work. |
| Original process settings are intercepted locally | Comparison must not temporarily overwrite the installed screensaver configuration. |
| Compare distributions and trajectories as well as images | The CRT random generator is reproduced, but call order and stream scheduling differ. |
| Original pictures use native sampling and clearance | Higher size values shrink the image; the next image waits for tagged trails to fall clear. |
| Bound resume work and warm single-frame captures | A resumed frame drops sleep time beyond one second; once/FPS-zero uses a ten-second snapshot. |
| Preserve existing Matrix scene and deferred product scope | This effort is the UC3D calibration pass, not the deferred ZMatrix or publishing features. |

## Original-runtime evidence

The private `uc3d-calibration-20261023` workspace contains 23 original-process
cases, each with nine OpenGL framebuffer captures and native-memory snapshots.
Matching comparison images are at 20.00 and 20.25 simulation seconds, using a
fixed 1/120-second update. Four additional movement recordings contain 25 frames
at 12 FPS, from 20 to 22 seconds. The comparison uses 800 by 450 pixels, with a
450 by 800 portrait case, the captured purple/pink colors and the local donor atlas.

The original host/module were launched unchanged in an owned preview child
window. Frida supplied only that process's test registry values. Captures read
`GL_BACK` immediately before swapping; they contain no desktop screenshot.
`matrix-invariants.json`, `motion-invariants.json` and the control invariants
prove both original file hashes and both saved registry branches stayed unchanged.
No original test process remained after cleanup.

An accelerated capture batch was verified against an unaccelerated control:
all nine PNGs and all captured native state were byte-identical. Independent
baseline and amount-500 launches were also identical. A deliberately wrong
population failed the original-memory validator's named count check.

Actual native memory confirms amount 100/500/800/1500, glyph half-sizes
.10/.12/.25/.40, fall-zero speeds 2 to 4.9, fall-six speeds 2 to 5.5 and
fall-150 speeds up to 19.9 world units/second. Constant forward settings
0/6/60/120 settle at -6/-5.4/0/+6 world units/second. Per-update before/after
positions verify displacement independently of stream-array sorting.

## Visual comparison and limits

Private `comparisons` images place the original, WebGL and WebGPU side by side.
The amount endpoints, glyph-size endpoints, 2D and portrait views, fall and
forward controls and isolated movement were inspected. Both modern backends
show matching geometry and composition for the same seed; the native source's
different random call order produces different individual streams and poses.

As supporting measurements, at 20 seconds in the isolated neutral-camera
800-by-450 amount cases, the fraction of pixels with a maximum RGB channel above
30/255 was:

| Amount | Original | WebGL | WebGPU |
| --- | --- | --- | --- |
| 100 | 2.21% | 2.31% | 2.31% |
| 500 | 10.73% | 10.52% | 10.52% |
| 800 | 18.43% | 17.58% | 17.58% |
| 1500 | 31.29% | 31.08% | 31.08% |

These are single-frame examples, not an equivalence tolerance or a promised
coverage percentage. Moving-camera and forward endpoint captures differ more
because stream layouts and poses differ. Visual inspection and actual unit/
trajectory witnesses accompany the numbers. Pixel-identical native output,
matching random trajectories and identical mouse gestures across refresh rates
are not claimed. The fixed 120Hz simulation intentionally removes polling-rate
dependence. Native preview suppresses mouse input, clock and sound, so those
features use separate application and input tests rather than original-frame
comparison. The bundled font, HTML clock/captions and synthesized sound remain
modern alternatives; choose local donor glyph/audio resources where desired.

## Verification

The build, full gate and published application results are recorded with the
backlog closure. `tests/uc3d-calibration.py --channel msedge` runs 22 original-unit
cases on each real backend. `--motion --only sideways,random-forward,rotation,captured`
adds 12 FPS sequences. The public probe uses bundled glyphs; private atlas input
requires an output outside the checkout. Source witnesses reject stale deployed
modules; runtime inspection samples the actual rendered scene.

Mutation witnesses cover independent amount, zero fall, gradual startup,
integrated travel, projection, initial camera rest, picture tagging/scale/pan/
clearance, resume debt, single-frame population, decode sizing, inactive command
units, slider synchronization, animation limits and rebuilt metadata parity.
Zero native alpha in either GPU shader fails the named rendered-frame check.

Local completion results: full gate 52 passed, zero failed and zero unverified,
under Windows PowerShell 5.1 and PowerShell 7.6.5. The final compatibility-table
correction was included in the PowerShell 7 run. Supporting suites passed 108 C#
cases, 37 scene scenarios, seven configuration/catalog cases and 17 UI checks.
All 44 GPU calibration cases and eight short motion recordings passed. Both
published backends passed defaults, original/legacy media delivery and picture
formation; their real mouse controls and four single-frame cases also passed.
No new tools were installed; the existing scripts-utilities toolbox was sufficient.
