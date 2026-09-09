const maxClickRipples = 5;
const inactiveTime = -1e9;

const clamp01 = (value) => Math.max(0, Math.min(1, value));

// Tracks recent canvas clicks so the rain shaders can draw a ripple from each one.
//
// MD-04: this used to stamp each click against its OWN performance.now() origin, captured
// when the pass was constructed, while the shaders compared those stamps against the
// renderer's clock. Those are two different origins:
//
//   REGL     the shader's `time` uniform is regl.context("time")
//   WebGPU   main.js sets `start` on the FIRST rAF frame, which happens after
//            `await makePipeline` has loaded roughly 1.9 MB of MSDF textures
//
// So on WebGPU every click was stamped earlier than the clock the shader read, the
// shader's `elapsedTime >= 0` guard hid the ripple until the difference elapsed, and the
// ripple appeared late by however long asset loading took. Cold start made it worse.
//
// Rather than trying to reconstruct the renderer's origin here, the render loop now hands
// its own current time in via syncTime, which both renderers already have exactly. There
// is only one clock, so the two cannot drift by construction.
const createClickRipples = (canvas, enabled, initialTime = 0) => {
	const clicks = Array(maxClickRipples * 3).fill(0);
	const touches = Array(maxClickRipples)
		.fill()
		.map(() => [0, 0, inactiveTime, 0]);

	for (let i = 0; i < maxClickRipples; i++) {
		clicks[i * 3 + 2] = inactiveTime;
	}

	let index = 0;
	let changed = true;
	let aspectRatio = 1;

	// The renderer's clock, in the same units and with the same origin the shaders see.
	// Updated once per frame by whichever pass uploads the ripple data.
	let currentTime = initialTime;

	const setClick = (x, y, time) => {
		clicks[index * 3 + 0] = x;
		clicks[index * 3 + 1] = y;
		clicks[index * 3 + 2] = time;
		touches[index][0] = x;
		touches[index][1] = y;
		touches[index][2] = time;
		index = (index + 1) % maxClickRipples;
		changed = true;
	};

	const clickHandler = (event) => {
		const rect = canvas.getBoundingClientRect();
		if (rect.width <= 0 || rect.height <= 0) {
			return;
		}

		const x = clamp01((event.clientX - rect.left) / rect.width);
		const y = clamp01(1 - (event.clientY - rect.top) / rect.height);
		setClick(x, y, currentTime);
	};

	if (enabled) {
		canvas.addEventListener("click", clickHandler);
	}

	return {
		clicks,
		touches,
		get changed() {
			return changed;
		},
		get aspectRatio() {
			return aspectRatio;
		},
		// Called every frame with the renderer's current time. Cheap by design: this runs
		// inside the same callback that uploads the ripple uniforms.
		syncTime(time) {
			if (Number.isFinite(time)) {
				currentTime = time;
			}
		},
		setAspectRatio(value) {
			const next = Number.isFinite(value) && value > 0 ? value : 1;
			if (aspectRatio !== next) {
				aspectRatio = next;
				changed = true;
			}
		},
		markClean() {
			changed = false;
		},
		cleanup() {
			if (enabled) {
				canvas.removeEventListener("click", clickHandler);
			}
		},
	};
};

export { maxClickRipples };
export default createClickRipples;
