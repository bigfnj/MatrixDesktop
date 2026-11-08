"""DOM smoke backed by the production C# configurator importer and command builder.

No screensaver registration or application launch is performed. Explicit-action
messages are recorded so their UI boundary can be checked without owner-state changes.
"""
import argparse
import functools
import hashlib
import http.server
import json
import subprocess
import sys
import threading
import time
import traceback
from urllib.parse import quote
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CONFIGURATOR = ROOT / "MatrixDesktopConfigurator" / "configurator"
HARNESS = ROOT / "tests" / "MatrixDesktop.Tests" / "bin" / "Release" / "MatrixDesktop.Tests.exe"
FIXTURE = ROOT / "artifacts" / "gate" / "loadState.json"
STUB = """
(() => {
  const state = __STATE__;
  window.__smokeMessages = [];
  window.__smokeReplies = [];
  window.__heldPreviewReplies = [];
  // The preset store, applied synchronously in arrival order as ConfiguratorForm does:
  // SaveDraft, SavePreset, DeletePreset and the renamePreset contract (Name only).
  // scratchDraft follows the host contract: a sent object wins; when absent an unlinked save
  // derives it from the draft and a linked save keeps it.
  const host = window.__host = structuredClone(state.state);
  const same = (a, b) => typeof a === 'string' && typeof b === 'string' && a.toLowerCase() === b.toLowerCase();
  const object = value => value && typeof value === 'object' && !Array.isArray(value) ? structuredClone(value) : {};
  const text = value => typeof value === 'string' ? value : null;
  const reply = preset => ({preset: structuredClone(preset), state: structuredClone(host)});
  const store = {
    saveDraft: p => {
      host.lastDraft = object(p.draft); host.selectedPresetId = text(p.selectedPresetId);
      if (p.scratchDraft && typeof p.scratchDraft === 'object' && !Array.isArray(p.scratchDraft)) host.scratchDraft = structuredClone(p.scratchDraft);
      else if (!host.selectedPresetId) host.scratchDraft = object(p.draft);
      return {saved:true};
    },
    savePreset: p => {
      const id = text(p.preset.id)?.trim() ? p.preset.id : crypto.randomUUID().replaceAll('-', '');
      let preset = host.userPresets.find(item => same(item.id, id));
      if (!preset) host.userPresets.push(preset = {id});
      preset.name = text(p.preset.name)?.trim() ? p.preset.name : 'Untitled preset';
      preset.values = object(p.preset.values);
      host.selectedPresetId = id; host.lastDraft = structuredClone(preset.values);
      return reply(preset);
    },
    renamePreset: p => {
      const preset = host.userPresets.find(item => same(item.id, text(p.id)));
      if (!preset) throw new Error('Unknown preset.');
      if (text(p.name)?.trim()) preset.name = p.name.trim();
      return reply(preset);
    },
    deletePreset: p => {
      const id = text(p.id);
      if (id?.trim()) {
        host.userPresets = host.userPresets.filter(item => !same(item.id, id));
        if (same(host.selectedPresetId, id)) host.selectedPresetId = null;
      }
      return {state: structuredClone(host)};
    },
  };
  window.chrome = window.chrome || {};
  window.chrome.webview = { postMessage: async msg => {
    window.__smokeMessages.push(structuredClone(msg));
    try {
      let payload = {};
      if (msg.type === 'loadState') payload = state;
      else if (['buildCommand','buildWebQuery','importCommand','testCommand'].includes(msg.type)) {
        const response = await fetch('/__host', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(msg)});
        if (!response.ok) throw new Error(await response.text());
        payload = await response.json();
      } else if (Object.hasOwn(store, msg.type)) payload = store[msg.type](msg.payload || {});
      else if (msg.type === 'browseConfig') payload = {text:window.__selectedCfgText??null};
      else if (msg.type === 'useScreensaver') payload = {saved:true,message:'Synthetic screensaver action recorded.'};
      else if (msg.type === 'restoreScreensaver') payload = {restored:true,message:'Synthetic restore action recorded.'};
      else if (msg.type === 'exportPowerShell') {  // {script, copied}, or {error} for a failed export, set by the check
        if (window.__exportReply?.error) throw new Error(window.__exportReply.error);
        payload = window.__exportReply;
      }
      window.__smokeReplies.push({type:msg.type,payload:structuredClone(payload)});
      if(msg.type==='buildWebQuery' && window.__holdPreviewReplies) {
        window.__heldPreviewReplies.push({id:msg.id,ok:true,payload});return;
      }
      setTimeout(() => window.configHost.receive({id:msg.id,ok:true,payload}),window.__replyDelay||0);
    } catch (error) { window.configHost.receive({id:msg.id,ok:false,payload:{message:String(error)}}); }
  }};
})();
"""


def run_host(message):
    process = subprocess.run(
        [str(HARNESS), "--configurator-request"], input=json.dumps(message, ensure_ascii=False),
        capture_output=True, text=True, encoding="utf-8", timeout=15, cwd=ROOT)
    if process.returncode:
        raise RuntimeError(process.stderr.strip() or process.stdout.strip())
    return json.loads(process.stdout)


def serve(app_js):
    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self, *_args):
            pass

        def do_GET(self):
            if self.path.startswith('/preview/index.html'):
                body = b'<!doctype html><html><body>Controlled preview fixture</body></html>'
                self.send_response(200)
                self.send_header('Content-Type', 'text/html')
                self.send_header('Content-Length', str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                return
            if self.path.split("?", 1)[0] == "/js/app.js" and app_js:
                body = app_js.read_bytes()
                self.send_response(200)
                self.send_header("Content-Type", "text/javascript; charset=utf-8")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            else:
                super().do_GET()

        def do_POST(self):
            if self.path != "/__host":
                self.send_error(404)
                return
            try:
                message = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                body = json.dumps(run_host(message), ensure_ascii=False).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
            except Exception as error:
                body = str(error).encode("utf-8")
                self.send_response(500)
                self.send_header("Content-Type", "text/plain")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            try:
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionError):
                pass  # The page may close while its final debounced request completes.

    handler = functools.partial(Handler, directory=str(CONFIGURATOR))
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, "http://127.0.0.1:" + str(server.server_port)


