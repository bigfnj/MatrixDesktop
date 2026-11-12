import { createPointerForwarder } from "./hostPointer.js";

// Every renderer uses an active-time clock. Host suspension must not turn the
// time spent locked, asleep, or manually paused into animation catch-up debt.
const listeners = new Set();
let paused = globalThis.window?.matrixHostPlaybackState?.paused === true;
let pointerCanvas = null, forwardPointer = null;
const notify = (value) => {
  if (typeof value !== "boolean" || value === paused) return;
  paused = value;
  if (paused) forwardPointer?.(null);
  for (const listener of listeners) listener(value);
};
// Pause state arrives only as the matrix-host-state window event below; the host's
// one PostWebMessage sender is the pointer bridge.
const hostPointerMessage = (event) => {
  if (event.data?.type === "matrix-host-pointer" && !paused) {
    const canvas = globalThis.document?.querySelector("canvas");
    if (!canvas) return;
    if (canvas !== pointerCanvas) { pointerCanvas = canvas; forwardPointer = createPointerForwarder(canvas); }
    forwardPointer(event.data);
  }
};
const bridge = globalThis.window?.chrome?.webview;
const hostEvent = (event) => notify(event.detail?.paused);
// R2-19: a host pause for lock, display off or suspend hides the WebView before suspending
// it, and a hidden or occluded page stops drawing, so nobody saw its last frame; a manual,
// fullscreen-app or battery pause keeps the frame on screen. Every visibility change moves
// this epoch, so a hidden-then-visible pair still counts if both arrive at once, and
// js/shaderTime.js moves its origin only across a gap the epoch moved in. R3-02: an unlock
// during a visible pause shows the frozen frame again while still paused, so shaderTime.js also
// needs when the page last came into view: shownAt, in performance.now() milliseconds.
let unseen = 0, shown = -Infinity;
const visibilityChange = () => {
  unseen++;
  if (globalThis.document?.visibilityState === "visible") shown = performance.now();
};
export const unseenEpoch = () => unseen;
export const shownAt = () => shown;
globalThis.document?.addEventListener?.("visibilitychange", visibilityChange);
bridge?.addEventListener("message", hostPointerMessage);
globalThis.window?.addEventListener("matrix-host-state", hostEvent);
globalThis.window?.addEventListener("pagehide", () => {
  bridge?.removeEventListener("message", hostPointerMessage);
  globalThis.window?.removeEventListener("matrix-host-state", hostEvent);
  globalThis.document?.removeEventListener?.("visibilitychange", visibilityChange);
  listeners.clear();
  pointerCanvas = null; forwardPointer = null;
}, { once: true });

export function subscribeHostPlayback(listener) {
  listeners.add(listener);
  listener(paused);
  return () => listeners.delete(listener);
}

export function createPlaybackClock({
  now = () => performance.now(),
  initialPaused = paused,
  subscribe = (listener) => { listeners.add(listener); return () => listeners.delete(listener); },
} = {}) {
  let isPaused = initialPaused, last = null, elapsed = 0, disposed = false;
  const advance = (timestamp) => {
    if (disposed) return;
    timestamp = Math.max(timestamp, last ?? timestamp);
    if (last !== null && !isPaused) elapsed += Math.max(0, timestamp - last);
    last = timestamp;
  };
  const unsubscribe = subscribe((value) => { advance(now()); isPaused = value; });
  return {
    get paused() { return isPaused; },
    seconds(timestamp = now()) { advance(timestamp); return elapsed / 1000; },
    dispose() { if (disposed) return; disposed = true; unsubscribe(); },
  };
}
