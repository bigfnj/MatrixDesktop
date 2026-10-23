// CSS size is refreshed by ResizeObserver, not by every animation frame. DPR changes
// use a self-renewing media query because moving between monitors need not resize CSS.
export default (canvas, resolution) => {
	let width = canvas.clientWidth;
	let height = canvas.clientHeight;
	let query;
	let closed = false;
	const refresh = () => {
		width = canvas.clientWidth;
		height = canvas.clientHeight;
	};
	const dprChanged = () => {
		query?.removeEventListener("change", dprChanged);
		if (closed) return;
		query = window.matchMedia(`(resolution: ${window.devicePixelRatio ?? 1}dppx)`);
		query.addEventListener("change", dprChanged, { once: true });
	};
	const observer = new ResizeObserver(refresh);
	observer.observe(canvas);
	window.addEventListener("resize", refresh);
	dprChanged();
	return {
		get: () => {
			const scale = (window.devicePixelRatio ?? 1) * resolution;
			return [Math.max(1, Math.ceil(width * scale)), Math.max(1, Math.ceil(height * scale))];
		},
		cleanup: () => {
			closed = true;
			observer.disconnect();
			window.removeEventListener("resize", refresh);
			query?.removeEventListener("change", dprChanged);
		},
	};
};
