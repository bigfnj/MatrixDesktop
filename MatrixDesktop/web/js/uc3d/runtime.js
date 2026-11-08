import { createScene, attachMouse, createGrid, viewProjection, defaults } from "./scene.js";
import { createMedia, selectImages } from "./media.js";
import { createOverlays } from "./overlays.js";
import { createAudio } from "./audio.js";
import colorToRGB from "../colorToRGB.js";
import { createPlaybackClock, subscribeHostPlayback } from "../hostPlayback.js";
import { fitSurface } from "../zmatrix/surface.js";
import { showRenderFailure } from "../renderFailure.js";

export async function loadImage(url) {
  if (!url) return null;
  const image = new Image(); image.crossOrigin = "anonymous"; image.src = url;
  await image.decode(); return image;
}
export async function loadText(url) {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`UC3D asset failed (${response.status}): ${url}`);
  return response.text();
}
export function glyphSource(config) {
  const donorUrl = window.matrixHostBootstrap?.glyphUrl;
  return { url: config.donorGlyphs && donorUrl ? donorUrl : config.glyphMSDFURL ?? "assets/matrixcode_msdf.png", donor: Boolean(config.donorGlyphs && donorUrl) };
}
export function glyphConfig(config, source, image) {
  if (!source.donor) return config;
  const grid = [Math.max(1, Math.floor(image.naturalWidth / 32)), Math.max(1, Math.floor(image.naturalHeight / 32))];
  return { ...config, glyphSequenceLength: Math.min(79, grid[0] * grid[1]), glyphTextureGridSize: grid, bitmapGlyphs: true };
}

