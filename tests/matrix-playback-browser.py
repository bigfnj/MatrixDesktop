"""Verify the original Matrix renderer survives an initial and mid-run pause."""
import argparse
import functools
import http.server
import io
import threading
from pathlib import Path

from PIL import Image, ImageChops, ImageStat
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "MatrixDesktop" / "web"

INITIAL_PAUSE = """(() => {
  window.matrixHostPlaybackState = {paused:true};
  window.matrixHostBootstrapReady = new Promise(resolve => { window.__resolveNativeBootstrap = resolve; });
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    addEventListener(type, listener) {
      // Native state may arrive before renderer modules attach their listeners.
    }, removeEventListener() {}
  };
  window.__matrixTickUniforms = [];
  for (const Context of [window.WebGLRenderingContext,window.WebGL2RenderingContext]) {
    if (!Context) continue;
    const locations = new WeakSet(), get = Context.prototype.getUniformLocation, set = Context.prototype.uniform1f;
    Context.prototype.getUniformLocation = function(program,name) {
      const location = get.call(this,program,name);
      if (name === 'tick' && location) locations.add(location);
      return location;
    };
    Context.prototype.uniform1f = function(location,value) {
      if (location && locations.has(location) && window.__matrixTickUniforms.length < 512) window.__matrixTickUniforms.push(value);
      return set.call(this,location,value);
    };
  }
})();"""


class Handler(http.server.SimpleHTTPRequestHandler):
    regl_main_override = None
    def log_message(self, *args):
        pass

    def do_GET(self):
        if self.path == "/js/regl/main.js" and self.regl_main_override:
            data = self.regl_main_override.read_bytes()
            self.send_response(200)
            self.send_header("Content-Type", "application/javascript")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        elif self.path == "/favicon.ico":
            self.send_response(204)
            self.end_headers()
        else:
            super().do_GET()


def capture(page):
    return Image.open(io.BytesIO(page.locator("canvas").first.screenshot())).convert("RGB")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--renderer", choices=["regl", "webgpu", "both"], default="both")
    parser.add_argument("--channel", default=None)
    parser.add_argument("--regl-main-override", type=Path, default=None, help="Serve an isolated module variant for mutation verification")
    args = parser.parse_args()
    Handler.regl_main_override = args.regl_main_override
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Handler, directory=str(WEB)))
    threading.Thread(target=server.serve_forever, daemon=True).start()
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
                page.add_init_script(INITIAL_PAUSE)
                try:
                    page.goto(f"http://127.0.0.1:{server.server_port}/index.html?renderer={backend}&suppressWarnings=true&resolution=1", wait_until="load")
                    page.wait_for_timeout(150)
                    assert page.evaluate("!window.matrixRuntimeDiagnostics"), "renderer imported before native resource readiness"
                    page.evaluate("window.__resolveNativeBootstrap()")
                    if backend == "webgpu" and not page.evaluate("async () => !!navigator.gpu && !!(await navigator.gpu.requestAdapter())"):
                        print("CANNOT VERIFY original Matrix WebGPU: no adapter", flush=True)
                        unavailable = True
                        continue
                    page.wait_for_function("window.matrixRuntimeDiagnostics?.initialized", timeout=30000)
                    assert page.evaluate("matrixRuntimeDiagnostics.backend") == backend, "requested backend silently fell back"
                    page.wait_for_timeout(750)
                    assert page.evaluate("matrixRuntimeDiagnostics.frames") == 0, "initially paused renderer executed GPU passes"
                    page.evaluate("window.dispatchEvent(new CustomEvent('matrix-host-state',{detail:{paused:false}}))")
                    page.wait_for_function("matrixRuntimeDiagnostics.frames > 8")
                    if backend == "regl":
                        ticks = page.evaluate("window.__matrixTickUniforms")
                        assert ticks and ticks[0] == 0 and 1 in ticks, f"GPU shader tick skipped initial state after pause: {ticks[:12]}"
                    assert max(ImageStat.Stat(capture(page)).stddev) > 3, "resumed original Matrix rendered a uniform frame"
                    page.evaluate("window.dispatchEvent(new CustomEvent('matrix-host-state',{detail:{paused:true}}))")
                    page.wait_for_timeout(50)
                    paused = capture(page)
                    frames = page.evaluate("matrixRuntimeDiagnostics.frames")
                    page.wait_for_timeout(250)
                    assert page.evaluate("matrixRuntimeDiagnostics.frames") == frames, "paused original Matrix continued drawing"
                    assert ImageChops.difference(paused, capture(page)).getbbox() is None, "paused original Matrix changed pixels"
                    page.evaluate("window.dispatchEvent(new CustomEvent('matrix-host-state',{detail:{paused:false}}))")
                    page.wait_for_function("previous => matrixRuntimeDiagnostics.frames > previous + 3", arg=frames)
                    assert not errors, errors
                    page.evaluate("matrixRuntimeDiagnostics.dispose()")
                    frames = page.evaluate("matrixRuntimeDiagnostics.frames")
                    page.wait_for_timeout(100)
                    assert page.evaluate("matrixRuntimeDiagnostics.disposed && !matrixRuntimeDiagnostics.ready")
                    assert page.evaluate("matrixRuntimeDiagnostics.frames") == frames
                    print(f"PASS original Matrix {backend}: initial pause, shader initialization, mid-run pause, resume and disposal", flush=True)
                    if backend == "regl":
                        failed = context.new_page()
                        failed.add_init_script("window.matrixHostBootstrapReady=new Promise((resolve,reject)=>{window.__rejectNativeBootstrap=reject;});")
                        failed.goto(f"http://127.0.0.1:{server.server_port}/index.html?scene=zmatrix&suppressWarnings=true", wait_until="load")
                        failed.evaluate("window.__rejectNativeBootstrap(new Error('Synthetic native bootstrap failure <b>literal</b>'))")
                        failed.wait_for_function("document.querySelector('.notice')?.textContent.includes('Synthetic native bootstrap failure')")
                        assert failed.evaluate("!window.matrixRuntimeDiagnostics"), "failed native readiness still started a renderer"
                        assert failed.locator(".notice b").count() == 0, "native failure text was interpreted as markup"
                        failed.close()
                        print("PASS native bootstrap failure remains visible as text and prevents renderer initialization", flush=True)
                finally:
                    context.close()
            browser.close()
    finally:
        server.shutdown()
        server.server_close()
    return 2 if unavailable else 0


if __name__ == "__main__":
    raise SystemExit(main())
