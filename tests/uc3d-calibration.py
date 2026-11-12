"""Render original-unit controls on both real backends with a deterministic clock.

Public runs use bundled glyphs. --donor-atlas is a private local comparison input;
the atlas and resulting captures must stay outside the repository.
"""
import argparse
import functools
import hashlib
import http.server
import io
import json
import os
import threading
from pathlib import Path
from urllib.parse import urlencode, urlsplit

from PIL import Image, ImageChops
from playwright.sync_api import sync_playwright

import browser_launch

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "MatrixDesktop" / "web"

# MD_UC3D_HIDE_WEBGPU_ADAPTER=1 simulates a browser with no WebGPU adapter, so the
# CANNOT VERIFY path has a regression case. It is an environment variable, not a flag,
# because argparse exits 2 on an unknown flag, which reads as "not verified" too.
HIDE_ADAPTER = os.environ.get("MD_UC3D_HIDE_WEBGPU_ADAPTER") == "1"
HIDE_ADAPTER_SCRIPT = """(() => {
    if (window.GPU) Object.defineProperty(GPU.prototype, "requestAdapter", {configurable: true, writable: true, value: async () => null});
})();"""


def cases():
    base = dict(scene="uc3d", uc3dControls="original", uc3dAmount=500,
                uc3dGlyphSize=12, uc3dFallSpeed=6, uc3dForwardSpeed=6,
                volumetric="true", codeRotation="false", sidewaysMovement="false",
                randomForwardMovement="false", forwardMovement="false",
                bloomStrength=0, resolution=1, suppressWarnings="true")
    result = []
    for key, name, values in (
            ("uc3dAmount", "amount", [100, 500, 800, 1500]),
            ("uc3dGlyphSize", "glyph", [10, 12, 25, 40]),
            ("uc3dFallSpeed", "fall", [0, 6, 75, 150]),
            ("uc3dForwardSpeed", "forward", [0, 6, 60, 120])):
        for value in values:
            overrides = {key: value}
            if name == "forward":
                overrides["forwardMovement"] = "true"
            result.append((f"{name}-{value}", dict(base, **overrides), (800, 450)))
    for name, overrides, size in (
            ("sideways", dict(sidewaysMovement="true"), (800, 450)),
            ("random-forward", dict(forwardMovement="true", randomForwardMovement="true"), (800, 450)),
            ("rotation", dict(codeRotation="true"), (800, 450)),
            ("captured", dict(codeRotation="true", sidewaysMovement="true", forwardMovement="true", randomForwardMovement="true"), (800, 450)),
            ("flat", dict(volumetric="false"), (800, 450)),
            ("portrait", {}, (450, 800))):
        result.append((name, dict(base, **overrides), size))
    return result


