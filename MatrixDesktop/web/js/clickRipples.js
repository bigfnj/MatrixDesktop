const maxClickRipples = 5;
const inactiveTime = -1e9;

const clamp01 = (value) => Math.max(0, Math.min(1, value));

const createClickRipples = (canvas, enabled) => {
	const clicks = Array(maxClickRipples * 3).fill(0);
	const touches = Array(maxClickRipples)
		.fill()
		.map(() => [0, 0, inactiveTime, 0]);

	for (let i = 0; i < maxClickRipples; i++) {
		clicks[i * 3 + 2] = inactiveTime;
	}

	let index = 0;
	let changed = true;
	let start = performance.now();
	let aspectRatio = 1;

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
		setClick(x, y, (performance.now() - start) / 1000);
	};

	if (enabled) {
		start = performance.now();
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
