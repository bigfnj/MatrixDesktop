import { clamp01, closeColorPicker, colorToHex, format01, openColorPicker, parseHex } from "./colorPicker.js";

const state = {
	metadata: null,
	defaultDraft: {},
	// The unlinked draft that 'Last draft' brings back. While no preset is selected it is the
	// editor draft (syncScratchDraft); choosing a preset leaves it as it was.
	scratchDraft: {},
	draft: {},
	presets: [],
	selectedPresetId: null,
	activeGroupId: null,
	filter: "",
	// dirty and testRunning used to live here. Both were written and never read once:
	// the save indicator is driven by setSaveState's argument, and the Stop Test button's
	// own disabled property says whether a test is running. A launch enables it and keeps
	// the pid on the button; Stop, or the host's configHost.testExited(pid) for that same
	// pid when the test window closes on its own, disables it again.
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
	appShell: document.querySelector(".app-shell"),
	storageStatus: document.querySelector("#storageStatus"),
	presetSelect: document.querySelector("#presetSelect"),
	newPresetButton: document.querySelector("#newPresetButton"),
	savePresetButton: document.querySelector("#savePresetButton"),
	saveAsPresetButton: document.querySelector("#saveAsPresetButton"),
	renamePresetButton: document.querySelector("#renamePresetButton"),
	deletePresetButton: document.querySelector("#deletePresetButton"),
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
	exportPanel: document.querySelector("#exportPanel"),
	exportScript: document.querySelector("#exportScript"),
	closeExportButton: document.querySelector("#closeExportButton"),
	themeButton: document.querySelector("#themeButton"),
	previewButton: document.querySelector("#previewButton"),
	previewFrame: document.querySelector("#previewFrame"),
	helpButton: document.querySelector("#helpButton"),
	helpModal: document.querySelector("#helpModal"),
	helpBody: document.querySelector("#helpBody"),
	helpCloseButton: document.querySelector("#helpCloseButton"),
};

window.configHost = {
    snapshotDraft() {
        if (!state.metadata) return null;
        window.clearTimeout(state.saveTimer);
        return { draft: clone(state.draft), selectedPresetId: state.selectedPresetId, scratchDraft: clone(syncScratchDraft()) };
    },
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
	// The host reports a test window that exited on its own. Only the last launched pid
	// counts, so a late notice for an earlier or already stopped test changes nothing.
	testExited(pid) {
		if (!el.stopButton.dataset.pid || el.stopButton.dataset.pid !== String(pid)) return;
		delete el.stopButton.dataset.pid;
		el.stopButton.disabled = true;
		setStatus("Test window closed.");
	},
};

const requestHost = (type, payload = {}) =>
	new Promise((resolve, reject) => {
		const id = String(++state.requestId);
		state.pending.set(id, { resolve, reject });
		window.chrome.webview.postMessage({ id, type, payload });
	});

const clone = (value) => JSON.parse(JSON.stringify(value));
const toNumber = (value, fallback = 0) => {
	const number = Number.parseFloat(value);
	return Number.isFinite(number) ? number : fallback;
};

const normalizeColor = (color) => ({
	r: clamp01(Number(color?.r)),
	g: clamp01(Number(color?.g)),
	b: clamp01(Number(color?.b)),
});

// A clickable swatch that opens the shared popover picker. Replaces the native
// <input type="color"> (OS dialog, 8-bit only, no inline dragging).
// label names the setting for assistive technology ("Edit Highlight code color"); without it
// every swatch on the page announced the same "Edit color".
const makeSwatchButton = (color, { disabled = false, label = "color" } = {}) => {
	const button = document.createElement("button");
	button.type = "button";
	button.className = "color-swatch";
	button.style.background = colorToHex(color);
	button.disabled = disabled;
	button.title = "Click to edit color";
	button.setAttribute("aria-label", `Edit ${label}`);
	return button;
};

