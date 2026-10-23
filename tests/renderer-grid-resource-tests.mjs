import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { runInThisContext } from "node:vm";
import { pathToFileURL } from "node:url";
import makeConfig from "../MatrixDesktop/web/js/config.js";
const root = process.env.RENDERER_GRID_TEST_ROOT ? pathToFileURL(path.resolve(process.env.RENDERER_GRID_TEST_ROOT) + path.sep)
  : new URL("../MatrixDesktop/web/js/", import.meta.url);
const web = new URL("../MatrixDesktop/web/", import.meta.url);
const { loadTexture } = await import(new URL("webgpu/utils.js", root));
const { default: makeReglRain } = await import(new URL("regl/rainPass.js", root));
const { default: makeGpuRain } = await import(new URL("webgpu/rainPass.js", root));
for (const file of ["regl/rainPass.js", "webgpu/rainPass.js", "webgpu/utils.js"])
  console.log(`WITNESS ${file} ${crypto.createHash("sha256").update(fs.readFileSync(new URL(file, root))).digest("hex")}`);
runInThisContext(fs.readFileSync(new URL("lib/gl-matrix.min.js", web), "utf8"));
globalThis.GPUTextureUsage = { TEXTURE_BINDING: 1, COPY_DST: 2, RENDER_ATTACHMENT: 4 };
globalThis.GPUBufferUsage = { UNIFORM: 1, COPY_DST: 2, STORAGE: 4 };
globalThis.Image = class { width = 4; height = 4; decode() { return Promise.resolve(); } };
globalThis.createImageBitmap = async () => ({ width: 4, height: 4, close() {} });
globalThis.fetch = async url => ({ text: async () => fs.readFileSync(new URL(url, web), "utf8"), blob: async () => ({}) });
const filterAt = process.argv.indexOf("--only"), filter = filterAt >= 0 ? new RegExp(process.argv[filterAt + 1]) : null;
let passed = 0, failed = 0;
async function test(name, run) {
  if (filter && !filter.test(name)) return;
  try { await run(); passed++; console.log(`PASS ${name}`); }
  catch (error) { failed++; console.error(`FAIL ${name}: ${error.stack}`); }
}
const canvas = { addEventListener() {}, removeEventListener() {} };
const cases = [[1, .01, 1], [60, .01, 1], [20, 1.5, 30], [1, 8, 8]];
await test("REGL volumetric density endpoints allocate at least one effective column", async () => {
  for (const [columns, density, expected] of cases) {
    const textures = [], handle = () => Object.assign(() => {}, { destroy() {} });
    const regl = () => () => {};
    Object.assign(regl, { texture(config) { textures.push(config); return handle(); }, framebuffer: handle, buffer: handle,
      prop: value => value, context: value => value, clear() {} });
    const config = makeConfig({ numColumns: String(columns), density: String(density), volumetric: "true" });
    const pass = makeReglRain({ regl, config, lkg: { enabled: false }, canvas });
    try {
      await pass.ready;
      const compute = textures.filter(options => options?.type === "half float");
      assert.equal(compute.length, 8); assert.ok(compute.every(options => options.width === expected),
        `Effective REGL columns must be ${expected} for ${columns} columns and density ${density}`);
    } finally { pass.cleanup(); }
  }
});
await test("WebGPU volumetric density endpoints allocate nonzero native cell populations", async () => {
  let oneColumnIntroSize = null;
  for (const [columns, density, expected] of cases) {
    const storage = [], pipeline = () => ({ getBindGroupLayout: () => ({}) });
    class Buffer {
      constructor(config) { this.data = new ArrayBuffer(config.size); if (config.usage === GPUBufferUsage.STORAGE) storage.push(config); }
      getMappedRange() { return this.data; } unmap() {} destroy() {}
    }
    globalThis.GPUBuffer = Buffer;
    const device = { createTexture: () => ({ createView: () => ({}), destroy() {} }), createBuffer: options => new Buffer(options),
      createSampler: () => ({}), createShaderModule: () => ({}), createBindGroup: () => ({}),
      createComputePipelineAsync: async () => pipeline(), createRenderPipelineAsync: async () => pipeline(),
      queue: { copyExternalImageToTexture() {} } };
    const pass = makeGpuRain({ config: makeConfig({ numColumns: String(columns), density: String(density), volumetric: "true" }), device, timeBuffer: {}, canvas });
    try {
      await pass.loaded; assert.equal(storage.length, 2); assert.ok(storage.every(options => options.size > 0),
        `WebGPU cell storage must be nonzero for ${columns} columns and density ${density}`);
      oneColumnIntroSize ??= storage[0].size;
      assert.equal(storage[0].size, oneColumnIntroSize * expected, "Nondegenerate density must retain its native column multiplier");
    } finally { pass.cleanup(); }
  }
});
await test("WebGPU texture upload closes its decoded bitmap on success and failures", async () => {
  const saved = { fetch: globalThis.fetch, createImageBitmap: globalThis.createImageBitmap };
  try {
    for (const failure of [null, "allocation", "upload"]) {
      let closed = 0, copies = 0;
      globalThis.fetch = async () => ({ blob: async () => ({}) });
      globalThis.createImageBitmap = async () => ({ width: 2, height: 3, close() { closed++; } });
      const texture = {}, device = { createTexture() { if (failure === "allocation") throw new Error("synthetic allocation fault"); return texture; },
        queue: { copyExternalImageToTexture() { copies++; if (failure === "upload") throw new Error("synthetic upload fault"); } } };
      if (failure) await assert.rejects(loadTexture(device, "fixture"), /synthetic/);
      else assert.equal(await loadTexture(device, "fixture"), texture);
      assert.equal(closed, 1, `The decoded bitmap must close after ${failure ?? "success"}`);
      assert.equal(copies, failure === "allocation" ? 0 : 1);
    }
  } finally { Object.assign(globalThis, saved); }
});
if (!passed && !failed) { console.error("No matching renderer grid/resource tests ran"); failed++; }
console.log(`Renderer grid/resource tests: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
