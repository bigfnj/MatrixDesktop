import { openColorPicker } from "./colorPicker.js";

const state = {
	metadata: null,
	defaultDraft: {},
	lastDraft: {},
	draft: {},
	presets: [],
	selectedPresetId: null,
	activeGroupId: null,
	filter: "",
	dirty: false,
	testRunning: false,
	requestId: 0,
	pending: new Map(),
	saveTimer: 0,
	commandTimer: 0,
	// v1.0 additions
	uiTheme: "dark",
	previewTimer: 0,
	// Embedded live-preview pane (iframe of the matrix web app).
	previewVisible: false,
	previewAvailable: false,
	previewOrigin: null,
	previewLastUrl: "",
};

const el = {
	storageStatus: document.querySelector("#storageStatus"),
	presetSelect: document.querySelector("#presetSelect"),
	newPresetButton: document.querySelector("#newPresetButton"),
	savePresetButton: document.querySelector("#savePresetButton"),
	saveAsPresetButton: document.querySelector("#saveAsPresetButton"),
	renamePresetButton: document.querySelector("#renamePresetButton"),
	deletePresetButton: document.querySelector("#deletePresetButton"),
	groupNav: document.querySelector("#groupNav"),
	fieldFilter: document.querySelector("#fieldFilter"),
	sectionJump: document.querySelector("#sectionJump"),
	fieldSurface: document.querySelector("#fieldSurface"),
	commandOutput: document.querySelector("#commandOutput"),
	randomizeScope: document.querySelector("#randomizeScope"),
	randomizeButton: document.querySelector("#randomizeButton"),
	importButton: document.querySelector("#importButton"),
	importPanel: document.querySelector("#importPanel"),
	importInput: document.querySelector("#importInput"),
	applyImportButton: document.querySelector("#applyImportButton"),
	cancelImportButton: document.querySelector("#cancelImportButton"),
	copyButton: document.querySelector("#copyButton"),
	testButton: document.querySelector("#testButton"),
	stopButton: document.querySelector("#stopButton"),
	statusLine: document.querySelector("#statusLine"),
	saveState: document.querySelector("#saveState"),
	// v1.0 additions
	exportPsButton: document.querySelector("#exportPsButton"),
	themeButton: document.querySelector("#themeButton"),
	previewButton: document.querySelector("#previewButton"),
	previewPane: document.querySelector("#previewPane"),
	previewFrame: document.querySelector("#previewFrame"),
	helpButton: document.querySelector("#helpButton"),
	helpModal: document.querySelector("#helpModal"),
	helpBody: document.querySelector("#helpBody"),
	helpCloseButton: document.querySelector("#helpCloseButton"),
};

window.configHost = {
	receive(message) {
		const pending = state.pending.get(message.id);
		if (!pending) return;
		state.pending.delete(message.id);
		if (message.ok) {
			pending.resolve(message.payload);
		} else {
			pending.reject(new Error(message.payload?.message || "Configurator request failed"));
		}
	},
};

const requestHost = (type, payload = {}) =>
	new Promise((resolve, reject) => {
		const id = String(++state.requestId);
		state.pending.set(id, { resolve, reject });
		window.chrome.webview.postMessage({ id, type, payload });
	});

const clone = (value) => JSON.parse(JSON.stringify(value));
const clamp01 = (value) => Math.max(0, Math.min(1, Number.isFinite(value) ? value : 0));
const toNumber = (value, fallback = 0) => {
	const number = Number.parseFloat(value);
	return Number.isFinite(number) ? number : fallback;
};

const colorToHex = (color) => {
	const channel = (value) =>
		Math.round(clamp01(Number(value)) * 255)
			.toString(16)
			.padStart(2, "0");
	return `#${channel(color?.r)}${channel(color?.g)}${channel(color?.b)}`;
};

const hexToColor = (hex) => {
	const normalized = String(hex || "#000000").replace("#", "");
	const value = normalized.length === 3
		? normalized.split("").map((c) => c + c).join("")
		: normalized.padEnd(6, "0").slice(0, 6);
	return {
		r: Number.parseInt(value.slice(0, 2), 16) / 255,
		g: Number.parseInt(value.slice(2, 4), 16) / 255,
		b: Number.parseInt(value.slice(4, 6), 16) / 255,
	};
};

const format01 = (value) => {
	const number = clamp01(Number(value));
	return Number.isInteger(number) ? String(number) : number.toFixed(3).replace(/0+$/, "").replace(/\.$/, "");
};