def field(page, key):
    return page.locator('[data-field="' + key + '"]')


def choose_zmatrix(page):
    field(page, "scene").locator("select").select_option("zmatrix")
    page.wait_for_function("document.querySelector('[data-field=\"zmMaxStream\"] input[type=number]').disabled === false")


def saved(page):
    page.wait_for_timeout(400)
    return page.evaluate("window.__smokeMessages.filter(m=>m.type==='saveDraft').at(-1)?.payload.draft")


def check_catalog(page, fixture):
    choose_zmatrix(page)
    specs = [entry for group in fixture["metadata"]["groups"] for entry in group["fields"] if entry["id"].startswith("zm")]
    assert len(specs) == 27, "the real catalog must contain all27 functional ZMatrix options"
    for spec in specs:
        node = field(page, spec["id"])
        assert node.count() == 1, spec["id"] + " editor missing"
        assert node.locator("input,select,textarea,button").count(), spec["id"] + " rendered no controls"
    expected = {entry["id"] for entry in fixture["metadata"]["fontFields"]}
    assert len(expected) == 15, "font metadata must cover all15 original LOGFONT fields"
    for key in ("zmFont", "zmSpecialFont"):
        actual = set(field(page, key).locator("[data-font-field]").evaluate_all("nodes=>nodes.map(n=>n.dataset.fontField)"))
        assert actual == expected, key + " has incomplete structured font controls"
        assert field(page, key).locator("textarea").count() == 0, "font settings must not expose rawJSON"
    assert not any(message["type"] in ("useScreensaver", "restoreScreensaver") for message in page.evaluate("window.__smokeMessages")), "initialization performed a screensaver action"


def check_editors(page, _fixture):
    choose_zmatrix(page)
    regular = field(page, "zmFont")
    regular.locator("summary").click()
    regular.locator('[data-font-field="name"]').fill("Consolas")
    regular.locator('[data-font-field="name"]').blur()
    regular.locator('[data-font-field="height"]').fill("-21")
    regular.locator('[data-font-field="height"]').blur()
    special = field(page, "zmSpecialFont")
    special.locator("summary").click()
    special.locator('[data-font-field="italic"]').check()
    field(page, "zmCharacters").locator("textarea").fill('AΩ"\\ ')
    field(page, "zmCharacters").locator("textarea").blur()
    phrase = 'He said "wake up", C:\\code\\'
    field(page, "zmPhrases").locator('input[type="text"]').fill(phrase)
    field(page, "zmForegroundColor").locator('[data-channel="r"]').fill("127")
    field(page, "zmForegroundColor").locator('[data-channel="r"]').blur()
    draft = saved(page)
    assert draft["zmFont"]["name"] == "Consolas" and draft["zmFont"]["height"] == -21, draft["zmFont"]
    assert draft["zmSpecialFont"]["italic"] is True
    assert draft["zmForegroundColor"]["r"] == 127
    assert draft["zmPhrases"] == [phrase]
    command = run_host({"type": "buildCommand", "payload": {"draft": draft}})["command"]
    page.locator("#importButton").click()
    page.locator("#importInput").fill(command)
    page.locator("#applyImportButton").click()
    page.wait_for_function("document.querySelector('#importPanel').hidden")
    assert field(page, "zmCharacters").locator("textarea").input_value() == 'AΩ"\\ ', repr(field(page, "zmCharacters").locator("textarea").input_value())
    assert field(page, "zmPhrases").locator('input[type="text"]').input_value() == phrase
    assert field(page, "zmFont").locator('[data-font-field="height"]').input_value() == "-21"


def check_gating(page, _fixture):
    choose_zmatrix(page)
    for key in ("font", "numColumns", "animationSpeed", "glyphIntensity", "uc3dAmount", "showPictures"):
        assert field(page, key).locator("input,select,textarea").evaluate_all("nodes=>nodes.every(n=>n.disabled)"), key + " is incorrectly active"
        assert field(page, key).locator(".field-help").inner_text(), key + " lacks a reason"
    assert field(page, "zmBackTrace").locator('input[type="number"]').is_disabled()
    field(page, "zmMonotonousCleanupEnabled").locator("input").check()
    assert field(page, "zmBackTrace").locator('input[type="number"]').is_enabled()
    field(page, "zmRandomizedCleanupEnabled").locator("input").check()
    assert field(page, "zmLeading").locator('input[type="number"]').is_enabled()
    field(page, "zmBackgroundMode").locator("select").select_option("color")
    assert field(page, "zmBlendMode").locator("select").is_disabled()
    field(page, "zmMaxStream").locator('input[type="number"]').fill("1234")
    field(page, "zmMaxStream").locator('input[type="number"]').blur()
    field(page, "scene").locator("select").select_option("matrix")
    assert field(page, "zmCharacters").locator("textarea").is_disabled()
    assert field(page, "zmFont").locator("[data-font-field]").evaluate_all("nodes=>nodes.every(n=>n.disabled)")
    assert field(page, "numColumns").locator("input").is_enabled()
    choose_zmatrix(page)
    assert field(page, "zmMaxStream").locator('input[type="number"]').input_value() == "1234", "scene switching lost the ZMatrix edit"


