"""Real WebView2 configurator probe with exact portable-store preservation."""
import argparse
import ctypes
from ctypes import wintypes
import hashlib
import importlib.util
import io
import json
import time
import winreg
from pathlib import Path
from urllib.parse import quote

from PIL import Image
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parent.parent
ARTIFACTS = ROOT / "artifacts" / "native-zmatrix-configurator"
spec = importlib.util.spec_from_file_location("native_uc3d_helper", ROOT / "tests" / "native-uc3d-smoke.py")
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)
native.USER32.EnumChildWindows.argtypes = [wintypes.HWND, native.CALLBACK, wintypes.LPARAM]
native.USER32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
native.USER32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
native.USER32.GetDlgItem.argtypes = [wintypes.HWND, ctypes.c_int]
native.USER32.GetDlgItem.restype = wintypes.HWND
native.USER32.GetParent.argtypes = [wintypes.HWND]
native.USER32.GetParent.restype = wintypes.HWND
native.USER32.IsWindowVisible.argtypes = [wintypes.HWND]
native.USER32.GetDlgCtrlID.argtypes = [wintypes.HWND]
native.USER32.SendMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
native.USER32.SendMessageW.restype = wintypes.LPARAM
native.USER32.SendMessageTimeoutW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM,
    wintypes.UINT, wintypes.UINT, ctypes.POINTER(ctypes.c_size_t)]
native.USER32.SendMessageTimeoutW.restype = wintypes.LPARAM
KERNEL32 = ctypes.WinDLL("kernel32", use_last_error=True)
KERNEL32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
KERNEL32.OpenProcess.restype = wintypes.HANDLE
KERNEL32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
KERNEL32.WaitForSingleObject.restype = wintypes.DWORD
KERNEL32.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
KERNEL32.CloseHandle.argtypes = [wintypes.HANDLE]


def choose_cfg_file(page, process, path):
    """Drive only this probe's native OpenFileDialog, retaining host text decoding."""
    def text(hwnd, class_name=False):
        value = ctypes.create_unicode_buffer(512)
        reader = native.USER32.GetClassNameW if class_name else native.USER32.GetWindowTextW
        reader(hwnd, value, len(value))
        return value.value

    def owned(hwnd):
        process_id = wintypes.DWORD()
        native.USER32.GetWindowThreadProcessId(hwnd, ctypes.byref(process_id))
        return process_id.value == process.pid

    page.locator("#importFileButton").click()
    deadline = time.monotonic() + 15
    dialog = None
    while time.monotonic() < deadline:
        matches = []
        def visit(hwnd, _):
            if owned(hwnd) and text(hwnd, True) == "#32770" and text(hwnd) == "Choose a ZMatrix configuration":
                matches.append(hwnd)
            return True
        native.USER32.EnumWindows(native.CALLBACK(visit), 0)
        assert len(matches) <= 1, "the owned file chooser is ambiguous"
        if matches:
            dialog = matches[0]
            break
        page.wait_for_timeout(50)
    assert dialog, "the owned native CFG chooser did not appear"
    edits = []
    open_button = None
    ready = False
    while time.monotonic() < deadline:
        edits = []
        def child(hwnd, _):
            if text(hwnd, True) != "Edit" or not native.USER32.IsWindowVisible(hwnd) or not owned(hwnd):
                return True
            parent = native.USER32.GetParent(hwnd)
            filename_ancestor = False
            while parent and parent != dialog:
                filename_ancestor |= text(parent, True) in ("ComboBox", "ComboBoxEx32") and native.USER32.GetDlgCtrlID(parent) == 1148
                parent = native.USER32.GetParent(parent)
            if parent == dialog and filename_ancestor:
                edits.append(hwnd)
            return True
        native.USER32.EnumChildWindows(dialog, native.CALLBACK(child), 0)
        open_button = native.USER32.GetDlgItem(dialog, 1)
        if len(edits) == 1 and open_button and text(open_button, True) == "Button" and native.USER32.IsWindowVisible(open_button):
            ready = True
            break
        page.wait_for_timeout(50)
    assert ready and len(edits) == 1, "the native chooser must expose one unambiguous filename edit under control1148 and its Open button"
    assert open_button and owned(dialog) and owned(edits[0]) and owned(open_button), "refusing a file action outside the probe's exact PID"
    value = ctypes.create_unicode_buffer(str(path.resolve()))
    native.USER32.SendMessageW(edits[0], 0x000C, 0, ctypes.addressof(value))  # WM_SETTEXT
    native.USER32.PostMessageW(open_button, 0x00F5, 0, 0)  # BM_CLICK