// AUD-13: what the renderer draws for each field a command leaves out, for this draft's
// scene, version and effect, in config.js's order: an effect's implied values over the scene
// profile over the version over the catalog. The page's copy of ConfiguratorMetadata
// EffectiveDefault (ArgumentCatalog.cs), which CommandBuilder uses to leave defaults out.
// ZMatrix emits neither a version nor an effect; legacy UC3D draws the catalog bloom.
const effectiveDefaultsFor = (draft) => {
	const metadata = state.metadata || {};
	const scene = draft?.scene || "matrix";
	const version = draft?.version || "classic";
	const effect = draft?.effect || "palette";
	const zmatrix = scene === "zmatrix";
	const defaults = {
		...state.defaultDraft,
		...(zmatrix ? {} : metadata.versionDefaults?.[metadata.versionAliases?.[version] ?? version]),
		...metadata.sceneDefaults?.[scene],
		...(zmatrix || effect === "palette" ? {} : metadata.effectDefaults?.[scene === "uc3d" ? "uc3d" : "matrix"]?.[effect]),
	};
	if (scene === "uc3d" && draft?.uc3dControls === "legacy") defaults.bloomStrength = state.defaultDraft.bloomStrength;
	return defaults;
};
const defaultValueFor = (field) => effectiveDefaultsFor(state.draft)[field.id] ?? field.defaultValue;
// Equal values whatever their objects' key order. A store reopened after a close holds colours as
// {b, g, r} (R2-44), which a JSON string comparison counted as an edit of the default (R2-45).
const sameValue = (a, b) => {
	if (a === b) return true;
	if (!a || !b || typeof a !== "object" || typeof b !== "object" || Array.isArray(a) !== Array.isArray(b)) return false;
	const keys = Object.keys(a);
	return keys.length === Object.keys(b).length && keys.every((key) => Object.hasOwn(b, key) && sameValue(a[key], b[key]));
};
const editedSceneFields = new Set();
const rememberDraftEdits = (explicitKeys = []) => {
	editedSceneFields.clear();
	const defaults = effectiveDefaultsFor(state.draft);
	for (const key of Object.keys(state.draft))
		if (key !== "scene" && !sameValue(state.draft[key], defaults[key])) editedSceneFields.add(key);
	for (const key of explicitKeys) if (key !== "scene") editedSceneFields.add(key);
};
const stripeEffects = new Set(["stripes", "customStripes", "pride", "trans", "transPride"]);
// The choices effectiveDefaultsFor reads; switching one re-targets untouched fields. uc3dControls
// is one: legacy UC3D draws the catalog bloom (R2-15).
const defaultContextFields = new Set(["scene", "version", "effect", "uc3dControls"]);

// Fields whose value changes which *other* fields are gated (see
// getDisabledReason). Editing one of these must re-render the whole group so
// the dependent fields pick up their new enabled/disabled state. Version gates
// nothing but moves untouched editors to its own values (defaultContextFields).
const gatingFields = new Set(["windowMode", "effect", "version", "clickRipples", "scene", "uc3dControls", "volumetric", "forwardMovement", "randomForwardMovement", "showPictures", "showClock", "sound", "zmBackgroundMode", "zmMonotonousCleanupEnabled", "zmRandomizedCleanupEnabled"]);

const normalizeDraft = (draft) => {
	const saved = draft ? clone(draft) : {};
	// Preserve old full drafts before the new scene defaults can claim their controls.
	if (saved.scene === "uc3d" && !["original", "legacy"].includes(saved.uc3dControls))
		saved.uc3dControls = Object.keys(saved).some(key => nativeUc3dFields.has(key)) ? "original" : "legacy";
	return { ...clone(effectiveDefaultsFor({ ...state.defaultDraft, ...state.metadata?.sceneDefaults?.[saved.scene], ...saved })), ...saved };
};

// detail is the status line's tooltip, for text too long to show whole; any other status clears it.
const setStatus = (message, tone = "normal", detail = "") => {
	el.statusLine.textContent = message || "";
	el.statusLine.title = detail;
	el.statusLine.style.color = tone === "error" ? "var(--danger)" : tone === "ok" ? "var(--green)" : "var(--muted)";
};

const setSaveState = (text) => {
	el.saveState.textContent = text;
};

