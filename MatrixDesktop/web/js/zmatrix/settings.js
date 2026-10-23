// Fresh implementation of the documented ZMatrix feature set. Values here use
// portable glyph counts and 96-DPI font units, rather than GDI device pixels.
const regularCharacters = [..."1234567890ABCDEFGHIJKLMNOPQRSUTVWXYZabcd"];
export const defaultFont = Object.freeze({ height: -7, width: 0, escapement: 0, orientation: 0, weight: 400,
  italic: false, underline: false, strikeOut: false, charSet: 1, outPrecision: 0, clipPrecision: 0,
  quality: 0, pitch: 0, family: 0, name: "Matrix Code Font" });
export const defaultSpecialFont = Object.freeze({ ...defaultFont, height: -8, name: "Terminal" });

const number = (value, min, max, integer = false) => {
  if (value === "" || value == null || typeof value === "boolean") return null;
  if (typeof value !== "number" && typeof value !== "string") return null;
  if (typeof value === "string") {
    value = value.trim();
    if (!/^[+-]?(?:\d+\.?\d*|\.\d+)(?:e[+-]?\d+)?$/i.test(value)) return null;
  }
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) return null;
  const bounded = Math.max(min, Math.min(max, parsed));
  return integer ? Math.trunc(bounded) : bounded;
};
const boolean = value => typeof value === "boolean" ? value : typeof value === "string"
  && ["true", "false", "1", "0"].includes(value.toLowerCase()) ? ["true", "1"].includes(value.toLowerCase()) : null;
