import { makeFullScreenQuad, makePipeline } from "./utils.js";

import makeRain from "./rainPass.js";
import makeBloomPass from "./bloomPass.js";
import makePalettePass from "./palettePass.js";
import makeStripePass from "./stripePass.js";
import makeImagePass from "./imagePass.js";
import makeQuiltPass from "./quiltPass.js";
import makeMirrorPass from "./mirrorPass.js";
import { setupCamera, stopCamera, cameraCanvas, cameraAspectRatio } from "../camera.js";
import getLKG from "./lkgHelper.js";
import { createPlaybackClock } from "../hostPlayback.js";

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

const dimensions = { width: 1, height: 1 };

const loadJS = (src) =>
	new Promise((resolve, reject) => {
		const tag = document.createElement("script");
		tag.onload = resolve;
		tag.onerror = reject;
		tag.src = src;
		document.body.appendChild(tag);
	});

export default async (canvas, config) => {
	if (config.scene === "zmatrix") {
		return (await import("./zmatrixPass.js")).default(canvas, config);
	}
	if (config.scene === "uc3d") {
		return (await import("./uc3dPass.js")).default(canvas, config);
	}
	await Promise.all([loadJS("lib/regl.min.js"), loadJS("lib/gl-matrix.min.js")]);
	const clock = createPlaybackClock();
	const diagnostics = { scene: "matrix", backend: "regl", activeRenderer: "regl", requestedRenderer: config.renderer,
		initialized: false, ready: false, frames: 0, disposed: false, errors: [] };
	window.matrixRuntimeDiagnostics = diagnostics; window.__matrixRuntime = diagnostics;
	canvas.dataset.scene = "matrix"; canvas.dataset.renderer = "regl";

	const resize = () => {
		const devicePixelRatio = window.devicePixelRatio ?? 1;
		canvas.width = Math.max(1, Math.ceil(canvas.clientWidth * devicePixelRatio * config.resolution));
		canvas.height = Math.max(1, Math.ceil(canvas.clientHeight * devicePixelRatio * config.resolution));
	};
	window.addEventListener("resize", resize);
	
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
	
	let pipeline = [];
	let cleanedUp = false;
	let regl;
	let tick;
	const cleanup = () => {
		if (cleanedUp) {
			return;
		}
		cleanedUp = true;
		diagnostics.ready = false; diagnostics.disposed = true;
		clock.dispose();
		window.removeEventListener("pagehide", cleanup);
		tick?.cancel();
		window.removeEventListener("resize", resize);
		if (document.fullscreenEnabled || document.webkitFullscreenEnabled) {
			canvas.removeEventListener("dblclick", dblclickHandler);
		}
		for (const step of pipeline) {
			step.cleanup?.();
		}
		if (config.useCamera) {
			stopCamera();
		}
		regl?.destroy();
	};
	resize();

	// MD-25: cleanup() was only reachable when config.once was set, so in normal operation
	// none of the teardown this codebase already writes ever ran: the mirror pass's window
	// click listener, the click-ripple canvas listener, the resize listener, and
	// stopCamera(), which is what actually releases the webcam and turns its indicator off.
	// pagehide covers navigation and window close, and it fires for the configurator's
	// preview iframe every time that reloads.
	window.addEventListener("pagehide", cleanup, { once: true });
	try {

	if (config.useCamera) {
		await setupCamera();
	}

	const extensions = ["OES_texture_half_float", "OES_texture_half_float_linear"];
	// These extensions are also needed, but Safari misreports that they are missing
	const optionalExtensions = ["EXT_color_buffer_half_float", "WEBGL_color_buffer_float", "OES_standard_derivatives"];

	switch (config.testFix) {
		case "fwidth_10_1_2022_A":
			extensions.push("OES_standard_derivatives");
			break;
		case "fwidth_10_1_2022_B":
			// Move all optional extensions to required, then clear optional
			optionalExtensions.forEach((ext) => extensions.push(ext));
			optionalExtensions.length = 0;
			break;
	}

	regl = createREGL({ canvas, pixelRatio: 1, extensions, optionalExtensions });

	const cameraTex = regl.texture(cameraCanvas);
	const lkg = await getLKG(config.useHoloplay);
	if (cleanedUp) { regl.destroy(); return; }

	// All this takes place in a full screen quad.
	const fullScreenQuad = makeFullScreenQuad(regl);
	let activeFrames = 0;
	const withClock = regl({ context: { time: () => clock.seconds(), tick: () => activeFrames } });
	const effectName = config.effect in effects ? config.effect : "palette";
	const context = { regl, config, lkg, cameraTex, cameraAspectRatio, canvas, elapsedSeconds: () => clock.seconds() };
	pipeline = makePipeline(context, [makeRain, makeBloomPass, effects[effectName], makeQuiltPass]);
	const screenUniforms = { tex: pipeline[pipeline.length - 1].outputs.primary };
	const drawToScreen = regl({ uniforms: screenUniforms });
	await Promise.all(pipeline.map((step) => step.ready));
	if (cleanedUp) { for (const step of pipeline) step.cleanup?.(); regl.destroy(); return; }

	const targetFrameTimeMilliseconds = 1000 / config.fps;
	let last = NaN;
	diagnostics.initialized = true;
	diagnostics.dispose = cleanup;

	tick = regl.frame(({ viewportWidth, viewportHeight }) => {
		if (clock.paused) return;
		if (config.once) {
			tick.cancel();
		}

		const now = clock.seconds() * 1000;

		if (isNaN(last)) {
			last = now;
		}

		const shouldRender = config.fps >= 60 || now - last >= targetFrameTimeMilliseconds || config.once == true;

		if (shouldRender) {
			while (now - targetFrameTimeMilliseconds > last) {
				last += targetFrameTimeMilliseconds;
			}
		}

		if (config.useCamera) {
			cameraTex(cameraCanvas);
		}
		if (dimensions.width !== viewportWidth || dimensions.height !== viewportHeight) {
			dimensions.width = viewportWidth;
			dimensions.height = viewportHeight;
			for (const step of pipeline) {
				step.setSize(viewportWidth, viewportHeight);
			}
		}
		withClock(() => fullScreenQuad(() => {
			for (const step of pipeline) {
				step.execute(shouldRender);
			}
			drawToScreen();
		}));
		activeFrames++;
		diagnostics.frames = activeFrames; diagnostics.ready = true;
		diagnostics.simulationTime = clock.seconds();
		if (config.once) {
			// Keep the rendered still frame visible; pagehide owns final GPU teardown.
			if (config.useCamera) stopCamera();
		}
	});
	} catch (error) {
		diagnostics.errors.push(error.message);
		cleanup();
		throw error;
	}
};
