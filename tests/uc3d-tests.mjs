import assert from "node:assert/strict";
import resolveConfig from "../MatrixDesktop/web/js/config.js";
import { createScene as createConfiguredScene, createGrid, projection, viewProjection, attachMouse } from "../MatrixDesktop/web/js/uc3d/scene.js";
import { createNativeRandom, nativeSettings, nativeFallSpeed, nativePictureGrid, wrapNative } from "../MatrixDesktop/web/js/uc3d/native.js";
import { selectImages, createMedia } from "../MatrixDesktop/web/js/uc3d/media.js";
import { formatClock } from "../MatrixDesktop/web/js/uc3d/overlays.js";
import { createAudio } from "../MatrixDesktop/web/js/uc3d/audio.js";
import { startLoop } from "../MatrixDesktop/web/js/uc3d/runtime.js";
import { createProgram } from "../MatrixDesktop/web/js/regl/uc3dPass.js";
import startWebGPU from "../MatrixDesktop/web/js/webgpu/uc3dPass.js";
import "./uc3d-config-tests.mjs";

console.log(`SOURCE ${new URL("../MatrixDesktop/web/js/uc3d/scene.js", import.meta.url).pathname}`);
let passed = 0, failures = 0;
function test(name, run) { try { run(); passed++; console.log(`PASS ${name}`); } catch (error) { failures++; console.error(`FAIL ${name}: ${error.message}`); } }
const near = (a, b, epsilon = 1e-7) => assert.ok(Math.abs(a - b) < epsilon, `${a} differs from ${b}`);
// Retain the existing legacy behavior cases while checking the original mode below.
const createScene = (options = {}, pictures = 0) => createConfiguredScene({ ...options, uc3dControls: "legacy" }, pictures);
const original = (options = {}, pictures = 0) => createConfiguredScene({ volumetric: true, codeRotation: false, forwardMovement: false, sidewaysMovement: false, ...options, uc3dSeed: options.uc3dSeed ?? 12345, uc3dControls: "original" }, pictures);

test("original controls keep the full stream range independent of glyph size, density, dimensions and 2D", () => {
  for (const amount of [100, 500, 800, 1500]) for (const volumetric of [false, true]) for (const aspect of [.5, 2]) {
    const scene = original({ uc3dAmount: amount, uc3dGlyphSize: amount === 500 ? 40 : 10, numColumns: 4, density: .1, volumetric });
    scene.viewport(aspect); const state = scene.update(10), grid = scene.grid(aspect);
    assert.equal(state.activeStreams, amount); assert.equal(grid.columns, amount); assert.equal(scene.inspect().length, amount);
    assert.equal(grid.glyphHalfSize, amount === 500 ? .4 : .1);
    assert.equal(grid.capacityBytes, amount*39*64); assert.ok(grid.instanceCount <= amount*39);
    assert.equal(grid.nativeMode, true); scene.dispose();
  }
});

test("original startup grows streams over ten seconds and each trail starts with one glyph", () => {
  const scene = original({ uc3dAmount: 100 });
  assert.equal(scene.update(0).activeStreams, 0);
  assert.equal(scene.update(1/120).activeStreams, 1); assert.equal(scene.inspect()[0].visible, 1);
  assert.equal(scene.update(1).activeStreams, 10); assert.equal(scene.update(5).activeStreams, 50);
  assert.equal(scene.update(10).activeStreams, 100); assert.equal(scene.update(12).activeStreams, 100);
  assert.ok(scene.inspect().every(s => s.length >= 10 && s.length <= 39));
});

test("original zero fall slider remains moving and endpoint speeds use recovered ranges", () => {
  near(nativeFallSpeed(n => n-1, 0), 4.9); near(nativeFallSpeed(n => n-1, 150), 19.9); near(nativeFallSpeed(() => 0, 150), 2);
  const scene = original({ uc3dFallSpeed: 0, fallSpeed: 0 });
  scene.update(1/120); const before = scene.inspect()[0]; scene.update(1); const after = scene.inspect()[0];
  assert.ok(after.y < before.y); assert.ok(after.visible > before.visible); assert.ok(after.speed >= 2 && after.speed <= 4.9);
});

test("original cell geometry steps by full glyph heights while visible length grows", () => {
  const scene = original({ uc3dFallSpeed: 0, uc3dGlyphSize: 40 });
  scene.update(1/120); const first = scene.inspect()[0];
  scene.update(2/120); const second = scene.inspect()[0]; near(first.renderY, second.renderY);
  scene.update(.5); const later = scene.inspect()[0]; assert.ok(later.visible > first.visible);
  near((first.renderY-later.renderY)/.8, Math.round((first.renderY-later.renderY)/.8));
});

