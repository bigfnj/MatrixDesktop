// CSS size is refreshed by ResizeObserver, not by every animation frame. DPR is read live
// in get(), so moving between monitors updates the backing size without a resize event.
export default (canvas, resolution) => {
	let width = canvas.clientWidth;
	let height = canvas.clientHeight;
	const refresh = () => {
		width = canvas.clientWidth;
		height = canvas.clientHeight;
	};
	const observer = new ResizeObserver(refresh);
	observer.observe(canvas);
	window.addEventListener("resize", refresh);
	return {
		get: () => {
			const scale = (window.devicePixelRatio ?? 1) * resolution;
			return [Math.max(1, Math.ceil(width * scale)), Math.max(1, Math.ceil(height * scale))];
		},
		cleanup: () => {
			observer.disconnect();
			window.removeEventListener("resize", refresh);
		},
	};
};