def close_edit_roundtrip(playwright, exe, process, browser, page, renderer):
    """Close before the normal debounce, then verify the synthetic portable store."""
    page.wait_for_timeout(400)
    proof = page.evaluate("""() => {
        const before=window.__nativeRequests.filter(message=>message.type==='saveDraft').length;
        // Only the native closing snapshot may persist this final synthetic edit.
        // A slow WM_CLOSE delivery must not let the ordinary debounce mask a bug.
        const post=chrome.webview.postMessage.bind(chrome.webview);
        window.__closingSuppressedSaves=[];
        chrome.webview.postMessage=message=>{
            if(message.type==='saveDraft') { window.__closingSuppressedSaves.push(structuredClone(message));return; }
            post(message);
        };
        const input=document.querySelector('[data-field="zmMaxStream"] input[type=number]');
        input.value='2345';input.dispatchEvent(new Event('input',{bubbles:true}));
        return {before,after:window.__nativeRequests.filter(message=>message.type==='saveDraft').length};
    }""")
    assert proof["before"] == proof["after"], "the final edit must still be pending before native close"
    native.close_window(process)
    browser.close()
    stored = json.loads((exe.parent / "MatrixDesktopConfigurator.presets.json").read_text(encoding="utf-8-sig"))
    assert stored["lastDraft"]["zmMaxStream"] == 2345, "native close lost the last field edit before its debounce"
    reopened_process, reopened_browser = native.open_application(playwright, exe, [], "zmatrix-close-reopen-" + renderer)
    try:
        reopened_page = native.page_at(reopened_browser, "https://configurator.local/")
        reopened_page.wait_for_function("document.querySelector('[data-field=\"zmMaxStream\"] input[type=number]')?.value==='2345'")
        native.close_window(reopened_process)
    finally:
        native.stop_application(reopened_process)
        reopened_browser.close()
    return {"pendingBeforeClose": True, "ordinarySaveRouteBlocked": True, "storedAndReloadedValue": 2345, "reopenedPid": reopened_process.pid}


def field(page, key):
    return page.locator('[data-field="' + key + '"]')


def registry_snapshot():
    result = {}
    for key, names in (
        (r"Control Panel\Desktop", ("SCRNSAVE.EXE", "ScreenSaveActive")),
        (r"Software\Microsoft\Windows\CurrentVersion\Run", ("MatrixDesktop",)),
        (r"Software\MatrixDesktop\ScreensaverBackup", ("Saved", "RegisteredPath", "RuntimeActive")),
    ):
        for name in names:
            try:
                with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key) as handle:
                    result[(key, name)] = winreg.QueryValueEx(handle, name)
            except FileNotFoundError:
                result[(key, name)] = None
    return result


def preview(page, minimum_generation=0, scene="zmatrix", renderer=None, query=None, font_height=None, exact_query=None):
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        for frame in page.frames:
            if frame.url.startswith("https://matrix-preview.local/"):
                try:
                    ready = frame.evaluate("""({generation,scene,renderer,query,fontHeight,exactQuery}) => window.__matrixRuntime?.ready===true
                        && window.__matrixRuntime.scene===scene
                        && (!renderer||window.__matrixRuntime.backend===renderer)
                        && Object.entries(query||{}).every(([key,value])=>new URLSearchParams(location.search).get(key)===value)
                        && (exactQuery===null||location.search===exactQuery)
                        && (fontHeight===null||window.matrixHostBootstrap?.zmatrixFonts?.normal?.requestedFont?.height===fontHeight)
                        && (scene!=='zmatrix'||window.__matrixRuntime.resourceGeneration>=generation)""",
                        {"generation": minimum_generation, "scene": scene, "renderer": renderer, "query": query, "fontHeight": font_height, "exactQuery": exact_query})
                    if ready:
                        return frame
                except Exception:
                    pass  # Reloads replace the iframe execution context.
        page.wait_for_timeout(100)
    raise AssertionError("A ready native preview did not appear for " + scene)


def inspect(frame):
    result = frame.evaluate("""() => ({runtime:window.__matrixRuntime.inspect(),errors:window.__matrixRuntime.errors,warnings:window.__matrixRuntime.warnings,
        fonts:window.matrixHostBootstrap.zmatrixFonts,wallpaper:window.matrixHostBootstrap.wallpaper})""")
    assert result["errors"] == [], "native preview reported renderer errors"
    for role in ("normal", "special"):
        assert result["runtime"]["nativeFonts"][role]["source"] == "native-gdi", role + " silently used a browser font"
        assert result["fonts"][role]["pages"], role + " native atlas has no resource pages"
    return result


