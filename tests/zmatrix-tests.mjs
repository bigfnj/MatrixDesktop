import assert from "node:assert/strict";
import { pathToFileURL } from "node:url";
import path from "node:path";
import { readFileSync } from "node:fs";
import { createHash } from "node:crypto";
const root = process.env.ZMATRIX_TEST_ROOT ? pathToFileURL(path.resolve(process.env.ZMATRIX_TEST_ROOT) + path.sep)
  : new URL("../MatrixDesktop/web/js/zmatrix/", import.meta.url);
const { zmatrixDefaults, zmatrixParamMapping, normalizeZMatrixSettings, normalizeFont, parseByteColor } = await import(new URL("settings.js", root));
const { createRandom, createScene, streamColor, cleanupCell } = await import(new URL("scene.js", root));
const { createSurface, fitSurface, rasterOperation } = await import(new URL("surface.js", root));
const { validateNativeFont, decodeImage, loadNativeFont } = await import(new URL("fonts.js", root));
const { createResourceSlot, createRuntime, startLoop } = await import(new URL("runtime.js", root));
const { createProgram } = await import(new URL("gpu.js", root));
console.log(`SOURCE ${new URL("scene.js", root).pathname}`);
for (const file of ["settings.js", "scene.js", "surface.js", "fonts.js", "runtime.js", "gpu.js"])
  console.log(`WITNESS ${file} ${createHash("sha256").update(readFileSync(new URL(file, root))).digest("hex")}`);
const only = process.argv.indexOf("--only");
const filter = only >= 0 ? new RegExp(process.argv[only + 1]) : null;
let passed = 0, failures = 0;
async function test(name, run) { if (filter && !filter.test(name)) return; try { await run(); passed++; console.log(`PASS ${name}`); }
  catch (error) { failures++; console.error(`FAIL ${name}: ${error.stack}`); } }
const basic = options => createScene({ zmMaxStream: 1, zmSpeedVariance: 0, zmSpecialStringProbability: 0, ...options },
  { seed: 123, width: 100, height: 100, metrics: { normalHeight: 10, specialHeight: 6, columnWidth: 5 } });
const glyphs = (coverage = [255, 255, 255, 255]) => ({ normal: { glyphs: new Map([["A", { width: 1, height: 1,
  offsetX: 0, offsetY: 0, pixels: new Uint8ClampedArray(coverage), channels: 4, scale: 1 }]]) } });
const glyph = { type: "glyph", x: 0, y: 0, width: 2, height: 1, anchorX: 0, text: "A", font: "normal", color: [204, 51, 15] };
const wallpaper = new Uint8ClampedArray([85, 170, 240, 255, 85, 170, 240, 255]);

