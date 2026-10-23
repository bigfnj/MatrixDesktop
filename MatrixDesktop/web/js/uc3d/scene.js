import uc3dDefaults from "./defaults.js";

// Independent recreation from the donor's documented behavior. Motion constants are
// intentionally explicit: they are not claimed to be recovered original constants.
export const clamp = (value, low, high) => Math.max(low, Math.min(high, value));
const smooth = (x) => { x = clamp(x, 0, 1); return x * x * (3 - 2 * x); };
const finite = (value, fallback) => Number.isFinite(Number(value)) ? Number(value) : fallback;
export const hash = (n) => { const x = Math.sin(n * 127.1 + 311.7) * 43758.5453123; return x - Math.floor(x); };
// Native donor updates use current += (target-current)*dt. The exact continuous
// solution below retains that one-second smoothing constant without frame-rate drift.
function smoothedTargets(low, high, maximumSeconds, salt) {
  let segment = 0, start = 0, value = 0, area = 0;
  const targetAt = (index) => low + Math.floor(hash(index * 3 + salt) * (high - low + 1));
  const durationAt = (index) => Math.max(1 / 60, Math.floor(hash(index * 3 + salt + 1) * maximumSeconds * 100) / 100);
  return (time) => {
    let duration = durationAt(segment), target = targetAt(segment);
    while (time >= start + duration) {
      const decay = Math.exp(-duration);
      area += target * duration + (value - target) * (1 - decay);
      value = target + (value - target) * decay;
      start += duration; segment++; duration = durationAt(segment); target = targetAt(segment);
    }
    const dt = Math.max(0, time - start), decay = Math.exp(-dt);
    return { value: target + (value - target) * decay, integral: area + target * dt + (value - target) * (1 - decay) };
  };
}
function automaticRotation() {
  let segment = 0, start = 0, angles = [0, 0, 0];
  const moving = (i) => 10 + Math.floor(hash(i * 7 + 20) * 120);
  const resting = (i) => Math.floor(hash(i * 7 + 21) * 120);
  const velocity = (i, axis) => (-20 + Math.floor(hash(i * 7 + 22 + axis) * 40)) * Math.PI / 180;
  return (time) => {
    while (time >= start + moving(segment) + resting(segment)) {
      angles = angles.map((angle, axis) => angle + moving(segment) * velocity(segment, axis));
      start += moving(segment) + resting(segment); segment++;
    }
    const dt = Math.min(Math.max(0, time - start), moving(segment));
    return angles.map((angle, axis) => (angle + dt * velocity(segment, axis)) % (Math.PI * 2));
  };
}
export const defaults = {
  shadedLines: true, sidewaysMovement: false, mouseRotation: false, codeRotation: true,
  forwardMovement: true, randomForwardMovement: false, showPictures: false,
  pictureDepth: 0, pictureMonochrome: true, pictureCaptions: true, pictureScale: 1,
  pictureDelay: 20, pictureDuration: 8, pictureSource: "all", showClock: false,
  clock12Hour: false, clockSize: 1, sound: "off", soundVolume: 0.25,
  ...uc3dDefaults,
};

