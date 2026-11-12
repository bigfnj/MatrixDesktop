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
  // R2-48: {state} is the host's own payload, not one written here, so a renamed parameter or
  // field, or a changed value, fails the pause below. The anonymous type's members are read from
  // SetHostPausedAsync and evaluated as JavaScript with the arguments a tray pause and resume pass
  // (MatrixApplicationContext.ReconcilePauseAsync); System.Text.Json's default options keep each
  // member's C# name, and any other Serialize form fails here instead of being guessed.
  const method = source.match(/SetHostPausedAsync\(bool (\w+), bool (\w+), string (\w+)\)\s*\{([\s\S]*?)ExecuteScriptAsync\(\$"window\.matrixHostPlaybackState=\{state\};/);
  const payload = method?.[4].match(/var state = (?:System\.Text\.Json\.)?JsonSerializer\.Serialize\(new \{ ([^{}]*) \}\);/);
  assert(payload, "SetHostPausedAsync's payload (var state = JsonSerializer.Serialize(new { ... }); before its pause script) was not found");
  const members = payload[1].split(",").map((member) => member.trim().replace(/^(\w+)\s*=\s*/, "$1: ")).join(", ");
  const hostState = (...args) => {
    try { return new Function(method[1], method[2], method[3], `return JSON.stringify({ ${members} });`)(...args); }
    catch (error) { assert.fail(`SetHostPausedAsync's payload new { ${payload[1]} } could not be evaluated as JavaScript (${error.message}); update this check`); }
  };
  const pauseState = hostState(true, false, "Manual"), resumeState = hostState(false, false, "None");
  const hostScript = (paused) => match[1].replace("{state}", paused ? pauseState : resumeState).replaceAll("{{", "{").replaceAll("}}", "}");
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
    assert.deepEqual(states, [false, true], `the host's ExecuteScriptAsync pause script must pause (host payload ${pauseState})`);
    new Function("window", "CustomEvent", hostScript(false))(window, CustomEvent);
    assert.deepEqual(states, [false, true, false], `the host's resume script must resume (host payload ${resumeState})`);
    message({ type: "matrix-host-pointer", eventType: "move", x: 5, y: 5, viewportWidth: 10, viewportHeight: 10, button: 0 });
    assert(pointerEvents.includes("pointermove"), "pointer web messages must still reach the canvas");
  } finally { delete globalThis.window; delete globalThis.document; delete globalThis.MouseEvent; }
  console.log("PASS host playback transport: pause and resume only through the host's window event; pointer web messages still forwarded");
}

// R2-19: the unseen epoch moves on every visibility change and on nothing else, so a visible
// host pause (manual, fullscreen app, battery) never lets js/shaderTime.js move its origin.
{
  const window = new EventTarget(), document = new EventTarget();
  Object.assign(globalThis, { window, document });
  try {
    const playback = await import("../MatrixDesktop/web/js/hostPlayback.js?visibility");
    const start = playback.unseenEpoch(), host = (paused) => window.dispatchEvent(new CustomEvent("matrix-host-state", { detail: { paused } }));
    host(true); host(false);
    assert.equal(playback.unseenEpoch(), start, "a host pause without a visibility change must not move the unseen epoch");
    document.dispatchEvent(new Event("visibilitychange"));
    assert.equal(playback.unseenEpoch(), start + 1, "a visibility change must move the unseen epoch");
    document.dispatchEvent(new Event("visibilitychange"));
    assert.equal(playback.unseenEpoch(), start + 2, "every visibility change must move it, so a coalesced hidden-then-visible pair still counts");
    window.dispatchEvent(new Event("pagehide"));
    document.dispatchEvent(new Event("visibilitychange"));
    assert.equal(playback.unseenEpoch(), start + 2, "pagehide must remove the visibility listener");
  } finally { delete globalThis.window; delete globalThis.document; }
  console.log("PASS host playback unseen epoch: moves on each visibility change, not on a visible host pause; pagehide removes the listener");
}

// R3-02: an unlock during a visible pause shows the frozen frame again while still paused, so
// js/shaderTime.js also takes when the page last came into view, which only a change to visible moves.
{
  const window = new EventTarget(), document = Object.assign(new EventTarget(), { visibilityState: "visible" });
  Object.assign(globalThis, { window, document });
  const now = performance.now;
  try {
    const playback = await import("../MatrixDesktop/web/js/hostPlayback.js?shown");
    const change = (state, at) => { document.visibilityState = state; performance.now = () => at; document.dispatchEvent(new Event("visibilitychange")); };
    assert.equal(playback.shownAt(), -Infinity, "a page whose visibility never changed was never shown again");
    change("hidden", 1000);
    assert.equal(playback.shownAt(), -Infinity, "a change to hidden must not count as shown");
    change("visible", 2000);
    assert.equal(playback.shownAt(), 2000, "a change to visible must record when it happened");
    change("hidden", 3000);
    assert.equal(playback.shownAt(), 2000, "a later hide must keep the time the page was shown");
    performance.now = () => 4000;
    window.dispatchEvent(new CustomEvent("matrix-host-state", { detail: { paused: true } }));
    window.dispatchEvent(new CustomEvent("matrix-host-state", { detail: { paused: false } }));
    assert.equal(playback.shownAt(), 2000, "a host pause and resume must not move it");
  } finally { performance.now = now; delete globalThis.window; delete globalThis.document; }
  console.log("PASS host playback shownAt: moves only on a change to visible, to the time of that change");
}