test("original constant forward starts eased, uses signed native units and integrates displacement", () => {
  const scene = original({ forwardMovement: true, randomForwardMovement: false, uc3dForwardSpeed: 120, forwardSpeed: -100 });
  scene.update(1/120); const before = scene.inspect()[0], first = scene.snapshot();
  near(first.motion.forwardVelocity, .05); assert.ok(first.motion.forwardDistance > 0 && first.motion.forwardDistance < .001);
  scene.update(1); const after = scene.inspect()[0], current = scene.snapshot();
  near(wrapNative(before.z+current.motion.forwardDistance-first.motion.forwardDistance), after.z, 1e-6);
  assert.ok(current.motion.forwardVelocity > 3.7 && current.motion.forwardVelocity < 3.9);
  const backward = original({ forwardMovement: true, randomForwardMovement: false, uc3dForwardSpeed: 0 });
  assert.ok(backward.update(1).forwardVelocity < 0);
  near(original({ forwardMovement: true, randomForwardMovement: false, uc3dForwardSpeed: 60 }).update(1).forwardVelocity, 0);
});

test("original sideways is integrated travel, wraps the world and remains disabled in 2D", () => {
  const scene = original({ sidewaysMovement: true });
  scene.update(1/120); const before = scene.inspect()[0], initial = scene.snapshot();
  scene.update(1); const after = scene.inspect()[0], current = scene.snapshot();
  near(wrapNative(before.x+current.motion.sidewaysDistance-initial.motion.sidewaysDistance), after.x, 1e-6);
  assert.notEqual(current.motion.sidewaysDistance, current.motion.sidewaysVelocity);
  near(wrapNative(25.5), -24.5); near(wrapNative(-26), 24); near(wrapNative(175.5), -24.5);
  const flat = original({ volumetric: false, sidewaysMovement: true, forwardMovement: true });
  const flatState = flat.update(5); near(flatState.sidewaysVelocity, 0); near(flatState.forwardVelocity, 0);
});

test("original fixed-step motion, RNG and streams agree at 30 and 120 frames per second", () => {
  const config = { uc3dAmount: 100, codeRotation: true, randomForwardMovement: true, forwardMovement: true, sidewaysMovement: true };
  const first = original(config), second = original(config);
  for (let i = 0; i <= 360; i++) first.update(i/30);
  for (let i = 0; i <= 1440; i++) second.update(i/120);
  assert.deepEqual(first.snapshot(8), second.snapshot(8));
});

test("original camera rests neutral for ten seconds then eases rotation with correct view origin", () => {
  const scene = original({ codeRotation: true });
  const rest = scene.update(10); assert.equal(rest.phaseMoving, false); assert.deepEqual(rest.eye, [0, 0, 25]);
  const moving = scene.update(12); assert.equal(moving.phaseMoving, true); assert.notDeepEqual(moving.eye, rest.eye);
  near(Math.hypot(...moving.eye), 25, 1e-6);
  const transformed = [0, 1, 2].map(row => moving.viewMatrix[row]*moving.eye[0]+moving.viewMatrix[4+row]*moving.eye[1]+moving.viewMatrix[8+row]*moving.eye[2]+moving.viewMatrix[12+row]);
  transformed.forEach(n => near(n, 0, 2e-6));
});

test("original projection uses 45 degrees, near one and matching WebGL/WebGPU depth", () => {
  for (const aspect of [.5, 1, 2]) {
    const scene = original(), state = scene.update(0), gl = viewProjection(aspect, scene.config, state), gpu = viewProjection(aspect, scene.config, state, true);
    near(gl[5], 1/Math.tan(Math.PI/8), 1e-6);
    for (const worldZ of [24, 0, -75]) {
      const clip = m => [(m[10]*worldZ+m[14])/(m[11]*worldZ+m[15]), m[0]];
      near((clip(gl)[0]+1)/2, clip(gpu)[0], 2e-6); near(clip(gl)[1], clip(gpu)[1]);
    }
    const flat = original({ volumetric: false, uc3dGlyphSize: 12 }), matrix = viewProjection(aspect, flat.config, flat.update(0));
    near(matrix[0]*.12, 1/116, 1e-7); near(matrix[5], matrix[0]*aspect, 1e-7);
    assert.equal(nativeSettings({ uc3dGlyphSize: 15 }).screenColumns, 93); assert.equal(nativeSettings({ uc3dGlyphSize: 40 }).screenColumns, 35);
  }
});

