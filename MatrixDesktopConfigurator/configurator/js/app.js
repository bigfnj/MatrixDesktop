const state = {
	metadata: null,
	defaultDraft: {},
	lastDraft: {},
	draft: {},
	presets: [],
	selectedPresetId: null,
	activeGroupId: null,
	dirty: false,
	testRunning: false,
	requestId: 0,
	pending: new Map(),
	saveTimer: 0,
	commandTimer: 0,
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
	fieldGrid: document.querySelector("#fieldGrid"),
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

const allFields = () => state.metadata.groups.flatMap((group) => group.fields);
const stripeEffects = new Set(["stripes", "customStripes", "pride", "trans", "transPride"]);

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

const renderGroups = () => {
	el.groupNav.replaceChildren();
	for (const group of state.metadata.groups) {
		const button = document.createElement("button");
		button.type = "button";
		button.textContent = group.title;
		button.className = group.id === state.activeGroupId ? "active" : "";
		button.addEventListener("click", () => {
			state.activeGroupId = group.id;
			renderGroups();
			renderFields();
		});
		el.groupNav.append(button);
	}
};

const renderFields = () => {
	const group = state.metadata.groups.find((item) => item.id === state.activeGroupId) || state.metadata.groups[0];
	el.fieldGrid.replaceChildren();
	for (const field of group.fields) {
		el.fieldGrid.append(renderField(field));
	}
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
	input.addEventListener("change", () => updateDraftValue(field, input.checked));
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
		if (field.id === "windowMode" || field.id === "effect") {
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
	input.addEventListener("input", () => {
		const value = input.value === "" ? null : toNumber(input.value, field.defaultValue ?? 0);
		updateDraftValue(field, value);
	});
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
	const color = state.draft[field.id] || { r: 0, g: 0, b: 0 };
	const swatch = document.createElement("input");
	swatch.type = "color";
	swatch.value = colorToHex(color);
	const numbers = document.createElement("div");
	numbers.className = "rgb-grid";
	const numberInputs = ["r", "g", "b"].map((channel) => {
		const input = document.createElement("input");
		input.type = "number";
		input.min = "0";
		input.max = "1";
		input.step = "0.01";
		input.value = format01(color[channel]);
		numbers.append(input);
		return [channel, input];
	});
	const push = (next) => {
		updateDraftValue(field, next);
		renderFields();
	};
	swatch.addEventListener("input", () => push(hexToColor(swatch.value)));
	for (const [channel, input] of numberInputs) {
		input.addEventListener("input", () => {
			push({ ...color, [channel]: clamp01(toNumber(input.value, color[channel])) });
		});
	}
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
	const swatch = document.createElement("input");
	swatch.type = "color";
	swatch.value = colorToHex(stop);
	const label = document.createElement("input");
	label.type = "text";
	label.value = `${format01(stop.r)}, ${format01(stop.g)}, ${format01(stop.b)}`;
	label.readOnly = true;
	const at = document.createElement("input");
	at.type = "number";
	at.min = "0";
	at.max = "1";
	at.step = "0.01";
	at.value = format01(stop.at);
	const up = miniButton("↑");
	const down = miniButton("↓");
	const remove = miniButton("×");
	const save = () => {
		updateDraftValue(field, stops);
		renderFields();
	};
	swatch.addEventListener("input", () => {
		stops[index] = { ...stops[index], ...hexToColor(swatch.value) };
		save();
	});
	at.addEventListener("input", () => {
		stops[index] = { ...stops[index], at: clamp01(toNumber(at.value, stops[index].at)) };
		save();
	});
	up.disabled = index === 0;
	up.addEventListener("click", () => {
		[stops[index - 1], stops[index]] = [stops[index], stops[index - 1]];
		save();
	});
	down.disabled = index === stops.length - 1;
	down.addEventListener("click", () => {
		[stops[index + 1], stops[index]] = [stops[index], stops[index + 1]];
		save();
	});
	remove.addEventListener("click", () => {
		stops.splice(index, 1);
		save();
	});
	row.append(swatch, label, at, up, down, remove);
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
	const swatch = document.createElement("input");
	swatch.type = "color";
	swatch.value = colorToHex(color);
	swatch.disabled = disabled;
	const label = document.createElement("input");
	label.type = "text";
	label.value = `${format01(color.r)}, ${format01(color.g)}, ${format01(color.b)}`;
	label.readOnly = true;
	const up = miniButton("↑");
	const down = miniButton("↓");
	const remove = miniButton("×");
	const save = () => {
		updateDraftValue(field, colors);
		renderFields();
	};
	swatch.addEventListener("input", () => {
		colors[index] = { ...colors[index], ...hexToColor(swatch.value) };
		save();
	});
	up.disabled = disabled || index === 0;
	up.addEventListener("click", () => {
		[colors[index - 1], colors[index]] = [colors[index], colors[index - 1]];
		save();
	});
	down.disabled = disabled || index === colors.length - 1;
	down.addEventListener("click", () => {
		[colors[index + 1], colors[index]] = [colors[index], colors[index + 1]];
		save();
	});
	remove.disabled = disabled;
	remove.addEventListener("click", () => {
		colors.splice(index, 1);
		save();
	});
	row.append(swatch, label, up, down, remove);
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
	renderGroups();
	renderFields();
	scheduleCommandBuild();
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
		bindEvents();
		setDirty(false);
		refreshAll();
		setStatus("Ready.");
	} catch (error) {
		setStatus(error.message, "error");
	}
};

init();
