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
const hostMessage = (event) => {
  if (event.data?.type === "matrix-host-state") notify(event.data.paused);
  if (event.data?.type === "matrix-host-pointer" && !paused) {
    const canvas = globalThis.document?.querySelector("canvas");
    if (!canvas) return;
    if (canvas !== pointerCanvas) { pointerCanvas = canvas; forwardPointer = createPointerForwarder(canvas); }
    forwardPointer(event.data);
  }
};
const bridge = globalThis.window?.chrome?.webview;
const hostEvent = (event) => notify(event.detail?.paused);
bridge?.addEventListener("message", hostMessage);
globalThis.window?.addEventListener("matrix-host-state", hostEvent);
globalThis.window?.addEventListener("pagehide", () => {
  bridge?.removeEventListener("message", hostMessage);
  globalThis.window?.removeEventListener("matrix-host-state", hostEvent);
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