const normalizeColor = (color) => ({
	r: clamp01(Number(color?.r)),
	g: clamp01(Number(color?.g)),
	b: clamp01(Number(color?.b)),
});

// A clickable swatch that opens the shared popover picker. Replaces the native
// <input type="color"> (OS dialog, 8-bit only, no inline dragging).
const makeSwatchButton = (color, { disabled = false } = {}) => {
	const button = document.createElement("button");
	button.type = "button";
	button.className = "color-swatch";
	button.style.background = colorToHex(color);
	button.disabled = disabled;
	button.title = "Click to edit color";
	button.setAttribute("aria-label", "Edit color");
	return button;
};

const allFields = () => state.metadata.groups.flatMap((group) => group.fields);
const stripeEffects = new Set(["stripes", "customStripes", "pride", "trans", "transPride"]);

// Fields whose value changes which *other* fields are gated (see
// getDisabledReason). Editing one of these must re-render the whole group so
// the dependent fields pick up their new enabled/disabled state.
const gatingFields = new Set(["windowMode", "effect", "clickRipples"]);

const normalizeDraft = (draft) => ({
	...clone(state.defaultDraft),
	...(draft ? clone(draft) : {}),
});

const setStatus = (message, tone = "normal") => {
	el.statusLine.textContent = message || "";
	el.statusLine.style.color = tone === "error" ? "var(--danger)" : tone === "ok" ? "var(--green)" : "var(--muted)";
};

const setDirty = (dirty) => {
	state.dirty = dirty;
	el.saveState.textContent = dirty ? "Draft changed" : "Draft saved";
};

const scheduleSaveDraft = () => {
	window.clearTimeout(state.saveTimer);
	state.saveTimer = window.setTimeout(async () => {
		try {
			await requestHost("saveDraft", {
				draft: state.draft,
				selectedPresetId: state.selectedPresetId,
			});
			state.lastDraft = clone(state.draft);
			if (!state.dirty) {
				el.saveState.textContent = "Draft saved";
			}
		} catch (error) {
			setStatus(error.message, "error");
		}
	}, 250);
};

const scheduleCommandBuild = () => {
	window.clearTimeout(state.commandTimer);
	state.commandTimer = window.setTimeout(updateCommand, 80);
};

const updateCommand = async () => {
	try {
		const result = await requestHost("buildCommand", {
			draft: state.draft,
			includeDefaults: false,
			forTest: false,
		});
		el.commandOutput.value = result.command || "MatrixDesktop.exe";
	} catch (error) {
		setStatus(error.message, "error");
	}
};

const updateDraftValue = (field, value) => {
	state.draft[field.id] = value;
	setDirty(true);
	scheduleSaveDraft();
	scheduleCommandBuild();
	schedulePreviewPush();
};

// Live-preview debounce. Only refreshes the embedded iframe when the pane is
// visible and the matrix assets were found — otherwise it's a no-op (no host
// roundtrips). Per the chosen design, each settled change reloads the preview
// with the new query string (debounced), rather than patching it live.
const schedulePreviewPush = () => {
	if (!state.previewVisible || !state.previewAvailable) return;
	window.clearTimeout(state.previewTimer);
	state.previewTimer = window.setTimeout(async () => {
		try {
			const result = await requestHost("buildWebQuery", { draft: state.draft });
			const url = `${state.previewOrigin}index.html${result?.query || ""}`;
			if (el.previewFrame && url !== state.previewLastUrl) {
				state.previewLastUrl = url;
				el.previewFrame.src = url;
			}
		} catch (error) {
			setStatus(`Preview update failed: ${error.message}`, "error");
		}
	}, 250);
};

const setPreviewVisible = (visible) => {
	state.previewVisible = Boolean(visible) && state.previewAvailable;
	document.body.classList.toggle("preview-hidden", !state.previewVisible);
	if (el.previewButton) {
		el.previewButton.textContent = state.previewVisible ? "Hide preview" : "Show preview";
		el.previewButton.title = state.previewVisible ? "Hide the live preview pane" : "Show the live preview pane";
	}
	if (state.previewVisible) {
		state.previewLastUrl = ""; // force a refresh on next push
		schedulePreviewPush();
	}
};

