// Original-control simulation, independently written from measured native behavior.
// A fixed step keeps movement and population independent of the display refresh rate.
const STEP = 1 / 120;
const DEG = Math.PI / 180;
const clamp = (n, low, high) => Math.max(low, Math.min(high, n));
const finite = (n, fallback) => Number.isFinite(Number(n)) ? Number(n) : fallback;
const identity = () => [0, 0, 0, 1];
const normalize = (v) => { const length = Math.hypot(...v) || 1; return v.map(n => n / length); };
const cross = (a, b) => [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]];
const dot = (a, b) => a.reduce((sum, n, i) => sum + n*b[i], 0);

export function createNativeRandom(seed = Date.now()) {
  let state = Number(seed) >>> 0;
  return (range = 32768) => { state = (Math.imul(state, 214013) + 2531011) >>> 0; return ((state >>> 16) & 32767) % range; };
}
export const wrapNative = (n) => n > 25 || n < -25 ? ((n + 25) % 50 + 50) % 50 - 25 : n;
export function nativeSettings(config = {}) {
  const glyphSize = Math.round(clamp(finite(config.uc3dGlyphSize, 12), 10, 40));
  return {
    amount: Math.round(clamp(finite(config.uc3dAmount, 500), 100, 1500)), glyphSize,
    halfSize: glyphSize * .01, screenColumns: Math.trunc(1400 / glyphSize),
    fall: Math.round(clamp(finite(config.uc3dFallSpeed, 6), 0, 150)),
    forward: Math.round(clamp(finite(config.uc3dForwardSpeed, 6), 0, 120)) - 60,
  };
}
export function nativeFallSpeed(random, fall) { return 2 + random(fall + 30) * .1; }
export function nativePictureGrid(photo, settings, scale, seconds, periodX = 8, periodY = 10) {
  const width = Math.max(1, Math.trunc(photo.width/scale)), height = Math.max(1, Math.trunc(photo.height/scale));
  const screenWidth = settings.screenColumns, screenHeight = Math.trunc(screenWidth*.75);
  return {
    width, height,
    x: width*.5+(width*.5-screenWidth*.5)*Math.sin(seconds*Math.PI/periodX),
    y: -height*.5+(height*.5-screenHeight*.5)*Math.cos(seconds*Math.PI/periodY),
  };
}
export function multiplyMatrices(a, b) {
  const result = new Float32Array(16);
  for (let col = 0; col < 4; col++) for (let row = 0; row < 4; row++) {
    for (let k = 0; k < 4; k++) result[col*4+row] += a[k*4+row]*b[col*4+k];
  }
  return result;
}
export function nativeLookAt(eye, up, target = [0, 0, -1]) {
  const z = normalize(eye.map((n, i) => n-target[i]));
  const x = normalize(cross(up, z)), y = cross(z, x);
  return new Float32Array([x[0], y[0], z[0], 0, x[1], y[1], z[1], 0, x[2], y[2], z[2], 0, -dot(x, eye), -dot(y, eye), -dot(z, eye), 1]);
}
export function nativeProjection(aspect, config = {}, webgpu = false) {
  aspect = Math.max(.001, finite(aspect, 1));
  const m = new Float32Array(16);
  if (!config.volumetric) {
    const { halfSize, screenColumns } = nativeSettings(config);
    const width = screenColumns * halfSize * 2;
    m[0] = 2 / width; m[5] = 2 * aspect / width; m[10] = webgpu ? 0 : -1;
    m[14] = webgpu ? .5 : 0; m[15] = 1;
    return m;
  }
  const f = 1 / Math.tan(Math.PI / 8), near = 1, far = 100;
  m[0] = f / aspect; m[5] = f; m[11] = -1;
  m[10] = webgpu ? far / (near-far) : (far+near) / (near-far);
  m[14] = (webgpu ? far : 2*far) * near / (near-far);
  return m;
}
export function nativeViewProjection(aspect, config, state, webgpu = false) {
  const p = nativeProjection(aspect, config, webgpu);
  return config.volumetric ? multiplyMatrices(p, state.viewMatrix ?? nativeLookAt([0, 0, 25], [0, 1, 0])) : p;
}
function eulerRotate(point, angles) {
  let [x, y, z] = point;
  for (let axis = 0; axis < 3; axis++) {
    const c = Math.cos(angles[axis]), s = Math.sin(angles[axis]);
    if (axis === 0) [y, z] = [y*c-z*s, y*s+z*c];
    if (axis === 1) [x, z] = [x*c+z*s, z*c-x*s];
    if (axis === 2) [x, y] = [x*c-y*s, x*s+y*c];
  }
  return [x, y, z];
}
function multiplyQuaternion(a, b) {
  return normalize([a[3]*b[0]+a[0]*b[3]+a[1]*b[2]-a[2]*b[1], a[3]*b[1]-a[0]*b[2]+a[1]*b[3]+a[2]*b[0], a[3]*b[2]+a[0]*b[1]-a[1]*b[0]+a[2]*b[3], a[3]*b[3]-a[0]*b[0]-a[1]*b[1]-a[2]*b[2]]);
}
function quaternionEuler(angles) {
  return angles.reduce((q, n, axis) => {
    const v = [0, 0, 0, Math.cos(n/2)]; v[axis] = Math.sin(n/2); return multiplyQuaternion(q, v);
  }, identity());
}
function interpolateQuaternion(a, b, weight) {
  let cosine = dot(a, b);
  if (cosine < 0) { b = b.map(n => -n); cosine = -cosine; }
  if (cosine > .9995) return normalize(a.map((n, i) => n+(b[i]-n)*weight));
  const angle = Math.acos(clamp(cosine, -1, 1)), sin = Math.sin(angle);
  const first = Math.sin((1-weight)*angle)/sin, second = Math.sin(weight*angle)/sin;
  return a.map((n, i) => n*first+b[i]*second);
}
function quaternionRotate(v, q) {
  const t = cross(q, v).map(n => n*2), u = cross(q, t);
  return v.map((n, i) => n + q[3]*t[i] + u[i]);
}
function plane(m, top) {
  const sign = top ? -1 : 1;
  const result = [m[3]+sign*m[1], m[7]+sign*m[5], m[11]+sign*m[9], m[15]+sign*m[13]];
  const length = Math.hypot(...result.slice(0, 3)) || 1;
  return result.map(n => n/length);
}