def check_cfg(page, _fixture):
    field(page, "windowMode").locator("select").select_option("single-monitor")
    field(page, "monitor").locator("input").fill("2")
    field(page, "processPriority").locator("select").select_option("normal")
    cfg = """[General]
ConfigDPI=144
MaxStream=777
OwnerExtension=retained
[Text]
FontName=Consolas
FontHeight=-18
CharSet=A,0x42,Ω
[Colors]
FGColor={187,255,187,255}
BGColor={0,0,0,128}
SpecialStringBGColor={1,2,3,255}
[SpecialText]
Strings=First|Second
"""
    page.locator("#importButton").click()
    page.locator("#importInput").fill(cfg)
    page.locator("#applyImportButton").click()
    page.wait_for_function("document.querySelector('[data-field=\"scene\"] select').value==='zmatrix'")
    assert field(page, "windowMode").locator("select").input_value() == "single-monitor"
    assert field(page, "monitor").locator("input").input_value() == "2"
    assert field(page, "processPriority").locator("select").input_value() == "normal"
    assert field(page, "zmMaxStream").locator('input[type="number"]').input_value() == "777"
    assert field(page, "zmFont").locator('[data-font-field="height"]').input_value() == "-12"
    assert field(page, "zmCharacters").locator("textarea").input_value() == "ABΩ", repr(field(page, "zmCharacters").locator("textarea").input_value())
    assert field(page, "zmOpaqueBackground").locator("input").is_checked()
    assert field(page, "zmPhrases").locator('input[type="text"]').evaluate_all("nodes=>nodes.map(node=>node.value)") == ["First", "Second"]
    report = page.locator(".import-report")
    assert report.count() == 1 and "migration notes" in report.inner_text()
    report.locator("summary").first.click()
    assert "extra DPI scaling" in report.inner_text() and "OwnerExtension" in report.inner_text()
    assert not any(message["type"] in ("useScreensaver", "restoreScreensaver", "testCommand") for message in page.evaluate("window.__smokeMessages")), "import caused a launch or registration action"


def check_cfg_chooser(page, _fixture):
    choose_zmatrix(page)
    page.wait_for_function("document.querySelector('#commandOutput').value.includes('scene')")
    cfg = "[General]\nMaxStream=789\n[Text]\nFontName=Consolas\nFontHeight=-19\nCharSet=A,0x42,Ω\n"
    page.locator("#importButton").click()
    page.evaluate("text=>window.__selectedCfgText=text", cfg)
    page.locator("#importFileButton").click()
    page.wait_for_function("text=>document.querySelector('#importInput').value===text", arg=cfg)
    assert page.locator("#importPanel").is_visible(), "choosing a file left the import panel hidden"
    page.evaluate("window.__selectedCfgText=null")
    page.locator("#importFileButton").click()
    page.wait_for_function("window.__smokeMessages.filter(message=>message.type==='browseConfig').length===2")
    assert page.locator("#importInput").input_value() == cfg, "cancelling the chooser replaced the selected CFG text"
    page.locator("#applyImportButton").click()
    page.wait_for_function("document.querySelector('[data-field=\"zmMaxStream\"] input[type=number]').value==='789'")
    assert field(page, "zmFont").locator('[data-font-field="height"]').input_value() == "-19"
    assert field(page, "zmCharacters").locator("textarea").input_value() == "ABΩ"


def check_late_picker(page, _fixture):
    for key, effect in (("palette", "palette"), ("stripeColors", "stripes")):
        field(page, "scene").locator("select").select_option("matrix")
        field(page, "effect").locator("select").select_option(effect)
        row = field(page, key).locator(".list-row").first
        row.locator(".hex-input").fill("#123456")
        row.locator(".color-swatch").click()
        picker = page.locator(".color-popover .cp-hex")
        picker.fill("#234567")
        expected = saved(page)[key]
        assert abs(expected[0]["r"] - 35 / 255) < 1e-10, "the enabled picker must first persist its color"
        field(page, "scene").locator("select").select_option("zmatrix")
        assert field(page, key).locator(".color-swatch").first.is_disabled()
        assert picker.is_visible(), "the test must retain the open picker callback across the scene change"
        picker.fill("#ff0000")
        assert saved(page)[key] == expected, key + " changed saved state through a late disabled picker callback"
        page.keyboard.press("Escape")
        field(page, "scene").locator("select").select_option("matrix")
        assert saved(page)[key] == expected, key + " lost its saved color after returning to Matrix"


def check_close_snapshot(page, _fixture):
    choose_zmatrix(page)
    saved(page)
    result = page.evaluate("""() => {
        const before=window.__smokeMessages.filter(message=>message.type==='saveDraft').length;
        const input=document.querySelector('[data-field="zmMaxStream"] input[type=number]');
        input.value='2345';input.dispatchEvent(new Event('input',{bubbles:true}));
        const snapshot=configHost.snapshotDraft();
        const count=window.__smokeMessages.filter(message=>message.type==='saveDraft').length;
        snapshot.draft.zmMaxStream=1;
        return {before,count,latest:configHost.snapshotDraft()};
    }""")
    assert result["before"] == result["count"], "the test must snapshot before the ordinary save debounce fires"
    assert result["latest"]["draft"]["zmMaxStream"] == 2345, "the native closing snapshot lost the latest unsaved field edit or exposed mutable state"
    page.wait_for_timeout(400)
    assert page.evaluate("window.__smokeMessages.filter(message=>message.type==='saveDraft').length") == result["before"], "closing snapshot did not cancel the pending ordinary draft save"


def check_actions(page, _fixture):
    choose_zmatrix(page)
    field(page, "windowMode").locator("select").select_option("wallpaper")
    assert field(page, "wallpaperInput").locator("input").is_enabled()
    for key in ("topmost", "hideCursor", "exitOnAnyKey", "workingArea"):
        assert field(page, key).locator("input").is_disabled(), key + " is active in wallpaper mode"
    assert not any(message["type"] in ("useScreensaver", "restoreScreensaver") for message in page.evaluate("window.__smokeMessages"))
    page.locator("#testButton").click()
    page.wait_for_function("window.__smokeReplies.some(r=>r.type==='testCommand')")
    reply = page.evaluate("window.__smokeReplies.filter(r=>r.type==='testCommand').at(-1).payload")
    assert "--windowed" in reply["arguments"] and "--no-exit-on-any-key" in reply["arguments"]
    assert not any(argument.startswith("--wallpaper") for argument in reply["arguments"]), reply
    assert not any(message["type"] in ("useScreensaver", "restoreScreensaver") for message in page.evaluate("window.__smokeMessages")), "Test Argument caused registration"
    page.locator("#screensaverButton").click()
    page.wait_for_function("window.__smokeMessages.filter(m=>m.type==='useScreensaver').length===1")
    page.locator("#restoreScreensaverButton").click()
    page.wait_for_function("window.__smokeMessages.filter(m=>m.type==='restoreScreensaver').length===1")


