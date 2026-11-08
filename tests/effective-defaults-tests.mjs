// AUD-13 drift guard. The configurator leaves a web field out of every command when its value
// equals ConfiguratorMetadata.EffectiveDefault (MatrixDesktopConfigurator/ArgumentCatalog.cs),
// and the page shows that value in untouched editors (app.js effectiveDefaultsFor). Both read
// the C# tables sceneDefaults, versionDefaults, versionAliases and effectDefaults. This holds
// those tables, taken from the rebuilt --dump-state fixture, to the real config.js and to the
// stripe rows in regl/ and webgpu/stripePass.js, for every scene, control mode, version, alias
// and effect, so a version or effect edit there fails here until the C# tables follow.
//
// Usage: node tests/effective-defaults-tests.mjs <loadState.json from MatrixDesktop.Tests.exe --dump-state>
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import resolveConfig from "../MatrixDesktop/web/js/config.js";
import colorToRGB from "../MatrixDesktop/web/js/colorToRGB.js";

let passed = 0, failures = 0;
function test(name, run) {
  try { run(); passed++; console.log(`PASS ${name}`); }
  catch (error) { failures++; console.error(`FAIL ${name}: ${error.message}`); }
}

const fixturePath = process.argv[2];
if (!fixturePath) {
  console.error("FAIL usage: node tests/effective-defaults-tests.mjs <loadState.json>");
  process.exit(1);
}
const fixture = JSON.parse(readFileSync(fixturePath, "utf8"));
const metadata = fixture.metadata;
const fields = metadata.groups.flatMap((group) => group.fields).filter((field) => field.scope === "web");
const byId = Object.fromEntries(fields.map((field) => [field.id, field]));
const versions = [...byId.version.options.map((option) => option.value), ...Object.keys(metadata.versionAliases ?? {})];
const effects = byId.effect.options.map((option) => option.value);
const stripeEffects = new Set(["stripes", "customStripes", "pride", "trans", "transPride"]);
console.log(`SOURCE ${new URL("../MatrixDesktop/web/js/config.js", import.meta.url).pathname} fixture=${fixturePath}`);

// The C# rule, in its order: legacy UC3D bloom, effect, scene, version, catalog.
const effectiveDefault = (field, scene, mode, version, effect) => {
  if (scene === "uc3d" && mode === "legacy" && field.id === "bloomStrength") return field.defaultValue;
  const effectValues = scene !== "zmatrix" && effect !== "palette" ? metadata.effectDefaults?.[scene === "uc3d" ? "uc3d" : "matrix"]?.[effect] : null;
  if (effectValues && field.id in effectValues) return effectValues[field.id];
  const sceneValues = metadata.sceneDefaults?.[scene];
  if (sceneValues && field.id in sceneValues) return sceneValues[field.id];
  const versionValues = scene !== "zmatrix" ? metadata.versionDefaults?.[metadata.versionAliases?.[version] ?? version] : null;
  if (versionValues && field.id in versionValues) return versionValues[field.id];
  return field.defaultValue;
};

// The rows a stripe effect draws without stripeColors, evaluated from the renderer source.
const stripeRows = (path) => {
  const source = readFileSync(new URL(path, import.meta.url), "utf8");
  const rows = {};
  for (const name of ["transPrideStripeColors", "prideStripeColors"]) {
    const match = source.match(new RegExp(`const ${name} = (\\[[\\s\\S]*?\\.flat\\(1?\\));`));
    assert.ok(match, `${path} no longer defines ${name} as a literal list`);
    rows[name] = new Function(`return ${match[1]};`)();
  }
  return rows;
};
const reglRows = stripeRows("../MatrixDesktop/web/js/regl/stripePass.js");
const webgpuRows = stripeRows("../MatrixDesktop/web/js/webgpu/stripePass.js");

const configKey = { url: "bgURL", camera: "useCamera" };
const toRgb = (color) => { const [r, g, b] = colorToRGB(color); return { r, g, b }; };
// What config.js holds for a field, in catalog units.
const rendered = (field, config, effect) => {
  if (field.id === "stripeColors")
    return ("stripeColors" in config ? config.stripeColors : effect === "pride" ? reglRows.prideStripeColors : reglRows.transPrideStripeColors).map(toRgb);
  const value = config[configKey[field.id] ?? field.id];
  if (value == null) return value;
  if (field.kind === "color") return toRgb(value);
  if (field.kind === "palette") return value.map(({ color, at }) => ({ ...toRgb(color), at }));
  if (field.id === "slant") return (value * 180) / Math.PI;
  return value;
};
// Catalog colours are rounded to three places (0.756 for hsl(0.242, 1, 0.73)).
const same = (a, b) => {
  if (typeof a === "number" && typeof b === "number") return Math.abs(a - b) <= 0.0006;
  if (Array.isArray(a) || Array.isArray(b)) return Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((item, index) => same(item, b[index]));
  if (a && b && typeof a === "object" && typeof b === "object") {
    const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
    return [...keys].every((key) => same(a[key], b[key]));
  }
  return a === b || ((a === "" || a == null) && (b === "" || b == null));
};

