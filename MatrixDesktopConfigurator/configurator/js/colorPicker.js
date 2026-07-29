// Shared popover color picker operating on {r,g,b} floats in the 0..1 range
// (the same representation the rest of the configurator and the matrix web app
// use). Provides a draggable saturation/value square + hue bar, plus precise
// hex and 0..1 RGB entry. Calls onChange({r,g,b}) live during interaction.
//
// One popover element is created lazily and reused for every swatch, so the
// DOM stays light no matter how many colors / palette stops are on screen.

const clamp01 = (v) => Math.max(0, Math.min(1, Number.isFinite(v) ? v : 0));

export const rgbToHsv = ({ r, g, b }) => {
	r = clamp01(r); g = clamp01(g); b = clamp01(b);
	const max = Math.max(r, g, b);
	const min = Math.min(r, g, b);
	const d = max - min;
	let h = 0;
	if (d !== 0) {
		if (max === r) h = ((g - b) / d) % 6;
		else if (max === g) h = (b - r) / d + 2;
		else h = (r - g) / d + 4;
		h *= 60;
		if (h < 0) h += 360;
	}
	return { h, s: max === 0 ? 0 : d / max, v: max };
};

export const hsvToRgb = ({ h, s, v }) => {
	h = ((h % 360) + 360) % 360;
	s = clamp01(s);
	v = clamp01(v);
	const c = v * s;
	const x = c * (1 - Math.abs(((h / 60) % 2) - 1));
	const m = v - c;
	let r = 0, g = 0, b = 0;
	if (h < 60) { r = c; g = x; }
	else if (h < 120) { r = x; g = c; }
	else if (h < 180) { g = c; b = x; }
	else if (h < 240) { g = x; b = c; }
	else if (h < 300) { r = x; b = c; }
	else { r = c; b = x; }
	return { r: r + m, g: g + m, b: b + m };
};

export const colorToHex = ({ r, g, b }) => {
	const ch = (v) => Math.round(clamp01(v) * 255).toString(16).padStart(2, "0");
	return `#${ch(r)}${ch(g)}${ch(b)}`;
};

export const hexToColor = (hex) => {
	const n = String(hex || "").replace(/[^0-9a-f]/gi, "");
	const s = n.length === 3 ? n.split("").map((c) => c + c).join("") : n.padEnd(6, "0").slice(0, 6);
	return {
		r: parseInt(s.slice(0, 2), 16) / 255,
		g: parseInt(s.slice(2, 4), 16) / 255,
		b: parseInt(s.slice(4, 6), 16) / 255,
	};
};

const fmt = (v) => {
	const n = clamp01(v);
	return Number.isInteger(n) ? String(n) : n.toFixed(3).replace(/0+$/, "").replace(/\.$/, "");
};

let refs = null;   // cached element references (built once)
let ctx = null;    // { hsv, color, onChange, anchor }
let outsideHandler = null;
let keyHandler = null;

const build = () => {
	const root = document.createElement("div");
	root.className = "color-popover";
	root.hidden = true;
	root.innerHTML = `
		<div class="cp-square" tabindex="-1"><div class="cp-square-thumb"></div></div>
		<div class="cp-hue"><div class="cp-hue-thumb"></div></div>
		<div class="cp-inputs">
			<label class="cp-hex-field">HEX<input type="text" class="cp-hex" spellcheck="false" maxlength="7" /></label>
			<div class="cp-rgb">
				<label>R<input type="number" class="cp-r" min="0" max="1" step="0.01" /></label>
				<label>G<input type="number" class="cp-g" min="0" max="1" step="0.01" /></label>
				<label>B<input type="number" class="cp-b" min="0" max="1" step="0.01" /></label>
			</div>
		</div>`;
	document.body.appendChild(root);
	refs = {
		root,
		square: root.querySelector(".cp-square"),
		squareThumb: root.querySelector(".cp-square-thumb"),
		hue: root.querySelector(".cp-hue"),
		hueThumb: root.querySelector(".cp-hue-thumb"),
		hex: root.querySelector(".cp-hex"),
		r: root.querySelector(".cp-r"),
		g: root.querySelector(".cp-g"),
		b: root.querySelector(".cp-b"),
	};
	wire();
	return root;
};

const emit = (skip) => {
	ctx.color = hsvToRgb(ctx.hsv);
	render(skip);
	if (ctx.onChange) ctx.onChange({ ...ctx.color });
};

