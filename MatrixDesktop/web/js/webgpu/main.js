import createCanvasSize from "../canvasSize.js";
import { createPlaybackClock, subscribeHostPlayback, unseenEpoch } from "../hostPlayback.js";
import { createShaderTime, introSafeLimits } from "../shaderTime.js";
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
	if (config.scene === "zmatrix") {
		return (await import("./zmatrixPass.js")).default(canvas, config);
	}
	if (config.scene === "uc3d") {
		return (await import("./uc3dPass.js")).default(canvas, config);
	}
	await loadJS("lib/gl-matrix.min.js");
	const clock = createPlaybackClock();
	const diagnostics = { scene: "matrix", backend: "webgpu", activeRenderer: "webgpu", requestedRenderer: config.renderer,
		initialized: false, ready: false, frames: 0, disposed: false, errors: [] };
	window.matrixRuntimeDiagnostics = diagnostics; window.__matrixRuntime = diagnostics;
	canvas.dataset.scene = "matrix"; canvas.dataset.renderer = "webgpu";
	let pipeline = null;
	let cleanedUp = false;
	let initialized = false;
	let device;
	let canvasContext;
	let frameRequest;
	let parked = false, stopListening = null;
	let sizeTracker;
	let gpuFailed = false;
	let gpuErrors = 0;
	let deviceLost = false;
	const destroyContext = () => {
		canvasContext?.unconfigure();
		device?.destroy();
	};

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
	
	// MD-45: attach only once the device exists. This used to run before
	// requestAdapter/requestDevice, which throw when WebGPU is unavailable, and cleanup()
	// is not invoked on that path. js/main.js catches and falls through to the regl
	// renderer, which attaches its OWN dblclick handler to the same canvas, so after a
	// fallback a double-click toggled fullscreen twice.
	const attachFullscreenToggle = () => {
		if (document.fullscreenEnabled || document.webkitFullscreenEnabled) {
			canvas.addEventListener("dblclick", dblclickHandler);
		}
	};
	
	const cleanup = () => {
		if (cleanedUp) {
			return;
		}
		cleanedUp = true;
		diagnostics.ready = false; diagnostics.disposed = true;
		clock.dispose();
		window.removeEventListener("pagehide", cleanup);
		cancelAnimationFrame(frameRequest);
		stopListening?.();
		sizeTracker?.cleanup();
		if (document.fullscreenEnabled || document.webkitFullscreenEnabled) {
			canvas.removeEventListener("dblclick", dblclickHandler);
		}
		pipeline?.cleanup?.();
		if (initialized) destroyContext();
		if (config.useCamera) {
			stopCamera();
		}
	};

	// MD-25: cleanup() was only reachable when config.once was set, so in normal operation
	// none of the teardown this codebase already writes ever ran: the mirror pass's window
	// click listener, the click-ripple canvas listener, and stopCamera(), which is what
	// actually releases the webcam and turns its indicator off. pagehide covers navigation
	// and window close, and it fires for the configurator's preview iframe every reload.
	window.addEventListener("pagehide", cleanup, { once: true });
	try {

	if (config.useCamera) {
		await setupCamera();
	}

	const canvasFormat = navigator.gpu.getPreferredCanvasFormat();
	const adapter = await navigator.gpu.requestAdapter();
	if (!adapter) {
		throw new Error("WebGPU not supported: no GPU adapter available");
	}
	// AUD-69: the default device limit is 8192 even when the adapter allows more, and a
	// canvas past the device limit failed validation every frame while ready stayed true.
	device = await adapter.requestDevice({ requiredLimits: { maxTextureDimension2D: adapter.limits.maxTextureDimension2D } });
	device.addEventListener("uncapturederror", (event) => {
		if (cleanedUp) return;
		gpuFailed = true; diagnostics.ready = false;
		if (++gpuErrors <= 10) diagnostics.errors.push(`WebGPU validation: ${event.error.message}`);
	});
	// AUD-19: "destroyed" is our own teardown. Any other loss (a GPU process restart or a
	// driver reset) leaves this device dead, so stop drawing and tell the page, which owns
	// recovery (js/main.js).
	device.lost.then((info) => {
		if (cleanedUp || info.reason === "destroyed") return;
		deviceLost = true; gpuFailed = true; diagnostics.ready = false;
		diagnostics.errors.push(`WebGPU device lost (${info.reason}): ${info.message}`);
		cancelAnimationFrame(frameRequest);
		window.matrixRendererLost?.(`WebGPU device lost (${info.reason})`);
	});
	canvasContext = canvas.getContext("webgpu");

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

	// Safe now: the device exists, so this function cannot be reached by a fallback path.
	attachFullscreenToggle();

	// MD-04: one source of truth for "what time does the shader think it is". This value
	// goes into the time buffer each frame AND is handed to the rain pass, so a click stamp
	// and the shader clock cannot come from different origins. `start` is set on the first
	// rAF frame, so before then this reads 0 rather than NaN.
	// AUD-64: the f32 seconds count from an origin that moves forward (js/shaderTime.js).
	let start = NaN;
	const shaderTime = createShaderTime(introSafeLimits(config));
	const elapsedSeconds = () => (Number.isNaN(start) ? 0 : shaderTime.seconds(clock.seconds() - start / 1000));

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
		elapsedSeconds,
		onTimeRebase: shaderTime.onRebase,
	};

	const effectName = Object.hasOwn(effects, config.effect) ? config.effect : "palette";
	pipeline = await makePipeline(context, [makeRain, makeBloomPass, effects[effectName], makeEndPass]);

	const targetFrameTimeMilliseconds = 1000 / config.fps;
	// AUD-47: fps 0 made the frame time Infinity, so nothing ever rendered and the canvas
	// stayed black. Like UC3D and ZMatrix, it now draws one still frame, as once does.
	const singleFrame = config.once || !(config.fps > 0);
	let frames = 0;
	let last = NaN;

	let outputs;
	let built = false;
	const canvasSize = [0, 0];
	sizeTracker = createCanvasSize(canvas, config.resolution, device.limits.maxTextureDimension2D);

	const renderLoop = (now) => {
		if (cleanedUp || deviceLost) return;
		const wall = now;
		now = clock.seconds(now) * 1000;
		// R2-24: a visible host pause (AUD-06) parks the loop instead of waking it at the display
		// rate; the resume listener below requests the next frame.
		if (clock.paused) { parked = true; return; }
		if (isNaN(start)) {
			start = now;
		}

		if (isNaN(last)) {
			last = start;
		}

		const shouldRender = config.fps >= 60 || now - last >= targetFrameTimeMilliseconds || singleFrame;
		if (shouldRender) {
			while (now - targetFrameTimeMilliseconds > last) {
				last += targetFrameTimeMilliseconds;
			}
		}

		const [canvasWidth, canvasHeight] = sizeTracker.get();
		canvasSize[0] = canvasWidth;
		canvasSize[1] = canvasHeight;
		if (!built || canvas.width !== canvasWidth || canvas.height !== canvasHeight) {
			canvas.width = canvasWidth;
			canvas.height = canvasHeight;
			outputs = pipeline.build(canvasSize);
			built = true;
		}

		if (config.useCamera) {
			device.queue.copyExternalImageToTexture({ source: cameraCanvas }, { texture: cameraTex }, cameraSize);
		}

		device.queue.writeBuffer(timeBuffer, 0, timeUniforms.toBuffer({ seconds: shaderTime.frame((now - start) / 1000, wall, unseenEpoch()), frames }));
		frames++;

		const encoder = device.createCommandEncoder();
		pipeline.run(encoder, shouldRender);
		// Eventually, when WebGPU allows it, we'll remove the endPass and just copy from our pipeline's output to the canvas texture.
		// encoder.copyTextureToTexture({ texture: outputs?.primary }, { texture: canvasContext.getCurrentTexture() }, canvasSize);
		device.queue.submit([encoder.finish()]);
		diagnostics.frames = frames; diagnostics.ready = !gpuFailed; diagnostics.simulationTime = (now - start) / 1000;

		if (!singleFrame) {
			frameRequest = requestAnimationFrame(renderLoop);
		} else {
			// Keep the rendered still frame visible; pagehide owns final GPU teardown.
			if (config.useCamera) stopCamera();
		}
	};

	initialized = true;
	diagnostics.initialized = true;
	diagnostics.dispose = cleanup;
	if (cleanedUp) {
		pipeline?.cleanup?.();
		sizeTracker?.cleanup();
		destroyContext();
		return;
	}
	stopListening = subscribeHostPlayback((paused) => {
		if (paused || !parked || cleanedUp || deviceLost) return;
		parked = false;
		frameRequest = requestAnimationFrame(renderLoop);
	});
	frameRequest = requestAnimationFrame(renderLoop);
	} catch (error) {
		diagnostics.errors.push(error.message);
		const closing = cleanedUp;
		initialized = true;
		cleanup();
		destroyContext();
		if (!closing) throw error;
	}
};
