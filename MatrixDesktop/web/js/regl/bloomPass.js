import { loadText, makePassFBO, makePass } from "./utils.js";

// The bloom pass is basically an added high-pass blur.
// The blur approximation is the sum of a pyramid of downscaled, blurred textures.

const pyramidHeight = 5;

// A pyramid is just an array of FBOs, where each FBO is half the width
// and half the height of the FBO below it.
const makePyramid = (regl, height, halfFloat) =>
	Array(height)
		.fill()
		.map((_) => makePassFBO(regl, halfFloat));

const resizePyramid = (pyramid, vw, vh, scale) =>
	pyramid.forEach((fbo, index) => fbo.resize(Math.max(1, Math.floor((vw * scale) / 2 ** index)), Math.max(1, Math.floor((vh * scale) / 2 ** index))));

export default ({ regl, config }, inputs) => {
	const { bloomStrength, bloomSize, highPassThreshold } = config;
	const enabled = bloomSize > 0 && bloomStrength > 0;

	// If there's no bloom to apply, return a no-op pass with an empty bloom texture
	if (!enabled) {
		return makePass({
			primary: inputs.primary,
			bloom: makePassFBO(regl),
		});
	}

	// Build three pyramids of FBOs, one for each step in the process
	const highPassPyramid = makePyramid(regl, pyramidHeight, config.useHalfFloat);
	const hBlurPyramid = makePyramid(regl, pyramidHeight, config.useHalfFloat);
	const vBlurPyramid = makePyramid(regl, pyramidHeight, config.useHalfFloat);
	const output = makePassFBO(regl, config.useHalfFloat);

	// The high pass restricts the blur to bright things in our input texture.
	const highPassFrag = loadText("shaders/glsl/bloomPass.highPass.frag.glsl");
	const highPass = regl({
		frag: regl.prop("frag"),
		uniforms: {
			highPassThreshold,
			tex: regl.prop("tex"),
		},
		framebuffer: regl.prop("fbo"),
	});

	// A 2D gaussian blur is just a 1D blur done horizontally, then done vertically.
	// The FBO pyramid's levels represent separate levels of detail;
	// by blurring them all, this basic blur approximates a more complex gaussian:
	// https://web.archive.org/web/20191124072602/https://software.intel.com/en-us/articles/compute-shader-hdr-and-bloom

	const blurFrag = loadText("shaders/glsl/bloomPass.blur.frag.glsl");
	const blur = regl({
		frag: regl.prop("frag"),
		uniforms: {
			tex: regl.prop("tex"),
			direction: regl.prop("direction"),
			// DO NOT "FIX" THIS BY UNSWAPPING IT. The transposition is deliberate and
			// correct, and it was already reverted once after being mistaken for a bug.
			//
			// bloomPass.blur.frag.glsl computes
			//     size   = width > height ? vec2(width/height, 1.) : vec2(1., height/width)
			//     offset = direction / max(width, height) * size
			// and `size`'s components are in the OPPOSITE order from what an isotropic
			// one-texel step needs, so it has to be fed the transposed pair to cancel out.
			//
			// Worked through for a 1920x1080 target, direction [1,0] then [0,1]:
			//
			//   transposed (this code)      width=1080 height=1920
			//     1080 > 1920 is false  ->  size = (1, 1.778),  max = 1920
			//     horizontal  (1/1920)*1     * 1920 = 1.000 texel
			//     vertical    (1/1920)*1.778 * 1080 = 1.000 texel   <- isotropic
			//
			//   "unswapped"                 width=1920 height=1080
			//     1920 > 1080 is true   ->  size = (1.778, 1),  max = 1920
			//     horizontal  (1/1920)*1.778 * 1920 = 1.778 texels
			//     vertical    (1/1920)*1     * 1080 = 0.563 texels  <- 3.16x anisotropic
			//
			// So naming these after the viewport axis they carry would be clearer, but the
			// values must stay crossed unless the shader's `size` order changes with them.
			height: regl.context("viewportWidth"),
			width: regl.context("viewportHeight"),
		},
		framebuffer: regl.prop("fbo"),
	});

	// The pyramid of textures gets flattened (summed) into a final blurry "bloom" texture
	const combineFrag = loadText("shaders/glsl/bloomPass.combine.frag.glsl");
	const combine = regl({
		frag: regl.prop("frag"),
		uniforms: {
			bloomStrength,
			...Object.fromEntries(vBlurPyramid.map((fbo, index) => [`pyr_${index}`, fbo])),
		},
		framebuffer: output,
	});

	return makePass(
		{
			primary: inputs.primary,
			bloom: output,
		},
		// MD-43 (upstream): combineFrag was missing here, so the pass could report ready
		// while its combine shader was still loading. loadText returns "" until the fetch
		// resolves, and regl.frame starts as soon as every step's `ready` settles, so the
		// first combine draw could compile an EMPTY fragment shader. Identical in kind to
		// the rainPassEffect.loaded omission already fixed in the click-ripples commit.
		Promise.all([highPassFrag.loaded, blurFrag.loaded, combineFrag.loaded]),
		(w, h) => {
			// The blur pyramids can be lower resolution than the screen.
			resizePyramid(highPassPyramid, w, h, bloomSize);
			resizePyramid(hBlurPyramid, w, h, bloomSize);
			resizePyramid(vBlurPyramid, w, h, bloomSize);
			output.resize(w, h);
		},
		(shouldRender) => {
			if (!shouldRender) {
				return;
			}

			for (let i = 0; i < pyramidHeight; i++) {
				const highPassFBO = highPassPyramid[i];
				const hBlurFBO = hBlurPyramid[i];
				const vBlurFBO = vBlurPyramid[i];
				highPass({ fbo: highPassFBO, frag: highPassFrag.text(), tex: i === 0 ? inputs.primary : highPassPyramid[i - 1] });
				blur({ fbo: hBlurFBO, frag: blurFrag.text(), tex: highPassFBO, direction: [1, 0] });
				blur({ fbo: vBlurFBO, frag: blurFrag.text(), tex: hBlurFBO, direction: [0, 1] });
			}

			combine({ frag: combineFrag.text() });
		}
	);
};