def check_preview_race(page, _fixture):
    choose_zmatrix(page)
    page.wait_for_function("document.querySelector('#previewFrame').src.includes('scene=zmatrix')")
    page.evaluate("window.__holdPreviewReplies=true;window.__heldPreviewReplies=[]")
    control = field(page, "zmMaxStream").locator('input[type="number"]')
    control.fill("1100")
    control.blur()
    page.wait_for_function("window.__heldPreviewReplies.length===1")
    control.fill("1200")
    control.blur()
    page.wait_for_function("window.__heldPreviewReplies.length===2")
    page.evaluate("configHost.receive(window.__heldPreviewReplies[1])")
    page.wait_for_function("document.querySelector('#previewFrame').src.includes('zmMaxStream=1200')")
    expected = page.locator("#previewFrame").get_attribute("src")
    page.evaluate("async()=>{configHost.receive(window.__heldPreviewReplies[0]);await Promise.resolve();await Promise.resolve();}")
    assert page.locator("#previewFrame").get_attribute("src") == expected, "an obsolete preview reply navigated after the latest edit"
    control.fill("1300")
    control.blur()
    page.wait_for_function("window.__heldPreviewReplies.length===3")
    page.locator("#previewButton").click()
    page.evaluate("async()=>{configHost.receive(window.__heldPreviewReplies[2]);await Promise.resolve();await Promise.resolve();}")
    assert page.locator("#previewFrame").get_attribute("src") == expected, "a preview reply navigated after the pane was hidden"


def check_doc_labels(page, _fixture):
    # README, docs/ZMATRIX.md and the guide (shown by the configurator's own ? modal) name
    # these controls; each rendered label must appear in each document, read-only from disk.
    docs = {name: " ".join((ROOT / name).read_text(encoding="utf-8").split())
            for name in ("README.md", "docs/ZMATRIX.md", "MatrixDesktop_Argument_Guide.txt")}
    problems = []
    for selector in ("#screensaverButton", "#restoreScreensaverButton"):
        label = " ".join(page.locator(selector).inner_text().split())
        assert label, selector + " rendered no label"
        problems += [name + " does not name " + selector + " '" + label + "'" for name, text in docs.items() if label not in text]
    assert not problems, "; ".join(problems)


def preset_store(fixture):
    # A saved store with two presets and an unlinked working draft of 33 columns.
    def values(columns):
        return dict(fixture["defaultDraft"], numColumns=columns)
    presets = [{"id": "alpha", "name": "Alpha", "values": values(50)}, {"id": "beta", "name": "Beta", "values": values(60)}]
    return dict(fixture, state=dict(fixture["state"], selectedPresetId=None, lastDraft=values(33), userPresets=presets))


def check_rename(page, _fixture):
    columns = field(page, "numColumns").locator('input[type="number"]')
    page.locator("#presetSelect").select_option("alpha")
    assert columns.input_value() == "50", "the Alpha preset did not load: " + columns.input_value()
    columns.fill("77")
    assert saved(page)["numColumns"] == 77, "the edit was not autosaved as the working draft"
    page.once("dialog", lambda dialog: dialog.accept("Alpha renamed"))
    page.locator("#renamePresetButton").click()
    page.wait_for_function("[...document.querySelector('#presetSelect').options].some(option=>option.text==='Alpha renamed')")
    host = page.evaluate("window.__host")
    alpha = next(preset for preset in host["userPresets"] if preset["id"] == "alpha")
    assert alpha["values"]["numColumns"] == 50, "Rename committed the unsaved draft to the preset's saved values: " + str(alpha["values"]["numColumns"])
    assert columns.input_value() == "77", "Rename replaced the working draft in the editor"
    assert host["selectedPresetId"] == "alpha" and host["lastDraft"]["numColumns"] == 77, "Rename changed the stored selection or draft"
    assert page.locator("#presetSelect").input_value() == "alpha", "Rename lost the dropdown selection"
    renames = [message["payload"] for message in page.evaluate("window.__smokeMessages") if message["type"] == "renamePreset"]
    assert renames == [{"id": "alpha", "name": "Alpha renamed"}], "Rename must send only the id and name: " + json.dumps(renames)
    assert page.locator("#statusLine").inner_text() == "Preset renamed.", page.locator("#statusLine").inner_text()


def relaunch(page, fixture, stored):
    # A second configurator page loaded from the store the first page left behind.
    fresh = page.context.browser.new_page(viewport={"width": 1280, "height": 860})
    errors = []
    fresh.on("pageerror", lambda error: errors.append(str(error)))
    fresh.add_init_script(STUB.replace("__STATE__", json.dumps(dict(fixture, state=stored), ensure_ascii=False)))
    fresh.goto(page.url, wait_until="load")
    fresh.wait_for_function("document.querySelectorAll('.field').length>40")
    return fresh, errors