test("original packed geometry stays bounded, sorts far-first and gives finite alpha", () => {
  const scene = original({ uc3dAmount: 100 }); scene.update(10);
  const first = scene.grid(2), second = scene.grid(2);
  assert.equal(first.data.buffer, second.data.buffer); assert.equal(first.data.length, first.instanceCount*16);
  const streams = new Map(scene.inspect().map(stream => [stream.id, stream]));
  let previous = Infinity, lastID = -1;
  for (let i = 0; i < first.data.length; i += 16) {
    assert.ok(first.data[i+14] > 0 && first.data[i+14] <= 1); assert.ok(first.data[i+15] >= 0 && first.data[i+15] <= 1);
    assert.ok(first.data[i+12] >= 0 && first.data[i+12] < 79);
    if (first.data[i] !== lastID) {
      const stream = streams.get(first.data[i]);
      const distance = stream.x**2+stream.y**2+(stream.z-25)**2;
      assert.ok(distance <= previous+1e-6, `stream order ${distance} exceeds ${previous}`); previous = distance; lastID = first.data[i];
    }
  }
  assert.equal(scene.snapshot(1000).streams.length, 32); scene.dispose(); assert.equal(scene.update(11), null); assert.equal(scene.grid(1), null);
});

test("original random generator matches MSVCRT witnesses and mouse quaternions return after inactivity", () => {
  const random = createNativeRandom(1); assert.deepEqual(Array.from({ length: 5 }, () => random()), [41, 18467, 6334, 26500, 19169]);
  const scene = original({ mouseRotation: true }); scene.update(1); scene.pointer(1200, 600, 1); scene.wheel(240, 1);
  const moving = scene.update(2); assert.equal(moving.mouseActive, true); assert.notDeepEqual(moving.eye, [0, 0, 25]);
  scene.stop(2); assert.equal(scene.stopped, true); scene.reset(2); assert.equal(scene.stopped, false);
  const returned = scene.update(24); assert.equal(returned.mouseActive, false); near(returned.eye[0], 0, .001); near(returned.eye[1], 0, .001);
});

test("original pictures wait twenty seconds initially and use the selected gap only after their episode", () => {
  const scene = original({ showPictures: true, pictureDelay: 60, pictureDuration: 30 }, 2);
  near(scene.update(19).pictureWeight, 0); near(scene.update(25).pictureWeight, 0);
  const forming = scene.update(30); assert.ok(forming.pictureWeight > .7 && forming.pictureWeight < .73);
  assert.equal(forming.pictureIndex, 0); assert.ok(scene.update(49).pictureWeight > .99);
  let time = 50;
  while (time < 100 && scene.update(time).pictureIndex === 0) time += 1/120;
  assert.ok(time < 100, "picture streams did not clear");
  assert.equal(scene.snapshot().picture.taggedStreams, 0); near(scene.snapshot().picture.countdown, 60, .01);
  near(scene.update(time+59).pictureWeight, 0); assert.equal(scene.snapshot().picture.forming, false);
  near(scene.update(time+65).pictureWeight, 0, .003); assert.ok(scene.update(time+66).pictureWeight > .2);
});

test("original zero picture gap waits for tagged streams and mouse input defers a new formation", () => {
  const scene = original({ uc3dAmount: 100, uc3dFallSpeed: 40, showPictures: true, pictureDelay: 0, pictureDuration: 8 }, 2);
  scene.update(28.1); const lingering = scene.snapshot();
  assert.ok(lingering.picture.taggedStreams > 0); assert.equal(lingering.picture.forming, false); assert.equal(scene.update(28.1).pictureIndex, 0);
  let time = 28.1;
  while (time < 70 && scene.update(time).pictureIndex === 0) time += 1/120;
  assert.ok(time < 70); assert.equal(scene.snapshot().picture.taggedStreams, 0); near(scene.snapshot().picture.countdown, 0);
  const manual = original({ uc3dAmount: 100, mouseRotation: true, showPictures: true }, 1);
  manual.update(19); manual.pointer(1200, 0, 19);
  manual.update(38); assert.equal(manual.snapshot().picture.forming, false); near(manual.snapshot().picture.countdown, 0, .01);
  manual.update(40); assert.equal(manual.snapshot().picture.forming, true); near(manual.snapshot().picture.weight, 0);
  assert.ok(manual.update(46).pictureWeight > .2);
});