const render = (skip) => {
	const { h, s, v } = ctx.hsv;
	const hex = colorToHex(ctx.color);
	refs.square.style.background =
		`linear-gradient(to top, #000, rgba(0,0,0,0)), linear-gradient(to right, #fff, hsl(${h}, 100%, 50%))`;
	refs.squareThumb.style.left = `${s * 100}%`;
	refs.squareThumb.style.top = `${(1 - v) * 100}%`;
	refs.squareThumb.style.background = hex;
	refs.hueThumb.style.left = `${(h / 360) * 100}%`;
	if (skip !== refs.hex) refs.hex.value = hex;
	if (skip !== refs.r) refs.r.value = fmt(ctx.color.r);
	if (skip !== refs.g) refs.g.value = fmt(ctx.color.g);
	if (skip !== refs.b) refs.b.value = fmt(ctx.color.b);
};

// Preserve the current hue when saturation collapses to 0 (grays have no
// meaningful hue), so dragging value up/down doesn't reset the hue bar.
const hsvFromColor = (color) => {
	const next = rgbToHsv(color);
	if (next.s === 0 && ctx) next.h = ctx.hsv.h;
	return next;
};

const dragSquare = (clientX, clientY) => {
	const rect = refs.square.getBoundingClientRect();
	ctx.hsv.s = clamp01((clientX - rect.left) / rect.width);
	ctx.hsv.v = clamp01(1 - (clientY - rect.top) / rect.height);
	emit();
};

const dragHue = (clientX) => {
	const rect = refs.hue.getBoundingClientRect();
	ctx.hsv.h = clamp01((clientX - rect.left) / rect.width) * 360;
	emit();
};

const startDrag = (handler) => (event) => {
	event.preventDefault();
	handler(event.clientX, event.clientY);
	const move = (ev) => handler(ev.clientX, ev.clientY);
	const up = () => {
		document.removeEventListener("pointermove", move);
		document.removeEventListener("pointerup", up);
	};
	document.addEventListener("pointermove", move);
	document.addEventListener("pointerup", up);
};

const wire = () => {
	refs.square.addEventListener("pointerdown", startDrag(dragSquare));
	refs.hue.addEventListener("pointerdown", startDrag((x) => dragHue(x)));

	refs.hex.addEventListener("input", () => {
		ctx.color = hexToColor(refs.hex.value);
		ctx.hsv = hsvFromColor(ctx.color);
		render(refs.hex);
		if (ctx.onChange) ctx.onChange({ ...ctx.color });
	});

	for (const channel of ["r", "g", "b"]) {
		refs[channel].addEventListener("input", () => {
			ctx.color = { ...ctx.color, [channel]: clamp01(Number.parseFloat(refs[channel].value)) };
			ctx.hsv = hsvFromColor(ctx.color);
			render(refs[channel]);
			if (ctx.onChange) ctx.onChange({ ...ctx.color });
		});
	}
};

const position = (anchor) => {
	const a = anchor.getBoundingClientRect();
	const pop = refs.root.getBoundingClientRect();
	let left = a.left;
	let top = a.bottom + 6;
	if (left + pop.width > window.innerWidth - 8) left = Math.max(8, window.innerWidth - pop.width - 8);
	if (top + pop.height > window.innerHeight - 8) top = Math.max(8, a.top - pop.height - 6);
	refs.root.style.left = `${Math.max(8, left)}px`;
	refs.root.style.top = `${Math.max(8, top)}px`;
};

const detachGlobal = () => {
	if (outsideHandler) document.removeEventListener("pointerdown", outsideHandler, true);
	if (keyHandler) document.removeEventListener("keydown", keyHandler);
	outsideHandler = null;
	keyHandler = null;
};

export const closeColorPicker = () => {
	detachGlobal();
	if (refs) refs.root.hidden = true;
	ctx = null;
};

export const openColorPicker = (anchor, color, onChange) => {
	if (!refs) build();
	const c = { r: clamp01(Number(color?.r)), g: clamp01(Number(color?.g)), b: clamp01(Number(color?.b)) };
	ctx = { hsv: rgbToHsv(c), color: c, onChange, anchor };
	refs.root.hidden = false;
	position(anchor);
	render();

	detachGlobal();
	outsideHandler = (event) => {
		if (refs.root.contains(event.target) || anchor === event.target || anchor.contains(event.target)) return;
		closeColorPicker();
	};
	keyHandler = (event) => { if (event.key === "Escape") closeColorPicker(); };
	// Defer so the click that opened the popover doesn't immediately close it.
	setTimeout(() => {
		if (!ctx) return;
		document.addEventListener("pointerdown", outsideHandler, true);
		document.addEventListener("keydown", keyHandler);
	}, 0);
};