export function createScene(options = {}, pictureCount = 0) {
  const config = { ...defaults, ...options };
  let elapsed = 0;
  let forwardPhase = 0;
  let lastInteraction = -Infinity;
  let mouseYaw = 0, mousePitch = 0, mouseRoll = 0;
  let mouseYawVelocity = 0, mousePitchVelocity = 0;
  let mouseStopped = false;
  let pictureTime = 0;
  let dead = false;
  let lastState = null;
  const randomTravel = smoothedTargets(-60, 59, 15, 52);
  const sideways = smoothedTargets(-1000, 999, 5, 94);
  const rotation = automaticRotation();
  const pictureDelay = Math.max(0, finite(config.pictureDelay, 20));
  const pictureDuration = Math.max(1, finite(config.pictureDuration, 8));
  const update = (seconds) => {
    if (dead) return null;
    const next = Math.max(elapsed, finite(seconds, elapsed));
    const delta = next - elapsed;
    // Only active manual time pauses the slideshow. Crossing the inactivity boundary
    // in a single update must give the remainder back to the automatic scheduler.
    const manualDelta = config.mouseRotation ? Math.max(0, Math.min(next, lastInteraction + 20) - Math.max(elapsed, lastInteraction)) : 0;
    if (!mouseStopped) { mouseYaw += mouseYawVelocity * manualDelta; mousePitch += mousePitchVelocity * manualDelta; }
    pictureTime += Math.max(0, delta - manualDelta);
    elapsed = next;
    const speed = finite(config.animationSpeed, 1);
    const motionTime = elapsed * Math.abs(speed);
    forwardPhase = !config.forwardMovement ? 0 : config.randomForwardMovement
      ? randomTravel(motionTime).integral * 0.1 / 50 * Math.sign(speed)
      : elapsed * speed * finite(config.forwardSpeed, 0.25) * 8 / 50;
    const idle = elapsed - lastInteraction;
    const manual = config.volumetric && config.mouseRotation ? 1 - smooth((idle - 20) / 1.2) : 0;
    const auto = config.volumetric && config.codeRotation ? 1 : 0;
    const autoAngles = rotation(motionTime);
    const autoYaw = autoAngles[0] * auto, autoPitch = autoAngles[1] * auto, autoRoll = autoAngles[2] * auto;
    // Recovered 30-second episode: wait5s, blend at.25/s, hold until30s,
    // then decay at.25/s. Shorter custom durations scale those intervals together.
    const fade = Math.min(4, pictureDuration * 4 / 30);
    const lead = pictureDuration * 5 / 30;
    const cycle = pictureDelay + pictureDuration + fade;
    const inCycle = pictureTime % cycle;
    const showing = config.showPictures && pictureCount > 0 && inCycle >= pictureDelay;
    const age = inCycle - pictureDelay;
    const pictureWeight = showing ? clamp((age - lead) / fade, 0, 1) * clamp((pictureDuration + fade - age) / fade, 0, 1) * (1 - manual) : 0;
    lastState = {
      elapsed, time: elapsed * speed, forwardPhase,
      yaw: autoYaw * (1 - manual) + mouseYaw * manual,
      pitch: autoPitch * (1 - manual) + mousePitch * manual,
      roll: autoRoll * (1 - manual) + mouseRoll * manual,
      sidewaysOffset: config.sidewaysMovement ? sideways(motionTime).value * 0.01 : 0,
      mouseActive: manual > 0, pictureWeight,
      pictureIndex: pictureCount ? Math.floor(pictureTime / cycle) % pictureCount : -1,
    };
    return lastState;
  };
  const beginManual = (seconds) => {
    if (seconds - lastInteraction >= 20 && lastState) {
      mouseYaw = lastState.yaw; mousePitch = lastState.pitch; mouseRoll = lastState.roll;
    }
    lastInteraction = seconds;
  };
  return {
    config, update,
    pointer(dx, dy, seconds = elapsed) {
      if (!config.mouseRotation || !config.volumetric || dead) return;
      beginManual(seconds);
      mouseStopped = false;
      mouseYawVelocity = clamp(finite(dx, 0) * 2, -90, 90) * Math.PI / 180;
      mousePitchVelocity = clamp(finite(dy, 0) * 2, -90, 90) * Math.PI / 180;
    },
    wheel(delta, seconds = elapsed) {
      if (!config.mouseRotation || !config.volumetric || dead) return;
      beginManual(seconds); mouseRoll += finite(delta, 0) * 0.001;
    },
    reset(seconds = elapsed) { mouseYaw = mousePitch = mouseRoll = mouseYawVelocity = mousePitchVelocity = 0; lastInteraction = seconds; mouseStopped = false; },
    stop(seconds = elapsed) { beginManual(seconds); mouseYawVelocity = mousePitchVelocity = 0; mouseStopped = true; },
    get stopped() { return mouseStopped; },
    dispose() { dead = true; },
  };
}