test("original picture sampling uses only forming streams and preserves recovered inverse scale and sinusoidal offsets", () => {
  const settings = nativeSettings({ uc3dGlyphSize: 12 }), photo = { width: 160, height: 120, data: new Uint8ClampedArray(160*120*4).fill(255) };
  const initial = nativePictureGrid(photo, settings, 1, 0, 8, 10);
  assert.deepEqual(initial, { width: 160, height: 120, x: 80, y: -43.5 });
  const small = nativePictureGrid(photo, settings, 3, 0, 8, 10);
  assert.equal(small.width, 53); assert.equal(small.height, 40);
  near(nativePictureGrid(photo, settings, 1, 4, 8, 10).x, 102);
  near(nativePictureGrid(photo, settings, 1, 10, 8, 10).y, -76.5);
  const scene = original({ uc3dAmount: 100, uc3dFallSpeed: 40, showPictures: true }, 1);
  scene.update(19);
  assert.equal(scene.snapshot().picture.taggedStreams, 0);
  assert.ok(scene.grid(2, photo).data.every((n, i) => i%16 !== 7 || n === 0));
  scene.update(25);
  const details = scene.snapshot(), streams = new Map(scene.inspect().map(s => [s.id, s]));
  assert.ok(details.picture.taggedStreams > 0 && details.picture.taggedStreams < 100);
  const grid = scene.grid(2, photo); let samples = 0;
  for (let i = 0; i < grid.data.length; i += 16) if (grid.data[i+7] > 0) {
    assert.equal(streams.get(grid.data[i]).picture, true, "old rain was colored before it respawned into the picture"); samples++;
  }
  assert.ok(samples > 0); assert.ok(details.picture.periodX >= 3 && details.picture.periodX <= 12.9);
  assert.ok(details.picture.periodY >= 5 && details.picture.periodY <= 14.9);
});

test("bare UC3D uses the installed purple 3D profile with 500 independent streams", () => {
  const config = resolveConfig({ scene: "uc3d" });
  assert.equal(config.volumetric, true);
  assert.equal(config.numColumns, 100); assert.equal(config.density, 5);
  near(12 / config.numColumns, 0.12); near(config.fallSpeed, 0.045); near(config.forwardSpeed, -0.675);
  assert.deepEqual(config.codeColor, { space: "rgb", values: [128 / 255, 0, 1] });
  assert.deepEqual(config.cursorColor, { space: "rgb", values: [1, 128 / 255, 1] });
  for (const key of ["sidewaysMovement", "randomForwardMovement", "forwardMovement", "codeRotation", "shadedLines"]) assert.equal(config[key], true, key);
  for (const key of ["showPictures", "showClock", "mouseRotation", "desktopBackground", "pictureMonochrome"]) assert.equal(config[key], false, key);
  assert.equal(config.sound, "off"); assert.equal(config.pictureDelay, 60); assert.equal(config.pictureDuration, 30);
  const scene = createConfiguredScene(config); scene.viewport(.6); scene.update(10);
  assert.equal(scene.grid(.6).columns, 500); scene.dispose();
});

test("UC3D explicit generic controls retain legacy behavior regardless of effect or version", () => {
  const config = resolveConfig({ scene: "uc3d", volumetric: "false", numColumns: "80", density: "1", fallSpeed: "0.3", forwardSpeed: "0.25", sidewaysMovement: "false", randomForwardMovement: "false", effect: "palette", version: "paradise" });
  assert.equal(config.volumetric, false); assert.equal(config.numColumns, 80); assert.equal(config.density, 1);
  assert.equal(config.uc3dControls, "legacy");
  near(config.fallSpeed, 0.3); near(config.forwardSpeed, 0.25);
  assert.equal(config.sidewaysMovement, false); assert.equal(config.randomForwardMovement, false);
  assert.deepEqual(config.cursorColor.values, [1, 128 / 255, 1]);
  assert.equal(resolveConfig({ scene: "uc3d", density: "5" }).density, 5);
  assert.equal(resolveConfig({ scene: "uc3d", density: "100" }).density, 8);
  const matrix = resolveConfig({});
  assert.equal(matrix.scene, "matrix"); assert.equal(matrix.volumetric, false);
  assert.equal(matrix.numColumns, 80); assert.equal(matrix.density, 1); near(matrix.fallSpeed, 0.3);
});