const json = value => {
  if (typeof value !== "string") return value;
  if (value.startsWith("json64:")) {
    const encoded = value.slice(7);
    if (encoded.length > 87384 || !/^[A-Za-z0-9_-]+$/.test(encoded)) return null;
    try {
      const bytes = Uint8Array.from(atob(encoded.replace(/-/g, "+").replace(/_/g, "/")), character => character.charCodeAt(0));
      if (bytes.length > 65536) return null;
      value = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
    } catch { return null; }
  }
  if (value.length > 65536 || new TextEncoder().encode(value).length > 65536) return null;
  try { return JSON.parse(value); } catch { return null; }
};
export function parseByteColor(value) {
  if (typeof value === "string" && /^#[\da-f]{6}$/i.test(value)) return [1, 3, 5].map(i => parseInt(value.slice(i, i + 2), 16));
  if (typeof value === "string") value = /^[\s]*[\[{]/.test(value) ? json(value) : value.split(",");
  if (value && !Array.isArray(value) && typeof value === "object") value = [value.r, value.g, value.b];
  if (!Array.isArray(value) || value.length !== 3) return null;
  const result = value.map(v => { const n = number(v, 0, 255); return n === null ? null : Math.round(n); });
  return result.includes(null) ? null : result;
}
export const fontFields = Object.freeze({ height: [-256, 256], width: [0, 256], escapement: [-3600, 3600],
  orientation: [-3600, 3600], weight: [0, 1000], charSet: [0, 255], outPrecision: [0, 255],
  clipPrecision: [0, 255], quality: [0, 255], pitch: [0, 3], family: [0, 240] });
export function normalizeFont(value, fallback = defaultFont) {
  value = json(value);
  if (!value || Array.isArray(value) || typeof value !== "object") return null;
  const font = { ...fallback };
  for (const [key, [min, max]] of Object.entries(fontFields)) {
    if (!(key in value)) continue;
    const parsed = number(value[key], min, max, true);
    if (parsed === null) return null;
    font[key] = key === "family" ? parsed & 0xf0 : parsed;
  }
  for (const key of ["italic", "underline", "strikeOut"]) if (key in value) {
    const parsed = boolean(value[key]);
    if (parsed === null) return null;
    font[key] = parsed;
  }
  if ("name" in value) {
    if (typeof value.name !== "string" || !value.name.trim() || value.name.trim().length > 31 || /[\x00-\x1f\x7f\ud800-\udfff]/.test(value.name)) return null;
    font.name = value.name.trim();
  }
  return font;
}
const validText = value => typeof value === "string" && !/[\x00-\x1f\x7f\ud800-\udfff]/.test(value);
const characters = value => {
  value = json(value);
  if (!Array.isArray(value) || !value.length || value.length > 512 || value.some(v => !validText(v) || v.length !== 1)) return null;
  return [...new Set(value)];
};
const phrases = value => {
  value = json(value);
  if (!Array.isArray(value) || value.length > 64 || value.some(v => !validText(v) || !v.length || v.length > 256)) return null;
  const result = [...new Set(value)];
  return new Set(result.join("")).size > 1024 ? null : result;
};
const scalar = (fallback, min, max, integer = false) => ({ kind: integer ? "integer" : "number", default: fallback, min, max,
  parser: value => number(value, min, max, integer) });
const toggle = fallback => ({ kind: "boolean", default: fallback, parser: boolean });
const choice = (fallback, choices) => ({ kind: "enum", default: fallback, choices,
  parser: value => typeof value === "string" && choices.includes(value.toLowerCase()) ? value.toLowerCase() : null });
const rgb = fallback => ({ kind: "rgb8", default: fallback, parser: parseByteColor });
export const zmatrixSettingsSpec = Object.freeze({
  zmMaxStream: scalar(1000, 1, 4096, true), zmSpeedVariance: scalar(5, 0, 120, true), zmRefreshTime: scalar(50, 10, 1000, true),
  zmMonotonousCleanupEnabled: toggle(false), zmBackTrace: scalar(40, 0, 4096, true),
  zmRandomizedCleanupEnabled: toggle(false), zmLeading: scalar(10, 0, 4096, true), zmSpacePad: scalar(30, 0, 4096, true),
  zmSpecialStringProbability: scalar(.1, 0, 1),
  zmCharacters: { kind: "characters", default: regularCharacters, parser: characters },
  zmPhrases: { kind: "phrases", default: ["The matrix has you"], parser: phrases },
  zmBackgroundMode: choice("bitmap", ["bitmap", "color"]), zmBlendMode: choice("or", ["xor", "and", "or"]),
  zmOpaqueBackground: toggle(false),
  zmForegroundColor: rgb([187, 255, 187]), zmFadeColor: rgb([0, 128, 0]), zmBackgroundColor: rgb([0, 0, 0]),
  zmSpecialForegroundColor: rgb([251, 255, 251]), zmSpecialFadeColor: rgb([208, 244, 208]),
  zmRedGain: scalar(1, -4, 4), zmGreenGain: scalar(1, -4, 4), zmBlueGain: scalar(1, -4, 4),
  zmRedOffset: scalar(0, -255, 255), zmGreenOffset: scalar(0, -255, 255), zmBlueOffset: scalar(0, -255, 255),
  zmFont: { kind: "font", default: defaultFont, parser: value => normalizeFont(value) },
  zmSpecialFont: { kind: "font", default: defaultSpecialFont, parser: value => normalizeFont(value, defaultSpecialFont) },
});
const clone = value => Array.isArray(value) ? [...value] : value && typeof value === "object" ? { ...value } : value;
export const zmatrixDefaults = Object.freeze(Object.fromEntries(Object.entries(zmatrixSettingsSpec).map(([key, spec]) => [key,
  spec.default && typeof spec.default === "object" ? Object.freeze(spec.default) : spec.default])));
export const zmatrixParamMapping = Object.freeze(Object.fromEntries(Object.entries(zmatrixSettingsSpec).map(([key, spec]) => [key, { key, parser: spec.parser }])));
export function normalizeZMatrixSettings(options = {}, warnings = []) {
  return Object.fromEntries(Object.entries(zmatrixSettingsSpec).map(([key, spec]) => {
    const parsed = options[key] == null ? clone(spec.default) : spec.parser(options[key]);
    if (parsed === null) warnings.push(`Invalid ${key}; using its default.`);
    return [key, parsed === null ? clone(spec.default) : parsed];
  }));
}
