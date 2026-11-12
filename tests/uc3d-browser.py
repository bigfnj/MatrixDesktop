"""Exercise UC3D shaders on actual WebGL and WebGPU contexts, with synthetic media.

No donor assets, personal pictures, desktop capture, camera, or audio files are read.
WebGPU absence is a cannot-verify result, never a fallback pass.
"""
import functools
import argparse
import http.server
import io
import json
import hashlib
import threading
from pathlib import Path

from PIL import Image, ImageChops, ImageStat
from playwright.sync_api import sync_playwright

import browser_launch

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "MatrixDesktop" / "web"
OUTPUT = ROOT / "artifacts" / "uc3d"
FIXTURE = b'<svg xmlns="http://www.w3.org/2000/svg" width="320" height="180"><path fill="#f00" d="M0 0h160v180H0z"/><path fill="#0f0" d="M160 0h160v180H160z"/><circle fill="#00f" cx="80" cy="60" r="28"/></svg>'


class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        if self.path == "/favicon.ico":
            self.send_response(204)
            self.end_headers()
        elif self.path == "/fixture.svg":
            self.send_response(200)
            self.send_header("Content-Type", "image/svg+xml")
            self.end_headers()
            self.wfile.write(FIXTURE)
        elif self.path.startswith("/desktop-") and self.path.endswith(".png"):
            width, height = (int(n) for n in self.path[len("/desktop-"):-len(".png")].split("x"))
            body = desktop_png(width, height)
            self.send_response(200)
            self.send_header("Content-Type", "image/png")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        else:
            super().do_GET()


# R2-28: the REGL pass fits the desktop snapshot to MAX_TEXTURE_SIZE, as AUD-71 made the WebGPU
# pass do. A synthetic desktop one pixel over the limit on either axis must render, and one at
# the limit uploads unchanged. Red over blue checks the orientation: WebGL ignores UNPACK_FLIP_Y
# for an ImageBitmap, so a resized upload without the bitmap's own flip shows blue on top.
DESKTOPS = {}
DESKTOP_UPLOADS = """(() => {
    const uploads = window.__desktopUploads = [], sources = window.__desktopSources = [];
    const texImage2D = WebGLRenderingContext.prototype.texImage2D;
    WebGLRenderingContext.prototype.texImage2D = function (...args) {
        const source = args.length === 6 ? args[5] : null;
        if (source instanceof HTMLImageElement || source instanceof ImageBitmap) {
            uploads.push({ kind: source.constructor.name, width: source.naturalWidth ?? source.width, height: source.naturalHeight ?? source.height });
            sources.push(source);
        }
        return texImage2D.apply(this, args);
    };
})();"""
DESKTOP_STATE = """() => { const d = window.matrixRuntimeDiagnostics ?? {};
    return { ready: d.ready, frames: d.frames, disposed: d.disposed, errors: d.errors,
        uploads: (window.__desktopUploads ?? []).map((upload, i) => ({ ...upload,
            closed: window.__desktopSources[i] instanceof ImageBitmap ? window.__desktopSources[i].width === 0 : null })) }; }"""


