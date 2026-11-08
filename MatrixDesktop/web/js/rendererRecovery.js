// Recovers from a lost GPU context or device by reloading the page, within a budget.
// Vendored REGL restores a lost WebGL context with blank textures (black output) and the
// UC3D and ZMatrix scenes dispose themselves on loss, so a reload is the one recovery that
// works for every scene. The budget lives in sessionStorage because it must survive the
// reloads it counts; without storage there is no budget, so no reload either.
export const RECOVERY_KEY = "matrixRendererRecoveries";

export default ({
	target, // window: capture listeners see context events from any canvas in the page
	storage, // () => sessionStorage; may throw
	reload,
	showError,
	now = () => Date.now(),
	setTimer = setTimeout,
	clearTimer = clearTimeout,
	budget = 3,
	windowMs = 5 * 60 * 1000,
	restoreWaitMs = 10000,
}) => {
	let waiting = null;
	let done = false;
	const recover = (reason) => {
		if (done) return;
		done = true;
		clearTimer(waiting);
		try {
			const store = storage();
			const time = now();
			let stamps;
			try { stamps = JSON.parse(store.getItem(RECOVERY_KEY)); } catch { stamps = null; }
			const recent = (Array.isArray(stamps) ? stamps : []).filter((stamp) => typeof stamp === "number" && time - stamp < windowMs);
			if (recent.length < budget) {
				store.setItem(RECOVERY_KEY, JSON.stringify([...recent, time]));
				reload();
				return;
			}
		} catch {
			// No storage: an unbounded reload loop would be worse than the notice.
		}
		showError(new Error(`the GPU renderer was lost (${reason}) and could not be recovered automatically. Restart MatrixDesktop to try again.`));
	};
	// preventDefault asks the browser to restore the context; restoration then reloads.
	target.addEventListener("webglcontextlost", (event) => {
		event.preventDefault();
		if (waiting == null && !done) waiting = setTimer(() => recover("WebGL context lost and not restored"), restoreWaitMs);
	}, true);
	target.addEventListener("webglcontextrestored", () => recover("WebGL context lost"), true);
	// A WebGPU device loss has no restore event. A pending WebGL loss already waits for one.
	return (reason) => {
		if (waiting == null) recover(reason ?? "renderer lost");
	};
};
