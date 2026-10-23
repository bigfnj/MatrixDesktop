import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { runInThisContext } from "node:vm";
import { pathToFileURL } from "node:url";
import makeConfig from "../MatrixDesktop/web/js/config.js";

const root = process.env.LKG_TEST_ROOT ? pathToFileURL(path.resolve(process.env.LKG_TEST_ROOT) + path.sep)
  : new URL("../MatrixDesktop/web/js/", import.meta.url);
const web = new URL("../MatrixDesktop/web/", import.meta.url);
const { default: getLKG } = await import(new URL("regl/lkgHelper.js", root));
const { default: makeRain } = await import(new URL("regl/rainPass.js", root));
for (const file of ["regl/lkgHelper.js", "regl/rainPass.js"])
  console.log(`WITNESS ${file} ${crypto.createHash("sha256").update(fs.readFileSync(new URL(file, root))).digest("hex")}`);
runInThisContext(fs.readFileSync(new URL("lib/gl-matrix.min.js", web), "utf8"));
globalThis.Image = class { width = 4; height = 4; decode() { return Promise.resolve(); } };
globalThis.fetch = async url => ({ text: async () => fs.readFileSync(new URL(url, web), "utf8") });
const filterAt = process.argv.indexOf("--only"), filter = filterAt >= 0 ? new RegExp(process.argv[filterAt + 1]) : null;
let passed = 0, failed = 0;
async function test(name, run) {
  if (filter && !filter.test(name)) return;
  try { await run(); passed++; console.log(`PASS ${name}`); }
  catch (error) { failed++; console.error(`FAIL ${name}: ${error.stack}`); }
}
const values = { DPI: 300, screenW: 1000, screenH: 1500, pitch: 50, slope: -7, center: .15, invView: 1, viewCone: 40 };
async function discover(quilt = {}, calibration = {}) {
  let disconnects = 0;
  const warnings = [], warn = console.warn;
  console.warn = (...args) => warnings.push(args.join(" "));
  try {
    const result = await getLKG(true, { loadCore: async () => ({ Client: class {
      constructor(ok) { ok({ devices: [{ calibration: Object.fromEntries(Object.entries({ ...values, ...calibration }).map(([key, value]) => [key, { value }])),
        defaultQuilt: { tileX: 8, tileY: 6, quiltX: 3840, quiltY: 3840, quiltAspect: .75, ...quilt } }] }); }
      disconnect() { disconnects++; }
    } }) });
    assert.equal(disconnects, 1, "Discovery must release its connection after accepting or rejecting calibration");
    return { result, warnings };
  } finally { console.warn = warn; }
}
function rejected({ result, warnings }, label) {
  assert.deepEqual(result, { enabled: false, tileX: 1, tileY: 1 }, `${label} must fall back before allocating viewpoints`);
  assert.equal(warnings.length, 1, `${label} must visibly report fallback`);
  assert.match(warnings[0], /1024 views.*at least one pixel/);
  assert.match(warnings[0], /rendering normal Matrix rain/);
}
await test("Looking Glass rejects billion-view and over-budget discovery", async () => {
  rejected(await discover({ tileX: 1000000000, tileY: 1, quiltX: 1000000000 }), "Billion-view calibration");
  rejected(await discover({ tileX: 1025, tileY: 1 }), "1025-view calibration");
});
await test("Looking Glass rejects zero-width quilt tiles", async () => {
  rejected(await discover({ tileX: 8, quiltX: 7 }), "Subpixel quilt width");
});
await test("Looking Glass rejects zero-height quilt tiles", async () => {
  rejected(await discover({ tileY: 6, quiltY: 5 }), "Subpixel quilt height");
});
await test("Looking Glass retains exact budget boundary and normal calibration", async () => {
  for (const quilt of [{}, { tileX: 32, tileY: 32, quiltX: 32, quiltY: 32 }, { tileX: 1, tileY: 1 }]) {
    const { result, warnings } = await discover(quilt);
    assert.equal(result.enabled, true, "Valid calibration must remain supported");
    assert.equal(result.tileX * result.tileY, quilt.tileX == null ? 48 : quilt.tileX * quilt.tileY);
    assert.ok(result.quiltViewPortion.every(value => value > 0 && value <= 1));
    assert.equal(warnings.length, 0);
  }
});
await test("Looking Glass protects validated quilt layout from calibration collisions", async () => {
  const expected = { tileX: 8, tileY: 6, quiltX: 3840, quiltY: 3840 };
  for (const [key, value] of Object.entries({ tileX: 1000000000, tileY: 1000000000, quiltX: .5, quiltY: .5 })) {
    const { result, warnings } = await discover({}, { [key]: value });
    assert.equal(result.enabled, true);
    assert.equal(result[key], expected[key], `${key} must retain the validated quilt value when calibration contains a colliding field`);
    assert.equal(result.tileX * result.tileY, 48);
    assert.equal(warnings.length, 0);
  }
});
async function renderViewports(width, height) {
  const viewports = [], handle = () => Object.assign(() => {}, { resize() {}, destroy() {} });
  const regl = () => props => { if (props?.viewport) viewports.push(props.viewport); };
  Object.assign(regl, { texture: handle, framebuffer: handle, buffer: handle,
    prop: value => value, context: value => value, clear() {} });
  const { result: lkg } = await discover();
  const pass = makeRain({ regl, lkg, config: makeConfig({ numColumns: "4" }), canvas: { addEventListener() {}, removeEventListener() {} } });
  try { await pass.ready; pass.setSize(width, height); pass.execute(true); return viewports; }
  finally { pass.cleanup(); }
}
await test("Looking Glass render tiles retain pixel width in a tiny viewport", async () => {
  const viewports = await renderViewports(1, 120);
  assert.equal(viewports.length, 48);
  assert.ok(viewports.every(viewport => viewport.width === 1 && viewport.height === 20), "Every actual render viewport must have at least one horizontal pixel");
});
await test("Looking Glass render tiles retain pixel height in a tiny viewport", async () => {
  const viewports = await renderViewports(160, 1);
  assert.equal(viewports.length, 48);
  assert.ok(viewports.every(viewport => viewport.width === 20 && viewport.height === 1), "Every actual render viewport must have at least one vertical pixel");
});
await test("Looking Glass render tiles preserve normal quilt dimensions", async () => {
  const viewports = await renderViewports(160, 120);
  assert.equal(viewports.length, 48);
  assert.deepEqual(viewports[0], { x: 0, y: 0, width: 20, height: 20 });
  assert.deepEqual(viewports.at(-1), { x: 140, y: 100, width: 20, height: 20 });
});
if (!passed && !failed) { console.error("No matching Looking Glass bounds tests ran"); failed++; }
console.log(`Looking Glass bounds tests: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
