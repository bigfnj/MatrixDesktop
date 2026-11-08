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


LOSS_HOOK = """(() => {
  // Record the renderer's loss reports and swallow js/main.js's own hook, so its recovery
  // reload cannot replace the page under this check.
  const calls = [];
  Object.defineProperty(window, 'matrixRendererLost', { configurable: true,
    get() { return reason => calls.push(String(reason)); }, set() {} });
  window.__matrixRendererLostCalls = calls;
})();"""


TIME_HOOK = """(() => {
  // Move the page clock on demand and record what the shaders receive as time and tick.
  const now = performance.now.bind(performance);
  window.__clockOffset = 0;
  performance.now = () => now() + window.__clockOffset;
  window.__shaderUniforms = { time: [], tick: [] };
  for (const Context of [window.WebGLRenderingContext, window.WebGL2RenderingContext]) {
    if (!Context) continue;
    const names = new WeakMap(), get = Context.prototype.getUniformLocation, set = Context.prototype.uniform1f;
    Context.prototype.getUniformLocation = function(program, name) {
      const location = get.call(this, program, name);
      if ((name === 'time' || name === 'tick') && location) names.set(location, name);
      return location;
    };
    Context.prototype.uniform1f = function(location, value) {
      const name = location && names.get(location);
      if (name) { const list = window.__shaderUniforms[name]; list.push(value); if (list.length > 256) list.shift(); }
      return set.call(this, location, value);
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


def check_regl_context_loss(context, port):
    """A lost WebGL context must stop claiming ready and report to the page hook once.

    regl re-creates every texture empty on restore, so a restored context draws black;
    the renderer must stay not ready (and stop counting frames) after the restore too.
    """
    page = context.new_page()
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.add_init_script(LOSS_HOOK)
    try:
        page.goto(f"http://127.0.0.1:{port}/index.html?renderer=regl&suppressWarnings=true&resolution=1", wait_until="load")
        page.wait_for_function("window.matrixRuntimeDiagnostics?.ready && matrixRuntimeDiagnostics.frames > 8", timeout=30000)
        page.evaluate("window.__loseContext = document.querySelector('canvas').getContext('webgl').getExtension('WEBGL_lose_context'); window.__loseContext.loseContext()")
        try:
            page.wait_for_function("!matrixRuntimeDiagnostics.ready", timeout=1000)
        except Exception:
            raise AssertionError("WebGL context loss left matrixRuntimeDiagnostics.ready true") from None
        lost = page.evaluate("({ errors: matrixRuntimeDiagnostics.errors, calls: window.__matrixRendererLostCalls, frames: matrixRuntimeDiagnostics.frames })")
        assert lost["errors"] == ["WebGL context lost"], f"context loss was not recorded: {lost['errors']}"
        assert lost["calls"] == ["WebGL context lost"], f"page hook calls after the loss: {lost['calls']}"
        page.evaluate("window.__loseContext.restoreContext()")
        page.wait_for_timeout(1500)
        restored = page.evaluate("({ ready: matrixRuntimeDiagnostics.ready, frames: matrixRuntimeDiagnostics.frames, calls: window.__matrixRendererLostCalls, contextLost: document.querySelector('canvas').getContext('webgl').isContextLost() })")
        assert not restored["contextLost"], "the WebGL context did not restore, so the check proved nothing"
        assert not restored["ready"], "ready came back after restore although the restored context draws black"
        assert restored["frames"] == lost["frames"], f"frames advanced after the loss: {lost['frames']} -> {restored['frames']}"
        assert restored["calls"] == ["WebGL context lost"], f"page hook calls after the restore: {restored['calls']}"
        page.evaluate("matrixRuntimeDiagnostics.dispose()")
        page.wait_for_timeout(100)
        assert not errors, errors
    finally:
        page.close()


def check_regl_time_rebase(context, port):
    """Four days of page time must not reach the float32 time uniform (AUD-64).

    The jump arrives as one long gap between frames, which is how a hidden or suspended
    page looks; tick must keep counting, or every glyph would re-seed as a first frame.
    """
    page = context.new_page()
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.add_init_script(TIME_HOOK)
    try:
        page.goto(f"http://127.0.0.1:{port}/index.html?renderer=regl&suppressWarnings=true&resolution=1", wait_until="load")
        page.wait_for_function("window.matrixRuntimeDiagnostics?.ready && matrixRuntimeDiagnostics.frames > 8", timeout=30000)
        before = page.evaluate("window.__shaderUniforms.time.slice()")
        assert before and max(before) < 60, f"time uniform before the jump: {before[-4:]}"
        frames = page.evaluate("matrixRuntimeDiagnostics.frames")
        page.evaluate("window.__clockOffset = 4 * 86400 * 1000; window.__shaderUniforms.time.length = 0; window.__shaderUniforms.tick.length = 0")
        page.wait_for_function("previous => matrixRuntimeDiagnostics.frames > previous + 8", arg=frames)
        after = page.evaluate("({ ...window.__shaderUniforms, clock: matrixRuntimeDiagnostics.simulationTime })")
        assert after["clock"] > 4 * 86400 - 60, f"the page clock did not jump, so the check proved nothing: {after['clock']}"
        assert after["time"] and max(after["time"]) < 700, f"time uniform after four days of page time: {after['time'][:4]}"
        assert after["tick"] and min(after["tick"]) > 1, f"tick fell back to a first-frame value: {after['tick'][:8]}"
        page.evaluate("matrixRuntimeDiagnostics.dispose()")
        page.wait_for_timeout(100)
        assert not errors, errors
    finally:
        page.close()


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
                    page.evaluate("matrixRuntimeDiagnostics.dispose()")
                    frames = page.evaluate("matrixRuntimeDiagnostics.frames")
                    page.wait_for_timeout(100)
                    assert page.evaluate("matrixRuntimeDiagnostics.disposed && !matrixRuntimeDiagnostics.ready")
                    assert page.evaluate("matrixRuntimeDiagnostics.frames") == frames
                    # AUD-55: checked after teardown, so errors raised by disposal are seen too.
                    assert not errors, errors
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
                        check_regl_context_loss(context, server.server_port)
                        print("PASS original Matrix regl context loss clears ready, reports once to the page and stays not ready after restore", flush=True)
                        check_regl_time_rebase(context, server.server_port)
                        print("PASS original Matrix regl time uniform stays small after four days of page time and tick keeps counting", flush=True)
                finally:
                    context.close()
            browser.close()
    finally:
        server.shutdown()
        server.server_close()
    return 2 if unavailable else 0


if __name__ == "__main__":
    raise SystemExit(main())