def check_scratch_draft(page, fixture):
    def columns(target):
        return field(target, "numColumns").locator('input[type="number"]').input_value()
    drafts = "window.__smokeMessages.filter(m=>m.type==='saveDraft').map(m=>m.payload)"
    presets = page.locator("#presetSelect")
    assert columns(page) == "33", "the unlinked draft did not load: " + columns(page)
    presets.select_option("alpha")
    page.wait_for_timeout(400)
    witness = page.evaluate(drafts)[-1]
    assert witness["selectedPresetId"] == "alpha" and witness["draft"]["numColumns"] == 50, "no saveDraft witnessed the Alpha selection"
    presets.select_option("")
    page.wait_for_timeout(400)
    assert columns(page) == "33", "Last draft brought back the preset instead of the unlinked draft: " + columns(page)
    last = page.evaluate(drafts)[-1]
    assert last["selectedPresetId"] is None and last["draft"]["numColumns"] == 33, "the Last draft save stored the wrong draft or selection"
    # An unlinked edit still inside the save debounce when a preset is chosen.
    page.evaluate("""() => {
        const input = document.querySelector('[data-field="numColumns"] input[type=number]');
        input.value = '44'; input.dispatchEvent(new Event('input', {bubbles: true}));
        const select = document.querySelector('#presetSelect');
        select.value = 'beta'; select.dispatchEvent(new Event('change', {bubbles: true}));
    }""")
    assert columns(page) == "60", "the Beta preset did not load: " + columns(page)
    page.wait_for_timeout(400)
    presets.select_option("")
    assert columns(page) == "44", "choosing a preset inside the save debounce lost the unlinked edit: " + columns(page)
    presets.select_option("alpha")
    page.wait_for_timeout(400)
    stored = page.evaluate("window.__host")
    assert stored["selectedPresetId"] == "alpha" and stored.get("scratchDraft", {}).get("numColumns") == 44, "the store did not keep the unlinked draft beside the selected preset"
    # A relaunch, then a store written before scratchDraft existed (falls back to the last draft).
    old_store = {key: value for key, value in stored.items() if key != "scratchDraft"}
    for state, expected, label in ((stored, "44", "after a relaunch"), (old_store, "50", "for a store without scratchDraft")):
        fresh, errors = relaunch(page, fixture, state)
        try:
            assert columns(fresh) == "50", "the relaunch did not restore the selected preset's draft: " + columns(fresh)
            fresh.locator("#presetSelect").select_option("")
            assert columns(fresh) == expected, "Last draft " + label + " gave " + columns(fresh) + ", not " + expected
            assert not errors, "; ".join(errors)
        finally:
            fresh.close()


def held_dialog(answer=None):
    # The page stays blocked in confirm()/prompt() past the 250 ms draft-save deadline. Callers
    # also give the stub a real host's reply latency (15 ms), or the reply beats the overdue save.
    def handle(dialog):
        time.sleep(0.35)
        dialog.accept(answer) if answer is not None else dialog.accept()
    return handle


def edit_then_click(page, selector):
    # numColumns 77 arms the draft save and the click follows in the same task, so the save is
    # still pending when the action starts; the state at click time is the witness.
    return page.evaluate("""selector => {
        const saves = () => window.__smokeMessages.filter(m => m.type === 'saveDraft').length;
        const before = window.__smokeMessages.length, count = saves();
        const input = document.querySelector('[data-field="numColumns"] input[type=number]');
        input.value = '77'; input.dispatchEvent(new Event('input', {bubbles: true}));
        const button = document.querySelector(selector);
        let atClick = null;
        button.addEventListener('click', () => {
            atClick = {saves: saves(), label: document.querySelector('#saveState').textContent};
        }, {capture: true, once: true});
        button.click();
        return {before, count, atClick};
    }""", selector)


def preset_messages(page, start):
    sent = []
    for message in page.evaluate("window.__smokeMessages")[start:]:
        payload = message["payload"]
        if message["type"] == "saveDraft":
            sent.append("saveDraft(" + str(payload["selectedPresetId"]) + "," + str(payload["draft"]["numColumns"]) + ")")
        elif message["type"] in ("deletePreset", "savePreset"):
            sent.append(message["type"] + "(" + (payload.get("id") or payload.get("preset", {}).get("name")) + ")")
    return sent


def check_pending_save_delete(page, _fixture):
    page.locator("#presetSelect").select_option("alpha")
    page.wait_for_timeout(400)
    page.evaluate("window.__replyDelay=15")
    page.once("dialog", held_dialog())
    witness = edit_then_click(page, "#deletePresetButton")
    assert witness["atClick"] == {"saves": witness["count"], "label": "Saving…"}, "the draft save was not pending at the click: " + json.dumps(witness)
    page.wait_for_function("window.__smokeReplies.some(r=>r.type==='deletePreset')")
    page.wait_for_timeout(400)
    sent = preset_messages(page, witness["before"])
    assert sent == ["saveDraft(alpha,77)", "deletePreset(alpha)"], "a draft save pending at Delete arrived out of order: " + ", ".join(sent)
    host = page.evaluate("window.__host")
    assert host["selectedPresetId"] is None and host["lastDraft"]["numColumns"] == 77, "the store kept a deleted selection or lost the edit"


def check_pending_save_save_as(page, _fixture):
    page.locator("#presetSelect").select_option("alpha")
    page.wait_for_timeout(400)
    page.evaluate("window.__replyDelay=15")
    page.once("dialog", held_dialog("Gamma"))
    witness = edit_then_click(page, "#saveAsPresetButton")
    assert witness["atClick"] == {"saves": witness["count"], "label": "Saving…"}, "the draft save was not pending at the click: " + json.dumps(witness)
    page.wait_for_function("window.__smokeReplies.some(r=>r.type==='savePreset')")
    page.wait_for_timeout(400)
    sent = preset_messages(page, witness["before"])
    assert sent == ["saveDraft(alpha,77)", "savePreset(Gamma)"], "a draft save pending at Save As arrived out of order: " + ", ".join(sent)
    host = page.evaluate("window.__host")
    gamma = next(preset for preset in host["userPresets"] if preset["name"] == "Gamma")
    assert host["selectedPresetId"] == gamma["id"] and host["lastDraft"]["numColumns"] == 77, "the store did not select the new preset with the edit"


def check_settled_save(page, _fixture):
    # Control: nothing pending, so Delete must not post a draft save of its own.
    page.locator("#presetSelect").select_option("alpha")
    field(page, "numColumns").locator('input[type="number"]').fill("77")
    page.wait_for_timeout(600)
    start = page.evaluate("window.__smokeMessages.length")
    assert preset_messages(page, 0)[-1] == "saveDraft(alpha,77)", "the edit was not saved before the control"
    page.once("dialog", lambda dialog: dialog.accept())
    page.locator("#deletePresetButton").click()
    page.wait_for_function("window.__smokeReplies.some(r=>r.type==='deletePreset')")
    page.wait_for_timeout(400)
    sent = preset_messages(page, start)
    assert sent == ["deletePreset(alpha)"], "Delete posted a draft save with nothing pending: " + ", ".join(sent)
    host = page.evaluate("window.__host")
    assert host["selectedPresetId"] is None and host["lastDraft"]["numColumns"] == 77, "the control store is wrong"