const applyTheme = (theme) => {
	const next = theme === "light" ? "light" : "dark";
	state.uiTheme = next;
	document.documentElement.setAttribute("data-theme", next);
	if (el.themeButton) {
		el.themeButton.textContent = next === "light" ? "☀️" : "🌙";
		el.themeButton.title = next === "light"
			? "Switch to dark theme"
			: "Switch to light theme";
	}
};

const renderPresetSelect = () => {
	el.presetSelect.replaceChildren();
	const draftOption = new Option("Last draft", "");
	el.presetSelect.add(draftOption);
	for (const preset of state.presets) {
		el.presetSelect.add(new Option(preset.name, preset.id));
	}
	el.presetSelect.value = state.selectedPresetId || "";
};

// Left-rail jump list: one button per group that scrolls its section into
// view. Replaces the old tab behavior (which showed one group at a time).
const renderNav = () => {
	el.sectionJump.replaceChildren();
	for (const group of state.metadata.groups) {
		const button = document.createElement("button");
		button.type = "button";
		button.dataset.group = group.id;
		button.textContent = group.title;
		button.className = group.id === state.activeGroupId ? "active" : "";
		button.addEventListener("click", () => {
			state.activeGroupId = group.id;
			const section = document.getElementById(`section-${group.id}`);
			if (section) section.scrollIntoView({ behavior: "smooth", block: "start" });
			highlightNav();
		});
		el.sectionJump.append(button);
	}
};

const highlightNav = () => {
	for (const button of el.sectionJump.querySelectorAll("button")) {
		button.classList.toggle("active", button.dataset.group === state.activeGroupId);
	}
};

// Highlight the section nearest the top of the scroll surface as the user
// scrolls, so the jump list acts as a position indicator too.
const updateActiveOnScroll = () => {
	const sections = [...el.fieldSurface.querySelectorAll(".field-section")];
	if (sections.length === 0) return;
	const surfaceTop = el.fieldSurface.getBoundingClientRect().top;
	let current = sections[0].id.replace("section-", "");
	for (const section of sections) {
		if (section.getBoundingClientRect().top - surfaceTop <= 12) {
			current = section.id.replace("section-", "");
		}
	}
	if (current && current !== state.activeGroupId) {
		state.activeGroupId = current;
		highlightNav();
	}
};

// Render every group as a section (sticky header + field grid) into one
// scrollable surface. A non-empty filter keeps only fields whose label (or
// group title) matches, and drops emptied sections. Scroll position is
// preserved so a gating-triggered re-render doesn't jump the view.
const renderFields = () => {
	const surface = el.fieldSurface;
	const prevScroll = surface.scrollTop;
	surface.replaceChildren();

	const filter = state.filter.trim().toLowerCase();
	for (const group of state.metadata.groups) {
		const matching = filter
			? group.fields.filter((field) =>
				field.label.toLowerCase().includes(filter) || group.title.toLowerCase().includes(filter))
			: group.fields;
		if (matching.length === 0) continue;

		const section = document.createElement("section");
		section.className = "field-section";
		section.id = `section-${group.id}`;

		const header = document.createElement("h2");
		header.className = "section-title";
		header.textContent = group.title;
		section.append(header);

		const grid = document.createElement("div");
		grid.className = "field-grid";
		for (const field of matching) {
			grid.append(renderField(field));
		}
		section.append(grid);
		surface.append(section);
	}

	if (surface.childElementCount === 0) {
		const empty = document.createElement("div");
		empty.className = "empty-hint";
		empty.textContent = `No settings match “${state.filter}”.`;
		surface.append(empty);
	}

	surface.scrollTop = prevScroll;
};

const renderField = (field) => {
	switch (field.kind) {
		case "bool":
			return renderBoolField(field);
		case "select":
			return renderSelectField(field);
		case "number":
			return renderNumberField(field);
		case "color":
			return renderColorField(field);
		case "palette":
			return renderPaletteField(field);
		case "stripes":
			return renderStripesField(field);
		default:
			return renderTextField(field);
	}
};

const getDisabledReason = (field) => {
	const windowMode = state.draft.windowMode || "borderless";
	const effect = state.draft.effect || "palette";
	if (field.id === "monitor" && windowMode !== "single-monitor") {
		return "Only used when Window mode is Single monitor.";
	}
	if (field.id === "workingArea" && windowMode === "windowed") {
		return "Only applies to borderless modes.";
	}
	if (field.id === "stripeColors" && !stripeEffects.has(effect)) {
		return "Only used by stripe-based effects: stripes, custom stripes, pride, trans, and trans pride.";
	}
	if (field.id === "url" && effect !== "image") {
		return "Only used when Effect is Image.";
	}
	if (field.id === "camera" && effect !== "mirror") {
		return "Only used by the Mirror effect (webcam input).";
	}
	if (field.id === "clickRippleShape" && !state.draft.clickRipples) {
		return "Only used when Click ripples is enabled.";
	}
	return "";
};

