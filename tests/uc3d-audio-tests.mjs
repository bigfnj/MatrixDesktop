import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createHash } from "node:crypto";
import { pathToFileURL } from "node:url";
import path from "node:path";
const root = process.env.UC3D_AUDIO_TEST_ROOT ? pathToFileURL(path.resolve(process.env.UC3D_AUDIO_TEST_ROOT) + path.sep)
  : new URL("../MatrixDesktop/web/js/", import.meta.url);
const { createAudio } = await import(new URL("uc3d/audio.js", root));
const { subscribeHostPlayback } = await import(new URL("hostPlayback.js", root));
for (const file of ["uc3d/audio.js", "hostPlayback.js"])
  console.log(`WITNESS ${file} ${createHash("sha256").update(readFileSync(new URL(file, root))).digest("hex")}`);
const flush = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); };
const filterAt = process.argv.indexOf("--only"), filter = filterAt >= 0 ? new RegExp(process.argv[filterAt + 1]) : null;
let passed = 0, failures = 0;
async function test(name, run) {
  if (filter && !filter.test(name)) return;
  try { await run(); passed++; console.log(`PASS ${name}`); }
  catch (error) { failures++; console.error(`FAIL ${name}: ${error.stack}`); }
}
function fixture({ paused = false, preview = false, local = false, defer = false, sound = "effects" } = {}) {
  const saved = { window: globalThis.window, document: globalThis.document, Audio: globalThis.Audio };
  const contexts = [], elements = [], buttons = [], listeners = new Set(), pending = [];
  const document = new EventTarget(); document.hidden = false;
  document.createElement = () => {
    const button = new EventTarget(); button.style = {}; button.remove = () => { button.removed = true; };
    buttons.push(button); return button;
  };
  document.body = { append() {} };
  class Context {
    constructor() { this.state = "suspended"; this.sampleRate = 32; this.destination = {}; this.nodes = []; this.resumes = 0; this.suspends = 0; this.closes = 0; contexts.push(this); }
    node() { const node = { gain: { value: 0 }, frequency: {}, Q: {}, connect() {}, start() {}, stop() {},
      disconnect() { this.disconnected = true; } }; this.nodes.push(node); return node; }
    createGain() { return this.node(); } createBufferSource() { return this.node(); } createBiquadFilter() { return this.node(); }
    createOscillator() { return this.node(); } createBuffer() { return { getChannelData: () => new Float32Array(96) }; }
    resume() {
      this.resumes++;
      if (defer) { defer = false; return new Promise((resolve, reject) => {
        const finish = () => { if (this.state !== "closed") this.state = "running"; resolve(); }; finish.reject = reject; pending.push(finish);
      }); }
      this.state = "running"; return Promise.resolve();
    }
    suspend() { this.suspends++; if (this.state !== "closed") this.state = "suspended"; return Promise.resolve(); }
    close() { this.closes++; this.state = "closed"; return Promise.resolve(); }
  }
  class Element {
    constructor(url) { this.url = url; this.paused = true; this.plays = 0; elements.push(this); }
    play() {
      this.plays++;
      if (defer) { defer = false; return new Promise(resolve => pending.push(() => { this.paused = false; resolve(); })); }
      this.paused = false; return Promise.resolve();
    }
    pause() { this.paused = true; } load() {} removeAttribute() {}
  }
  globalThis.window = { AudioContext: Context }; globalThis.document = document; globalThis.Audio = Element;
  const audio = createAudio({ sound, soundVolume: .5 }, { preview, effectsUrl: local ? "fixture.wav" : null }, {}, {
    subscribe(listener) { listeners.add(listener); listener(paused); return () => listeners.delete(listener); },
  });
  return { audio, contexts, elements, buttons, pending, listeners,
    pause(value) { paused = value; for (const listener of listeners) listener(value); },
    hidden(value) { document.hidden = value; document.dispatchEvent(new Event("visibilitychange")); },
    restore() { audio.dispose(); Object.assign(globalThis, saved); },
  };
}
await test("host audio subscription immediately reports the current playback state", () => {
  const values = [], unsubscribe = subscribeHostPlayback(value => values.push(value));
  try { assert.equal(values.length, 1, "A new audio owner must receive current host state immediately"); assert.equal(typeof values[0], "boolean"); }
  finally { unsubscribe(); }
});
await test("host pause suspends Web Audio and visibility cannot override it", async () => {
  const f = fixture();
  try {
    await flush(); assert.equal(f.audio.state.status, "playing");
    const context = f.contexts[0]; assert.ok(context.nodes[0].gain.value > 0);
    f.pause(true); await flush(); assert.equal(context.state, "suspended"); assert.equal(context.nodes[0].gain.value, 0);
    assert.equal(f.audio.state.status, "paused"); const resumes = context.resumes;
    f.hidden(true); f.hidden(false); await flush(); assert.equal(context.resumes, resumes, "Visibility restoration must not resume host-paused audio");
    f.pause(false); await flush(); assert.equal(context.state, "running"); assert.equal(f.audio.state.status, "playing");
  } finally { f.restore(); }
});
await test("initial host pause creates no audio until resumed", async () => {
  const f = fixture({ paused: true });
  try {
    await flush(); assert.equal(f.contexts.length, 0); assert.equal(f.audio.state.status, "paused");
    f.pause(false); await flush(); assert.equal(f.contexts.length, 1); assert.equal(f.audio.state.contextState, "running");
  } finally { f.restore(); }
});
await test("paused preview gestures cannot start or authorize later automatic audio", async () => {
  const f = fixture({ paused: true, preview: true });
  try {
    const button = f.buttons[0]; assert.equal(button.disabled, true); button.dispatchEvent(new Event("click")); await flush();
    assert.equal(f.contexts.length, 0, "A paused preview gesture must not create an audio context");
    f.pause(false); await flush(); assert.equal(button.disabled, false); assert.equal(f.contexts.length, 0);
    button.dispatchEvent(new Event("click")); await flush(); assert.equal(f.audio.state.status, "playing"); assert.equal(button.removed, true);
  } finally { f.restore(); }
});
await test("HTML audio completion after host pause remains muted and paused", async () => {
  const f = fixture({ local: true, defer: true });
  try {
    const element = f.elements[0]; f.pause(true); assert.equal(element.muted, true); assert.equal(element.paused, true);
    f.pending.shift()(); await flush();
    assert.equal(element.paused, true, "Late play completion must physically pause the media element again");
    assert.equal(element.muted, true); assert.equal(f.audio.state.status, "paused");
    f.pause(false); await flush(); assert.equal(element.paused, false); assert.equal(element.muted, false); assert.equal(f.audio.state.status, "playing");
  } finally { f.restore(); }
});
await test("Web Audio completion after host pause remains suspended and silent", async () => {
  const f = fixture({ defer: true });
  try {
    const context = f.contexts[0]; f.pause(true); f.pending.shift()(); await flush();
    assert.equal(context.state, "suspended", "Late resume completion must physically suspend Web Audio again");
    assert.equal(context.nodes[0].gain.value, 0); assert.equal(f.audio.state.status, "paused");
    f.pause(false); await flush(); assert.equal(context.state, "running"); assert.equal(f.audio.state.status, "playing");
  } finally { f.restore(); }
});
await test("older audio startup failure cannot replace a newer successful resume", async () => {
  const f = fixture({ defer: true });
  try {
    f.pause(true); f.pause(false); await flush(); assert.equal(f.audio.state.status, "playing");
    f.pending.shift().reject(new Error("obsolete startup failure")); await flush();
    assert.equal(f.audio.state.status, "playing", "An obsolete asynchronous failure must not replace current playback state");
    assert.equal(f.buttons.length, 0, "An obsolete startup failure must not add a retry control");
  } finally { f.restore(); }
});
await test("document hidden pauses audio until both document and host permit playback", async () => {
  const f = fixture();
  try {
    await flush(); f.hidden(true); await flush(); assert.equal(f.audio.state.contextState, "suspended");
    const resumes = f.contexts[0].resumes; f.pause(true); f.pause(false); await flush(); assert.equal(f.contexts[0].resumes, resumes);
    f.hidden(false); await flush(); assert.equal(f.audio.state.contextState, "running");
  } finally { f.restore(); }
});
await test("dispose unsubscribes closes Web Audio and ignores late startup completion", async () => {
  const f = fixture({ defer: true });
  try {
    const context = f.contexts[0]; f.audio.dispose(); f.audio.dispose(); f.pending.shift()(); await flush();
    assert.equal(f.listeners.size, 0); assert.equal(context.state, "closed"); assert.equal(context.closes, 1);
    assert.equal(f.audio.state.status, "disposed"); assert.ok(context.nodes.every(node => node.disconnected));
    const resumes = context.resumes; f.pause(false); f.hidden(false); await flush(); assert.equal(context.resumes, resumes);
  } finally { f.restore(); }
});
await test("dispose stops HTML audio even if an earlier play completes afterward", async () => {
  const f = fixture({ local: true, defer: true });
  try {
    const element = f.elements[0]; f.audio.dispose(); f.pending.shift()(); await flush();
    assert.equal(element.paused, true); assert.equal(element.muted, true); assert.equal(f.listeners.size, 0); assert.equal(f.audio.state.status, "disposed");
  } finally { f.restore(); }
});
await test("off mode subscribes to no audio lifecycle and allocates no resources", async () => {
  const f = fixture({ sound: "off" });
  try { await flush(); assert.equal(f.listeners.size, 0); assert.equal(f.contexts.length, 0); assert.equal(f.elements.length, 0); assert.equal(f.audio.state.status, "off"); }
  finally { f.restore(); }
});
if (!passed && !failures) { console.error("No matching UC3D audio tests ran"); failures++; }
console.log(`UC3D audio tests: ${passed} passed, ${failures} failed`);
if (failures) process.exitCode = 1;