def check_test_exited(page, _fixture):
    stop, status = page.locator("#stopButton"), page.locator("#statusLine")
    launches = "window.__smokeReplies.filter(r=>r.type==='testCommand').length"
    page.locator("#testButton").click()
    page.wait_for_function("!document.querySelector('#stopButton').disabled")
    pid = page.evaluate("window.__smokeReplies.filter(r=>r.type==='testCommand').at(-1).payload.processId")
    assert pid == 123 and "Launched test process 123" in status.inner_text(), "launch witness: " + status.inner_text()
    page.evaluate("pid=>configHost.testExited(pid)", pid - 1)
    assert stop.is_enabled(), "a stale test exit notice disabled Stop Test"
    assert "Launched test process 123" in status.inner_text(), "a stale test exit notice replaced the status"
    page.evaluate("pid=>configHost.testExited(pid)", pid)
    assert stop.is_disabled(), "the running test's exit notice left Stop Test enabled"
    assert status.inner_text() == "Test window closed.", status.inner_text()
    page.locator("#testButton").click()
    page.wait_for_function(launches + "===2 && !document.querySelector('#stopButton').disabled")
    stop.click()
    page.wait_for_function("document.querySelector('#statusLine').textContent==='Test process stopped.'")
    page.evaluate("pid=>configHost.testExited(pid)", pid)
    assert status.inner_text() == "Test process stopped.", "a notice for a stopped test replaced its status: " + status.inner_text()


def apply_import(page, text):
    # One import through the real C# importer: the status line it left and the host's reply.
    replies = "window.__smokeReplies.filter(r=>r.type==='importCommand')"
    count = page.evaluate(replies + ".length")
    page.locator("#importButton").click()
    page.locator("#importInput").fill(text)
    page.locator("#applyImportButton").click()
    page.wait_for_function("n=>" + replies + ".length>n && document.querySelector('#importPanel').hidden", arg=count)
    return page.locator("#statusLine").inner_text(), page.evaluate(replies + ".at(-1).payload")


def check_import_status(page, _fixture):
    status, reply = apply_import(page, "MatrixDesktop.exe --numColumns 80 --bogusFlag 1")
    assert reply["applied"] == ["numColumns"] and reply["ignored"] == ["bogusFlag"], "importer witness: " + json.dumps([reply["applied"], reply["ignored"]])
    assert status == "Imported 1 setting. Ignored 1 unknown or invalid setting: bogusFlag.", "a command import misreported its ignored setting: " + status
    status, reply = apply_import(page, "MatrixDesktop.exe --numColumns 80 " + " ".join("--bogus" + str(index) + " 1" for index in range(1, 8)))
    names = reply["ignored"]
    assert len(names) == 7, "importer witness: " + json.dumps(names)
    expected = "Imported 1 setting. Ignored 7 unknown or invalid settings: " + ", ".join(names[:5]) + " and 2 more."
    assert status == expected, "seven ignored settings were not reported as five names and a count: " + status
    # A .cfg keeps an unknown key for the import report, but a line without '=' is dropped.
    cfg = """[General]
MaxStream=777
OwnerExtension=retained
not a setting
"""
    status, reply = apply_import(page, cfg)
    assert reply["applied"] == ["scene", "zmMaxStream"] and reply["ignored"] == ["General.OwnerExtension", "General: not a setting"], \
        "importer witness: " + json.dumps([reply["applied"], reply["ignored"]])
    expected = ("Imported 2 settings. Retained 1 unknown, unused or invalid setting for review in the ZMatrix import report."
                " Ignored 1 unknown or invalid setting: General: not a setting.")
    assert status == expected, "a .cfg import misreported its retained and unreadable settings: " + status
    report = page.locator(".import-report")
    assert report.count() == 1, "the status points to an import report that is not shown"
    report.locator("summary").first.click()
    assert "General.OwnerExtension retained" in report.inner_text(), "the import report does not list the retained setting"


CRLF, LF = chr(13) + chr(10), chr(10)
EXPORT_SCRIPT = CRLF.join(["# synthetic-ps1-marker", "$matrixStartInfo.Arguments = '--numColumns 80'",
                           "[void][System.Diagnostics.Process]::Start($matrixStartInfo)"]) + CRLF


def check_export_fallback(page, _fixture):
    panel, script, status = page.locator("#exportPanel"), page.locator("#exportScript"), page.locator("#statusLine")

    def export(reply):
        # The status line is cleared first, so a new status marks the reply as handled.
        page.evaluate("reply=>{window.__exportReply=reply;document.querySelector('#statusLine').textContent='';}", reply)
        page.locator("#exportPsButton").click()
        page.wait_for_function("document.querySelector('#statusLine').textContent!==''")
        if "error" not in reply:
            sent = page.evaluate("window.__smokeReplies.filter(r=>r.type==='exportPowerShell').at(-1).payload")
            assert sent == reply, "stub witness: " + json.dumps(sent)

    busy, copied = {"script": EXPORT_SCRIPT, "copied": False}, {"script": EXPORT_SCRIPT, "copied": True}
    assert panel.is_hidden(), "the export panel showed before any export"
    export(busy)
    shown = script.input_value().replace(CRLF, LF) if panel.is_visible() else None
    assert shown == EXPORT_SCRIPT.replace(CRLF, LF), "the clipboard fallback did not show the exported script: " + status.inner_text()
    assert script.get_attribute("readonly") is not None, "the shown script is editable"
    selected = page.evaluate("() => { const box = document.activeElement; return box?.id === 'exportScript' && box.selectionStart === 0 && box.selectionEnd === box.value.length; }")
    assert selected, "the shown script was not focused and selected for copying"
    assert status.inner_text() == "The clipboard is in use by another program. Copy the script shown above.", status.inner_text()
    export(copied)
    assert status.inner_text() == "PowerShell script copied to clipboard.", status.inner_text()
    assert panel.is_hidden(), "an export that reached the clipboard left the earlier fallback script on screen"
    export(busy)
    assert panel.is_visible(), "the second clipboard fallback did not show the script"
    export({"error": "The launch arguments exceed Windows' 32,767-character limit."})
    assert status.inner_text().startswith("PowerShell export failed:"), "failure witness: " + status.inner_text()
    assert panel.is_hidden(), "a failed export left the earlier fallback script on screen"
    export(busy)
    assert panel.is_visible(), "the third clipboard fallback did not show the script"
    page.locator("#closeExportButton").click()
    assert panel.is_hidden(), "Close did not hide the exported script"
    assert page.evaluate("document.activeElement?.id") == "exportPsButton", "Close did not return focus to Export .ps1"