test("WebGL program initialization releases shaders and failed programs on every compile and link outcome", () => {
  for (const outcome of ["vertex", "fragment", "link", "success"]) {
    const shaders = new Set(), programs = new Set(), deletedShaders = new Set(), deletedPrograms = new Set();
    const gl = {
      VERTEX_SHADER: "vertex", FRAGMENT_SHADER: "fragment", COMPILE_STATUS: "compile", LINK_STATUS: "link",
      createShader(type) { const shader = { type }; shaders.add(shader); return shader; },
      createProgram() { const program = {}; programs.add(program); return program; },
      shaderSource() {}, compileShader() {}, attachShader() {}, linkProgram() {},
      getShaderParameter: shader => shader.type !== outcome,
      getProgramParameter: () => outcome !== "link",
      getShaderInfoLog: () => outcome, getProgramInfoLog: () => outcome,
      deleteShader(shader) { assert.ok(!deletedShaders.has(shader)); deletedShaders.add(shader); },
      deleteProgram(program) { assert.ok(!deletedPrograms.has(program)); deletedPrograms.add(program); },
    };
    if (outcome === "success") {
      const result = createProgram(gl, "vertex source", "fragment source");
      assert.ok(programs.has(result)); assert.equal(deletedPrograms.size, 0);
    } else {
      assert.throws(() => createProgram(gl, "vertex source", "fragment source"), new RegExp(`UC3D (?:shader|program): ${outcome}`));
      assert.deepEqual(deletedPrograms, programs, `${outcome} leaked a program`);
    }
    assert.deepEqual(deletedShaders, shaders, `${outcome} leaked a shader`);
    assert.equal(shaders.size, outcome === "vertex" ? 1 : 2);
  }
});

