"""Verify the original Matrix renderer survives an initial and mid-run pause, and that every scene
shows a frame under a pause in force at load."""
import argparse
import functools
import http.server
import io
import json
import threading
from pathlib import Path

from PIL import Image, ImageChops, ImageStat
from playwright.sync_api import sync_playwright

import browser_launch

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


PLUG_START = "?renderer=regl&numColumns=40"
PLUG_CHANGED = "?renderer=regl&numColumns=41"
PLUG_HOST = """(() => {
  // The native bootstrap of an interactive host (windowed, fullscreen, configurator preview)
  // belongs to one configuration, and the page may use it only while location.search equals
  // it (Shared/NativeMediaBridge.cs, PublishBootstrapAsync). This host knows two of them.
  const configurations = %s;
  const configuration = configurations.includes(location.search) ? location.search : configurations[0];
  const bootstrap = { nativeGeneration: 1, configuration, interactive: true, images: [], warnings: [] };
  const matches = b => b?.configuration === location.search;
  if (matches(bootstrap)) window.matrixHostBootstrap = bootstrap;
  window.matrixHostBootstrapReady = Promise.resolve(bootstrap).then(b => {
    if (!matches(b)) throw new Error('Native resources belong to another configuration');
    return b;
  });
  window.matrixHostBootstrapReady.catch(() => {});
  // js/main.js shows the software rendering notice when the WebGL renderer string names
  // SwiftShader; report that on any adapter, so the check runs the same on a real GPU.
  for (const Context of [window.WebGLRenderingContext, window.WebGL2RenderingContext]) {
    if (!Context) continue;
    const get = Context.prototype.getParameter;
    Context.prototype.getParameter = function(name) {
      return name === 0x9246 ? 'Google SwiftShader (reported by the test)' : get.call(this, name);
    };
  }
})();""" % json.dumps([PLUG_START, PLUG_CHANGED])