export function createNativeScene(config, pictureCount = 0) {
  const settings = nativeSettings(config), random = createNativeRandom(config.uc3dSeed ?? (Date.now() ^ Math.floor(Math.random()*0xffffffff)));
  const h = settings.halfSize, cellHeight = h*2, streams = [];
  let geometry = new Float32Array(settings.amount * 39 * 16);
  const ordered = [];
  let elapsed = 0, simulated = 0, ticks = 0, active = 0, aspect = 1, dead = false, initialized = false;
  let forwardVelocity = 0, forwardTarget = settings.forward, forwardTimer = 0, forwardDistance = 0;
  let sidewaysVelocity = 0, sidewaysTarget = 0, sidewaysTimer = 0, sidewaysDistance = 0;
  let phaseMoving = false, phaseTimer = 10, autoGain = 0, previousPicture = false;
  let targetAngles = [0, 0, 0], angles = [0, 0, 0], angularVelocity = [0, 0, 0];
  let manualUntil = -Infinity, pending = [0, 0, 0], manualVelocity = [0, 0, 0], manualTarget = identity(), manualCurrent = identity(), manualGain = 0, stopped = false;
  let pictureTimer = 20, pictureIndex = pictureCount ? 0 : -1, pictureWeight = 0, pictureMode = false;
  let picturePeriodX = 8, picturePeriodY = 10, pictureGrid = null;
  let eye = [0, 0, 25], up = [0, 1, 0], viewMatrix = nativeLookAt(eye, up), state;
  const activeCount = () => Math.max(0, Math.ceil(active-1e-9));
  const delay = Math.max(0, finite(config.pictureDelay, 60)), duration = Math.max(1, finite(config.pictureDuration, 30));
  const speed = clamp(finite(config.animationSpeed, 1), 0, 4);
  const updatePicture = (dt) => {
    const manual = config.volumetric && config.mouseRotation && simulated < manualUntil;
    if (!config.showPictures || !pictureCount) { pictureTimer = 20; pictureWeight = 0; pictureMode = false; return; }
    const lead = duration*5/30, rate = .25*30/duration;
    // The donor always waits twenty seconds for its first image. PicDelay is the
    // gap after every tagged stream has cleared, rather than the total period.
    pictureTimer -= dt;
    let tagged = false;
    for (let i = 0; i < activeCount() && !tagged; i++) tagged = streams[i].picture;
    if (pictureMode) {
      if (pictureTimer < -lead) pictureWeight += (1-pictureWeight)*dt*rate;
      if (pictureTimer < -duration) pictureMode = false;
    }
    if (!pictureMode) {
      pictureWeight = Math.max(0, pictureWeight-dt*rate);
      if (!tagged && pictureTimer < -duration) {
        pictureIndex = (pictureIndex+1)%pictureCount; pictureTimer = delay;
      }
      if (!tagged && pictureTimer < -1e-9 && pictureTimer >= -duration) {
        if (manual) pictureTimer = 0;
        else {
          pictureMode = true;
          picturePeriodX = (random(100)-50)*.1+8;
          picturePeriodY = (random(100)-50)*.1+10;
        }
      }
    }
  };
  const updateCamera = (dt) => {
    const manual = config.volumetric && config.mouseRotation && simulated < manualUntil;
    if (!manual && manualUntil !== -Infinity) {
      manualTarget = identity(); manualVelocity = [0, 0, 0]; manualGain = 0; manualUntil = -Infinity;
    }
    if (manual && !stopped) {
      manualVelocity = manualVelocity.map((n, i) => clamp(n-pending[i]*STEP, -90, 90));
      manualTarget = multiplyQuaternion(manualTarget, quaternionEuler(manualVelocity.map(n => n*DEG*dt)));
    }
    pending = [0, 0, 0]; manualGain = Math.min(1, manualGain+.5*dt);
    manualCurrent = interpolateQuaternion(manualCurrent, manualTarget, manualGain);
    if (config.volumetric && config.codeRotation) {
      phaseTimer -= dt;
      if (phaseTimer < -1e-9) {
        phaseMoving = !phaseMoving; phaseTimer += random(120)+(phaseMoving ? 10 : 0);
        if (!phaseMoving) autoGain = 0;
      }
      if (!phaseMoving || pictureMode || manual) {
        targetAngles = [0, 0, 0];
        angles = angles.map(n => ((n+Math.PI)%(Math.PI*2)+Math.PI*2)%(Math.PI*2)-Math.PI);
        if (!phaseMoving) angularVelocity = angularVelocity.map(() => (random(40)-20)*DEG);
      } else {
        targetAngles = targetAngles.map((n, i) => n+angularVelocity[i]*dt);
        for (let i = 0; i < 3; i++) if (Math.abs(targetAngles[i]) > Math.PI*2) {
          const turns = Math.trunc(targetAngles[i]/(Math.PI*2))*Math.PI*2; targetAngles[i] -= turns; angles[i] -= turns;
        }
      }
      if (pictureMode && !previousPicture) autoGain = 0;
      autoGain = Math.min(1, autoGain+.5*dt);
      angles = angles.map((n, i) => n+(targetAngles[i]-n)*autoGain*dt);
    }
    previousPicture = pictureMode;
    const manualEye = quaternionRotate([0, 0, 25], manualCurrent), manualUp = quaternionRotate([0, 1, 0], manualCurrent);
    eye = eulerRotate(manualEye, angles); up = eulerRotate(manualUp, angles); viewMatrix = nativeLookAt(eye, up);
  };
  const spawn = (stream) => {
    stream.length = random(30)+10; stream.visible = 1; stream.remainder = 0;
    stream.x = (random(5000)-2500)*.01; stream.y = 25; stream.z = (random(5000)-2500)*.01;
    stream.picture = pictureMode;
    if (!config.volumetric || stream.picture) {
      stream.x = (random(settings.screenColumns)-Math.trunc(700/settings.glyphSize))*cellHeight; stream.z = 0;
    }
    if (!config.volumetric || stream.picture || !config.codeRotation) {
      const top = plane(nativeViewProjection(aspect, config, { viewMatrix }), true);
      if (Math.abs(top[1]) > 1e-6) stream.y = Math.min(25, -(top[0]*stream.x+top[2]*stream.z+top[3])/top[1]);
    }
    stream.speed = nativeFallSpeed(random, settings.fall); stream.shade = (random(90)+10)*.01; stream.mutationTimer = 0;
    for (let i = 0; i < stream.length; i++) { stream.symbols[i] = random(79); stream.mutations[i] = random(4) === 0 ? 1 : 0; stream.mirrors[i] = random(2); }
  };
  const initialize = () => {
    if (initialized) return; initialized = true;
    for (let i = 0; i < settings.amount; i++) {
      const stream = { id: i, symbols: new Uint8Array(39), mutations: new Uint8Array(39), mirrors: new Uint8Array(39) };
      spawn(stream); stream.y = 25; streams.push(stream);
    }
  };
  const step = () => {
    simulated = ++ticks*STEP; const dt = STEP*speed;
    updatePicture(STEP); updateCamera(dt);
    active = Math.min(settings.amount, active+settings.amount*.1*dt);
    if (config.forwardMovement && config.volumetric) {
      if (config.randomForwardMovement && (forwardTimer -= dt) < 0) { forwardTarget = random(120)-60; forwardTimer = random(1500)*.01; }
      forwardVelocity += (forwardTarget-forwardVelocity)*dt; forwardDistance += forwardVelocity*.1*dt;
    }
    if (config.sidewaysMovement && config.volumetric) {
      if (pictureMode) sidewaysTarget = 0;
      else if ((sidewaysTimer -= dt) < 0) { sidewaysTarget = (random(2000)-1000)*.01; sidewaysTimer = random(500)*.01; }
      sidewaysVelocity += (sidewaysTarget-sidewaysVelocity)*dt; sidewaysDistance += sidewaysVelocity*dt;
    }
    const bottom = plane(nativeViewProjection(aspect, config, { viewMatrix }), false);
    for (let index = 0; index < activeCount(); index++) {
      const stream = streams[index];
      stream.y -= stream.speed*dt;
      if (config.forwardMovement && config.volumetric && !stream.picture) stream.z = wrapNative(stream.z+forwardVelocity*.1*dt);
      if (config.sidewaysMovement && config.volumetric) stream.x = wrapNative(stream.x+sidewaysVelocity*dt);
      stream.remainder += stream.speed*dt;
      const cells = Math.floor((stream.remainder+1e-12)/cellHeight);
      if (cells > 0) {
        stream.remainder -= cells*cellHeight; stream.visible = Math.min(stream.length, stream.visible+cells);
        for (let cell = 0; cell < cells; cell++) {
          stream.symbols.copyWithin(1, 0, stream.length-1); stream.mutations.copyWithin(1, 0, stream.length-1); stream.mirrors.copyWithin(1, 0, stream.length-1);
          stream.symbols[0] = random(79); stream.mutations[0] = random(5) === 2 ? 1 : 0; stream.mirrors[0] = random(2);
        }
      }
      stream.mutationTimer -= dt;
      if (stream.mutationTimer < 0) {
        for (let i = 0; i < stream.visible; i++) if (stream.mutations[i]) stream.symbols[i] = random(79);
        stream.mutationTimer = random(40)*.01;
      }
      const topY = stream.y+stream.visible*cellHeight;
      const worldFloor = config.volumetric && config.codeRotation && (!stream.picture || !pictureMode || config.pictureDepth > 0);
      const gone = worldFloor ? topY < -25 : bottom[0]*stream.x+bottom[1]*topY+bottom[2]*stream.z+bottom[3] <= -h;
      if (gone) spawn(stream);
    }
  };
  const snapshot = () => state = {
    elapsed, time: elapsed*finite(config.animationSpeed, 1), forwardPhase: forwardDistance/50, sidewaysOffset: sidewaysDistance,
    yaw: angles[1], pitch: angles[0], roll: angles[2], eye: [...eye], up: [...up], viewMatrix,
    mouseActive: Boolean(config.volumetric && config.mouseRotation && simulated < manualUntil), pictureWeight, pictureIndex,
    activeStreams: activeCount(), forwardVelocity: forwardVelocity*.1, sidewaysVelocity, phaseMoving, phaseTimer,
    nativeMode: true,
  };
  const interact = (seconds) => {
    api.update(seconds); manualUntil = simulated+20;
  };
  const api = {
    config, settings,
    viewport(nextAspect) { aspect = Math.max(.001, finite(nextAspect, 1)); },
    update(seconds) {
      if (dead) return null;
      initialize(); elapsed = Math.max(elapsed, finite(seconds, elapsed));
      const target = Math.floor(elapsed/STEP+1e-8);
      while (ticks < target) step();
      return snapshot();
    },
    pointer(dx, dy, seconds = elapsed) {
      if (dead || !config.mouseRotation || !config.volumetric) return;
      interact(seconds); stopped = false; pending[0] += finite(dy, 0); pending[1] += finite(dx, 0);
    },
    wheel(delta, seconds = elapsed) {
      if (dead || !config.mouseRotation || !config.volumetric) return;
      interact(seconds); stopped = false; pending[2] += finite(delta, 0);
    },
    reset(seconds = elapsed) {
      if (dead) return; interact(seconds); manualVelocity = [0, 0, 0]; pending = [0, 0, 0]; manualTarget = identity(); manualGain = 0; stopped = false;
    },
    stop(seconds = elapsed) {
      if (dead) return; interact(seconds); manualVelocity = [0, 0, 0]; pending = [0, 0, 0]; stopped = true;
    },
    get stopped() { return stopped; },
    grid(nextAspect, photo = null) {
      if (dead) return null; api.viewport(nextAspect); initialize();
      ordered.length = 0;
      for (let i = 0; i < activeCount(); i++) {
        const stream = streams[i]; stream.distance = (stream.x-eye[0])**2+(stream.y-eye[1])**2+(stream.z-eye[2])**2; ordered.push(stream);
      }
      if (config.volumetric) ordered.sort((a, b) => b.distance-a.distance);
      const scale = clamp(finite(config.pictureScale, 1), 1, 3);
      pictureGrid = photo ? nativePictureGrid(photo, settings, scale, simulated*speed, picturePeriodX, picturePeriodY) : null;
      let offset = 0;
      for (const stream of ordered) for (let row = 0; row < stream.visible; row++) {
        const renderY = stream.y+stream.remainder;
        const imageX = pictureGrid ? pictureGrid.x+Math.trunc(stream.x/cellHeight) : -1;
        const imageY = pictureGrid ? -(row+pictureGrid.y+Math.trunc(renderY/cellHeight)) : -1;
        let r = 0, g = 0, b = 0, alpha = 0;
        if (stream.picture && photo && imageX > 0 && imageX < pictureGrid.width && imageY > 0 && imageY < pictureGrid.height) {
          const sourceX = Math.min(photo.width-1, Math.trunc(Math.trunc(imageX)*scale));
          const sourceY = Math.min(photo.height-1, Math.trunc(Math.trunc(imageY)*scale));
          const index = (sourceY*photo.width+sourceX)*4;
          r = photo.data[index]/255; g = photo.data[index+1]/255; b = photo.data[index+2]/255; alpha = photo.data[index+3]/255;
        }
        const tail = row > stream.length-9 ? (stream.length-row)*.1 : 1;
        geometry[offset] = stream.id; geometry[offset+1] = row; geometry[offset+2] = 0; geometry[offset+3] = stream.shade;
        geometry[offset+4] = r; geometry[offset+5] = g; geometry[offset+6] = b; geometry[offset+7] = alpha;
        geometry[offset+8] = stream.x; geometry[offset+9] = renderY; geometry[offset+10] = stream.z; geometry[offset+11] = stream.length;
        geometry[offset+12] = stream.symbols[row]; geometry[offset+13] = stream.mirrors[row]; geometry[offset+14] = tail;
        geometry[offset+15] = config.volumetric ? clamp((2700-stream.distance)/2700, 0, 1) : 1;
        offset += 16;
      }
      return { data: geometry.subarray(0, offset), capacityBytes: geometry.byteLength, instanceCount: offset/16, count: 6, columns: settings.amount, rows: 40, baseColumns: settings.screenColumns, glyphHalfSize: h, nativeMode: true };
    },
    snapshot(limit = 8) {
      const current = snapshot();
      let taggedStreams = 0;
      for (let i = 0; i < activeCount(); i++) taggedStreams += Number(streams[i].picture);
      return {
        targetStreams: settings.amount, activeStreams: current.activeStreams, elapsed, simulated,
        glyphHalfSize: h, screenColumns: settings.screenColumns,
        motion: { forwardVelocity: current.forwardVelocity, sidewaysVelocity, forwardDistance, sidewaysDistance },
        picture: { forming: pictureMode, weight: pictureWeight, countdown: pictureTimer, periodX: picturePeriodX, periodY: picturePeriodY,
          taggedStreams, grid: pictureGrid ? { ...pictureGrid } : null },
        camera: { eye: [...eye], up: [...up], angles: [...angles], moving: phaseMoving, phaseRemaining: phaseTimer, manual: current.mouseActive },
        streams: streams.slice(0, Math.min(activeCount(), Math.round(clamp(finite(limit, 8), 0, 32)))).map(s => ({
          id: s.id, x: s.x, y: s.y, z: s.z, halfSize: h, fallSpeed: s.speed,
          length: s.length, visibleCount: s.visible, remainder: s.remainder, renderY: s.y+s.remainder, picture: s.picture,
        })),
      };
    },
    // Returns copies for equation tests; renderer uses only the packed buffer.
    inspect() { return streams.slice(0, activeCount()).map(s => ({ id: s.id, x: s.x, y: s.y, z: s.z, speed: s.speed, length: s.length, visible: s.visible, remainder: s.remainder, renderY: s.y+s.remainder, picture: s.picture })); },
    dispose() { dead = true; ordered.length = 0; streams.length = 0; geometry = new Float32Array(0); },
  };
  snapshot(); return api;
}
