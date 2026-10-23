"""Exercise the published Windows WebView2 applications, including the native media bridge."""
import argparse
import ctypes
from ctypes import wintypes
import json
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
    process.terminate()
    raise AssertionError("WebView2 debug endpoint did not appear")

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
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=10)
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
        page.evaluate("query => {document.querySelector('#previewFrame').src='https://matrix-preview.local/index.html'+query;}", result["query"])
        deadline=time.monotonic()+25
        frame=None
        while time.monotonic()<deadline:
            frame=next((f for f in page.frames if f.url.startswith("https://matrix-preview.local/")),None)
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
        if process.poll() is None: process.terminate(); process.wait(timeout=10)
        browser.close()

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--publish",type=Path,default=ROOT/"artifacts/gate/win-x64-fd")
    parser.add_argument("--donor-directory",default="")
    parser.add_argument("--renderer",choices=["regl","webgpu","both"],default="both")
    parser.add_argument("--case",choices=["runtime","configurator","all"],default="all")
    options=parser.parse_args()
    ARTIFACTS.mkdir(parents=True,exist_ok=True)
    with sync_playwright() as playwright:
        if options.case != "configurator":
            for renderer in (["regl","webgpu"] if options.renderer=="both" else [options.renderer]):
                runtime_case(playwright,options.publish/"MatrixDesktop.exe",renderer,options.donor_directory)
        if options.case != "runtime":
            configurator_case(playwright,options.publish/"MatrixDesktopConfigurator.exe",options.donor_directory)
    return 0

if __name__=="__main__":
    raise SystemExit(main())