def check_test_status(page, _fixture):
    status = page.locator("#statusLine")
    replies = "window.__smokeReplies.filter(r=>r.type==='testCommand')"

    def launch():
        count = page.evaluate(replies + ".length")
        page.locator("#testButton").click()
        page.wait_for_function("n=>" + replies + ".length>n && !document.querySelector('#stopButton').disabled", arg=count)
        return page.evaluate(replies + ".at(-1).payload")["command"]

    command = launch()
    assert command.startswith("MatrixDesktop.exe --windowed") and len(command) <= 160, "short launch witness: " + command
    assert status.inner_text() == "Launched test process 123: " + command, "the Test status did not show the launched command: " + status.inner_text()
    assert status.get_attribute("title") == command, "the Test status tooltip does not hold the launched command"
    page.locator("#stopButton").click()
    page.wait_for_function("document.querySelector('#statusLine').textContent==='Test process stopped.'")
    assert status.get_attribute("title") == "", "a later status kept the launched command's tooltip"
    choose_zmatrix(page)
    phrase = "a" * 200
    field(page, "zmPhrases").locator('input[type="text"]').fill(phrase)
    command = launch()
    assert "--zmPhrases" in command and len(command) > 160, "long launch witness: " + command[:80]
    expected = "Launched test process 123: " + command[:159] + "…"
    assert status.inner_text() == expected, "a long launched command was not shortened in the status: " + str(len(status.inner_text())) + " characters"
    assert status.get_attribute("title") == command, "the shortened Test status lost the whole command from its tooltip"


def check_limits(page, fixture):
    choose_zmatrix(page)
    original = fixture["defaultDraft"]["zmCharacters"]
    characters = field(page, "zmCharacters").locator("textarea")
    characters.fill("😀")
    characters.blur()
    assert "invalid-input" in characters.get_attribute("class"), "non-BMP glyph guard did not report invalid input"
    assert saved(page)["zmCharacters"] == original, "invalid glyph content replaced the saved list"
    characters.fill("AB")
    field(page, "zmPhrases").get_by_role("button", name="Remove", exact=True).click()
    assert saved(page)["zmPhrases"] == [], "empty phrases must persist instead of restoring a default phrase"
    font = field(page, "zmFont")
    font.locator("summary").click()
    font.locator('[data-font-field="height"]').fill("9999")
    font.locator('[data-font-field="height"]').blur()
    assert font.locator('[data-font-field="height"]').input_value() == "256"
    field(page, "zmForegroundColor").locator('[data-channel="r"]').fill("999")
    field(page, "zmForegroundColor").locator('[data-channel="r"]').blur()
    assert saved(page)["zmForegroundColor"]["r"] == 255
    phrases = [str(index) + "Ω" * 250 for index in range(64)]
    page.locator("#importButton").click()
    page.locator("#importInput").fill("?scene=zmatrix&zmPhrases=" + quote(json.dumps(phrases, ensure_ascii=False)))
    page.locator("#applyImportButton").click()
    page.wait_for_function("document.querySelector('[data-field=\"zmPhrases\"]')?.querySelectorAll('.phrase-row').length===64")
    page.wait_for_function("document.querySelector('#commandOutput').value===''")
    assert "32,767" in page.locator("#statusLine").inner_text(), "the copied-command failure lacked an actionable Windows length error: " + page.locator("#statusLine").inner_text()
    page.locator("#testButton").click()
    page.wait_for_function("window.__smokeReplies.some(r=>r.type==='testCommand')")
    reply = page.evaluate("window.__smokeReplies.filter(r=>r.type==='testCommand').at(-1).payload")
    assert "direct structured launch" in reply["command"], "an oversized copied command prevented a valid direct Test launch"
    assert page.locator("#stopButton").is_enabled(), "a successful direct Test launch lost its Stop action"
    page.locator("#stopButton").click()
    page.wait_for_function("document.querySelector('#stopButton').disabled")


def hex_rows(rows):
    # app.js colorToHex rounds half up, as Math.round does.
    return ["#" + "".join("%02x" % int(row[channel] * 255 + 0.5) for channel in "rgb") for row in rows]


def wait_command(page, text):
    page.wait_for_function("text=>document.querySelector('#commandOutput').value.includes(text)", arg=text)
    return page.locator("#commandOutput").input_value()


