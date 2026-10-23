"""Verify native desktop capture using a fully synthetic, test-owned desktop cover.

Never writes captured pixels. The app starts only after the borderless fixture covers
the virtual desktop and covers the exact primary capture region without obstruction. Every captured pixel
must belong to the synthetic palette. Each process/profile/marker belongs to this run.
"""
import argparse
import base64
import ctypes
from ctypes import wintypes
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import time

from PIL import Image
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parent.parent
ARTIFACTS = ROOT / "artifacts" / "desktop-snapshot"
PALETTE = {(32, 64, 224), (224, 48, 64), (32, 208, 96), (208, 64, 208)}
USER32 = ctypes.WinDLL("user32", use_last_error=True)
USER32.SetProcessDpiAwarenessContext.argtypes = [wintypes.HANDLE]
USER32.SetProcessDpiAwarenessContext(wintypes.HANDLE(-4))
USER32.GetTopWindow.argtypes = [wintypes.HWND]
USER32.GetTopWindow.restype = wintypes.HWND
USER32.GetWindow.argtypes = [wintypes.HWND, wintypes.UINT]
USER32.GetWindow.restype = wintypes.HWND
USER32.IsWindowVisible.argtypes = [wintypes.HWND]
USER32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
USER32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
USER32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
USER32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
USER32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.UINT]
USER32.GetLayeredWindowAttributes.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD), ctypes.POINTER(ctypes.c_ubyte), ctypes.POINTER(wintypes.DWORD)]
DWM = ctypes.WinDLL("dwmapi")
DWM.DwmGetWindowAttribute.argtypes = [wintypes.HWND, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD]


def rect(hwnd):
    value = wintypes.RECT()
    if not USER32.GetWindowRect(hwnd, ctypes.byref(value)):
        raise AssertionError("Test window bounds were unavailable")
    return value.left, value.top, value.right, value.bottom


def assert_fixture_cover(metadata, capture_bounds=None):
    hwnd = metadata["hwnd"]
    pid = wintypes.DWORD()
    USER32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    assert pid.value == metadata["pid"], "Fixture window ownership changed"
    assert USER32.SetWindowPos(hwnd, wintypes.HWND(-1), 0, 0, 0, 0, 0x13), "Could not promote the test-owned fixture"
    expected = (USER32.GetSystemMetrics(76), USER32.GetSystemMetrics(77),
                USER32.GetSystemMetrics(78), USER32.GetSystemMetrics(79))
    actual = (metadata["x"], metadata["y"], metadata["width"], metadata["height"])
    assert actual == expected, "Fixture does not cover the complete virtual desktop"
    x, y, w, h = actual
    assert rect(hwnd) == (x, y, x + w, y + h), "Fixture bounds changed before capture"
    # UIAccess widgets can occupy a higher z-order band on other displays. They cannot
    # affect a fixed primary-monitor capture, so only intersecting windows obstruct it.
    capture_left, capture_top, capture_right, capture_bottom = capture_bounds or (x, y, x + w, y + h)
    current = USER32.GetTopWindow(None)
    while current and current != hwnd:
        cloaked = wintypes.DWORD()
        hidden_by_dwm = DWM.DwmGetWindowAttribute(current, 14, ctypes.byref(cloaked), ctypes.sizeof(cloaked)) == 0 and cloaked.value != 0
        color, alpha, flags = wintypes.DWORD(), ctypes.c_ubyte(), wintypes.DWORD()
        invisible_layer = USER32.GetLayeredWindowAttributes(current, ctypes.byref(color), ctypes.byref(alpha), ctypes.byref(flags)) and flags.value & 2 and alpha.value == 0
        if USER32.IsWindowVisible(current) and not hidden_by_dwm and not invisible_layer:
            left, top, right, bottom = rect(current)
            owner = wintypes.DWORD()
            USER32.GetWindowThreadProcessId(current, ctypes.byref(owner))
            kind = ctypes.create_unicode_buffer(256)
            USER32.GetClassNameW(current, kind, len(kind))
            assert not (left < capture_right and right > capture_left and top < capture_bottom and bottom > capture_top), \
                f"A visible window covers the synthetic fixture (PID {owner.value}, class {kind.value}, bounds {(left,top,right,bottom)}); capture was not started"
        current = USER32.GetWindow(current, 2)
    assert current == hwnd, "Fixture was absent from the desktop window order"


