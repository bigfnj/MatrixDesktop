"""DOM smoke backed by the production C# configurator importer and command builder.

No screensaver registration or application launch is performed. Explicit-action
messages are recorded so their UI boundary can be checked without owner-state changes.
"""
import argparse
import functools
import hashlib
import http.server
import json
import re
import subprocess
import sys
import threading
import time
import traceback
from collections import Counter
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
  // ConfiguratorForm answers buildCommand, importCommand and testCommand synchronously on its UI
  // thread, one at a time in arrival order, and sends every reply through ExecuteScriptAsync in
  // that order, so the C# harness requests for them queue here. Run concurrently, a slow harness
  // start answered an older command last and the page kept the command of an edit it had already
  // replaced. buildWebQuery is not queued: the host awaits the media bridge for it.
  // A body is serialized when the page posts it, as WebView2 does: payload.draft is the page's
  // live draft, which a later edit changes while the request waits its turn.
  // check_host_reply_order holds this queue to the host's order.
  const askHost = async body => {
    const response = await fetch('/__host', {method:'POST',headers:{'Content-Type':'application/json'},body});
    if (!response.ok) throw new Error(await response.text());
    return response.json();
  };
  let hostQueue = Promise.resolve();
  const askHostInOrder = msg => {
    const body = JSON.stringify(msg);
    const answer = hostQueue.then(() => askHost(body));
    hostQueue = answer.catch(() => {});
    return answer;
  };
  window.chrome = window.chrome || {};
  window.chrome.webview = { postMessage: async msg => {
    window.__smokeMessages.push(structuredClone(msg));
    try {
      let payload = {};
      if (msg.type === 'loadState') payload = state;
      else if (['buildCommand','importCommand','testCommand'].includes(msg.type)) payload = await askHostInOrder(msg);
      else if (msg.type === 'buildWebQuery') payload = await askHost(JSON.stringify(msg));
      else if (Object.hasOwn(store, msg.type)) payload = store[msg.type](msg.payload || {});
      else if (msg.type === 'loadHelp') payload = {text:window.__helpText??null};
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


def popover_hidden(page):
    return page.evaluate("document.querySelector('.color-popover')?.hidden ?? true")


def late_hex(page, value):
    # Dispatched rather than typed: a hidden popover takes no keystrokes, but a stale
    # listener would still act on an input event.
    page.evaluate("""value => { const box = document.querySelector('.color-popover .cp-hex');
        box.value = value; box.dispatchEvent(new Event('input', {bubbles: true})); }""", value)


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
        assert popover_hidden(page), key + ": the scene change re-rendered the editor but left its popover open"
        late_hex(page, "#ff0000")
        assert saved(page)[key] == expected, key + " changed saved state through a late picker callback"
        field(page, "scene").locator("select").select_option("matrix")
        assert saved(page)[key] == expected, key + " lost its saved color after returning to Matrix"


def check_picker_closes_on_rerender(page, _fixture):
    stops = page.evaluate("configHost.snapshotDraft().draft.palette")
    assert len(stops) == 4, "the default palette must have four stops for this check"
    rows = field(page, "palette").locator(".list-row")
    # Opened by Enter and re-rendered by a click that sends no pointerdown and moves no focus,
    # as keyboard and assistive-technology activation do, so neither the popover's outside
    # pointer handler nor any focus handler can close it: only the rebuild can.
    rows.nth(0).locator(".color-swatch").focus()
    page.keyboard.press("Enter")
    assert not popover_hidden(page), "Enter on a palette swatch did not open the popover"
    rows.nth(0).locator("button", has_text="↓").evaluate("node => node.click()")
    assert popover_hidden(page), "moving a stop re-rendered the palette but left the popover open on the detached row"
    late_hex(page, "#00ff00")
    assert saved(page)["palette"] == [stops[1], stops[0], stops[2], stops[3]], "a late picker edit rewrote the reordered palette"
    rows.nth(3).locator(".color-swatch").focus()
    page.keyboard.press("Enter")
    assert not popover_hidden(page), "Enter on the last swatch did not open the popover"
    rows.nth(3).locator("button", has_text="×").evaluate("node => node.click()")
    assert popover_hidden(page), "removing a stop left the popover open on the removed row"
    late_hex(page, "#ff0000")
    assert len(saved(page)["palette"]) == 3, "a late picker edit brought a removed stop back"
    # Defence in depth: an anchor detached by anything other than renderFields still refuses the edit.
    rows.nth(0).locator(".color-swatch").focus()
    page.keyboard.press("Enter")
    assert not popover_hidden(page), "Enter on the first swatch did not open the popover"
    before = page.evaluate("window.__smokeMessages.filter(m=>m.type==='saveDraft').length")
    rows.nth(0).locator(".color-swatch").evaluate("node => node.remove()")
    late_hex(page, "#0000ff")
    page.wait_for_timeout(400)
    assert page.evaluate("window.__smokeMessages.filter(m=>m.type==='saveDraft').length") == before, "a popover whose anchor left the document still saved its edit"
    assert popover_hidden(page), "a popover whose anchor left the document stayed open"


def marked_invalid(locator):
    return "invalid-input" in (locator.get_attribute("class") or "")


def check_invalid_hex(page, _fixture):
    for key, effect in (("palette", "palette"), ("stripeColors", "stripes")):
        field(page, "effect").locator("select").select_option(effect)
        row = field(page, key).locator(".list-row").first
        box = row.locator(".hex-input")
        first = lambda: saved(page)[key][0]
        rgb = lambda stop: [round(stop[channel] * 255) for channel in "rgb"]
        box.fill("#123456")
        good = first()
        assert rgb(good) == [0x12, 0x34, 0x56] and not marked_invalid(box), key + ": the row did not save '#123456': " + str(good)
        for text in ("red", " #ff880"):
            box.fill(text)
            now = first()
            assert all(isinstance(now[channel], (int, float)) for channel in "rgb"), key + ": " + repr(text) + " saved a non-number channel: " + str(now)
            assert now == good, key + ": the row saved the invalid hex " + repr(text) + ": " + str(now)
            assert marked_invalid(box), key + ": the row did not mark the invalid hex " + repr(text)
        box.fill(" #abc ")
        assert rgb(first()) == [0xaa, 0xbb, 0xcc], key + ": the row refused ' #abc ': " + str(first())
        assert not marked_invalid(box), key + ": the row kept its invalid mark after ' #abc '"
        box.fill("red")
        # The popover reads the same text the same way: refused where the row refuses, taken where it takes.
        row.locator(".color-swatch").click()
        picker = page.locator(".color-popover .cp-hex")
        picker.fill(" #ff880")
        assert rgb(first()) == [0xaa, 0xbb, 0xcc], key + ": the popover took ' #ff880', which the row refuses: " + str(first())
        assert marked_invalid(picker), key + ": the popover did not mark ' #ff880' invalid"
        picker.fill(" #123 ")
        assert rgb(first()) == [0x11, 0x22, 0x33], key + ": the popover refused ' #123 ': " + str(first())
        assert not marked_invalid(picker), key + ": the popover kept its invalid mark after ' #123 '"
        assert box.input_value() == "#112233" and not marked_invalid(box), key + ": the popover's colour left the row box stale or marked invalid"
        picker.fill("zz")
        page.keyboard.press("Escape")
        row.locator(".color-swatch").click()
        assert picker.input_value() == "#112233" and not marked_invalid(picker), key + ": the reopened popover kept the refused text or its invalid mark"
        page.keyboard.press("Escape")


def focused(page):
    return page.evaluate("""() => { const node = document.activeElement;
        const shell = node?.closest('[data-field]'); const row = node?.closest('.list-row, .phrase-row');
        const rows = shell ? [...shell.querySelectorAll('.list-row, .phrase-row')] : [];
        return {tag: node?.tagName, field: shell?.dataset.field ?? null, text: shell ? node.textContent : '',
                row: row ? rows.indexOf(row) : -1, rows: rows.length, value: node?.value ?? null}; }""")


def check_focus_rerender(page, _fixture):
    field(page, "isometric").locator("input").focus()
    page.keyboard.press("Space")
    now = focused(page)
    assert now["field"] == "isometric", "control: the non-gating isometric checkbox lost focus: " + str(now)
    field(page, "volumetric").locator("input").focus()
    page.keyboard.press("Space")
    now = focused(page)
    assert now["field"] == "volumetric" and now["tag"] == "INPUT", "Space on the gating volumetric checkbox moved focus to " + str(now)
    palette = field(page, "palette")
    palette.locator(".list-row").nth(3).locator("button", has_text="×").focus()
    page.keyboard.press("Enter")
    now = focused(page)
    assert (now["field"], now["row"], now["rows"], now["text"]) == ("palette", 2, 3, "×"), "removing the last stop did not land on the new last row's remove: " + str(now)
    palette.locator(".add-row").focus()
    page.keyboard.press("Enter")
    now = focused(page)
    assert (now["field"], now["rows"], now["text"]) == ("palette", 4, "Add stop"), "Add stop did not keep focus: " + str(now)
    field(page, "effect").locator("select").focus()
    page.keyboard.press("ArrowDown")
    page.keyboard.press("ArrowDown")
    now = focused(page)
    assert (now["field"], now["value"]) == ("effect", "none"), "two ArrowDowns on the gating effect select did not step two options with focus kept: " + str(now)
    choose_zmatrix(page)
    phrases = field(page, "zmPhrases")
    phrases.get_by_role("button", name="Add phrase").focus()
    page.keyboard.press("Enter")
    now = focused(page)
    assert (now["field"], now["rows"], now["text"]) == ("zmPhrases", 2, "Add phrase"), "Add phrase did not keep focus: " + str(now)
    phrases.locator(".phrase-row").nth(1).get_by_role("button", name="Remove").focus()
    page.keyboard.press("Enter")
    now = focused(page)
    assert (now["field"], now["row"], now["rows"], now["text"]) == ("zmPhrases", 0, 1, "Remove"), "removing the last phrase did not land on the new last row's remove: " + str(now)


def check_phrase_rows(page, _fixture):
    choose_zmatrix(page)
    phrases = field(page, "zmPhrases")
    rows = phrases.locator(".phrase-row input")
    hint = phrases.locator(":scope > .field-help").last
    values = lambda: rows.evaluate_all("nodes => nodes.map(node => node.value)")
    # Marked means both the visible invalid-input class and a constraint-validation message.
    marked = lambda: rows.evaluate_all("nodes => nodes.map(node => node.classList.contains('invalid-input') && node.matches(':invalid'))")
    add = phrases.get_by_role("button", name="Add phrase")
    add.click()
    add.click()
    shown, kept = values(), saved(page)["zmPhrases"]
    assert len(set(shown)) == len(shown) == 3 and kept == shown, "two Adds gave rows " + str(shown) + " but saved " + str(kept)
    assert hint.text_content() == "3 phrase entries", "the hint does not count the saved phrases: " + repr(hint.text_content())
    rows.nth(2).fill(shown[1])
    kept = saved(page)["zmPhrases"]
    assert kept == shown[:2], "a repeated phrase was saved twice or lost: " + str(kept)
    assert marked() == [False, False, True], "only the repeating row must be marked invalid: " + str(marked())
    assert hint.text_content() == "2 phrase entries; 1 duplicate ignored", "the hint does not count distinct phrases and report the repeat: " + repr(hint.text_content())
    phrases.locator(".phrase-row").first.get_by_role("button", name="Remove").click()
    assert values() == [shown[1], shown[1]] and marked() == [False, True], "after removing a row the remaining repeat lost its mark: " + str(marked())
    rows.nth(1).fill("Third phrase")
    assert marked() == [False, False] and saved(page)["zmPhrases"] == [shown[1], "Third phrase"], "editing the repeat away did not clear its mark or save it: " + str(marked())


def where(page):
    return page.evaluate("""() => { const node = document.activeElement;
        return {tag: node?.tagName, id: node?.id ?? '', cls: String(node?.className ?? ''),
                field: node?.closest('[data-field]')?.dataset.field ?? null, popover: Boolean(node?.closest('.color-popover')),
                modal: Boolean(node?.closest('#helpModal')), section: node?.closest('.field-section')?.id ?? null}; }""")


def popover_box(page):
    return page.evaluate("document.querySelector('.color-popover').getBoundingClientRect().top")


def check_keyboard_focus(page, _fixture):
    swatch = field(page, "cursorColor").locator(".color-swatch")
    hex_box = page.locator(".color-popover .cp-hex")
    swatch.evaluate("node => node.scrollIntoView({block: 'center'})")
    swatch.focus()
    page.keyboard.press("Enter")
    page.wait_for_timeout(150)  # the popover focuses its hex box on the next frame
    assert "cp-hex" in where(page)["cls"], "opening the popover did not move focus into its hex box: " + str(where(page))
    # cursorColor is saturated, so a hue step changes its hex (black would not).
    for selector, key in ((".cp-hue", "ArrowRight"), (".cp-square", "ArrowDown")):
        before = hex_box.input_value()
        page.locator(".color-popover " + selector).focus()
        page.keyboard.press(key)
        assert hex_box.input_value() != before, key + " on " + selector + " did not change the colour from " + before
    # The popover is fixed to the viewport and follows its swatch when the field surface scrolls.
    anchor_before, box_before = swatch.bounding_box()["y"], popover_box(page)
    page.locator("#fieldSurface").evaluate("node => node.scrollBy(0, 60)")
    page.wait_for_timeout(150)
    moved = (swatch.bounding_box()["y"] - anchor_before, popover_box(page) - box_before)
    assert abs(moved[0] + 60) < 1 and abs(moved[1] - moved[0]) < 1, "scrolling left the popover behind its swatch (swatch, popover moved): " + str(moved)
    page.keyboard.press("Escape")
    now = where(page)
    assert popover_hidden(page) and now["field"] == "cursorColor" and now["tag"] == "BUTTON", "Escape did not close the popover and return focus to its swatch: " + str(now)
    # Tab order treats the popover as if it sat right after its swatch, which it closes behind it.
    page.keyboard.press("Enter")
    page.wait_for_timeout(150)
    for _ in range(3):  # from the hex box to the hue bar, the square, then out of the popover
        page.keyboard.press("Shift+Tab")
    now = where(page)
    assert popover_hidden(page) and now["field"] == "cursorColor" and now["tag"] == "BUTTON", "Shift+Tab out of the popover did not close it and return to its swatch: " + str(now)
    page.keyboard.press("Enter")
    page.wait_for_timeout(150)
    for _ in range(4):  # from the hex box to R, G, B, then out of the popover
        page.keyboard.press("Tab")
    now = where(page)
    assert popover_hidden(page) and now["field"] == "cursorColor" and now["tag"] == "INPUT", "Tab out of the popover did not close it and move on from its swatch: " + str(now)
    # Focus moved out of it by anything else (assistive technology, a script) closes it too.
    page.keyboard.press("Shift+Tab")
    page.keyboard.press("Enter")
    page.wait_for_timeout(150)
    assert not popover_hidden(page), "Enter on the swatch did not reopen the popover: " + str(where(page))
    page.locator("#fieldFilter").focus()
    assert popover_hidden(page), "focus moved outside the popover left it open: " + str(where(page))
    # Help honours its aria-modal: focus starts inside, stays inside, and returns to the opener.
    page.locator("#helpButton").focus()
    page.keyboard.press("Enter")
    page.wait_for_function("!document.querySelector('#helpModal').hidden")
    assert where(page)["id"] == "helpBody", "opening help did not move focus to the guide: " + str(where(page))
    # Tab cycles between Close and the guide, so the guide stays reachable for arrow-key scrolling.
    trail = []
    for _ in range(3):
        page.keyboard.press("Tab")
        trail.append(where(page)["id"] or where(page)["tag"])
    assert trail == ["helpCloseButton", "helpBody", "helpCloseButton"], "Tab did not cycle between Close and the guide inside the open help dialog: " + str(trail)
    page.locator("#fieldFilter").focus()
    assert where(page)["modal"], "the page behind the open help dialog still takes focus: " + str(where(page))
    page.keyboard.press("Escape")
    now = where(page)
    assert page.locator("#helpModal").is_hidden() and now["id"] == "helpButton", "closing help did not return focus to the help button: " + str(now)
    # Focus starts in the guide, so a guide longer than the dialog scrolls with the arrow keys at once.
    page.evaluate("window.__helpText = Array.from({length: 400}, (_, i) => 'Line ' + i).join(String.fromCharCode(10))")
    page.keyboard.press("Enter")
    page.wait_for_function("!document.querySelector('#helpModal').hidden")
    page.keyboard.press("ArrowDown")
    page.wait_for_timeout(400)
    assert page.evaluate("document.querySelector('#helpBody').scrollTop") > 0, "ArrowDown in the open help dialog did not scroll the guide: " + str(where(page))
    page.keyboard.press("Escape")
    # A section jump moves focus with the view.
    page.locator("#sectionJump button[data-group='advanced']").focus()
    page.keyboard.press("Enter")
    assert where(page)["section"] == "section-advanced", "the advanced jump left focus outside its section: " + str(where(page))
    page.keyboard.press("Tab")
    now = where(page)
    assert now["section"] == "section-advanced" and now["field"], "Tab after the advanced jump did not reach its first field: " + str(now)


CONTROL_ROLES = {"checkbox", "combobox", "textbox", "searchbox", "spinbutton", "slider", "button", "listbox"}
COMPOUND_KINDS = {"color", "rgb8", "font", "phrases", "palette", "stripes"}


def ax_snapshot(page):
    """Chromium's computed accessibility tree, each live node joined to its DOM element."""
    cdp = page.context.new_cdp_session(page)
    try:
        root = cdp.send("DOM.getDocument", {"depth": -1})["root"]
        nodes = cdp.send("Accessibility.getFullAXTree")["nodes"]
    finally:
        cdp.detach()
    elements = {}
    stack = [(root, ())]
    while stack:
        node, ancestors = stack.pop()
        flat = node.get("attributes", [])
        chain = ancestors + ((node.get("localName", ""), dict(zip(flat[::2], flat[1::2]))),)
        elements[node["backendNodeId"]] = chain
        stack.extend((child, chain) for child in node.get("children", []))
    live = []
    for node in nodes:
        chain = elements.get(node.get("backendDOMNodeId"))
        if node.get("ignored") or not chain:
            continue
        live.append({"role": node.get("role", {}).get("value", ""), "name": str(node.get("name", {}).get("value", "")).strip(),
                     "tag": chain[-1][0], "attrs": chain[-1][1],
                     "field": next((attrs["data-field"] for _, attrs in reversed(chain) if "data-field" in attrs), None),
                     "surface": any(attrs.get("id") == "fieldSurface" for _, attrs in chain)})
    return live


def describe(node):
    where = "#" + node["attrs"]["id"] if node["attrs"].get("id") else "[" + str(node["field"]) + "]"
    return node["role"] + " " + node["tag"] + where


def check_accessible_names(page, fixture):
    page.locator("#importButton").click()  # #importInput is only in the tree while its panel is open
    page.wait_for_function("!document.querySelector('#importPanel').hidden")
    live = ax_snapshot(page)
    roles = Counter(node["role"] for node in live)
    for role in ("checkbox", "combobox", "textbox", "spinbutton", "slider", "button"):
        assert roles[role], "witness: the accessibility tree has no live " + role + ": " + str(dict(roles))
    controls = [node for node in live if node["role"] in CONTROL_ROLES]
    unnamed = [describe(node) for node in controls if not node["name"]]
    assert not unnamed, str(len(unnamed)) + " controls have no accessible name: " + ", ".join(unnamed[:10])
    glyphs = [describe(node) + " " + repr(node["name"]) for node in controls if node["surface"] and not re.search("[A-Za-z]", node["name"])]
    assert not glyphs, str(len(glyphs)) + " field buttons are named only by a glyph: " + ", ".join(glyphs[:6])
    compound = sorted(entry["id"] for group in fixture["metadata"]["groups"] for entry in group["fields"] if entry["kind"] in COMPOUND_KINDS)
    labels = dict(page.evaluate("ids => ids.map(id => [id, document.querySelector(`[data-field='${id}'] > label`).textContent])", compound))
    # Chromium names from rendered text, so the label's text-transform: uppercase shows in the name.
    groups = {node["attrs"]["data-field"]: node["name"].casefold() for node in live if node["role"] == "group" and "data-field" in node["attrs"]}
    wrong = [key + "=" + repr(groups.get(key)) for key in compound if groups.get(key) != labels[key].casefold()]
    assert not wrong, str(len(wrong)) + " compound editors are not a group named by their label: " + ", ".join(wrong)
    page.locator("#cancelImportButton").click()
    # The visible label operates the control it names: a click toggles the box or focuses the control.
    key = page.locator(".checkbox-field:not(.disabled-field)").first.get_attribute("data-field")
    before = field(page, key).locator("input").is_checked()
    field(page, key).locator(":scope > label").click()
    assert field(page, key).locator("input").is_checked() != before, "clicking the " + key + " label did not toggle its checkbox"
    for control in ("select", "input[type=number]"):
        key = page.locator(".field:not(.disabled-field):has(> " + control + ")").first.get_attribute("data-field")
        field(page, key).locator(":scope > label").click()
        assert page.evaluate("([key, control]) => document.activeElement === document.querySelector(`[data-field='${key}'] > ${control}`)", [key, control]), \
            "clicking the " + key + " label did not focus its " + control


FONT_NAMES = ["Fixture Font " + str(index) for index in range(50)]


def check_font_list(page, _fixture):
    lists = page.evaluate("[...document.querySelectorAll('[data-font-field=name]')].map(input => input.list?.options.length ?? null)")
    assert lists == [len(FONT_NAMES)] * 2, "the two font-name boxes do not each offer the installed fonts: " + str(lists)
    page.evaluate("window.__fontList = document.querySelector('[data-field=zmFont] [data-font-field=name]').list")
    for text in ("font", ""):  # two full re-renders of the field surface
        page.locator("#fieldFilter").fill(text)
    now = page.evaluate("""() => ({options: document.querySelectorAll('datalist option').length,
        connected: window.__fontList.isConnected,
        shared: [...document.querySelectorAll('[data-font-field=name]')].map(input => input.list === window.__fontList)})""")
    assert now["connected"] and now["shared"] == [True, True], "re-rendering replaced the font list instead of reusing it: " + str(now)
    assert now["options"] == len(FONT_NAMES), "the page holds " + str(now["options"]) + " font options for " + str(len(FONT_NAMES)) + " fonts"


def check_filter_actions(page, _fixture):
    # The second case is the restore button's own label cut mid-word and upper-cased: partial, in
    # another case and naming that button only. Taken from the page, it follows a rename as the
    # filter does; AUD-61 relabelled "Restore screensaver" (cut: 'RESTORE SCREEN') to the docs' name.
    label = lambda selector: " ".join(page.locator(selector).inner_text().split())
    restore, use = label("#restoreScreensaverButton"), label("#screensaverButton")
    partial = restore[:-5].upper()
    assert len(partial) > 8 and partial != partial.lower() and partial.lower() not in use.lower(), \
        "witness: " + repr(partial) + " must be a case-changed part of the restore label only: " + repr([restore, use])
    for text, shown in (("screensaver", True), (partial, True), ("cursor", True), ("palette", False), ("", True)):
        page.locator("#fieldFilter").fill(text)
        now = page.evaluate("""() => ({buttons: ['#screensaverButton', '#restoreScreensaverButton'].map(id => document.querySelector(id)?.isConnected ?? false),
            empty: Boolean(document.querySelector('.empty-hint')), grids: document.querySelectorAll('#section-launch .field-grid').length,
            fields: document.querySelectorAll('#section-launch .field').length})""")
        assert now["buttons"] == [shown, shown], "filter " + repr(text) + (" lost" if shown else " kept") + " the screensaver actions: " + str(now)
        if text in ("screensaver", partial):
            assert not now["empty"] and now["grids"] == 0, "an action-only match showed an empty hint or an empty field grid: " + str(now)
            assert page.locator("#screensaverButton").is_visible() and page.locator("#restoreScreensaverButton").is_visible()
        if text == "cursor":
            assert now["fields"] > 0, "control: 'cursor' must match a Launch field: " + str(now)


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


def check_preview_unavailable(page, fixture):
    assert not fixture["preview"]["available"], "this check needs the default fixture, which has no web assets"
    button = page.locator("#previewButton")
    title = button.get_attribute("title") or ""
    assert button.is_disabled(), "the preview button is enabled although there are no web assets"
    assert "web assets not found" in title, "the disabled preview button does not say why: " + repr(title)
    # A disabled button dispatches no click, even a programmatic one, so no click handler can explain it.
    status = page.locator("#statusLine").inner_text()
    button.evaluate("node => node.click()")
    assert page.locator("#statusLine").inner_text() == status, "a click on the disabled preview button reached a handler"


def check_preview_race(page, _fixture):
    title = page.locator("#previewButton").get_attribute("title") or ""
    assert page.locator("#previewButton").is_enabled() and "not found" not in title, "control: an available preview is reported missing: " + repr(title)
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
    from playwright.sync_api import TimeoutError as PlaywrightTimeout
    try:
        page.wait_for_function("text=>document.querySelector('#commandOutput').value.includes(text)", arg=text)
    except PlaywrightTimeout as error:
        # A bare timeout cannot tell a stale reply (an older command shown) from a failed build (empty, error status).
        raise AssertionError("the command never showed " + repr(text) + "; it shows " + repr(page.locator("#commandOutput").input_value())
                             + " with status " + repr(page.locator("#statusLine").inner_text())) from error
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


# Holds the answer to the first stripes buildCommand at the page's fetch, outside the stub, as a
# slow harness start would. The witness records how long it was held and how many builds the
# page had posted when it was let go.
HOLD_STRIPES_BUILD = """hold => {
  const realFetch = window.fetch.bind(window);
  window.__heldBuild = null;
  window.fetch = async (url, init) => {
    const response = await realFetch(url, init);
    const message = url === '/__host' ? JSON.parse(init.body) : null;
    if (!window.__heldBuild && message?.type === 'buildCommand' && message.payload?.draft?.effect === 'stripes') {
      const witness = window.__heldBuild = {held: 0, builds: 0};
      const started = performance.now();
      await new Promise(resolve => setTimeout(resolve, hold));
      witness.held = performance.now() - started;
      witness.builds = window.__smokeMessages.filter(m => m.type === 'buildCommand').length;
    }
    return response;
  };
}"""


def check_host_reply_order(page, _fixture):
    # This holds the STUB to ConfiguratorForm's order contract; it is not a page guarantee.
    # app.js updateCommand shows whichever buildCommand reply comes last and has no stale-reply
    # guard, because the host answers buildCommand synchronously and replies in arrival order.
    # The stripes build's answer is held at the page's fetch while the pride edit posts the next
    # build, so a stub that forwards the two concurrently delivers pride first and the stale
    # stripes reply last. Concurrent forwarding once timed out the AUD-13 check above.
    hold = 1500
    builds = "window.__smokeMessages.filter(m=>m.type==='buildCommand').length"
    answered = "window.__smokeReplies.filter(r=>r.type==='buildCommand').length"
    page.wait_for_function(builds + ">0 && " + answered + "===" + builds)  # init's build has settled
    start = page.evaluate(builds)
    page.evaluate(HOLD_STRIPES_BUILD, hold)
    field(page, "effect").locator("select").select_option("stripes")
    page.wait_for_function(builds + "===" + str(start + 1))
    field(page, "effect").locator("select").select_option("pride")
    page.wait_for_function(answered + "===" + str(start + 2))
    page.wait_for_timeout(200)  # both replies received
    witness = page.evaluate("window.__heldBuild")
    assert witness and witness["held"] >= hold - 50, "witness: the stripes build's answer was not held back: " + str(witness)
    assert witness["builds"] == start + 2, "witness: the pride build was not posted while the stripes answer was held: " + str(witness)
    command = page.locator("#commandOutput").input_value()
    assert "--effect pride" in command, "a slow earlier command reply replaced the latest edit's command: " + command


EL_LITERAL = re.compile(r"^const el = \{(.*?)^\};", re.M | re.S)
EL_KEY = re.compile(r"^\s*([A-Za-z_$][\w$]*)\s*:", re.M)
EL_USE = re.compile(r"(?<![\w$.])el\b(?:\??\.([A-Za-z_$][\w$]*))?")


def check_el_lookups(page, _fixture):
    # R2-16: each element app.js looks up in its el literal has a reader, so a lookup left behind
    # when its last reader goes (AUD-45 dropped el.groupNav and missed el.previewPane) fails here.
    # el is module scoped and read as el.KEY; any other use (el[name], a spread, el passed on)
    # reads keys this scan cannot see, so it fails too. The served app.js is scanned, so an
    # --app-js mutant is what is read.
    source = page.evaluate("fetch('js/app.js').then(response => response.text())")
    literal = EL_LITERAL.search(source)
    assert literal, "witness: app.js has no 'const el = {' literal to scan"
    keys = EL_KEY.findall(literal.group(1))
    assert len(keys) >= 30 and len(set(keys)) == len(keys), "witness: the el literal scan found " + str(len(keys)) + " keys: " + str(keys)
    reads, dynamic = set(), []
    for match in EL_USE.finditer(source):
        if literal.start() <= match.start() < literal.end():
            continue
        if match.group(1):
            reads.add(match.group(1))
        else:
            dynamic.append(source.count("\n", 0, match.start()) + 1)
    assert not dynamic, "app.js uses el other than as el.KEY at line(s) " + str(dynamic) + ", so its reads cannot be counted"
    unread = [key for key in keys if key not in reads]
    assert not unread, "el looks up elements nothing reads: " + ", ".join(unread)
    unknown = sorted(reads - set(keys))
    assert not unknown, "app.js reads el keys the literal does not look up: " + ", ".join(unknown)


def check_controls_switch_bloom(page, _fixture):
    # R2-15: legacy UC3D draws the catalog bloom (0.7) and Original draws 0 (effectiveDefaultsFor,
    # EffectiveDefault), so a Control units switch moves an untouched Bloom strength as a scene,
    # version or effect switch does, and the real C# builder leaves it out in both modes.
    bloom = field(page, "bloomStrength").locator('input[type="number"]')
    controls = field(page, "uc3dControls").locator("select")

    def now():
        page.wait_for_timeout(400)  # past the save debounce, so the snapshot cancels no pending save
        draft = page.evaluate("configHost.snapshotDraft().draft")
        command = run_host({"type": "buildCommand", "payload": {"draft": draft, "includeDefaults": False, "forTest": False}})["command"]
        return {"bloom": draft["bloomStrength"], "editor": bloom.input_value(), "command": command}

    def expect(label, value, sent=None):
        state = now()
        token = "--bloomStrength" in state["command"]
        assert state["bloom"] == value and state["editor"] == str(value) and token == (sent is not None), \
            label + ": Bloom strength should be " + str(value) + (", sent" if sent else ", left out") + ": " + str(state)
        if sent:
            assert "--bloomStrength " + sent in state["command"], label + ": " + state["command"]

    apply_import(page, "MatrixDesktop.exe --scene uc3d --uc3dControls legacy")
    expect("witness: the legacy UC3D import", 0.7)
    for mode, value in (("original", 0), ("legacy", 0.7)):
        controls.select_option(mode)
        expect("an imported legacy draft switched to " + mode, value)
    page.locator("#newPresetButton").click()
    field(page, "scene").locator("select").select_option("uc3d")
    expect("witness: a new Matrix draft switched to UC3D", 0)
    controls.select_option("legacy")
    expect("a new draft on UC3D switched to legacy", 0.7)
    # Control: a Bloom strength the user chose stays chosen across the switch, and is sent.
    bloom.fill("0.3")
    bloom.blur()
    controls.select_option("original")
    expect("control: a chosen Bloom strength switched to original", 0.3, sent="0.3")


def check_select_typeahead(page, _fixture):
    # R2-14: Chromium keeps a select's type-ahead text on the element, and a gating select's change
    # re-renders the field surface. A rebuilt select took each keystroke afresh, so "mo" in Version
    # picked Mega City, then Operator, and moved every untouched editor to Operator's values.
    assert page.evaluate("typeof Element.prototype.moveBefore") == "function", \
        "witness: this Chromium has no Node.moveBefore, which app.js needs to keep a focused select across a re-render"
    columns = field(page, "numColumns").locator('input[type="number"]')

    def type_into(key, start, text):
        select = field(page, key).locator("select")
        select.select_option(start)
        select.focus()
        page.keyboard.type(text, delay=60)
        now = focused(page)
        page.wait_for_timeout(1200)  # past Chromium's one-second type-ahead window, so the next case starts clean
        return now["value"] if (now["field"], now["tag"]) == (key, "SELECT") else "focus elsewhere: " + str(now)

    # Control: a repeated letter cycles from the selection, which a fresh buffer does as well, so a
    # failure below is the buffer and not keystrokes missing the select.
    now = type_into("version", "classic", "mm")
    assert now == "morpheus", "control: 'mm' in Version gave " + now
    now = type_into("version", "classic", "mo")
    assert now == "morpheus", "typing 'mo' in Version picked " + now + ", not morpheus"
    assert columns.input_value() == "60" and saved(page)["version"] == "morpheus", \
        "Version morpheus shows Columns " + columns.input_value() + ", the renderer draws 60"
    now = type_into("effect", "palette", "im")
    assert now == "image", "typing 'im' in Effect picked " + now + ", not image"


def css_color(text):
    # "rgb(r, g, b)" or "rgba(r, g, b, a)" as computed by Chromium: ([r, g, b], a).
    numbers = [float(part) for part in re.findall(r"[\d.]+", text)]
    return numbers[:3], numbers[3] if len(numbers) > 3 else 1.0


def contrast(first, second):
    def luminance(rgb):
        linear = [value / 255 / 12.92 if value / 255 <= 0.04045 else ((value / 255 + 0.055) / 1.055) ** 2.4 for value in rgb]
        return 0.2126 * linear[0] + 0.7152 * linear[1] + 0.0722 * linear[2]
    high, low = sorted((luminance(first), luminance(second)), reverse=True)
    return (high + 0.05) / (low + 0.05)


SLIDER_FOCUS = """selector => { const node = document.querySelector('.color-popover ' + selector), style = getComputedStyle(node);
    return {focused: document.activeElement === node && node.matches(':focus-visible'), outlineStyle: style.outlineStyle,
            outlineWidth: parseFloat(style.outlineWidth), outlineColor: style.outlineColor, boxShadow: style.boxShadow,
            background: getComputedStyle(node.closest('.color-popover')).backgroundColor}; }"""


def check_slider_focus_ring(page, _fixture):
    # R2-42: the popover's square and hue bar take keyboard focus. With no rule of their own they
    # drew the UA ring (outline: auto, rgb(16, 16, 16)), about 1.2:1 on the dark popover, so a
    # keyboard user could not see which one had focus. The ring must be the page's own (an outline
    # other than auto, or a zero-offset spread box-shadow) and reach 3:1 on the popover in both themes.
    swatch = field(page, "cursorColor").locator(".color-swatch")
    for theme in ("dark", "light"):
        if theme == "light":
            page.locator("#themeButton").click()
        assert page.evaluate("document.documentElement.dataset.theme") == theme, "witness: the page is not on the " + theme + " theme"
        swatch.evaluate("node => node.scrollIntoView({block: 'center'})")
        swatch.focus()
        page.keyboard.press("Enter")
        page.wait_for_timeout(150)  # the popover focuses its hex box on the next frame
        for selector in (".cp-hue", ".cp-square"):  # Shift+Tab from the hex box, then from the hue bar
            page.keyboard.press("Shift+Tab")
            style = page.evaluate(SLIDER_FOCUS, selector)
            assert style["focused"], "witness: Shift+Tab did not give " + selector + " keyboard focus: " + str(style)
            background = css_color(style["background"])[0]
            shadow = re.match(r"(rgba?\([^)]*\)) 0px 0px 0px [1-9]", style["boxShadow"])
            if style["outlineStyle"] not in ("none", "auto") and style["outlineWidth"] > 0:
                ring = style["outlineColor"]
            elif shadow:
                ring = shadow.group(1)
            else:
                raise AssertionError(theme + " theme: " + selector + " has no focus ring of the page's own: " + str(style))
            rgb, alpha = css_color(ring)
            seen = [channel * alpha + under * (1 - alpha) for channel, under in zip(rgb, background)]
            ratio = contrast(seen, background)
            assert ratio >= 3, theme + " theme: the " + selector + " focus ring " + ring + " is " + str(round(ratio, 2)) + ":1 on the popover " + style["background"] + ", under 3:1"
        page.keyboard.press("Escape")


POPOVER_BOXES = """() => { const box = document.querySelector('.color-popover').getBoundingClientRect();
    let worst = {over: 0, node: ''}, measured = 0;
    for (const node of document.querySelectorAll('.color-popover *')) {
        const r = node.getBoundingClientRect();
        if (!r.width && !r.height) continue;
        measured++;
        const over = Math.max(box.left - r.left, box.top - r.top, r.right - box.right, r.bottom - box.bottom);
        if (over > worst.over) worst = {over: Math.round(over * 10) / 10, node: node.tagName + '.' + node.className};
    }
    return {width: box.width, measured, ...worst}; }"""


def check_popover_fits(page, _fixture):
    # R2-43: every part of the colour popover lies inside its box (1 px allowed), at the
    # configurator's default window (ConfiguratorForm ClientSize 1240x820) and at 150% zoom.
    swatch = field(page, "cursorColor").locator(".color-swatch")
    for width, height in ((1240, 820), (827, 547)):
        page.set_viewport_size({"width": width, "height": height})
        swatch.evaluate("node => node.scrollIntoView({block: 'center'})")
        swatch.focus()  # opened by keyboard: at 827x547 the sticky section title covers a centred swatch
        page.keyboard.press("Enter")
        page.wait_for_function("!document.querySelector('.color-popover').hidden")
        now = page.evaluate(POPOVER_BOXES)
        assert now["measured"] >= 8 and now["width"] > 100, "witness: the open popover has too little to measure: " + str(now)
        assert now["over"] <= 1, "at " + str(width) + "x" + str(height) + " the popover's " + now["node"] + " overflows its box by " + str(now["over"]) + " px"
        page.keyboard.press("Escape")


def sorted_keys(value):
    # A store after a close and reopen (R2-44): every object's keys sorted, so a colour is {b, g, r}
    # and a palette stop {at, b, g, r}.
    if isinstance(value, dict):
        return {key: sorted_keys(value[key]) for key in sorted(value)}
    return [sorted_keys(item) for item in value] if isinstance(value, list) else value


def check_sorted_store_retarget(page, fixture):
    # R2-45: rememberDraftEdits and the switch re-target compared values as JSON strings, so a
    # default colour or palette whose keys came back in another order counted as an edit and kept
    # the old version's value on a version switch.
    operator = fixture["metadata"]["versionDefaults"]["operator"]
    stored = dict(fixture["state"], selectedPresetId=None, lastDraft=sorted_keys(fixture["defaultDraft"]))
    fresh, errors = relaunch(page, fixture, stored)
    try:
        keys = fresh.evaluate("(({cursorColor, palette}) => [Object.keys(cursorColor), Object.keys(palette[0])])(configHost.snapshotDraft().draft)")
        assert keys == [["b", "g", "r"], ["at", "b", "g", "r"]], "witness: the reopened draft does not hold sorted keys: " + str(keys)
        field(fresh, "version").locator("select").select_option("operator")
        draft = saved(fresh)
        assert draft["numColumns"] == operator["numColumns"], "control: Version operator did not move untouched Columns: " + str(draft["numColumns"])
        kept = [key + "=" + json.dumps(draft[key])[:60] for key in ("cursorColor", "palette") if draft[key] != operator[key]]
        assert not kept, "untouched editors stored with sorted keys kept the classic values on operator: " + "; ".join(kept)
        shown = field(fresh, "cursorColor").locator('input[type="number"]').evaluate_all("nodes => nodes.map(node => Number(node.value))")
        assert shown == [operator["cursorColor"][channel] for channel in "rgb"], "the cursor editor shows " + str(shown)
        assert not errors, "; ".join(errors)
    finally:
        fresh.close()


CHECKS = [
    ("Effect and version switches keep untouched editors on what renders", check_effective_defaults),
    ("Stub replies in the host's arrival order, so a slow earlier build never replaces the latest command", check_host_reply_order),
    ("ZMatrix catalog renders every structured control", check_catalog),
    ("ZMatrix editor changes survive real C# command import", check_editors),
    ("ZMatrix scene and cleanup dependencies retain disabled values", check_gating),
    ("ZMatrix CFG import preserves launch choices and reports migration", check_cfg),
    ("CFG chooser retains selected file text and cancellation", check_cfg_chooser),
    ("Late picker callbacks preserve disabled palette and stripe saved values", check_late_picker),
    ("Colour popover closes when the editor re-renders", check_picker_closes_on_rerender),
    ("Hex boxes refuse invalid colours and read text as the popover does", check_invalid_hex),
    ("Configurator keeps keyboard focus across re-render", check_focus_rerender),
    ("Phrase editor shows and counts each saved phrase once", check_phrase_rows),
    ("Keyboard focus moves into and back out of the popover, help and section jumps", check_keyboard_focus),
    ("Field controls have accessible names and their labels operate them", check_accessible_names),
    ("Font-name boxes share one installed-font list across re-renders", check_font_list),
    ("Filter finds the screensaver actions by their own text", check_filter_actions),
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
    ("Preview button says why it is disabled without web assets", check_preview_unavailable),
    ("Every element app.js looks up has a reader", check_el_lookups),
    ("A Control units switch keeps an untouched Bloom strength on what renders", check_controls_switch_bloom),
    ("Typing a name in a gating select picks the option it spells", check_select_typeahead),
    ("Colour popover sliders show a visible keyboard focus ring in both themes", check_slider_focus_ring),
    ("Colour popover content stays inside its box", check_popover_fits),
    ("A reopened store's sorted colour keys still follow a version switch", check_sorted_store_retarget),
]
PRESET_CHECKS = {check_rename, check_scratch_draft, check_pending_save_delete, check_pending_save_save_as, check_settled_save}


def main():
    # A failure message can quote a glyph (a button named only by an arrow). A redirected cp1252
    # stream must escape it rather than crash the harness and skip the remaining checks.
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(errors="backslashreplace")
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
    picker_source = CONFIGURATOR / "js" / "colorPicker.js"
    print("PICKER", picker_source, "sha256=" + hashlib.sha256(picker_source.read_bytes()).hexdigest(), flush=True)
    page_source = CONFIGURATOR / "index.html"
    print("PAGE", page_source, "sha256=" + hashlib.sha256(page_source.read_bytes()).hexdigest(), flush=True)
    styles_source = CONFIGURATOR / "styles.css"
    print("STYLES", styles_source, "sha256=" + hashlib.sha256(styles_source.read_bytes()).hexdigest(), flush=True)
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
                if function is check_font_list:
                    page_fixture["fonts"] = FONT_NAMES
                page.add_init_script(STUB.replace("__STATE__", json.dumps(page_fixture, ensure_ascii=False)))
                try:
                    page.goto(origin + "/index.html", wait_until="load")
                    page.wait_for_function("document.querySelectorAll('.field').length>40")
                    assert page.request.get(origin + "/js/app.js").body() == source.read_bytes(), "served source witness mismatch"
                    assert page.request.get(origin + "/js/colorPicker.js").body() == picker_source.read_bytes(), "served colorPicker witness mismatch"
                    assert page.request.get(origin + "/index.html").body() == page_source.read_bytes(), "served index.html witness mismatch"
                    assert page.request.get(origin + "/styles.css").body() == styles_source.read_bytes(), "served styles.css witness mismatch"
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