const createFieldShell = (field, extraClass = "") => {
	const shell = document.createElement("div");
	const disabledReason = getDisabledReason(field);
	shell.className = `field ${extraClass}${disabledReason ? " disabled-field" : ""}`.trim();
	const label = document.createElement("label");
	label.textContent = field.label;
	shell.append(label);
	const helpText = disabledReason || field.help;
	if (helpText) {
		const help = document.createElement("div");
		help.className = "field-help";
		help.textContent = helpText;
		shell.append(help);
	}
	return shell;
};

const renderBoolField = (field) => {
	const shell = createFieldShell(field, "checkbox-field");
	const input = document.createElement("input");
	input.type = "checkbox";
	input.disabled = Boolean(getDisabledReason(field));
	input.checked = Boolean(state.draft[field.id]);
	input.addEventListener("change", () => {
		updateDraftValue(field, input.checked);
		if (gatingFields.has(field.id)) {
			renderFields();
		}
	});
	shell.append(input);
	return shell;
};

const renderSelectField = (field) => {
	const shell = createFieldShell(field);
	const select = document.createElement("select");
	for (const option of field.options || []) {
		select.add(new Option(option.label, option.value));
	}
	select.value = state.draft[field.id] ?? field.defaultValue ?? "";
	select.disabled = Boolean(getDisabledReason(field));
	select.addEventListener("change", () => {
		updateDraftValue(field, select.value);
		if (gatingFields.has(field.id)) {
			renderFields();
		}
	});
	shell.append(select);
	return shell;
};

const renderNumberField = (field) => {
	const shell = createFieldShell(field, "number-field");
	const input = document.createElement("input");
	input.type = "number";
	if (field.min != null) input.min = field.min;
	if (field.max != null) input.max = field.max;
	if (field.step != null) input.step = field.step;
	input.disabled = Boolean(getDisabledReason(field));
	input.value = state.draft[field.id] ?? "";

	// v1.0 validation feedback — re-evaluate on every input.
	let hint = null;
	const applyValidation = () => {
		const raw = input.value;
		if (raw === "" || input.disabled) {
			input.classList.remove("invalid");
			input.removeAttribute("aria-invalid");
			if (hint) { hint.remove(); hint = null; }
			return;
		}
		const num = Number.parseFloat(raw);
		const tooLow = field.min != null && Number.isFinite(num) && num < field.min;
		const tooHigh = field.max != null && Number.isFinite(num) && num > field.max;
		const notNumber = !Number.isFinite(num);
		if (tooLow || tooHigh || notNumber) {
			input.classList.add("invalid");
			input.setAttribute("aria-invalid", "true");
			if (!hint) {
				hint = document.createElement("span");
				hint.className = "validation-hint";
				shell.append(hint);
			}
			const rangeText =
				field.min != null && field.max != null ? `Allowed range: ${field.min} – ${field.max}`
				: field.min != null ? `Minimum: ${field.min}`
				: field.max != null ? `Maximum: ${field.max}`
				: "Must be a number";
			hint.textContent = rangeText;
		} else {
			input.classList.remove("invalid");
			input.removeAttribute("aria-invalid");
			if (hint) { hint.remove(); hint = null; }
		}
	};

	input.addEventListener("input", () => {
		const value = input.value === "" ? null : toNumber(input.value, field.defaultValue ?? 0);
		updateDraftValue(field, value);
		applyValidation();
	});

	// B5: enforce the field's range on blur rather than only flagging it red.
	// Empty reverts to the field default; out-of-range clamps to min/max. This
	// runs on blur (not on each keystroke) so it never fights mid-typing.
	input.addEventListener("blur", () => {
		if (input.disabled) {
			applyValidation();
			return;
		}
		if (input.value === "") {
			const fallback = field.defaultValue;
			if (fallback == null) {
				updateDraftValue(field, null);
			} else {
				input.value = fallback;
				updateDraftValue(field, toNumber(fallback, 0));
			}
		} else {
			let num = toNumber(input.value, field.defaultValue ?? 0);
			if (field.min != null && num < field.min) num = field.min;
			if (field.max != null && num > field.max) num = field.max;
			if (String(num) !== input.value) {
				input.value = num;
				updateDraftValue(field, num);
			}
		}
		applyValidation();
	});
	applyValidation();

	shell.append(input);
	return shell;
};

