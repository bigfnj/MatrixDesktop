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
import threading
from pathlib import Path
from urllib.parse import urlencode, urlsplit

from PIL import Image, ImageChops
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "MatrixDesktop" / "web"


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
    try:
        with sync_playwright() as pw:
            flags = ["--enable-unsafe-webgpu", "--ignore-gpu-blocklist"]
            flags += ["--use-angle=swiftshader", "--enable-unsafe-swiftshader"] if args.renderer == "regl" and not args.channel else ["--use-angle=d3d11"]
            browser = pw.chromium.launch(channel=args.channel, args=flags)
            for backend in (["regl", "webgpu"] if args.renderer == "both" else [args.renderer]):
                context = browser.new_context(device_scale_factor=1)
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
                    assert all(2 <= s["fallSpeed"] <= 2+(params["uc3dFallSpeed"]+29)*.1 for s in report["streams"]), report
                    if backend == "webgpu" and not page.evaluate("!!navigator.gpu"):
                        raise AssertionError("WebGPU unavailable")
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
                    assert not errors and not report["errors"], errors or report
                    page.evaluate("matrixRuntimeDiagnostics.dispose()")
                    assert page.evaluate("matrixRuntimeDiagnostics.inspect().disposed"), "original CPU scene survived disposal"
                    before = page.evaluate("matrixRuntimeDiagnostics.frames")
                    page.wait_for_timeout(40)
                    assert page.evaluate("matrixRuntimeDiagnostics.frames") == before, "render loop survived disposal"
                    (folder / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
                    reports.append(report)
                    print(f"PASS {backend} {name}: {report['activeStreams']} streams, {report['glyphInstances']} glyphs, {visible} visible pixels", flush=True)
                    page.close()
                context.close()
            browser.close()
    finally:
        server.shutdown()
        (output / "report.json").write_text(json.dumps(reports, indent=2), encoding="utf-8")
    assert reports, "no calibration cases selected"
    print(f"PASS {len(reports)} actual-backend calibration cases", flush=True)


if __name__ == "__main__":
    main()
