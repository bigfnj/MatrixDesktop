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
      } else if (msg.type === 'saveDraft') payload = {saved:true};
      else if (msg.type === 'useScreensaver') payload = {saved:true,message:'Synthetic screensaver action recorded.'};
      else if (msg.type === 'restoreScreensaver') payload = {restored:true,message:'Synthetic restore action recorded.'};
      window.__smokeReplies.push({type:msg.type,payload:structuredClone(payload)});
      if(msg.type==='buildWebQuery' && window.__holdPreviewReplies) {
        window.__heldPreviewReplies.push({id:msg.id,ok:true,payload});return;
      }
      setTimeout(() => window.configHost.receive({id:msg.id,ok:true,payload}),0);
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


CHECKS = [
    ("ZMatrix catalog renders every structured control", check_catalog),
    ("ZMatrix editor changes survive real C# command import", check_editors),
    ("ZMatrix scene and cleanup dependencies retain disabled values", check_gating),
    ("ZMatrix CFG import preserves launch choices and reports migration", check_cfg),
    ("Wallpaper tests stay windowed and registration requires explicit actions", check_actions),
    ("ZMatrix editor bounds reject invalid glyphs and preserve empty phrases", check_limits),
    ("Preview ignores obsolete and hidden asynchronous query replies", check_preview_race),
]


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
