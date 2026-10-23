export const MAX_ATLAS_PIXELS = 16777216;
const assertActive = signal => { if (signal?.aborted) throw new DOMException("ZMatrix resource load was canceled", "AbortError"); };
export async function decodeImage(url, signal) {
  assertActive(signal);
  const response = await fetch(url, { signal });
  if (!response.ok) throw new Error(`ZMatrix image failed (${response.status})`);
  const blob = await response.blob(); assertActive(signal);
  const image = await createImageBitmap(blob);
  if (signal?.aborted) { image.close(); assertActive(signal); }
  return image;
}
const canvas = (width, height) => { const result = document.createElement("canvas"); result.width = width; result.height = height; return result; };
const crop = (pixels, pageWidth, x, y, width, height) => {
  const result = new Uint8ClampedArray(width * height * 4);
  for (let row = 0; row < height; row++) result.set(pixels.subarray(((row + y) * pageWidth + x) * 4,
    ((row + y) * pageWidth + x + width) * 4), row * width * 4);
  return result;
};
export function validateNativeFont(metadata, repertoire, maximumGlyphs = 512) {
  if (!metadata || !Array.isArray(metadata.pages) || !metadata.pages.length || metadata.pages.length > 4
    || !Array.isArray(metadata.glyphs) || metadata.glyphs.length > maximumGlyphs)
    throw new Error("ZMatrix native font atlas metadata exceeds its bounds");
  const dpi = Number(metadata.atlasDpi);
  const metrics = metadata.metrics;
  if (!Number.isFinite(dpi) || dpi < 48 || dpi > 768 || !metrics || !Number.isFinite(metrics.height) || metrics.height < 1 || metrics.height > 1024
    || !Number.isFinite(metrics.maxCharWidth) || metrics.maxCharWidth < 1 || metrics.maxCharWidth > 2048)
    throw new Error("ZMatrix native font metrics are invalid");
  let total = 0;
  for (const page of metadata.pages) {
    if (!page || !Number.isInteger(page.width) || !Number.isInteger(page.height) || page.width < 1 || page.height < 1
      || page.width > 2048 || page.height > 2048 || typeof page.url !== "string") throw new Error("ZMatrix native atlas page is invalid");
    total += page.width * page.height;
  }
  if (total > MAX_ATLAS_PIXELS) throw new Error("ZMatrix native font atlas exceeds its pixel budget");
  let glyphPixels = 0;
  const texts = new Set();
  for (const descriptor of metadata.glyphs) {
    if (!descriptor || typeof descriptor !== "object") throw new Error("ZMatrix native glyph rectangle is invalid");
    const { text, page, x, y, width, height, offsetX, offsetY } = descriptor;
    if (typeof text !== "string" || text.length !== 1 || texts.has(text) || !Number.isInteger(page) || !metadata.pages[page]
      || [x, y, width, height].some(n => !Number.isInteger(n) || n < 0)
      || x + width > metadata.pages[page].width || y + height > metadata.pages[page].height
      || !Number.isFinite(offsetX) || !Number.isFinite(offsetY) || Math.abs(offsetX) > 2048 || Math.abs(offsetY) > 2048)
      throw new Error("ZMatrix native glyph rectangle is invalid");
    texts.add(text); glyphPixels += width * height;
    if (glyphPixels > MAX_ATLAS_PIXELS) throw new Error("ZMatrix native glyphs exceed their pixel budget");
  }
  if (repertoire.some(text => !texts.has(text))) throw new Error("ZMatrix native atlas omitted requested characters");
  return { dpi, metrics };
}
export async function loadNativeFont(metadata, repertoire, signal, maximumGlyphs = 512) {
  assertActive(signal);
  const { dpi, metrics } = validateNativeFont(metadata, repertoire, maximumGlyphs);
  const glyphs = new Map(), pages = [];
  try {
    for (const page of metadata.pages) {
      const image = await decodeImage(page.url, signal);
      let temporary;
      try {
        if (image.width !== page.width || image.height !== page.height) throw new Error("ZMatrix atlas dimensions differ from their metadata");
        temporary = canvas(page.width, page.height); const context = temporary.getContext("2d", { willReadFrequently: true });
        if (!context) throw new Error("ZMatrix cannot decode native font pixels");
        context.drawImage(image, 0, 0); pages.push(context.getImageData(0, 0, page.width, page.height).data);
      } finally { image.close(); if (temporary) { temporary.width = 0; temporary.height = 0; } }
    }
    for (const descriptor of metadata.glyphs) {
      const { text, page, x, y, width, height, offsetX, offsetY } = descriptor;
      glyphs.set(text, { width, height, offsetX, offsetY, scale: 96 / dpi, channels: 4,
        pixels: crop(pages[page], metadata.pages[page].width, x, y, width, height) });
    }
    assertActive(signal);
    return { glyphs, metrics: { ...metrics }, atlasDpi: dpi, source: "native-gdi", resolvedFace: metadata.resolvedFace,
      warnings: Array.isArray(metadata.warnings) ? metadata.warnings.map(String) : [],
      dispose() { glyphs.clear(); } };
  } finally { pages.length = 0; }
}
let fontAssetPromise = null;
async function loadBundledFont() {
  if (!globalThis.FontFace || !document.fonts) return;
  fontAssetPromise ??= new FontFace("Matrix Code Font", "url(assets/zmatrix/matrix-code.ttf)").load()
    .then(face => { document.fonts.add(face); }).catch(() => {});
  await fontAssetPromise;
}
export async function createBrowserFont(font, repertoire, signal) {
  await loadBundledFont(); assertActive(signal);
  const glyphs = new Map();
  const size = Math.max(1, Math.abs(font.height) || 12), glyphCanvas = canvas(1, 1);
  const context = glyphCanvas.getContext("2d", { willReadFrequently: true });
  if (!context) throw new Error("ZMatrix browser font fallback requires Canvas2D");
  const cssFont = `${font.italic ? "italic " : ""}${font.weight || 400} ${size}px ${JSON.stringify(font.name)}, monospace`;
  context.font = cssFont;
  const measured = context.measureText("Mg");
  const height = Math.max(1, Math.ceil((measured.fontBoundingBoxAscent ?? size) + (measured.fontBoundingBoxDescent ?? 0)));
  let maxWidth = Math.max(1, font.width || Math.ceil(Math.max(...repertoire.map(text => context.measureText(text).width))));
  const padding = Math.min(1024, Math.ceil(Math.max(height, maxWidth, size) * 3 + 4));
  glyphCanvas.width = padding * 2; glyphCanvas.height = padding * 2;
  if (glyphCanvas.width * glyphCanvas.height * Math.max(1, repertoire.length) > 67108864) {
    glyphCanvas.width = 0; glyphCanvas.height = 0;
    throw new Error("ZMatrix browser font fallback exceeds its raster work budget; use native GDI fonts or a smaller repertoire");
  }
  let totalPixels = 0;
  try {
    for (const text of repertoire) {
      assertActive(signal);
      context.clearRect(0, 0, glyphCanvas.width, glyphCanvas.height); context.save();
      context.translate(padding, padding); context.rotate(-font.orientation * Math.PI / 1800);
      context.font = cssFont; context.textAlign = "center"; context.textBaseline = "top"; context.fillStyle = "white";
      if (font.width) context.scale(font.width / Math.max(1, context.measureText(text).width), 1);
      context.fillText(text, 0, 0);
      if (font.underline) context.fillRect(-maxWidth / 2, height - 1, maxWidth, 1);
      if (font.strikeOut) context.fillRect(-maxWidth / 2, height / 2, maxWidth, 1);
      context.restore();
      const image = context.getImageData(0, 0, glyphCanvas.width, glyphCanvas.height).data;
      let left = glyphCanvas.width, top = glyphCanvas.height, right = 0, bottom = 0;
      for (let y = 0; y < glyphCanvas.height; y++) for (let x = 0; x < glyphCanvas.width; x++) if (image[(y * glyphCanvas.width + x) * 4 + 3]) {
        left = Math.min(left, x); top = Math.min(top, y); right = Math.max(right, x + 1); bottom = Math.max(bottom, y + 1);
      }
      const width = Math.max(0, right - left), inkHeight = Math.max(0, bottom - top);
      totalPixels += width * inkHeight;
      if (totalPixels > MAX_ATLAS_PIXELS) throw new Error("ZMatrix browser font exceeds its pixel budget");
      const pixels = crop(image, glyphCanvas.width, width ? left : 0, inkHeight ? top : 0, width, inkHeight);
      for (let p = 0; p < pixels.length; p += 4) pixels[p] = pixels[p + 1] = pixels[p + 2] = pixels[p + 3];
      glyphs.set(text, { width, height: inkHeight, pixels, channels: 4, scale: 1,
        offsetX: width ? left - padding : 0, offsetY: inkHeight ? top - padding : 0 });
    }
    return { glyphs, metrics: { height, maxCharWidth: maxWidth }, atlasDpi: 96, source: "browser-fallback", resolvedFace: "CSS font selection",
      warnings: ["Browser font fallback cannot reproduce exact GDI metrics, charset, precision, quality, or independent escapement. Requested face availability is unverified."],
      dispose() { glyphs.clear(); } };
  } finally { glyphCanvas.width = 0; glyphCanvas.height = 0; }
}
export async function loadFonts(config, bootstrap, signal) {
  const result = {}, specialCharacters = [...new Set(config.zmPhrases.join(""))];
  // An empty phrase list needs metrics but no special glyphs.
  for (const [key, descriptor, repertoire, maximum] of [["normal", config.zmFont, config.zmCharacters, 512],
    ["special", config.zmSpecialFont, specialCharacters, 1024]]) {
    try {
      const metadata = bootstrap?.zmatrixFonts?.[key];
      try { result[key] = await loadNativeFont(metadata, repertoire, signal, maximum); }
      catch (error) {
        assertActive(signal);
        result[key] = await createBrowserFont(descriptor, repertoire, signal);
        result[key].warnings.unshift(`Native ${key} font unavailable: ${error.message}`);
      }
    } catch (error) { for (const font of Object.values(result)) font.dispose(); throw error; }
  }
  return result;
}
