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
	for (const text of ["", "true", "TRUE", "1", "y", "yes", " on "]) assert.equal(makeConfig({ camera: text }).useCamera, true, text);
	for (const text of ["false", "0", "no", "off", "truthy", "untrue", "nottrue", "true-ish"]) assert.equal(makeConfig({ camera: text }).useCamera, false, text);
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

// A fake page for webgpu/main.js: the device records texture sizes and can raise GPU events.
// These stubs are installed last so they cannot change any earlier case.
const makeMatrixDevice = (maxTextureDimension2D) => {
	const device = makeDevice(), events = new Events(), createTexture = device.createTexture;
	let lose;
	return Object.assign(device, {
		limits: { maxTextureDimension2D }, sizes: [],
		lost: new Promise((resolve) => { lose = resolve; }),
		lose: (reason, message) => lose({ reason, message }),
		addEventListener: (type, fn) => events.addEventListener(type, fn),
		removeEventListener: (type, fn) => events.removeEventListener(type, fn),
		emit: (type, event) => { for (const fn of [...(events.listeners.get(type) ?? [])]) fn(event); },
		createTexture(config) { device.sizes.push(config.size); return createTexture.call(device, config); },
		createCommandEncoder: () => ({ ...encoder, finish: () => ({}) }),
		queue: { ...device.queue, submit() {} },
		destroy() { device.destroyed = true; },
	});
};
let pendingFrame = null;
const startMatrixWebGPU = async ({ adapterLimit, cssWidth, cssHeight, resolution }) => {
	const run = {};
	const borrowed = { createView: () => ({}), destroy() { throw new Error("canvas texture destroyed"); } };
	globalThis.requestAnimationFrame = (fn) => { pendingFrame = fn; return 1; };
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
	await start(run.canvas, makeConfig({ numColumns: "4", camera: "false", resolution: String(resolution) }));
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

console.log(`${passes} renderer lifecycle checks passed; source=${fileURLToPath(root)}`);
