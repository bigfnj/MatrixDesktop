import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { runInThisContext } from "node:vm";
import { fileURLToPath } from "node:url";
import createOwner, { ownDevice } from "../MatrixDesktop/web/js/resourceOwner.js";
import createSize, { fitInside } from "../MatrixDesktop/web/js/canvasSize.js";
import { wantsWebGPU } from "../MatrixDesktop/web/js/rendererChoice.js";
import makeConfig from "../MatrixDesktop/web/js/config.js";
import getLKG from "../MatrixDesktop/web/js/regl/lkgHelper.js";
import { makePipeline, loadTexture } from "../MatrixDesktop/web/js/webgpu/utils.js";
import { createShaderTime, rebaseLimits, shaderTick } from "../MatrixDesktop/web/js/shaderTime.js";
import createClickRipples from "../MatrixDesktop/web/js/clickRipples.js";

const root = new URL("../MatrixDesktop/web/", import.meta.url);
runInThisContext(await readFile(new URL("lib/gl-matrix.min.js", root), "utf8"));
globalThis.GPUBufferUsage = { UNIFORM: 1, COPY_DST: 2, STORAGE: 4 };
globalThis.GPUTextureUsage = { TEXTURE_BINDING: 1, COPY_DST: 2, RENDER_ATTACHMENT: 4, COPY_SRC: 8, STORAGE_BINDING: 16 };
globalThis.fetch = async (path) => ({
	text: () => readFile(new URL(path, root), "utf8"),
	blob: async () => ({}),
});
globalThis.createImageBitmap = async () => ({ width: 4, height: 4, close() {} });
globalThis.Image = class { width = 4; height = 4; decode() { return Promise.resolve(); } };
class Events {
	listeners = new Map();
	addEventListener(type, fn) { if (!this.listeners.has(type)) this.listeners.set(type, new Set()); this.listeners.get(type).add(fn); }
	removeEventListener(type, fn) { this.listeners.get(type)?.delete(fn); }
	fire(type) { for (const fn of [...(this.listeners.get(type) ?? [])]) fn(); }
	count() { return [...this.listeners.values()].reduce((n, set) => n + set.size, 0); }
}
globalThis.window = new Events();
window.devicePixelRatio = 1;
let mediaQueries = 0;
window.matchMedia = () => { mediaQueries++; return new Events(); };
globalThis.ResizeObserver = class {
	static latest;
	constructor(callback) { this.callback = callback; ResizeObserver.latest = this; }
	observe() {}
	disconnect() { this.disconnected = true; }
};
let passes = 0;
const test = async (name, fn) => {
	try { await fn(); passes++; console.log(`PASS  ${name}`); }
	catch (error) { console.error(`FAIL  ${name}: ${error.stack}`); process.exitCode = 1; }
	finally { window.listeners.clear(); window.devicePixelRatio = 1; }
};

await test("resource owner closes current and late handles exactly once", () => {
	const owner = createOwner();
	const make = () => ({ destroys: 0, destroy() { this.destroys++; } });
	const old = owner.own(make());
	owner.release(old); owner.release(old);
	const current = owner.own(make());
	owner.cleanup(); owner.cleanup();
	const late = owner.own(make());
	assert.deepEqual([old.destroys, current.destroys, late.destroys], [1, 1, 1]);
	assert.throws(() => ownDevice(owner, { createBuffer: make }).createBuffer({}), /Renderer closed/);
});

await test("canvas size observes CSS changes and DPR without frame layout reads", () => {
	let reads = 0, w = 101, h = 51;
	const canvas = { get clientWidth() { reads++; return w; }, get clientHeight() { reads++; return h; } };
	const queriesBefore = mediaQueries;
	const tracker = createSize(canvas, 0.5);
	// get() reads devicePixelRatio live, so a DPR media-query listener would only re-arm itself.
	assert.equal(mediaQueries - queriesBefore, 0, "canvas size must not register a DPR media query");
	assert.deepEqual(tracker.get(), [51, 26]);
	const initialReads = reads;
	for (let i = 0; i < 100; i++) tracker.get();
	assert.equal(reads, initialReads, "unchanged frames must not read layout");
	w = 200; h = 90; ResizeObserver.latest.callback();
	assert.deepEqual(tracker.get(), [100, 45], "CSS-only resize must update");
	window.devicePixelRatio = 1.25;
	assert.deepEqual(tracker.get(), [125, 57], "DPR change must update backing size");
	w = 0; h = 0; ResizeObserver.latest.callback();
	assert.deepEqual(tracker.get(), [1, 1]);
	w = 400; h = 200; ResizeObserver.latest.callback();
	assert.deepEqual(tracker.get(), [250, 125], "visible again must restore dimensions");
	tracker.cleanup(); tracker.cleanup();
	assert.equal(ResizeObserver.latest.disconnected, true);
	assert.equal(window.count(), 0);
	window.devicePixelRatio = 1;
});

await test("Holoplay uses REGL before any WebGPU adapter probe", () => {
	assert.equal(wantsWebGPU({ renderer: "webgpu", useHoloplay: true }), false);
	assert.equal(wantsWebGPU({ renderer: "WEBGPU", useHoloplay: false }), true);
	assert.equal(wantsWebGPU({ renderer: "regl", useHoloplay: false }), false);
});

await test("web boolean parser accepts exact aliases and rejects embedded true", () => {
	for (const text of ["", "true", "TRUE", "1", "y", "yes", " on "]) assert.equal(makeConfig({ camera: text, effect: "mirror" }).useCamera, true, text);
	for (const text of ["false", "0", "no", "off", "truthy", "untrue", "nottrue", "true-ish"]) assert.equal(makeConfig({ camera: text, effect: "mirror" }).useCamera, false, text);
});

await test("camera is requested only by the mirror effect", () => {
	assert.equal(makeConfig({ camera: "true", effect: "mirror" }).useCamera, true, "Mirror keeps its camera");
	for (const effect of ["palette", "stripes", "image", "none", "Mirror"]) assert.equal(makeConfig({ camera: "true", effect }).useCamera, false, effect);
	assert.equal(makeConfig({ camera: "true" }).useCamera, false, "the default palette effect must not open the camera");
});