const renderTextField = (field) => {
	const shell = createFieldShell(field);
	const input = document.createElement("input");
	input.type = "text";
	input.disabled = Boolean(getDisabledReason(field));
	input.value = state.draft[field.id] ?? "";
	input.addEventListener("input", () => updateDraftValue(field, input.value));
	shell.append(input);
	return shell;
};

const renderColorField = (field) => {
	const shell = createFieldShell(field);
	const editor = document.createElement("div");
	editor.className = "color-editor";
	let color = normalizeColor(state.draft[field.id]);

	const swatch = makeSwatchButton(color);
	const numbers = document.createElement("div");
	numbers.className = "rgb-grid";
	const inputs = {};
	for (const channel of ["r", "g", "b"]) {
		const input = document.createElement("input");
		input.type = "number";
		input.min = "0";
		input.max = "1";
		input.step = "0.01";
		input.value = format01(color[channel]);
		inputs[channel] = input;
		numbers.append(input);
	}

	// In-place updates only — never renderFields() here, so focus is retained
	// while typing (the old code rebuilt the whole grid on every keystroke).
	const commit = (next, { syncInputs = true } = {}) => {
		color = normalizeColor(next);
		swatch.style.background = colorToHex(color);
		if (syncInputs) {
			inputs.r.value = format01(color.r);
			inputs.g.value = format01(color.g);
			inputs.b.value = format01(color.b);
		}
		updateDraftValue(field, color);
	};

	for (const channel of ["r", "g", "b"]) {
		inputs[channel].addEventListener("input", () => {
			commit({ ...color, [channel]: clamp01(toNumber(inputs[channel].value, color[channel])) }, { syncInputs: false });
		});
	}
	swatch.addEventListener("click", () => openColorPicker(swatch, color, (next) => commit(next)));

	editor.append(swatch, numbers);
	shell.append(editor);
	return shell;
};

const renderPaletteField = (field) => {
	const shell = createFieldShell(field);
	const editor = document.createElement("div");
	editor.className = "list-editor";
	const stops = Array.isArray(state.draft[field.id]) ? clone(state.draft[field.id]) : [];
	stops.forEach((stop, index) => editor.append(renderPaletteRow(field, stops, stop, index)));
	const add = document.createElement("button");
	add.type = "button";
	add.className = "add-row";
	add.textContent = "Add stop";
	add.addEventListener("click", () => {
		stops.push({ r: 0, g: 1, b: 0.45, at: stops.length ? 1 : 0 });
		updateDraftValue(field, stops);
		renderFields();
	});
	editor.append(add);
	shell.append(editor);
	return shell;
};

const renderPaletteRow = (field, stops, stop, index) => {
	const row = document.createElement("div");
	row.className = "list-row";
	let value = { ...normalizeColor(stop), at: clamp01(Number(stop.at)) };

	const swatch = makeSwatchButton(value);
	const hex = document.createElement("input");
	hex.type = "text";
	hex.className = "hex-input";
	hex.spellcheck = false;
	hex.maxLength = 7;
	hex.value = colorToHex(value);
	const at = document.createElement("input");
	at.type = "number";
	at.min = "0";
	at.max = "1";
	at.step = "0.01";
	at.value = format01(value.at);
	const up = miniButton("↑");
	const down = miniButton("↓");
	const remove = miniButton("×");

	// Color/hex/at edits update in place (no renderFields → no focus loss).
	const persist = () => {
		stops[index] = { ...value };
		updateDraftValue(field, stops);
	};
	// Reorder/remove are discrete clicks, so a group re-render is fine there.
	const rebuild = () => {
		updateDraftValue(field, stops);
		renderFields();
	};

	swatch.addEventListener("click", () => openColorPicker(swatch, value, (next) => {
		value = { ...value, ...normalizeColor(next) };
		swatch.style.background = colorToHex(value);
		hex.value = colorToHex(value);
		persist();
	}));
	hex.addEventListener("input", () => {
		value = { ...value, ...hexToColor(hex.value) };
		swatch.style.background = colorToHex(value);
		persist();
	});
	at.addEventListener("input", () => {
		value = { ...value, at: clamp01(toNumber(at.value, value.at)) };
		persist();
	});
	up.disabled = index === 0;
	up.addEventListener("click", () => {
		[stops[index - 1], stops[index]] = [stops[index], stops[index - 1]];
		rebuild();
	});
	down.disabled = index === stops.length - 1;
	down.addEventListener("click", () => {
		[stops[index + 1], stops[index]] = [stops[index], stops[index + 1]];
		rebuild();
	});
	remove.addEventListener("click", () => {
		stops.splice(index, 1);
		rebuild();
	});
	row.append(swatch, hex, at, up, down, remove);
	return row;
};

