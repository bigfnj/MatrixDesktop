import { loadText, makePassFBO, makePass } from "./utils.js";

const numClicks = 5;

export default ({ regl, config, cameraTex, cameraAspectRatio, canvas }, inputs) => {
	const clicks = Array(numClicks).fill([0, 0, -Infinity]).flat();
	let aspectRatio = 1;
	let start = Date.now();

	let index = 0;
	// MD-48 (upstream): this used e.srcElement, a non-standard alias for e.target, on a
	// WINDOW-level listener. It therefore fired for a click on any element and divided by
	// that element's size, so clicking the software-rendering notice divided by zero and
	// pushed Infinity or NaN into the clicks uniform. Measure against the canvas, whose
	// coordinate space these values actually describe, and ignore a degenerate rect.
	const clickHandler = (e) => {
		const rect = canvas.getBoundingClientRect();
		if (rect.width <= 0 || rect.height <= 0) {
			return;
		}
		clicks[index * 3 + 0] = (e.clientX - rect.left) / rect.width;
		clicks[index * 3 + 1] = 1 - (e.clientY - rect.top) / rect.height;
		clicks[index * 3 + 2] = (Date.now() - start) / 1000;
		index = (index + 1) % numClicks;
	};
	window.addEventListener("click", clickHandler);

	const output = makePassFBO(regl, config.useHalfFloat);
	const mirrorPassFrag = loadText("shaders/glsl/mirrorPass.frag.glsl");
	const render = regl({
		frag: regl.prop("frag"),
		uniforms: {
			time: regl.context("time"),
			tex: inputs.primary,
			bloomTex: inputs.bloom,
			cameraTex,
			clicks: () => clicks,
			aspectRatio: () => aspectRatio,
			cameraAspectRatio,
		},
		framebuffer: output,
	});

	start = Date.now();

	return makePass(
		{
			primary: output,
		},
		Promise.all([mirrorPassFrag.loaded]),
		(w, h) => {
			output.resize(w, h);
			aspectRatio = w / h;
		},
			(shouldRender) => {
				if (shouldRender) {
					render({ frag: mirrorPassFrag.text() });
				}
			},
			() => window.removeEventListener("click", clickHandler)
		);
	};
