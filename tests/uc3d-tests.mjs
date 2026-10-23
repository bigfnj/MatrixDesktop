import assert from "node:assert/strict";
import { createScene, createGrid, projection, attachMouse } from "../MatrixDesktop/web/js/uc3d/scene.js";
import { selectImages } from "../MatrixDesktop/web/js/uc3d/media.js";
import { formatClock } from "../MatrixDesktop/web/js/uc3d/overlays.js";
import { createAudio } from "../MatrixDesktop/web/js/uc3d/audio.js";
import { startLoop } from "../MatrixDesktop/web/js/uc3d/runtime.js";
import { createProgram } from "../MatrixDesktop/web/js/regl/uc3dPass.js";

console.log(`SOURCE ${new URL("../MatrixDesktop/web/js/uc3d/scene.js", import.meta.url).pathname}`);
let passed = 0, failures = 0;
function test(name, run) { try { run(); passed++; console.log(`PASS ${name}`); } catch (error) { failures++; console.error(`FAIL ${name}: ${error.message}`); } }
const near = (a, b, epsilon = 1e-7) => assert.ok(Math.abs(a - b) < epsilon, `${a} differs from ${b}`);

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
  const first = createScene({ forwardSpeed: -0.17 }), second = createScene({ forwardSpeed: -0.17 });
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
console.log(`${passed} UC3D scenario tests passed, ${failures} failed`);
process.exitCode = failures ? 1 : 0;