test("signed constant travel has the same phase at 30 and 120 fps", () => {
  const first = createScene({ forwardSpeed: -0.17, randomForwardMovement: false }), second = createScene({ forwardSpeed: -0.17, randomForwardMovement: false });
  let a, b;
  for (let i = 0; i <= 300; i++) a = first.update(i / 30);
  for (let i = 0; i <= 1200; i++) b = second.update(i / 120);
  near(a.forwardPhase, -0.272); near(a.forwardPhase, b.forwardPhase);
});
test("random travel is continuous across its speed keyframe and independent of updates", () => {
  const scene = createScene({ randomForwardMovement: true });
  const before = scene.update(12 - 0.00001), after = scene.update(12 + 0.00001);
  assert.ok(Math.abs(after.forwardPhase - before.forwardPhase) < 0.00001);
  const direct = createScene({ randomForwardMovement: true }).update(360);
  let stepped;
  for (let time = 0; time <= 360; time += 0.5) stepped = scene.update(time);
  near(stepped.forwardPhase, direct.forwardPhase);
  assert.notEqual(scene.update(372).forwardPhase, direct.forwardPhase);
});
test("disabling forward movement freezes travel despite nonzero speed", () => {
  near(createScene({ forwardMovement: false, forwardSpeed: 4 }).update(100).forwardPhase, 0);
});
test("mouse input suppresses pictures and returns to automatic control after 20 seconds", () => {
  const scene = createScene({ volumetric: true, mouseRotation: true, codeRotation: false, showPictures: true, pictureDelay: 0, pictureDuration: 8 }, 1);
  scene.update(1); scene.pointer(100, 0, 1);
  const active = scene.update(2); assert.ok(active.mouseActive); near(active.yaw, Math.PI / 2); near(active.pictureWeight, 0);
  scene.update(21); const automatic = scene.update(23); assert.equal(automatic.mouseActive, false); near(automatic.yaw, 0);
});
test("reset, wheel, and disposal have observable effects", () => {
  const scene = createScene({ volumetric: true, mouseRotation: true });
  scene.pointer(100, 50); scene.wheel(200); near(scene.update(0).roll, 0.2);
  scene.reset(); const reset = scene.update(0); near(reset.yaw, 0); near(reset.pitch, 0); near(reset.roll, 0);
  scene.stop(); assert.equal(scene.stopped, true); scene.dispose(); assert.equal(scene.update(1), null);
});
test("slideshow advances and has distinct wait, fade, hold, and next-picture states", () => {
  const scene = createScene({ showPictures: true, pictureDelay: 2, pictureDuration: 8 }, 2);
  near(scene.update(1).pictureWeight, 0); assert.ok(scene.update(3.8).pictureWeight > 0);
  near(scene.update(5).pictureWeight, 1); assert.equal(scene.update(12).pictureIndex, 1); near(scene.update(12).pictureWeight, 0);
});
test("only enabled 3D mouse control attaches and removes exactly its listeners", () => {
  const listeners = new Map();
  const canvas = { addEventListener: (name, fn) => listeners.set(name, fn), removeEventListener: (name, fn) => { assert.equal(listeners.get(name), fn); listeners.delete(name); } };
  attachMouse(canvas, createScene({ mouseRotation: false }), () => 0)(); assert.equal(listeners.size, 0);
  const cleanup = attachMouse(canvas, createScene({ mouseRotation: true, volumetric: true }), () => 0);
  assert.equal(listeners.size, 5); cleanup(); assert.equal(listeners.size, 0);
});
test("photo sampling preserves asymmetric colors and excludes letterboxed edges", () => {
  const photo = { width: 2, height: 1, data: new Uint8ClampedArray([255, 0, 0, 255, 0, 255, 0, 255]) };
  const grid = createGrid({ numColumns: 20, pictureScale: 1 }, 1, photo);
  const colors = [];
  for (let i = 0; i < grid.data.length; i += 60) if (grid.data[i + 9] > 0) colors.push(Array.from(grid.data.subarray(i + 6, i + 9)));
  assert.ok(colors.some(color => color[0] === 1 && color[1] === 0)); assert.ok(colors.some(color => color[1] === 1 && color[0] === 0));
  assert.equal(grid.data[9], 0);
  const large = createGrid({ numColumns: 20, pictureScale: 2 }, 1, photo);
  let count = 0; for (let i = 9; i < large.data.length; i += 60) if (large.data[i] > 0) count++;
  assert.ok(count > colors.length);
});
test("density changes 3D cell count while 2D and glyph size keep independent meaning", () => {
  const normal = createGrid({ numColumns: 20, volumetric: true, density: 1 }, 2);
  const dense = createGrid({ numColumns: 20, volumetric: true, density: 2 }, 2);
  assert.equal(dense.count, normal.count * 2); assert.equal(dense.baseColumns, normal.baseColumns);
  assert.equal(createGrid({ numColumns: 20, density: 2 }, 2).count, normal.count);
});
test("WebGL and WebGPU project the same landmarks with their correct depth conventions", () => {
  for (const aspect of [0.6, 1, 2.8]) for (const isometric of [false, true]) {
    const gl = projection(aspect, true, false, isometric), gpu = projection(aspect, true, true, isometric);
    const z = -5, w = gl[11] * z + gl[15];
    near(gl[0], gpu[0]); near(gl[5], gpu[5]);
    const glZ = (gl[10] * z + gl[14]) / w, gpuZ = (gpu[10] * z + gpu[14]) / w;
    near((glZ + 1) / 2, gpuZ); assert.ok(gpuZ > 0 && gpuZ < 1);
  }
});
test("source filters do not silently select a different movie group", () => {
  const images = [{ url: "a", group: "matrix" }, { url: "b", group: "custom" }, { url: "c", group: "reloaded", groups: ["reloaded", "revolutions"] }];
  assert.equal(selectImages(images, "all").length, 3); assert.equal(selectImages(images, "custom")[0].url, "b");
  assert.equal(selectImages(images, "revolutions")[0].url, "c");
});
test("clock formats midnight, noon, and late evening without ambiguous 12-hour output", () => {
  assert.equal(formatClock(new Date(2026, 0, 1, 0, 3, 4), true), "12:03:04 AM");
  assert.equal(formatClock(new Date(2026, 0, 1, 12, 3, 4), true), "12:03:04 PM");
  assert.equal(formatClock(new Date(2026, 0, 1, 23, 3, 4), false), "23:03:04");
});
test("zero fps retains one frame and schedules no background rendering loop", () => {
  const oldWindow = globalThis.window, oldRequest = globalThis.requestAnimationFrame, oldCancel = globalThis.cancelAnimationFrame;
  const callbacks = []; let draws = 0, disposals = 0;
  globalThis.window = { devicePixelRatio: 1, addEventListener() {}, removeEventListener() {} };
  globalThis.requestAnimationFrame = callback => { callbacks.push(callback); return callbacks.length; };
  globalThis.cancelAnimationFrame = () => {};
  try {
    const runtime = { config: { fps: 0, resolution: 1 }, diagnostics: { errors: [] }, update: () => ({}), dispose: () => disposals++ };
    const cleanup = startLoop({ clientWidth: 20, clientHeight: 10 }, runtime, () => draws++, () => {});
    callbacks.shift()(0); assert.equal(draws, 1); assert.equal(callbacks.length, 0); assert.equal(disposals, 0);
    cleanup(); assert.equal(disposals, 1);
  } finally { globalThis.window = oldWindow; globalThis.requestAnimationFrame = oldRequest; globalThis.cancelAnimationFrame = oldCancel; }
});