const renderStripesField = (field) => {
	const shell = createFieldShell(field);
	const editor = document.createElement("div");
	editor.className = "list-editor";
	const disabled = Boolean(getDisabledReason(field));
	const colors = Array.isArray(state.draft[field.id]) ? clone(state.draft[field.id]) : [];
	colors.forEach((color, index) => editor.append(renderStripeRow(field, colors, color, index, disabled)));
	const add = document.createElement("button");
	add.type = "button";
	add.className = "add-row";
	add.textContent = "Add color";
	add.disabled = disabled;
	add.addEventListener("click", () => {
		colors.push({ r: 0, g: 1, b: 0.45 });
		updateDraftValue(field, colors);
		renderFields();
	});
	editor.append(add);
	shell.append(editor);
	return shell;
};

const renderStripeRow = (field, colors, color, index, disabled) => {
	const row = document.createElement("div");
	row.className = "list-row stripe-row";
	let value = normalizeColor(color);

	const swatch = makeSwatchButton(value, { disabled });
	const hex = document.createElement("input");
	hex.type = "text";
	hex.className = "hex-input";
	hex.spellcheck = false;
	hex.maxLength = 7;
	hex.value = colorToHex(value);
	hex.disabled = disabled;
	const up = miniButton("↑");
	const down = miniButton("↓");
	const remove = miniButton("×");

	const persist = () => {
		colors[index] = { ...value };
		updateDraftValue(field, colors);
	};
	const rebuild = () => {
		updateDraftValue(field, colors);
		renderFields();
	};

	swatch.addEventListener("click", () => {
		if (disabled) return;
		openColorPicker(swatch, value, (next) => {
			value = normalizeColor(next);
			swatch.style.background = colorToHex(value);
			hex.value = colorToHex(value);
			persist();
		});
	});
	hex.addEventListener("input", () => {
		value = normalizeColor(hexToColor(hex.value));
		swatch.style.background = colorToHex(value);
		persist();
	});
	up.disabled = disabled || index === 0;
	up.addEventListener("click", () => {
		[colors[index - 1], colors[index]] = [colors[index], colors[index - 1]];
		rebuild();
	});
	down.disabled = disabled || index === colors.length - 1;
	down.addEventListener("click", () => {
		[colors[index + 1], colors[index]] = [colors[index], colors[index + 1]];
		rebuild();
	});
	remove.disabled = disabled;
	remove.addEventListener("click", () => {
		colors.splice(index, 1);
		rebuild();
	});
	row.append(swatch, hex, up, down, remove);
	return row;
};

const miniButton = (text) => {
	const button = document.createElement("button");
	button.type = "button";
	button.textContent = text;
	return button;
};

const refreshAll = () => {
	renderPresetSelect();
	renderNav();
	renderFields();
	scheduleCommandBuild();
	// Wholesale draft changes (preset select, New, Import, Randomize) all route
	// through here; push them to the live preview too — previously only
	// per-field edits (updateDraftValue) refreshed it.
	schedulePreviewPush();
};

const saveCurrentPreset = async (forceName = false) => {
	const existing = state.presets.find((preset) => preset.id === state.selectedPresetId);
	let name = existing?.name;
	if (!name || forceName) {
		name = window.prompt("Preset name", name || "New preset");
		if (!name) return;
	}
	try {
		const result = await requestHost("savePreset", {
			preset: {
				id: forceName ? "" : existing?.id,
				name,
				values: state.draft,
			},
		});
		state.presets = result.state.userPresets || [];
		state.selectedPresetId = result.state.selectedPresetId || result.preset?.id || null;
		setDirty(false);
		refreshAll();
		setStatus("Preset saved.", "ok");
	} catch (error) {
		setStatus(error.message, "error");
	}
};

