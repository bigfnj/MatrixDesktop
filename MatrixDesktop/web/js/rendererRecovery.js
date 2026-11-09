// Recovers from a lost GPU context or device by reloading the page, within a budget.
// Vendored REGL restores a lost WebGL context with blank textures (black output) and the
// UC3D and ZMatrix scenes dispose themselves on loss, so a reload is the one recovery that
// works for every scene. The budget lives in sessionStorage because it must survive the
// reloads it counts; without storage there is no budget, so no reload either.
// The AUD-19 contract: a renderer stops reporting matrixRuntimeDiagnostics.ready on a loss
// and never reports it again, and this module brings the page back drawing. It reloads as
// soon as a lost WebGL context is restored (from its own capture listener, whether or not
// a renderer called the hook), 10 s after a WebGL loss that is never restored, and at once
// after a WebGPU device loss. Past the budget it shows a persistent notice instead.
export const RECOVERY_KEY = "matrixRendererRecoveries";

// The notice for a renderer the page cannot bring back: the budget below is spent, or the browser
// refuses the page a new WebGL context, which no reload can change (rendererLoader.js, R2-55).
export const unrecoverable = (reason, options) =>
	new Error(`the GPU renderer was lost (${reason}) and could not be recovered automatically. Restart MatrixDesktop to try again.`, options);

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
		showError(unrecoverable(reason));
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