def desktop_png(width, height):
    if (width, height) not in DESKTOPS:
        image = Image.new("RGB", (width, height), (0, 0, 255))
        image.paste((255, 0, 0), (0, 0, width, height // 2))
        buffer = io.BytesIO()
        image.save(buffer, "PNG")
        DESKTOPS[width, height] = buffer.getvalue()
    return DESKTOPS[width, height]


def check_desktop_snapshot(browser, origin):
    url = f"{origin}/index.html?scene=uc3d&renderer=regl&suppressWarnings=true&resolution=1&numColumns=48&bloomStrength=0&codeRotation=false&forwardMovement=false&volumetric=false&desktopBackground=true"
    probe = browser.new_page()
    limit = probe.evaluate("() => { const gl = document.createElement('canvas').getContext('webgl'); return gl.getParameter(gl.MAX_TEXTURE_SIZE); }")
    probe.close()
    results = []
    for width, height in ((limit + 1, 600), (600, limit + 1), (limit, 600)):
        label = f"{width}x{height} desktop (MAX_TEXTURE_SIZE {limit})"
        context = browser.new_context(viewport={"width": 800, "height": 450}, device_scale_factor=1)
        try:
            page = context.new_page()
            errors = []
            page.on("pageerror", lambda error: errors.append(str(error)))
            page.on("console", lambda message: errors.append(message.text) if message.type == "error" else None)
            page.add_init_script(DESKTOP_UPLOADS)
            page.add_init_script("window.matrixHostBootstrap=" + json.dumps({"desktopUrl": f"{origin}/desktop-{width}x{height}.png"}))
            page.goto(url, wait_until="load")
            page.wait_for_function("() => { const d = window.matrixRuntimeDiagnostics; return d && (d.frames > 5 || d.disposed || d.errors.length > 0); }", timeout=20000)
            if not results:
                served = bytes(page.evaluate("async url => Array.from(new Uint8Array(await (await fetch(url, {cache:'no-store'})).arrayBuffer()))", f"{origin}/js/regl/uc3dPass.js"))
                assert served == (WEB / "js" / "regl" / "uc3dPass.js").read_bytes(), "served REGL UC3D pass differs from source on disk"
                print("PASS WITNESS regl/uc3dPass.js", hashlib.sha256(served).hexdigest())
            state = page.evaluate(DESKTOP_STATE)
            assert state["ready"] is True and state["frames"] > 5 and not state["disposed"] and state["errors"] == [], f"{label} did not render: {state}"
            frames = state["frames"]
            page.wait_for_timeout(250)
            assert page.evaluate("matrixRuntimeDiagnostics.frames") > frames, f"{label}: frames stopped advancing"
            # Witness: the glyph atlas uploads first and the desktop snapshot second.
            assert len(state["uploads"]) == 2, f"{label}: expected the atlas and desktop uploads, saw {state['uploads']}"
            desktop = state["uploads"][1]
            if max(width, height) > limit:
                assert desktop["kind"] == "ImageBitmap", f"{label} must upload a resized bitmap: {desktop}"
                assert max(desktop["width"], desktop["height"]) <= limit and abs(desktop["width"] * height - desktop["height"] * width) <= max(width, height), f"{label}: resized to {desktop['width']}x{desktop['height']}, aspect or limit lost"
                assert desktop["closed"] is True, f"{label}: the resized bitmap must be released after upload"
            else:
                assert desktop == {"kind": "HTMLImageElement", "width": width, "height": height, "closed": None}, f"{label} must upload unchanged: {desktop}"
            shot = Image.open(io.BytesIO(page.screenshot())).convert("RGB")
            top = ImageStat.Stat(shot.crop((0, 20, 800, 200))).median
            bottom = ImageStat.Stat(shot.crop((0, 250, 800, 430))).median
            assert top[0] > top[2] + 80 and bottom[2] > bottom[0] + 80, f"{label}: the desktop top half must stay on top (red over blue); top median {top}, bottom median {bottom}"
            assert not errors, f"{label}: {errors}"
            results.append({"desktop": f"{width}x{height}", "upload": desktop, "frames": frames, "top": top, "bottom": bottom})
        finally:
            context.close()
    print("PASS desktop snapshot fits MAX_TEXTURE_SIZE:", json.dumps(results))
    return results


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--channel", default=None, help="Use an already installed Playwright browser channel, e.g. msedge")
    parser.add_argument("--renderer", choices=["regl", "webgpu", "both"], default="both")
    args = parser.parse_args()
    print(f"SOURCE {WEB}")
    OUTPUT.mkdir(parents=True, exist_ok=True)
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Handler, directory=str(WEB)))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    origin = f"http://127.0.0.1:{server.server_port}"
    failures = []
    reports = []
    unavailable = False
    try:
        with sync_playwright() as pw:
            flags = ["--enable-unsafe-webgpu", "--ignore-gpu-blocklist"]
            # The CI lane deliberately exercises WebGL through the CPU implementation;
            # the host both-backend lane uses the installed driver and asserts WebGPU.
            flags += ["--use-angle=swiftshader", "--enable-unsafe-swiftshader"] if args.renderer == "regl" and not args.channel else ["--use-angle=d3d11"]
            browser = browser_launch.launch(pw.chromium, channel=args.channel, args=flags)
            # Its own failure record, so neither this check nor the backend loop masks the other.
            if args.renderer in ("regl", "both"):
                try:
                    reports.append({"backend": "regl", "desktopSnapshot": check_desktop_snapshot(browser, origin)})
                except Exception as error:
                    failures.append(f"desktop snapshot: {error}")
                    print("FAIL UC3D desktop snapshot:", error)
            for backend in ["regl", "webgpu"] if args.renderer == "both" else [args.renderer]:
                context = browser.new_context(viewport={"width": 800, "height": 450}, device_scale_factor=1)
                page = context.new_page()
                # Pixel assertions can outlast a short picture episode on a slow CI
                # worker. Hold its displayed RAF timestamp while inspecting that frame.
                # Resume before the teardown assertion so a leaked loop cannot pass it.
                page.add_init_script("""(() => {
                    const raf=window.requestAnimationFrame.bind(window);
                    const clock=window.__uc3dSmokeClock={held:null,last:0};
                    window.requestAnimationFrame=callback=>raf(now=>{
                        clock.last=now; callback(clock.held ?? now);
                    });
                })();""")
                errors = []
                page.on("pageerror", lambda error: errors.append(str(error)))
                page.on("console", lambda message: errors.append(message.text) if message.type == "error" else None)
                page.on("console", lambda message: print("browser warning:", message.text) if message.type == "warning" else None)
                bootstrap = {"images": [{"id": "fixture", "url": f"{origin}/fixture.svg", "caption": "<script>literal caption</script>", "group": "custom"}], "desktopUrl": None, "audioUrl": None, "preview": True}
                page.add_init_script("window.matrixHostBootstrap=" + json.dumps(bootstrap))
                base = f"{origin}/index.html?scene=uc3d&renderer={backend}&suppressWarnings=true&resolution=1&numColumns=48&bloomStrength=0&codeRotation=false&forwardMovement=false"
                page.goto(base + "&volumetric=false", wait_until="load")
                if backend == "webgpu":
                    supported = page.evaluate("async () => !!navigator.gpu && !!(await navigator.gpu.requestAdapter())")
                    if not supported:
                        print("CANNOT VERIFY WebGPU: browser has no adapter")
                        unavailable = True
                        context.close()
                        continue
                page.wait_for_function("window.matrixRuntimeDiagnostics?.frames > 5", timeout=20000)
                diagnostics = page.evaluate("({scene:matrixRuntimeDiagnostics.scene,backend:matrixRuntimeDiagnostics.backend,errors:matrixRuntimeDiagnostics.errors})")
                assert diagnostics["backend"] == backend, diagnostics
                assert not diagnostics["errors"], diagnostics
                shader_path = "shaders/glsl/uc3d.vert.glsl" if backend == "regl" else "shaders/wgsl/uc3d.wgsl"
                served_shader = bytes(page.evaluate("async url => Array.from(new Uint8Array(await (await fetch(url, {cache:'no-store'})).arrayBuffer()))", f"{origin}/{shader_path}"))
                assert served_shader == (WEB / shader_path).read_bytes(), f"renderer source witness differs from source on disk: {shader_path}"
                print("SHADER WITNESS", backend, hashlib.sha256(served_shader).hexdigest())
                first = Image.open(io.BytesIO(page.screenshot())).convert("RGB")
                assert sum(ImageStat.Stat(first).var) > 30, "blank 2D frame"
                page.wait_for_timeout(250)
                second = Image.open(io.BytesIO(page.screenshot())).convert("RGB")
                assert sum(ImageStat.Stat(ImageChops.difference(first, second)).mean) > 0.2, "rain did not animate"
                second.save(OUTPUT / f"{backend}-2d.png")

                # This must be true photo RGB in the glyph field, rather than the normal
                # green palette or a raw picture placed behind otherwise unchanged rain.
                # R3-06: both relief legs hold only a fully formed picture. pictureDuration 8
                # fades in over 1.07 s, about 0.0156 weight per 60 Hz frame, so a hold above .98
                # could land at 0.984, where each glyph keeps 1.6% of its rain position and
                # scatters green past the picture: the flat count rose about 10% and the ratio
                # failed with nothing broken. The weight then stays 1 for 5.6 s, and each held
                # frame's weight is read back so a hold that lands early fails by name.
                page.goto(base + "&volumetric=true&showPictures=true&pictureSource=custom&pictureDelay=0&pictureDuration=8&pictureMonochrome=false&pictureCaptions=true&pictureDepth=1&showClock=true", wait_until="load")
                page.wait_for_function("window.matrixRuntimeDiagnostics?.pictureStatus === 'ready' && matrixRuntimeDiagnostics.pictureWeight >= .999 && matrixRuntimeDiagnostics.frames > 5", timeout=20000)
                page.evaluate("__uc3dSmokeClock.held=__uc3dSmokeClock.last")
                picture = Image.open(io.BytesIO(page.screenshot())).convert("RGB")
                raised_weight = page.evaluate("matrixRuntimeDiagnostics.pictureWeight")
                assert raised_weight >= .999, f"raised relief frame held before the picture formed: weight={raised_weight}"
                pixels = list(picture.get_flattened_data() if hasattr(picture, "get_flattened_data") else picture.getdata())
                red = sum(r > 50 and r > g * 1.8 and r > b * 1.8 for r, g, b in pixels)
                green = sum(g > 50 and g > r * 1.8 and g > b * 1.8 for r, g, b in pixels)
                assert red > 300 and green > 300, f"photo RGB absent: red={red}, green={green}"
                caption = page.locator("[data-uc3d-caption]").inner_text()
                assert caption == "<script>literal caption</script>", f"photo caption changed during frame inspection: {caption!r}"
                assert page.locator("[data-uc3d-clock]").inner_text(), "clock was empty"
                assert not page.locator(".uc3d-overlays script").count(), "caption interpreted markup"
                picture.save(OUTPUT / f"{backend}-photo.png")
                page.goto(base + "&volumetric=true&showPictures=true&pictureSource=custom&pictureDelay=0&pictureDuration=8&pictureMonochrome=false&pictureCaptions=false&pictureDepth=0", wait_until="load")
                page.wait_for_function("window.matrixRuntimeDiagnostics?.pictureStatus === 'ready' && matrixRuntimeDiagnostics.pictureWeight >= .999", timeout=20000)
                page.evaluate("__uc3dSmokeClock.held=__uc3dSmokeClock.last")
                flat = Image.open(io.BytesIO(page.screenshot())).convert("RGB")
                flat_weight = page.evaluate("matrixRuntimeDiagnostics.pictureWeight")
                assert flat_weight >= .999, f"flat relief frame held before the picture formed: weight={flat_weight}"
                flat_pixels = list(flat.get_flattened_data() if hasattr(flat, "get_flattened_data") else flat.getdata())
                flat_green = sum(g > 50 and g > r * 1.8 and g > b * 1.8 for r, g, b in flat_pixels)
                assert green > flat_green * 1.1, f"picture depth expands bright relief [{shader_path}]: raised={green}, flat={flat_green}"
                # Lifetime behavior is observable: frames stop advancing and DOM overlays
                # disappear. A cleanup function that merely exists does not pass this.
                held_frames = page.evaluate("matrixRuntimeDiagnostics.frames")
                page.evaluate("__uc3dSmokeClock.held=null")
                page.wait_for_function("frames => matrixRuntimeDiagnostics.frames > frames", arg=held_frames, timeout=10000)
                page.evaluate("matrixRuntimeDiagnostics.dispose()")
                count = page.evaluate("matrixRuntimeDiagnostics.frames")
                page.wait_for_timeout(150)
                assert page.evaluate("matrixRuntimeDiagnostics.frames") == count, "render callback survived disposal"
                assert not page.locator(".uc3d-overlays").count(), "overlay survived disposal"
                assert not errors, errors
                report = {"backend": backend, "redPixels": red, "greenPixels": green, "flatGreenPixels": flat_green, "raisedWeight": raised_weight, "flatWeight": flat_weight, "errors": errors}
                reports.append(report)
                print("PASS actual backend:", json.dumps(report))
                context.close()
            browser.close()
    except browser_launch.LaunchArgumentsError:
        raise  # a harness error, never a product FAIL: the entry point reports it (MDST-05)
    except Exception as error:
        failures.append(str(error))
        print("FAIL UC3D browser:", error)
    finally:
        server.shutdown()
    (OUTPUT / "browser-report.json").write_text(json.dumps({"reports": reports, "failures": failures, "webgpuUnavailable": unavailable}, indent=2), encoding="utf-8")
    return 1 if failures else 2 if unavailable else 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except browser_launch.LaunchArgumentsError as error:
        # The browser did not get the arguments the helper verifies, so nothing it ran is a
        # verdict on the product: a harness error, exit 3 with this line for the gate (MDST-05).
        print(f"HARNESS ERROR: uc3d-browser.py: {error}", flush=True)
        raise SystemExit(3)
