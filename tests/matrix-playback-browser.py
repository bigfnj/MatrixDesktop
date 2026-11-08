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
  // Record the renderer's loss reports and pass each one on to js/main.js's hook, so the
  // page's own recovery (js/rendererRecovery.js) runs as it does in the product.
  const calls = [];
  let pageHook = null;
  Object.defineProperty(window, 'matrixRendererLost', { configurable: true,
    get() { return reason => { calls.push(String(reason)); return pageHook?.(reason); }; },
    set(value) { pageHook = value; } });
  window.__matrixRendererLostCalls = calls;
  // This loop starts before any renderer, so it runs first in every frame: a loss it causes
  // meets the renderer's own frame callback in that frame, before webglcontextlost (a later
  // task) arrives. The capture listener reads ready as that event arrives.
  window.__frameFirst = [];
  const first = () => { requestAnimationFrame(first); for (const task of window.__frameFirst.splice(0)) task(); };
  requestAnimationFrame(first);
  window.__readyAtLostEvent = null;
  addEventListener('webglcontextlost', () => { window.__readyAtLostEvent = Boolean(window.matrixRuntimeDiagnostics?.ready); }, true);
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


def lose_regl_context(page):
    """Lose the running renderer's WebGL context; it must clear ready and report the loss once.

    The first frame that finds the context lost must already clear ready: the canvas is
    blank from the loss on, and webglcontextlost only arrives in a later task.
    """
    page.wait_for_function("window.matrixRuntimeDiagnostics?.ready && matrixRuntimeDiagnostics.frames > 8", timeout=30000)
    page.evaluate("window.__beforeLoss = true; window.__loseContext = document.querySelector('canvas').getContext('webgl').getExtension('WEBGL_lose_context'); window.__frameFirst.push(() => window.__loseContext.loseContext())")
    try:
        page.wait_for_function("window.__beforeLoss !== true || !matrixRuntimeDiagnostics.ready", timeout=1000)
    except Exception:
        raise AssertionError("WebGL context loss left matrixRuntimeDiagnostics.ready true") from None
    assert page.evaluate("window.__beforeLoss === true"), "the page reloaded during a pending WebGL loss instead of waiting for the restore"
    try:
        page.wait_for_function("window.__readyAtLostEvent !== null", timeout=1000)
    except Exception:
        raise AssertionError("webglcontextlost never reached the page, so the check proved nothing") from None
    assert page.evaluate("window.__readyAtLostEvent") is False, "ready was still true when webglcontextlost arrived, after a frame had found the context lost"
    lost = page.evaluate("({ errors: matrixRuntimeDiagnostics.errors, calls: window.__matrixRendererLostCalls, frames: matrixRuntimeDiagnostics.frames })")
    assert lost["errors"] == ["WebGL context lost"], f"context loss was not recorded: {lost['errors']}"
    assert lost["calls"] == ["WebGL context lost"], f"page hook calls after the loss: {lost['calls']}"
    return lost


def check_regl_context_loss(context, port):
    """A lost WebGL context must stop claiming ready, report to the page hook once, and draw again.

    regl re-creates every texture empty on restore, so a restored context draws black.
    js/rendererRecovery.js answers the restore event itself by reloading the page, within
    a budget of 3 reloads per 5 minutes, so the first leg expects a new document that
    draws. Past the budget the page keeps the old renderer under a notice instead; the
    second leg spends the budget, so the restored renderer stays in the page and must stay
    not ready (and stop counting frames) for as long as it is watched.
    """
    url = f"http://127.0.0.1:{port}/index.html?renderer=regl&suppressWarnings=true&resolution=1"
    page = context.new_page()
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.add_init_script(LOSS_HOOK)
    try:
        page.goto(url, wait_until="load")
        lose_regl_context(page)
        page.wait_for_timeout(1000)
        assert page.evaluate("window.__beforeLoss === true"), "the page reloaded during a pending WebGL loss instead of waiting for the restore"
        try:
            with page.expect_navigation(wait_until="commit", timeout=5000):
                page.evaluate("window.__loseContext.restoreContext()")
        except Exception:
            raise AssertionError("the page did not reload within 5 s of restoreContext()") from None
        page.wait_for_function("window.__beforeLoss === undefined && window.matrixRuntimeDiagnostics?.ready && matrixRuntimeDiagnostics.frames > 8", timeout=30000)
        for _ in range(12):  # the rain builds up after the reload
            image = capture(page).convert("L")
            bright = sum(image.histogram()[41:]) / (image.width * image.height)
            if bright > 0.01:
                break
            page.wait_for_timeout(500)
        assert bright > 0.01, f"the reloaded page draws black: {bright:.2%} of pixels above luma 40"
        reloaded = page.evaluate("({ errors: matrixRuntimeDiagnostics.errors, calls: window.__matrixRendererLostCalls })")
        assert reloaded == {"errors": [], "calls": []}, f"the reloaded page reports a loss: {reloaded}"
        page.evaluate("matrixRuntimeDiagnostics.dispose()")
        page.wait_for_timeout(100)
        assert not errors, errors
    finally:
        page.close()

    page = context.new_page()
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.add_init_script(LOSS_HOOK)
    try:
        page.goto(url, wait_until="load")
        # Spend the reload budget, so the page keeps this renderer through the restore.
        page.evaluate("sessionStorage.setItem('matrixRendererRecoveries', JSON.stringify([Date.now(), Date.now(), Date.now()]))")
        lost = lose_regl_context(page)
        page.evaluate("window.__loseContext.restoreContext()")
        page.wait_for_timeout(1500)
        restored = page.evaluate("({ beforeLoss: window.__beforeLoss === true, ready: matrixRuntimeDiagnostics.ready, "
                                 "frames: matrixRuntimeDiagnostics.frames, calls: window.__matrixRendererLostCalls, "
                                 "contextLost: document.querySelector('canvas').getContext('webgl').isContextLost(), "
                                 "notice: [...document.querySelectorAll('.notice')].some(n => n.textContent.includes('could not be recovered automatically')) })")
        assert restored["beforeLoss"], "the page reloaded although the reload budget was spent, so the check proved nothing"
        assert not restored["contextLost"], "the WebGL context did not restore, so the check proved nothing"
        assert not restored["ready"], "ready came back after restore although the restored context draws black"
        assert restored["frames"] == lost["frames"], f"frames advanced after the loss: {lost['frames']} -> {restored['frames']}"
        assert restored["calls"] == ["WebGL context lost"], f"page hook calls after the restore: {restored['calls']}"
        assert restored["notice"], "past the reload budget the page did not explain the loss"
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