const renamePreset = async () => {
	const existing = state.presets.find((preset) => preset.id === state.selectedPresetId);
	if (!existing) {
		setStatus("Select a saved preset to rename.", "error");
		return;
	}
	const name = window.prompt("Preset name", existing.name);
	if (!name) return;
	await savePresetWithName(existing.id, name, state.draft);
};

const savePresetWithName = async (id, name, values) => {
	try {
		const result = await requestHost("savePreset", {
			preset: { id, name, values },
		});
		state.presets = result.state.userPresets || [];
		state.selectedPresetId = result.state.selectedPresetId || id;
		setDirty(false);
		refreshAll();
		setStatus("Preset saved.", "ok");
	} catch (error) {
		setStatus(error.message, "error");
	}
};

const deletePreset = async () => {
	const existing = state.presets.find((preset) => preset.id === state.selectedPresetId);
	if (!existing) {
		setStatus("Select a saved preset to delete.", "error");
		return;
	}
	if (!window.confirm(`Delete "${existing.name}"?`)) return;
	try {
		const result = await requestHost("deletePreset", { id: existing.id });
		state.presets = result.state.userPresets || [];
		state.selectedPresetId = null;
		renderPresetSelect();
		setStatus("Preset deleted.", "ok");
	} catch (error) {
		setStatus(error.message, "error");
	}
};

const showImportPanel = (show) => {
	el.importPanel.hidden = !show;
	if (show) {
		el.importInput.value = el.commandOutput.value === "MatrixDesktop.exe" ? "" : el.commandOutput.value;
		window.setTimeout(() => el.importInput.focus(), 0);
	}
};

const importCommand = async () => {
	const command = el.importInput.value.trim();
	if (!command) {
		setStatus("Paste a MatrixDesktop command or argument line first.", "error");
		return;
	}

	try {
		const result = await requestHost("importCommand", { command });
		state.selectedPresetId = null;
		state.draft = normalizeDraft(result.draft);
		state.lastDraft = clone(state.draft);
		state.presets = result.state?.userPresets || state.presets;
		showImportPanel(false);
		setDirty(true);
		refreshAll();
		const applied = result.applied?.length || 0;
		const ignored = result.ignored?.length || 0;
		const suffix = ignored ? ` Ignored ${ignored} unknown setting${ignored === 1 ? "" : "s"}.` : "";
		setStatus(`Imported ${applied} setting${applied === 1 ? "" : "s"}.${suffix}`, ignored ? "normal" : "ok");
	} catch (error) {
		setStatus(error.message, "error");
	}
};

const randomizeDraft = async () => {
	try {
		const scope = el.randomizeScope.value || "visual";
		const result = await requestHost("randomizeDraft", {
			draft: state.draft,
			scope,
		});
		state.draft = normalizeDraft(result.draft);
		setDirty(true);
		scheduleSaveDraft();
		refreshAll();
		const label = el.randomizeScope.selectedOptions[0]?.textContent || "Visual preset";
		setStatus(`${label} randomized.`, "ok");
	} catch (error) {
		setStatus(error.message, "error");
	}
};