function loopTimes(config, timestamps) {
  const oldWindow = globalThis.window, oldRequest = globalThis.requestAnimationFrame, oldCancel = globalThis.cancelAnimationFrame;
  const callbacks = [], observed = [];
  globalThis.window = { devicePixelRatio: 1, addEventListener() {}, removeEventListener() {} };
  globalThis.requestAnimationFrame = callback => { callbacks.push(callback); return callbacks.length; };
  globalThis.cancelAnimationFrame = () => {};
  try {
    const runtime = { config: { fps: 60, resolution: 1, ...config }, diagnostics: { errors: [] }, update: seconds => { observed.push(seconds); return {}; }, dispose() {} };
    const stop = startLoop({ clientWidth: 20, clientHeight: 10 }, runtime, () => {}, () => {});
    for (const timestamp of timestamps) { assert.ok(callbacks.length, "render loop stopped before the supplied timestamp"); callbacks.shift()(timestamp); }
    const scheduled = callbacks.length; stop(); return { observed, scheduled };
  } finally { globalThis.window = oldWindow; globalThis.requestAnimationFrame = oldRequest; globalThis.cancelAnimationFrame = oldCancel; }
}

test("original render loop drops long resume debt and preserves ordinary frame cadence", () => {
  const resumed = loopTimes({ uc3dControls: "original" }, [0, 1000/60, 5000, 5000+1000/60, 5000+2000/60]);
  const expected = [0, 1/60, 1+1/60, 1+2/60, 1+3/60];
  resumed.observed.forEach((n, i) => near(n, expected[i])); assert.equal(resumed.scheduled, 1);
  const ordinary = loopTimes({ uc3dControls: "original", fps: 120 }, Array.from({ length: 361 }, (_, i) => i*1000/30));
  near(ordinary.observed.at(-1), 12); assert.equal(ordinary.observed.length, 361);
  near(loopTimes({ uc3dControls: "legacy" }, [0, 5000]).observed.at(-1), 5);
});

test("original once and zero-fps frames show settled startup while legacy captures time zero", () => {
  for (const controls of ["original", "legacy"]) for (const options of [{ once: true }, { fps: 0 }]) {
    const result = loopTimes({ uc3dControls: controls, ...options }, [12345]);
    assert.deepEqual(result.observed, [controls === "original" ? 10 : 0]); assert.equal(result.scheduled, 0);
  }
});
const previousDocument = globalThis.document, previousAudio = globalThis.Audio;
const listeners = new Map(), buttons = [];
globalThis.document = {
  hidden: false,
  addEventListener: (name, callback) => listeners.set(name, callback),
  removeEventListener: (name) => listeners.delete(name),
  createElement: () => ({ style: {}, addEventListener() {}, remove() { this.removed = true; } }),
  body: { append: button => buttons.push(button) },
};
try {
  test("sound off creates no visibility callback or media element", () => {
    globalThis.Audio = class { constructor() { throw new Error("off mode opened audio"); } };
    const audio = createAudio({ sound: "off" }, {}, {});
    assert.equal(listeners.size, 0); audio.dispose(); assert.equal(audio.state.status, "disposed");
  });
  globalThis.Audio = class { async play() { throw new DOMException("Unsupported test source", "NotSupportedError"); } pause() {} removeAttribute() {} load() {} };
  const failedAudio = createAudio({ sound: "music" }, { audioUrl: "synthetic:invalid" }, {});
  await new Promise(resolve => setImmediate(resolve));
  test("bad local audio reports a failed source instead of asking for a gesture", () => {
    assert.equal(failedAudio.state.status, "failed"); assert.equal(buttons.at(-1).textContent, "Retry sound");
    failedAudio.dispose(); assert.equal(listeners.size, 0); assert.equal(buttons.at(-1).removed, true);
  });
  let completePlay, pauses = 0;
  globalThis.Audio = class { play() { return new Promise(resolve => { completePlay = resolve; }); } pause() { pauses++; } removeAttribute() {} load() {} };
  const lateAudio = createAudio({ sound: "music" }, { audioUrl: "synthetic:late" }, {});
  lateAudio.dispose(); completePlay(); await new Promise(resolve => setImmediate(resolve));
  test("late play completion cannot reactivate audio after preview disposal", () => {
    assert.equal(lateAudio.state.status, "disposed"); assert.ok(pauses >= 2); assert.equal(listeners.size, 0);
  });
} finally {
  if (previousDocument === undefined) delete globalThis.document; else globalThis.document = previousDocument;
  if (previousAudio === undefined) delete globalThis.Audio; else globalThis.Audio = previousAudio;
}

