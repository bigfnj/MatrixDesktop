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
	// dirty and testRunning used to live here. Both were written and never read once:
	// the save indicator is driven by setSaveState's argument, and the Stop Test button's
	// own disabled property is the single source of truth for whether a test is running.
	// Keeping a second copy of state that nothing consults is how the two get to disagree.
	requestId: 0,
	pending: new Map(),
	saveTimer: 0,
	commandTimer: 0,
	// v1.0 additions
	uiTheme: "dark",
	previewTimer: 0,
	previewRequestVersion: 0,
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
	importFileButton: document.querySelector("#importFileButton"),
	screensaverButton: document.querySelector("#screensaverButton"),
	restoreScreensaverButton: document.querySelector("#restoreScreensaverButton"),
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
const defaultValueFor = (field) => state.draft.scene === "uc3d" && state.draft.uc3dControls === "legacy" && field.id === "bloomStrength"
	? field.defaultValue : state.metadata.sceneDefaults?.[state.draft.scene]?.[field.id] ?? field.defaultValue;
const editedSceneFields = new Set();
const rememberDraftEdits = (explicitKeys = []) => {
	editedSceneFields.clear();
	const defaults = { ...state.defaultDraft, ...state.metadata.sceneDefaults?.[state.draft.scene] };
	for (const key of Object.keys(state.draft))
		if (key !== "scene" && JSON.stringify(state.draft[key]) !== JSON.stringify(defaults[key])) editedSceneFields.add(key);
	for (const key of explicitKeys) if (key !== "scene") editedSceneFields.add(key);
};
const stripeEffects = new Set(["stripes", "customStripes", "pride", "trans", "transPride"]);

// Fields whose value changes which *other* fields are gated (see
// getDisabledReason). Editing one of these must re-render the whole group so
// the dependent fields pick up their new enabled/disabled state.
const gatingFields = new Set(["windowMode", "effect", "clickRipples", "scene", "uc3dControls", "volumetric", "forwardMovement", "randomForwardMovement", "showPictures", "showClock", "sound", "zmBackgroundMode", "zmMonotonousCleanupEnabled", "zmRandomizedCleanupEnabled"]);

const normalizeDraft = (draft) => {
	const saved = draft ? clone(draft) : {};
	// Preserve old full drafts before the new scene defaults can claim their controls.
	if (saved.scene === "uc3d" && !["original", "legacy"].includes(saved.uc3dControls))
		saved.uc3dControls = Object.keys(saved).some(key => nativeUc3dFields.has(key)) ? "original" : "legacy";
	return { ...clone(state.defaultDraft), ...clone(state.metadata?.sceneDefaults?.[saved.scene] || {}), ...saved };
};

const setStatus = (message, tone = "normal") => {
	el.statusLine.textContent = message || "";
	el.statusLine.style.color = tone === "error" ? "var(--danger)" : tone === "ok" ? "var(--green)" : "var(--muted)";
};

const setSaveState = (text) => {
	el.saveState.textContent = text;
};

const setDirty = (dirty) => {
	setSaveState(dirty ? "Draft changed" : "Draft saved");
};