await test("an intro that cannot progress is skipped instead of rendering nothing", () => {
	const warnings = [], warn = console.warn;
	console.warn = (message) => warnings.push(String(message));
	try {
		for (const speeds of [{ fallSpeed: "0" }, { animationSpeed: "0" }, { animationSpeed: "-1" }, { fallSpeed: "-0.3" }]) {
			const before = warnings.length;
			assert.equal(makeConfig({ skipIntro: "false", ...speeds }).skipIntro, true, JSON.stringify(speeds));
			assert.equal(warnings.length, before + 1, `${JSON.stringify(speeds)} must say why the intro was skipped`);
		}
		const before = warnings.length;
		assert.equal(makeConfig({ skipIntro: "false" }).skipIntro, false, "default speeds keep the intro");
		assert.equal(makeConfig({ skipIntro: "false", animationSpeed: "-1", fallSpeed: "-0.3" }).skipIntro, false, "two negative speeds still progress");
		assert.equal(makeConfig({ skipIntro: "false", scene: "uc3d", animationSpeed: "0" }).skipIntro, false, "UC3D has no intro sweep");
		assert.equal(warnings.length, before, "an intro that can progress must not warn");
	} finally { console.warn = warn; }
});

await test("inherited Object property names in the query are ignored like any unknown key", () => {
	const inherited = ["constructor", "toString", "hasOwnProperty", "valueOf", "__proto__"];
	const query = Object.fromEntries([["unknownKey", "1"], ...inherited.map((key) => [key, "1"]), ["numColumns", "17"], ["fallSpeed", "0.9"]]);
	assert.equal(Object.hasOwn(query, "__proto__"), true, "the probe must carry __proto__ as an own query key");
	const config = makeConfig(query);
	assert.equal(config.numColumns, 17); assert.equal(config.fallSpeed, 0.9);
	for (const key of ["unknownKey", ...inherited]) assert.equal(Object.hasOwn(config, key), false, key);
	assert.equal(Object.getPrototypeOf(config), Object.prototype);
	const plain = makeConfig({});
	for (const name of inherited) {
		assert.equal(makeConfig({ font: name }).glyphMSDFURL, plain.glyphMSDFURL, `font=${name} must fall back to the default glyph atlas`);
		assert.equal(makeConfig({ font: name, version: "megacity" }).glyphMSDFURL, "assets/megacity_msdf.png", `font=${name} must fall back to the version's font`);
	}
});

await test("WebGPU-to-REGL fallback draws on a fresh canvas and keeps the WebGPU error", async () => {
	const { default: loadRenderer } = await import("../MatrixDesktop/web/js/rendererLoader.js");
	const doc = { createElement: (tag) => makeCanvas(tag) };
	const makeCanvas = (tag = "canvas") => ({ tag, ownerDocument: doc, claimed: null, replacedBy: null,
		getContext(type) { if (this.claimed != null && this.claimed !== type) return null; this.claimed = type; return {}; },
		replaceWith(node) { this.replacedBy = node; } });
	const pipelineError = Object.assign(new Error("invalid WGSL in palettePass"), { name: "GPUPipelineError" });
	const webgpu = async () => ({ default: async (canvas) => { canvas.getContext("webgpu"); throw pipelineError; } });
	const warn = console.warn;
	console.warn = () => {};
	try {
		let drawnOn = null, stops = 0;
		const original = makeCanvas();
		const result = await loadRenderer(original, { scene: "matrix" }, true, { importWebGPU: webgpu, stopCamera: () => stops++,
			importRegl: async () => ({ default: async (canvas) => {
				assert.notEqual(canvas.getContext("webgl"), null, "REGL received a canvas WebGPU had already claimed");
				drawnOn = canvas; globalThis.matrixRuntimeDiagnostics = { backend: "regl" };
			} }) });
		assert.notEqual(drawnOn, original);
		assert.equal(drawnOn.tag, "canvas");
		assert.equal(original.replacedBy, drawnOn, "the fresh canvas must take the claimed canvas's place in the page");
		assert.equal(result, drawnOn);
		assert.equal(stops, 1);
		assert.equal(globalThis.matrixRuntimeDiagnostics.requestedRendererError, "invalid WGSL in palettePass");
		await assert.rejects(loadRenderer(makeCanvas(), { scene: "matrix" }, true, { importWebGPU: webgpu,
			importRegl: async () => ({ default: async () => { throw new Error("no WebGL"); } }) }),
			/no WebGL \(after WebGPU failed: invalid WGSL in palettePass\)/);
		const kept = makeCanvas();
		assert.equal(await loadRenderer(kept, { scene: "matrix" }, true, { importWebGPU: async () => ({ default: async () => {} }),
			importRegl: async () => { throw new Error("REGL must not load after WebGPU started"); } }), kept);
		await assert.rejects(loadRenderer(makeCanvas(), { scene: "uc3d" }, true, { importWebGPU: webgpu,
			importRegl: async () => { throw new Error("UC3D must not fall back"); } }), /invalid WGSL/);
	} finally {
		console.warn = warn;
		delete globalThis.matrixRuntimeDiagnostics;
	}
});

