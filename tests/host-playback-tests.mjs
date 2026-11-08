import assert from "node:assert/strict";
import { createPlaybackClock } from "../MatrixDesktop/web/js/hostPlayback.js";

let timestamp = 100, receive, unsubscribed = 0;
const clock = createPlaybackClock({ now: () => timestamp, subscribe(listener) { receive = listener; return () => unsubscribed++; } });
assert.equal(clock.seconds(), 0);
timestamp = 1100;
assert.equal(clock.seconds(), 1);
timestamp = 1350; receive(true);
assert.equal(clock.paused, true);
timestamp = 100_000;
assert.equal(clock.seconds(), 1.25, "a paused sample must not advance time");
timestamp = 200_000; receive(false);
assert.equal(clock.seconds(), 1.25, "resume must exclude the entire paused interval");
timestamp = 200_500;
assert.equal(clock.seconds(), 1.75);
assert.equal(clock.seconds(timestamp - 10), 1.75, "an older RAF timestamp must not move the clock backwards");
// A pause/resume between rendered frames must also be removed from the clock.
timestamp = 200_700; receive(true);
timestamp = 300_000; receive(false);
timestamp = 300_100;
assert.equal(clock.seconds(), 2.05);
clock.dispose(); clock.dispose();
assert.equal(unsubscribed, 1);
timestamp = 400_000;
assert.equal(clock.seconds(), 2.05, "disposed clocks remain frozen");

const pausedClock = createPlaybackClock({ initialPaused: true, now: () => timestamp, subscribe(listener) { receive = listener; return () => {}; } });
assert.equal(pausedClock.seconds(), 0);
timestamp += 1000; receive(false);
timestamp += 1000;
assert.equal(pausedClock.seconds(), 1);
pausedClock.dispose();
console.log("PASS host playback clock: pause, between-frame pause, resume, initial pause, disposal");

// Transport: the host sends pause only as a window event from MainForm.SetHostPausedAsync's
// ExecuteScriptAsync; its one PostWebMessage sender is the pointer bridge.
{
  const { readFile } = await import("node:fs/promises");
  const source = await readFile(new URL("../MatrixDesktop/MainForm.cs", import.meta.url), "utf8");
  const match = source.match(/ExecuteScriptAsync\(\$"(window\.matrixHostPlaybackState=\{state\};[^"]*)"\)/);
  assert(match, "MainForm.SetHostPausedAsync's pause script was not found");
  const hostScript = (paused) => match[1].replace("{state}", JSON.stringify({ paused, reason: "test" })).replaceAll("{{", "{").replaceAll("}}", "}");
  const webview = new EventTarget(), pointerEvents = [];
  const window = Object.assign(new EventTarget(), { chrome: { webview } });
  const canvas = { getBoundingClientRect: () => ({ left: 0, top: 0, width: 100, height: 100 }), dispatchEvent: (event) => pointerEvents.push(event.type) };
  Object.assign(globalThis, { window, document: { querySelector: () => canvas }, MouseEvent: class { constructor(type, fields) { this.type = type; Object.assign(this, fields); } } });
  try {
    const transport = await import("../MatrixDesktop/web/js/hostPlayback.js?transport");
    const states = [];
    transport.subscribeHostPlayback((value) => states.push(value));
    const message = (data) => { const event = new Event("message"); event.data = data; webview.dispatchEvent(event); };
    message({ type: "matrix-host-state", paused: true });
    assert.deepEqual(states, [false], "a matrix-host-state web message has no host sender and must not pause");
    new Function("window", "CustomEvent", hostScript(true))(window, CustomEvent);
    assert.deepEqual(states, [false, true], "the host's ExecuteScriptAsync pause script must pause");
    new Function("window", "CustomEvent", hostScript(false))(window, CustomEvent);
    assert.deepEqual(states, [false, true, false], "the host's resume script must resume");
    message({ type: "matrix-host-pointer", eventType: "move", x: 5, y: 5, viewportWidth: 10, viewportHeight: 10, button: 0 });
    assert(pointerEvents.includes("pointermove"), "pointer web messages must still reach the canvas");
  } finally { delete globalThis.window; delete globalThis.document; delete globalThis.MouseEvent; }
  console.log("PASS host playback transport: pause and resume only through the host's window event; pointer web messages still forwarded");
}
