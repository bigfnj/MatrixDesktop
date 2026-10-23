"""Run the served ZMatrix scene on actual WebGL/WebGPU contexts.

Uses only bundled assets and a synthetic color background. Native font/wallpaper
integration is checked separately against the published Windows application.
"""
import argparse
import functools
import hashlib
import http.server
import io
import json
import threading
from pathlib import Path

from PIL import Image, ImageChops, ImageStat
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "MatrixDesktop" / "web"
OUTPUT = ROOT / "artifacts" / "zmatrix-browser"


class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        if self.path == "/favicon.ico":
            self.send_response(204)
            self.end_headers()
        else:
            super().do_GET()


def canvas_image(page):
    return Image.open(io.BytesIO(page.locator("canvas").first.screenshot())).convert("RGB")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--channel", default=None)
    parser.add_argument("--renderer", choices=["regl", "webgpu", "both"], default="both")
    args = parser.parse_args()
    OUTPUT.mkdir(parents=True, exist_ok=True)
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Handler, directory=str(WEB)))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    origin = f"http://127.0.0.1:{server.server_port}"
    reports, failures = [], []
    unavailable = False
    try:
        with sync_playwright() as playwright:
            flags = ["--enable-unsafe-webgpu", "--ignore-gpu-blocklist"]
            flags += ["--use-angle=swiftshader", "--enable-unsafe-swiftshader"] if args.renderer == "regl" and not args.channel else ["--use-angle=d3d11"]
            browser = playwright.chromium.launch(channel=args.channel, args=flags)
            for backend in (["regl", "webgpu"] if args.renderer == "both" else [args.renderer]):
                context = browser.new_context(viewport={"width": 640, "height": 360}, device_scale_factor=1)
                page = context.new_page()
                errors = []
                page.on("pageerror", lambda error: errors.append(str(error)))
                page.on("console", lambda message: errors.append(message.text) if message.type == "error" else None)
                try:
                    query = (f"scene=zmatrix&renderer={backend}&suppressWarnings=true&resolution=1&fps=30"
                             "&zmMaxStream=80&zmRefreshTime=20&zmBackgroundMode=color&zmBackgroundColor=9,18,27")
                    page.goto(f"{origin}/index.html?{query}", wait_until="load")
                    if backend == "webgpu" and not page.evaluate("async () => !!navigator.gpu && !!(await navigator.gpu.requestAdapter())"):
                        print("CANNOT VERIFY ZMatrix WebGPU: no adapter", flush=True)
                        unavailable = True
                        continue
                    page.wait_for_function("window.matrixRuntimeDiagnostics?.ready && matrixRuntimeDiagnostics.frames > 8", timeout=30000)
                    diagnostics = page.evaluate("({scene:matrixRuntimeDiagnostics.scene,backend:matrixRuntimeDiagnostics.backend,errors:matrixRuntimeDiagnostics.errors,warnings:matrixRuntimeDiagnostics.warnings})")
                    assert diagnostics["scene"] == "zmatrix" and diagnostics["backend"] == backend, diagnostics
                    assert not diagnostics["errors"] and not errors, (diagnostics, errors)
                    for relative in ("js/config.js", "js/hostPlayback.js", "js/hostPointer.js", "js/zmatrix/settings.js",
                                     "js/zmatrix/scene.js", "js/zmatrix/surface.js", "js/zmatrix/fonts.js", "js/zmatrix/runtime.js",
                                     f"js/{backend}/zmatrixPass.js"):
                        deployed = bytes(page.evaluate("async path => Array.from(new Uint8Array(await (await fetch(path,{cache:'no-store'})).arrayBuffer()))", relative))
                        source = (WEB / relative).read_bytes()
                        assert deployed == source, f"served source mismatch: {relative}"
                        print(f"SOURCE {relative} sha256={hashlib.sha256(source).hexdigest()}", flush=True)
                    first = canvas_image(page)
                    assert max(ImageStat.Stat(first).stddev) > 3, "ZMatrix rendered a uniform fill"
                    first.save(OUTPUT / f"{backend}-running.png")
                    page.wait_for_timeout(350)
                    second = canvas_image(page)
                    assert ImageChops.difference(first, second).getbbox(), "ZMatrix animation did not change pixels"

                    page.evaluate("window.dispatchEvent(new CustomEvent('matrix-host-state',{detail:{paused:true}}))")
                    page.wait_for_timeout(100)
                    paused = canvas_image(page)
                    frame_count = page.evaluate("matrixRuntimeDiagnostics.frames")
                    page.wait_for_timeout(250)
                    assert page.evaluate("matrixRuntimeDiagnostics.frames") == frame_count, "paused scene continued rendering"
                    assert ImageChops.difference(paused, canvas_image(page)).getbbox() is None, "paused scene changed pixels"
                    page.evaluate("window.dispatchEvent(new CustomEvent('matrix-host-state',{detail:{paused:false}}))")
                    page.wait_for_function("count => matrixRuntimeDiagnostics.frames > count + 2", arg=frame_count)

                    page.set_viewport_size({"width": 480, "height": 270})
                    page.wait_for_function("document.querySelector('canvas').width === 480 && document.querySelector('canvas').height === 270")
                    assert max(ImageStat.Stat(canvas_image(page)).stddev) > 3, "resized scene lost its content"
                    assert not errors, errors
                    page.evaluate("matrixRuntimeDiagnostics.dispose()")
                    disposed_frames = page.evaluate("matrixRuntimeDiagnostics.frames")
                    page.wait_for_timeout(150)
                    assert page.evaluate("matrixRuntimeDiagnostics.disposed && !matrixRuntimeDiagnostics.ready"), "disposal not reported"
                    assert page.evaluate("matrixRuntimeDiagnostics.frames") == disposed_frames, "disposed scene continued rendering"
                    reports.append({"backend": backend, "frames": disposed_frames, "checks": ["source", "pixels", "animation", "pause", "resume", "resize", "dispose"], "warnings": diagnostics["warnings"]})
                    print(f"PASS ZMatrix {backend}: rendered, animated, paused, resumed, resized and disposed", flush=True)
                except Exception as error:
                    failures.append(f"{backend}: {error}")
                    print(f"FAIL ZMatrix {backend}: {error}", flush=True)
                finally:
                    context.close()
            browser.close()
    finally:
        server.shutdown()
        server.server_close()
    (OUTPUT / "report.json").write_text(json.dumps({"reports": reports, "failures": failures, "unavailable": unavailable}, indent=2), encoding="utf-8")
    return 1 if failures else 2 if unavailable else 0


if __name__ == "__main__":
    raise SystemExit(main())