await test("a lost renderer reloads within a budget and then explains itself", async () => {
	const { default: install, RECOVERY_KEY } = await import("../MatrixDesktop/web/js/rendererRecovery.js");
	const run = ({ stamps = [], storage, time = 1_000_000 } = {}) => {
		const listeners = {}, timers = [], log = { reloads: 0, errors: [], prevented: 0 };
		const items = new Map([[RECOVERY_KEY, JSON.stringify(stamps)]]);
		const hook = install({
			target: { addEventListener: (type, fn, capture) => { assert.equal(capture, true, `${type} must be a capture listener`); listeners[type] = fn; } },
			storage: storage ?? (() => ({ getItem: (key) => items.get(key) ?? null, setItem: (key, value) => items.set(key, value) })),
			reload: () => log.reloads++, showError: (error) => log.errors.push(error.message), now: () => time,
			setTimer: (fn, ms) => { timers.push({ fn, ms, live: true }); return timers.length; },
			clearTimer: (id) => { if (id != null) timers[id - 1].live = false; },
		});
		const fire = (type) => listeners[type]?.({ preventDefault: () => log.prevented++ });
		return { hook, fire, timers, log, items };
	};
	let page = run();
	page.fire("webglcontextlost");
	assert.equal(page.log.prevented, 1, "the loss must ask the browser to restore the context");
	assert.equal(page.log.reloads, 0, "a WebGL loss waits for the restore");
	page.hook("WebGL context lost");
	assert.equal(page.log.reloads, 0, "a renderer's hook call during a pending WebGL loss must not reload early");
	page.fire("webglcontextrestored");
	assert.equal(page.log.reloads, 1, "a restored context must reload the page");
	assert.equal(page.timers[0].live, false, "the restore must cancel the no-restore timer");
	assert.deepEqual(JSON.parse(page.items.get(RECOVERY_KEY)), [1_000_000]);
	page.fire("webglcontextrestored"); page.hook("again");
	assert.equal(page.log.reloads, 1, "one loss reloads once");
	page = run();
	page.fire("webglcontextlost");
	assert.equal(page.timers[0].ms, 10000);
	page.timers[0].fn();
	assert.equal(page.log.reloads, 1, "no restore within the wait must still reload");
	page = run();
	page.hook("WebGPU device lost (unknown)");
	assert.equal(page.log.reloads, 1, "a WebGPU device loss reloads at once");
	page = run({ stamps: [999_000, 998_000, 997_000] });
	page.fire("webglcontextlost"); page.fire("webglcontextrestored");
	assert.equal(page.log.reloads, 0, "three recoveries in five minutes exhaust the budget");
	assert.match(page.log.errors[0] ?? "", /could not be recovered automatically/);
	page = run({ stamps: [999_000, 998_000, 1_000_000 - 5 * 60 * 1000] });
	page.hook("WebGPU device lost");
	assert.equal(page.log.reloads, 1, "a stamp older than five minutes no longer counts");
	page = run({ storage: () => { throw new Error("SecurityError"); } });
	page.hook("WebGPU device lost");
	assert.equal(page.log.reloads, 0, "without storage there is no budget, so no reload");
	assert.equal(page.log.errors.length, 1);
});

// R2-55: after repeated GPU resets Chromium refuses the page every WebGL context, and the
// recovery reload showed how REGL tripped over the null instead of the restart notice.
await test("a renderer the browser refuses a WebGL context ends in the restart notice", async () => {
	const { default: loadRenderer } = await import("../MatrixDesktop/web/js/rendererLoader.js");
	const { unrecoverable } = await import("../MatrixDesktop/web/js/rendererRecovery.js");
	const canvasIn = (refused) => {
		const doc = { createElement: () => ({ ownerDocument: doc, getContext: () => (refused ? null : {}), replaceWith() {} }) };
		return doc.createElement("canvas");
	};
	const startError = new Error("REGL could not start: webgl not supported");
	const importRegl = async () => ({ default: async () => { throw startError; } });
	const notice = unrecoverable("the browser refused a new WebGL context").message;
	assert.match(notice, /could not be recovered automatically\. Restart MatrixDesktop to try again\.$/);
	for (const scene of ["matrix", "uc3d", "zmatrix"]) {
		const error = await loadRenderer(canvasIn(true), { scene }, false, { importRegl }).catch((caught) => caught);
		assert.equal(error.message, notice, `${scene}: a refused context must end in the restart notice`);
		assert.equal(error.cause, startError, `${scene}: the renderer's own error must stay attached`);
	}
	await assert.rejects(loadRenderer(canvasIn(false), { scene: "matrix" }, false, { importRegl }), (error) => error === startError,
		"a renderer that fails while WebGL works must keep its own error");
	const warn = console.warn;
	console.warn = () => {};
	try {
		const importWebGPU = async () => ({ default: async () => { throw new Error("no WebGPU device"); } });
		const error = await loadRenderer(canvasIn(true), { scene: "matrix" }, true, { importWebGPU, importRegl }).catch((caught) => caught);
		assert.equal(error.message, `${notice} (after WebGPU failed: no WebGPU device)`);
	} finally { console.warn = warn; }
});

await test("config ignores inherited Object names as parameter keys and font names", () => {
	// URL keys and font names are looked up in plain object tables. With `in`, names every
	// object inherits matched too: ?constructor=1 threw before any renderer started, and
	// font=constructor spread Object into the config, leaving no glyph atlas URL. (AUD-79)
	const baseline = makeConfig({});
	for (const name of ["constructor", "toString", "__proto__", "hasOwnProperty", "valueOf"]) {
		assert.deepEqual(makeConfig({ [name]: "1" }), baseline, `${name} is not a parameter and must be ignored`);
		const font = makeConfig({ font: name });
		assert.equal(font.glyphMSDFURL, baseline.glyphMSDFURL, `font=${name} must fall back to the version's font`);
		assert.equal(font.glyphSequenceLength, baseline.glyphSequenceLength, `font=${name} must fall back to the version's font`);
	}
});

await test("Looking Glass missing, invalid, timeout and late responses never invent calibration", async () => {
	let disconnects = 0;
	const run = (behavior) => getLKG(true, { timeoutMs: 5, loadCore: async () => ({ Client: class {
		constructor(ok, error) { behavior(ok, error); }
		disconnect() { disconnects++; }
	} }) });
	for (const behavior of [
		(ok) => ok({ devices: [] }),
		(ok, error) => error(new Error("offline")),
		() => { throw new Error("constructor failed"); },
		() => {},
		(ok) => setTimeout(() => ok({ devices: [{ calibration: {}, defaultQuilt: {} }] }), 15),
		(ok) => ok({ devices: [{ calibration: {}, defaultQuilt: {} }] }),
	]) {
		const result = await run(behavior);
		assert.deepEqual(result, { enabled: false, tileX: 1, tileY: 1 });
	}
	assert.equal(disconnects, 5);
	const values = { DPI: 300, screenW: 1000, screenH: 1500, pitch: 50, slope: -7, center: 0.15, invView: 1, viewCone: 40 };
	const valid = { calibration: Object.fromEntries(Object.entries(values).map(([key, value]) => [key, { value }])), defaultQuilt: { tileX: 8, tileY: 6, quiltX: 3840, quiltY: 3840, quiltAspect: 0.75 } };
	const found = await run((ok) => ok({ devices: [valid] }));
	assert.equal(found.enabled, true);
	assert.equal(found.tileX, 8);
	assert(Number.isFinite(found.pitch));
	assert.equal((await getLKG(true, { loadCore: async () => { throw new Error("module missing"); } })).enabled, false);
});

