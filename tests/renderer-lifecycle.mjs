import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { runInThisContext } from "node:vm";
import { fileURLToPath } from "node:url";
import createOwner, { ownDevice } from "../MatrixDesktop/web/js/resourceOwner.js";
import createSize from "../MatrixDesktop/web/js/canvasSize.js";
import { wantsWebGPU } from "../MatrixDesktop/web/js/rendererChoice.js";
import makeConfig from "../MatrixDesktop/web/js/config.js";
import getLKG from "../MatrixDesktop/web/js/regl/lkgHelper.js";
import { makePipeline } from "../MatrixDesktop/web/js/webgpu/utils.js";

const root = new URL("../MatrixDesktop/web/", import.meta.url);
runInThisContext(await readFile(new URL("lib/gl-matrix.min.js", root), "utf8"));
globalThis.GPUBufferUsage = { UNIFORM: 1, COPY_DST: 2, STORAGE: 4 };
globalThis.GPUTextureUsage = { TEXTURE_BINDING: 1, COPY_DST: 2, RENDER_ATTACHMENT: 4, COPY_SRC: 8, STORAGE_BINDING: 16 };
globalThis.fetch = async (path) => ({
	text: () => readFile(new URL(path, root), "utf8"),
	blob: async () => ({}),
});
globalThis.createImageBitmap = async () => ({ width: 4, height: 4 });
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
window.matchMedia = () => new Events();
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
	const tracker = createSize(canvas, 0.5);
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

console.log(`${passes} renderer lifecycle checks passed; source=${fileURLToPath(root)}`);
