"""Real WebView2 configurator probe with exact portable-store preservation."""
import argparse
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
        page.locator("#importInput").fill(cfg)
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
        report = {"renderer": renderer, "normalSource": imported["runtime"]["nativeFonts"]["normal"]["source"],
            "specialSource": imported["runtime"]["nativeFonts"]["special"]["source"],
            "initialGeneration": generation, "editedGeneration": edited["runtime"]["resourceGeneration"],
            "normalMetricsBefore": initial["runtime"]["nativeFonts"]["normal"]["metrics"],
            "normalMetricsAfter": edited["runtime"]["nativeFonts"]["normal"]["metrics"],
            "importedGlyphCount": len(imported["fonts"]["normal"]["glyphs"]), "testPid": launched["processId"],
            "largeDirectTestPid": large_launch["processId"], "payloadIdentity": payload_identity, "finalPreview": {"url": frame.url,
                "resourceGeneration": final_preview["runtime"]["resourceGeneration"], "warnings": final_preview["warnings"],
                "normalSource": final_preview["runtime"]["nativeFonts"]["normal"]["source"],
                "specialSource": final_preview["runtime"]["nativeFonts"]["special"]["source"],
                "normalGlyphCount": len(final_preview["fonts"]["normal"]["glyphs"]),
                "specialGlyphCount": len(final_preview["fonts"]["special"]["glyphs"])},
            "registryPreserved": True, "errors": errors, "sourceWitnesses": witnesses}
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


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish", type=Path, default=ROOT / "artifacts" / "gate" / "win-x64-fd")
    parser.add_argument("--renderer", choices=["regl", "webgpu", "both"], default="both")
    args = parser.parse_args()
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as playwright:
        for renderer in (["regl", "webgpu"] if args.renderer == "both" else [args.renderer]):
            configurator_case(playwright, args.publish / "MatrixDesktopConfigurator.exe", renderer)
    print("PASS portable preset and backup bytes restored by the native preservation helper.", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