class Handler(http.server.SimpleHTTPRequestHandler):
    atlas = None

    def log_message(self, *args):
        pass

    def do_GET(self):
        if urlsplit(self.path).path == "/comparison-atlas" and self.atlas:
            self.send_response(200)
            self.send_header("Content-Type", "image/bmp")
            self.end_headers()
            self.wfile.write(self.atlas.read_bytes())
        elif self.path == "/adapter-probe":
            body = b"<!doctype html><title>adapter probe</title>"
            self.send_response(200)
            self.send_header("Content-Type", "text/html")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        elif self.path == "/favicon.ico":
            self.send_response(204)
            self.end_headers()
        else:
            super().do_GET()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--channel", default=None)
    parser.add_argument("--renderer", choices=["regl", "webgpu", "both"], default="both")
    parser.add_argument("--donor-atlas", type=Path)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts" / "uc3d-calibration")
    parser.add_argument("--only", help="Comma-separated case names")
    parser.add_argument("--motion", action="store_true", help="Also save a 12 FPS sequence from 20 to 22 seconds")
    args = parser.parse_args()
    output = args.output.resolve()
    if args.donor_atlas:
        assert not output.is_relative_to(ROOT), "Donor comparison captures must remain private"
        Handler.atlas = args.donor_atlas.resolve()
    output.mkdir(parents=True, exist_ok=True)
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Handler, directory=str(WEB)))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    origin = f"http://127.0.0.1:{server.server_port}"
    selected = set(args.only.split(",")) if args.only else None
    reports = []
    webgpu_unavailable = False
    try:
        with sync_playwright() as pw:
            flags = ["--enable-unsafe-webgpu", "--ignore-gpu-blocklist"]
            # A hidden adapter means nothing should touch the GPU, so it gets SwiftShader too.
            software = (args.renderer == "regl" or HIDE_ADAPTER) and not args.channel
            flags += ["--use-angle=swiftshader", "--enable-unsafe-swiftshader"] if software else ["--use-angle=d3d11"]
            browser = browser_launch.launch(pw.chromium, channel=args.channel, args=flags)
            for backend in (["regl", "webgpu"] if args.renderer == "both" else [args.renderer]):
                context = browser.new_context(device_scale_factor=1)
                if HIDE_ADAPTER:
                    context.add_init_script(HIDE_ADAPTER_SCRIPT)
                if backend == "webgpu":
                    # Without an adapter main.js falls back to REGL and the backend assertion
                    # below would report a product failure for an environment gap.
                    probe = context.new_page()
                    probe.goto(origin + "/adapter-probe", wait_until="load")
                    has_adapter = probe.evaluate("async () => !!navigator.gpu && !!(await navigator.gpu.requestAdapter())")
                    probe.close()
                    if not has_adapter:
                        print("CANNOT VERIFY WebGPU: browser has no adapter", flush=True)
                        webgpu_unavailable = True
                        context.close()
                        continue
                context.add_init_script("""(() => {
                    const raf=window.requestAnimationFrame.bind(window);
                    window.__calibrationTime=0;
                    window.requestAnimationFrame=callback=>raf(()=>callback(window.__calibrationTime));
                    Date.now=()=>123456; Math.random=()=>0.25;
                })();""")
                if args.donor_atlas:
                    context.add_init_script("window.matrixHostBootstrap=" + json.dumps(dict(images=[], preview=True, glyphUrl=origin+"/comparison-atlas")))
                for name, params, dimensions in cases():
                    if selected and name not in selected:
                        continue
                    page = context.new_page()
                    page.set_viewport_size(dict(zip(["width", "height"], dimensions)))
                    errors = []
                    page.on("pageerror", lambda error: errors.append(str(error)))
                    page.on("console", lambda message: errors.append(message.text) if message.type == "error" else None)
                    query = dict(params, renderer=backend)
                    if args.donor_atlas:
                        query["donorGlyphs"] = "true"
                    page.goto(origin + "/index.html?" + urlencode(query), wait_until="load")
                    page.wait_for_function("window.matrixRuntimeDiagnostics?.ready", timeout=25000)
                    for second in range(1, 21):
                        page.evaluate("second => __calibrationTime=second*1000", second)
                        page.wait_for_function("second=>matrixRuntimeDiagnostics.simulationTime >= second", arg=second, timeout=25000)
                    report = page.evaluate("({backend:matrixRuntimeDiagnostics.backend,controls:matrixRuntimeDiagnostics.controls,glyphInstances:matrixRuntimeDiagnostics.glyphInstances,errors:matrixRuntimeDiagnostics.errors,...matrixRuntimeDiagnostics.inspect()})")
                    assert report["backend"] == backend and report["controls"] == "original", report
                    assert report["targetStreams"] == report["activeStreams"] == params["uc3dAmount"], report
                    assert abs(report["glyphHalfSize"] - params["uc3dGlyphSize"]*.01) < 1e-9, report
                    assert report["glyphInstances"] <= params["uc3dAmount"]*39, report
                    assert isinstance(report.get("streams"), list) and report["streams"], f"missing actual stream samples: {name}/{backend}"
                    assert all(2 <= s["fallSpeed"] <= 2+(params["uc3dFallSpeed"]+29)*.1 for s in report["streams"]), report
                    for relative in ("js/uc3d/native.js", "shaders/glsl/uc3d.vert.glsl" if backend == "regl" else "shaders/wgsl/uc3d.wgsl"):
                        served = bytes(page.evaluate("async url=>Array.from(new Uint8Array(await (await fetch(url)).arrayBuffer()))", relative))
                        assert served == (WEB / relative).read_bytes(), f"source witness mismatch: {relative}"
                        report.setdefault("sourceWitnesses", {})[relative] = hashlib.sha256(served).hexdigest()
                    folder = output / backend / name
                    folder.mkdir(parents=True, exist_ok=True)
                    first_png = page.locator("canvas").first.screenshot()
                    first = Image.open(io.BytesIO(first_png)).convert("RGB")
                    pixels = first.get_flattened_data() if hasattr(first, "get_flattened_data") else first.getdata()
                    visible = sum(max(pixel) > 30 for pixel in pixels)
                    assert visible > 20, f"blank original-mode frame: {name}/{backend} [{relative}], visible={visible}"
                    (folder / "t020.00.png").write_bytes(first_png)
                    if args.motion:
                        motion = [first.copy()]
                        second_png = None
                        for index in range(1, 25):
                            seconds = 20+index/12
                            page.evaluate("seconds=>__calibrationTime=seconds*1000", seconds)
                            page.wait_for_function("seconds=>matrixRuntimeDiagnostics.simulationTime >= seconds-1e-9", arg=seconds)
                            capture = page.locator("canvas").first.screenshot()
                            motion.append(Image.open(io.BytesIO(capture)).convert("RGB"))
                            if index == 3:
                                second_png = capture
                                report["after"] = page.evaluate("matrixRuntimeDiagnostics.inspect()")
                        motion[0].save(folder / "motion.gif", save_all=True, append_images=motion[1:], duration=1000/12, loop=0)
                    else:
                        page.evaluate("__calibrationTime=20250")
                        page.wait_for_function("matrixRuntimeDiagnostics.simulationTime >= 20.25")
                        second_png = page.locator("canvas").first.screenshot()
                    second = Image.open(io.BytesIO(second_png)).convert("RGB")
                    assert ImageChops.difference(first, second).getbbox(), f"movement frozen: {name}/{backend}"
                    (folder / "t020.25.png").write_bytes(second_png)
                    if "after" not in report:
                        report["after"] = page.evaluate("matrixRuntimeDiagnostics.inspect()")
                    report.update(name=name, dimensions=dimensions, parameters=params, visiblePixels=visible)
                    page.evaluate("matrixRuntimeDiagnostics.dispose()")
                    assert page.evaluate("matrixRuntimeDiagnostics.inspect().disposed"), "original CPU scene survived disposal"
                    before = page.evaluate("matrixRuntimeDiagnostics.frames")
                    page.wait_for_timeout(40)
                    assert page.evaluate("matrixRuntimeDiagnostics.frames") == before, "render loop survived disposal"
                    # AUD-55: checked after teardown, so errors raised by disposal are seen too.
                    assert not errors and not report["errors"], errors or report
                    (folder / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
                    reports.append(report)
                    print(f"PASS {backend} {name}: {report['activeStreams']} streams, {report['glyphInstances']} glyphs, {visible} visible pixels", flush=True)
                    page.close()
                context.close()
            browser.close()
    finally:
        server.shutdown()
        (output / "report.json").write_text(json.dumps({"reports": reports, "webgpuUnavailable": webgpu_unavailable}, indent=2), encoding="utf-8")
    if reports:
        print(f"PASS {len(reports)} actual-backend calibration cases", flush=True)
    elif not webgpu_unavailable:
        raise AssertionError("no calibration cases selected")
    return 2 if webgpu_unavailable else 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except browser_launch.LaunchArgumentsError as error:
        # The browser did not get the arguments the helper verifies, so nothing it ran is a
        # verdict on the product: a harness error, exit 3 with this line for the gate (MDST-05).
        print(f"HARNESS ERROR: uc3d-calibration.py: {error}", flush=True)
        raise SystemExit(3)
