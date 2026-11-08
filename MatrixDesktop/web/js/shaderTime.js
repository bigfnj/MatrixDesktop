// AUD-64: the Matrix shaders read time as float32. The playback clock counts active seconds
// from page load and never goes back, so on a wallpaper that runs for days the time uniform
// loses precision: rain motion steps at 32 Hz after about 3 days of active time, and the
// WebGPU palette and stripe dither, which hashes uv plus raw seconds, turns into blocks
// within hours. The rain does not depend on where time 0 is, and only click stamps store
// times, so the renderers feed the shaders clock time minus an origin and move the origin
// forward. A move changes the rain pattern, so it happens when nobody saw the last frame:
// after a minute or more with no rendered frame (a host pause for lock, display off,
// suspend or a fullscreen app, or a hidden or suspended WebView), after a gap of a second
// or more once shader time passes an hour, and unconditionally past six hours. Shader time
// restarts at ten minutes, not zero: with skipIntro off the intro is a function of time,
// and restarting it would sweep its cursor wave over a screen already full of rain.
export const rebaseLimits = Object.freeze({ gapMs: 60_000, briefGapMs: 1_000, softSeconds: 3_600, hardSeconds: 21_600, restartSeconds: 600 });

export const createShaderTime = (limits = rebaseLimits) => {
	let origin = 0, lastWall = NaN;
	const listeners = new Set();
	return {
		// Once per rendered frame, with the clock's active seconds and the frame's wall time in
		// milliseconds. Returns the shader time for that frame.
		frame(seconds, wall) {
			const gap = wall - lastWall, time = seconds - origin;
			lastWall = wall;
			const unseen = gap >= limits.gapMs || (gap >= limits.briefGapMs && time >= limits.softSeconds);
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
