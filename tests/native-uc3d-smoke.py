"""Exercise the published Windows WebView2 applications, including the native media bridge."""
import argparse
import ctypes
from ctypes import wintypes
import hashlib
import io
import json
import math
import os
from pathlib import Path
import socket
import subprocess
import time
import urllib.request
from PIL import Image
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parent.parent
ARTIFACTS = ROOT / "artifacts" / "native-uc3d"
USER32 = ctypes.WinDLL("user32", use_last_error=True)
CALLBACK = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
USER32.EnumWindows.argtypes = [CALLBACK, wintypes.LPARAM]
USER32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
USER32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]

def close_window(process):
    def visit(hwnd, _):
        pid = wintypes.DWORD()
        USER32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value == process.pid:
            USER32.PostMessageW(hwnd, 0x0010, 0, 0)
        return True
    USER32.EnumWindows(CALLBACK(visit), 0)
    process.wait(timeout=12)
    assert process.returncode == 0, f"application exited {process.returncode}"

def stop_application(process):
    """Reap an application owned by this test, including a failed startup."""
    if process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)

def open_application(playwright, exe, args, case):
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    env = os.environ.copy()
    env["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = f"--remote-debugging-port={port}"
    env["WEBVIEW2_USER_DATA_FOLDER"] = str(ARTIFACTS / f"profile-{case}-{time.time_ns()}")
    process = subprocess.Popen([str(exe), *args], env=env, creationflags=subprocess.CREATE_NO_WINDOW)
    print(f"OPEN {case} pid={process.pid} debugger={port}", flush=True)
    endpoint = f"http://127.0.0.1:{port}"
    try:
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            assert process.poll() is None, f"application exited before exposing its WebView: {process.returncode}"
            try:
                with urllib.request.urlopen(endpoint + "/json/version", timeout=1) as response:
                    assert json.load(response).get("webSocketDebuggerUrl")
                browser = playwright.chromium.connect_over_cdp(endpoint)
                return process, browser
            except (OSError, ValueError):
                time.sleep(0.15)
        raise AssertionError("WebView2 debug endpoint did not appear")
    except BaseException:
        # The caller has no process handle until this function returns, so it cannot
        # clean up a CDP attachment failure or timeout in its own finally block.
        stop_application(process)
        raise

def page_at(browser, origin):
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        for context in browser.contexts:
            for page in context.pages:
                if page.url.startswith(origin):
                    page.wait_for_load_state("domcontentloaded")
                    return page
        # Pump Playwright's protocol while the native host finishes navigation.
        # time.sleep leaves cached Page.url at about:blank after an early CDP attach.
        pages = [page for context in browser.contexts for page in context.pages]
        if pages:
            pages[0].wait_for_timeout(100)
        else:
            browser.new_browser_cdp_session().send("Target.getTargets")
    urls=[page.url for context in browser.contexts for page in context.pages]
    raise AssertionError(f"No published WebView2 page at {origin}; observed {urls}")

def default_snapshot(frame):
    """Read the configuration and geometry produced by the deployed web modules."""
    for relative in ("js/config.js", "js/uc3d/defaults.js", "js/uc3d/scene.js"):
        deployed = frame.evaluate("""async relative => {
            const response = await fetch(new URL(relative, location.href));
            if (!response.ok) throw new Error(`Source witness failed: ${relative}`);
            return Array.from(new Uint8Array(await response.arrayBuffer()));
        }""", relative)
        source = (ROOT / "MatrixDesktop" / "web" / relative).read_bytes()
        assert bytes(deployed) == source, f"published source differs from checkout: {relative}"
        print(f"SOURCE {relative} sha256={hashlib.sha256(source).hexdigest()}", flush=True)
    return frame.evaluate("""async () => {
        const {default:makeConfig} = await import('./js/config.js');
        const {createGrid} = await import('./js/uc3d/scene.js');
        const config = makeConfig(Object.fromEntries(new URLSearchParams(location.search)));
        const canvas = document.querySelector('canvas');
        const aspect = canvas.clientWidth / canvas.clientHeight;
        const {columns,rows,baseColumns,count} = createGrid(config, aspect);
        return {config,grid:{columns,rows,baseColumns,count},aspect,
            runtime:window.__matrixRuntime,bootstrap:window.matrixHostBootstrap};
    }""")

def assert_default_snapshot(report, preview=False, volumetric=True):
    config = report["config"]
    expected = {"scene": "uc3d", "volumetric": volumetric, "numColumns": 100,
                "density": 5, "fallSpeed": 0.045, "forwardSpeed": -0.675,
                "sidewaysMovement": True, "randomForwardMovement": True,
                "showPictures": False, "showClock": False, "sound": "off",
                "desktopBackground": False, "pictureMonochrome": False,
                "pictureDelay": 60, "pictureDuration": 30}
    for name, value in expected.items():
        observed = config.get(name)
        matches = math.isclose(observed, value, rel_tol=0, abs_tol=1e-12) if type(value) in (int, float) else observed == value
        assert matches, f"UC3D default {name}: expected {value!r}, observed {observed!r}"
    for name, value in (("codeColor", [128 / 255, 0, 1]), ("cursorColor", [1, 128 / 255, 1])):
        color = config[name]
        assert color["space"] == "rgb" and len(color["values"]) == 3 and all(
            math.isclose(actual, wanted, rel_tol=0, abs_tol=1e-12)
            for actual, wanted in zip(color["values"], value)), f"UC3D default {name}: {color}"
    grid = report["grid"]
    expected_columns = grid["baseColumns"] * (5 if volumetric else 1)
    assert grid["columns"] == expected_columns, f"UC3D default stream population: {grid}"
    assert report["runtime"]["scene"] == "uc3d" and not report["runtime"]["errors"], report["runtime"]
    bootstrap = report["bootstrap"]
    assert bootstrap and bootstrap["preview"] is preview, "native UC3D bootstrap has the wrong preview mode"
    assert not bootstrap["images"] and not any(bootstrap.get(name) for name in (
        "desktopUrl", "audioUrl", "effectsUrl", "glyphUrl")), "default UC3D launch granted optional local media"

def default_case(playwright, exe, renderer):
    """A bare UC3D launch must render the saved donor profile, without media grants."""
    case = "defaults-" + renderer
    args = ["--windowed", "--no-exit-on-any-key", "--scene", "uc3d", "--renderer", renderer,
            "--suppressWarnings", "true"]
    process, browser = open_application(playwright, exe, args, case)
    try:
        page = page_at(browser, "https://appassets.local/")
        errors = []
        page.on("pageerror", lambda error: errors.append(str(error)))
        page.wait_for_function("window.__matrixRuntime?.ready === true", timeout=25000)
        report = default_snapshot(page)
        assert_default_snapshot(report)
        assert report["runtime"]["backend"] == renderer, "default UC3D requested backend silently fell back"
        assert report["aspect"] >= 1 and report["grid"]["columns"] == 500, report["grid"]
        statuses = page.evaluate("""async () => Promise.all(['desktop','image-0'].map(async id =>
            (await fetch('https://uc3d-media.local/__uc3d/' + id)).status))""")
        assert statuses == [404, 404], f"default UC3D launch exposed optional media: {statuses}"
        canvas = page.locator("canvas").first
        first_png = canvas.screenshot()
        first = Image.open(io.BytesIO(first_png)).convert("RGB")
        pixels = lambda image: image.get_flattened_data() if hasattr(image, "get_flattened_data") else image.getdata()
        purple_pixels = sum(r > 30 and b > 50 and r > g * 1.3 and b > g * 1.3 for r, g, b in pixels(first))
        assert purple_pixels > max(20, first.width * first.height * 0.0001), f"default UC3D screenshot lacks visible purple code: {purple_pixels} pixels"
        frames = page.evaluate("window.__matrixRuntime.frames")
        page.wait_for_function("prior => window.__matrixRuntime.frames > prior + 2", arg=frames, timeout=15000)
        second_png = canvas.screenshot()
        second = Image.open(io.BytesIO(second_png)).convert("RGB")
        assert first.size == second.size and sum(a != b for a, b in zip(pixels(first), pixels(second))) > 20, "default UC3D code did not visibly animate"
        assert not errors, errors
        assert page.evaluate("window.__matrixRuntime.errors") == [], "default UC3D rendering reported errors"
        (ARTIFACTS / f"{case}.png").write_bytes(second_png)
        report["purplePixels"] = purple_pixels
        report["framesAfter"] = page.evaluate("window.__matrixRuntime.frames")
        (ARTIFACTS / f"{case}.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(f"PASS published UC3D defaults {renderer}: saved purple profile, 500 streams, visible animation, no optional media grants", flush=True)
        close_window(process)
    finally:
        stop_application(process)
        browser.close()

def default_configurator_case(playwright, exe):
    """Exercise the real scene selector and its generated native preview query."""
    process, browser = open_application(playwright, exe, [], "defaults-configurator")
    try:
        page = page_at(browser, "https://configurator.local/")
        page.wait_for_function("document.querySelectorAll('.field').length > 40")
        page.locator("#newPresetButton").click()

        def field(label):
            return page.locator(".field").filter(has=page.get_by_text(label, exact=True))

        field("Scene").locator("select").select_option("uc3d")
        # A previous saved UC3D preview can still be displayed during the debounce.
        # Wait for the fresh New + UC3D command rather than that stale scene alone.
        page.wait_for_function("new URL(document.querySelector('#previewFrame').src, location.href).search === '?scene=uc3d'")

        def preview():
            target = page.locator("#previewFrame").get_attribute("src")
            deadline = time.monotonic() + 20
            frame = None
            while time.monotonic() < deadline:
                frame = next((frame for frame in page.frames if frame.url == target), None)
                if frame:
                    break
                page.wait_for_timeout(100)
            assert frame, f"published configurator preview did not navigate to {target}"
            frame.wait_for_function("window.__matrixRuntime?.scene === 'uc3d' && window.__matrixRuntime.ready === true", timeout=25000)
            return frame

        report = default_snapshot(preview())
        assert_default_snapshot(report, preview=True)
        assert field("Volumetric / 3D").locator("input").is_checked(), "configurator scene switch kept Matrix's flat default"
        for label, value in (("Columns", "100"), ("Fall speed", "0.045"), ("Forward speed", "-0.675")):
            control = field(label).locator("input[type=number]")
            assert control.input_value() == value and control.evaluate("node => node.checkValidity()"), f"configurator default {label} is absent or invalid"
        columns = field("Columns").locator("input[type=number]")
        columns.fill("")
        columns.press("Tab")
        assert columns.input_value() == "100", "clearing Columns reset to Matrix's default instead of UC3D's"
        field("Volumetric / 3D").locator("input").uncheck()
        page.wait_for_function("new URL(document.querySelector('#previewFrame').src, location.href).searchParams.get('volumetric') === 'false'")
        override = default_snapshot(preview())
        assert_default_snapshot(override, preview=True, volumetric=False)
        field("Scene").locator("select").select_option("matrix")
        page.wait_for_function("new URL(document.querySelector('#previewFrame').src, location.href).searchParams.get('scene') !== 'uc3d'")
        field("Scene").locator("select").select_option("uc3d")
        page.wait_for_function("new URL(document.querySelector('#previewFrame').src, location.href).searchParams.get('scene') === 'uc3d'")
        round_trip = default_snapshot(preview())
        assert_default_snapshot(round_trip, preview=True, volumetric=False)
        assert not field("Volumetric / 3D").locator("input").is_checked(), "scene round trip replaced the explicit flat override"
        page.screenshot(path=str(ARTIFACTS / "defaults-configurator.png"))
        (ARTIFACTS / "defaults-configurator.json").write_text(json.dumps({"default": report, "explicitFlatOverride": override, "sceneRoundTrip": round_trip}, indent=2), encoding="utf-8")
        print("PASS published configurator UC3D defaults: real scene switch, valid profile controls, scene reset value, explicit flat override survives preview and scene round trip", flush=True)
        close_window(process)
    finally:
        stop_application(process)
        browser.close()

def runtime_case(playwright, exe, renderer, donor):
    case = "runtime-" + renderer
    fixture = ARTIFACTS / "pictures"
    fixture.mkdir(parents=True, exist_ok=True)
    picture = Image.new("RGB", (160, 120), "red")
    picture.paste("lime", (80, 0, 160, 120))
    picture.save(fixture / "Red and green.png")
    args = ["--windowed", "--no-exit-on-any-key", "--scene", "uc3d", "--renderer", renderer,
            "--volumetric", "true", "--showClock", "true", "--codeRotation", "false",
            "--showPictures", "true", "--pictureDelay", "1", "--pictureDuration", "8",
            "--pictureMonochrome", "false", "--pictureDepth", "0.7", "--pictureDirectory", str(fixture),
            "--sound", "effects", "--soundVolume", "0", "--suppressWarnings", "true"]
    if donor:
        args += ["--donorDirectory", donor, "--donorGlyphs", "true"]
    process, browser = open_application(playwright, exe, args, case)
    try:
        page = page_at(browser, "https://appassets.local/")
        page.on("pageerror", lambda error: print(f"PAGE ERROR {case}: {error}", flush=True))
        try:
            page.wait_for_function("window.__matrixRuntime?.ready === true", timeout=25000)
        except Exception:
            print(page.evaluate("({url:location.href,body:document.body.innerText,runtime:window.__matrixRuntime,bootstrap:!!window.matrixHostBootstrap})"), flush=True)
            raise
        assert page.evaluate("window.__matrixRuntime.backend") == renderer, "requested backend silently fell back"
        bootstrap = page.evaluate("window.matrixHostBootstrap")
        assert bootstrap and not bootstrap["preview"] and bootstrap["images"], "native bootstrap/media grant missing"
        assert all(not image["url"].startswith("file:") for image in bootstrap["images"]), "raw filesystem URL exposed"
        response = page.evaluate("async () => {const r=await fetch(matrixHostBootstrap.images[0].url);return {status:r.status,type:r.headers.get('content-type'),cache:r.headers.get('cache-control'),bytes:(await r.arrayBuffer()).byteLength};}")
        assert response["status"] == 200 and response["bytes"] > 0 and response["type"].startswith("image/") and response["cache"] == "no-store", response
        assert page.evaluate("async () => (await fetch(new URL('not-granted',matrixHostBootstrap.images[0].url))).status") == 404, "ungranted native resource was readable"
        assert page.evaluate("async () => (await fetch(new URL('desktop',matrixHostBootstrap.images[0].url))).status") == 404, "desktop resource was granted without opting in"
        if donor:
            assert bootstrap.get("glyphUrl") and bootstrap.get("effectsUrl"), "imported glyph/sound resources missing"
            assert len(bootstrap["images"]) == 107, "106 donor selections plus one personal picture must survive native import"
        page.wait_for_timeout(1800)
        page.mouse.click(12, 12)
        page.screenshot(path=str(ARTIFACTS / f"{case}.png"))
        report = page.evaluate("({runtime:window.__matrixRuntime,clock:document.querySelector('[data-uc3d-clock]')?.textContent})")
        (ARTIFACTS / f"{case}.json").write_text(json.dumps({"media": response, "diagnostics": report}, indent=2), encoding="utf-8")
        print(f"PASS published WebView2 {renderer}: exact backend, local images, grant boundaries, original media and render readiness")
        close_window(process)
    finally:
        stop_application(process)
        browser.close()

def configurator_case(playwright, exe, donor):
    process, browser = open_application(playwright, exe, [], "configurator")
    try:
        page = page_at(browser, "https://configurator.local/")
        page.wait_for_function("document.querySelectorAll('.field').length > 40")
        page.wait_for_timeout(1200)  # Let the initial debounced preview finish first.
        # Call the same public host request/reply route the page uses, then navigate the
        # real preview iframe. This tests native bootstrap publication before navigation.
        result = page.evaluate("""async donor => {
            const id='native-smoke';
            const prior=window.configHost.receive;
            const result=new Promise((resolve,reject)=>{
              window.configHost.receive=m=>{if(m.id===id){window.configHost.receive=prior;m.ok?resolve(m.payload):reject(new Error(m.payload.message));}else prior(m);};
            });
            window.chrome.webview.postMessage({id,type:'buildWebQuery',payload:{draft:{scene:'uc3d',showClock:true,volumetric:true,showPictures:!!donor,donorDirectory:donor||'',donorGlyphs:!!donor,sound:donor?'effects':'off',soundVolume:0}}});
            return await result;
        }""", donor)
        target = page.evaluate("query => {const frame=document.querySelector('#previewFrame');frame.src='https://matrix-preview.local/index.html'+query;return frame.src;}", result["query"])
        deadline=time.monotonic()+25
        frame=None
        while time.monotonic()<deadline:
            frame=next((f for f in page.frames if f.url == target),None)
            if frame: break
            page.wait_for_timeout(100)
        assert frame, "preview iframe did not navigate"
        try:
            frame.wait_for_function("window.__matrixRuntime?.ready === true", timeout=20000)
        except Exception:
            print(frame.evaluate("({url:location.href,body:document.body.innerText,runtime:window.__matrixRuntime,bootstrap:window.matrixHostBootstrap})"),flush=True)
            raise
        assert frame.evaluate("window.matrixHostBootstrap.preview") is True, "preview did not receive muted native bootstrap"
        if donor:
            assert len(frame.evaluate("window.matrixHostBootstrap.images"))==106, "preview lost donor media manifest"
            assert frame.evaluate("!!window.matrixHostBootstrap.effectsUrl"), "explicit sound test lost selected donor audio"
            frame.get_by_role("button",name="Test sound",exact=True).click()
            frame.wait_for_function("window.__matrixRuntime.audio.status === 'playing'",timeout=10000)
        page.screenshot(path=str(ARTIFACTS / "configurator.png"))
        print("PASS published configurator: controls loaded, native preview bootstrap, muted UC3D frame rendered")
        close_window(process)
    finally:
        stop_application(process)
        browser.close()

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--publish",type=Path,default=ROOT/"artifacts/gate/win-x64-fd")
    parser.add_argument("--donor-directory",default="")
    parser.add_argument("--renderer",choices=["regl","webgpu","both"],default="both")
    parser.add_argument("--case",choices=["runtime","configurator","defaults","all"],default="all")
    options=parser.parse_args()
    ARTIFACTS.mkdir(parents=True,exist_ok=True)
    with sync_playwright() as playwright:
        if options.case in ("defaults", "all"):
            for renderer in (["regl","webgpu"] if options.renderer=="both" else [options.renderer]):
                default_case(playwright,options.publish/"MatrixDesktop.exe",renderer)
            default_configurator_case(playwright,options.publish/"MatrixDesktopConfigurator.exe")
        if options.case in ("runtime", "all"):
            for renderer in (["regl","webgpu"] if options.renderer=="both" else [options.renderer]):
                runtime_case(playwright,options.publish/"MatrixDesktop.exe",renderer,options.donor_directory)
        if options.case in ("configurator", "all"):
            configurator_case(playwright,options.publish/"MatrixDesktopConfigurator.exe",options.donor_directory)
    return 0

if __name__=="__main__":
    raise SystemExit(main())