class BufferHandle {
	constructor(config, resources) { this.data = new ArrayBuffer(config.size ?? 16); resources.add(this); }
	getMappedRange() { return this.data; }
	unmap() {}
	destroy() { assert(!this.dead, "buffer destroyed twice"); this.dead = true; }
}
globalThis.GPUBuffer = BufferHandle;
const makeDevice = () => {
	const resources = new Set();
	const pipeline = () => ({ getBindGroupLayout: () => ({}) });
	return {
		resources,
		createBuffer(config) { return new BufferHandle(config, resources); },
		createTexture(config) {
			const tex = { createView() { assert(!this.dead, "view of destroyed texture"); return {}; }, destroy() { assert(!this.dead, "texture destroyed twice"); this.dead = true; } };
			resources.add(tex); return tex;
		},
		createSampler: () => ({}), createShaderModule: () => ({}), createBindGroup: () => ({}),
		createComputePipeline: pipeline, createComputePipelineAsync: async () => pipeline(), createRenderPipelineAsync: async () => pipeline(),
		queue: { writeBuffer() {}, writeTexture() {}, copyExternalImageToTexture() {} },
	};
};
const encoder = { beginComputePass: () => ({ setPipeline() {}, setBindGroup() {}, dispatchWorkgroups() {}, end() {} }), beginRenderPass: () => ({ setPipeline() {}, setBindGroup() {}, draw() {}, end() {} }) };

for (const name of ["rain", "bloom", "palette", "stripe", "image", "mirror", "end"]) {
	await test(`WebGPU ${name} pass releases owned resources across resize and double cleanup`, async () => {
		const device = makeDevice();
		const borrowed = { createView: () => ({}), destroy() { throw new Error("borrowed texture destroyed"); } };
		const canvas = new Events(); canvas.getBoundingClientRect = () => ({ left: 0, top: 0, width: 320, height: 200 });
		const context = { config: makeConfig({ numColumns: "4", camera: "false" }), device, timeBuffer: {}, cameraTex: borrowed, cameraAspectRatio: 1, canvas, elapsedSeconds: () => 1, canvasFormat: "rgba8unorm", canvasContext: { getCurrentTexture: () => borrowed } };
		const { default: make } = await import(`../MatrixDesktop/web/js/webgpu/${name}Pass.js`);
		const pass = make(context);
		await pass.loaded;
		pass.build([320, 200], { primary: borrowed, bloom: borrowed, highPass: borrowed });
		pass.run(encoder, true);
		pass.build([200, 320], { primary: borrowed, bloom: borrowed, highPass: borrowed });
		pass.run(encoder, true);
		pass.cleanup(); pass.cleanup();
		assert.equal([...device.resources].filter((r) => !r.dead).length, 0, `${name} leaked an owned GPU resource`);
		assert.equal(canvas.count(), 0);
		assert.equal(window.count(), 0);
	});
}

await test("WebGPU disabled bloom placeholder is released", async () => {
	const device = makeDevice();
	const { default: make } = await import("../MatrixDesktop/web/js/webgpu/bloomPass.js");
	const pass = make({ device, config: { bloomSize: 0, bloomStrength: 0 } });
	pass.cleanup(); pass.cleanup();
	assert.equal([...device.resources].filter((r) => !r.dead).length, 0);
});

await test("failed WebGPU pipeline initialization cleans sibling passes", async () => {
	let cleaned = 0;
	await assert.rejects(makePipeline({}, [() => ({ loaded: Promise.resolve(), cleanup() { cleaned++; } }), () => ({ loaded: Promise.reject(new Error("broken shader")), cleanup() { cleaned++; } })]), /broken shader/);
	assert.equal(cleaned, 2);
	cleaned = 0;
	await assert.rejects(makePipeline({}, [() => ({ loaded: Promise.resolve(), cleanup() { cleaned++; } }), () => { throw new Error("factory failed"); }]), /factory failed/);
	assert.equal(cleaned, 1);
});

// AUD-21: effect=image must start without its background image. The fetch, Image and
// console.warn stubs below replace the suite's for one pass and are restored afterwards.
const runImagePass = async (backend, query, { offline = false, closeEarly = false } = {}) => {
	const config = makeConfig({ effect: "image", ...query });
	const saved = { fetch: globalThis.fetch, Image: globalThis.Image, warn: console.warn };
	const run = { requested: [], warnings: [], writes: [], diagnostics: { errors: [] } };
	const gate = closeEarly ? new Promise((resolve) => { run.open = resolve; }) : Promise.resolve();
	globalThis.fetch = async (path) => {
		if (String(path).startsWith("shaders/")) return saved.fetch(path);
		run.requested.push(path);
		await gate;
		if (offline) throw new TypeError("Failed to fetch");
		return { blob: async () => ({}) };
	};
	globalThis.Image = class { async decode() { run.requested.push(this.src); await gate; if (offline) throw new Error("The source image cannot be decoded."); } };
	console.warn = (message) => run.warnings.push(String(message));
	globalThis.matrixRuntimeDiagnostics = run.diagnostics;
	try {
		if (backend === "WebGPU") {
			const device = makeDevice(), borrowed = { createView: () => ({}) };
			device.queue = { ...device.queue, writeTexture: (target, data) => run.writes.push([...data]) };
			const { default: make } = await import("../MatrixDesktop/web/js/webgpu/imagePass.js");
			const pass = make({ config, device });
			if (closeEarly) { pass.cleanup(); run.open(); await assert.rejects(pass.loaded, /Failed to fetch/); return run; }
			await pass.loaded;
			pass.build([4, 4], { primary: borrowed, bloom: borrowed });
			pass.run(encoder, true);
			pass.cleanup();
		} else {
			const commands = [], handle = (data) => Object.assign(() => {}, { data, resize() {}, destroy() {} });
			const regl = Object.assign((command) => { commands.push(command); return () => {}; }, { texture: handle, framebuffer: handle, prop: (p) => p });
			const { default: make } = await import("../MatrixDesktop/web/js/regl/imagePass.js");
			const pass = make({ regl, config }, { primary: {}, bloom: {} });
			if (closeEarly) { pass.cleanup(); run.open(); await assert.rejects(pass.ready, /cannot be decoded/); return run; }
			await pass.ready;
			pass.setSize(4, 4); pass.execute(true);
			run.bound = commands[0].uniforms.backgroundTex();
			pass.cleanup();
		}
		return run;
	} finally {
		globalThis.fetch = saved.fetch; globalThis.Image = saved.Image; console.warn = saved.warn;
		delete globalThis.matrixRuntimeDiagnostics;
	}
};