export async function createRuntime(canvas, options, backend) {
  const config = { ...defaults, ...options };
  const bootstrap = window.matrixHostBootstrap ?? {};
  const images = config.showPictures ? selectImages(bootstrap.images, config.pictureSource) : [];
  const warnings = Array.isArray(bootstrap.warnings) ? [...bootstrap.warnings] : [];
  if (config.showPictures && !images.length) warnings.push("No pictures are available for the selected source.");
  if (config.donorGlyphs && !config.bitmapGlyphs) warnings.push("Original glyphs are unavailable. Using the bundled font.");
  if (config.renderer?.toLowerCase() === "webgpu" && backend !== "webgpu") warnings.push("WebGPU is unavailable. Using WebGL.");
  const media = createMedia(images, { original: config.uc3dControls !== "legacy" });
  const scene = createScene(config, images.length);
  const overlays = createOverlays(config, warnings);
  const audio = createAudio(config, bootstrap);
  let currentTime = 0, currentIndex = -1, photo = null, grid = null, dirty = true, dead = false;
  let aspect = 1;
  const detachMouse = attachMouse(canvas, scene, () => currentTime);
  const normal = colorToRGB(config.codeColor ?? config.palette?.at(-1)?.color ?? { space: "rgb", values: [0.25, 1, 0.3] });
  const cursor = colorToRGB(config.cursorColor ?? { space: "rgb", values: [0.75, 1, 0.8] });
  const background = colorToRGB(config.backgroundColor ?? { space: "rgb", values: [0, 0, 0] });
  const diagnostics = { scene: "uc3d", backend, activeRenderer: backend, requestedRenderer: config.renderer ?? backend, ready: false, frames: 0, pictures: images.length,
    pictureStatus: images.length ? "loading" : config.showPictures ? "unavailable" : "none", glyphSource: config.bitmapGlyphs ? "donor" : "bundled", warnings, audio: audio.state, disposed: false, errors: [] };
  window.matrixRuntimeDiagnostics = diagnostics;
  window.__matrixRuntime = diagnostics;
  canvas.dataset.renderer = backend; canvas.dataset.scene = "uc3d";
  const requestPhoto = (index) => {
    currentIndex = index;
    if (index < 0) return;
    diagnostics.pictureStatus = "loading";
    media.load(index).then(value => {
      if (dead || currentIndex !== index) return;
      photo = value; dirty = true; diagnostics.pictureStatus = value ? "ready" : "failed";
      if (!value) overlays.addWarning("A picture could not be decoded. Other pictures will continue.");
      if (images.length > 1) media.load((index + 1) % images.length);
    });
  };
  requestPhoto(images.length ? 0 : -1);
  let desktop = null;
  diagnostics.inspect = () => dead ? { disposed: true } : scene.snapshot?.(8) ?? { controls: "legacy" };
  const dispose = () => {
    if (dead) return;
    dead = true; diagnostics.disposed = true; diagnostics.ready = false;
    window.removeEventListener("pagehide", dispose);
    detachMouse(); scene.dispose(); media.dispose(); overlays.dispose(); audio.dispose();
    grid = null; photo = null;
    if (desktop) desktop.src = "";
    desktop = null;
  };
  window.addEventListener("pagehide", dispose, { once: true });
  try { desktop = await loadImage(bootstrap.desktopUrl); }
  catch (error) { diagnostics.errors.push(`Desktop background unavailable: ${error.message}`); overlays.addWarning("The desktop background could not be loaded."); console.warn(diagnostics.errors.at(-1)); }
  if (dead) throw new Error("UC3D initialization was canceled by navigation");
  return {
    config, diagnostics, desktop, background,
    warn: message => overlays.addWarning(message),
    update(seconds, width, height) {
      if (dead) return null;
      currentTime = seconds;
      const nextAspect = Math.max(1, width) / Math.max(1, height);
      if (Math.abs(nextAspect - aspect) > 0.0001) { aspect = nextAspect; dirty = true; }
      scene.viewport?.(aspect);
      let state = scene.update(seconds);
      if (state.pictureIndex !== currentIndex) requestPhoto(state.pictureIndex);
      if (diagnostics.pictureStatus !== "ready") state.pictureWeight = 0;
      // A forming picture returns to a front-facing pose, as manual movement already
      // suppresses the picture. Keep the relief visible without shearing its silhouette.
      if (!scene.grid) {
        state.yaw *= 1 - state.pictureWeight; state.pitch *= 1 - state.pictureWeight; state.roll *= 1 - state.pictureWeight;
        // The scene seeds a mouse gesture from its own state, so the fixed isometric
        // view offset belongs to this derived view only, never to the scene's object.
        if (config.volumetric && config.isometric) state = { ...state, yaw: state.yaw + Math.PI / 4 * (1 - state.pictureWeight), pitch: state.pitch + Math.PI / 8 * (1 - state.pictureWeight) };
      }
      const changed = dirty || Boolean(scene.grid);
      if (changed) { grid = scene.grid ? scene.grid(aspect, photo) : createGrid(config, aspect, photo); dirty = false; }
      const uniforms = new Float32Array(56);
      // Days of active time do not fit a float32 uniform, so reduce exactly here. Legacy stream
      // speeds are multiples of 0.1: 500 s moves every head whole 50-unit wraps. The glyph term
      // needs only its cycle count modulo the sequence length, and travel only its fraction.
      const sequence = config.glyphSequenceLength ?? 57, cycle = Math.floor(state.time * (config.cycleSpeed ?? 0.03) * 30);
      uniforms.set(viewProjection(aspect, config, state, backend === "webgpu"), 0);
      uniforms.set([aspect, grid.rows, grid.columns, grid.baseColumns], 16);
      uniforms.set([state.time - 500 * Math.floor(state.time / 500), state.forwardPhase - Math.floor(state.forwardPhase), state.yaw, state.pitch], 20);
      uniforms.set([state.roll, state.sidewaysOffset, state.pictureWeight, config.volumetric ? 1 : 0], 24);
      uniforms.set([config.fallSpeed ?? 0.3, sequence > 0 ? cycle - sequence * Math.floor(cycle / sequence) : cycle, sequence, config.shadedLines ? 1 : 0], 28);
      uniforms.set([config.pictureDepth, config.pictureMonochrome ? 1 : 0, config.glyphIntensity ?? 1, config.cursorIntensity ?? 2], 32);
      uniforms.set([...(config.glyphTextureGridSize ?? [8, 8]), config.glyphFlip ? 1 : 0, (config.glyphRotation ?? 0) * Math.PI / 180], 36);
      uniforms.set([config.raindropLength ?? 0.75, config.glyphHeightToWidth ?? 1, config.glyphVerticalSpacing ?? 1, config.glyphEdgeCrop ?? 0], 40);
      uniforms.set([...normal, 0], 44); uniforms.set([...cursor, 0], 48);
      uniforms[52] = config.bitmapGlyphs ? 1 : 0;
      uniforms[53] = grid.nativeMode ? 1 : 0;
      uniforms[54] = grid.glyphHalfSize ?? 0;
      overlays.update(state, photo);
      diagnostics.pictureWeight = state.pictureWeight; diagnostics.mouseActive = state.mouseActive;
      diagnostics.controls = config.uc3dControls ?? "legacy";
      diagnostics.simulationTime = state.time; diagnostics.targetStreams = grid.columns;
      diagnostics.activeStreams = state.activeStreams ?? grid.columns;
      diagnostics.glyphInstances = grid.instanceCount ?? grid.count / 6;
      diagnostics.glyphHalfSize = grid.glyphHalfSize ?? 12 / grid.baseColumns;
      diagnostics.motion = { sideways: state.sidewaysOffset, forward: state.forwardPhase,
        yaw: state.yaw, pitch: state.pitch, roll: state.roll, eye: state.eye };
      return { uniforms, grid, changed, state };
    },
    dispose,
  };
}

