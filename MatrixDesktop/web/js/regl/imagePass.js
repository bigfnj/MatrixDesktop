import createResourceOwner, { ownRegl } from "../resourceOwner.js";
import { loadImage, loadText, makePassFBO, makePass } from "./utils.js";

// Multiplies the rendered rain and bloom by a loaded in image

const defaultBGURL = "https://upload.wikimedia.org/wikipedia/commons/thumb/0/0a/Flammarion_Colored.jpg/917px-Flammarion_Colored.jpg";

export default ({ regl: nativeRegl, config }, inputs) => {
	const owner = createResourceOwner();
	const regl = ownRegl(owner, nativeRegl);
	const output = makePassFBO(regl, config.useHalfFloat);
	// AUD-21: url= means the default image, and an image that cannot load (offline, blocked,
	// undecodable) leaves the rain uncoloured on a white texture instead of stopping the renderer.
	const bgURL = config.bgURL || defaultBGURL;
	const background = loadImage(regl, bgURL);
	let backgroundTex = background.texture;
	const backgroundLoaded = background.loaded.catch((error) => {
		if (owner.closed) throw error;
		const white = regl.texture([[255]]);
		backgroundTex = () => white;
		const message = `Background image ${bgURL} could not be loaded (${error?.message ?? error}); drawing the rain uncoloured.`;
		globalThis.matrixRuntimeDiagnostics?.errors?.push(message);
		console.warn(message);
	});
	const imagePassFrag = loadText("shaders/glsl/imagePass.frag.glsl");
	const render = regl({
		frag: regl.prop("frag"),
		uniforms: {
			backgroundTex: () => backgroundTex(),
			tex: inputs.primary,
			bloomTex: inputs.bloom,
		},
		framebuffer: output,
	});
	return makePass(
		{
			primary: output,
		},
		Promise.all([backgroundLoaded, imagePassFrag.loaded]),
		(w, h) => output.resize(w, h),
		(shouldRender) => {
			if (shouldRender) {
				render({ frag: imagePassFrag.text() });
			}
		},
		owner.cleanup
	);
};