for (const backend of ["WebGPU", "REGL"]) {
	await test(`${backend} image effect draws uncoloured rain when its background image cannot load`, async () => {
		const run = await runImagePass(backend, {}, { offline: true });
		assert.equal(run.requested.length, 1, "the background image must be requested once");
		if (backend === "WebGPU") assert.deepEqual(run.writes, [[255, 255, 255, 255]], "a white texture must stand in for the image");
		else assert.deepEqual(run.bound?.data, [[255]], "a white texture must stand in for the image");
		assert.equal(run.warnings.length, 1, "the missing image must be announced once");
		assert.match(run.warnings[0], /^Background image .+ could not be loaded/);
		assert(run.warnings[0].includes(run.requested[0]), "the warning must name the image");
		assert.deepEqual(run.diagnostics.errors, run.warnings, "diagnostics must record the missing image");
		const closed = await runImagePass(backend, {}, { offline: true, closeEarly: true });
		assert.deepEqual([closed.warnings, closed.diagnostics.errors], [[], []], "a pass closed before its image failed must stay quiet");
	});

	await test(`${backend} image effect treats an empty url like no url`, async () => {
		const plain = await runImagePass(backend, {});
		const empty = await runImagePass(backend, { url: "" });
		assert.equal(plain.requested.length, 1);
		assert.notEqual(plain.requested[0], "", "with no url the default image must be requested");
		assert.doesNotMatch(plain.requested[0], /^[a-z][a-z\d+.-]*:/i, "the default image must be bundled, not fetched from the network");
		assert.ok((await readFile(new URL(plain.requested[0], root))).length > 0, "the default image must ship in web/");
		assert.deepEqual(empty.requested, plain.requested, "url= must request the same default image as no url");
		for (const run of [plain, empty]) {
			assert.deepEqual([run.warnings, run.diagnostics.errors], [[], []], "a loaded image must not warn");
			if (backend === "WebGPU") assert.deepEqual(run.writes, [], "a loaded image must not be replaced");
			else assert.equal(run.bound?.data?.flipY, true, "the loaded image must be bound");
		}
	});
}

const makeRegl = () => {
	const resources = new Set();
	const regl = () => () => {};
	const resource = () => {
		const value = Object.assign(() => {}, { width: 4, height: 4, resize() {}, destroy() { assert(!value.dead, "REGL handle destroyed twice"); value.dead = true; } });
		resources.add(value); return value;
	};
	Object.assign(regl, { texture: resource, framebuffer: resource, buffer: resource, prop: (p) => p, context: (p) => p, clear() {}, resources });
	return regl;
};
for (const name of ["rain", "bloom", "palette", "stripe", "image", "mirror", "quilt"]) {
	await test(`REGL ${name} pass releases owned resources and preserves inputs`, async () => {
		const regl = makeRegl();
		const borrowed = { destroy() { throw new Error("borrowed FBO destroyed"); } };
		const canvas = new Events(); canvas.getBoundingClientRect = () => ({ width: 320, height: 200 });
		const { default: make } = await import(`../MatrixDesktop/web/js/regl/${name}Pass.js`);
		const pass = make({ regl, config: makeConfig({ numColumns: "4" }), lkg: { enabled: name === "quilt", tileX: 1, tileY: 1 }, cameraTex: borrowed, cameraAspectRatio: 1, canvas }, { primary: borrowed, bloom: borrowed });
		await pass.ready;
		pass.setSize(320, 200); pass.execute(true);
		pass.setSize(200, 320); pass.execute(true);
		pass.cleanup(); pass.cleanup();
		assert.equal([...regl.resources].filter((r) => !r.dead).length, 0, `${name} leaked an owned REGL resource`);
		assert.equal(canvas.count(), 0); assert.equal(window.count(), 0);
	});
}

const near = (actual, expected, message, tolerance = 1e-6) => assert(Math.abs(actual - expected) <= tolerance, `${message}: ${actual} is not ${expected}`);