def validate_pixels(image, expected_size):
    assert image.size == expected_size, "Snapshot dimensions do not match native capture bounds"
    colors = image.getcolors(maxcolors=16)
    assert colors is not None and len(colors) >= 2, "Captured pixels were not the synthetic fixture"
    assert all(color in PALETTE for _, color in colors), "Capture contained pixels outside the synthetic palette"
    return len(colors)


def staged_files(web):
    # Metadata only, with protected names excluded before file access.
    result = {}
    protected_suffixes = {".env", ".pem", ".key", ".p12", ".pfx", ".keystore"}
    import os
    def protected(name):
        lower = name.lower()
        return (lower.endswith(".env") or lower.startswith((".env.", "privatekey"))
                or lower == ".npmrc" or Path(name).suffix.lower() in protected_suffixes)
    for directory, folders, files in os.walk(web, followlinks=False):
        folders[:] = [name for name in folders if not protected(name) and name.lower() != ".git"
                      and not (Path(directory) / name).is_symlink()]
        for name in files:
            if protected(name):
                continue
            path = Path(directory) / name
            if path.is_symlink():
                continue
            stat = path.stat()
            result[str(path.relative_to(web))] = (stat.st_size, stat.st_mtime_ns)
    return result


def own_window(pid):
    current = USER32.GetTopWindow(None)
    while current:
        owner = wintypes.DWORD()
        USER32.GetWindowThreadProcessId(current, ctypes.byref(owner))
        if owner.value == pid and USER32.IsWindowVisible(current):
            return current
        current = USER32.GetWindow(current, 2)
    raise AssertionError("Published app window was unavailable")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish", type=Path, default=ROOT / "artifacts/gate/win-x64-fd")
    parser.add_argument("--windowed", action="store_true", help="Require unobstructed full-desktop coverage and test windowed initial bounds instead of fixed primary bounds")
    parser.add_argument("--self-test", action="store_true", help="Mutation-test pixel and dimension witnesses in memory without opening windows")
    options = parser.parse_args()
    if options.self_test:
        image = Image.new("RGB", (128, 64), sorted(PALETTE)[0])
        image.paste(sorted(PALETTE)[1], (64, 0, 128, 64))
        validate_pixels(image, (128, 64))
        image.putpixel((0, 0), (1, 2, 3))
        try:
            validate_pixels(image, (128, 64))
            raise RuntimeError("Single-pixel mutation survived")
        except AssertionError as error:
            assert "outside the synthetic palette" in str(error)
        try:
            validate_pixels(image, (127, 64))
            raise RuntimeError("Dimension mutation survived")
        except AssertionError as error:
            assert "dimensions" in str(error)
        print("PASS mutation witnesses: one nonfixture pixel and wrong capture dimensions both rejected")
        return 0
    publish = options.publish.resolve()
    exe = publish / "MatrixDesktop.exe"
    assert exe.is_file(), "Publish the app before running this test"
    run = ARTIFACTS / ("run-" + str(time.time_ns()))
    run.mkdir(parents=True)
    ready, stop = run / "ready.json", run / "stop"
    spec = importlib.util.spec_from_file_location("snapshot_native_helper", ROOT / "tests/native-uc3d-smoke.py")
    native = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(native)
    native.ARTIFACTS = run / "webview"
    native.ARTIFACTS.mkdir()
    fixture = process = browser = None
    before = staged_files(publish / "web")
    stdout = (run / "fixture.stdout.log").open("w", encoding="utf-8")
    stderr = (run / "fixture.stderr.log").open("w", encoding="utf-8")
    try:
        fixture = subprocess.Popen(["powershell.exe", "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File",
                                    str(ROOT / "tests/desktop-fixture.ps1"), "-ReadyFile", str(ready), "-StopFile", str(stop)],
                                   stdout=stdout, stderr=stderr, creationflags=subprocess.CREATE_NO_WINDOW)
        deadline = time.monotonic() + 15
        while not ready.exists():
            assert fixture.poll() is None, f"Synthetic fixture exited; see {run / 'fixture.stderr.log'}"
            assert time.monotonic() < deadline, "Synthetic fixture did not become ready"
            time.sleep(0.05)
        metadata = json.loads(ready.read_text(encoding="utf-8-sig"))
        assert metadata["pid"] == fixture.pid and metadata["powershell"].startswith("5.1."), metadata
        capture_bounds = None if options.windowed else (0, 0, USER32.GetSystemMetrics(0), USER32.GetSystemMetrics(1))
        with sync_playwright() as playwright:
            assert_fixture_cover(metadata, capture_bounds)
            process, browser = native.open_application(playwright, exe,
                ["--windowed" if options.windowed else "--single-monitor", "--topmost", "--no-exit-on-any-key", "--scene", "uc3d", "--renderer", "regl",
                 "--desktopBackground", "true", "--showPictures", "false", "--sound", "off", "--suppressWarnings", "true"], "snapshot")
            # Snapshot is captured in the constructor, before this app shows. The fixture
            # need not cover the owner's desktop while the WebView subsequently initializes.
            stop.write_text("stop", encoding="ascii")
            fixture.wait(timeout=8)
            assert fixture.returncode == 0, "Fixture did not close cleanly"
            page = native.page_at(browser, "https://appassets.local/")
            page.wait_for_function("window.__matrixRuntime?.ready === true", timeout=25000)
            payload = page.evaluate("""async () => {
                const url=window.matrixHostBootstrap?.desktopUrl;
                if (!url) return {url:null};
                const response=await fetch(url);
                const bytes=new Uint8Array(await response.arrayBuffer());
                let binary=''; for(let i=0;i<bytes.length;i+=16384) binary+=String.fromCharCode(...bytes.subarray(i,i+16384));
                return {url,status:response.status,type:response.headers.get('content-type'),cache:response.headers.get('cache-control'),data:btoa(binary)};
            }""")
            assert payload["url"] == "https://uc3d-media.local/__uc3d/desktop", "Desktop snapshot grant missing"
            assert payload["status"] == 200 and payload["type"] == "image/png" and payload["cache"] == "no-store"
            with Image.open(io.BytesIO(base64.b64decode(payload.pop("data")))) as image:
                image = image.convert("RGB")
                left, top, right, bottom = rect(own_window(process.pid))
                if capture_bounds:
                    assert (left, top, right, bottom) == capture_bounds, "Single-monitor capture bounds were not fixed as expected"
                colors = validate_pixels(image, (right - left, bottom - top))
                report = {"dimensions": image.size, "paletteColors": colors, "pixels": image.width * image.height,
                          "mode": "windowed" if options.windowed else "primary-monitor",
                          "fixturePowerShell": metadata["powershell"], "status": payload["status"], "cache": payload["cache"]}
            assert staged_files(publish / "web") == before, "Desktop snapshot changed the published web staging tree"
            native.close_window(process)
            browser.close()
            browser = None
            (run / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
            print("PASS native desktop snapshot: synthetic pixels only, correct bounds, opt-in grant, no-store response, unchanged web staging")
            print(json.dumps(report))
    finally:
        if browser is not None:
            try:
                browser.close()
            except Exception:
                pass  # Playwright may already have closed its driver on an earlier error.
        try:
            if process is not None and process.poll() is None:
                try:
                    native.close_window(process)
                except Exception:
                    process.terminate()
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)
        finally:
            try:
                if fixture is not None and fixture.poll() is None:
                    stop.write_text("stop", encoding="ascii")
                    try:
                        fixture.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        fixture.terminate()
                        try:
                            fixture.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            fixture.kill()
                            fixture.wait(timeout=5)
            finally:
                stdout.close()
                stderr.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
