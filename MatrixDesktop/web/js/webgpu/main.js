import { structs } from "../../lib/gpu-buffer.js";
import { makeUniformBuffer, makePipeline } from "./utils.js";

import makeRain from "./rainPass.js";
import makeBloomPass from "./bloomPass.js";
import makePalettePass from "./palettePass.js";
import makeStripePass from "./stripePass.js";
import makeImagePass from "./imagePass.js";
import makeMirrorPass from "./mirrorPass.js";
import makeEndPass from "./endPass.js";
import { setupCamera, stopCamera, cameraCanvas, cameraAspectRatio, cameraSize } from "../camera.js";

const loadJS = (src) =>
	new Promise((resolve, reject) => {
		const tag = document.createElement("script");
		tag.onload = resolve;
		tag.onerror = reject;
		tag.src = src;
		document.body.appendChild(tag);
	});

const effects = {
	none: null,
	plain: makePalettePass,
	palette: makePalettePass,
	customStripes: makeStripePass,
	stripes: makeStripePass,
	pride: makeStripePass,
	transPride: makeStripePass,
	trans: makeStripePass,
	image: makeImagePass,
	mirror: makeMirrorPass,
};

export default async (canvas, config) => {
	await loadJS("lib/gl-matrix.js");
	let pipeline = null;
	let cleanedUp = false;

	const dblclickHandler = () => {
		if (document.fullscreenElement == null) {
			if (canvas.webkitRequestFullscreen != null) {
				canvas.webkitRequestFullscreen();
			} else {
				canvas.requestFullscreen();
			}
		} else {
			document.exitFullscreen();
		}
	};
	
	if (document.fullscreenEnabled || document.webkitFullscreenEnabled) {
		canvas.addEventListener("dblclick", dblclickHandler);
	}
	
	const cleanup = () => {
		if (cleanedUp) {
			return;
		}
		cleanedUp = true;
		if (document.fullscreenEnabled || document.webkitFullscreenEnabled) {
			canvas.removeEventListener("dblclick", dblclickHandler);
		}
		pipeline?.cleanup?.();
		if (config.useCamera) {
			stopCamera();
		}
	};

	if (config.useCamera) {
		await setupCamera();
	}

	const canvasFormat = navigator.gpu.getPreferredCanvasFormat();
	const adapter = await navigator.gpu.requestAdapter();
	if (!adapter) {
		throw new Error("WebGPU not supported: no GPU adapter available");
	}
	const device = await adapter.requestDevice();
	const canvasContext = canvas.getContext("webgpu");

	// console.table(device.limits);

	canvasContext.configure({
		device,
		format: canvasFormat,
		alphaMode: "opaque",
		usage:
			// GPUTextureUsage.STORAGE_BINDING |
			GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_DST,
	});

	const timeUniforms = structs.from(`struct Time { seconds : f32, frames : i32, };`).Time;
	const timeBuffer = makeUniformBuffer(device, timeUniforms);

	const cameraTex = config.useCamera
		? device.createTexture({
				size: cameraSize,
				format: "rgba8unorm",
				usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.RENDER_ATTACHMENT,
			})
		: device.createTexture({
				size: [1, 1, 1],
				format: "rgba8unorm",
				usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.RENDER_ATTACHMENT,
			});

	const context = {
		config,
		adapter,
		device,
		canvasContext,
		timeBuffer,
		canvasFormat,
		cameraTex,
		cameraAspectRatio,
		cameraSize,
		canvas,
	};

	const effectName = config.effect in effects ? config.effect : "palette";
	pipeline = await makePipeline(context, [makeRain, makeBloomPass, effects[effectName], makeEndPass]);

	const targetFrameTimeMilliseconds = 1000 / config.fps;
	let frames = 0;
	let start = NaN;
	let last = NaN;
	let outputs;
	const canvasSize = [0, 0];

	const renderLoop = (now) => {
		if (isNaN(start)) {
			start = now;
		}

		if (isNaN(last)) {
			last = start;
		}

		const shouldRender = config.fps >= 60 || now - last >= targetFrameTimeMilliseconds || config.once;
		if (shouldRender) {
			while (now - targetFrameTimeMilliseconds > last) {
				last += targetFrameTimeMilliseconds;
			}
		}

		const devicePixelRatio = window.devicePixelRatio ?? 1;
		const canvasWidth = Math.max(1, Math.ceil(canvas.clientWidth * devicePixelRatio * config.resolution));
		const canvasHeight = Math.max(1, Math.ceil(canvas.clientHeight * devicePixelRatio * config.resolution));
		canvasSize[0] = canvasWidth;
		canvasSize[1] = canvasHeight;
		if (canvas.width !== canvasWidth || canvas.height !== canvasHeight) {
			canvas.width = canvasWidth;
			canvas.height = canvasHeight;
			outputs = pipeline.build(canvasSize);
		}

		if (config.useCamera) {
			device.queue.copyExternalImageToTexture({ source: cameraCanvas }, { texture: cameraTex }, cameraSize);
		}

		device.queue.writeBuffer(timeBuffer, 0, timeUniforms.toBuffer({ seconds: (now - start) / 1000, frames }));
		frames++;

		const encoder = device.createCommandEncoder();
		pipeline.run(encoder, shouldRender);
		// Eventually, when WebGPU allows it, we'll remove the endPass and just copy from our pipeline's output to the canvas texture.
		// encoder.copyTextureToTexture({ texture: outputs?.primary }, { texture: canvasContext.getCurrentTexture() }, canvasSize);
		device.queue.submit([encoder.finish()]);

		if (!config.once) {
			requestAnimationFrame(renderLoop);
		} else {
			cleanup();
		}
	};

	requestAnimationFrame(renderLoop);
};