async function asyncTest(name, run) {
  try { await run(); passed++; console.log(`PASS ${name}`); }
  catch (error) { failures++; console.error(`FAIL ${name}: ${error.message}`); }
}
await asyncTest("original custom pictures use the native 300-pixel decode while legacy and donor dimensions remain intact", async () => {
  const previousImage = globalThis.Image, previousDocument = globalThis.document;
  globalThis.Image = class { naturalWidth = 601; naturalHeight = 400; async decode() {} };
  globalThis.document = { createElement: () => {
    const canvas = { width: 0, height: 0 };
    canvas.getContext = () => ({ drawImage() {}, getImageData: () => ({ data: new Uint8ClampedArray(canvas.width*canvas.height*4) }) });
    return canvas;
  } };
  try {
    for (const [entry, originalMode, expected] of [
      [{ url: "synthetic:custom", group: "custom" }, true, [300, 199]],
      [{ url: "synthetic:donor", groups: ["matrix"] }, true, [601, 400]],
      [{ url: "synthetic:legacy", group: "custom" }, false, [601, 400]],
    ]) {
      const media = createMedia([entry], { original: originalMode });
      const decoded = await media.load(0); assert.deepEqual([decoded.width, decoded.height], expected); media.dispose();
    }
  } finally { globalThis.Image = previousImage; globalThis.document = previousDocument; }
});
await asyncTest("WebGPU initialization rejects missing contexts and preserves errors while releasing its device", async () => {
  const globals = ["navigator", "window", "document", "Image", "fetch", "GPUBufferUsage", "GPUTextureUsage", "requestAnimationFrame", "cancelAnimationFrame"];
  const descriptors = new Map(globals.map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
  const previousWarn = console.warn;
  try {
    const element = () => ({ style: {}, dataset: {}, append() {}, setAttribute() {}, remove() {} });
    globalThis.document = { createElement: element, body: { append() {} }, addEventListener() {}, removeEventListener() {} };
    globalThis.Image = class { naturalWidth = 32; naturalHeight = 32; async decode() {} };
    globalThis.fetch = async () => ({ ok: true, text: async () => "synthetic shader source" });
    globalThis.GPUBufferUsage = { UNIFORM: 1, COPY_DST: 2, VERTEX: 4 };
    globalThis.GPUTextureUsage = { TEXTURE_BINDING: 1, COPY_DST: 2, RENDER_ATTACHMENT: 4 };
    globalThis.requestAnimationFrame = () => 1;
    globalThis.cancelAnimationFrame = () => {};
    for (const outcome of ["null-context", "configure", "unconfigure", "success"]) {
      let destroyed = 0, unconfigured = 0, configured = 0, resources = 0, released = 0, cleanupWarnings = 0;
      globalThis.window = { addEventListener() {}, removeEventListener() {} };
      const resource = () => { resources++; return { createView: () => ({}), destroy() { released++; } }; };
      const device = {
        destroy() { destroyed++; }, queue: { copyExternalImageToTexture() {}, writeBuffer() {} },
        createShaderModule: () => ({ getCompilationInfo: async () => ({ messages: [] }) }),
        createRenderPipelineAsync: async () => ({ getBindGroupLayout: () => ({}) }),
        createBuffer: resource, createTexture: resource, createSampler: () => ({}), createBindGroup: () => ({}),
        addEventListener() {}, lost: new Promise(() => {}),
      };
      Object.defineProperty(globalThis, "navigator", { configurable: true, value: { gpu: {
        requestAdapter: async () => ({ requestDevice: async () => device }), getPreferredCanvasFormat: () => "rgba8unorm",
      } } });
      const originalError = new Error("controlled configure failure");
      const context = {
        configure() { configured++; if (outcome !== "success") throw originalError; },
        unconfigure() { unconfigured++; if (outcome === "unconfigure") throw new Error("controlled unconfigure failure"); },
      };
      console.warn = () => { cleanupWarnings++; };
      const initialize = startWebGPU({ dataset: {}, getContext: () => outcome === "null-context" ? null : context }, {});
      if (outcome === "null-context") {
        await assert.rejects(initialize, /canvas context is unavailable/);
        assert.equal(configured, 0); assert.equal(unconfigured, 0);
      } else if (outcome !== "success") {
        await assert.rejects(initialize, error => error === originalError, "cleanup must preserve the original configure error");
        assert.equal(configured, 1); assert.equal(unconfigured, 1);
        assert.equal(cleanupWarnings, outcome === "unconfigure" ? 1 : 0);
      } else {
        const stop = await initialize;
        assert.equal(destroyed, 0, "a live renderer must retain its device");
        assert.equal(configured, 1); assert.equal(unconfigured, 0);
        stop(); stop();
        assert.equal(unconfigured, 1); assert.equal(released, resources, "normal teardown must release allocated buffers and textures");
      }
      assert.equal(destroyed, 1, `${outcome} must destroy the device exactly once`);
    }
  } finally {
    console.warn = previousWarn;
    for (const [name, descriptor] of descriptors) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor); else delete globalThis[name];
    }
  }
});
console.log(`${passed} UC3D scenario tests passed, ${failures} failed`);
process.exitCode = failures ? 1 : 0;