// The settings a command carries for a context, as CommandBuilder emits them: scene, the UC3D
// control mode when it is not the profile's, and version and effect except their defaults and
// except under ZMatrix, which never emits them (ZMatrixControlProfile).
const contexts = [];
for (const [scene, modes] of [["matrix", [null]], ["uc3d", ["original", "legacy"]], ["zmatrix", [null]]])
  for (const mode of modes)
    for (const version of scene === "zmatrix" ? ["classic"] : versions)
      for (const effect of scene === "zmatrix" ? ["palette"] : effects) {
        const params = {};
        if (scene !== "matrix") params.scene = scene;
        if (mode === "legacy") params.uc3dControls = mode;
        if (version !== "classic") params.version = version;
        if (effect !== "palette") params.effect = effect;
        contexts.push({ scene, mode, version, effect, params });
      }

// Fields a command for that context never carries, so their default is never compared with a
// value and cannot be shown in an active editor: the context's own choices, Matrix and UC3D
// settings under ZMatrix, UC3D-only settings elsewhere, and the other UC3D control set.
// ZMatrix's own zm* defaults are held to its parsers by tests/zmatrix-config-tests.mjs.
const uc3dNative = new Set(["uc3dAmount", "uc3dGlyphSize", "uc3dFallSpeed", "uc3dForwardSpeed", "uc3dPictureScale", "uc3dPictureDelay", "uc3dClockSize"]);
const uc3dLegacyOnly = new Set(["numColumns", "density", "fallSpeed", "forwardSpeed", "raindropLength", "pictureScale", "pictureDelay", "clockSize", "cycleSpeed", "isometric"]);
const compared = (field, { scene, mode, effect }) => {
  if (["scene", "version", "effect", "uc3dControls"].includes(field.id) || field.id.startsWith("zm")) return false;
  if (scene === "zmatrix") return ["renderer", "resolution", "fps", "once", "suppressWarnings"].includes(field.id);
  if (uc3dNative.has(field.id)) return scene === "uc3d" && mode === "original";
  if (scene === "uc3d" && mode === "original" && uc3dLegacyOnly.has(field.id)) return false;
  if (field.id === "stripeColors") return stripeEffects.has(effect);
  return true;
};

test("both renderers draw the same stripe rows without stripeColors", () => {
  assert.deepEqual(webgpuRows, reglRows);
  assert.equal(reglRows.transPrideStripeColors.length, 15);
  assert.equal(reglRows.prideStripeColors.length, 12);
});

test("every version and alias the catalog offers is a config.js version", () => {
  const classic = resolveConfig({});
  for (const version of versions) {
    if (version === "classic" || version === "2003") continue;
    const config = resolveConfig({ version });
    assert.ok(Object.keys(config).some((key) => JSON.stringify(config[key]) !== JSON.stringify(classic[key])), `${version} renders classic, so config.js does not know it`);
  }
});

test("untouched defaults equal what config.js draws for every scene, version and effect", () => {
  const problems = [];
  let comparisons = 0;
  for (const context of contexts) {
    const config = resolveConfig(context.params);
    for (const field of fields) {
      if (!compared(field, context)) continue;
      const expected = rendered(field, config, context.effect);
      if (expected === undefined) continue;
      comparisons++;
      const actual = effectiveDefault(field, context.scene, context.mode, context.version, context.effect);
      if (!same(actual, expected))
        problems.push(`${context.scene}/${context.mode ?? "-"}/${context.version}/${context.effect} ${field.id}: C# ${JSON.stringify(actual)}, config.js ${JSON.stringify(expected)}`);
    }
  }
  assert.ok(comparisons > contexts.length * 20, `only ${comparisons} values compared, so the field mapping is broken`);
  assert.ok(problems.length === 0, `${problems.length} mismatches:\n  ${problems.slice(0, 40).join("\n  ")}`);
  console.log(`  compared ${comparisons} values across ${contexts.length} contexts`);
});

console.log(`${passed} passed, ${failures} failed`);
process.exit(failures ? 1 : 0);
