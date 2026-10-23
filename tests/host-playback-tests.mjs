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