def changed_fraction(a, b, threshold=40):
    histogram = ImageChops.difference(a.convert("L"), b.convert("L")).histogram()
    return sum(histogram[threshold + 1:]) / max(1, sum(histogram))


def check_regl_visible_pause(context, port):
    """A visible host pause must resume where it froze; a hidden one may still move shader time (R2-19).

    Manual, fullscreen-app and battery pauses keep the paused frame on screen (AUD-06), so
    moving the shader time origin on resume would snap the whole rain field to a new pattern.
    Lock, display off and suspend hide the WebView first, which the page sees as a visibility
    change; a synthetic visibilitychange stands in for that here. animationSpeed is low, so
    the rain barely moves between the frozen and resumed screenshots unless time jumps.
    """
    page = context.new_page()
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.add_init_script(TIME_HOOK)
    host = "paused => window.dispatchEvent(new CustomEvent('matrix-host-state', { detail: { paused } }))"
    try:
        page.goto(f"http://127.0.0.1:{port}/index.html?renderer=regl&suppressWarnings=true&resolution=1&numColumns=40&animationSpeed=0.05", wait_until="load")
        page.wait_for_function("window.matrixRuntimeDiagnostics?.ready && matrixRuntimeDiagnostics.frames > 2", timeout=30000)
        for _ in range(20):  # 1000 s of active time in 50 s steps, so shader time is past the 600 s restart point
            page.evaluate("window.__clockOffset += 50000")
            page.wait_for_timeout(60)
        page.wait_for_timeout(500)
        results = {}
        for name, pause_ms, hidden in (("control", 30_000, False), ("visible", 61_000, False), ("hidden", 61_000, True)):
            page.evaluate(host, True)
            page.wait_for_timeout(400)
            frames = page.evaluate("matrixRuntimeDiagnostics.frames")
            frozen = capture(page)
            page.evaluate(f"window.__clockOffset += {pause_ms}")
            if hidden:
                page.evaluate("document.dispatchEvent(new Event('visibilitychange'))")
            page.wait_for_timeout(200)
            assert page.evaluate("matrixRuntimeDiagnostics.frames") == frames, f"the {name} pause kept drawing, so the check proved nothing"
            before = page.evaluate("window.__shaderUniforms.time.at(-1)")
            page.evaluate(host, False)
            page.wait_for_function("previous => matrixRuntimeDiagnostics.frames > previous + 2", arg=frames, timeout=10000)
            page.wait_for_timeout(120)
            after = page.evaluate("window.__shaderUniforms.time.at(-1)")
            results[name] = {"changed": changed_fraction(frozen, capture(page)), "before": before, "after": after}
        control, visible, hidden = results["control"], results["visible"], results["hidden"]
        assert control["before"] > 900, f"shader time was {control['before']} s before the pauses, below the restart point, so the check proved nothing"
        assert visible["changed"] <= 3 * control["changed"] + 0.02, (
            f"a visible 61 s pause changed {visible['changed']:.2%} of pixels on resume against {control['changed']:.2%} after a 30 s pause")
        assert visible["after"] > visible["before"], f"a visible 61 s pause moved shader time from {visible['before']} s to {visible['after']} s"
        assert hidden["after"] < 650, f"a hidden 61 s pause kept shader time at {hidden['after']} s instead of restarting it"
        assert hidden["changed"] > 3 * control["changed"] + 0.02, (
            f"a hidden 61 s pause changed only {hidden['changed']:.2%} of pixels on resume, so the rebase was not visible to this check")
        page.evaluate("matrixRuntimeDiagnostics.dispose()")
        page.wait_for_timeout(100)
        assert not errors, errors
        return results
    finally:
        page.close()