await test("settings expose all27 controls and isolate mutable defaults", () => {
  assert.equal(Object.keys(zmatrixParamMapping).length, 27);
  const first = normalizeZMatrixSettings(), second = normalizeZMatrixSettings();
  first.zmCharacters.pop(); first.zmFont.name = "changed";
  assert.equal(second.zmCharacters.length, 40); assert.equal(second.zmFont.name, "Matrix Code Font");
  assert.equal(zmatrixDefaults.zmMaxStream, 1000); assert.equal(zmatrixDefaults.zmRefreshTime, 50);
});
await test("settings reject nonfinite and truncate bounded integers", () => {
  const warnings = [], config = normalizeZMatrixSettings({ zmMaxStream: Infinity, zmRefreshTime: "-3", zmLeading: "4.9",
    zmPhrases: '[]', zmCharacters: '[]', zmRedGain: "NaN" }, warnings);
  assert.equal(config.zmMaxStream, 1000); assert.equal(config.zmRefreshTime, 10); assert.equal(config.zmLeading, 4);
  assert.deepEqual(config.zmPhrases, []); assert.equal(config.zmCharacters.length, 40); assert.equal(config.zmRedGain, 1);
  assert.deepEqual(warnings, ["Invalid zmMaxStream; using its default.", "Invalid zmCharacters; using its default.", "Invalid zmRedGain; using its default."]);
});
await test("byteRGB preserves exact legacy channels and is separate from normalized colors", () => {
  assert.deepEqual(parseByteColor("#bbffbb"), [187, 255, 187]); assert.deepEqual(parseByteColor("187,255,187"), [187, 255, 187]);
  assert.deepEqual(parseByteColor({ r: 2.5, g: -1, b: 300 }), [3, 0, 255]); assert.equal(parseByteColor("NaN,2,3"), null);
});
await test("font descriptors validate every field and preserve allfunctional values", () => {
  const font = normalizeFont({ height: -25.9, width: 9, escapement: -900, orientation: 1800, weight: 700, italic: true,
    underline: true, strikeOut: true, charSet: 128, outPrecision: 4, clipPrecision: 2, quality: 5, pitch: 2, family: 0x31, name: " Arial " });
  assert.deepEqual(font, { height: -25, width: 9, escapement: -900, orientation: 1800, weight: 700, italic: true,
    underline: true, strikeOut: true, charSet: 128, outPrecision: 4, clipPrecision: 2, quality: 5, pitch: 2, family: 0x30, name: "Arial" });
  assert.equal(normalizeFont({ name: "bad\nfont" }), null); assert.equal(normalizeFont({ height: NaN }), null);
});
await test("JSON lists retain phrase delimiters and emptylist disables phrases", () => {
  const c = normalizeZMatrixSettings({ zmCharacters: '["A",",","A"]', zmPhrases: '["a;b|c","word,word","a;b|c"]' });
  assert.deepEqual(c.zmCharacters, ["A", ","]); assert.deepEqual(c.zmPhrases, ["a;b|c", "word,word"]);
  assert.deepEqual(normalizeZMatrixSettings({ zmPhrases: [] }).zmPhrases, []);
});
await test("seeded PRNG produces deterministic bounded independent instances", () => {
  const a = createRandom(123), b = createRandom(123), c = createRandom(124);
  const aa = Array.from({ length: 100 }, () => a.next()), bb = Array.from({ length: 100 }, () => b.next());
  assert.deepEqual(aa, bb); assert.notEqual(aa[0], c.next());
  for (let i = 0; i < 1000; i++) { const n = a.range(6); assert.ok(n >= 0 && n <= 5); assert.ok(a.unit() < 1); }
});
await test("startup spawns only one stream per native tick", () => {
  const scene = basic({ zmMaxStream: 20 });
  assert.equal(scene.snapshot().activeStreams, 0); scene.tick(); assert.equal(scene.snapshot().activeStreams, 1);
  scene.tick(); assert.equal(scene.snapshot().activeStreams, 2); scene.advance(150); assert.equal(scene.snapshot().activeStreams, 5);
  scene.dispose();
});
await test("fixed native clock agrees across presentation cadence and drops long debt", () => {
  const a = basic({ zmMaxStream: 30 }), b = basic({ zmMaxStream: 30 });
  for (let i = 0; i < 300; i++) a.advance(10);
  for (let i = 0; i < 90; i++) b.advance(100 / 3);
  assert.deepEqual(a.snapshot(), b.snapshot());
  const before = a.snapshot().ticks; a.advance(50000); assert.equal(a.snapshot().ticks - before, 20);
  assert.equal(a.snapshot().droppedMs, 49000); a.advance(0); assert.equal(a.snapshot().ticks - before, 20);
});
await test("stream waiting uses waitplusone native ticks per movement", () => {
  let found = false;
  for (let seed = 1; seed < 100 && !found; seed++) {
    const scene = createScene({ zmMaxStream: 1, zmSpeedVariance: 5, zmSpecialStringProbability: 0 }, { seed, width: 100, height: 1000 });
    scene.tick(); const s = scene.snapshot().streams[0];
    if (s.wait < 2) continue;
    const positions = [];
    for (let i = 0; i < 3 * (s.wait + 1); i++) { scene.tick(); positions.push(scene.snapshot().streams[0].y); }
    const changes = positions.flatMap((y, i) => i > 0 && y !== positions[i - 1] ? [i] : []);
    assert.ok(changes.length >= 2); assert.equal(changes[1] - changes[0], s.wait + 1); found = true;
  }
  assert.equal(found, true);
});
await test("brightness is speedlinked integer math then gain offset clamp", () => {
  assert.deepEqual(streamColor([187, 255, 187], 5, 5, [1, 1, 1], [0, 0, 0]), [32, 43, 32]);
  assert.deepEqual(streamColor([100, 20, 255], 0, 5, [-1, 2, 4], [255, -50, 0]), [155, 0, 255]);
});
await test("previous head is randomized again rather than retaining its old symbol", () => {
  const scene = basic({ zmCharacters: ["A", "B", "C", "D", "E", "F"] }); let oldHead, changed = false;
  for (let i = 0; i < 8; i++) { const ops = []; scene.tick(op => ops.push(op));
    if (oldHead && ops[1].text !== oldHead) changed = true; oldHead = ops[0].text; }
  assert.equal(changed, true);
});
await test("cleanup modes are independent maycombine and disabled retainhistory", () => {
  const off = []; basic().tick(o => off.push(o)); assert.equal(off.length, 2);
  const fixed = []; basic({ zmMonotonousCleanupEnabled: true, zmBackTrace: 4 }).tick(o => fixed.push(o));
  assert.deepEqual(fixed.at(-1), { type: "clear", x: fixed[0].x, y: -30, width: 6, height: 11, distance: 4 });
  const both = []; basic({ zmMonotonousCleanupEnabled: true, zmRandomizedCleanupEnabled: true, zmLeading: 10, zmSpacePad: 0 }).tick(o => both.push(o));
  assert.equal(both.length, 4); assert.equal(both[2].distance, 10); assert.equal(both[3].distance, 40);
});
await test("random cleanup includes both Leading and SpacePad endpoints", () => {
  const scene = basic({ zmRandomizedCleanupEnabled: true, zmLeading: 3, zmSpacePad: 2 });
  const distances = new Set(); for (let i = 0; i < 200; i++) scene.tick(op => { if (op.type === "clear") distances.add(op.distance); });
  assert.deepEqual([...distances].sort(), [3, 4, 5]);
});
await test("special phrase moves vertically with separate font and returns to random", () => {
  const scene = createScene({ zmMaxStream: 1, zmSpeedVariance: 0, zmSpecialStringProbability: 1, zmPhrases: ["AB"] },
    { seed: 1, width: 100, height: 1, metrics: { normalHeight: 10, specialHeight: 6, columnWidth: 5 } });
  // Cleanup extends lifetime so this tiny viewport can exercise all transitions.
  const extended = createScene({ ...scene.config, zmMonotonousCleanupEnabled: true, zmBackTrace: 20 },
    { seed: 1, width: 100, height: 1, metrics: { normalHeight: 10, specialHeight: 6, columnWidth: 5 } });
  const heads = []; for (let i = 0; i < 4; i++) extended.tick(op => { if (op.type === "glyph" && !op.previous) heads.push(op); });
  assert.deepEqual(heads.map(o => [o.y, o.font]), [[10, "special"], [16, "special"], [22, "normal"], [32, "normal"]]);
  assert.deepEqual(heads.slice(0, 2).map(o => o.text), ["A", "B"]);
});
await test("mixedfont cleanup measures original glyph heights", () => {
  assert.equal(cleanupCell({ x: 3, y: 100, phraseIndex: 2 }, 3, 10, 6, 4, 5).y, 78);
  assert.equal(cleanupCell({ x: 3, y: 100, phraseIndex: 7 }, 4, 10, 6, 4, 5).y, 64);
  assert.equal(cleanupCell({ x: 3, y: 100, phraseIndex: 7 }, 8, 10, 6, 4, 5).y, 36);
});
await test("streams continue below viewport until cleanup tail completes", () => {
  const a = basic(), b = basic({ zmMonotonousCleanupEnabled: true, zmBackTrace: 5 });
  for (let i = 0; i < 12; i++) { a.tick(); b.tick(); }
  assert.equal(a.snapshot().activeStreams, 0); assert.equal(b.snapshot().activeStreams, 1); assert.equal(b.snapshot().streams[0].y, 120);
});
await test("scene disposal releases streams and blocks further work", () => {
  const scene = basic(); scene.tick(); scene.dispose(); scene.dispose(); assert.deepEqual(scene.snapshot(), { disposed: true }); assert.equal(scene.tick(), 0);
});
await test("surface bounds guard allocation and preserve aspect when reducing", () => {
  assert.throws(() => createSurface(8193, 1), /pixel budget/); assert.throws(() => createSurface(8192, 8192), /pixel budget/);
  const size = fitSurface(16000, 8000); assert.equal(size.width / size.height, 2); assert.ok(size.width * size.height <= 16777216);
});
await test("bytewise raster operations differ from PorterDuff xor", () => {
  assert.equal(rasterOperation(85, 204, "xor"), 153); assert.equal(rasterOperation(85, 204, "and"), 68); assert.equal(rasterOperation(85, 204, "or"), 221);
});
await test("transparent bitmap AND and OR are direct overlays while XOR clears cellspace", () => {
  for (const mode of ["and", "or", "xor"]) {
    const surface = createSurface(2, 1, { zmBlendMode: mode }); surface.setBackground(wallpaper); surface.apply(glyph, glyphs());
    assert.deepEqual(surface.snapshot([[0, 0], [1, 0]]), mode === "xor" ? [[153, 153, 255, 255], [0, 0, 0, 255]]
      : [[204, 51, 15, 255], [85, 170, 240, 255]]);
  }
});
await test("opaque bitmap implements allthree literal raster operation results", () => {
  const expected = { xor: [[153, 153, 255, 255], [84, 168, 244, 255]], and: [[68, 34, 0, 255], [1, 2, 0, 255]], or: [[221, 187, 255, 255], [85, 170, 244, 255]] };
  for (const mode of ["xor", "and", "or"]) {
    const surface = createSurface(2, 1, { zmBlendMode: mode, zmOpaqueBackground: true, zmBackgroundColor: [1, 2, 4] });
    surface.setBackground(wallpaper); surface.apply(glyph, glyphs()); assert.deepEqual(surface.snapshot([[0, 0], [1, 0]]), expected[mode]);
  }
});
await test("color mode ignores blend selector and retains transparent outsideink pixels", () => {
  for (const mode of ["xor", "and", "or"]) {
    const surface = createSurface(2, 1, { zmBlendMode: mode, zmBackgroundMode: "color", zmBackgroundColor: [10, 20, 30] });
    surface.apply(glyph, glyphs()); assert.deepEqual(surface.snapshot([[0, 0], [1, 0]]), [[204, 51, 15, 255], [10, 20, 30, 255]]);
  }
});
await test("atlas retains RGB subpixel coverage rather than scalar alpha", () => {
  const surface = createSurface(2, 1, { zmBackgroundMode: "color" });
  surface.apply({ ...glyph, color: [200, 200, 200] }, glyphs([255, 128, 0, 255]));
  assert.deepEqual(surface.snapshot([[0, 0]])[0], [200, 100, 0, 255]);
});
await test("cleanup restores only requested cell from original bitmap", () => {
  const surface = createSurface(2, 1); surface.setBackground(wallpaper); surface.apply(glyph, glyphs());
  surface.apply({ type: "clear", x: 0, y: 0, width: 1, height: 1 }); assert.deepEqual(surface.snapshot([[0, 0], [1, 0]]), [[85, 170, 240, 255], [85, 170, 240, 255]]);
});
await test("surface clipping and disposal keep allocations bounded", () => {
  const surface = createSurface(2, 1); surface.consumeDirty(); const revision = surface.revision;
  surface.apply({ type: "clear", x: -100, y: -100, width: 2, height: 2 }); assert.equal(surface.revision, revision);
  assert.equal(surface.consumeDirty(), null); assert.equal(surface.byteLength, 16); surface.dispose(); surface.dispose();
  assert.equal(surface.byteLength, 0); assert.equal(surface.pixels, null); assert.deepEqual(surface.snapshot([[0, 0]]), []);
});
const atlas = () => ({ atlasDpi: 144, coverageMode: "rgb", metrics: { height: 10, maxCharWidth: 5 },
  pages: [{ url: "fixture.png", width: 2, height: 1 }],
  glyphs: [{ text: "A", page: 0, x: 0, y: 0, width: 1, height: 1, offsetX: -2, offsetY: 0 }], resolvedFace: "Fixture" });
