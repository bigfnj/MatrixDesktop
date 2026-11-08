import assert from "node:assert/strict";
import { createPointerForwarder } from "../MatrixDesktop/web/js/hostPointer.js";
import { createScene, attachMouse } from "../MatrixDesktop/web/js/uc3d/scene.js";
const events = [];
class Event { constructor(type, fields) { this.type = type; Object.assign(this, fields); } }
const canvas = { getBoundingClientRect: () => ({ left: 10, top: 20, width: 800, height: 450 }), dispatchEvent: event => events.push(event) };
const forward = createPointerForwarder(canvas, { PointerEvent: Event, MouseEvent: Event, WheelEvent: Event });
const pointer = (eventType, more = {}) => ({ eventType, x: 800, y: 450, viewportWidth: 1600, viewportHeight: 900, button: 0, ...more });
assert.equal(forward(pointer("move")), true);
assert.equal(events[0].type, "pointermove");
assert.deepEqual([events[0].clientX, events[0].clientY], [410, 245], "host pixels must scale to CSS coordinates at non-default DPI");
forward(pointer("down")); forward(pointer("up"));
assert.equal(events.filter(event => event.type === "click").length, 1, "paired left clicks must reach ripple handlers");
forward(pointer("down")); forward(pointer("move", { x: 1000 })); forward(pointer("up", { x: 1000 }));
assert.equal(events.filter(event => event.type === "click").length, 1, "dragging must not turn into a click");
forward(pointer("up"));
assert.equal(events.filter(event => event.type === "click").length, 1, "an unpaired mouse-up must not invent a click");
forward(pointer("wheel", { deltaX: 0, deltaY: -120 }));
assert.equal(events.at(-1).type, "wheel"); assert.equal(events.at(-1).deltaY, -120);
forward(pointer("down", { button: 2 }));
assert.equal(events.at(-1).buttons, 2);
forward(null);
assert.equal(events.at(-1).type, "pointerleave");
forward(pointer("up", { button: 2 }));
const count = events.length;
for (const invalid of [pointer("keyboard"), pointer("leave"), pointer("move", { x: NaN }), pointer("move", { viewportWidth: 0 }), pointer("move", { y: -1 })]) assert.equal(forward(invalid), false);
assert.equal(events.length, count, "invalid coordinates/events must not reach visual handlers");
console.log("PASS host pointer: DPI scaling, event routing, click pairing, drag suppression, wheel, leave and invalid input");

// R2-29: the host forwards nothing while another window is in front and posts no leave when that
// starts (PointerGate.cs; AUD-49 removed the page's 'leave'), so UC3D attachMouse must not join the
// moves on either side of a foreground excursion into one delta. Original controls add each delta
// to a persistent velocity (dx/120 deg/s), so a stale point keeps the camera turning until timeout.
{
  const W = 1920, H = 1080;
  // Seconds. clock drives event.timeStamp (or performance.now); frame is the scene's frame clock,
  // which stops at the first move when frames pause (an occluded wallpaper draws no frames).
  let clock = 0, frame = 0, stamped = true, framesPaused = false;
  class TimedEvent { constructor(type, fields) { this.type = type; Object.assign(this, fields); if (stamped) this.timeStamp = clock * 1000; } }
  const degrees = (a, b) => Math.acos(Math.max(-1, Math.min(1, a.reduce((sum, n, i) => sum + n * b[i], 0) / Math.hypot(...a) / Math.hypot(...b)))) * 180 / Math.PI;
  // Camera turn rate between frames at 6 s and 8 s after host moves [seconds, x] at y 500.
  const turnRate = (moves) => {
    const listeners = new Map();
    const surface = { getBoundingClientRect: () => ({ left: 0, top: 0, width: W, height: H }),
      addEventListener: (name, listener) => listeners.set(name, listener), removeEventListener: name => listeners.delete(name),
      dispatchEvent: event => { listeners.get(event.type)?.(event); return true; } };
    const scene = createScene({ uc3dControls: "original", uc3dSeed: 12345, uc3dAmount: 50, volumetric: true, mouseRotation: true,
      codeRotation: false, forwardMovement: false, sidewaysMovement: false });
    const detach = attachMouse(surface, scene, () => frame);
    const send = createPointerForwarder(surface, { PointerEvent: TimedEvent, MouseEvent: TimedEvent, WheelEvent: TimedEvent });
    clock = frame = 0; scene.update(0);
    for (const [at, x] of moves) {
      clock = at; frame = framesPaused ? moves[0][0] : at;
      assert.equal(send({ eventType: "move", x, y: 500, viewportWidth: W, viewportHeight: H }), true);
    }
    const eye = at => { clock = frame = at; scene.update(at); return scene.snapshot(0).camera.eye; };
    const before = eye(6), after = eye(8);
    detach(); scene.dispose();
    return degrees(before, after) / 2;
  };
  // A still touch while the desktop is in front, an excursion, then the first move back 1750 px away.
  const excursion = [[1, 100], [1.03, 110], [4, 1850]];
  // The positive control, and the witness that moves reach the camera at all: one continuous
  // 1800 px drag at the bridge's 30 ms move interval.
  const drag = Array.from({ length: 61 }, (_, i) => [1 + i * 0.03, 100 + i * 30]);
  const realNow = performance.now, rates = {};
  try {
    for (const mode of ["event.timeStamp", "performance.now fallback", "event.timeStamp, frames paused"]) {
      stamped = mode !== "performance.now fallback"; framesPaused = mode.endsWith("frames paused");
      performance.now = stamped ? realNow : () => clock * 1000;
      const spun = turnRate(excursion), dragged = turnRate(drag);
      assert.ok(spun < 0.5, `${mode}: the move after a foreground excursion turned the camera ${spun.toFixed(2)} deg/s`);
      assert.ok(dragged > 10, `${mode}: a continuous 1800 px drag turned the camera only ${dragged.toFixed(2)} deg/s`);
      rates[mode] = { excursion: Number(spun.toFixed(3)), drag: Number(dragged.toFixed(3)) };
    }
  } finally { performance.now = realNow; }
  console.log("PASS UC3D mouse rotation: a move after a foreground excursion starts a new gesture; a continuous drag still turns the camera (deg/s)", JSON.stringify(rates));
}