FRAME_REQUESTS = """(() => {
  // Count the page's animation frame requests, whoever makes them.
  const request = window.requestAnimationFrame.bind(window);
  window.__frameRequests = 0;
  window.requestAnimationFrame = callback => { window.__frameRequests++; return request(callback); };
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


HELD_BOOTSTRAP = """(() => {
  // Hold the renderer before it starts, so the test can pause the loaded page first.
  window.matrixHostBootstrapReady = new Promise(resolve => { window.__resolveNativeBootstrap = resolve; });
})();"""


def check_initial_pause_frame(page, label):
    """A pause already in force at the first frame must leave that frame on screen (R3-01).

    A manual, fullscreen-app or battery pause leaves the page visible (AUD-06), and it outlives
    a surface rebuild and a reload, so a renderer that parked before its first frame showed
    black for the whole pause. It draws one frame and parks: one frame, and no other.
    """
    try:
        page.wait_for_function("window.matrixRuntimeDiagnostics?.frames >= 1", timeout=10000)
    except Exception:
        image = capture(page)
        frames = page.evaluate("window.matrixRuntimeDiagnostics?.frames")
        raise AssertionError(f"{label}: a renderer paused before its first frame drew {frames} frames in 10 s, "
                             f"so the paused screen is blank (canvas stddev {max(ImageStat.Stat(image).stddev):.2f})") from None
    page.wait_for_timeout(750)
    held = capture(page)
    frames = page.evaluate("matrixRuntimeDiagnostics.frames")
    stddev = max(ImageStat.Stat(held).stddev)
    assert frames == 1, f"{label}: a renderer paused before its first frame drew {frames} frames, not one"
    assert stddev > 3, f"{label}: the frame drawn under a pause in force at load is a uniform fill (stddev {stddev:.2f})"
    page.wait_for_timeout(250)
    later = page.evaluate("matrixRuntimeDiagnostics.frames")
    assert later == 1, f"{label}: a paused renderer kept drawing after its first frame ({later} frames)"
    assert ImageChops.difference(held, capture(page)).getbbox() is None, f"{label}: the paused first frame changed"
    return stddev


def check_scene_initial_pause(context, port, backend):
    """Every scene keeps a frame on screen under a pause that arrives before its first frame (R3-01).

    In the host's order: the host pauses at NavigationCompleted (MainForm.SetHostPausedAsync),
    and the renderer starts after the native bootstrap and its own assets. The Matrix case runs
    at 30 fps, where a frame may skip rendering; the UC3D and ZMatrix frames are warmed as once
    warms them, so the frozen frame is not empty.
    """
    # The host's own script: renderer modules not yet imported read the state when they load.
    host = "paused => { window.matrixHostPlaybackState = { paused }; window.dispatchEvent(new CustomEvent('matrix-host-state', { detail: window.matrixHostPlaybackState })); }"
    results = {}
    for label, query in (
        ("original Matrix at 30 fps", f"renderer={backend}&suppressWarnings=true&resolution=1&fps=30"),
        ("UC3D", f"scene=uc3d&renderer={backend}&suppressWarnings=true&resolution=1"),
        ("ZMatrix", f"scene=zmatrix&renderer={backend}&suppressWarnings=true&resolution=1&fps=30"
                    "&zmMaxStream=80&zmRefreshTime=20&zmBackgroundMode=color&zmBackgroundColor=9,18,27"),
    ):
        page = context.new_page()
        errors = []
        page.on("pageerror", lambda error: errors.append(str(error)))
        page.add_init_script(HELD_BOOTSTRAP)
        try:
            page.goto(f"http://127.0.0.1:{port}/index.html?{query}", wait_until="load")
            page.evaluate(host, True)
            page.evaluate("window.__resolveNativeBootstrap()")
            results[label] = check_initial_pause_frame(page, f"{label} {backend}")
            assert page.evaluate("matrixRuntimeDiagnostics.backend") == backend, f"{label}: requested backend silently fell back"
            page.evaluate(host, False)
            try:
                page.wait_for_function("matrixRuntimeDiagnostics.frames > 3", timeout=10000)
            except Exception:
                raise AssertionError(f"{label} {backend}: the renderer did not draw again within 10 s of a resume") from None
            page.evaluate("matrixRuntimeDiagnostics.dispose()")
            page.wait_for_timeout(100)
            assert not errors, errors
        finally:
            page.close()
    return results


SKIP_THEN_PAUSE = """(() => {
  // Pause in the task of the renderer's first frame, right after it, so the second frame is
  // the first paused one. Every frame callback is wrapped; the first to end with one frame
  // counted sends the host's pause.
  const request = window.requestAnimationFrame.bind(window);
  window.__pausedAtFrames = null;
  window.requestAnimationFrame = callback => request(time => {
    try { callback(time); }
    finally {
      if (window.__pausedAtFrames === null && window.matrixRuntimeDiagnostics?.frames === 1) {
        window.__pausedAtFrames = window.matrixRuntimeDiagnostics.frames;
        window.matrixHostPlaybackState = { paused: true };
        window.dispatchEvent(new CustomEvent('matrix-host-state', { detail: window.matrixHostPlaybackState }));
      }
    }
  });
})();"""


def check_regl_skip_then_pause(context, port):
    """A pause after a first frame that skipped rendering must still show a frame (R3-01).

    Below 60 fps the first frame only starts the frame clock and shows the pipeline's empty
    output, so nothing is on screen yet. A host pause that lands before the next frame must
    not find the loop marked as presented: that frame renders once and parks, or the wallpaper
    stays black under the pause. The WebGPU node case 'a pause after a frame that skipped
    rendering must still render one' is the same contract.
    """
    page = context.new_page()
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.add_init_script(SKIP_THEN_PAUSE)
    host = "paused => { window.matrixHostPlaybackState = { paused }; window.dispatchEvent(new CustomEvent('matrix-host-state', { detail: window.matrixHostPlaybackState })); }"
    try:
        page.goto(f"http://127.0.0.1:{port}/index.html?renderer=regl&suppressWarnings=true&resolution=1&fps=30", wait_until="load")
        try:
            page.wait_for_function("window.__pausedAtFrames !== null", timeout=30000)
        except Exception:
            raise AssertionError("the renderer never drew its first frame, so the skip-then-pause check proved nothing") from None
        page.wait_for_timeout(1000)
        held = capture(page)
        state = page.evaluate("({ pausedAt: window.__pausedAtFrames, frames: matrixRuntimeDiagnostics.frames, backend: matrixRuntimeDiagnostics.backend })")
        stddev = max(ImageStat.Stat(held).stddev)
        assert state["backend"] == "regl", "requested backend silently fell back"
        assert state["pausedAt"] == 1, f"the pause landed after frame {state['pausedAt']}, not the first, so the check proved nothing"
        assert state["frames"] == 2, (f"original Matrix at 30 fps regl: a pause after a first frame that skipped rendering left "
                                      f"{state['frames']} frames, not 2, and the paused screen shows canvas stddev {stddev:.2f}")
        assert stddev > 3, f"original Matrix at 30 fps regl: the frame drawn under a pause after a skipped first frame is a uniform fill (stddev {stddev:.2f})"
        page.wait_for_timeout(250)
        later = page.evaluate("matrixRuntimeDiagnostics.frames")
        assert later == 2, f"a paused renderer kept drawing after its frozen frame ({later} frames)"
        assert ImageChops.difference(held, capture(page)).getbbox() is None, "the frozen frame after a skipped first frame changed"
        page.evaluate(host, False)
        try:
            page.wait_for_function("matrixRuntimeDiagnostics.frames > 4", timeout=10000)
        except Exception:
            raise AssertionError("the renderer did not draw again within 10 s of a resume after a skip-then-pause") from None
        page.evaluate("matrixRuntimeDiagnostics.dispose()")
        page.wait_for_timeout(100)
        assert not errors, errors
        return stddev
    finally:
        page.close()


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
    R3-02: a lock early in a visible pause hides the page, and the unlock shows the frozen
    frame again while still paused, so the resume a minute later must not move time either.
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
        for name, pause_ms, lock in (("control", 30_000, None), ("visible", 61_000, None), ("spanned", 61_000, "start"), ("hidden", 61_000, "end")):
            page.evaluate(host, True)
            page.wait_for_timeout(400)
            frames = page.evaluate("matrixRuntimeDiagnostics.frames")
            frozen = capture(page)
            if lock == "start":  # hidden, then shown again 10 s later, still paused
                page.evaluate("document.dispatchEvent(new Event('visibilitychange')); window.__clockOffset += 10000; document.dispatchEvent(new Event('visibilitychange'))")
            page.evaluate(f"window.__clockOffset += {pause_ms}")
            page.wait_for_timeout(200)
            assert page.evaluate("matrixRuntimeDiagnostics.frames") == frames, f"the {name} pause kept drawing, so the check proved nothing"
            before = page.evaluate("window.__shaderUniforms.time.at(-1)")
            if lock == "end":  # the page comes back into view just before the resumed frame, as at unlock
                page.evaluate("document.dispatchEvent(new Event('visibilitychange'))")
            page.evaluate(host, False)
            page.wait_for_function("previous => matrixRuntimeDiagnostics.frames > previous + 2", arg=frames, timeout=10000)
            page.wait_for_timeout(120)
            after = page.evaluate("window.__shaderUniforms.time.at(-1)")
            results[name] = {"changed": changed_fraction(frozen, capture(page)), "before": before, "after": after}
        control, visible, spanned, hidden = results["control"], results["visible"], results["spanned"], results["hidden"]
        assert control["before"] > 900, f"shader time was {control['before']} s before the pauses, below the restart point, so the check proved nothing"
        # Natural motion between the two screenshots grows with load (a 30 s control measured
        # 0.5% to 4.3%); a move of shader time changes about 27% of pixels.
        assert visible["changed"] <= 3 * control["changed"] + 0.05, (
            f"a visible 61 s pause changed {visible['changed']:.2%} of pixels on resume against {control['changed']:.2%} after a 30 s pause")
        assert visible["after"] > visible["before"], f"a visible 61 s pause moved shader time from {visible['before']} s to {visible['after']} s"
        assert spanned["changed"] <= 3 * control["changed"] + 0.05, (
            f"a visible pause that spanned a lock changed {spanned['changed']:.2%} of pixels on resume against {control['changed']:.2%} after a 30 s pause")
        assert spanned["after"] > spanned["before"], f"a visible pause that spanned a lock moved shader time from {spanned['before']} s to {spanned['after']} s"
        assert hidden["after"] < 650, f"a hidden 61 s pause kept shader time at {hidden['after']} s instead of restarting it"
        assert hidden["changed"] >= 0.1, (
            f"a hidden 61 s pause changed only {hidden['changed']:.2%} of pixels on resume, so this check cannot see a move of shader time")
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
        # Hidden, and shown again at the end of the gap, as an unlock brings it back (R3-02).
        page.evaluate("document.dispatchEvent(new Event('visibilitychange')); window.__clockOffset += 61000; document.dispatchEvent(new Event('visibilitychange'))")
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
    """fps=0 must draw one still frame, as UC3D and ZMatrix do, not a black canvas (AUD-47).

    R2-22: the still frame (fps=0, and once) must also survive a real window resize, which
    used to assign the canvas size and clear it while ready stayed true.
    """
    page = context.new_page()
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    viewport = page.viewport_size
    try:
        for query, still in (("fps=0", True), ("once=true", True), ("fps=30", False)):
            page.set_viewport_size(viewport)
            page.goto(f"http://127.0.0.1:{port}/index.html?renderer=regl&suppressWarnings=true&resolution=1&{query}", wait_until="load")
            page.wait_for_function("window.matrixRuntimeDiagnostics?.frames > 0", timeout=30000)
            page.wait_for_timeout(2000)
            frames = page.evaluate("matrixRuntimeDiagnostics.frames")
            image = capture(page).convert("L")
            bright = sum(image.histogram()[41:]) / (image.width * image.height)
            assert bright > 0.01, f"{query} canvas is black: {bright:.2%} of pixels above luma 40"
            page.wait_for_timeout(300)
            later = page.evaluate("matrixRuntimeDiagnostics.frames")
            if still:
                assert frames == 1 and later == 1, f"{query} must draw exactly one frame: {frames} then {later}"
            else:
                assert later > frames, f"{query} control stopped animating at {frames} frames"
            width = page.evaluate("document.querySelector('canvas').clientWidth")
            page.set_viewport_size({"width": viewport["width"] - 80, "height": viewport["height"] - 40})
            page.wait_for_timeout(1000)
            resized = page.evaluate("({ width: document.querySelector('canvas').clientWidth, frames: matrixRuntimeDiagnostics.frames, ready: matrixRuntimeDiagnostics.ready })")
            assert resized["width"] != width, f"the canvas kept its {width} px width, so the resize check proved nothing"
            image = capture(page).convert("L")
            bright = sum(image.histogram()[41:]) / (image.width * image.height)
            assert bright > 0.01, f"{query} canvas is black after a resize: {bright:.2%} of pixels above luma 40 (ready {resized['ready']})"
            if still:
                assert resized["frames"] == 1, f"{query} drew again after a resize: {resized['frames']} frames"
            else:
                assert resized["frames"] > later, f"{query} control stopped animating after a resize at {later} frames"
        page.evaluate("matrixRuntimeDiagnostics.dispose()")
        page.wait_for_timeout(100)
        assert not errors, errors
    finally:
        page.close()


def check_regl_plug_me_in_reload(context, port):
    """Plug me in must survive a recovery reload and the host's renderer reload (R2-23).

    Both return to the startup query, which the native bootstrap requires (js/main.js undoes
    Plug me in's query rewrite before a recovery reload), so the consent cannot ride in the
    query. Before the fix the reloaded page showed the notice again over a hidden canvas and
    started no renderer. The consent covers one configuration: a changed one still warns.
    """
    page = context.new_page()
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.add_init_script(PLUG_HOST)
    url = f"http://127.0.0.1:{port}/index.html"
    state = ("({ search: location.search, pill: document.querySelector('.blue.pill') !== null, "
             "canvas: getComputedStyle(document.querySelector('canvas')).display, "
             "notices: [...document.querySelectorAll('.notice')].map(n => n.textContent.trim().replace(/\\s+/g, ' ').slice(0, 120)), "
             "renderer: window.matrixRuntimeDiagnostics ? { ready: matrixRuntimeDiagnostics.ready, frames: matrixRuntimeDiagnostics.frames } : null })")
    settled = "document.querySelector('.notice') !== null || (window.matrixRuntimeDiagnostics?.ready && matrixRuntimeDiagnostics.frames > 2)"

    def warns(where):
        page.wait_for_function(settled, timeout=60000)
        page.wait_for_timeout(300)
        shown = page.evaluate(state)
        assert shown["pill"], f"{where} showed no software rendering notice: {shown}"
        assert shown["canvas"] == "none" and shown["renderer"] is None, f"{where} started a renderer under the notice: {shown}"

    def renders(where):
        page.wait_for_function(settled, timeout=60000)
        shown = page.evaluate(state)
        assert not shown["notices"] and shown["renderer"], f"{where} did not start a renderer: {shown}"
        assert shown["search"] == PLUG_START, f"{where} left the page at {shown['search']!r}, not the startup query"
        page.wait_for_timeout(1000)
        assert page.evaluate("matrixRuntimeDiagnostics.frames") > shown["renderer"]["frames"], f"{where} stopped drawing: {page.evaluate(state)}"

    try:
        page.goto(url + PLUG_START, wait_until="load")
        warns("the first visit")
        page.click(".blue.pill")
        page.wait_for_function("window.matrixRuntimeDiagnostics?.ready && matrixRuntimeDiagnostics.frames > 2", timeout=60000)
        clicked = page.evaluate("location.search")
        assert clicked != PLUG_START, "Plug me in left the query alone, so the reload had nothing to undo and the check proved nothing"
        page.evaluate("window.__beforeLoss = true; window.__lose = document.querySelector('canvas').getContext('webgl').getExtension('WEBGL_lose_context'); window.__lose.loseContext()")
        page.wait_for_timeout(300)
        assert page.evaluate("window.__beforeLoss === true"), "the page reloaded before the context was restored"
        try:
            with page.expect_navigation(wait_until="commit", timeout=10000):
                page.evaluate("window.__lose.restoreContext()")
        except Exception:
            raise AssertionError("the page did not reload within 10 s of restoreContext()") from None
        renders(f"the recovery reload after Plug me in (query {clicked!r})")
        page.goto(url + PLUG_START, wait_until="load")  # the host's renderer reload navigates to the startup target
        renders("a navigation to the startup query after Plug me in")
        page.goto(url + PLUG_CHANGED, wait_until="load")
        warns("a changed configuration in the same session")
        assert not errors, errors
    finally:
        page.close()


def check_regl_paused_loop(context, port):
    """A visible host pause must park the frame loop, not wake it at the display rate (R2-24).

    A manual, fullscreen-app or battery pause leaves the page visible and running (AUD-06), so
    a loop that only skipped drawing while paused still requested a frame for every display
    refresh. A resume must start the loop again.
    """
    page = context.new_page()
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.add_init_script(FRAME_REQUESTS)
    host = "paused => window.dispatchEvent(new CustomEvent('matrix-host-state', { detail: { paused } }))"
    counts = "({ requests: window.__frameRequests, frames: matrixRuntimeDiagnostics.frames })"
    try:
        page.goto(f"http://127.0.0.1:{port}/index.html?renderer=regl&suppressWarnings=true&resolution=1&numColumns=40", wait_until="load")
        page.wait_for_function("window.matrixRuntimeDiagnostics?.ready && matrixRuntimeDiagnostics.frames > 8", timeout=30000)
        start = page.evaluate(counts)
        page.wait_for_timeout(1000)
        playing = page.evaluate(counts)["requests"] - start["requests"]
        assert playing >= 3, f"the counter saw {playing} frame requests in 1 s of play, so it cannot see a running loop"
        page.evaluate(host, True)
        page.wait_for_timeout(300)
        before = page.evaluate(counts)
        frozen = capture(page)
        page.wait_for_timeout(2000)
        after = page.evaluate(counts)
        assert after["requests"] == before["requests"], f"a paused renderer requested {after['requests'] - before['requests']} animation frames in 2 s"
        assert after["frames"] == before["frames"], f"a paused renderer drew {after['frames'] - before['frames']} frames"
        assert ImageChops.difference(frozen, capture(page)).getbbox() is None, "the paused frame changed"
        page.evaluate(host, False)
        try:
            page.wait_for_function("previous => matrixRuntimeDiagnostics.frames > previous + 2", arg=after["frames"], timeout=5000)
        except Exception:
            raise AssertionError(f"the renderer did not draw again within 5 s of a resume: {page.evaluate(counts)}") from None
        page.evaluate("matrixRuntimeDiagnostics.dispose()")
        page.wait_for_timeout(100)
        assert not errors, errors
        return playing
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
            browser = browser_launch.launch(playwright.chromium, channel=args.channel, args=flags)
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
                    check_initial_pause_frame(page, f"original Matrix {backend}")
                    page.evaluate("window.dispatchEvent(new CustomEvent('matrix-host-state',{detail:{paused:false}}))")
                    page.wait_for_function("matrixRuntimeDiagnostics.frames > 8")
                    if backend == "regl":
                        ticks = page.evaluate("window.__matrixTickUniforms")
                        assert ticks and ticks[0] == 0 and 1 in ticks, f"GPU shader tick skipped initial state after pause: {ticks[:12]}"
                    assert max(ImageStat.Stat(capture(page)).stddev) > 3, "resumed original Matrix rendered a uniform frame"
                    # The count is read in the pause's own task, so the frame already requested
                    # when the pause arrives must park too, and not draw once more.
                    frames = page.evaluate("() => { window.dispatchEvent(new CustomEvent('matrix-host-state',{detail:{paused:true}})); return matrixRuntimeDiagnostics.frames; }")
                    page.wait_for_timeout(50)
                    paused = capture(page)
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
                    print(f"PASS original Matrix {backend}: initial pause shows one frame, shader initialization, mid-run pause, resume and disposal", flush=True)
                    scenes = check_scene_initial_pause(context, server.server_port, backend)
                    print(f"PASS {backend} initial host pause shows one frozen frame in every scene, then resumes (canvas stddev "
                          + ", ".join(f"{label} {stddev:.1f}" for label, stddev in scenes.items()) + ")", flush=True)
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
                        print("PASS original Matrix regl visible 61 s pause resumes where it froze ({:.2%} of pixels changed, 30 s control {:.2%}), so does one that spanned a lock ({:.2%}); a hidden one still restarts shader time ({:.2%})".format(
                            pauses["visible"]["changed"], pauses["control"]["changed"], pauses["spanned"]["changed"], pauses["hidden"]["changed"]), flush=True)
                        intro = check_regl_intro_rebase(context, server.server_port)
                        print("PASS original Matrix regl shader time move with skipIntro=false restarts after the intro ({:.0f} s to {:.0f} s, {:.2%} to {:.2%} bright pixels)".format(
                            intro["before"], intro["after"], intro["brightBefore"], intro["brightAfter"]), flush=True)
                        check_regl_single_frame(context, server.server_port)
                        print("PASS original Matrix regl fps=0 and once draw one still frame that survives a window resize, and an fps=30 control keeps animating", flush=True)
                        check_regl_plug_me_in_reload(context, server.server_port)
                        print("PASS Plug me in survives a recovery reload and a navigation to the startup query; a changed configuration still shows the software rendering notice", flush=True)
                        playing = check_regl_paused_loop(context, server.server_port)
                        print(f"PASS original Matrix regl parks its frame loop through a visible host pause (0 frame requests in 2 s against {playing} in 1 s of play) and resumes", flush=True)
                        skipped = check_regl_skip_then_pause(context, server.server_port)
                        print(f"PASS original Matrix regl at 30 fps: a pause after a first frame that skipped rendering still draws one frozen frame (canvas stddev {skipped:.1f}), then resumes", flush=True)
                finally:
                    context.close()
            browser.close()
    finally:
        server.shutdown()
        server.server_close()
    return 2 if unavailable else 0


if __name__ == "__main__":
    raise SystemExit(main())