await test("native atlas validates pages metrics rectangles and full repertoire before decoding", () => {
  assert.equal(validateNativeFont(atlas(), ["A"]).dpi, 144);
  assert.throws(() => validateNativeFont({ ...atlas(), atlasDpi: NaN }, ["A"]), /metrics/);
  assert.throws(() => validateNativeFont({ ...atlas(), pages: [{ url: "bad", width: 4096, height: 1 }] }, ["A"]), /page/);
  assert.throws(() => validateNativeFont({ ...atlas(), glyphs: [{ ...atlas().glyphs[0], x: 2 }] }, ["A"]), /rectangle/);
  assert.throws(() => validateNativeFont(atlas(), ["A", "B"]), /omitted requested characters/);
});
await test("overlapping atlas glyphs cannot multiply cropped memory beyond budget", () => {
  const metadata = { ...atlas(), pages: [{ url: "fixture", width: 512, height: 512 }], glyphs: Array.from({ length: 65 }, (_, i) =>
    ({ ...atlas().glyphs[0], text: String.fromCharCode(32 + i), width: 512, height: 512 })) };
  assert.throws(() => validateNativeFont(metadata, []), /glyphs exceed their pixel budget/);
});
await test("native atlas decodes RGB coverage with logical offsets and releases images", async () => {
  const saved = { fetch: globalThis.fetch, createImageBitmap: globalThis.createImageBitmap, document: globalThis.document };
  let closed = 0;
  const pagePixels = new Uint8ClampedArray([255, 128, 0, 255, 0, 0, 0, 0]);
  try {
    globalThis.fetch = async () => ({ ok: true, blob: async () => ({}) });
    globalThis.createImageBitmap = async () => ({ width: 2, height: 1, close: () => closed++ });
    globalThis.document = { createElement: () => ({ getContext: () => ({ drawImage() {}, getImageData: () => ({ data: pagePixels }) }) }) };
    const font = await loadNativeFont(atlas(), ["A"], new AbortController().signal);
    assert.equal(closed, 1); assert.equal(font.source, "native-gdi"); assert.equal(font.glyphs.get("A").scale, 2 / 3);
    assert.equal(font.glyphs.get("A").offsetX, -2); assert.deepEqual([...font.glyphs.get("A").pixels], [255, 128, 0, 255]);
    font.dispose(); assert.equal(font.glyphs.size, 0);
  } finally { Object.assign(globalThis, saved); }
});
await test("late bitmap completion after abort closes its owned image", async () => {
  const saved = { fetch: globalThis.fetch, createImageBitmap: globalThis.createImageBitmap };
  const controller = new AbortController(); let finish, closed = 0;
  try {
    globalThis.fetch = async () => ({ ok: true, blob: async () => ({}) });
    globalThis.createImageBitmap = () => new Promise(resolve => { finish = resolve; });
    const pending = decodeImage("fixture", controller.signal);
    for (let i = 0; i < 10 && !finish; i++) await Promise.resolve();
    assert.equal(typeof finish, "function"); controller.abort(); finish({ close: () => closed++ });
    await assert.rejects(pending, /canceled/); assert.equal(closed, 1);
  } finally { Object.assign(globalThis, saved); }
});
await test("resource generation race installs newest and disposes obsolete owned result", async () => {
  const slot = createResourceSlot(); let completeOld, completeNew, oldClosed = 0, newClosed = 0; const installed = [];
  const old = slot.replace(() => new Promise(resolve => { completeOld = resolve; }), value => installed.push(value.id));
  const current = slot.replace(() => new Promise(resolve => { completeNew = resolve; }), value => installed.push(value.id));
  completeNew({ id: "new", dispose: () => newClosed++ }); assert.equal(await current, true);
  completeOld({ id: "old", dispose: () => oldClosed++ }); assert.equal(await old, false);
  assert.deepEqual(installed, ["new"]); assert.equal(oldClosed, 1); assert.equal(newClosed, 0);
  slot.dispose(); slot.dispose(); assert.equal(newClosed, 1);
});
await test("resource disposal during initialization never publishes late result", async () => {
  const slot = createResourceSlot(); let finish, closed = 0, installed = 0;
  const pending = slot.replace(() => new Promise(resolve => { finish = resolve; }), () => installed++);
  slot.dispose(); finish({ dispose: () => closed++ }); assert.equal(await pending, false); assert.equal(installed, 0); assert.equal(closed, 1);
});
await test("runtime startup observes resource changes and waits for the newest atlas", async () => {
  const saved = { window: globalThis.window, document: globalThis.document, fetch: globalThis.fetch, createImageBitmap: globalThis.createImageBitmap };
  const events = new EventTarget(), releases = new Map(), requests = [], seen = new Set();
  let runtime = null, pending = null, completed = false, closed = 0;
  const metadata = generation => ({ nativeGeneration: generation, zmatrixFonts: { generation,
    normal: { atlasDpi: 96, pages: [{ url: `font-${generation}`, width: 1, height: 1 }], metrics: { height: generation, maxCharWidth: 1 },
      glyphs: [{ text: "A", page: 0, x: 0, y: 0, width: 1, height: 1, offsetX: 0, offsetY: 0 }] },
    special: { atlasDpi: 96, pages: [{ url: `font-${generation}`, width: 1, height: 1 }], metrics: { height: 1, maxCharWidth: 1 }, glyphs: [] } } });
  const publish = generation => {
    events.matrixHostBootstrap = metadata(generation);
    const event = new Event("matrix-native-resources-updated"); event.detail = events.matrixHostBootstrap; events.dispatchEvent(event);
  };
  try {
    globalThis.window = events; events.matrixHostBootstrap = metadata(1);
    globalThis.document = { body: { appendChild() {} }, createElement: type => type === "div" ? { style: {}, remove() {} }
      : { getContext: () => ({ drawImage() {}, getImageData: () => ({ data: new Uint8ClampedArray([255, 255, 255, 255]) }) }) } };
    globalThis.fetch = url => {
      requests.push(url); const response = { ok: true, blob: async () => ({}) };
      if (seen.has(url)) return Promise.resolve(response);
      seen.add(url); return new Promise(resolve => releases.set(url, () => resolve(response)));
    };
    globalThis.createImageBitmap = async () => ({ width: 1, height: 1, close: () => closed++ });
    pending = createRuntime({ dataset: {} }, { zmCharacters: ["A"], zmPhrases: [], zmBackgroundMode: "color" }, "test")
      .then(value => { completed = true; runtime = value; return value; });
    publish(2);
    assert.equal(typeof releases.get("font-2"), "function", "A host update during initial decode must start the newest atlas load");
    releases.get("font-1")();
    for (let i = 0; i < 20; i++) await Promise.resolve();
    assert.equal(completed, false, "Initialization must wait for the current generation instead of returning with missing resources");
    releases.get("font-2")(); await pending;
    assert.equal(runtime.diagnostics.resourceGeneration, 2); assert.equal(runtime.diagnostics.nativeFonts.normal.metrics.height, 2);
    assert.equal(runtime.diagnostics.nativeFonts.normal.source, "native-gdi"); assert.equal(closed, 2);
    runtime.dispose(); const before = requests.length; publish(3); assert.equal(requests.length, before, "Disposed runtime must unsubscribe from host updates");
  } finally {
    for (const release of releases.values()) release();
    try { await pending; } finally { runtime?.dispose(); Object.assign(globalThis, saved); }
  }
});
await test("resource install failure releases new and previous generations once", async () => {
  const slot = createResourceSlot(); let closed = 0;
  await slot.replace(async () => ({ dispose: () => closed++ }), () => {});
  await assert.rejects(slot.replace(async () => ({ dispose: () => closed++ }), () => { throw new Error("install"); }), /install/);
  assert.equal(closed, 2); slot.dispose(); assert.equal(closed, 2);
});
function mockLoop(config, draw = () => {}) {
  const saved = { window: globalThis.window, requestAnimationFrame: globalThis.requestAnimationFrame, cancelAnimationFrame: globalThis.cancelAnimationFrame };
  const callbacks = new Map(), listeners = new Map(); let serial = 0, disposed = 0, cleaned = 0, clockDisposed = 0;
  globalThis.window = { addEventListener: (name, listener) => { if (!listeners.has(name)) listeners.set(name, new Set()); listeners.get(name).add(listener); },
    removeEventListener: (name, listener) => listeners.get(name)?.delete(listener) };
  globalThis.requestAnimationFrame = fn => { callbacks.set(++serial, fn); return serial; };
  globalThis.cancelAnimationFrame = id => callbacks.delete(id);
  const updates = [], steps = [], clock = { paused: false, value: 0, seconds() { return this.value; }, dispose: () => clockDisposed++ };
  const runtime = { config: { fps: 60, zmRefreshTime: 50, ...config }, diagnostics: { errors: [], frames: 0, inspect: () => ({ ticks: steps.reduce((a, b) => a + b, 0) }) },
    update: seconds => { updates.push(seconds); return { revision: updates.length }; }, advanceTicks: n => { steps.push(n); return { revision: 1000 + n }; }, warn() {}, dispose: () => disposed++ };
  const stop = startLoop({}, runtime, draw, () => cleaned++, { clock });
  return { runtime, updates, steps, clock, callbacks, listeners, stop,
    run(timestamp) { const [id, fn] = callbacks.entries().next().value; callbacks.delete(id); fn(timestamp); },
    counts: () => ({ disposed, cleaned, clockDisposed }), restore() { stop(); Object.assign(globalThis, saved); } };
}
await test("once mode warms bounded ticks and stops RAF while preserving frame", () => {
  const loop = mockLoop({ once: true });
  try { loop.run(0); assert.deepEqual(loop.steps, [100, 100]); assert.equal(loop.runtime.diagnostics.warmupTicks, 200);
    assert.equal(loop.callbacks.size, 0); assert.equal(loop.runtime.diagnostics.ready, true); assert.equal(loop.runtime.diagnostics.frames, 1);
    loop.stop(); loop.stop(); assert.deepEqual(loop.counts(), { disposed: 1, cleaned: 1, clockDisposed: 1 }); assert.equal(loop.listeners.get("pagehide").size, 0);
  } finally { loop.restore(); }
});
await test("host paused loop does not simulate present or accumulate inactive clocktime", () => {
  const loop = mockLoop({});
  try { loop.clock.paused = true; loop.run(0); assert.deepEqual(loop.updates, []);
    loop.clock.paused = false; loop.clock.value = .5; loop.run(500); assert.deepEqual(loop.updates, [.5]);
    loop.clock.paused = true; loop.run(20000); assert.equal(loop.runtime.diagnostics.frames, 1);
    loop.clock.paused = false; loop.clock.value = .6; loop.run(21000); assert.deepEqual(loop.updates, [.5, .6]);
  } finally { loop.restore(); }
});
await test("explicit teardown cancels pending RAF and prevents future scheduling", () => {
  const loop = mockLoop({});
  try { loop.run(0); assert.equal(loop.callbacks.size, 1); loop.stop(); assert.equal(loop.callbacks.size, 0);
    assert.deepEqual(loop.counts(), { disposed: 1, cleaned: 1, clockDisposed: 1 });
  } finally { loop.restore(); }
});
await test("render failure stops RAF and releases runtime GPU and clock owners", () => {
  const oldError = console.error, loop = mockLoop({}, () => { throw new Error("synthetic render fault"); }); console.error = () => {};
  try { loop.run(0); assert.equal(loop.callbacks.size, 0); assert.deepEqual(loop.runtime.diagnostics.errors, ["synthetic render fault"]);
    assert.deepEqual(loop.counts(), { disposed: 1, cleaned: 1, clockDisposed: 1 });
  } finally { console.error = oldError; loop.restore(); }
});
await test("WebGL shader compile link and allocation failures release owned objects", () => {
  for (const fault of ["vertex", "fragment", "link", "allocation", null]) {
    const deletedShaders = [], deletedPrograms = []; let created = 0;
    const gl = { VERTEX_SHADER: 1, FRAGMENT_SHADER: 2, COMPILE_STATUS: 3, LINK_STATUS: 4,
      createProgram: () => fault === "allocation" ? null : {}, createShader: type => ({ type, id: ++created }), shaderSource() {}, compileShader() {},
      getShaderParameter: shader => !(fault === "vertex" && shader.type === 1 || fault === "fragment" && shader.type === 2),
      getShaderInfoLog: () => "synthetic shader fault", attachShader() {}, linkProgram() {}, getProgramParameter: () => fault !== "link",
      getProgramInfoLog: () => "synthetic link fault", deleteShader: shader => deletedShaders.push(shader), deleteProgram: program => deletedPrograms.push(program) };
    if (fault) assert.throws(() => createProgram(gl, "vertex", "fragment"), /ZMatrix/); else assert.ok(createProgram(gl, "vertex", "fragment"));
    assert.equal(deletedShaders.length, fault === "allocation" ? 0 : fault === "vertex" ? 1 : 2);
    assert.equal(deletedPrograms.length, !fault || fault === "allocation" ? 0 : 1);
  }
});
if (!passed && !failures) { console.error("No matching ZMatrix tests ran"); failures++; }
console.log(`ZMatrix tests: ${passed} passed, ${failures} failed`);
if (failures) process.exitCode = 1;
