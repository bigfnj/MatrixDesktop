import makeConfig from "./config.js";
import { stopCamera } from "./camera.js";

const canvas = document.createElement("canvas");
document.body.appendChild(canvas);
document.addEventListener("touchmove", (e) => e.preventDefault(), {
	passive: false,
});

const supportsWebGPU = async () => {
	try {
		if (window.GPUQueue == null || navigator.gpu == null || navigator.gpu.getPreferredCanvasFormat == null) {
			return false;
		}
		const adapter = await navigator.gpu.requestAdapter();
		return adapter != null;
	} catch {
		return false;
	}
};

const isRunningSwiftShader = () => {
	// Some environments block WEBGL_debug_renderer_info for privacy reasons.
	// If we can't detect the renderer, treat it as "not SwiftShader" and continue.
	try {
		const gl = document.createElement("canvas").getContext("webgl");
		if (!gl) return false;
		const debugInfo = gl.getExtension("WEBGL_debug_renderer_info");
		if (!debugInfo) return false;
		const renderer = gl.getParameter(debugInfo.UNMASKED_RENDERER_WEBGL);
		return typeof renderer === "string" && renderer.toLowerCase().includes("swiftshader");
	} catch {
		return false;
	}
};

const loadRenderer = async (canvas, config, useWebGPU) => {
	if (useWebGPU) {
		try {
			const webgpuModule = await import("./webgpu/main.js");
			await webgpuModule.default(canvas, config);
			return;
		} catch (err) {
			console.warn("WebGPU renderer failed, falling back to regl:", err);
			stopCamera();
		}
	}
	const reglModule = await import("./regl/main.js");
	await reglModule.default(canvas, config);
};

document.body.onload = async () => {
	const urlParams = new URLSearchParams(window.location.search);
	const config = makeConfig(Object.fromEntries(urlParams.entries()));
	// Config check FIRST. && evaluates left to right, so asking supportsWebGPU() first
	// meant awaiting navigator.gpu.requestAdapter() on every launch even though the default
	// renderer is "regl", which initialises the D3D12 adapter enumeration and can spin up the
	// GPU process for nothing.
	const useWebGPU = config.renderer?.toLowerCase() === "webgpu" && (await supportsWebGPU());

	// suppressWarnings FIRST, same reason. isRunningSwiftShader() creates a second canvas
	// and a whole throwaway WebGL context just to read a renderer string, and never releases
	// it, so it burned one of the browser's live-context budget immediately before the real
	// context is created. Now skipped entirely when the answer cannot matter.
	if (!config.suppressWarnings && isRunningSwiftShader()) {
		const notice = document.createElement("notice");
		notice.innerHTML = `<div class="notice">
		<p>Wake up, Neo... you've got hardware acceleration disabled.</p>
		<p>This project will still run, incredibly, but at a noticeably low framerate.</p>
		<button class="blue pill">Plug me in</button>
		<a class="red pill" target="_blank" href="https://www.google.com/search?q=chrome+enable+hardware+acceleration">Free me</a>
		`;
		canvas.style.display = "none";
		document.body.appendChild(notice);
		document.querySelector(".blue.pill").addEventListener("click", async () => {
			try {
				config.suppressWarnings = true;
				urlParams.set("suppressWarnings", true);
				history.replaceState({}, "", "?" + urlParams.toString());
				await loadRenderer(canvas, config, useWebGPU);
				canvas.style.display = "unset";
				document.body.removeChild(notice);
			} catch (err) {
				stopCamera();
				console.error("Failed to load renderer:", err);
				const errorDiv = document.createElement("div");
				errorDiv.className = "notice";
				errorDiv.innerHTML = `<p>Error loading renderer: ${err.message}</p>`;
				document.body.appendChild(errorDiv);
			}
		});
	} else {
		try {
			await loadRenderer(canvas, config, useWebGPU);
		} catch (err) {
			stopCamera();
			console.error("Failed to load renderer:", err);
			const errorDiv = document.createElement("div");
			errorDiv.className = "notice";
			errorDiv.innerHTML = `<p>Error loading renderer: ${err.message}</p>`;
			document.body.appendChild(errorDiv);
		}
	}
};