const setDirty = (dirty) => {
	setSaveState(dirty ? "Draft changed" : "Draft saved");
};

// Everything that reads the scratch draft, or leaves the unlinked state, calls this first, so
// an edit still inside the save debounce is kept.
const syncScratchDraft = () => {
	if (!state.selectedPresetId) state.scratchDraft = clone(state.draft);
	return state.scratchDraft;
};

// requestHost posts before its first await, so a call orders the save at once.
const saveDraftNow = async () => {
	try {
		await requestHost("saveDraft", {
			draft: state.draft,
			selectedPresetId: state.selectedPresetId,
			scratchDraft: syncScratchDraft(),
		});
		// Unconditional. The old guard was `if (!state.dirty)`, which could never be
		// true here because updateDraftValue sets dirty before scheduling the save.
		setSaveState("Draft saved");
	} catch (error) {
		setSaveState("Save failed");
		setStatus(error.message, "error");
	}
};

const scheduleSaveDraft = () => {
	window.clearTimeout(state.saveTimer);
	setSaveState("Saving…");
	state.saveTimer = window.setTimeout(() => {
		state.saveTimer = 0;
		saveDraftNow();
	}, 250);
};

// confirm() and prompt() block the page, so a save armed just before one comes due only after
// the preset request that follows the dialog, and stores the selection from before it. The
// preset actions post a pending save first instead.
const flushPendingDraftSave = () => {
	if (!state.saveTimer) return;
	window.clearTimeout(state.saveTimer);
	state.saveTimer = 0;
	saveDraftNow();
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
	// A scene, version, effect or controls switch moves each untouched field from what the old choice
	// draws to what the new one draws (AUD-13), so the editors keep showing the render.
	if (defaultContextFields.has(field.id) && value !== state.draft[field.id]) {
		const previous = effectiveDefaultsFor(state.draft);
		const next = effectiveDefaultsFor({ ...state.draft, [field.id]: value });
		for (const key of Object.keys(next)) {
			if (!defaultContextFields.has(key) && !editedSceneFields.has(key) && sameValue(state.draft[key], previous[key]))
				state.draft[key] = clone(next[key]);
		}
	}
	if (field.id !== "scene") editedSceneFields.add(field.id);
	// Editors retain their own arrays while a picker is open. Saved state must
	// own a separate copy so a late disabled callback cannot mutate it in place.
	state.draft[field.id] = value && typeof value === "object" ? clone(value) : value;
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
		// Without web assets the button stays disabled, and its title is the only place the reason shows.
		el.previewButton.title = !state.previewAvailable ? "Matrix web assets not found next to the configurator"
			: state.previewVisible ? "Hide the live preview pane" : "Show the live preview pane";
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
			if (section) {
				section.scrollIntoView({ behavior: "smooth", block: "start" });
				// Focus moves with the view, so the next Tab continues inside the section.
				section.querySelector(".section-title")?.focus({ preventScroll: true });
			}
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

// The catalog's sections with the selected scene's placements applied
// (ArgumentCatalog.cs ScenePlacements). A placement moves or renames one shared
// field while that scene is selected; the copy keeps id, kind and scope, so every
// editor, gate and saved value still addresses the same key. Each placement is
// all-or-nothing: one naming a missing field, group or anchor (or anchoring a field
// to itself) is reported and leaves that field exactly as the catalog has it, so the
// page always offers one editor per key. Catalog arrays are copied, never edited.
const sceneSections = (scene) => {
	const sections = state.metadata.groups.map((group) => ({ group, fields: [...group.fields] }));
	for (const placement of state.metadata.scenePlacements?.[scene] ?? []) {
		const home = sections.find((section) => section.fields.some((field) => field.id === placement.field));
		const target = placement.group ? sections.find((section) => section.group.id === placement.group) : home;
		const anchor = placement.group
			? target?.fields.find((candidate) => candidate.id === placement.after && candidate.id !== placement.field)
			: null;
		if (!home || !target || (placement.group && !anchor)) {
			console.error(`Scene placement for "${scene}" cannot place "${placement.field}" in "${placement.group}" after "${placement.after}".`);
			continue;
		}
		const index = home.fields.findIndex((field) => field.id === placement.field);
		const field = home.fields[index];
		const shown = {
			...field,
			label: placement.label,
			help: placement.help ?? field.help,
			catalogLabel: field.catalogLabel ?? field.label,
			catalogGroupTitle: field.catalogGroupTitle ?? home.group.title,
		};
		if (!placement.group) {
			home.fields[index] = shown;
			continue;
		}
		home.fields.splice(index, 1);
		target.fields.splice(target.fields.indexOf(anchor) + 1, 0, shown);
	}
	return sections;
};

// Keyboard focus across a rebuild. A removed control takes focus with it to <body>, so
// every gating change and list edit used to send the next Tab back to the first field.
// The focused control is recorded by position (its field, its list row and its index in
// that row, or among the field's controls outside rows) and the same position is focused
// in the rebuilt field. A removed last row lands on the new last row; an emptied list, or
// a control that is now disabled, falls back to the field's first enabled control.
const focusableSelector = "input, select, textarea, button, summary";
const focusRowSelector = ".list-row, .phrase-row";
const focusControls = (scope, inRow) =>
	[...scope.querySelectorAll(focusableSelector)].filter((node) => inRow || !node.closest(focusRowSelector));
const captureFocus = (root) => {
	const node = document.activeElement;
	if (!node || node === document.body || !root.contains(node)) return null;
	const shell = node.closest("[data-field]");
	const row = node.closest(focusRowSelector);
	return {
		node,
		field: shell?.dataset.field ?? null,
		row: row && shell ? [...shell.querySelectorAll(focusRowSelector)].indexOf(row) : -1,
		index: (row ? focusControls(row, true) : focusControls(shell ?? root, false)).indexOf(node),
	};
};
const restoreFocus = (shell, focus) => {
	if (!focus) return;
	// Moved rather than rebuilt (the screensaver actions): the same node, refocused.
	if (focus.node.isConnected) {
		focus.node.focus({ preventScroll: true });
		return;
	}
	if (!shell) return;
	const rows = shell.querySelectorAll(focusRowSelector);
	const row = focus.row >= 0 && rows.length ? rows[Math.min(focus.row, rows.length - 1)] : null;
	const inRow = row ? focusControls(row, true) : [];
	const outside = focusControls(shell, false);
	const preferred = row ? inRow[Math.min(focus.index, inRow.length - 1)] : focus.row < 0 ? outside[focus.index] : null;
	for (const candidate of [preferred, ...inRow, ...outside]) {
		if (!candidate || candidate.disabled) continue;
		candidate.focus({ preventScroll: true });
		if (document.activeElement === candidate) return;
	}
};

// R2-14: Chromium keeps a select's type-ahead text on the element, and a gating select's change
// re-renders the surface. The rebuilt select took each keystroke afresh, so "mo" in Version
// picked Mega City, then Operator. The focused select is held beside the surface with moveBefore,
// which keeps its focus and that text (removing and re-inserting it loses both), and then takes
// its rebuilt copy's place with that copy's value and disabled state. Without moveBefore
// (Chromium before 133) it is rebuilt like the rest.
const holdFocusedSelect = (focus) => {
	const surface = el.fieldSurface;
	if (!(focus?.node instanceof HTMLSelectElement) || !focus.field || typeof surface.moveBefore !== "function") return null;
	surface.parentNode.moveBefore(focus.node, surface);
	return focus.node;
};
const placeHeldSelect = (held, focus) => {
	if (!held) return;
	const fresh = el.fieldSurface.querySelector(`[data-field="${CSS.escape(focus.field)}"] > select`);
	if (!fresh) {
		held.remove();
		return;
	}
	fresh.parentNode.moveBefore(held, fresh);
	held.disabled = fresh.disabled;
	if (held.value !== fresh.value) held.value = fresh.value;
	fresh.remove();
};

// Render every group as a section (sticky header + field grid) into one
// scrollable surface. A non-empty filter keeps only fields whose visible label,
// catalog label or either section title matches, and drops emptied sections, so a
// field moved for the scene is still found by its catalog name. Scroll position is
// preserved so a gating-triggered re-render doesn't jump the view.
const renderFields = () => {
	// The popover is bound to the row that opened it. A rebuild without a pointerdown
	// (keyboard activation, a gating select, refreshAll) used to leave it writing through
	// that detached row's closure: a removed stop came back, or a reorder lost a neighbour.
	closeColorPicker();
	const surface = el.fieldSurface;
	const prevScroll = surface.scrollTop;
	const focus = captureFocus(surface);
	const held = holdFocusedSelect(focus);
	surface.replaceChildren();

	const filter = state.filter.trim().toLowerCase();
	for (const { group, fields } of sceneSections(state.draft.scene)) {
		const matching = filter
			? fields.filter((field) => [field.label, field.catalogLabel, group.title, field.catalogGroupTitle]
				.some((text) => typeof text === "string" && text.toLowerCase().includes(filter)))
			: fields;
		// The screensaver actions belong to the Launch section and are also found by their own text.
		const actions = group.id === "launch" && el.screensaverButton && el.restoreScreensaverButton
			? [el.screensaverButton, el.restoreScreensaverButton] : [];
		const showActions = actions.length > 0
			&& (matching.length > 0 || actions.some((button) => button.textContent.toLowerCase().includes(filter)));
		if (matching.length === 0 && !showActions) continue;

		const section = document.createElement("section");
		section.className = "field-section";
		section.id = `section-${group.id}`;

		const header = document.createElement("h2");
		header.className = "section-title";
		header.tabIndex = -1; // the jump list focuses it
		header.textContent = group.title;
		section.append(header);

		const grid = document.createElement("div");
		grid.className = "field-grid";
		for (const field of matching) {
			grid.append(renderField(field));
		}
		if (matching.length > 0) section.append(grid);
		if (showActions) {
			const row = document.createElement("div"); row.className = "screensaver-actions";
			row.append(...actions); section.append(row);
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

	placeHeldSelect(held, focus);
	restoreFocus(focus?.field ? surface.querySelector(`[data-field="${CSS.escape(focus.field)}"]`) : null, focus);
	surface.scrollTop = prevScroll;
};

const renderField = (field) => {
	const renderers = { bool: renderBoolField, select: renderSelectField, number: renderNumberField,
		color: renderColorField, rgb8: renderRgb8Field, font: renderFontField, characters: renderCharacterField,
		phrases: renderPhraseField, palette: renderPaletteField, stripes: renderStripesField };
	const shell = (renderers[field.kind] || renderTextField)(field);
	// A compound editor holds several controls under one visible label, so the label names
	// the editor as a group; single-control renderers bind it to their control (bindLabel).
	const label = shell.querySelector(":scope > label");
	if (label && !label.htmlFor) {
		label.id = `label-${field.id}`;
		shell.setAttribute("role", "group");
		shell.setAttribute("aria-labelledby", label.id);
	}
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

// The visible label names a single-control field for assistive technology and operates it:
// a click toggles the checkbox or focuses the control.
const bindLabel = (shell, control, field) => {
	control.id = `field-${field.id}`;
	shell.querySelector(":scope > label").htmlFor = control.id;
};

const renderBoolField = (field) => {
	const shell = createFieldShell(field, "checkbox-field");
	const input = document.createElement("input");
	input.type = "checkbox";
	bindLabel(shell, input, field);
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
	bindLabel(shell, select, field);
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
	bindLabel(shell, input, field);
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
	bindLabel(shell, input, field);
	input.disabled = Boolean(getDisabledReason(field));
	input.value = state.draft[field.id] ?? "";
	input.addEventListener("input", () => updateDraftValue(field, input.value));
	shell.append(input);
    if (["pictureDirectory", "donorDirectory", "musicFile"].includes(field.id)) {
        const browse = document.createElement("button");
        browse.type = "button";
        browse.textContent = "Browse...";
        browse.setAttribute("aria-label", `Browse for ${field.label}`);
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
	bindLabel(shell, input, field);
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
	const validPhrase = value => value.length > 0 && value.length <= 256 && !invalidBmpText(value);
	// The draft keeps each phrase once, so the count is of distinct phrases and a row repeating an
	// earlier one is marked: the editor used to show and count rows the save had dropped.
	const countText = () => {
		const distinct = new Set(phrases).size, repeated = phrases.length - distinct;
		return distinct + " phrase entries" + (repeated ? "; " + repeated + (repeated === 1 ? " duplicate" : " duplicates") + " ignored" : "");
	};
	// Only where the rows match the list: after typing and at the end of every redraw. Add and
	// Remove save before they redraw, while the old rows are still on screen.
	const markRows = () => editor.querySelectorAll(".phrase-row input").forEach((input, index) => {
		const first = phrases.indexOf(phrases[index]);
		input.setCustomValidity(first < index ? "Repeats phrase " + (first + 1) + ", so it is saved once." : "");
		input.classList.toggle("invalid-input", first < index || !validPhrase(phrases[index]));
	});
	const save = () => {
		const valid = phrases.length <= 64 && phrases.every(validPhrase) && new Set(phrases.join("")).size <= 1024;
		hint.textContent = valid ? countText() : "Use up to 64 nonempty phrases, 256 BMP characters each and 1024 distinct glyphs.";
		hint.classList.toggle("validation-error", !valid);
		if (valid) updateDraftValue(field, [...new Set(phrases)]);
		return valid;
	};
	const draw = () => {
		const focus = captureFocus(editor);
		editor.replaceChildren();
		phrases.forEach((phrase, index) => {
			const row = document.createElement("div"); row.className = "phrase-row";
			const input = document.createElement("input"); input.type = "text"; input.value = phrase; input.maxLength = 256;
			input.setAttribute("aria-label", "Phrase " + (index + 1));
			input.addEventListener("input", () => {
				if (getDisabledReason(field)) return;
				phrases[index] = input.value;
				const valid = save();
				markRows();
				if (!valid) input.classList.add("invalid-input");
			});
			const remove = document.createElement("button"); remove.type = "button"; remove.textContent = "Remove";
			remove.addEventListener("click", () => { if (!getDisabledReason(field)) { phrases.splice(index, 1); save(); draw(); } });
			row.append(input, remove); editor.append(row);
		});
		const add = document.createElement("button"); add.type = "button"; add.textContent = "Add phrase";
		add.disabled = phrases.length >= 64;
		add.addEventListener("click", () => {
			if (getDisabledReason(field) || phrases.length >= 64) return;
			let text = "New phrase";
			for (let n = 2; phrases.includes(text); n++) text = "New phrase " + n;
			phrases.push(text); save(); draw();
		});
		editor.append(add);
		if (getDisabledReason(field)) for (const control of editor.querySelectorAll("input, button")) control.disabled = true;
		markRows();
		restoreFocus(shell, focus);
	};
	hint.textContent = countText(); draw(); shell.append(editor, hint); return shell;
};

// One list of installed font families serves every font-name box. It is built once, outside
// the field surface: renderFields clears the surface on every filter keystroke, gating change
// and list edit, and used to rebuild two copies of the list each time.
const fontListId = "installed-font-families";
const renderFontList = (names) => {
	const list = document.createElement("datalist");
	list.id = fontListId;
	for (const name of names) list.append(new Option(name, name));
	document.body.append(list);
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
				input.setAttribute("list", fontListId);
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
	const swatch = makeSwatchButton(normalized(), { label: field.label });
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

	const swatch = makeSwatchButton(color, { label: field.label });
	const numbers = document.createElement("div");
	numbers.className = "rgb-grid";
	const inputs = {};
	for (const channel of ["r", "g", "b"]) {
		const input = document.createElement("input");
		input.type = "number";
		input.min = "0";
		input.max = "1";
		input.step = "0.01";
		input.setAttribute("aria-label", `${field.label} ${channel.toUpperCase()}`);
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

	const name = `${field.label} stop ${index + 1}`;
	const swatch = makeSwatchButton(value, { label: name });
	const hex = document.createElement("input");
	hex.type = "text";
	hex.className = "hex-input";
	hex.spellcheck = false;
	hex.maxLength = 7;
	hex.setAttribute("aria-label", `${name} hex`);
	hex.value = colorToHex(value);
	const at = document.createElement("input");
	at.type = "number";
	at.min = "0";
	at.max = "1";
	at.step = "0.01";
	at.setAttribute("aria-label", `${name} position`);
	at.value = format01(value.at);
	const up = miniButton("↑", `Move ${name} up`);
	const down = miniButton("↓", `Move ${name} down`);
	const remove = miniButton("×", `Remove ${name}`);

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
		hex.classList.remove("invalid-input");
		persist();
	}));
	// Text that is not a whole hex colour is marked and not saved: the lenient parser this
	// replaces stored a NaN channel for "red", which the draft saved as null.
	hex.addEventListener("input", () => {
		const parsed = parseHex(hex.value);
		hex.classList.toggle("invalid-input", !parsed);
		if (!parsed) return;
		value = { ...value, ...normalizeColor(parsed) };
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

	const name = `${field.label} ${index + 1}`;
	const swatch = makeSwatchButton(value, { disabled, label: name });
	const hex = document.createElement("input");
	hex.type = "text";
	hex.className = "hex-input";
	hex.spellcheck = false;
	hex.maxLength = 7;
	hex.setAttribute("aria-label", `${name} hex`);
	hex.value = colorToHex(value);
	hex.disabled = disabled;
	const up = miniButton("↑", `Move ${name} up`);
	const down = miniButton("↓", `Move ${name} down`);
	const remove = miniButton("×", `Remove ${name}`);

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
			hex.classList.remove("invalid-input");
			persist();
		});
	});
	hex.addEventListener("input", () => {
		const parsed = parseHex(hex.value);
		hex.classList.toggle("invalid-input", !parsed);
		if (!parsed) return;
		value = normalizeColor(parsed);
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

// The glyph is the visible face; label is what assistive technology announces.
const miniButton = (text, label) => {
	const button = document.createElement("button");
	button.type = "button";
	button.textContent = text;
	button.setAttribute("aria-label", label);
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
	flushPendingDraftSave();
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
	const name = window.prompt("Preset name", existing.name)?.trim();
	if (!name) return;
	try {
		// Only the name changes. Saving values is Save's job, so an unsaved experiment in
		// the editor is never committed to the preset by renaming it and the save indicator stays.
		const result = await requestHost("renamePreset", { id: existing.id, name });
		state.presets = result.state?.userPresets || state.presets;
		renderPresetSelect();
		setStatus("Preset renamed.", "ok");
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
	flushPendingDraftSave();
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
		state.presets = result.state?.userPresets || state.presets;
		showImportPanel(false);
		setDirty(true);
		refreshAll();
		// The host names every setting it did not apply. A ZMatrix .cfg import keeps an unknown or
		// unused key in zmImportedSettings, which the import report lists; anything else (every
		// command or query setting, or a .cfg line it could not read) is dropped, so it is named here.
		const plural = (count, noun) => `${count} ${noun}${count === 1 ? "" : "s"}`;
		const ignored = result.ignored || [];
		const kept = result.draft?.zmImportedSettings || {};
		const retained = ignored.filter((name) => Object.hasOwn(kept, name)).length;
		const dropped = ignored.filter((name) => !Object.hasOwn(kept, name));
		let message = `Imported ${plural(result.applied?.length || 0, "setting")}.`;
		if (retained) message += ` Retained ${plural(retained, "unknown, unused or invalid setting")} for review in the ZMatrix import report.`;
		if (dropped.length) {
			const more = dropped.length > 5 ? ` and ${dropped.length - 5} more` : "";
			message += ` Ignored ${plural(dropped.length, "unknown or invalid setting")}: ${dropped.slice(0, 5).join(", ")}${more}.`;
		}
		setStatus(message, ignored.length ? "normal" : "ok");
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
		syncScratchDraft();
		state.selectedPresetId = id;
		state.draft = normalizeDraft(preset?.values || state.scratchDraft);
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
			if (result?.text != null) { showImportPanel(true); el.importInput.value = result.text; }
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
			el.stopButton.dataset.pid = String(result.processId);
			el.stopButton.disabled = false;
			// The host describes what it launched: the generated command run windowed, or a note that
			// a configuration too long for one command line went over direct arguments. A long
			// description is shortened here and kept whole in the tooltip.
			const command = String(result.command ?? "");
			const shown = command.length > 160 ? command.slice(0, 159) + "…" : command;
			setStatus(`Launched test process ${result.processId}: ${shown}`, "ok", command);
		} catch (error) {
			setStatus(error.message, "error");
		}
	});

	el.stopButton.addEventListener("click", async () => {
		try {
			await requestHost("stopTest");
			delete el.stopButton.dataset.pid;
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
				// The host returns the script either way. When the clipboard is busy it is shown,
				// selected, for copying by hand; a later export that reaches the clipboard, or fails,
				// hides it, so the panel only ever holds the latest export.
				el.exportPanel.hidden = Boolean(result?.copied);
				if (result?.copied) {
					setStatus("PowerShell script copied to clipboard.", "ok");
				} else {
					el.exportScript.value = result?.script ?? "";
					el.exportScript.focus();
					el.exportScript.select();
					setStatus("The clipboard is in use by another program. Copy the script shown above.", "error");
				}
			} catch (error) {
				el.exportPanel.hidden = true;
				setStatus(`PowerShell export failed: ${error.message}`, "error");
			}
		});
		el.closeExportButton.addEventListener("click", () => {
			el.exportPanel.hidden = true;
			el.exportPsButton.focus();
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
			setPreviewVisible(!state.previewVisible);
			setStatus(state.previewVisible ? "Preview shown." : "Preview hidden.", "ok");
		});
	}

	if (el.helpButton && el.helpModal && el.helpBody && el.helpCloseButton) {
		// The dialog declares aria-modal, so it behaves as one: the page behind is inert, focus
		// starts in the guide, Tab cycles inside the dialog and closing returns focus to the opener.
		let opener = null;
		const closeHelp = () => {
			if (el.helpModal.hidden) return;
			el.helpModal.hidden = true;
			if (el.appShell) el.appShell.inert = false;
			if (opener?.isConnected) opener.focus();
			opener = null;
		};
		el.helpButton.addEventListener("click", async () => {
			const from = document.activeElement;
			try {
				const result = await requestHost("loadHelp");
				el.helpBody.textContent = result?.text ?? "Argument guide unavailable.";
				opener = from;
				if (el.appShell) el.appShell.inert = true;
				el.helpModal.hidden = false;
				el.helpBody.focus();
			} catch (error) {
				setStatus(`Help failed: ${error.message}`, "error");
			}
		});
		el.helpCloseButton.addEventListener("click", closeHelp);
		el.helpModal.addEventListener("click", (event) => {
			if (event.target === el.helpModal) closeHelp();
		});
		el.helpModal.addEventListener("keydown", (event) => {
			if (event.key !== "Tab") return;
			event.preventDefault();
			const stops = [el.helpBody, el.helpCloseButton];
			stops[(stops.indexOf(document.activeElement) + (event.shiftKey ? stops.length - 1 : 1)) % stops.length].focus();
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
		renderFontList(payload.fonts || []);
		state.defaultDraft = payload.defaultDraft;
		state.presets = payload.state?.userPresets || [];
		state.selectedPresetId = payload.state?.selectedPresetId || null;
		state.draft = normalizeDraft(payload.state?.lastDraft);
		// With no preset selected the last draft is the unlinked draft, and a store written before
		// scratchDraft existed has only the last draft.
		state.scratchDraft = state.selectedPresetId && payload.state?.scratchDraft
			? normalizeDraft(payload.state.scratchDraft) : clone(state.draft);
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
		if (!state.previewAvailable && el.previewButton) el.previewButton.disabled = true;

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
