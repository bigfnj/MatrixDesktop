import makeConfig from "./config.js";
import { wantsWebGPU } from "./rendererChoice.js";
import { stopCamera } from "./camera.js";
import loadBackend from "./rendererLoader.js";
import installRecovery from "./rendererRecovery.js";

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

// The REGL fallback after a WebGPU failure gets a fresh canvas (see rendererLoader.js);
// hostPlayback.js looks the canvas up per message, so nothing else holds the old one.
const loadRenderer = (canvas, config, useWebGPU) => loadBackend(canvas, config, useWebGPU, { stopCamera });

const showError = (error) => {
	stopCamera();
	console.error("Failed to load renderer:", error);
	// The canvas fills the viewport, so a notice placed after it would land below the fold.
	for (const canvas of document.querySelectorAll("canvas")) canvas.style.display = "none";
	const notice = document.createElement("div");
	notice.className = "notice";
	const message = document.createElement("p");
	message.textContent = `Error loading renderer: ${error?.message ?? String(error)}`;
	notice.appendChild(message);
	document.body.appendChild(notice);
};

// A lost GPU context or device reloads the page within a budget, then leaves a notice
// (see rendererRecovery.js). Renderers call window.matrixRendererLost?.(reason). The native
// bootstrap only matches the query the page was opened with, so a reload first undoes
// Plug me in's history rewrite.
const startupSearch = location.search;
window.matrixRendererLost = installRecovery({
	target: window,
	storage: () => window.sessionStorage,
	reload: () => {
		if (location.search !== startupSearch) history.replaceState(history.state, "", location.pathname + startupSearch);
		location.reload();
	},
	showError,
});

// R2-23: Plug me in's consent must outlive that reload and the host's renderer reload, which
// both return to the startup query, or the notice comes back over a hidden canvas and no
// renderer starts. It holds for this tab's session and this exact configuration only, so a
// fresh launch or a changed configuration still warns. Storage can throw; without it the
// notice simply returns.
const CONSENT_KEY = "matrixSoftwareRenderingConsent";
const consentGiven = () => {
	try { return window.sessionStorage.getItem(CONSENT_KEY) === startupSearch; }
	catch { return false; }
};
const recordConsent = () => {
	try { window.sessionStorage.setItem(CONSENT_KEY, startupSearch); }
	catch { /* the notice returns after a reload */ }
};

document.body.onload = async () => {
	// WebView2 can retain an older document-created script in a reused iframe.
	// The native bridge fetches the current generation before any renderer uses
	// atlas or wallpaper IDs. A failed handshake must remain visible.
	try { await window.matrixHostBootstrapReady; }
	catch (error) { showError(error); return; }
	const urlParams = new URLSearchParams(window.location.search);
	// Inside the visible error boundary: a configuration failure must explain itself.
	let config;
	try { config = makeConfig(Object.fromEntries(urlParams.entries())); }
	catch (error) { showError(error); return; }
	// Config check FIRST. && evaluates left to right, so asking supportsWebGPU() first
	// meant awaiting navigator.gpu.requestAdapter() on every launch even though the default
	// renderer is "regl", which initialises the D3D12 adapter enumeration and can spin up the
	// GPU process for nothing.
	const useWebGPU = wantsWebGPU(config) && (await supportsWebGPU());
	if (config.useHoloplay && config.renderer?.toLowerCase() === "webgpu") {
		console.info("Looking Glass uses the WebGL renderer for its multiview quilt output.");
	}
	// The same state a click on Plug me in leaves behind (below).
	if (consentGiven()) config.suppressWarnings = true;

	// suppressWarnings FIRST, same reason. isRunningSwiftShader() creates a second canvas
	// and a whole throwaway WebGL context just to read a renderer string, and never releases
	// it, so it burned one of the browser's live-context budget immediately before the real
	// context is created. Now skipped entirely when the answer cannot matter.
	const softwareRendering = !config.suppressWarnings && isRunningSwiftShader();
	// Nothing on a wallpaper, screensaver or preview host can press "Plug me in" (a wallpaper's
	// opt-in pointer forwarding reaches only the canvas): warn and start rather than leave a
	// static notice up forever.
	const nonInteractive = window.matrixHostBootstrap?.interactive === false;
	if (softwareRendering && nonInteractive) {
		console.warn("Hardware acceleration is disabled; rendering in software because this host takes no input.");
	}
	if (softwareRendering && !nonInteractive) {
		const notice = document.createElement("notice");
		notice.innerHTML = `<div class="notice">
		<p>Wake up, Neo... you've got hardware acceleration disabled.</p>
		<p>This project will still run, incredibly, but at a noticeably low framerate.</p>
		<button class="blue pill">Plug me in</button>
		<a class="red pill" target="_blank" href="https://www.google.com/search?q=chrome+enable+hardware+acceleration">Free me</a>
		`;
		canvas.style.display = "none";
		document.body.appendChild(notice);
		// once: a second click would start a second renderer on the same canvas. The canvas is
		// shown BEFORE loading because REGL sizes its backing store from clientWidth, which is
		// 0 while hidden, and nothing resizes it when it appears.
		document.querySelector(".blue.pill").addEventListener("click", async () => {
			try {
				config.suppressWarnings = true;
				recordConsent();
				urlParams.set("suppressWarnings", true);
				history.replaceState({}, "", "?" + urlParams.toString());
				notice.remove();
				canvas.style.display = "unset";
				await loadRenderer(canvas, config, useWebGPU);
			} catch (err) {
				showError(err);
			}
		}, { once: true });
	} else {
		try {
			await loadRenderer(canvas, config, useWebGPU);
		} catch (err) {
			showError(err);
		}
	}
};
