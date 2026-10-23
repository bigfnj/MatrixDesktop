import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import assert from "node:assert/strict";
import crypto from "node:crypto";
import { fileURLToPath } from "node:url";

const filename = process.env.NATIVE_BOOTSTRAP_SOURCE
  ? path.resolve(process.env.NATIVE_BOOTSTRAP_SOURCE)
  : fileURLToPath(new URL("../Shared/NativeMediaBridge.cs", import.meta.url));
const source = fs.readFileSync(filename, "utf8");
console.log(`SOURCE ${filename}`);
console.log(`WITNESS NativeMediaBridge.cs ${crypto.createHash("sha256").update(source).digest("hex")}`);
const first = source.indexOf("var initialize = ");
const end = source.indexOf("_scriptId = await", first);
assert.ok(first >= 0 && end > first, "The native initialization expression must be present and nonempty");
const section = source.slice(first, end);
const tokens = [...section.matchAll(/(\$?)"((?:\\.|[^"\\])*)"/gs)];
assert.ok(tokens.length > 0, "The native initialization expression must contain C# string literals");
const origin = "https://matrix-preview.local";
const configuration = "?scene=zmatrix";
function script(bootstrap, expressionTokens = tokens) {
  const result = expressionTokens.map(match => {
    let value = JSON.parse('"' + match[2] + '"');
    if (match[1]) value = value.replaceAll("{expectedOrigin}", "@@origin@@").replaceAll("{json}", "@@json@@").replaceAll("{update}", "@@update@@")
      .replaceAll("{ResourceOrigin}", "@@resource@@").replaceAll("{{", "{").replaceAll("}}", "}")
      .replaceAll("@@origin@@", JSON.stringify(origin)).replaceAll("@@json@@", JSON.stringify(bootstrap))
      .replaceAll("@@resource@@", "https://uc3d-media.local").replaceAll("@@update@@", JSON.stringify({ type: "matrix-native-resources", bootstrap }));
    return value;
  }).join("");
  assert.ok(result.trim().length > 0, "The extracted native initialization script must not be empty");
  return result;
}
function fixture(initial = { nativeGeneration: 1, label: "captured-old" }) {
  const pending = [], listeners = new Map(), native = [], events = [];
  const context = { location: { origin, search: configuration }, parent: {}, Number, Error,
    CustomEvent: class { constructor(type, detail) { this.type = type; Object.assign(this, detail); } },
    addEventListener(name, listener) { listeners.set(name, listener); },
    dispatchEvent(event) { events.push(event); },
    chrome: { webview: { addEventListener(name, listener) { native.push(listener); } } },
    fetch(url, options) {
      assert.equal(url, "https://uc3d-media.local/__uc3d/bootstrap"); assert.equal(options.cache, "no-store");
      assert.equal(typeof listeners.get("message"), "function", "Parent message listener must exist before fetch starts");
      assert.equal(native.length, 1, "Native message listener must exist before fetch starts");
      return new Promise(resolve => pending.push(resolve));
    },
  };
  context.window = context; vm.runInNewContext(script({ configuration, ...initial }), context);
  return { context, events, pending,
    message(bootstrap, from = "https://configurator.local", sourceWindow = context.parent) {
      assert.equal(typeof listeners.get("message"), "function", "Parent resource listener was not installed");
      listeners.get("message")({ data: { type: "matrix-native-resources", bootstrap: { configuration, ...bootstrap } }, origin: from, source: sourceWindow });
    },
    nativeMessage(bootstrap) { assert.equal(native.length, 1); native[0]({ data: { type: "matrix-native-resources", bootstrap: { configuration, ...bootstrap } } }); },
    finish(bootstrap) {
      assert.equal(pending.length, 1, "Each document readiness must fetch the current native bootstrap");
      pending.shift()({ ok: true, json: async () => ({ configuration, ...bootstrap }) });
    },
  };
}
const argument = process.argv.indexOf("--only");
const filter = argument >= 0 ? new RegExp(process.argv[argument + 1]) : null;
let passed = 0, failed = 0;
async function test(name, run) {
  if (filter && !filter.test(name)) return;
  try { await run(); passed++; console.log(`PASS ${name}`); }
  catch (error) { failed++; console.error(`FAIL ${name}: ${error.stack}`); }
}
await test("native preparation normalizes application and configurator query prefixes", () => {
  const expression = source.match(/configuration = ([^\r\n]+),\r?\n\s+images =/);
  assert.ok(expression, "The serialized native configuration identity expression must be present");
  for (const [query, expected] of [["", ""], ["scene=uc3d", "?scene=uc3d"], ["?scene=zmatrix", "?scene=zmatrix"],
    ["zmPhrases=%5B%22%E6%BC%A2%20A%2BB%22%5D", "?zmPhrases=%5B%22%E6%BC%A2%20A%2BB%22%5D"]]) {
    const value = vm.runInNewContext(expression[1], { _configuration: { Length: query.length, TrimStart: character => {
      let offset = 0; while (query[offset] === character) offset++; return query.slice(offset);
    } } });
    assert.equal(value, expected, "Native and configurator preparation must publish the document's exact prefixed query");
  }
});
await test("cached initializer fetches current bootstrap before readiness", async () => {
  const fresh = fixture();
  assert.equal(fresh.pending.length, 1, "Each document readiness must fetch the current native bootstrap");
  assert.equal(typeof fresh.context.matrixHostBootstrapReady?.then, "function", "Native readiness must be an awaited promise");
  fresh.finish({ nativeGeneration: 7, label: "fetched-current" });
  assert.equal((await fresh.context.matrixHostBootstrapReady).nativeGeneration, 7);
  assert.equal(fresh.context.matrixHostBootstrap.label, "fetched-current");
});
await test("older fetch completion cannot replace a newer pushed generation", async () => {
  const race = fixture(); race.message({ nativeGeneration: 3, label: "newer-message" });
  race.finish({ nativeGeneration: 2, label: "older-fetch" });
  assert.equal((await race.context.matrixHostBootstrapReady).nativeGeneration, 3);
  assert.equal(race.context.matrixHostBootstrap.label, "newer-message");
  assert.equal(race.events.length, 1, "A rejected stale fetch must not dispatch another resource update");
});
await test("cross-scene generation ignores stale messages when ZMatrix metadata is absent", async () => {
  const race = fixture(); race.finish({ nativeGeneration: 1 }); await race.context.matrixHostBootstrapReady;
  race.nativeMessage({ nativeGeneration: 5, images: ["uc3d"], wallpaper: null, zmatrixFonts: null });
  race.message({ nativeGeneration: 4, label: "stale" });
  assert.equal(race.context.matrixHostBootstrap.nativeGeneration, 5);
  assert.deepEqual(race.context.matrixHostBootstrap.images, ["uc3d"]);
});
await test("parent resource messages require both exact origin and parent window", async () => {
  const race = fixture(); race.finish({ nativeGeneration: 2 }); await race.context.matrixHostBootstrapReady;
  race.message({ nativeGeneration: 99 }, "https://wrong-origin.local");
  race.message({ nativeGeneration: 99 }, "https://configurator.local", {});
  assert.equal(race.context.matrixHostBootstrap.nativeGeneration, 2);
});
await test("native bootstrap HTTP failure rejects renderer readiness", async () => {
  const failing = fixture();
  assert.equal(failing.pending.length, 1, "Each document readiness must fetch the current native bootstrap");
  failing.pending.shift()({ ok: false, status: 503, json: async () => ({ nativeGeneration: 99 }) });
  await assert.rejects(failing.context.matrixHostBootstrapReady, /Native resource bootstrap failed \(503\)/);
  assert.equal(failing.context.matrixHostBootstrap.nativeGeneration, 1);
});
await test("cached initializer from another configuration never publishes its resources", async () => {
  const fresh = fixture({ nativeGeneration: 99, configuration: "?scene=uc3d", label: "wrong-captured" });
  assert.equal(fresh.context.matrixHostBootstrap, undefined, "A cached wrong-query bootstrap must not populate this document");
  fresh.finish({ nativeGeneration: 7, label: "correct-current" });
  assert.equal((await fresh.context.matrixHostBootstrapReady).configuration, configuration);
  assert.equal(fresh.context.matrixHostBootstrap.label, "correct-current");
});
await test("newer pushed resources from another configuration are ignored", async () => {
  const fresh = fixture(); fresh.finish({ nativeGeneration: 2 }); await fresh.context.matrixHostBootstrapReady;
  const before = fresh.events.length;
  fresh.message({ nativeGeneration: 99, configuration: "?scene=uc3d" });
  fresh.nativeMessage({ nativeGeneration: 100, configuration: "?scene=zmatrix&zmCharacters=other" });
  assert.equal(fresh.context.matrixHostBootstrap.nativeGeneration, 2, "A newer wrong-query push must not replace current resources");
  assert.equal(fresh.events.length, before, "A wrong-query push must not trigger an atlas reload");
});
await test("current bootstrap fetch from another configuration rejects readiness visibly", async () => {
  const fresh = fixture(); fresh.finish({ nativeGeneration: 99, configuration: "?scene=uc3d" });
  await assert.rejects(fresh.context.matrixHostBootstrapReady, /Native resources belong to another configuration/);
  assert.equal(fresh.context.matrixHostBootstrap.nativeGeneration, 1);
});
await test("direct native updater ignores another configuration before publishing events", async () => {
  const begin = source.indexOf("await core.ExecuteScriptAsync(");
  const finish = source.indexOf("private string ResourceUrl", begin);
  assert.ok(begin >= 0 && finish > begin, "The direct native updater expression must be present");
  const updaterTokens = [...source.slice(begin, finish).matchAll(/(\$?)"((?:\\.|[^"\\])*)"/gs)];
  assert.ok(updaterTokens.length > 0, "The direct native updater must contain executable C# string literals");
  const fresh = fixture(); fresh.finish({ nativeGeneration: 2 }); await fresh.context.matrixHostBootstrapReady;
  const before = fresh.events.length;
  vm.runInNewContext(script({ nativeGeneration: 99, configuration: "?scene=uc3d" }, updaterTokens), fresh.context);
  assert.equal(fresh.context.matrixHostBootstrap.nativeGeneration, 2, "The direct updater must retain the document's matching resources");
  assert.equal(fresh.events.length, before);
  vm.runInNewContext(script({ nativeGeneration: 3, configuration }, updaterTokens), fresh.context);
  assert.equal(fresh.context.matrixHostBootstrap.nativeGeneration, 3); assert.equal(fresh.events.length, before + 1);
});
if (!passed && !failed) { console.error("No matching native bootstrap tests ran"); failed++; }
console.log(`Native bootstrap tests: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