def check_effective_defaults(page, fixture):
    # AUD-13: an Effect or Version switch moves untouched editors to what the renderer draws
    # (app.js effectiveDefaultsFor over metadata.effectDefaults and versionDefaults), the
    # command leaves them out, and a value chosen afterwards is sent.
    effects = fixture["metadata"]["effectDefaults"]["matrix"]
    for effect, rows in (("stripes", 15), ("pride", 12)):
        field(page, "effect").locator("select").select_option(effect)
        command = wait_command(page, "--effect " + effect)
        draft = saved(page)
        assert draft["stripeColors"] == effects[effect]["stripeColors"] and len(draft["stripeColors"]) == rows, effect + " stripe editor kept " + str(draft["stripeColors"])
        shown = field(page, "stripeColors").locator(".hex-input").evaluate_all("nodes=>nodes.map(node=>node.value.toLowerCase())")
        assert shown == hex_rows(effects[effect]["stripeColors"]), effect + " stripe editor shows " + str(shown)
        assert draft["cursorColor"] == effects[effect]["cursorColor"], effect + " cursor editor kept " + str(draft["cursorColor"])
        assert "stripeColors" not in command and "cursorColor" not in command, "untouched editors were sent: " + command
    field(page, "effect").locator("select").select_option("palette")
    page.wait_for_function("!document.querySelector('#commandOutput').value.includes('--effect')")
    draft = saved(page)
    assert draft["stripeColors"] == fixture["defaultDraft"]["stripeColors"] and draft["cursorColor"] == fixture["defaultDraft"]["cursorColor"], "returning to palette did not restore the catalog editors"
    field(page, "version").locator("select").select_option("operator")
    command = wait_command(page, "--version operator")
    columns = field(page, "numColumns").locator('input[type="number"]')
    assert columns.input_value() == "108", "Version operator shows Columns " + columns.input_value() + ", the renderer draws 108"
    assert "numColumns" not in command, "untouched Columns was sent: " + command
    columns.fill("80")
    columns.blur()
    command = wait_command(page, "--numColumns 80")
    assert saved(page)["numColumns"] == 80, "Columns 80 on operator was not kept"


CHECKS = [
    ("Effect and version switches keep untouched editors on what renders", check_effective_defaults),
    ("ZMatrix catalog renders every structured control", check_catalog),
    ("ZMatrix editor changes survive real C# command import", check_editors),
    ("ZMatrix scene and cleanup dependencies retain disabled values", check_gating),
    ("ZMatrix CFG import preserves launch choices and reports migration", check_cfg),
    ("CFG chooser retains selected file text and cancellation", check_cfg_chooser),
    ("Late picker callbacks preserve disabled palette and stripe saved values", check_late_picker),
    ("Closing snapshot captures the latest edit before the save debounce", check_close_snapshot),
    ("Wallpaper tests stay windowed and registration requires explicit actions", check_actions),
    ("ZMatrix editor bounds reject invalid glyphs and preserve empty phrases", check_limits),
    ("Preview ignores obsolete and hidden asynchronous query replies", check_preview_race),
    ("Screensaver action labels match the controls the docs name", check_doc_labels),
    ("Rename changes only the preset name and keeps its saved values", check_rename),
    ("Stop Test follows the host's exit notice for the running test only", check_test_exited),
    ("Last draft brings back the unlinked draft after a preset was chosen", check_scratch_draft),
    ("A draft save pending at Delete lands before the delete", check_pending_save_delete),
    ("A draft save pending at Save As lands before the new preset", check_pending_save_save_as),
    ("Delete with no pending draft save posts no save of its own", check_settled_save),
    ("Import status names dropped settings and points only retained ones to the report", check_import_status),
    ("Export .ps1 shows the script for manual copy when the clipboard is busy", check_export_fallback),
    ("Test status shows the command the host launched", check_test_status),
]
PRESET_CHECKS = {check_rename, check_scratch_draft, check_pending_save_delete, check_pending_save_save_as, check_settled_save}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--only", default="")
    parser.add_argument("--app-js", type=Path)
    args = parser.parse_args()
    checks = [(name, function) for name, function in CHECKS if args.only.lower() in name.lower()]
    if not checks or not FIXTURE.is_file() or not HARNESS.is_file():
        raise RuntimeError("A matching check, fresh loadState fixture and built C# harness are required.")
    fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))
    assert len([field for group in fixture["metadata"]["groups"] for field in group["fields"] if field["id"].startswith("zm")]) == 27, "stale fixture: rebuild the C# harness and dump loadState"
    source = args.app_js or CONFIGURATOR / "js" / "app.js"
    print("SOURCE", source, "sha256=" + hashlib.sha256(source.read_bytes()).hexdigest(), flush=True)
    print("HARNESS", HARNESS, "sha256=" + hashlib.sha256(HARNESS.with_suffix(".dll").read_bytes()).hexdigest(), flush=True)
    try:
        from playwright.sync_api import sync_playwright
    except ImportError as error:
        print("UNVERIFIED:", error)
        return 2
    server, origin = serve(args.app_js)
    failures = 0
    try:
        with sync_playwright() as playwright:
            try:
                browser = playwright.chromium.launch()
            except Exception as error:
                print("UNVERIFIED: Chromium unavailable:", error)
                return 2
            for name, function in checks:
                page = browser.new_page(viewport={"width": 1280, "height": 860})
                errors = []
                page.on("pageerror", lambda error: errors.append(str(error)))
                page_fixture = dict(fixture)
                if function is check_preview_race:
                    page_fixture["preview"] = {"available": True, "origin": origin + "/preview/"}
                if function in PRESET_CHECKS:
                    page_fixture = preset_store(page_fixture)
                page.add_init_script(STUB.replace("__STATE__", json.dumps(page_fixture, ensure_ascii=False)))
                try:
                    page.goto(origin + "/index.html", wait_until="load")
                    page.wait_for_function("document.querySelectorAll('.field').length>40")
                    assert page.request.get(origin + "/js/app.js").body() == source.read_bytes(), "served source witness mismatch"
                    function(page, fixture)
                    assert not errors, "; ".join(errors)
                    print("PASS", name, flush=True)
                except Exception as error:
                    failures += 1
                    print("FAIL", name + ":", str(error), flush=True)
                    traceback.print_exc()
                finally:
                    page.close()
            browser.close()
    finally:
        server.shutdown()
        server.server_close()
    print(str(len(checks) - failures) + "/" + str(len(checks)) + " ZMatrix configurator checks passed.", flush=True)
    return 1 if failures else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print("HARNESS ERROR:", type(error).__name__, str(error), file=sys.stderr)
        sys.exit(3)