await test("shader time stays small over days and moves its origin only when the last frame went unseen", () => {
	const { gapMs, briefGapMs, softSeconds, hardSeconds, restartSeconds } = rebaseLimits;
	const time = createShaderTime(), shifts = [];
	time.onRebase((shift) => shifts.push(shift));
	let wall = 1000;
	const frame = (seconds, gap = 16) => time.frame(seconds, (wall += gap));
	assert.equal(frame(0), 0);
	assert.equal(frame(500, 2 * gapMs), 500, "time below the restart point must not move");
	assert.equal(frame(1800, gapMs - 1000), 1800, "a pause under a minute must keep time continuous");
	assert.deepEqual(shifts, []);
	near(frame(1800.016, gapMs), restartSeconds, "a minute without frames (lock, display off, suspend) must restart shader time");
	near(frame(1800.032), restartSeconds + 0.016, "time must stay continuous after a move", 1e-9);
	near(shifts[0], 1200.016, "the shift must be the distance moved");
	// Four days of active time with no pause: the hard bound keeps every value under six hours.
	let seconds = 1800.032, previous = restartSeconds + 0.016, max = 0, moves = shifts.length;
	for (let step = 0; step < 4 * 24 * 60; step++) {
		seconds += 60;
		const value = frame(seconds);
		if (shifts.length === moves) near(value, previous + 60, "time must stay continuous between moves", 1e-6);
		max = Math.max(max, value); previous = value; moves = shifts.length;
	}
	assert(max < hardSeconds + 60, `shader time reached ${max} s during a 4-day run`);
	assert(shifts.length >= 15, `a 4-day run without pauses moved the origin ${shifts.length} time(s)`);
	near(frame(seconds, 5 * gapMs), restartSeconds, "a long pause after four days must restart shader time");
	// Past an hour, any pause of a second or more is unseen enough; frame jitter is not.
	const soft = createShaderTime();
	soft.frame(softSeconds - 10, (wall += 16));
	near(soft.frame(softSeconds - 9, (wall += briefGapMs)), softSeconds - 9, "a brief pause before an hour must keep time continuous");
	near(soft.frame(softSeconds + 1, (wall += briefGapMs - 1)), softSeconds + 1, "jitter under a second must keep time continuous");
	near(soft.frame(softSeconds + 2, (wall += briefGapMs)), restartSeconds, "a brief pause past an hour must restart shader time");
	// Click stamps move with the origin: a ripple in flight keeps its age, unused slots stay unused.
	const canvas = new Events(); canvas.getBoundingClientRect = () => ({ left: 0, top: 0, width: 100, height: 100 });
	const ripples = createClickRipples(canvas, true), stamped = createShaderTime();
	stamped.onRebase((shift) => ripples.shiftTime(shift));
	ripples.syncTime(stamped.frame(5000, (wall += 16)));
	for (const fn of canvas.listeners.get("click")) fn({ clientX: 50, clientY: 50 });
	ripples.markClean();
	const now = stamped.frame(5000.5, (wall += gapMs));
	near(now, restartSeconds, "the stamped clock must have moved");
	near(now - ripples.clicks[2], 0.5, "a ripple in flight must keep its age across a move");
	near(now - ripples.touches[0][2], 0.5, "a ripple in flight must keep its age across a move (WebGPU layout)");
	assert(ripples.clicks[5] < -1e8 && ripples.touches[1][2] < -1e8, "unused ripple slots must stay inactive");
	assert.equal(ripples.changed, true, "moved stamps must be uploaded again");
	ripples.cleanup();
});

await test("rain and mirror passes move their click stamps with the shader time origin", async () => {
	const borrowed = { createView: () => ({}), destroy() { throw new Error("borrowed resource destroyed"); } };
	for (const [backend, name] of [["regl", "rain"], ["regl", "mirror"], ["webgpu", "rain"], ["webgpu", "mirror"]]) {
		const label = `${backend} ${name} pass`, listeners = [];
		let now = 5.5, stamps, pass;
		const canvas = new Events(); canvas.getBoundingClientRect = () => ({ left: 0, top: 0, width: 320, height: 200 });
		const config = makeConfig({ numColumns: "4", camera: "false", clickRipples: "true", effect: name === "mirror" ? "mirror" : "palette" });
		const context = { config, canvas, cameraTex: borrowed, cameraAspectRatio: 1, elapsedSeconds: () => now,
			onTimeRebase: (fn) => { listeners.push(fn); return () => {}; } };
		const { default: make } = await import(`../MatrixDesktop/web/js/${backend}/${name}Pass.js`);
		if (backend === "regl") {
			const specs = [], regl = Object.assign((spec) => { specs.push(spec); return () => {}; }, makeRegl());
			pass = make({ ...context, regl, lkg: { enabled: false, tileX: 1, tileY: 1 } }, { primary: borrowed, bloom: borrowed });
			await pass.ready;
			const clicks = specs.find((spec) => spec.uniforms?.clicks).uniforms.clicks;
			stamps = () => clicks({ time: now });
		} else {
			const device = makeDevice(), writes = [];
			device.queue = { ...device.queue, writeBuffer: (buffer, offset, data) => writes.push(new Float32Array(data)) };
			pass = make({ ...context, device, timeBuffer: {} });
			await pass.loaded;
			pass.build([320, 200], { primary: borrowed, bloom: borrowed, highPass: borrowed });
			stamps = () => { pass.run(encoder, true); return writes.at(-1); };
		}
		stamps();
		for (const target of [canvas, window]) for (const fn of [...(target.listeners.get("click") ?? [])]) fn({ clientX: 160, clientY: 100 });
		assert([...stamps()].includes(5.5), `${label} must stamp a click at the shader time`);
		assert.equal(listeners.length, 1, `${label} must follow the shader time origin`);
		listeners[0](4.25); now = 1.25;
		const moved = [...stamps()];
		assert(moved.includes(1.25) && !moved.includes(5.5), `${label} must move its click stamp with the origin: ${moved}`);
		pass.cleanup();
	}
});

await test("regl tick stays exact in float32 past 2^24 frames and cycles glyphs on the same frames", () => {
	assert.deepEqual([0, 1, 2, 3].map((frames) => shaderTick(frames, 3)), [0, 1, 2, 3], "the first frames must be unchanged");
	// rainPass.symbol.frag.glsl: mod(tick, cycleFrameSkip) == 0., where GLSL mod(x, y) is
	// x - y * floor(x / y), each step rounded to float32.
	const f = Math.fround, cycles = (tick, skip) => { const x = f(tick); return f(x - f(skip * Math.floor(f(x / skip)))) === 0; };
	for (const skip of [3, 8]) {
		const period = skip * 2 ** 20, firstWrap = Math.ceil((2 ** 24 - 2) / period);
		for (let wrap = firstWrap; wrap < firstWrap + 64; wrap++) {
			for (let frames = 2 + wrap * period - 2 * skip; frames < 2 + wrap * period + 2 * skip; frames++) {
				const tick = shaderTick(frames, skip), label = `skip ${skip}, frame ${frames}`;
				assert.equal(f(tick), tick, `the tick must be exact in float32 (${label}: ${tick})`);
				assert(tick > 1, `only the first frames may read as a first frame (${label})`);
				assert.equal(cycles(tick, skip), frames % skip === 0, `glyphs must cycle on the frames the exact counter does (${label})`);
				assert.equal(tick % 2, frames % 2, `the ping-pong side must alternate as before (${label})`);
			}
		}
	}
});

