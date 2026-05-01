import makeConfig from "./config.js";

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
		}
	}
	const reglModule = await import("./regl/main.js");
	await reglModule.default(canvas, config);
};

document.body.onload = async () => {
	const urlParams = new URLSearchParams(window.location.search);
	const config = makeConfig(Object.fromEntries(urlParams.entries()));
	const useWebGPU = (await supportsWebGPU()) && ["webgpu"].includes(config.renderer?.toLowerCase());

	if (isRunningSwiftShader() && !config.suppressWarnings) {
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
			console.error("Failed to load renderer:", err);
			const errorDiv = document.createElement("div");
			errorDiv.className = "notice";
			errorDiv.innerHTML = `<p>Error loading renderer: ${err.message}</p>`;
			document.body.appendChild(errorDiv);
		}
	}
};
