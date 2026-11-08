import createResourceOwner, { ownDevice } from "../resourceOwner.js";
import { structs } from "../../lib/gpu-buffer.js";
import { makeComputeTarget, makeUniformBuffer, loadTexture, loadShader, makeBindGroup, makePass } from "./utils.js";

// Multiplies the rendered rain and bloom by a loaded in image

const defaultBGURL = "https://upload.wikimedia.org/wikipedia/commons/thumb/0/0a/Flammarion_Colored.jpg/917px-Flammarion_Colored.jpg";

export default ({ config, device: nativeDevice }) => {
	const owner = createResourceOwner();
	const device = ownDevice(owner, nativeDevice);

	// AUD-21: url= means the default image, and an image that cannot load (offline, blocked,
	// undecodable) leaves the rain uncoloured on a white texture instead of stopping the renderer.
	const bgURL = config.bgURL || defaultBGURL;
	const background = loadTexture(device, bgURL).catch((error) => {
		if (owner.closed) throw error;
		const white = device.createTexture({ size: [1, 1, 1], format: "rgba8unorm", usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST });
		device.queue.writeTexture({ texture: white }, new Uint8Array([255, 255, 255, 255]), { bytesPerRow: 4 }, [1, 1, 1]);
		const message = `Background image ${bgURL} could not be loaded (${error?.message ?? error}); drawing the rain uncoloured.`;
		globalThis.matrixRuntimeDiagnostics?.errors?.push(message);
		console.warn(message);
		return white;
	});
	const assets = [background, loadShader(device, "shaders/wgsl/imagePass.wgsl")];

	const linearSampler = device.createSampler({
		magFilter: "linear",
		minFilter: "linear",
	});

	let computePipeline;
	let configBuffer;
	let output;
	let screenSize;
	let backgroundTex;
	let computeBindGroup;

	const loaded = (async () => {
		const [bgTex, imageShader] = await Promise.all(assets);

		backgroundTex = bgTex;

		computePipeline = await device.createComputePipelineAsync({
			layout: "auto",
			compute: {
				module: imageShader.module,
				entryPoint: "computeMain",
			},
		});

		const configUniforms = structs.from(imageShader.code).Config;
		configBuffer = makeUniformBuffer(device, configUniforms, { unused: 0 });
	})();

	const build = (size, inputs) => {
		owner.release(output);
		output = makeComputeTarget(device, size);
		screenSize = size;
		computeBindGroup = makeBindGroup(device, computePipeline, 0, [
			configBuffer,
			linearSampler,
			inputs.primary.createView(),
			inputs.bloom.createView(),
			backgroundTex.createView(),
			output.createView(),
		]);
		return { primary: output };
	};

	const run = (encoder, shouldRender) => {
		if (!shouldRender) {
			return;
		}

		const computePass = encoder.beginComputePass();
		computePass.setPipeline(computePipeline);
		computePass.setBindGroup(0, computeBindGroup);
		computePass.dispatchWorkgroups(Math.ceil(screenSize[0] / 32), screenSize[1], 1);
		computePass.end();
	};

	return makePass("Image", loaded, build, run, owner.cleanup);
};
