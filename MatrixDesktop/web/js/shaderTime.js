// AUD-64: the Matrix shaders read time as float32. The playback clock counts active seconds
// from page load and never goes back, so on a wallpaper that runs for days the time uniform
// loses precision: rain motion steps at 32 Hz after about 3 days of active time, and the
// WebGPU palette and stripe dither, which hashes uv plus raw seconds, turns into blocks
// within hours. The rain does not depend on where time 0 is, and only click stamps store
// times, so the renderers feed the shaders clock time minus an origin and move the origin
// forward. A move changes the rain pattern, so it happens when nobody saw the last frame:
// after a minute or more with no rendered frame while the page was hidden (a host pause for
// lock, display off or suspend hides the WebView first, and a hidden or occluded page stops
// drawing), after such a gap of a second or more once shader time passes an hour, and
// unconditionally past six hours. R2-19: a manual, fullscreen-app or battery pause keeps the
// paused frame on screen, so a gap without a visibility change never moves the origin.
// Shader time restarts at ten minutes, not zero, or later for a slow intro (introSafeLimits):
// with skipIntro off the intro is a function of time, and restarting it would sweep its
// cursor wave over a screen already full of rain.
export const rebaseLimits = Object.freeze({ gapMs: 60_000, briefGapMs: 1_000, softSeconds: 3_600, hardSeconds: 21_600, restartSeconds: 600 });

export const createShaderTime = (limits = rebaseLimits) => {
	let origin = 0, lastWall = NaN, lastEpoch = 0;
	const listeners = new Set();
	return {
		// Once per rendered frame, with the clock's active seconds, the frame's wall time in
		// milliseconds and the page's unseen epoch (js/hostPlayback.js), which moves whenever
		// the page's visibility changes. Returns the shader time for that frame.
		frame(seconds, wall, epoch = lastEpoch) {
			const gap = wall - lastWall, time = seconds - origin, hidden = epoch !== lastEpoch;
			lastWall = wall; lastEpoch = epoch;
			const unseen = hidden && (gap >= limits.gapMs || (gap >= limits.briefGapMs && time >= limits.softSeconds));
			if (time > limits.restartSeconds && (unseen || time >= limits.hardSeconds)) {
				const shift = time - limits.restartSeconds;
				origin += shift;
				for (const listener of listeners) listener(shift);
			}
			return seconds - origin;
		},
		// Shader time for a clock reading between frames, such as a click.
		seconds: (seconds) => seconds - origin,
		// listener(shift) runs when shader time moves back by shift seconds, so stored stamps follow.
		onRebase(listener) {
			listeners.add(listener);
			return () => listeners.delete(listener);
		},
	};
};

// R2-20: with skipIntro off, the rain pass draws a cell the intro front has not reached with
// extra brightness (1 - 5 * introProgress) over its sticky activated flag, so restarting shader
// time inside a slow intro whites out the screen under a new cursor wave. A column's intro
// passes the bottom row once (simTime + offset) * fallSpeed / numRows * 100 reaches
// 1.2 + 1 / numRows. The rain grid has numColumns rows, the column offset goes down to -10.5
// (WebGPU reaches the lower half of its sine term at volumetric density above 1), and two
// negative speeds progress the same way. Shader time restarts a minute after the slowest column
// is done; the soft and hard limits move by the same amount, so the hard rule never pins time
// at the restart point. skipIntro keeps the plain limits.
export const introSafeLimits = (config, numRows = config.numColumns) => {
	if (config.skipIntro) return rebaseLimits;
	const simSeconds = ((1.2 + 1 / numRows) * numRows) / (100 * Math.abs(config.fallSpeed)) + 10.5;
	const raise = Math.max(0, simSeconds / Math.abs(config.animationSpeed) + 60 - rebaseLimits.restartSeconds);
	return Object.freeze({ ...rebaseLimits, softSeconds: rebaseLimits.softSeconds + raise,
		hardSeconds: rebaseLimits.hardSeconds + raise, restartSeconds: rebaseLimits.restartSeconds + raise });
};

// AUD-65: regl feeds the frame counter to the shaders as the float32 tick uniform, and
// rainPass.symbol.frag.glsl cycles glyphs when mod(tick, cycleFrameSkip) == 0. Float32 holds
// every integer only up to 2^24 (78 h at 60 Hz, 32 h at 144 Hz); past that, Operator and
// Palimpsest glyphs cycle 3x too fast, then 5x and 8x. The counter wraps at cycleFrameSkip *
// 2^20, a multiple of both the skip and 2 (the ping-pong buffers pick a side by tick % 2),
// and 0 and 1 stay unique to the first frames, which the shaders treat as tick <= 1.
export const shaderTick = (frames, cycleFrameSkip = 1) => {
	if (frames < 2) return frames;
	const skip = Number.isInteger(cycleFrameSkip) && cycleFrameSkip >= 1 && cycleFrameSkip < 16 ? cycleFrameSkip : 1;
	return 2 + ((frames - 2) % (skip * 2 ** 20));
};
