import { unrecoverable } from "./rendererRecovery.js";

// R2-55: after repeated GPU resets Chromium refuses the page every new 3D context for the rest of
// the browser session, so a recovery reload starts a renderer that has nothing to draw with and
// only a restart (a new browser process) helps. A detached canvas asks without touching the page.
const webglRefused = (doc) => {
	try { return doc.createElement("canvas").getContext("webgl") == null; } catch { return false; }
};

// Chooses the renderer backend for main.js and owns the WebGPU-to-REGL fallback.
// A canvas WebGPU has claimed answers getContext("webgl") with null for good, even
// after unconfigure(), and createREGL then returns null without throwing, so the
// fallback runs on a fresh canvas that takes the claimed one's place in the page.
// Returns the canvas the running renderer draws to.
export default async (canvas, config, useWebGPU, {
	importWebGPU = () => import("./webgpu/main.js"),
	importRegl = () => import("./regl/main.js"),
	stopCamera = () => {},
} = {}) => {
	let webgpuError = null;
	if (useWebGPU) {
		try {
			const webgpuModule = await importWebGPU();
			await webgpuModule.default(canvas, config);
			return canvas;
		} catch (err) {
			if (["uc3d", "zmatrix"].includes(config.scene)) {
				// Shader and device failures in this optional scene must remain visible;
				// a working WebGL replacement would otherwise conceal a broken WebGPU pass.
				throw err;
			}
			console.warn("WebGPU renderer failed, falling back to regl:", err);
			stopCamera();
			webgpuError = err;
			const fresh = canvas.ownerDocument.createElement("canvas");
			canvas.replaceWith(fresh);
			canvas = fresh;
		}
	}
	const reason = (error) => error?.message ?? String(error);
	try {
		const reglModule = await importRegl();
		await reglModule.default(canvas, config);
	} catch (err) {
		const failure = webglRefused(canvas.ownerDocument) ? unrecoverable("the browser refused a new WebGL context", { cause: err }) : err;
		if (webgpuError == null) throw failure;
		throw new Error(`${reason(failure)} (after WebGPU failed: ${reason(webgpuError)})`, { cause: failure });
	} finally {
		// regl/main.js replaces the diagnostics object; keep why WebGPU was abandoned.
		if (webgpuError != null && globalThis.matrixRuntimeDiagnostics) {
			globalThis.matrixRuntimeDiagnostics.requestedRendererError = reason(webgpuError);
		}
	}
	return canvas;
};