// A fake page for webgpu/main.js: the device records texture sizes and can raise GPU events.
// These stubs are installed last so they cannot change any earlier case.
const makeMatrixDevice = (maxTextureDimension2D) => {
	const device = makeDevice(), events = new Events(), createTexture = device.createTexture, createBuffer = device.createBuffer;
	let lose;
	return Object.assign(device, {
		limits: { maxTextureDimension2D }, sizes: [], buffers: [], writes: [],
		createBuffer(config) { const buffer = createBuffer.call(device, config); device.buffers.push(buffer); return buffer; },
		lost: new Promise((resolve) => { lose = resolve; }),
		lose: (reason, message) => lose({ reason, message }),
		addEventListener: (type, fn) => events.addEventListener(type, fn),
		removeEventListener: (type, fn) => events.removeEventListener(type, fn),
		emit: (type, event) => { for (const fn of [...(events.listeners.get(type) ?? [])]) fn(event); },
		createTexture(config) { device.sizes.push(config.size); return createTexture.call(device, config); },
		renderPasses: 0,
		createCommandEncoder: () => ({ ...encoder, beginRenderPass: (descriptor) => { device.renderPasses++; return encoder.beginRenderPass(descriptor); }, finish: () => ({}) }),
		queue: { ...device.queue, submit() {}, writeBuffer(buffer, offset, data) { device.writes.push([buffer, data]); } },
		destroy() { device.destroyed = true; },
	});
};
let pendingFrame = null, lastFrame = null;
const startMatrixWebGPU = async ({ adapterLimit, cssWidth, cssHeight, resolution, params = {} }) => {
	const run = {};
	const borrowed = { createView: () => ({}), destroy() { throw new Error("canvas texture destroyed"); } };
	globalThis.requestAnimationFrame = (fn) => { pendingFrame = lastFrame = fn; return 1; };
	globalThis.cancelAnimationFrame = () => { pendingFrame = null; };
	globalThis.document = { fullscreenEnabled: false, body: { appendChild: (tag) => queueMicrotask(() => tag.onload?.()) },
		createElement: () => ({ getContext: () => ({ drawImage() {} }) }) };
	Object.defineProperty(globalThis, "navigator", { configurable: true, writable: true, value: { gpu: {
		getPreferredCanvasFormat: () => "rgba8unorm",
		requestAdapter: async () => ({ limits: { maxTextureDimension2D: adapterLimit }, requestDevice: async (descriptor) => {
			run.request = descriptor;
			return (run.device = makeMatrixDevice(descriptor?.requiredLimits?.maxTextureDimension2D ?? 8192));
		} }),
	} } });
	run.canvas = Object.assign(new Events(), { clientWidth: cssWidth, clientHeight: cssHeight, width: 300, height: 150, dataset: {},
		getBoundingClientRect: () => ({ left: 0, top: 0, width: cssWidth, height: cssHeight }),
		getContext: () => ({ configure() {}, unconfigure() {}, getCurrentTexture: () => borrowed }) });
	const { default: start } = await import("../MatrixDesktop/web/js/webgpu/main.js");
	await start(run.canvas, makeConfig({ numColumns: "4", camera: "false", resolution: String(resolution), ...params }));
	run.diagnostics = window.matrixRuntimeDiagnostics;
	run.frame = (now) => { const frame = pendingFrame; pendingFrame = null; frame?.(now); };
	run.dispose = () => run.diagnostics.dispose();
	return run;
};

await test("canvas size fits a GPU texture limit with its aspect kept", () => {
	const wide = { clientWidth: 6000, clientHeight: 1440 };
	const clamped = createSize(wide, 2, 8192), open = createSize(wide, 2), ordinary = createSize({ clientWidth: 1280, clientHeight: 720 }, 1, 8192);
	assert.deepEqual(clamped.get(), [8192, 1966], "a 12000x2880 backing size must fit 8192 on its long side");
	assert.deepEqual(open.get(), [12000, 2880], "no limit must leave the size alone");
	assert.deepEqual(ordinary.get(), [1280, 720], "an ordinary size must be unchanged");
	assert.deepEqual(fitInside(3000, 9000, 8192), [2731, 8192], "a tall size must fit on its long side");
	for (const tracker of [clamped, open, ordinary]) tracker.cleanup();
});

await test("WebGPU Matrix renderer requests the adapter's texture limit and keeps every target inside the device's", async () => {
	for (const [adapterLimit, cssWidth, cssHeight, resolution, expected] of [
		[8192, 6000, 1440, 2, [8192, 1966]],
		[16384, 6000, 1440, 2, [12000, 2880]],
		[8192, 1280, 720, 1, [1280, 720]],
	]) {
		const label = `${cssWidth}x${cssHeight} CSS px at resolution ${resolution}, adapter limit ${adapterLimit}`;
		const run = await startMatrixWebGPU({ adapterLimit, cssWidth, cssHeight, resolution });
		try {
			assert.equal(run.request?.requiredLimits?.maxTextureDimension2D, adapterLimit, `requestDevice must receive the adapter's limit (${label})`);
			run.frame(16); run.frame(33);
			assert.deepEqual([run.canvas.width, run.canvas.height], expected, `canvas size (${label})`);
			const limit = run.device.limits.maxTextureDimension2D;
			const over = run.device.sizes.filter((size) => size.slice(0, 2).some((side) => side > limit));
			assert.deepEqual(over, [], `every texture must fit the device limit (${label})`);
			assert(run.device.sizes.some(([w, h]) => w === expected[0] && h === expected[1]), `a canvas-sized target must exist (${label})`);
			assert.equal(run.diagnostics.frames, 2, label);
			assert.equal(run.diagnostics.ready, true, label);
		} finally { run.dispose(); }
	}
});