def latest_large_preview(page, renderer, phrases):
    expected = {"renderer": renderer, "phrases": phrases, "characters": list("1234567890ABCDEFGHIJKLMNOPQRSUTVWXYZabcd")}
    page.wait_for_function("""expected => {
        const request=window.__nativeRequests.filter(message=>message.type==='buildWebQuery').at(-1);
        const draft=request?.payload?.draft;
        if(!draft||draft.scene!=='zmatrix'||draft.renderer!==expected.renderer
            || JSON.stringify(draft.zmPhrases)!==JSON.stringify(expected.phrases)
            || JSON.stringify(draft.zmCharacters)!==JSON.stringify(expected.characters)
            || draft.zmFont.height!==-7||draft.zmFont.name!=='Matrix Code Font'
            || draft.zmSpecialFont.height!==-8||draft.zmSpecialFont.name!=='Terminal') return false;
        return window.__nativeReplies.some(reply=>reply.id===request.id&&reply.ok&&typeof reply.payload?.query==='string');
    }""", arg=expected)
    query = page.evaluate("""() => {
        const request=window.__nativeRequests.filter(message=>message.type==='buildWebQuery').at(-1);
        return window.__nativeReplies.find(reply=>reply.id===request.id).payload.query;
    }""")
    frame = preview(page, renderer=renderer, exact_query=query,
        query={"zmPhrases": json.dumps(phrases, ensure_ascii=False, separators=(",", ":"))})
    result = inspect(frame)
    normal = [item["text"] for item in result["fonts"]["normal"]["glyphs"]]
    special = [item["text"] for item in result["fonts"]["special"]["glyphs"]]
    assert normal == expected["characters"], "latest large preview retained the prior CFG character repertoire"
    assert set(special) == set("".join(phrases)), "latest large preview omitted phrase glyphs"
    assert not any("omitted requested characters" in warning.lower() for warning in result["warnings"]), "latest large preview accepted font metadata for an obsolete configuration"
    return frame, result


def reject_obsolete_configuration(page, frame, obsolete):
    current = frame.evaluate("structuredClone(window.matrixHostBootstrap)")
    assert current["configuration"] == frame.evaluate("location.search"), "current native bootstrap does not identify the displayed configuration"
    frame.evaluate("""() => {
        window.__bootstrapProbeMessages=[];
        window.addEventListener('message',event=>{
            if(event.source===parent&&event.origin==='https://configurator.local'&&event.data?.probeToken)
                window.__bootstrapProbeMessages.push(event.data.probeToken);
        });
    }""")

    def deliver(payload, token):
        page.evaluate("""({payload,token})=>document.querySelector('#previewFrame').contentWindow.postMessage(
            {type:'matrix-native-resources',bootstrap:payload,probeToken:token},'https://matrix-preview.local')""",
            {"payload": payload, "token": token})
        frame.wait_for_function("token=>window.__bootstrapProbeMessages.includes(token)", arg=token)

    matching = json.loads(json.dumps(current))
    matching["nativeGeneration"] += 1
    deliver(matching, "matching-current-query")
    frame.wait_for_function("generation=>window.matrixHostBootstrap.nativeGeneration===generation", arg=matching["nativeGeneration"])
    wrong = json.loads(json.dumps(obsolete))
    wrong["nativeGeneration"] = matching["nativeGeneration"] + 100
    assert wrong["configuration"] != current["configuration"], "the obsolete-configuration witness must differ from the current query"
    before_frames = frame.evaluate("window.__matrixRuntime.frames")
    deliver(wrong, "newer-obsolete-query")
    accepted = frame.evaluate("({configuration:window.matrixHostBootstrap.configuration,generation:window.matrixHostBootstrap.nativeGeneration})")
    assert accepted == {"configuration": current["configuration"], "generation": matching["nativeGeneration"]}, "newer mismatched bootstrap changed current preview resources"
    frame.wait_for_function("before=>window.__matrixRuntime.frames>before+2", arg=before_frames)
    result = inspect(frame)
    assert result["runtime"]["resourceGeneration"] == current["zmatrixFonts"]["generation"], "an obsolete query replaced the current native font generation"
    assert not any("omitted requested characters" in warning.lower() for warning in result["warnings"]), "an obsolete native payload caused a repertoire fallback"
    return {"matchingPayloadAccepted": matching["nativeGeneration"], "newerWrongQueryRejected": wrong["nativeGeneration"]}


def source_witness(page, frame):
    witnesses = {}
    for context, relative, source in (
        (page, "/js/app.js", ROOT / "MatrixDesktopConfigurator/configurator/js/app.js"),
        (frame, "/js/config.js", ROOT / "MatrixDesktop/web/js/config.js"),
        (frame, "/js/main.js", ROOT / "MatrixDesktop/web/js/main.js"),
        (frame, "/js/hostPlayback.js", ROOT / "MatrixDesktop/web/js/hostPlayback.js"),
        (frame, "/js/zmatrix/settings.js", ROOT / "MatrixDesktop/web/js/zmatrix/settings.js"),
        (frame, "/js/zmatrix/runtime.js", ROOT / "MatrixDesktop/web/js/zmatrix/runtime.js"),
        (frame, "/js/zmatrix/fonts.js", ROOT / "MatrixDesktop/web/js/zmatrix/fonts.js"),
    ):
        actual = context.evaluate("""async path => {
            const response=await fetch(path,{cache:'no-store'});
            if(!response.ok) throw Error('Source witness HTTP '+response.status+' '+path);
            return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',await response.arrayBuffer())),
                byte=>byte.toString(16).padStart(2,'0')).join('');
        }""", relative)
        expected = hashlib.sha256(source.read_bytes()).hexdigest()
        assert actual == expected, "deployed source witness mismatch: " + str(source.relative_to(ROOT))
        witnesses[str(source.relative_to(ROOT))] = actual
    return witnesses


