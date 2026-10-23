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
        else:
            super().do_GET()


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
            browser = pw.chromium.launch(channel=args.channel, args=flags)
            for backend in ["regl", "webgpu"] if args.renderer == "both" else [args.renderer]:
                context = browser.new_context(viewport={"width": 800, "height": 450}, device_scale_factor=1)
                page = context.new_page()
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
                page.goto(base + "&volumetric=true&showPictures=true&pictureSource=custom&pictureDelay=0&pictureDuration=8&pictureMonochrome=false&pictureCaptions=true&pictureDepth=1&showClock=true", wait_until="load")
                page.wait_for_function("window.matrixRuntimeDiagnostics?.pictureStatus === 'ready' && matrixRuntimeDiagnostics.pictureWeight > .98 && matrixRuntimeDiagnostics.frames > 5", timeout=20000)
                picture = Image.open(io.BytesIO(page.screenshot())).convert("RGB")
                pixels = list(picture.get_flattened_data() if hasattr(picture, "get_flattened_data") else picture.getdata())
                red = sum(r > 50 and r > g * 1.8 and r > b * 1.8 for r, g, b in pixels)
                green = sum(g > 50 and g > r * 1.8 and g > b * 1.8 for r, g, b in pixels)
                assert red > 300 and green > 300, f"photo RGB absent: red={red}, green={green}"
                assert page.locator("[data-uc3d-caption]").inner_text() == "<script>literal caption</script>"
                assert page.locator("[data-uc3d-clock]").inner_text(), "clock was empty"
                assert not page.locator(".uc3d-overlays script").count(), "caption interpreted markup"
                picture.save(OUTPUT / f"{backend}-photo.png")
                page.goto(base + "&volumetric=true&showPictures=true&pictureSource=custom&pictureDelay=0&pictureDuration=8&pictureMonochrome=false&pictureCaptions=false&pictureDepth=0", wait_until="load")
                page.wait_for_function("window.matrixRuntimeDiagnostics?.pictureStatus === 'ready' && matrixRuntimeDiagnostics.pictureWeight > .98", timeout=20000)
                flat = Image.open(io.BytesIO(page.screenshot())).convert("RGB")
                flat_pixels = list(flat.get_flattened_data() if hasattr(flat, "get_flattened_data") else flat.getdata())
                flat_green = sum(g > 50 and g > r * 1.8 and g > b * 1.8 for r, g, b in flat_pixels)
                assert green > flat_green * 1.1, f"picture depth expands bright relief [{shader_path}]: raised={green}, flat={flat_green}"
                # Lifetime behavior is observable: frames stop advancing and DOM overlays
                # disappear. A cleanup function that merely exists does not pass this.
                page.evaluate("matrixRuntimeDiagnostics.dispose()")
                count = page.evaluate("matrixRuntimeDiagnostics.frames")
                page.wait_for_timeout(150)
                assert page.evaluate("matrixRuntimeDiagnostics.frames") == count, "render callback survived disposal"
                assert not page.locator(".uc3d-overlays").count(), "overlay survived disposal"
                assert not errors, errors
                report = {"backend": backend, "redPixels": red, "greenPixels": green, "errors": errors}
                reports.append(report)
                print("PASS actual backend:", json.dumps(report))
                context.close()
            browser.close()
    except Exception as error:
        failures.append(str(error))
        print("FAIL UC3D browser:", error)
    finally:
        server.shutdown()
    (OUTPUT / "browser-report.json").write_text(json.dumps({"reports": reports, "failures": failures, "webgpuUnavailable": unavailable}, indent=2), encoding="utf-8")
    return 1 if failures else 2 if unavailable else 0


if __name__ == "__main__":
    raise SystemExit(main())
