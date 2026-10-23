"""Actual Windows wallpaper smoke. Launches/captures only its owned application.

Requires an interactive desktop, Playwright and Pillow. Never installs/registers
a screensaver, changes startup/wallpaper settings, or restarts Explorer.
"""
import argparse, asyncio, ctypes, json, os, socket, subprocess, tempfile, time, urllib.request
from ctypes import wintypes
from pathlib import Path
from playwright.async_api import async_playwright
from PIL import Image, ImageStat

u = ctypes.WinDLL('user32', use_last_error=True)
u.GetForegroundWindow.restype = wintypes.HWND
u.GetParent.argtypes = [wintypes.HWND]
u.GetParent.restype = wintypes.HWND
u.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u.GetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int]
u.GetWindowLongPtrW.restype = ctypes.c_ssize_t
u.IsWindowVisible.argtypes = [wintypes.HWND]
u.IsWindowEnabled.argtypes = [wintypes.HWND]
u.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
CALLBACK = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
u.EnumChildWindows.argtypes = [wintypes.HWND, CALLBACK, wintypes.LPARAM]
u.EnumWindows.argtypes = [CALLBACK, wintypes.LPARAM]

def cls(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(hwnd, buf, 256)
    return buf.value

def caption(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    u.GetWindowTextW(hwnd, buf, 256)
    return buf.value

def owned_windows(pid):
    result = []
    @CALLBACK
    def each(hwnd, _):
        owner = wintypes.DWORD()
        u.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid:
            result.append((hwnd, cls(hwnd), u.GetParent(hwnd)))
        return True
    @CALLBACK
    def top(hwnd, _):
        each(hwnd, 0)
        u.EnumChildWindows(hwnd, each, 0)
        return True
    u.EnumWindows(top, 0)
    return result

def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]

async def run(app, output, scene, renderer):
    name = f'{scene}-{renderer}'
    directory = output / name
    directory.mkdir(parents=True, exist_ok=True)
    before = u.GetForegroundWindow()
    port = free_port()
    env = dict(os.environ)
    env['WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS'] = f'--remote-debugging-port={port} --remote-allow-origins=*'
    env['WEBVIEW2_USER_DATA_FOLDER'] = str(directory / 'owned-webview-profile')
    process = subprocess.Popen([str(app), '--wallpaper', '--wallpaper-pause-fullscreen', 'false', '--scene', scene, '--renderer', renderer], env=env)
    witness = {'case':name, 'pid':process.pid, 'foregroundBefore':int(before or 0)}
    try:
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise AssertionError(f'owned application exited: {process.returncode}')
            try:
                with urllib.request.urlopen(f'http://127.0.0.1:{port}/json/version', timeout=1) as response:
                    json.load(response)
                break
            except Exception:
                await asyncio.sleep(.2)
        else:
            raise AssertionError('owned WebView2 did not expose the requested debug endpoint')
        async with async_playwright() as playwright:
            browser = await playwright.chromium.connect_over_cdp(f'http://127.0.0.1:{port}')
            page = None
            for _ in range(200):
                page = next((p for context in browser.contexts for p in context.pages if p.url.startswith('https://appassets.local/')), None)
                if page:
                    break
                await asyncio.sleep(.1)
            assert page is not None, 'owned application did not navigate to its bundled page'
            errors = []
            page.on('pageerror', lambda error: errors.append(str(error)))
            await page.wait_for_selector('canvas', timeout=20000)
            await asyncio.sleep(4)
            windows = owned_windows(process.pid)
            forms = [(h,c,p) for h,c,p in windows if c.startswith('WindowsForms10.Window') and p and cls(p) == 'WorkerW']
            assert len(forms) == 1, f'expected one owned WorkerW render form, got {windows}'
            hwnd, _, parent = forms[0]
            witness.update(child=int(hwnd), parent=int(parent), parentClass=cls(parent), visible=bool(u.IsWindowVisible(hwnd)), enabled=bool(u.IsWindowEnabled(hwnd)), style=u.GetWindowLongPtrW(hwnd,-16), exStyle=u.GetWindowLongPtrW(hwnd,-20))
            assert witness['visible'] and not witness['enabled'], witness
            assert witness['style'] & 0x40000000, witness
            assert not witness['exStyle'] & 0x40000, witness
            first = directory / 'frame-a.png'
            second = directory / 'frame-b.png'
            await page.screenshot(path=str(first))
            await asyncio.sleep(.5)
            await page.screenshot(path=str(second))
            with Image.open(second) as image:
                witness['size'] = list(image.size)
                witness['meanRgb'] = ImageStat.Stat(image.convert('RGB')).mean
            assert max(witness['meanRgb']) > .01, f'blank owned canvas: {witness}'
            assert first.read_bytes() != second.read_bytes(), 'owned canvas did not animate'
            runtime = await page.evaluate('window.matrixRuntimeDiagnostics || window.__matrixRuntime || null')
            witness['runtime'] = runtime
            if scene in ['uc3d','zmatrix']:
                assert runtime and runtime.get('ready') and runtime.get('backend') == renderer, runtime
                assert not runtime.get('errors'), runtime
                if scene == 'zmatrix':
                    witness['nativeBootstrap'] = await page.evaluate('({fonts:!!window.matrixHostBootstrap?.zmatrixFonts,wallpaper:!!window.matrixHostBootstrap?.wallpaper,warnings:window.matrixHostBootstrap?.warnings})')
                    assert witness['nativeBootstrap']['fonts'], witness['nativeBootstrap']
                    assert witness['nativeBootstrap']['wallpaper'], witness['nativeBootstrap']
                    assert set(runtime['nativeFonts']) == {'normal','special'}, runtime
                    assert all(v.get('source') == 'native-gdi' for v in runtime['nativeFonts'].values()), runtime
            else:
                witness['contextType'] = 'webgpu' if renderer == 'webgpu' else 'webgl'
                witness['contextAvailable'] = await page.evaluate('(kind)=>!!document.querySelector("canvas").getContext(kind)', witness['contextType'])
                assert witness['contextAvailable'], witness
            assert not errors, errors
            witness['foregroundAfter'] = int(u.GetForegroundWindow() or 0)
            foreground_pid = wintypes.DWORD()
            u.GetWindowThreadProcessId(witness['foregroundAfter'], ctypes.byref(foreground_pid))
            witness['foregroundAfterPid'] = foreground_pid.value
            witness['foregroundAfterClass'] = cls(witness['foregroundAfter'])
            assert witness['foregroundAfter'] == witness['foregroundBefore'], 'wallpaper launch changed foreground focus'
            coordinator = next((h for h,_,_ in owned_windows(process.pid) if caption(h) == 'MatrixDesktop coordinator'), None)
            assert coordinator, 'owned application coordinator was not found for orderly cleanup'
            u.PostMessageW(coordinator, 0x10, 0, 0)
            for _ in range(100):
                if process.poll() is not None:
                    break
                await asyncio.sleep(.1)
            assert process.poll() == 0, 'owned application did not exit cleanly through its coordinator'
            witness['closedCleanly'] = True
            await browser.close()
        witness['passed'] = True
    except Exception as error:
        witness['passed'] = False
        witness['error'] = f'{type(error).__name__}: {error}'
        raise
    finally:
        # Stop only the application created above. Its browser children follow controller teardown.
        witness['forcedCleanup'] = process.poll() is None
        if process.poll() is None:
            process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
        (directory / 'evidence.json').write_text(json.dumps(witness, indent=2), encoding='utf-8')

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--app', type=Path, default=Path(__file__).resolve().parents[1] / 'MatrixDesktop/bin/Release/net10.0-windows/MatrixDesktop.exe')
    parser.add_argument('--output', type=Path)
    parser.add_argument('--scene', default='all', choices=['matrix','uc3d','zmatrix','all'])
    parser.add_argument('--renderer', default='both', choices=['regl','webgpu','both'])
    options = parser.parse_args()
    if not options.app.is_file():
        parser.error('build/publish MatrixDesktop first, or supply --app')
    output = options.output or Path(tempfile.mkdtemp(prefix='matrix-wallpaper-smoke-'))
    scenes = ['matrix','uc3d','zmatrix'] if options.scene == 'all' else [options.scene]
    renderers = ['regl','webgpu'] if options.renderer == 'both' else [options.renderer]
    for scene in scenes:
        for renderer in renderers:
            asyncio.run(run(options.app.resolve(), output.resolve(), scene, renderer))
            print(f'PASS {scene}/{renderer}: actual WorkerW, requested backend, animation, focus and clean exit', flush=True)
    print(f'Evidence: {output.resolve()}', flush=True)
