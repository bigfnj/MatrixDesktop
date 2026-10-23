import { normalizeZMatrixSettings } from "./settings.js";

// xorshift32 is a public, general-purpose PRNG. The optional seed makes the new
// simulation reproducible in tests without depending on a native executable.
export function createRandom(seed) {
  let state = (Number(seed) >>> 0) || 0x9e3779b9;
  return {
    next() { state ^= state << 13; state ^= state >>> 17; state ^= state << 5; return state >>> 0; },
    range(max) { return max <= 1 ? 0 : Math.floor(this.next() * max / 0x100000000); },
    unit() { return (this.next() >>> 8) / 0x1000000; },
    state() { return state >>> 0; },
  };
}
export function streamColor(rgb, wait, variance, gains, offsets) {
  return rgb.map((channel, i) => Math.max(0, Math.min(255,
    Math.trunc((channel - Math.floor(wait * channel / (variance + 1))) * gains[i] + offsets[i]))));
}
export function cleanupCell(stream, distance, normalHeight, specialHeight, phraseLength, columnWidth) {
  let normal = 0, special = 0, height = normalHeight;
  if (stream.phraseIndex < 0) normal = distance;
  else if (stream.phraseIndex <= phraseLength) {
    special = Math.min(distance, stream.phraseIndex);
    normal = distance - special;
    if (distance <= stream.phraseIndex) height = specialHeight;
  } else {
    const after = stream.phraseIndex - phraseLength;
    normal = Math.min(distance, after);
    special = Math.min(Math.max(0, distance - after), phraseLength);
    normal += Math.max(0, distance - after - phraseLength);
    if (distance > after && distance <= after + phraseLength) height = specialHeight;
  }
  return { type: "clear", x: stream.x, y: stream.y - normal * normalHeight - special * specialHeight,
    width: columnWidth + 1, height: height + 1, distance };
}
export function createScene(options = {}, environment = {}) {
  const config = normalizeZMatrixSettings(options);
  const randomSeed = environment.seed ?? (globalThis.crypto?.getRandomValues
    ? globalThis.crypto.getRandomValues(new Uint32Array(1))[0] : Math.floor(Math.random() * 0x100000000));
  const random = createRandom(randomSeed);
  const gains = [config.zmRedGain, config.zmGreenGain, config.zmBlueGain];
  const offsets = [config.zmRedOffset, config.zmGreenOffset, config.zmBlueOffset];
  const streams = Array.from({ length: config.zmMaxStream }, (_, id) => ({ id, active: false }));
  let width = 1, height = 1, normalHeight = 7, specialHeight = 8, columnWidth = 7, nativeScale = 1;
  let active = 0, ticks = 0, remainder = 0, droppedMs = 0, dead = false, lastOperations = 0;
  const maximumTail = Math.max(config.zmMonotonousCleanupEnabled ? config.zmBackTrace : 0,
    config.zmRandomizedCleanupEnabled ? config.zmLeading + config.zmSpacePad : 0);
  const viewport = (w, h, metrics = {}) => {
    if (dead) return;
    width = Math.max(1, Number.isFinite(w) ? w : 1); height = Math.max(1, Number.isFinite(h) ? h : 1);
    normalHeight = Math.max(1, metrics.normalHeight ?? normalHeight);
    specialHeight = Math.max(1, metrics.specialHeight ?? specialHeight);
    columnWidth = Math.max(1, metrics.columnWidth ?? columnWidth);
    nativeScale = Math.max(.5, metrics.nativeScale ?? nativeScale);
  };
  viewport(environment.width ?? 800, environment.height ?? 600, environment.metrics);
  const tick = (emit = () => {}) => {
    if (dead) return 0;
    ticks++; lastOperations = 0;
    if (active < streams.length) {
      const stream = streams.find(s => !s.active);
      Object.assign(stream, { active: true, x: random.range(Math.ceil(width / columnWidth)) * columnWidth, y: 0,
        wait: random.range(config.zmSpeedVariance + 1), draw: true, phraseIndex: -1,
        eligible: config.zmPhrases.length > 0 && random.unit() < config.zmSpecialStringProbability,
        phrase: config.zmPhrases.length ? random.range(config.zmPhrases.length) : 0 });
      stream.remaining = stream.wait; active++;
    }
    const bottom = height + maximumTail * Math.max(normalHeight, specialHeight);
    for (const stream of streams) if (stream.active && stream.y > bottom) { stream.active = false; active--; }
    const rows = Math.floor(height / normalHeight);
    for (const stream of streams) if (stream.active) {
      if (stream.remaining > 0) { stream.remaining--; continue; }
      stream.y += normalHeight; stream.draw = true; stream.remaining = stream.wait;
      if (stream.eligible) {
        const length = config.zmPhrases[stream.phrase].length;
        if (stream.phraseIndex < 0) {
          if (!rows || random.range(rows) === 0) stream.phraseIndex = 0;
        } else {
          if (stream.phraseIndex < length) stream.y += specialHeight - normalHeight;
          stream.phraseIndex = Math.min(length + maximumTail + 2, stream.phraseIndex + 1);
        }
      }
    }
    const output = op => { lastOperations++; emit(op); };
    for (const stream of streams) if (stream.active && stream.draw) {
      stream.draw = false;
      const phrase = stream.eligible ? config.zmPhrases[stream.phrase] : "";
      const index = stream.phraseIndex;
      const currentSpecial = index >= 0 && index < phrase.length;
      const previousSpecial = index > 0 && index <= phrase.length;
      for (const [previous, special] of [[false, currentSpecial], [true, previousSpecial]]) {
        const text = special ? phrase[index - (previous ? 1 : 0)] : config.zmCharacters[random.range(config.zmCharacters.length)];
        const fontHeight = special ? specialHeight : normalHeight;
        const color = special ? previous ? config.zmSpecialFadeColor : config.zmSpecialForegroundColor
          : previous ? config.zmFadeColor : config.zmForegroundColor;
        output({ type: "glyph", stream: stream.id, x: stream.x, y: stream.y - (previous ? fontHeight : 0),
          width: columnWidth, height: fontHeight + 1, anchorX: stream.x + Math.floor(columnWidth * nativeScale / 2) / nativeScale,
          text, font: special ? "special" : "normal", previous,
          color: streamColor(color, stream.wait, config.zmSpeedVariance, gains, offsets) });
      }
      if (config.zmRandomizedCleanupEnabled) output(cleanupCell(stream,
        config.zmLeading + random.range(config.zmSpacePad + 1), normalHeight, specialHeight, phrase.length, columnWidth));
      if (config.zmMonotonousCleanupEnabled) output(cleanupCell(stream,
        config.zmBackTrace, normalHeight, specialHeight, phrase.length, columnWidth));
    }
    return lastOperations;
  };
  return {
    config, viewport, tick,
    advance(milliseconds, emit) {
      if (dead) return 0;
      const delta = Math.max(0, Number.isFinite(milliseconds) ? milliseconds : 0);
      droppedMs += Math.max(0, delta - 1000); remainder += Math.min(delta, 1000);
      let count = 0;
      while (remainder + 1e-8 >= config.zmRefreshTime) { remainder -= config.zmRefreshTime; tick(emit); count++; }
      if (Math.abs(remainder) < 1e-8) remainder = 0;
      return count;
    },
    snapshot(limit = 8) {
      if (dead) return { disposed: true };
      return { width, height, ticks, remainderMs: Math.max(0, remainder), droppedMs, activeStreams: active,
        targetStreams: streams.length, normalHeight, specialHeight, columnWidth, lastOperations, randomState: random.state(),
        streams: streams.filter(s => s.active).slice(0, Math.max(0, Math.min(32, limit))).map(s => ({ id: s.id, x: s.x, y: s.y,
          wait: s.wait, remaining: s.remaining, eligible: s.eligible, phrase: s.phrase, phraseIndex: s.phraseIndex })) };
    },
    dispose() { if (dead) return; dead = true; streams.length = 0; active = 0; remainder = 0; },
  };
}