def bright_fraction(image, threshold=150):
    histogram = image.convert("L").histogram()
    return sum(histogram[threshold + 1:]) / max(1, sum(histogram))


def check_regl_intro_rebase(context, port):
    """A shader time move with the intro on must not replay it over a screen full of rain (R2-20).

    With skipIntro off at animationSpeed 0.01 the slowest column's intro takes about 20 minutes
    of active time. A move back to ten minutes would put every column inside it again, where the
    rain pass boosts the cells below the intro front toward white. The 61 s gap comes with a
    visibility change, as a lock or display-off pause brings.
    """
    page = context.new_page()
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.add_init_script(TIME_HOOK)
    try:
        page.goto(f"http://127.0.0.1:{port}/index.html?renderer=regl&suppressWarnings=true&resolution=1&numColumns=40&skipIntro=false&animationSpeed=0.01", wait_until="load")
        page.wait_for_function("window.matrixRuntimeDiagnostics?.ready && matrixRuntimeDiagnostics.frames > 2", timeout=30000)
        for _ in range(70):  # 3500 s of active time in 50 s steps, past the end of the intro
            page.evaluate("window.__clockOffset += 50000")
            page.wait_for_timeout(60)
        page.wait_for_timeout(1500)
        before = page.evaluate("window.__shaderUniforms.time.at(-1)")
        bright_before = bright_fraction(capture(page))
        page.evaluate("document.dispatchEvent(new Event('visibilitychange')); window.__clockOffset += 61000")
        page.wait_for_timeout(1500)
        after = page.evaluate("window.__shaderUniforms.time.at(-1)")
        bright_after = bright_fraction(capture(page))
        assert before > 3000, f"shader time was {before} s before the gap, so the intro had not run its course and the check proved nothing"
        assert after < before - 1000, f"the hidden 61 s gap did not move shader time ({before} s to {after} s), so the check proved nothing"
        assert bright_after <= 2 * bright_before + 0.01, (
            f"a shader time move from {before:.0f} s to {after:.0f} s replayed the intro: {bright_before:.2%} of pixels above luma 150 before, {bright_after:.2%} after")
        page.evaluate("matrixRuntimeDiagnostics.dispose()")
        page.wait_for_timeout(100)
        assert not errors, errors
        return {"before": before, "after": after, "brightBefore": bright_before, "brightAfter": bright_after}
    finally:
        page.close()


def check_regl_single_frame(context, port):
    """fps=0 must draw one still frame, as UC3D and ZMatrix do, not a black canvas (AUD-47)."""
    page = context.new_page()
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    try:
        for fps in ("0", "30"):
            page.goto(f"http://127.0.0.1:{port}/index.html?renderer=regl&suppressWarnings=true&resolution=1&fps={fps}", wait_until="load")
            page.wait_for_function("window.matrixRuntimeDiagnostics?.frames > 0", timeout=30000)
            page.wait_for_timeout(2000)
            frames = page.evaluate("matrixRuntimeDiagnostics.frames")
            image = capture(page).convert("L")
            bright = sum(image.histogram()[41:]) / (image.width * image.height)
            assert bright > 0.01, f"fps={fps} canvas is black: {bright:.2%} of pixels above luma 40"
            page.wait_for_timeout(300)
            later = page.evaluate("matrixRuntimeDiagnostics.frames")
            if fps == "0":
                assert frames == 1 and later == 1, f"fps=0 must draw exactly one frame: {frames} then {later}"
            else:
                assert later > frames, f"fps=30 control stopped animating at {frames} frames"
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
                        print("PASS original Matrix regl context loss clears ready and reports once; the restore reloads into a drawing page, and past the reload budget the restored renderer stays not ready under a notice", flush=True)
                        check_regl_time_rebase(context, server.server_port)
                        print("PASS original Matrix regl time uniform stays small after four days of page time and tick keeps counting", flush=True)
                        pauses = check_regl_visible_pause(context, server.server_port)
                        print("PASS original Matrix regl visible 61 s pause resumes where it froze ({:.2%} of pixels changed, 30 s control {:.2%}); a hidden one still restarts shader time ({:.2%})".format(
                            pauses["visible"]["changed"], pauses["control"]["changed"], pauses["hidden"]["changed"]), flush=True)
                        intro = check_regl_intro_rebase(context, server.server_port)
                        print("PASS original Matrix regl shader time move with skipIntro=false restarts after the intro ({:.0f} s to {:.0f} s, {:.2%} to {:.2%} bright pixels)".format(
                            intro["before"], intro["after"], intro["brightBefore"], intro["brightAfter"]), flush=True)
                        check_regl_single_frame(context, server.server_port)
                        print("PASS original Matrix regl fps=0 draws one still frame and an fps=30 control keeps animating", flush=True)
                finally:
                    context.close()
            browser.close()
    finally:
        server.shutdown()
        server.server_close()
    return 2 if unavailable else 0


if __name__ == "__main__":
    raise SystemExit(main())
