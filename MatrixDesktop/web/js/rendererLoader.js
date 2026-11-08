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
		if (webgpuError == null) throw err;
		throw new Error(`${reason(err)} (after WebGPU failed: ${reason(webgpuError)})`, { cause: err });
	} finally {
		// regl/main.js replaces the diagnostics object; keep why WebGPU was abandoned.
		if (webgpuError != null && globalThis.matrixRuntimeDiagnostics) {
			globalThis.matrixRuntimeDiagnostics.requestedRendererError = reason(webgpuError);
		}
	}
	return canvas;
};