def visible_rain(frame):
    frame.evaluate("window.__matrixRuntime.step(20)")
    before = frame.evaluate("window.__matrixRuntime.frames")
    frame.wait_for_function("before=>window.__matrixRuntime.frames>before", arg=before)
    image = Image.open(io.BytesIO(frame.locator("canvas").screenshot())).convert("RGB")
    pixels = list(image.get_flattened_data())
    assert len(set(pixels[::max(1, len(pixels)//10000)])) > 8, "native preview lacks visible rendered variation"
    return image


@native.preserve_portable_presets
def configurator_case(playwright, exe, renderer):
    before_registry = registry_snapshot()
    process, browser = native.open_application(playwright, exe, [], "zmatrix-configurator-" + renderer)
    page = None
    errors = []
    witnesses = {}
    try:
        page = native.page_at(browser, "https://configurator.local/")
        page.on("pageerror", lambda error: errors.append(str(error)))
        page.wait_for_function("document.querySelectorAll('.field').length>40")
        page.evaluate("""() => {
            window.__nativeRequests=[];window.__nativeReplies=[];
            const post=chrome.webview.postMessage.bind(chrome.webview);
            chrome.webview.postMessage=message=>{window.__nativeRequests.push(structuredClone(message));post(message);};
            const receive=configHost.receive.bind(configHost);
            configHost.receive=message=>{window.__nativeReplies.push(structuredClone(message));receive(message);};
        }""")
        page.locator("#newPresetButton").click()
        field(page, "scene").locator("select").select_option("zmatrix")
        field(page, "renderer").locator("select").select_option(renderer)
        frame = preview(page, renderer=renderer)
        witnesses = source_witness(page, frame)
        initial = inspect(frame)
        assert initial["fonts"]["normal"]["resolvedFace"] == "Matrix Code Font", "the bundled font was not privately available to the configurator preview"
        visible_rain(frame).save(ARTIFACTS / ("initial-" + renderer + ".png"))
        generation = initial["runtime"]["resourceGeneration"]
        code_font = field(page, "zmFont")
        code_font.locator("summary").click()
        code_font.locator('[data-font-field="height"]').fill("-17")
        code_font.locator('[data-font-field="height"]').blur()
        frame = preview(page, generation + 1, renderer=renderer, font_height=-17)
        edited = inspect(frame)
        assert edited["fonts"]["normal"]["requestedFont"]["height"] == -17
        assert edited["runtime"]["nativeFonts"]["normal"]["metrics"]["height"] != initial["runtime"]["nativeFonts"]["normal"]["metrics"]["height"], "font editing did not change actual GDI metrics"
        special = field(page, "zmSpecialFont")
        special.locator("summary").click()
        special.locator('[data-font-field="italic"]').check()
        frame = preview(page, edited["runtime"]["resourceGeneration"] + 1, renderer=renderer)
        assert inspect(frame)["fonts"]["special"]["requestedFont"]["italic"] is True
        field(page, "windowMode").locator("select").select_option("wallpaper")
        cfg = """[General]
ConfigDPI=144
MaxStream=777
OwnerFutureSetting=retained
[Text]
FontName=Consolas
FontHeight=-24
CharSet=A,0x42,Ω
[Colors]
BGMode=bgmodeColor
FGColor={187,255,187,255}
SpecialStringBGColor={1,2,3,255}
[SpecialText]
FontName=Consolas
FontHeight=-18
Strings=First|Second
"""
        page.locator("#importButton").click()
        cfg_path = ARTIFACTS / ("selected-" + renderer + ".cfg")
        cfg_path.write_text(cfg, encoding="utf-16")  # Native File.ReadAllText must retain BOM handling.
        choose_cfg_file(page, process, cfg_path)
        page.wait_for_function("text=>document.querySelector('#importInput').value===text", arg=cfg)
        page.locator("#applyImportButton").click()
        page.wait_for_function("document.querySelector('[data-field=\"zmMaxStream\"] input[type=number]').value==='777'")
        assert field(page, "windowMode").locator("select").input_value() == "wallpaper", "native CFG import discarded the selected launch mode"
        assert field(page, "zmCharacters").locator("textarea").input_value() == "ABΩ"
        assert field(page, "zmFont").locator('[data-font-field="height"]').input_value() == "-16"
        field(page, "renderer").locator("select").select_option(renderer)
        frame = preview(page, renderer=renderer, query={"zmMaxStream": "777"}, font_height=-16)
        imported = inspect(frame)
        cfg_bootstrap = frame.evaluate("structuredClone(window.matrixHostBootstrap)")
        assert imported["fonts"]["normal"]["resolvedFace"] == "Consolas"
        assert [item["text"] for item in imported["fonts"]["normal"]["glyphs"]] == ["A", "B", "Ω"], "native atlas missed imported characters"
        assert page.locator(".import-report").count() == 1
        visible_rain(frame).save(ARTIFACTS / ("imported-" + renderer + ".png"))
        field(page, "scene").locator("select").select_option("uc3d")
        preview(page, scene="uc3d")
        field(page, "scene").locator("select").select_option("zmatrix")
        frame = preview(page, renderer=renderer, query={"zmMaxStream": "777"}, font_height=-16)
        assert field(page, "zmMaxStream").locator('input[type="number"]').input_value() == "777"
        assert field(page, "zmFont").locator('[data-font-field="height"]').input_value() == "-16"
        page.locator("#testButton").click()
        page.wait_for_function("window.__nativeReplies.some(m=>m.ok&&m.payload?.processId)")
        launched = page.evaluate("window.__nativeReplies.findLast(m=>m.ok&&m.payload?.processId).payload")
        assert "--windowed" in launched["command"] and "--wallpaper" not in launched["command"], "native Test Argument attempted wallpaper hosting"
        page.locator("#stopButton").click()
        page.wait_for_function("document.querySelector('#stopButton').disabled")
        phrases = [str(index) + "Ω" * 250 for index in range(64)]
        page.locator("#importButton").click()
        page.locator("#importInput").fill("?scene=zmatrix&zmPhrases=" + quote(json.dumps(phrases, ensure_ascii=False)))
        page.locator("#applyImportButton").click()
        page.wait_for_function("document.querySelector('[data-field=\"zmPhrases\"]')?.querySelectorAll('.phrase-row').length===64")
        field(page, "renderer").locator("select").select_option(renderer)
        page.wait_for_function("document.querySelector('#commandOutput').value==='' && document.querySelector('#statusLine').textContent.includes('32,767')")
        launches_before = page.evaluate("window.__nativeReplies.filter(m=>m.ok&&m.payload?.processId).length")
        page.locator("#testButton").click()
        page.wait_for_function("count=>window.__nativeReplies.filter(m=>m.ok&&m.payload?.processId).length>count", arg=launches_before)
        large_launch = page.evaluate("window.__nativeReplies.findLast(m=>m.ok&&m.payload?.processId).payload")
        assert "direct structured launch" in large_launch["command"], "a valid direct launch failed while reporting an oversized copied command"
        assert page.locator("#stopButton").is_enabled()
        page.locator("#stopButton").click()
        page.wait_for_function("document.querySelector('#stopButton').disabled")
        frame, final_preview = latest_large_preview(page, renderer, phrases)
        for role in ("normal", "special"):
            assert final_preview["runtime"]["nativeFonts"][role]["metrics"] == initial["runtime"]["nativeFonts"][role]["metrics"], role + " font did not return to its default GDI metrics"
        payload_identity = reject_obsolete_configuration(page, frame, cfg_bootstrap)
        visible_rain(frame).save(ARTIFACTS / ("large-" + renderer + ".png"))
        requests = page.evaluate("window.__nativeRequests.map(message=>message.type)")
        assert "useScreensaver" not in requests and "restoreScreensaver" not in requests
        assert registry_snapshot() == before_registry, "preview/import/Test changed screensaver or autostart registry values"
        assert not errors, "configurator page errors: " + "; ".join(errors)
        page.screenshot(path=str(ARTIFACTS / ("configurator-" + renderer + ".png")))
        final_preview_url = frame.url
        closing_page = page
        page = None
        close_proof = close_edit_roundtrip(playwright, exe, process, browser, closing_page, renderer)
        report = {"renderer": renderer, "normalSource": imported["runtime"]["nativeFonts"]["normal"]["source"],
            "specialSource": imported["runtime"]["nativeFonts"]["special"]["source"],
            "initialGeneration": generation, "editedGeneration": edited["runtime"]["resourceGeneration"],
            "normalMetricsBefore": initial["runtime"]["nativeFonts"]["normal"]["metrics"],
            "normalMetricsAfter": edited["runtime"]["nativeFonts"]["normal"]["metrics"],
            "importedGlyphCount": len(imported["fonts"]["normal"]["glyphs"]), "testPid": launched["processId"],
            "largeDirectTestPid": large_launch["processId"], "payloadIdentity": payload_identity, "finalPreview": {"url": final_preview_url,
                "resourceGeneration": final_preview["runtime"]["resourceGeneration"], "warnings": final_preview["warnings"],
                "normalSource": final_preview["runtime"]["nativeFonts"]["normal"]["source"],
                "specialSource": final_preview["runtime"]["nativeFonts"]["special"]["source"],
                "normalGlyphCount": len(final_preview["fonts"]["normal"]["glyphs"]),
                "specialGlyphCount": len(final_preview["fonts"]["special"]["glyphs"])},
            "closeEditProof": close_proof, "registryPreserved": True, "errors": errors, "sourceWitnesses": witnesses}
        (ARTIFACTS / ("report-" + renderer + ".json")).write_text(json.dumps(report, indent=2), encoding="utf-8")
        print("PASS native ZMatrix configurator", renderer, ": GDI atlases, font refresh, CFG import, scene retention, windowed Test, registry unchanged", flush=True)
    except BaseException:
        if page is not None:
            diagnostics = {"status": page.locator("#statusLine").inner_text(), "command": page.locator("#commandOutput").input_value(),
                "previewUrl": page.locator("#previewFrame").get_attribute("src"), "sourceWitnesses": witnesses, "frames": []}
            for candidate in page.frames:
                if candidate.url.startswith("https://matrix-preview.local/"):
                    try:
                        diagnostics["frames"].append(candidate.evaluate("""() => ({url:location.href,bootstrap:window.matrixHostBootstrap,
                            runtime:window.__matrixRuntime})"""))
                    except Exception as error:
                        diagnostics["frames"].append({"url": candidate.url, "error": str(error)})
            (ARTIFACTS / ("failure-" + renderer + ".json")).write_text(json.dumps(diagnostics, indent=2), encoding="utf-8")
        raise
    finally:
        try:
            if process.poll() is None:
                native.close_window(process)
        finally:
            native.stop_application(process)
            browser.close()
        assert registry_snapshot() == before_registry, "native configurator teardown changed registration state"


def second_instance_case(playwright, publish):
    """AUD-01: a second configurator on the same store exits 0; the owner and its store are untouched.

    Runs on a private copy of the published folder with a synthetic portable store, so the
    gate's own store is never involved. Removing the early exit in Program.Main makes the
    second process start a full configurator, which fails the 5 s exit requirement.
    """
    import os
    import shutil
    import subprocess
    import tempfile
    root = Path(tempfile.mkdtemp(prefix="second-instance-", dir=ARTIFACTS))
    app = root / "app"
    shutil.copytree(publish, app)
    exe = app / "MatrixDesktopConfigurator.exe"
    store = app / "MatrixDesktopConfigurator.presets.json"
    backup = store.with_name(store.name + ".bak")
    store.write_text(json.dumps({"schemaVersion": 1, "presetSeedVersion": 0, "selectedPresetId": None, "lastDraft": {},
        "userPresets": [{"id": "second-instance-owner", "name": "Owner preset", "values": {"scene": "matrix", "numColumns": 61}}],
        "uiTheme": "dark"}), encoding="utf-8")
    process, browser = native.open_application(playwright, exe, [], "second-instance-owner")
    second = None
    try:
        page = native.page_at(browser, "https://configurator.local/")
        page.wait_for_function("document.querySelectorAll('.field').length>40")
        # Startup seeding may already have saved; compare from the settled owner onward.
        page.wait_for_timeout(500)
        before = {path: path.read_bytes() if path.exists() else None for path in (store, backup)}
        assert before[store] and b"second-instance-owner" in before[store], "the owner did not load the synthetic store"
        env = os.environ.copy()
        env["WEBVIEW2_USER_DATA_FOLDER"] = str(root / "second-profile")
        second = subprocess.Popen([str(exe)], env=env, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            second.wait(timeout=5)
        except subprocess.TimeoutExpired:
            raise AssertionError("a second configurator on the same store kept running instead of exiting")
        assert second.returncode == 0, f"the refused second configurator exited {second.returncode}"
        assert process.poll() is None, "the owning configurator exited when a second one was launched"
        assert page.evaluate("document.querySelectorAll('.field').length") > 40, "the owning configurator lost its window"
        after = {path: path.read_bytes() if path.exists() else None for path in (store, backup)}
        assert after == before, "a refused second configurator changed the store or its backup"
        native.close_window(process)
        print("PASS second configurator on one store exits 0; owner window, store and backup unchanged", flush=True)
        return {"ownerPid": process.pid, "secondPid": second.pid, "secondExit": second.returncode}
    finally:
        if second is not None and second.poll() is None:
            native.stop_application(second)
        native.stop_application(process)
        browser.close()
        shutil.rmtree(root, ignore_errors=True)


def configurator_window(process):
    """The one visible top-level configurator window of exactly this process."""
    found = []
    def visit(hwnd, _):
        pid = wintypes.DWORD()
        native.USER32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value == process.pid and native.USER32.IsWindowVisible(hwnd):
            title = ctypes.create_unicode_buffer(256)
            native.USER32.GetWindowTextW(hwnd, title, len(title))
            if title.value == "MatrixDesktop Configurator":
                found.append(hwnd)
        return True
    native.USER32.EnumWindows(native.CALLBACK(visit), 0)
    assert len(found) == 1, f"expected one configurator window for pid {process.pid}, found {len(found)}"
    return found[0]


def session_message(hwnd, message, wparam, timeout_ms, require_answer=True):
    """Send like Windows does at shutdown and return the window's answer."""
    answer = ctypes.c_size_t()
    delivered = native.USER32.SendMessageTimeoutW(hwnd, message, wparam, 0, 0x0002, timeout_ms, ctypes.byref(answer))  # SMTO_ABORTIFHUNG
    assert delivered or not require_answer, f"message 0x{message:04X} was not answered within {timeout_ms} ms"
    return answer.value


def session_end_case(playwright, publish):
    """AUD-37: answer WM_QUERYENDSESSION at once, survive a cancelled shutdown, tear down on a real end.

    Private copy of the published folder with a synthetic portable store. The old close flush
    cancelled the query (answer 0, so Windows lists the configurator as blocking shutdown) and
    then closed itself after WM_ENDSESSION(FALSE). A fix that tears down during the query fails
    the snapshotDraft leg; one whose FormClosed skips teardown leaves the Test host running.
    """
    import shutil
    import tempfile
    root = Path(tempfile.mkdtemp(prefix="session-end-", dir=ARTIFACTS))
    app = root / "app"
    shutil.copytree(publish, app)
    exe = app / "MatrixDesktopConfigurator.exe"
    store = app / "MatrixDesktopConfigurator.presets.json"
    store.write_text(json.dumps({"schemaVersion": 1, "presetSeedVersion": 0, "selectedPresetId": None, "lastDraft": {},
        "userPresets": [{"id": "session-end-owner", "name": "Session preset", "values": {"scene": "matrix", "numColumns": 62}}],
        "uiTheme": "dark"}), encoding="utf-8")
    process, browser = native.open_application(playwright, exe, [], "session-end")
    test_host = None
    try:
        page = native.page_at(browser, "https://configurator.local/")
        page.wait_for_function("document.querySelectorAll('.field').length>40")
        page.evaluate("""() => {
            window.__nativeReplies=[];
            const receive=configHost.receive.bind(configHost);
            configHost.receive=message=>{window.__nativeReplies.push(structuredClone(message));receive(message);};
        }""")
        hwnd = configurator_window(process)
        assert session_message(hwnd, 0x0011, 0, 5000) != 0, "the configurator refused WM_QUERYENDSESSION, so Windows lists it as blocking shutdown"
        session_message(hwnd, 0x0016, 0, 5000)  # WM_ENDSESSION(FALSE): the shutdown was cancelled
        page.wait_for_timeout(3000)
        assert process.poll() is None, "the configurator exited although the shutdown was cancelled"
        try:
            snapshot = page.evaluate("window.configHost.snapshotDraft()")
        except Exception as error:
            raise AssertionError("the page stopped answering snapshotDraft after a cancelled shutdown: " + str(error))
        assert isinstance(snapshot, dict) and isinstance(snapshot.get("draft"), dict), "the page stopped answering snapshotDraft after a cancelled shutdown"
        page.locator("#testButton").click()
        page.wait_for_function("window.__nativeReplies.some(m=>m.ok&&m.payload?.processId)")
        test_pid = page.evaluate("window.__nativeReplies.findLast(m=>m.ok&&m.payload?.processId).payload.processId")
        test_host = KERNEL32.OpenProcess(0x00100001, False, test_pid)  # SYNCHRONIZE | PROCESS_TERMINATE
        assert test_host, f"could not open Test host {test_pid}"
        assert KERNEL32.WaitForSingleObject(test_host, 0) == 0x102, "the Test host ended before the session did"  # WAIT_TIMEOUT
        assert session_message(hwnd, 0x0011, 0, 5000) != 0, "the second WM_QUERYENDSESSION was refused"
        # WM_ENDSESSION(TRUE): the session ends. WinForms destroys the form while handling it,
        # so only the process exit and the Test host below say whether teardown ran.
        session_message(hwnd, 0x0016, 1, 10000, require_answer=False)
        process.wait(timeout=15)
        assert process.returncode == 0, f"the configurator exited {process.returncode} after WM_ENDSESSION(TRUE)"
        assert KERNEL32.WaitForSingleObject(test_host, 5000) == 0, "WM_ENDSESSION(TRUE) did not tear down: the Test host is still running"
        assert b"session-end-owner" in store.read_bytes(), "the session end lost the saved preset"
        print("PASS session-end query answered at once; cancelled shutdown keeps the configurator; WM_ENDSESSION(TRUE) tears down", flush=True)
        return {"pid": process.pid, "testPid": test_pid}
    finally:
        if test_host:
            if KERNEL32.WaitForSingleObject(test_host, 0) != 0:
                KERNEL32.TerminateProcess(test_host, 1)
            KERNEL32.CloseHandle(test_host)
        native.stop_application(process)
        browser.close()
        shutil.rmtree(root, ignore_errors=True)


def process_windows(pid, visible_only):
    """Top-level windows of one process id."""
    found = []
    def visit(hwnd, _):
        owner = wintypes.DWORD()
        native.USER32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and (not visible_only or native.USER32.IsWindowVisible(hwnd)):
            found.append(hwnd)
        return True
    native.USER32.EnumWindows(native.CALLBACK(visit), 0)
    return found


def test_exit_case(playwright, publish):
    """AUD-43 host half: a Test host that ends by itself is reported to the page with its pid.

    A page spy stands in for configHost.testExited, so this checks the host's Exited,
    BeginInvoke and ExecuteScriptAsync wiring whatever the page half does with the notice.
    Without EnableRaisingEvents or the Exited subscription the spy is never called. A replaced
    Test host's exit notice must not clear its replacement: without the identity guard in
    OnTestExited, Stop no longer ends the current Test host.
    """
    import shutil
    import tempfile
    root = Path(tempfile.mkdtemp(prefix="test-exit-", dir=ARTIFACTS))
    app = root / "app"
    shutil.copytree(publish, app)
    exe = app / "MatrixDesktopConfigurator.exe"
    (app / "MatrixDesktopConfigurator.presets.json").write_text(json.dumps({"schemaVersion": 1, "presetSeedVersion": 0,
        "selectedPresetId": None, "lastDraft": {}, "userPresets": [], "uiTheme": "dark"}), encoding="utf-8")
    process, browser = native.open_application(playwright, exe, [], "test-exit")
    handles = []
    try:
        page = native.page_at(browser, "https://configurator.local/")
        page.wait_for_function("document.querySelectorAll('.field').length>40")
        page.evaluate("""() => {
            window.__nativeReplies=[];window.__testExited=[];
            const receive=configHost.receive.bind(configHost);
            configHost.receive=message=>{window.__nativeReplies.push(structuredClone(message));receive(message);};
            const pageHalf=configHost.testExited?.bind(configHost);
            configHost.testExited=pid=>{window.__testExited.push(pid);pageHalf?.(pid);};
        }""")

        def launch():
            count = page.evaluate("window.__nativeReplies.filter(m=>m.ok&&m.payload?.processId).length")
            page.locator("#testButton").click()
            page.wait_for_function("count=>window.__nativeReplies.filter(m=>m.ok&&m.payload?.processId).length>count", arg=count)
            pid = page.evaluate("window.__nativeReplies.findLast(m=>m.ok&&m.payload?.processId).payload.processId")
            handle = KERNEL32.OpenProcess(0x00100001, False, pid)  # SYNCHRONIZE | PROCESS_TERMINATE
            assert handle, f"could not open Test host {pid}"
            handles.append(handle)
            return pid, handle

        first, first_handle = launch()
        deadline = time.monotonic() + 15
        while not process_windows(first, visible_only=True) and time.monotonic() < deadline:
            page.wait_for_timeout(100)
        assert process_windows(first, visible_only=True), f"Test host {first} showed no window"
        for hwnd in process_windows(first, visible_only=False):
            native.USER32.PostMessageW(hwnd, 0x0010, 0, 0)  # WM_CLOSE, as native.close_window ends a windowed host
        assert KERNEL32.WaitForSingleObject(first_handle, 10000) == 0, "the Test host did not close on WM_CLOSE"
        try:
            page.wait_for_function("pid=>window.__testExited.includes(pid)", arg=first, timeout=5000)
        except Exception:
            raise AssertionError(f"the host never called configHost.testExited({first}) after the Test window closed")
        second, second_handle = launch()
        third, third_handle = launch()
        assert KERNEL32.WaitForSingleObject(second_handle, 5000) == 0, "a replaced Test host kept running"
        page.wait_for_timeout(1000)  # the replaced host's exit notice is queued behind the launch
        page.locator("#stopButton").click()
        assert KERNEL32.WaitForSingleObject(third_handle, 5000) == 0, \
            "Stop Test left the current Test host running: a replaced host's exit notice cleared it"
        assert page.evaluate("window.__testExited") == [first], "only the Test host that ended by itself may be reported"
        native.close_window(process)
        print("PASS a Test host that ends by itself is reported once; a replaced host's exit leaves Stop working", flush=True)
        return {"pid": process.pid, "testPids": [first, second, third]}
    finally:
        for handle in handles:
            if KERNEL32.WaitForSingleObject(handle, 0) != 0:
                KERNEL32.TerminateProcess(handle, 1)
            KERNEL32.CloseHandle(handle)
        native.stop_application(process)
        browser.close()
        shutil.rmtree(root, ignore_errors=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish", type=Path, default=ROOT / "artifacts" / "gate" / "win-x64-fd")
    parser.add_argument("--renderer", choices=["regl", "webgpu", "both"], default="both")
    args = parser.parse_args()
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as playwright:
        second_instance_case(playwright, args.publish)
        session_end_case(playwright, args.publish)
        test_exit_case(playwright, args.publish)
        for renderer in (["regl", "webgpu"] if args.renderer == "both" else [args.renderer]):
            configurator_case(playwright, args.publish / "MatrixDesktopConfigurator.exe", renderer)
    print("PASS portable preset and backup bytes restored by the native preservation helper.", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
