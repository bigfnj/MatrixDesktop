// AUD-69: a texture past the GPU's 2D limit fails validation on every frame and the canvas
// goes black, so a size past maxDimension is scaled down on both sides, aspect kept.
export const fitInside = (width, height, maxDimension) => {
	if (width <= maxDimension && height <= maxDimension) return [width, height];
	const factor = Math.min(maxDimension / width, maxDimension / height);
	const fit = (side) => Math.min(maxDimension, Math.max(1, Math.round(side * factor)));
	return [fit(width), fit(height)];
};

// CSS size is refreshed by ResizeObserver, not by every animation frame. DPR is read live
// in get(), so moving between monitors updates the backing size without a resize event.
export default (canvas, resolution, maxDimension = Infinity) => {
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
			return fitInside(Math.max(1, Math.ceil(width * scale)), Math.max(1, Math.ceil(height * scale)), maxDimension);
		},
		cleanup: () => {
			observer.disconnect();
			window.removeEventListener("resize", refresh);
		},
	};
};