const scheduleSaveDraft = () => {
	window.clearTimeout(state.saveTimer);
	setSaveState("Saving…");
	state.saveTimer = window.setTimeout(async () => {
		try {
			await requestHost("saveDraft", {
				draft: state.draft,
				selectedPresetId: state.selectedPresetId,
			});
			state.lastDraft = clone(state.draft);
			// Unconditional. The old guard was `if (!state.dirty)`, which could never be
			// true here because updateDraftValue sets dirty before scheduling the save.
			setSaveState("Draft saved");
		} catch (error) {
			setSaveState("Save failed");
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
		el.commandOutput.value = "";
		setStatus(error.message, "error");
	}
};

const updateDraftValue = (field, value) => {
	// A picker opened before a scene change can finish after its field is disabled.
	if (getDisabledReason(field)) return;
	if (field.id === "scene" && value !== state.draft.scene) {
		const previous = { ...state.defaultDraft, ...state.metadata.sceneDefaults?.[state.draft.scene] };
		const next = { ...state.defaultDraft, ...state.metadata.sceneDefaults?.[value] };
		for (const key of Object.keys(next)) {
			if (key !== "scene" && !editedSceneFields.has(key) && JSON.stringify(state.draft[key]) === JSON.stringify(previous[key]))
				state.draft[key] = clone(next[key]);
		}
	}
	if (field.id !== "scene") editedSceneFields.add(field.id);
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
	const version = ++state.previewRequestVersion;
	window.clearTimeout(state.previewTimer);
	if (!state.previewVisible || !state.previewAvailable) return;
	state.previewTimer = window.setTimeout(async () => {
		try {
			const result = await requestHost("buildWebQuery", { draft: state.draft });
			if (version !== state.previewRequestVersion || !state.previewVisible) return;
			const url = `${state.previewOrigin}index.html${result?.query || ""}`;
			if (el.previewFrame && url !== state.previewLastUrl) {
				state.previewLastUrl = url;
				el.previewFrame.src = url;
			}
		} catch (error) {
			if (version !== state.previewRequestVersion || !state.previewVisible) return;
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
	} else {
		++state.previewRequestVersion;
		window.clearTimeout(state.previewTimer);
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
		if (group.id === "launch" && el.screensaverButton && el.restoreScreensaverButton) {
			const actions = document.createElement("div"); actions.className = "screensaver-actions";
			actions.append(el.screensaverButton, el.restoreScreensaverButton); section.append(actions);
		}
		if (group.id === "zmatrix" && state.draft.scene === "zmatrix" && state.draft.zmImportNotes?.length) {
			const report = document.createElement("details");
			report.className = "import-report";
			const summary = document.createElement("summary");
			summary.textContent = "Imported settings: " + state.draft.zmImportNotes.length + " migration notes";
			const list = document.createElement("ul");
			for (const note of state.draft.zmImportNotes) {
				const item = document.createElement("li"); item.textContent = String(note); list.append(item);
			}
			report.append(summary, list);
			if (state.draft.zmImportedSettings) {
				const source = document.createElement("details");
				const title = document.createElement("summary"); title.textContent = "Retained source values";
				const values = document.createElement("dl");
				for (const [key, value] of Object.entries(state.draft.zmImportedSettings)) {
					const term = document.createElement("dt"); term.textContent = key;
					const description = document.createElement("dd"); description.textContent = String(value);
					values.append(term, description);
				}
				source.append(title, values); report.append(source);
			}
			section.append(report);
		}
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
	const renderers = { bool: renderBoolField, select: renderSelectField, number: renderNumberField,
		color: renderColorField, rgb8: renderRgb8Field, font: renderFontField, characters: renderCharacterField,
		phrases: renderPhraseField, palette: renderPaletteField, stripes: renderStripesField };
	const shell = (renderers[field.kind] || renderTextField)(field);
	// Compound editors contain several native controls. Disable every descendant,
	// including swatches and palette actions, rather than only styling the shell.
	if (getDisabledReason(field)) {
		for (const control of shell.querySelectorAll("input, select, button, textarea")) control.disabled = true;
	}
	return shell;
};

const nativeUc3dFields = new Set(["uc3dAmount","uc3dGlyphSize","uc3dFallSpeed","uc3dForwardSpeed","uc3dPictureScale","uc3dPictureDelay","uc3dClockSize"]);
const legacyUc3dFields = new Set(["numColumns","density","fallSpeed","forwardSpeed","raindropLength","pictureScale","pictureDelay","clockSize"]);
const uc3dFields = new Set(["uc3dControls",...nativeUc3dFields,"codeColor","donorGlyphs","shadedLines","sidewaysMovement","mouseRotation","codeRotation","forwardMovement","randomForwardMovement","showPictures","pictureDepth","pictureMonochrome","pictureCaptions","pictureScale","pictureDelay","pictureDuration","pictureSource","showClock","clock12Hour","clockSize","sound","soundVolume","pictureDirectory","donorDirectory","desktopBackground","musicFile"]);
const legacyOnlyFields = new Set(["effect","url","camera","clickRipples","clickRippleShape","glintColor","palette","stripeColors","ditherMagnitude","slant","loops","skipIntro","testFix"]);

const getDisabledReason = (field) => {
    const zmatrix = state.draft.scene === "zmatrix";
    if (field.id.startsWith("zm") && !zmatrix) return "Choose the ZMatrix scene in Theme to use this setting.";
    if (zmatrix && field.scope === "web" && !field.id.startsWith("zm") && !["scene", "renderer", "resolution", "fps", "once", "suppressWarnings"].includes(field.id))
        return "Used by Matrix or UC3D. ZMatrix has independent text, stream and color controls.";
    if (zmatrix && field.id === "zmBackTrace" && !state.draft.zmMonotonousCleanupEnabled) return "Enable Monotonous cleanup to use its fixed glyph distance.";
    if (zmatrix && ["zmLeading", "zmSpacePad"].includes(field.id) && !state.draft.zmRandomizedCleanupEnabled) return "Enable Randomized cleanup to use these glyph distances.";
    if (zmatrix && field.id === "zmBlendMode" && state.draft.zmBackgroundMode !== "bitmap") return "Choose Desktop wallpaper background to use bitwise blending.";
    const uc3d = state.draft.scene === "uc3d";
    if (uc3dFields.has(field.id) && !uc3d) return "Choose the UC3D scene in Theme to use this setting.";
    const original = state.draft.uc3dControls !== "legacy";
    if (uc3d && original && legacyUc3dFields.has(field.id)) return "Use the original screensaver sliders in UC3D, or choose Legacy MatrixDesktop controls.";
    if (uc3d && !original && nativeUc3dFields.has(field.id)) return "Choose Original screensaver controls to use this slider.";
    if (uc3d && original && ["cycleSpeed", "isometric"].includes(field.id)) return "Used by Legacy MatrixDesktop controls. Original controls use the screensaver's glyph timing and perspective.";
    if (uc3d && original && field.id === "sidewaysMovement" && !state.draft.volumetric) return "Original sideways movement is used by 3D code. Enable Volumetric / 3D to use it.";
    if (uc3d && legacyOnlyFields.has(field.id)) return "Used by the Matrix scene. UC3D has its own code colors and picture controls.";
    if (uc3d && ["mouseRotation","codeRotation","forwardMovement","randomForwardMovement","pictureDepth"].includes(field.id) && !state.draft.volumetric)
        return "Enable Volumetric / 3D to use this setting.";
    if (uc3d && field.id === "shadedLines" && state.draft.volumetric) return "Used by 2D code.";
    if (uc3d && field.id === "randomForwardMovement" && !state.draft.forwardMovement) return "Enable forward/back movement first.";
    if (uc3d && field.id === "uc3dForwardSpeed" && (!state.draft.volumetric || !state.draft.forwardMovement || state.draft.randomForwardMovement))
        return "Enable 3D forward/back movement and turn off random movement to set constant travel.";
    if (uc3d && ["pictureDepth","pictureMonochrome","pictureCaptions","pictureScale","pictureDelay","uc3dPictureScale","uc3dPictureDelay","pictureDuration","pictureSource","pictureDirectory"].includes(field.id) && !state.draft.showPictures)
        return "Enable Form pictures from code first.";
    if (uc3d && ["clock12Hour","clockSize","uc3dClockSize"].includes(field.id) && !state.draft.showClock) return "Enable Show clock first.";
    if (uc3d && field.id === "soundVolume" && state.draft.sound === "off") return "Choose a sound mode first.";
    if (uc3d && field.id === "musicFile" && state.draft.sound !== "music") return "Choose Ambient music / local file first.";
	const windowMode = state.draft.windowMode || "borderless";
	if (field.id.startsWith("wallpaper") && windowMode !== "wallpaper") return "Choose Live desktop wallpaper in Window mode first.";
	if (windowMode === "wallpaper" && ["topmost", "hideCursor", "exitOnAnyKey", "exitOnEsc", "globalKeyExit", "workingArea"].includes(field.id))
		return "Wallpaper stays behind desktop icons and uses its tray menu for controls.";
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
	shell.dataset.field = field.id;
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
	select.value = state.draft[field.id] ?? defaultValueFor(field) ?? "";
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

const renderNumberField = (definition) => {
	const field = definition.id === "animationSpeed" && state.draft.scene === "uc3d" && state.draft.uc3dControls !== "legacy"
		? { ...definition, min: 0 } : definition;
	const shell = createFieldShell(field, "number-field");
	const input = document.createElement("input");
	input.type = "number";
	input.setAttribute("aria-label", `${field.label} value`);
	if (field.min != null) input.min = field.min;
	if (field.max != null) input.max = field.max;
	if (field.step != null) input.step = field.step;
	input.disabled = Boolean(getDisabledReason(field));
	input.value = state.draft[field.id] ?? "";
	let range = null;
	const syncRange = () => { if (range && input.value !== "") range.value = input.value; };
	if (field.slider) {
		range = document.createElement("input");
		range.type = "range";
		range.min = field.min;
		range.max = field.max;
		range.step = field.step;
		range.value = input.value;
		range.disabled = input.disabled;
		range.setAttribute("aria-label", `${field.label} slider`);
		range.addEventListener("input", () => {
			if (getDisabledReason(field)) return;
			input.value = range.value;
			updateDraftValue(field, Number(range.value));
			applyValidation();
		});
	}

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
		const value = input.value === "" ? null : toNumber(input.value, defaultValueFor(field) ?? 0);
		updateDraftValue(field, value);
		syncRange();
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
			const fallback = defaultValueFor(field);
			if (fallback == null) {
				updateDraftValue(field, null);
			} else {
				input.value = fallback;
				updateDraftValue(field, toNumber(fallback, 0));
			}
		} else {
			let num = toNumber(input.value, defaultValueFor(field) ?? 0);
			if (field.min != null && num < field.min) num = field.min;
			if (field.max != null && num > field.max) num = field.max;
			if (field.slider) num = Math.trunc(num);
			if (String(num) !== input.value) {
				input.value = num;
				updateDraftValue(field, num);
			}
		}
		syncRange();
		applyValidation();
	});
	applyValidation();

	if (range) shell.append(range);
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
    if (["pictureDirectory", "donorDirectory", "musicFile"].includes(field.id)) {
        const browse = document.createElement("button");
        browse.type = "button";
        browse.textContent = "Browse...";
        browse.disabled = input.disabled;
        browse.addEventListener("click", async () => {
            try {
                const result = await requestHost("browseMedia", { field: field.id });
                if (result?.path) {
                    input.value = result.path;
                    updateDraftValue(field, result.path);
                }
            } catch (error) { setStatus(error.message, "error"); }
        });
        shell.append(browse);
    }
	return shell;
};

const invalidBmpText = value => /[\x00-\x1f\x7f\ud800-\udfff]/.test(value);
const renderCharacterField = (field) => {
	const shell = createFieldShell(field, "wide-field");
	const input = document.createElement("textarea");
	input.rows = 2;
	input.setAttribute("aria-label", field.label);
	input.value = (state.draft[field.id] ?? field.defaultValue ?? []).join("");
	const hint = document.createElement("div"); hint.className = "field-help";
	const validate = () => {
		const values = [...input.value];
		const valid = values.length > 0 && values.length <= 512 && !invalidBmpText(input.value);
		input.classList.toggle("invalid-input", !valid);
		input.setCustomValidity(valid ? "" : "Use 1 to 512 BMP characters without control characters.");
		hint.textContent = valid ? new Set(values).size + " distinct characters" : input.validationMessage;
		return valid;
	};
	input.addEventListener("input", () => {
		if (getDisabledReason(field)) return;
		if (validate()) updateDraftValue(field, [...new Set([...input.value])]);
	});
	input.addEventListener("blur", () => { if (validate()) input.value = [...new Set([...input.value])].join(""); });
	validate(); shell.append(input, hint); return shell;
};

const renderPhraseField = (field) => {
	const shell = createFieldShell(field, "wide-field");
	const editor = document.createElement("div"); editor.className = "phrase-editor";
	let phrases = [...(state.draft[field.id] ?? field.defaultValue ?? [])];
	const hint = document.createElement("div"); hint.className = "field-help";
	const save = () => {
		const valid = phrases.length <= 64 && phrases.every(value => value.length > 0 && value.length <= 256 && !invalidBmpText(value))
			&& new Set(phrases.join("")).size <= 1024;
		hint.textContent = valid ? phrases.length + " phrase entries" : "Use up to 64 nonempty phrases, 256 BMP characters each and 1024 distinct glyphs.";
		hint.classList.toggle("validation-error", !valid);
		if (valid) updateDraftValue(field, [...new Set(phrases)]);
		return valid;
	};
	const draw = () => {
		editor.replaceChildren();
		phrases.forEach((phrase, index) => {
			const row = document.createElement("div"); row.className = "phrase-row";
			const input = document.createElement("input"); input.type = "text"; input.value = phrase; input.maxLength = 256;
			input.setAttribute("aria-label", "Phrase " + (index + 1));
			input.addEventListener("input", () => {
				if (getDisabledReason(field)) return;
				phrases[index] = input.value;
				input.classList.toggle("invalid-input", !save());
			});
			const remove = document.createElement("button"); remove.type = "button"; remove.textContent = "Remove";
			remove.addEventListener("click", () => { if (!getDisabledReason(field)) { phrases.splice(index, 1); save(); draw(); } });
			row.append(input, remove); editor.append(row);
		});
		const add = document.createElement("button"); add.type = "button"; add.textContent = "Add phrase";
		add.disabled = phrases.length >= 64;
		add.addEventListener("click", () => { if (!getDisabledReason(field) && phrases.length < 64) { phrases.push("New phrase"); save(); draw(); } });
		editor.append(add);
		if (getDisabledReason(field)) for (const control of editor.querySelectorAll("input, button")) control.disabled = true;
	};
	hint.textContent = phrases.length + " phrase entries"; draw(); shell.append(editor, hint); return shell;
};

const renderFontField = (field) => {
	const shell = createFieldShell(field, "wide-field");
	const editor = document.createElement("details"); editor.className = "font-editor";
	const summary = document.createElement("summary");
	let font = clone(state.draft[field.id] ?? field.defaultValue);
	const updateSummary = () => { summary.textContent = font.name + ", height " + font.height + (font.italic ? ", italic" : ""); };
	updateSummary(); editor.append(summary);
	const grid = document.createElement("div"); grid.className = "font-grid";
	const ordered = [...(state.metadata.fontFields || [])].sort((a, b) => Number(b.id === "name") - Number(a.id === "name"));
	for (const option of ordered) {
		const label = document.createElement("label"); label.textContent = option.label;
		const input = document.createElement("input"); input.dataset.fontField = option.id;
		input.setAttribute("aria-label", field.label + ": " + option.label);
		input.type = option.kind === "bool" ? "checkbox" : option.kind === "number" ? "number" : "text";
		if (input.type === "checkbox") {
			input.checked = Boolean(font[option.id]);
			input.addEventListener("change", () => {
				if (getDisabledReason(field)) return;
				font[option.id] = input.checked; updateDraftValue(field, clone(font)); updateSummary();
			});
		} else {
			input.value = font[option.id] ?? option.defaultValue ?? "";
			if (input.type === "number") { input.min = String(option.min); input.max = String(option.max); input.step = "1"; }
			else {
				input.maxLength = 31;
				const list = document.createElement("datalist"); list.id = field.id + "-families";
				for (const name of state.fontNames || []) list.append(new Option(name, name));
				input.setAttribute("list", list.id); label.append(list);
			}
			input.addEventListener("input", () => {
				const valid = input.type === "number" ? input.value !== "" && Number.isFinite(Number(input.value))
					&& Number(input.value) >= option.min && Number(input.value) <= option.max
					: Boolean(input.value.trim()) && input.value.length <= 31 && !invalidBmpText(input.value);
				input.classList.toggle("invalid-input", !valid);
				input.setCustomValidity(valid ? "" : input.type === "number" ? "Allowed range: " + option.min + " to " + option.max : "Use a font family name of 1 to 31 characters.");
			});
			input.addEventListener("blur", () => {
				if (getDisabledReason(field)) return;
				let next;
				if (input.type === "number") {
					const numeric = input.value === "" ? Number(field.defaultValue[option.id]) : Number(input.value);
					next = Math.max(option.min, Math.min(option.max, Number.isFinite(numeric) ? Math.trunc(numeric) : Number(field.defaultValue[option.id])));
					if (option.id === "family") next &= 0xf0;
				} else next = input.value.trim() && !invalidBmpText(input.value) ? input.value.trim().slice(0, 31) : field.defaultValue[option.id];
				font[option.id] = next; input.value = String(next); input.classList.remove("invalid-input"); input.setCustomValidity("");
				updateDraftValue(field, clone(font)); updateSummary();
			});
		}
		label.append(input); grid.append(label);
	}
	editor.append(grid); shell.append(editor); return shell;
};

const renderRgb8Field = (field) => {
	const shell = createFieldShell(field);
	const editor = document.createElement("div"); editor.className = "color-editor";
	const initial = state.draft[field.id] ?? field.defaultValue;
	let color = { r: Number(initial.r), g: Number(initial.g), b: Number(initial.b) };
	const normalized = () => ({ r: color.r / 255, g: color.g / 255, b: color.b / 255 });
	const swatch = makeSwatchButton(normalized());
	const numbers = document.createElement("div"); numbers.className = "rgb-grid";
	const inputs = {};
	const commit = next => {
		if (getDisabledReason(field)) return;
		color = Object.fromEntries(["r", "g", "b"].map(channel => [channel, Math.max(0, Math.min(255, Math.round(next[channel])))]));
		for (const channel of ["r", "g", "b"]) inputs[channel].value = String(color[channel]);
		swatch.style.background = colorToHex(normalized()); updateDraftValue(field, clone(color));
	};
	for (const channel of ["r", "g", "b"]) {
		const input = document.createElement("input"); input.type = "number"; input.min = "0"; input.max = "255"; input.step = "1";
		input.dataset.channel = channel; input.setAttribute("aria-label", field.label + " " + channel.toUpperCase()); input.value = String(color[channel]);
		input.addEventListener("input", () => {
			const n = Number(input.value);
			const valid = input.value !== "" && Number.isFinite(n) && n >= 0 && n <= 255;
			input.classList.toggle("invalid-input", !valid);
			if (valid) commit({ ...color, [channel]: n });
		});
		input.addEventListener("blur", () => {
			const n = input.value === "" ? Number(field.defaultValue[channel]) : Number(input.value);
			commit({ ...color, [channel]: Number.isFinite(n) ? n : Number(field.defaultValue[channel]) });
			input.classList.remove("invalid-input");
		});
		inputs[channel] = input; numbers.append(input);
	}
	swatch.addEventListener("click", () => {
		if (!getDisabledReason(field)) openColorPicker(swatch, normalized(), next => commit({ r: next.r * 255, g: next.g * 255, b: next.b * 255 }));
	});
	editor.append(swatch, numbers); shell.append(editor); return shell;
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
		// MD-16: every added stop used to land at exactly 1.0, so adding two produced two
		// stops at the same position and a degenerate gradient. Place the new one between
		// the last stop and the end instead, which is both non-colliding and where someone
		// clicking "Add stop" would expect it.
		const last = stops.length ? clamp01(Number(stops[stops.length - 1].at)) : -1;
		const at = stops.length === 0 ? 0 : clamp01((last + 1) / 2);
		stops.push({ r: 0, g: 1, b: 0.45, at });
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
		setStatus("Paste a MatrixDesktop command, arguments or ZMatrix .cfg first.", "error");
		return;
	}

	try {
		const result = await requestHost("importCommand", { command, draft: state.draft });
		state.selectedPresetId = null;
		state.draft = normalizeDraft(result.draft);
		rememberDraftEdits(result.applied || []);
		state.lastDraft = clone(state.draft);
		state.presets = result.state?.userPresets || state.presets;
		showImportPanel(false);
		setDirty(true);
		refreshAll();
		const applied = result.applied?.length || 0;
		const ignored = result.ignored?.length || 0;
		const suffix = ignored ? " Retained " + ignored + " unknown, unused or invalid settings for review." : "";
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
		rememberDraftEdits();
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
		rememberDraftEdits();
		setDirty(false);
		scheduleSaveDraft();
		refreshAll();
	});

	el.newPresetButton.addEventListener("click", () => {
		state.selectedPresetId = null;
		state.draft = normalizeDraft(state.defaultDraft);
		rememberDraftEdits();
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
	el.importFileButton?.addEventListener("click", async () => {
		try {
			const result = await requestHost("browseConfig");
			if (result?.text != null) { el.importInput.value = result.text; showImportPanel(true); }
		} catch (error) { setStatus(error.message, "error"); }
	});
	el.screensaverButton?.addEventListener("click", async () => {
		try {
			const result = await requestHost("useScreensaver", { draft: state.draft });
			setStatus(result?.message || "Screensaver profile saved.", "ok");
		} catch (error) { setStatus(error.message, "error"); }
	});
	el.restoreScreensaverButton?.addEventListener("click", async () => {
		try {
			const result = await requestHost("restoreScreensaver");
			setStatus(result?.message || "Previous screensaver restored.", "ok");
		} catch (error) { setStatus(error.message, "error"); }
	});
	el.cancelImportButton.addEventListener("click", () => showImportPanel(false));

	el.copyButton.addEventListener("click", async () => {
		try {
			// The host now reports whether the clipboard write actually succeeded, rather
			// than always answering copied = true.
			const result = await requestHost("copyCommand", { command: el.commandOutput.value });
			if (result?.copied) {
				setStatus("Command copied.", "ok");
			} else {
				setStatus(result?.message || "Could not copy the command.", "error");
			}
		} catch (error) {
			setStatus(error.message, "error");
		}
	});

	el.testButton.addEventListener("click", async () => {
		try {
			const result = await requestHost("testCommand", { draft: state.draft });
			el.stopButton.disabled = false;
			setStatus(`Launched test process ${result.processId}.`, "ok");
		} catch (error) {
			setStatus(error.message, "error");
		}
	});

	el.stopButton.addEventListener("click", async () => {
		try {
			await requestHost("stopTest");
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
				const result = await requestHost("exportPowerShell", { draft: state.draft });
				if (result?.copied) {
					setStatus("PowerShell script copied to clipboard.", "ok");
				} else {
					setStatus("Script generated, but the clipboard is in use by another program.", "error");
				}
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
		state.fontNames = payload.fonts || [];
		state.defaultDraft = payload.defaultDraft;
		state.presets = payload.state?.userPresets || [];
		state.selectedPresetId = payload.state?.selectedPresetId || null;
		state.lastDraft = normalizeDraft(payload.state?.lastDraft);
		state.draft = normalizeDraft(state.lastDraft);
		rememberDraftEdits();
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