const bindEvents = () => {
	el.fieldFilter.addEventListener("input", () => {
		state.filter = el.fieldFilter.value;
		renderFields();
	});

	let scrollRaf = 0;
	el.fieldSurface.addEventListener("scroll", () => {
		if (scrollRaf) return;
		scrollRaf = window.requestAnimationFrame(() => {
			scrollRaf = 0;
			updateActiveOnScroll();
		});
	});

	el.presetSelect.addEventListener("change", () => {
		const id = el.presetSelect.value || null;
		const preset = state.presets.find((item) => item.id === id);
		state.selectedPresetId = id;
		state.draft = normalizeDraft(preset?.values || state.lastDraft);
		setDirty(false);
		scheduleSaveDraft();
		refreshAll();
	});

	el.newPresetButton.addEventListener("click", () => {
		state.selectedPresetId = null;
		state.draft = normalizeDraft(state.defaultDraft);
		setDirty(true);
		scheduleSaveDraft();
		refreshAll();
		setStatus("New draft ready.");
	});
	el.savePresetButton.addEventListener("click", () => saveCurrentPreset(false));
	el.saveAsPresetButton.addEventListener("click", () => saveCurrentPreset(true));
	el.renamePresetButton.addEventListener("click", renamePreset);
	el.deletePresetButton.addEventListener("click", deletePreset);
	el.randomizeButton.addEventListener("click", randomizeDraft);
	el.importButton.addEventListener("click", () => showImportPanel(el.importPanel.hidden));
	el.applyImportButton.addEventListener("click", importCommand);
	el.cancelImportButton.addEventListener("click", () => showImportPanel(false));

	el.copyButton.addEventListener("click", async () => {
		try {
			await requestHost("copyCommand", { command: el.commandOutput.value });
			setStatus("Command copied.", "ok");
		} catch (error) {
			setStatus(error.message, "error");
		}
	});

	el.testButton.addEventListener("click", async () => {
		try {
			const result = await requestHost("testCommand", { draft: state.draft });
			state.testRunning = true;
			el.stopButton.disabled = false;
			setStatus(`Launched test process ${result.processId}.`, "ok");
		} catch (error) {
			setStatus(error.message, "error");
		}
	});

	el.stopButton.addEventListener("click", async () => {
		try {
			await requestHost("stopTest");
			state.testRunning = false;
			el.stopButton.disabled = true;
			setStatus("Test process stopped.", "ok");
		} catch (error) {
			setStatus(error.message, "error");
		}
	});

	// ─── v1.0 button bindings ────────────────────────────────────────

	if (el.exportPsButton) {
		el.exportPsButton.addEventListener("click", async () => {
			try {
				await requestHost("exportPowerShell", { draft: state.draft });
				setStatus("PowerShell script copied to clipboard.", "ok");
			} catch (error) {
				setStatus(`PowerShell export failed: ${error.message}`, "error");
			}
		});
	}

	if (el.themeButton) {
		el.themeButton.addEventListener("click", async () => {
			const next = state.uiTheme === "light" ? "dark" : "light";
			applyTheme(next);
			try {
				await requestHost("setTheme", { theme: next });
			} catch (error) {
				setStatus(`Theme save failed: ${error.message}`, "error");
			}
		});
	}

	if (el.previewButton) {
		el.previewButton.addEventListener("click", () => {
			if (!state.previewAvailable) {
				setStatus("Live preview unavailable: matrix web assets not found next to the configurator.", "error");
				return;
			}
			setPreviewVisible(!state.previewVisible);
			setStatus(state.previewVisible ? "Preview shown." : "Preview hidden.", "ok");
		});
	}

	if (el.helpButton && el.helpModal && el.helpBody && el.helpCloseButton) {
		const closeHelp = () => { el.helpModal.hidden = true; };
		el.helpButton.addEventListener("click", async () => {
			try {
				const result = await requestHost("loadHelp");
				el.helpBody.textContent = result?.text ?? "Argument guide unavailable.";
				el.helpModal.hidden = false;
			} catch (error) {
				setStatus(`Help failed: ${error.message}`, "error");
			}
		});
		el.helpCloseButton.addEventListener("click", closeHelp);
		el.helpModal.addEventListener("click", (event) => {
			if (event.target === el.helpModal) closeHelp();
		});
		document.addEventListener("keydown", (event) => {
			if (event.key === "Escape" && !el.helpModal.hidden) closeHelp();
		});
	}
};

const init = async () => {
	try {
		const payload = await requestHost("loadState");
		state.metadata = payload.metadata;
		state.defaultDraft = payload.defaultDraft;
		state.presets = payload.state?.userPresets || [];
		state.selectedPresetId = payload.state?.selectedPresetId || null;
		state.lastDraft = normalizeDraft(payload.state?.lastDraft);
		state.draft = normalizeDraft(state.lastDraft);
		state.activeGroupId = state.metadata.groups[0]?.id || null;
		el.storageStatus.textContent = payload.storage?.portable ? "Portable preset storage" : "AppData preset storage";

		// v1.0: apply the saved theme before the first render so the user
		// never sees a flash of the wrong palette during cold start.
		applyTheme(payload.state?.uiTheme || "dark");

		// Embedded preview availability comes from the host (it locates the
		// matrix web/ assets and exposes a virtual-host origin for the iframe).
		const preview = payload.preview || {};
		state.previewAvailable = Boolean(preview.available && preview.origin);
		state.previewOrigin = preview.origin || null;
		if (!state.previewAvailable && el.previewButton) {
			el.previewButton.disabled = true;
			el.previewButton.title = "Matrix web assets not found next to the configurator";
		}

		bindEvents();
		setDirty(false);
		refreshAll();
		// Show the preview by default when it's available.
		setPreviewVisible(state.previewAvailable);
		setStatus("Ready.");
	} catch (error) {
		setStatus(error.message, "error");
	}
};

init();
