import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import makeConfig from "../MatrixDesktop/web/js/config.js";
import { zmatrixDefaults, zmatrixSettingsSpec } from "../MatrixDesktop/web/js/zmatrix/settings.js";

const encode = value => "json64:" + Buffer.from(JSON.stringify(value), "utf8").toString("base64url");
const phrases = ["Text & pipe | %PATH% ! \"quoted\" '$()`", "Matrix \u03bb \u4e2d"];
const characters = ["A", "&", "\u03bb", "\u4e2d"];
const font = { ...zmatrixDefaults.zmFont, name: "Face & \u03bb", height: -19, italic: true };
for (const [key, value] of Object.entries({ zmPhrases: phrases, zmCharacters: characters, zmFont: font })) {
  const raw = makeConfig({ scene: "zmatrix", [key]: JSON.stringify(value) });
  const encoded = makeConfig({ scene: "zmatrix", [key]: encode(value) });
  assert.deepEqual(raw[key], value, `${key} raw JSON must remain accepted`);
  assert.deepEqual(encoded[key], value, `${key} copied shell-safe JSON must decode exactly`);
}
for (const invalid of ["json64:", "json64:!invalid", "json64:_w", "json64:a", "json64:" + "A".repeat(262145)]) {
  assert.deepEqual(makeConfig({ scene: "zmatrix", zmPhrases: invalid }).zmPhrases, zmatrixDefaults.zmPhrases, "invalid encoded data must select the default");
}
assert.deepEqual(makeConfig({ scene: "zmatrix", zmPhrases: encode([]) }).zmPhrases, []);
for (const invalid of ["12broken", "   ", "0x10", "0b1100", "Infinity", "NaN"])
  assert.equal(makeConfig({ scene: "zmatrix", zmMaxStream: invalid }).zmMaxStream, 1000, "numeric syntax must agree with the C# importer");
assert.equal(makeConfig({ scene: "matrix" }).scene, "matrix");
assert.equal(makeConfig({ scene: "uc3d" }).uc3dAmount, 500);
console.log("PASS ZMatrix routing and raw/encoded structured values, Unicode, empty lists, invalid data and scene defaults");

if (process.argv[2]) {
  const fixture = JSON.parse(readFileSync(process.argv[2], "utf8"));
  const fields = fixture.metadata.groups.flatMap(group => group.fields);
  const config = makeConfig({ scene: "zmatrix" });
  let compared = 0;
  for (const [key, spec] of Object.entries(zmatrixSettingsSpec)) {
    const field = fields.find(field => field.id === key);
    assert.ok(field, `rebuilt C# catalog is missing ${key}`);
    const value = field.kind === "rgb8" ? [field.defaultValue.r, field.defaultValue.g, field.defaultValue.b] : field.defaultValue;
    assert.deepEqual(value, config[key], `${key} C# and JS defaults differ`);
    if (spec.kind === "integer" || spec.kind === "number") assert.deepEqual([field.min, field.max], [spec.min, spec.max], `${key} ranges differ`);
    compared++;
  }
  assert.equal(compared, 27, "all ZMatrix settings must be compared against the rebuilt C# catalog");
  console.log("PASS all27 rebuilt C# ZMatrix settings agree with effective web parsers");
}