export function attachMouse(canvas, scene, now) {
  if (!scene.config.mouseRotation || !scene.config.volumetric) return () => {};
  let previous = null;
  const move = (event) => {
    if (previous) scene.pointer(event.clientX - previous[0], event.clientY - previous[1], now());
    previous = [event.clientX, event.clientY];
  };
  const leave = () => { previous = null; };
  const down = (event) => {
    if (event.button === 0) scene.reset(now());
    if (event.button === 2) { scene.stop(now()); event.preventDefault(); }
  };
  const wheel = (event) => { event.preventDefault(); scene.wheel(event.deltaY, now()); };
  const context = (event) => event.preventDefault();
  canvas.addEventListener("pointermove", move);
  canvas.addEventListener("pointerleave", leave);
  canvas.addEventListener("pointerdown", down);
  canvas.addEventListener("wheel", wheel, { passive: false });
  canvas.addEventListener("contextmenu", context);
  return () => {
    canvas.removeEventListener("pointermove", move); canvas.removeEventListener("pointerleave", leave);
    canvas.removeEventListener("pointerdown", down); canvas.removeEventListener("wheel", wheel);
    canvas.removeEventListener("contextmenu", context);
  };
}

export function createGrid(config, aspect, photo = null) {
  const dimension = Math.round(clamp(finite(config.numColumns, 80), 4, 256));
  const baseColumns = aspect >= 1 ? dimension : Math.max(4, Math.round(dimension * aspect));
  const rows = Math.max(40, aspect >= 1 ? Math.round(dimension / aspect) : dimension);
  const columns = Math.max(4, Math.round(baseColumns * (config.volumetric ? clamp(finite(config.density, 1), 0.1, 8) : 1)));
  const data = new Float32Array(columns * rows * 6 * 10);
  const corners = [[0, 0], [1, 0], [1, 1], [0, 0], [1, 1], [0, 1]];
  const scale = clamp(finite(config.pictureScale, 1), 0.1, 4) * 0.85;
  const photoAspect = photo ? photo.width / photo.height : 1;
  const fitWidth = Math.min(1, photoAspect / aspect) * scale;
  const fitHeight = Math.min(1, aspect / photoAspect) * scale;
  let offset = 0;
  for (let row = 0; row < rows; row++) for (let col = 0; col < columns; col++) {
    const seed = hash(col + 1);
    const u = ((col + 0.5) / columns - 0.5) / fitWidth + 0.5;
    const v = ((row + 0.5) / rows - 0.5) / fitHeight + 0.5;
    let rgba = [0, 0, 0, 0];
    if (photo && u >= 0 && u < 1 && v >= 0 && v < 1) {
      const index = (Math.floor(v * photo.height) * photo.width + Math.floor(u * photo.width)) * 4;
      rgba = Array.from(photo.data.subarray(index, index + 4), x => x / 255);
    }
    for (const corner of corners) {
      data.set([col, row, seed, 0.1 + Math.floor(hash(col + 400) * 90) / 100, ...corner, ...rgba], offset);
      offset += 10;
    }
  }
  return { data, columns, rows, baseColumns, count: columns * rows * 6 };
}

export function projection(aspect, volumetric, webgpu = false, isometric = false) {
  const m = new Float32Array(16);
  if (!volumetric) { m[0] = 1 / aspect; m[5] = 1; m[10] = webgpu ? 0 : -1; m[14] = webgpu ? 0.5 : 0; m[15] = 1; return m; }
  if (isometric) {
    m[0] = 1 / (aspect * 17.5); m[5] = 1 / 17.5; m[15] = 1;
    m[10] = (webgpu ? 1 : 2) / (0.1 - 100);
    m[14] = webgpu ? 0.1 / (0.1 - 100) : (100 + 0.1) / (0.1 - 100);
    return m;
  }
  const f = 1 / Math.tan(Math.PI / 6), near = 0.1, far = 100;
  m[0] = f / aspect; m[5] = f; m[11] = -1;
  m[10] = webgpu ? far / (near - far) : (far + near) / (near - far);
  m[14] = (webgpu ? far : 2 * far) * near / (near - far);
  return m;
}