// Both backends use one lifecycle and throttling implementation. No rendering callback
// can survive pagehide, explicit teardown, or an initialization/runtime failure.
// maximumDimension is the backend's texture/viewport limit for the canvas and render target.
export function startLoop(canvas, runtime, draw, cleanup, maximumDimension = Infinity, environment = {}) {
  const clock = environment.clock ?? createPlaybackClock();
  let frame = 0, dead = false, parked = false, start = null, previous = -Infinity, elapsed = 0, lastDraw = null, reduced = false;
  const requestedFps = Number(runtime.config.fps ?? 60);
  const fps = Number.isFinite(requestedFps) ? Math.max(0, Math.min(120, requestedFps)) : 60;
  const dispose = () => {
    if (dead) return;
    dead = true; cancelAnimationFrame(frame); window.removeEventListener("pagehide", dispose);
    unsubscribe(); clock.dispose(); runtime.dispose(); cleanup();
  };
  const tick = (now) => {
    if (dead) return;
    now = clock.seconds(now) * 1000;
    // A paused host keeps the last frame on screen: park without requesting another frame.
    if (clock.paused) { parked = true; return; }
    if (start === null) start = now;
    if (fps === 0 || now - previous >= 1000 / fps - 0.5 || runtime.config.once) {
      const ratio = window.devicePixelRatio || 1;
      // One aspect-preserving factor keeps a wide virtual screen inside the GPU limit.
      const { width, height, factor } = fitSurface(canvas.clientWidth * ratio * (runtime.config.resolution ?? 0.75),
        canvas.clientHeight * ratio * (runtime.config.resolution ?? 0.75), maximumDimension, Infinity);
      if (factor < 1 && !reduced) { reduced = true; runtime.warn?.("Render resolution was reduced to fit the GPU texture limit."); }
      if (runtime.config.uc3dControls === "original") {
        // Do not simulate an entire sleep/lock interval on the first resumed frame.
        // Dropped time is intentional; active simulation resumes without catch-up debt.
        if (lastDraw !== null) elapsed += Math.min(1, Math.max(0, (now-lastDraw)/1000));
        else if (runtime.config.once || fps === 0) elapsed = 10;
      } else elapsed = (now - start) / 1000;
      lastDraw = now;
      // A draw that returns false presented nothing: a lost context waits for its event.
      // Only a presented frame counts, as in ZMatrix; a throwing draw never reaches the count.
      try {
        runtime.diagnostics.ready = draw(runtime.update(elapsed, width, height), width, height) !== false;
        if (runtime.diagnostics.ready) runtime.diagnostics.frames++;
      }
      catch (error) { runtime.diagnostics.errors.push(error.message); console.error("UC3D render failed", error); showRenderFailure("UC3D", error); dispose(); return; }
      previous = now;
      // Keep the single frame and overlays visible. Resources are released on pagehide
      // or explicit teardown, just as for the animated path, without scheduling a loop.
      if (runtime.config.once || fps === 0) return;
    }
    frame = requestAnimationFrame(tick);
  };
  // The host's resume re-arms a parked loop once. The listener also runs on subscribing.
  const unsubscribe = (environment.subscribe ?? subscribeHostPlayback)(paused => {
    if (!paused && parked && !dead) { parked = false; frame = requestAnimationFrame(tick); }
  });
  window.addEventListener("pagehide", dispose, { once: true });
  runtime.diagnostics.dispose = dispose;
  frame = requestAnimationFrame(tick);
  return dispose;
}
