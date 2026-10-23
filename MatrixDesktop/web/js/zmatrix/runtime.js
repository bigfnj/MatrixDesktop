import { normalizeZMatrixSettings } from "./settings.js";
import { createScene } from "./scene.js";
import { createSurface, fitSurface, MAX_SURFACE_PIXELS } from "./surface.js";
import { loadFonts, decodeImage } from "./fonts.js";
import { createPlaybackClock } from "../hostPlayback.js";

// Every asynchronous generation has one owner. Obsolete loads must dispose
// their result even when a decoder does not implement AbortSignal itself.
export function createResourceSlot() {
  let generation = 0, controller = null, current = null, dead = false;
  return {
    async replace(factory, install) {
      if (dead) return false;
      const token = ++generation; controller?.abort(); controller = new AbortController();
      const signal = controller.signal;
      let value;
      try { value = await factory(signal); }
      catch (error) { if (dead || token !== generation || signal.aborted) return false; throw error; }
      if (dead || token !== generation || signal.aborted) { value.dispose(); return false; }
      const previous = current;
      current = null;
      try { install(value); current = value; }
      catch (error) { value.dispose(); throw error; }
      finally { previous?.dispose(); }
      return true;
    },
    dispose() { if (dead) return; dead = true; generation++; controller?.abort(); controller = null; current?.dispose(); current = null; },
  };
}
async function loadResources(config, bootstrap, signal) {
  const fonts = await loadFonts(config, bootstrap, signal);
  let wallpaper = null;
  const warnings = [...(Array.isArray(bootstrap.warnings) ? bootstrap.warnings.map(String) : []),
    ...fonts.normal.warnings, ...fonts.special.warnings];
  try {
    if (config.zmBackgroundMode === "bitmap") {
      try {
        if (!bootstrap.wallpaper?.url) throw new Error("No native wallpaper capture was supplied");
        wallpaper = await decodeImage(bootstrap.wallpaper.url, signal);
        if (wallpaper.width * wallpaper.height > MAX_SURFACE_PIXELS || wallpaper.width > 16384 || wallpaper.height > 16384)
          throw new Error("Wallpaper capture exceeds its decoded pixel budget");
        if (Array.isArray(bootstrap.wallpaper.warnings)) warnings.push(...bootstrap.wallpaper.warnings.map(String));
      } catch (error) {
        wallpaper?.close(); wallpaper = null;
        if (signal.aborted) throw error;
        warnings.push(`Desktop wallpaper unavailable; using the configured background color. ${error.message}`);
      }
    }
    if (signal.aborted) throw new DOMException("ZMatrix resource load was canceled", "AbortError");
    return { fonts, wallpaper, warnings, generation: bootstrap.wallpaper?.generation ?? bootstrap.zmatrixFonts?.generation ?? 0,
      dispose() { fonts.normal.dispose(); fonts.special.dispose(); wallpaper?.close(); wallpaper = null; } };
  } catch (error) { fonts.normal.dispose(); fonts.special.dispose(); wallpaper?.close(); throw error; }
}
export async function createRuntime(canvas, options, backend, maximumDimension = 8192) {
  const warnings = [], config = { ...options, ...normalizeZMatrixSettings(options, warnings) };
  let bootstrap = window.matrixHostBootstrap ?? {}, resources = null, surface = null, imageData = null, upload = null, uploadContext = null, pendingRefresh = null;
  let dead = false, lastTime = null, cssWidth = 0, cssHeight = 0, backgroundDirty = true, lastRatio = 0;
  const slot = createResourceSlot();
  const scene = createScene(config, { seed: bootstrap.zmatrixSeed });
  const diagnostics = { scene: "zmatrix", backend, activeRenderer: backend, requestedRenderer: config.renderer ?? backend,
    ready: false, frames: 0, simulationTicks: 0, warnings: [], errors: [], disposed: false, nativeFonts: {}, resourceGeneration: 0 };
  window.matrixRuntimeDiagnostics = diagnostics; window.__matrixRuntime = diagnostics;
  canvas.dataset.scene = "zmatrix"; canvas.dataset.renderer = backend;
  const warningBox = document.createElement("div"); warningBox.className = "zmatrix-warnings";
  Object.assign(warningBox.style, { position: "fixed", left: "12px", bottom: "12px", maxWidth: "min(720px,90vw)", color: "#fff",
    background: "rgba(0,0,0,.85)", padding: "8px", font: "12px sans-serif", zIndex: "10", pointerEvents: "none", display: "none" });
  document.body.appendChild(warningBox);
  const warn = message => {
    message = String(message);
    if (diagnostics.warnings.includes(message)) return;
    if (diagnostics.warnings.length < 32) diagnostics.warnings.push(message);
    console.warn(`ZMatrix: ${message}`);
    if (!config.suppressWarnings && !dead) { warningBox.textContent = diagnostics.warnings.join("\n"); warningBox.style.display = "block"; }
  };
  warnings.forEach(warn);
  const refresh = next => pendingRefresh = slot.replace(signal => loadResources(config, next, signal), value => {
    resources = value; backgroundDirty = true; lastRatio = 0;
    diagnostics.resourceGeneration = value.generation;
    diagnostics.nativeFonts = Object.fromEntries(Object.entries(value.fonts).map(([key, font]) => [key,
      { source: font.source, resolvedFace: font.resolvedFace, warnings: [...font.warnings], metrics: { ...font.metrics }, atlasDpi: font.atlasDpi }]));
    value.warnings.forEach(warn);
  });
  const resourceEvent = event => {
    if (dead || !event.detail || typeof event.detail !== "object") return;
    bootstrap = event.detail;
    refresh(bootstrap).catch(error => { if (!dead) {
      if (diagnostics.errors.length < 32) diagnostics.errors.push(error.message);
      warn(`Resource refresh failed: ${error.message}`);
    } });
  };
  const resize = () => {
    const nextWidth = Math.max(1, canvas.clientWidth || 1), nextHeight = Math.max(1, canvas.clientHeight || 1);
    const rawResolution = Number(config.resolution ?? .75);
    const ratio = (window.devicePixelRatio || 1) * (Number.isFinite(rawResolution) ? Math.max(.05, Math.min(2, rawResolution)) : .75);
    const fit = fitSurface(nextWidth * ratio, nextHeight * ratio, maximumDimension);
    if (!surface || surface.width !== fit.width || surface.height !== fit.height || cssWidth !== nextWidth || cssHeight !== nextHeight || lastRatio !== ratio) {
      surface?.dispose(); cssWidth = nextWidth; cssHeight = nextHeight; lastRatio = ratio;
      surface = createSurface(fit.width, fit.height, config, fit.width / nextWidth);
      upload ??= document.createElement("canvas"); upload.width = fit.width; upload.height = fit.height;
      uploadContext = upload.getContext("2d");
      if (!uploadContext) throw new Error("ZMatrix requires a Canvas2D upload surface");
      imageData = new ImageData(surface.pixels, fit.width, fit.height); backgroundDirty = true;
      if (fit.factor < .999) warn("Render resolution was reduced to fit the ZMatrix pixel/texture budget.");
    }
    const normal = resources.fonts.normal, special = resources.fonts.special;
    scene.viewport(cssWidth, cssHeight, { normalHeight: normal.metrics.height, specialHeight: special.metrics.height,
      columnWidth: Math.max(normal.metrics.maxCharWidth, special.metrics.maxCharWidth), nativeScale: normal.atlasDpi / 96 });
    if (backgroundDirty) {
      if (resources.wallpaper) {
        uploadContext.clearRect(0, 0, surface.width, surface.height);
        uploadContext.drawImage(resources.wallpaper, 0, 0, surface.width, surface.height);
        surface.setBackground(uploadContext.getImageData(0, 0, surface.width, surface.height).data);
      } else surface.reset();
      backgroundDirty = false;
    }
  };
  const frame = () => {
    const dirty = surface.consumeDirty();
    if (dirty) uploadContext.putImageData(imageData, 0, 0, dirty.left, dirty.top, dirty.right - dirty.left, dirty.bottom - dirty.top);
    const state = scene.snapshot(0);
    Object.assign(diagnostics, { simulationTicks: state.ticks, activeStreams: state.activeStreams, targetStreams: state.targetStreams,
      surfaceRevision: surface.revision, surfaceWidth: surface.width, surfaceHeight: surface.height, surfaceBytes: surface.byteLength,
      droppedMs: state.droppedMs });
    return { source: upload, width: surface.width, height: surface.height, revision: surface.revision, changed: Boolean(dirty) };
  };
  const dispose = () => {
    if (dead) return; dead = true; diagnostics.disposed = true; diagnostics.ready = false;
    window.removeEventListener("pagehide", dispose);
    window.removeEventListener("matrix-native-resources-updated", resourceEvent);
    slot.dispose(); resources = null; pendingRefresh = null; scene.dispose(); surface?.dispose(); surface = null; imageData = null;
    if (upload) { upload.width = 0; upload.height = 0; } upload = null; uploadContext = null;
    warningBox.remove(); diagnostics.step = null;
  };
  diagnostics.inspect = () => dead ? { disposed: true } : { ...scene.snapshot(8), resourceGeneration: diagnostics.resourceGeneration,
    nativeFonts: diagnostics.nativeFonts, surfaceWidth: surface?.width, surfaceHeight: surface?.height, surfaceBytes: surface?.byteLength };
  diagnostics.samplePixels = points => dead ? [] : surface?.snapshot(Array.isArray(points) ? points : []) ?? [];
  window.addEventListener("pagehide", dispose, { once: true });
  window.addEventListener("matrix-native-resources-updated", resourceEvent);
  try {
    let pending = refresh(bootstrap);
    for (;;) {
      await pending;
      if (dead) throw new Error("ZMatrix initialization was canceled");
      if (pending === pendingRefresh) break;
      pending = pendingRefresh;
    }
    return {
      config, diagnostics,
      update(seconds) {
        if (dead) return null;
        resize();
        const delta = lastTime === null ? 0 : Math.max(0, (seconds - lastTime) * 1000); lastTime = seconds;
        scene.advance(delta, op => surface.apply(op, resources.fonts)); return frame();
      },
      advanceTicks(count) {
        if (dead) return null;
        resize(); count = Number.isFinite(count) ? Math.max(0, Math.min(100, Math.trunc(count))) : 0;
        for (let i = 0; i < count; i++) scene.tick(op => surface.apply(op, resources.fonts)); return frame();
      },
      warn, dispose,
    };
  } catch (error) { diagnostics.errors.push(error.message); dispose(); throw error; }
}
export function startLoop(canvas, runtime, draw, cleanup, environment = {}) {
  const clock = environment.clock ?? createPlaybackClock();
  const requested = Number(runtime.config.fps ?? 60), fps = Number.isFinite(requested) ? Math.max(0, Math.min(120, requested)) : 60;
  let frame = 0, dead = false, previous = -Infinity, warmed = false;
  const dispose = () => {
    if (dead) return; dead = true; cancelAnimationFrame(frame); window.removeEventListener("pagehide", dispose);
    clock.dispose(); runtime.dispose(); cleanup();
  };
  const present = state => {
    if (!state || dead) return;
    draw(state); runtime.diagnostics.frames++; runtime.diagnostics.lastPresentedRevision = state.revision; runtime.diagnostics.ready = true;
  };
  const fail = error => {
    if (runtime.diagnostics.errors.length < 32) runtime.diagnostics.errors.push(error.message);
    runtime.warn(`Rendering failed: ${error.message}`); console.error("ZMatrix render failed", error); dispose();
  };
  runtime.diagnostics.dispose = dispose;
  runtime.diagnostics.step = count => {
    if (dead) return { disposed: true };
    try { present(runtime.advanceTicks(Number(count ?? 1))); return runtime.diagnostics.inspect(); }
    catch (error) { fail(error); return { error: error.message }; }
  };
  const tick = now => {
    if (dead) return;
    const seconds = clock.seconds(now);
    if (!clock.paused && (fps === 0 || runtime.config.once || now - previous >= 1000 / fps - .5)) {
      try {
        let state = runtime.update(seconds);
        if ((runtime.config.once || fps === 0) && !warmed) {
          const ticks = Math.min(200, Math.ceil(10000 / runtime.config.zmRefreshTime));
          for (let i = 0; i < ticks; i += 100) state = runtime.advanceTicks(Math.min(100, ticks - i));
          runtime.diagnostics.warmupTicks = ticks; warmed = true;
        }
        present(state); previous = now;
      } catch (error) { fail(error); return; }
      if (runtime.config.once || fps === 0) return;
    }
    frame = requestAnimationFrame(tick);
  };
  window.addEventListener("pagehide", dispose, { once: true }); frame = requestAnimationFrame(tick);
  return dispose;
}
