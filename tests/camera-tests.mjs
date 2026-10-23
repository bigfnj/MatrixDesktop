import assert from "node:assert/strict";
import fs from "node:fs";
import crypto from "node:crypto";
import path from "node:path";
import { fileURLToPath } from "node:url";
const filename = process.env.CAMERA_TEST_SOURCE ? path.resolve(process.env.CAMERA_TEST_SOURCE)
  : fileURLToPath(new URL("../MatrixDesktop/web/js/camera.js", import.meta.url));
const source = fs.readFileSync(filename, "utf8");
console.log(`SOURCE ${filename}`);
console.log(`WITNESS camera.js ${crypto.createHash("sha256").update(source).digest("hex")}`);
const filterAt = process.argv.indexOf("--only"), filter = filterAt >= 0 ? new RegExp(process.argv[filterAt + 1]) : null;
let passed = 0, failed = 0, serial = 0;
const flush = async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); };
async function test(name, run) {
  if (filter && !filter.test(name)) return;
  try { await run(); passed++; console.log(`PASS ${name}`); }
  catch (error) { failed++; console.error(`FAIL ${name}: ${error.stack}`); }
}
async function fixture({ deferredPlay = false } = {}) {
  const saved = { document: globalThis.document, requestAnimationFrame: globalThis.requestAnimationFrame, cancelAnimationFrame: globalThis.cancelAnimationFrame };
  const navigatorDescriptor = Object.getOwnPropertyDescriptor(globalThis, "navigator"), oldWarn = console.warn;
  const requests = [], plays = [], callbacks = new Map(), warnings = [];
  let raf = 0;
  const video = { readyState: 2, srcObject: null, play() {
    if (!deferredPlay) return Promise.resolve();
    return new Promise((resolve, reject) => plays.push({ resolve, reject }));
  } };
  globalThis.document = { createElement: type => type === "video" ? video : { getContext: () => ({ drawImage() {} }) } };
  globalThis.requestAnimationFrame = fn => { callbacks.set(++raf, fn); return raf; };
  globalThis.cancelAnimationFrame = id => callbacks.delete(id);
  Object.defineProperty(globalThis, "navigator", { configurable: true, value: { mediaDevices: {
    getUserMedia: () => new Promise((resolve, reject) => requests.push({ resolve, reject })),
  } } });
  console.warn = message => warnings.push(message);
  const camera = await import("data:text/javascript;base64," + Buffer.from(source).toString("base64") + "#" + ++serial);
  const stream = () => {
    const tracks = Array.from({ length: 2 }, () => ({ stops: 0, stop() { this.stops++; }, getSettings: () => ({ width: 320, height: 200 }) }));
    return { tracks, getTracks: () => tracks, getVideoTracks: () => [tracks[0]] };
  };
  return { camera, video, requests, plays, callbacks, warnings, stream,
    restore() {
      camera.stopCamera(); Object.assign(globalThis, saved); console.warn = oldWarn;
      if (navigatorDescriptor) Object.defineProperty(globalThis, "navigator", navigatorDescriptor); else delete globalThis.navigator;
    } };
}
await test("late camera acquisition after stop releases tracks without reviving video or RAF", async () => {
  const f = await fixture();
  try {
    const pending = f.camera.setupCamera(), stream = f.stream(); f.camera.stopCamera(); f.requests.shift().resolve(stream); await pending;
    assert.ok(stream.tracks.every(track => track.stops === 1), "A late stream must release each acquired track exactly once");
    assert.equal(f.video.srcObject, null); assert.equal(f.callbacks.size, 0);
  } finally { f.restore(); }
});
await test("late video play completion after stop cannot restart camera RAF", async () => {
  const f = await fixture({ deferredPlay: true });
  try {
    const pending = f.camera.setupCamera(), stream = f.stream(); f.requests.shift().resolve(stream); await flush();
    assert.equal(f.plays.length, 1); f.camera.stopCamera(); f.plays.shift().resolve(); await pending;
    assert.equal(f.callbacks.size, 0, "A late video.play completion must not schedule a disposed camera loop");
    assert.equal(f.video.srcObject, null); assert.ok(stream.tracks.every(track => track.stops === 1));
  } finally { f.restore(); }
});
await test("newer camera setup owns the video while an older acquisition completes", async () => {
  const f = await fixture();
  try {
    const older = f.camera.setupCamera(), newer = f.camera.setupCamera(), oldStream = f.stream(), current = f.stream();
    f.requests[1].resolve(current); await newer; f.requests[0].resolve(oldStream); await older;
    assert.equal(f.video.srcObject, current, "An obsolete acquisition must not replace the current stream");
    assert.equal(f.callbacks.size, 1); assert.ok(oldStream.tracks.every(track => track.stops === 1));
    assert.ok(current.tracks.every(track => track.stops === 0));
  } finally { f.restore(); }
});
await test("obsolete camera play failure cannot stop a newer stream", async () => {
  const f = await fixture({ deferredPlay: true });
  try {
    const older = f.camera.setupCamera(), oldStream = f.stream(); f.requests[0].resolve(oldStream); await flush();
    const newer = f.camera.setupCamera(), current = f.stream(); f.requests[1].resolve(current); await flush();
    f.plays[1].resolve(); await newer; f.plays[0].reject(new Error("obsolete play failure")); await older;
    assert.equal(f.video.srcObject, current); assert.equal(f.callbacks.size, 1);
    assert.ok(current.tracks.every(track => track.stops === 0), "An obsolete failure must not release current tracks");
    assert.ok(oldStream.tracks.every(track => track.stops === 1)); assert.equal(f.warnings.length, 0);
  } finally { f.restore(); }
});
await test("active camera metrics update and repeated stop releases every owned track once", async () => {
  const f = await fixture();
  try {
    const pending = f.camera.setupCamera(), stream = f.stream(); f.requests.shift().resolve(stream); await pending;
    assert.deepEqual(f.camera.cameraSize, [320, 200]); assert.equal(f.camera.cameraAspectRatio, 1.6); assert.equal(f.callbacks.size, 1);
    f.camera.stopCamera(); f.camera.stopCamera(); assert.equal(f.callbacks.size, 0); assert.equal(f.video.srcObject, null);
    assert.ok(stream.tracks.every(track => track.stops === 1));
  } finally { f.restore(); }
});
await test("failed camera acquisition leaves no loop and permits a later successful setup", async () => {
  const f = await fixture();
  try {
    const first = f.camera.setupCamera(); f.requests[0].reject(new Error("synthetic denied")); await first;
    assert.equal(f.callbacks.size, 0); assert.equal(f.video.srcObject, null); assert.equal(f.warnings.length, 1);
    const next = f.camera.setupCamera(), stream = f.stream(); f.requests[1].resolve(stream); await next;
    assert.equal(f.video.srcObject, stream); assert.equal(f.callbacks.size, 1);
  } finally { f.restore(); }
});
if (!passed && !failed) { console.error("No matching camera tests ran"); failed++; }
console.log(`Camera lifecycle tests: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
