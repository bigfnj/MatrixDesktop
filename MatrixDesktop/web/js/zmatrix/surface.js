import { normalizeZMatrixSettings } from "./settings.js";

export const MAX_SURFACE_PIXELS = 16777216;
export const MAX_SURFACE_DIMENSION = 8192;
export function fitSurface(width, height, maximumDimension = MAX_SURFACE_DIMENSION) {
  width = Math.max(1, Math.ceil(Number.isFinite(width) ? width : 1));
  height = Math.max(1, Math.ceil(Number.isFinite(height) ? height : 1));
  const factor = Math.min(1, maximumDimension / width, maximumDimension / height,
    Math.sqrt(MAX_SURFACE_PIXELS / width / height));
  return { width: Math.max(1, Math.floor(width * factor)), height: Math.max(1, Math.floor(height * factor)), factor };
}
export function rasterOperation(background, source, operation) {
  if (operation === "xor") return background ^ source;
  if (operation === "and") return background & source;
  return background | source;
}
const blend = (background, foreground, coverage) => Math.round(background + (foreground - background) * coverage / 255);

// Persistent pixels, not a tail-age approximation. The canvas is only an upload
// surface; this byte array defines the same result for either GPU backend.
export function createSurface(width, height, options = {}, scale = 1) {
  if (!Number.isInteger(width) || !Number.isInteger(height) || width < 1 || height < 1
    || width > MAX_SURFACE_DIMENSION || height > MAX_SURFACE_DIMENSION || width * height > MAX_SURFACE_PIXELS)
    throw new RangeError("ZMatrix surface exceeds the bounded pixel budget");
  const config = normalizeZMatrixSettings(options);
  let pixels = new Uint8ClampedArray(width * height * 4);
  let background = new Uint8ClampedArray(pixels.length), revision = 0, dead = false, dirty = null;
  scale = Number.isFinite(scale) && scale > 0 ? scale : 1;
  const mark = rect => {
    if (rect.right <= rect.left || rect.bottom <= rect.top) return;
    if (dirty) { dirty.left = Math.min(dirty.left, rect.left); dirty.top = Math.min(dirty.top, rect.top);
      dirty.right = Math.max(dirty.right, rect.right); dirty.bottom = Math.max(dirty.bottom, rect.bottom); }
    else dirty = { ...rect };
    revision++;
  };
  const clip = rect => ({ left: Math.max(0, Math.floor(rect.x * scale)), top: Math.max(0, Math.floor(rect.y * scale)),
    right: Math.min(width, Math.ceil((rect.x + rect.width) * scale)), bottom: Math.min(height, Math.ceil((rect.y + rect.height) * scale)) });
  const fill = (rect, restore) => {
    for (let y = rect.top; y < rect.bottom; y++) for (let x = rect.left; x < rect.right; x++) {
      const p = (y * width + x) * 4;
      for (let c = 0; c < 3; c++) pixels[p + c] = restore ? background[p + c] : config.zmBackgroundColor[c];
      pixels[p + 3] = 255;
    }
    mark(rect);
  };
  const reset = () => { if (!dead) fill({ left: 0, top: 0, right: width, bottom: height }, config.zmBackgroundMode === "bitmap"); };
  for (let p = 0; p < background.length; p += 4) {
    background.set(config.zmBackgroundColor, p); background[p + 3] = 255;
  }
  reset();
  return {
    width, height, scale,
    get pixels() { return pixels; },
    get revision() { return revision; },
    get byteLength() { return (pixels?.byteLength ?? 0) + (background?.byteLength ?? 0); },
    setBackground(data) {
      if (dead) return;
      if (!data || data.length !== pixels.length) throw new Error("ZMatrix wallpaper pixels do not match the surface");
      background.set(data);
      for (let p = 3; p < background.length; p += 4) background[p] = 255;
      reset();
    },
    apply(op, fonts) {
      if (dead) return;
      const cell = clip(op);
      if (op.type === "clear") { fill(cell, config.zmBackgroundMode === "bitmap"); return; }
      const glyph = fonts?.[op.font]?.glyphs?.get(op.text);
      const bitmap = config.zmBackgroundMode === "bitmap";
      if (!bitmap && config.zmOpaqueBackground) fill(cell, false);
      let ink = null;
      if (glyph && glyph.width && glyph.height) {
        const glyphScale = glyph.scale ?? 1;
        ink = { x: (op.anchorX ?? op.x + op.width / 2) + glyph.offsetX,
          y: op.y + glyph.offsetY, width: glyph.width * glyphScale, height: glyph.height * glyphScale };
      }
      const inkRect = ink ? clip(ink) : { left: 0, top: 0, right: 0, bottom: 0 };
      const target = bitmap ? cell : inkRect;
      // Bitmap operations use the original captured background even when a
      // previous stream has drawn here. Color/transparent mode retains pixels.
      for (let y = target.top; y < target.bottom; y++) for (let x = target.left; x < target.right; x++) {
        const p = (y * width + x) * 4;
        let sample = -1;
        if (ink && x + .5 >= ink.x * scale && x + .5 < (ink.x + ink.width) * scale
          && y + .5 >= ink.y * scale && y + .5 < (ink.y + ink.height) * scale) {
          const gx = Math.min(glyph.width - 1, Math.floor(((x + .5) / scale - ink.x) / ink.width * glyph.width));
          const gy = Math.min(glyph.height - 1, Math.floor(((y + .5) / scale - ink.y) / ink.height * glyph.height));
          sample = (gy * glyph.width + gx) * (glyph.channels ?? 4);
        }
        for (let c = 0; c < 3; c++) {
          const coverage = sample < 0 ? 0 : glyph.pixels[sample + ((glyph.channels ?? 4) === 1 ? 0 : c)];
          if (bitmap) {
            const base = config.zmOpaqueBackground ? config.zmBackgroundColor[c] : background[p + c];
            const source = blend(base, op.color[c], coverage);
            pixels[p + c] = config.zmOpaqueBackground || config.zmBlendMode === "xor"
              ? rasterOperation(background[p + c], source, config.zmBlendMode) : source;
          } else if (coverage) pixels[p + c] = blend(pixels[p + c], op.color[c], coverage);
        }
        pixels[p + 3] = 255;
      }
      mark(target);
    },
    consumeDirty() { const result = dirty; dirty = null; return result; },
    reset,
    snapshot(points = []) {
      if (dead) return [];
      return points.slice(0, 32).map(([x, y]) => { x = Math.floor(x); y = Math.floor(y);
        return x < 0 || x >= width || y < 0 || y >= height ? null : [...pixels.subarray((y * width + x) * 4, (y * width + x) * 4 + 4)]; });
    },
    dispose() { if (dead) return; dead = true; pixels = null; background = null; dirty = null; },
  };
}
