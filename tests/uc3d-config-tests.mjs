import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import resolveConfig from "../MatrixDesktop/web/js/config.js";

const native = {
  uc3dAmount: [100, 1500, 500], uc3dGlyphSize: [10, 40, 12],
  uc3dFallSpeed: [0, 150, 6], uc3dForwardSpeed: [0, 120, 6],
  uc3dPictureScale: [100, 300, 100], uc3dPictureDelay: [0, 120, 60],
  uc3dClockSize: [50, 170, 90],
};
const legacy = ["numColumns", "density", "fallSpeed", "forwardSpeed", "raindropLength", "pictureScale", "pictureDelay", "clockSize", "cycleSpeed", "isometric"];
let passed = 0, failures = 0;
function test(name, run) {
  try { run(); passed++; console.log(`PASS ${name}`); }
  catch (error) { failures++; console.error(`FAIL ${name}: ${error.message}`); }
}

console.log(`SOURCE ${new URL("../MatrixDesktop/web/js/config.js", import.meta.url).pathname}`);
test("bare UC3D chooses original independent slider units", () => {
  const config = resolveConfig({ scene: "uc3d" });
  assert.equal(config.uc3dControls, "original");
  assert.equal(config.bloomStrength, 0);
  for (const [key, [, , current]] of Object.entries(native)) assert.equal(config[key], current, key);
});
test("all seven UC3D parsers retain endpoints, midpoint, current and finite integer limits", () => {
  for (const [key, [min, max, current]] of Object.entries(native)) {
    for (const value of [min - 1, min, (min + max) / 2, max, max + 1, current, `${current}.9`, "1e3"])
      assert.equal(resolveConfig({ scene: "uc3d", [key]: String(value) })[key], Math.max(min, Math.min(max, Math.trunc(Number(value)))), `${key}=${value}`);
    for (const invalid of ["NaN", "Infinity", "broken", "", "12broken", "0x10", "0b1100"])
      assert.equal(resolveConfig({ scene: "uc3d", [key]: invalid })[key], current, `${key} must reject ${invalid}`);
  }
});
test("explicit native units and control mode win independent of query ordering", () => {
  for (const params of [{ numColumns: "120", uc3dAmount: "800" }, { uc3dAmount: "800", numColumns: "120" }]) {
    assert.equal(resolveConfig({ scene: "uc3d", ...params }).uc3dControls, "original");
    assert.equal(resolveConfig({ scene: "uc3d", uc3dControls: "legacy", ...params }).uc3dControls, "legacy");
    assert.equal(resolveConfig({ scene: "uc3d", ...params, uc3dControls: "legacy" }).uc3dControls, "legacy");
  }
  for (const key of legacy) assert.equal(resolveConfig({ scene: "uc3d", [key]: "1" }).uc3dControls, "legacy", key);
  for (const requested of [{ cycleSpeed: "0.2" }, { isometric: "true" }]) {
    assert.equal(resolveConfig({ scene: "uc3d", ...requested }).uc3dControls, "legacy");
    for (const ordered of [{ ...requested, uc3dAmount: "800" }, { uc3dAmount: "800", ...requested }])
      assert.equal(resolveConfig({ scene: "uc3d", ...ordered }).uc3dControls, "original");
    for (const ordered of [{ uc3dControls: "original", ...requested }, { ...requested, uc3dControls: "original" }])
      assert.equal(resolveConfig({ scene: "uc3d", ...ordered }).uc3dControls, "original");
  }
  for (const key of ["width", "dropLength"]) assert.equal(resolveConfig({ scene: "uc3d", [key]: "1" }).uc3dControls, "legacy", key);
});
test("legacy UC3D commands retain generic customized values", () => {
  const config = resolveConfig({ scene: "uc3d", numColumns: "123", density: "7.333", fallSpeed: "-3.75", forwardSpeed: "-2.5", pictureScale: "2.75", pictureDelay: "175", clockSize: "3.5" });
  assert.equal(config.uc3dControls, "legacy");
  assert.equal(config.bloomStrength, 0.7);
  for (const [key, expected] of Object.entries({ numColumns: 123, density: 7.333, fallSpeed: -3.75, forwardSpeed: -2.5, pictureScale: 2.75, pictureDelay: 175, clockSize: 3.5 })) assert.equal(config[key], expected, key);
});
test("original picture and clock units override retained generic values", () => {
  const original = resolveConfig({ scene: "uc3d", uc3dControls: "original", uc3dPictureScale: "300", uc3dPictureDelay: "0", uc3dClockSize: "170", pictureScale: "1.25", pictureDelay: "90", clockSize: "0.5" });
  assert.equal(original.pictureScale, 3); assert.equal(original.pictureDelay, 0); assert.equal(original.clockSize, 170 / 90);
  const legacyConfig = resolveConfig({ scene: "uc3d", uc3dControls: "legacy", uc3dPictureScale: "300", uc3dPictureDelay: "0", uc3dClockSize: "170", pictureScale: "1.25", pictureDelay: "90", clockSize: "0.5" });
  assert.equal(legacyConfig.pictureScale, 1.25); assert.equal(legacyConfig.pictureDelay, 90); assert.equal(legacyConfig.clockSize, 0.5);
  const matrix = resolveConfig({ scene: "matrix", uc3dPictureScale: "300", uc3dPictureDelay: "0", uc3dClockSize: "170" });
  assert.equal(matrix.pictureScale, 1); assert.equal(matrix.pictureDelay, 20); assert.equal(matrix.clockSize, 1);
});
test("only original UC3D constrains animation speed to a nonnegative multiplier", () => {
  for (const [input, expected] of [["-2", 0], ["0", 0], ["1.5", 1.5], ["10", 4]])
    assert.equal(resolveConfig({ scene: "uc3d", animationSpeed: input }).animationSpeed, expected);
  assert.equal(resolveConfig({ scene: "uc3d", uc3dControls: "legacy", animationSpeed: "-2" }).animationSpeed, -2);
  assert.equal(resolveConfig({ scene: "matrix", animationSpeed: "-2" }).animationSpeed, -2);
});
// The gate writes this fixture from the rebuilt C# catalogue. Standalone runs can
// pass its path explicitly; no stale hand-authored fixture is accepted.
const fixturePath = process.argv[2];
if (fixturePath) test("rebuilt C# slider metadata agrees with every web parser", () => {
  const fixture = JSON.parse(readFileSync(fixturePath, "utf8"));
  const fields = fixture.metadata.groups.flatMap(group => group.fields);
  for (const [key, [min, max, current]] of Object.entries(native)) {
    const field = fields.find(field => field.id === key);
    assert.ok(field?.slider, `${key} must offer a slider in the actual catalogue`);
    assert.deepEqual([field.min, field.max, field.defaultValue], [min, max, current], key);
    assert.equal(fixture.metadata.sceneDefaults.uc3d[key], resolveConfig({ scene: "uc3d" })[key], key);
  }
});
console.log(`${passed} UC3D config tests passed, ${failures} failed`);
if (failures) throw new Error(`${failures} UC3D configuration checks failed`);