await test("WebGPU texture loads past the device limit are decoded again inside it", async () => {
	const saved = globalThis.createImageBitmap, decodes = [], closed = [];
	globalThis.createImageBitmap = async (blob, options) => {
		decodes.push(options);
		const [width, height] = options ? [options.resizeWidth, options.resizeHeight] : [10000, 5000];
		return { width, height, close() { closed.push(`${width}x${height}`); } };
	};
	try {
		const device = makeMatrixDevice(8192);
		await loadTexture(device, "assets/fixture.png");
		assert.deepEqual(device.sizes, [[8192, 4096, 1]], "the uploaded texture must fit the limit with its aspect kept");
		assert.deepEqual(decodes, [undefined, { resizeWidth: 8192, resizeHeight: 4096, resizeQuality: "high" }]);
		assert.deepEqual(closed, ["10000x5000", "8192x4096"], "both decoded bitmaps must close");
		decodes.length = 0;
		await loadTexture(makeMatrixDevice(16384), "assets/fixture.png");
		assert.deepEqual(decodes, [undefined], "an image inside the limit must decode once");
	} finally { globalThis.createImageBitmap = saved; }
});

await test("WebGPU Matrix renderer reports uncaptured GPU errors and stops claiming ready", async () => {
	const run = await startMatrixWebGPU({ adapterLimit: 8192, cssWidth: 640, cssHeight: 360, resolution: 1 });
	try {
		run.frame(16);
		assert.equal(run.diagnostics.ready, true);
		run.device.emit("uncapturederror", { error: { message: "synthetic validation failure" } });
		run.frame(33);
		assert.equal(run.diagnostics.ready, false, "ready must stay false after a GPU validation error");
		assert.deepEqual(run.diagnostics.errors, ["WebGPU validation: synthetic validation failure"]);
		for (let i = 0; i < 20; i++) run.device.emit("uncapturederror", { error: { message: `again ${i}` } });
		assert.equal(run.diagnostics.errors.length, 10, "validation errors must stay bounded");
	} finally { run.dispose(); }
});

await test("WebGPU Matrix renderer reports a lost device to the page once and stops drawing", async () => {
	const calls = [], settle = () => new Promise((resolve) => setTimeout(resolve, 0));
	window.matrixRendererLost = (reason) => calls.push(reason);
	try {
		const run = await startMatrixWebGPU({ adapterLimit: 8192, cssWidth: 640, cssHeight: 360, resolution: 1 });
		try {
			run.frame(16);
			assert.equal(run.diagnostics.ready, true);
			run.device.lose("unknown", "GPU process exited");
			await settle();
			assert.equal(run.diagnostics.ready, false, "ready must be false once the device is lost");
			assert.deepEqual(run.diagnostics.errors, ["WebGPU device lost (unknown): GPU process exited"]);
			assert.deepEqual(calls, ["WebGPU device lost (unknown)"], "the page hook must hear about the loss once");
			assert.equal(pendingFrame, null, "a lost device must not schedule another frame");
			const frames = run.diagnostics.frames;
			lastFrame(33);
			assert.equal(run.diagnostics.frames, frames, "a stray frame after the loss must not draw");
			assert.equal(run.diagnostics.ready, false);
		} finally { run.dispose(); }
		// Teardown destroys the device itself; that loss is not a failure.
		const own = await startMatrixWebGPU({ adapterLimit: 8192, cssWidth: 640, cssHeight: 360, resolution: 1 });
		own.frame(16);
		own.device.lose("destroyed", "");
		await settle();
		own.frame(33);
		assert.deepEqual([own.diagnostics.ready, own.diagnostics.errors, own.diagnostics.frames, calls.length], [true, [], 2, 1], "reason 'destroyed' must be ignored");
		own.dispose();
	} finally { delete window.matrixRendererLost; }
	// The hook belongs to js/main.js; without it a loss is still recorded and nothing throws.
	const bare = await startMatrixWebGPU({ adapterLimit: 8192, cssWidth: 640, cssHeight: 360, resolution: 1 });
	bare.frame(16);
	bare.device.lose("unknown", "driver reset");
	await settle();
	assert.deepEqual([bare.diagnostics.ready, bare.diagnostics.errors], [false, ["WebGPU device lost (unknown): driver reset"]]);
	bare.dispose();
});

await test("WebGPU Matrix renderer feeds the shaders a small time after four days", async () => {
	const run = await startMatrixWebGPU({ adapterLimit: 8192, cssWidth: 640, cssHeight: 360, resolution: 1 });
	try {
		// webgpu/main.js creates the Time uniform buffer first: { seconds : f32, frames : i32 }.
		const written = () => run.device.writes.filter(([buffer]) => buffer === run.device.buffers[0])
			.map(([, data]) => ({ seconds: new Float32Array(data)[0], frames: new Int32Array(data)[1] }));
		const day = 86_400_000;
		run.frame(16); run.frame(33);
		run.frame(4 * day); run.frame(4 * day + 17);
		const [first, second, moved, next] = written();
		// The fake page mixes synthetic frame times with real performance.now() reads, so only
		// the order of magnitude before the move is meaningful.
		assert(second.seconds < 60, `time before the move was ${second.seconds} s`);
		near(moved.seconds, rebaseLimits.restartSeconds, `the time uniform after four days was ${moved.seconds} s`, 1);
		near(next.seconds - moved.seconds, 0.017, "time must stay continuous after the move", 1e-3);
		assert.deepEqual([first, second, moved, next].map(({ frames }) => frames), [0, 1, 2, 3], "the frame counter must keep counting through a move");
	} finally { run.dispose(); }
});

await test("WebGPU Matrix renderer at fps 0 draws one still frame and stops", async () => {
	const still = await startMatrixWebGPU({ adapterLimit: 8192, cssWidth: 640, cssHeight: 360, resolution: 1, params: { fps: "0" } });
	try {
		still.frame(16);
		assert(still.device.renderPasses > 0, "fps 0 must render its one frame, not leave the canvas black");
		assert.equal(pendingFrame, null, "fps 0 must not schedule another frame");
		assert.deepEqual([still.diagnostics.frames, still.diagnostics.ready], [1, true]);
	} finally { still.dispose(); }
	const control = await startMatrixWebGPU({ adapterLimit: 8192, cssWidth: 640, cssHeight: 360, resolution: 1, params: { fps: "30" } });
	try {
		control.frame(16);
		assert.notEqual(pendingFrame, null, "fps 30 must keep animating");
	} finally { control.dispose(); }
});

console.log(`${passes} renderer lifecycle checks passed; source=${fileURLToPath(root)}`);
