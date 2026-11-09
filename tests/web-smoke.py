"""Headless DOM smoke for the two web bundles this repo ships.

Why this exists
---------------
tests\\run-gate.ps1 captures the real windows and measures pixel statistics. That catches a
black frame or a frozen animation, and it structurally CANNOT catch a layout defect: a
configurator whose footer has been pushed off the bottom of the window still has a healthy
mean and standard deviation. Exactly that shipped in v1.0.2. `.app-shell` used `min-height`
instead of `height`, so the `1fr` grid track sized to max-content, the workspace grew to the
full unscrolled height of every field, the generated-command panel with Copy and Test
Argument left the viewport entirely, and the preview iframe went to an aspect ratio near
0.09. The gate was green for the whole run.

So the assertions here are geometric and structural, not photographic. They also run without
a GPU, a window station or WebView2, which makes the configurator UI testable in CI for the
first time.

The page normally boots by asking its WebView2 host for state. There is no host here, so
window.chrome.webview is stubbed and the loadState reply comes from
`MatrixDesktop.Tests.exe --dump-state`, which builds it from the real ArgumentCatalog. A
hand-written fixture would drift from the catalogue and quietly turn these assertions into
claims about a page the product never renders.

Exit codes match the gate's own convention:
    0 pass    1 fail    2 cannot verify    3 harness error
"""

import functools
import hashlib
import http.server
import io
import json
import socket
import sys
import threading
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
CONFIGURATOR = REPO / "MatrixDesktopConfigurator" / "configurator"
WEB = REPO / "MatrixDesktop" / "web"

VIEWPORT = {"width": 1280, "height": 860}

failures = []
passes = []


def ok(name, detail=""):
    passes.append(name)
    print(f"  ok    {name}" + (f"  ({detail})" if detail else ""))


def fail(name, detail):
    failures.append((name, detail))
    print(f"  FAIL  {name}: {detail}")


class _QuietHandler(http.server.SimpleHTTPRequestHandler):
    """Static files without a stderr line per request.

    The override has to live on the class. It used to be assigned to the functools.partial,
    which the server never consults: it instantiates the class, so log_message still resolved
    to BaseHTTPRequestHandler's and every request went to stderr. The gate prints all of
    stderr as the detail of a harness error, so that noise buried the line naming the error.
    """

    def log_message(self, format, *args):
        pass


# Errors a handler raised other than a client hanging up; main() reports them as a FAIL.
server_errors = []


class _QuietServer(http.server.ThreadingHTTPServer):
    """A static server whose handler threads write nothing to stderr (R2-49).

    socketserver's handle_error prints a traceback to stderr from the handler thread. A page
    closed while it still loads aborts its transfers, so each run wrote 5-6 of them, and from
    daemon threads such a write can land while the interpreter finalizes and kill the process
    ("could not acquire lock for <stderr>"). A client hanging up is expected and only counted;
    anything else is recorded. Handler threads are not daemons, so server_close() waits for
    them.
    """

    daemon_threads = False
    hangups = 0

    def handle_error(self, request, client_address):
        error = sys.exc_info()[1]
        if isinstance(error, (ConnectionAbortedError, ConnectionResetError, BrokenPipeError)):
            self.hangups += 1
            return
        server_errors.append(f"{client_address[0]}:{client_address[1]} {error!r}")


def serve(directory):
    """A localhost origin, because <script type="module"> will not load over file://."""
    handler = functools.partial(_QuietHandler, directory=str(directory))
    sock = socket.socket()
    sock.bind(("127.0.0.1", 0))
    port = sock.getsockname()[1]
    sock.close()
    httpd = _QuietServer(("127.0.0.1", port), handler)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    return httpd, f"http://127.0.0.1:{port}"


def close_server(httpd):
    """Stop serving, then wait for every handler thread, so none is still running (or
    recording an error) once this returns."""
    httpd.shutdown()
    httpd.server_close()


def check_server_is_quiet():
    """serve() answers a found and a missing path, and a client that resets a transfer, without
    writing anything to stderr, and close_server() waits for the handler threads."""
    import contextlib
    import os
    import struct
    import tempfile
    import urllib.error
    import urllib.request

    captured = io.StringIO()
    statuses = []
    errors_before = len(server_errors)
    handlers = lambda: [t for t in threading.enumerate() if t.name.endswith("(process_request_thread)")]
    with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as folder:
        (Path(folder) / "present.txt").write_text("present", encoding="utf-8")
        (Path(folder) / "big.bin").write_bytes(bytes(8 * 1024 * 1024))
        # Everything up to the last handler finishing is captured, close_server() included.
        with contextlib.redirect_stderr(captured):
            httpd, origin = serve(folder)
            try:
                with urllib.request.urlopen(f"{origin}/present.txt", timeout=10) as response:
                    response.read()
                    statuses.append(response.status)
                try:
                    urllib.request.urlopen(f"{origin}/missing.txt", timeout=10).close()
                    statuses.append(200)
                except urllib.error.HTTPError as error:
                    statuses.append(error.code)
                # A page closed while it loads hangs up mid-transfer. This client reads only
                # the status line, so copyfile blocks on the full socket, and resets the
                # connection (SO_LINGER 0) 0.5 s later, while close_server() is waiting.
                client = socket.create_connection(httpd.server_address, timeout=10)
                try:
                    client.sendall(b"GET /big.bin HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n")
                    statuses.append(int(client.recv(64)[9:12]))  # HTTP/1.x status code
                    running = handlers()
                    client.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER,
                                      struct.pack("hh" if os.name == "nt" else "ii", 1, 0))
                finally:
                    threading.Timer(0.5, client.close).start()
                # Any other handler error is recorded for main(), not printed.
                try:
                    raise OSError("synthetic handler error")
                except OSError:
                    httpd.handle_error(None, ("127.0.0.1", 0))
            finally:
                close_server(httpd)
            outlived = handlers()
            for thread in outlived:
                thread.join(10)
    noise = [line for line in captured.getvalue().splitlines() if line.strip()]
    errors = server_errors[errors_before:]
    del server_errors[errors_before:]
    name = "the static server writes nothing to stderr"
    if statuses != [200, 404, 200]:
        fail(name, f"expected statuses [200, 404, 200], got {statuses}")
    elif noise:
        fail(name, f"{len(noise)} stderr line(s) for three requests, first: {noise[0]}")
    elif errors != ["127.0.0.1:0 OSError('synthetic handler error')"]:
        fail(name, f"expected only the synthetic handler error to be recorded, got {errors}")
    elif httpd.hangups < 1:
        fail(name, "the reset transfer never reached handle_error, so it was not exercised")
    else:
        ok(name, "one 200, one 404, one transfer the client reset, one handler error recorded")
    name = "the static server waits for its handler threads on close"
    if not running:
        fail(name, "no handler thread was visible mid-transfer, so none could be seen to outlive close")
    elif outlived:
        fail(name, f"{len(outlived)} handler thread(s) still running after server_close()")
    else:
        ok(name, "a handler blocked mid-transfer finished before server_close() returned")


STUB = """
(() => {
  const state = __STATE__;
  window.__smokeMessages = [];
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage: (msg) => {
      window.__smokeMessages.push(structuredClone(msg));
      let payload = {};
      switch (msg.type) {
        case 'loadState':     payload = state; break;
        case 'buildCommand':  payload = { command: 'MatrixDesktop.exe --smoke' }; break;
        case 'buildWebQuery': payload = { query: '' }; break;
        case 'saveDraft':     payload = { saved: true }; break;
        default:              payload = {}; break;
      }
      // Asynchronous, like the real host, so the page's promise plumbing is exercised
      // rather than short-circuited by a synchronous resolve.
      setTimeout(() => window.configHost.receive({ id: msg.id, ok: true, payload }), 0);
    },
  };
})();
"""


def check_configurator(page, origin, state_json):
    source = page.request.get(f"{origin}/js/app.js").body()
    assert source == (CONFIGURATOR / "js" / "app.js").read_bytes(), "configurator source witness differs from source on disk: js/app.js"
    print(f"  SOURCE {CONFIGURATOR / 'js' / 'app.js'} sha256={hashlib.sha256(source).hexdigest()}")
    page.add_init_script(STUB.replace("__STATE__", state_json))

    errors = []
    page.on("console", lambda m: errors.append(m.text) if m.type == "error" else None)
    page.on("pageerror", lambda e: errors.append(str(e)))

    page.goto(f"{origin}/index.html", wait_until="load")
    page.wait_for_function("document.querySelectorAll('.field').length > 0", timeout=15000)

    # Guard against every assertion below passing on an empty page.
    fields = page.evaluate("document.querySelectorAll('.field').length")
    if fields < 20:
        fail("configurator renders its fields", f"only {fields} .field elements; the page did not boot")
        return
    ok("configurator renders its fields", f"{fields} fields")

    if errors:
        fail("configurator loads without console errors", "; ".join(errors[:3]))
    else:
        ok("configurator loads without console errors")
    load_errors = len(errors)

    box = page.evaluate("""() => {
        const r = (sel) => {
            const el = document.querySelector(sel);
            if (!el) return null;
            const b = el.getBoundingClientRect();
            return { top: b.top, bottom: b.bottom, left: b.left, right: b.right,
                     width: b.width, height: b.height };
        };
        return {
            shell: r('.app-shell'),
            panel: r('.command-panel'),
            preview: r('.preview-pane'),
            surface: r('.field-surface'),
            viewport: { w: window.innerWidth, h: window.innerHeight },
            // Actually scroll it, rather than asking whether scrollHeight exceeds
            // clientHeight. Those are different questions: with overflow:visible the
            // content is still taller than the box, it just spills and gets clipped by
            // .editor's overflow:hidden, so the naive comparison reports healthy while
            // the lower fields are unreachable. A mutation to overflow:visible escaped
            // the first version of this check for exactly that reason.
            surfaceScrolls: (() => {
                const el = document.querySelector('.field-surface');
                if (!el) return false;
                const before = el.scrollTop;
                el.scrollTop = 99999;
                const moved = el.scrollTop > 0;
                el.scrollTop = before;
                return moved;
            })(),
        };
    }""")

    vh = box["viewport"]["h"]

    # THE regression. A 1px tolerance for subpixel rounding, nothing more.
    if box["panel"] is None:
        fail("the command panel exists", ".command-panel not found")
    elif box["panel"]["bottom"] > vh + 1:
        fail("the command panel is inside the viewport",
             f"its bottom edge is at {box['panel']['bottom']:.0f}px in a {vh}px viewport, "
             f"so Copy / Test Argument / the generated command are off-screen")
    else:
        ok("the command panel is inside the viewport",
           f"bottom {box['panel']['bottom']:.0f} <= {vh}")

    # The shell must fill the viewport and not exceed it. min-height:100% satisfies the
    # first half and fails the second, which is why both bounds are asserted.
    if abs(box["shell"]["height"] - vh) > 2:
        fail("the app shell is exactly viewport height",
             f"{box['shell']['height']:.0f}px against a {vh}px viewport; a taller shell means "
             f"the 1fr track sized to max-content instead of to the leftover space")
    else:
        ok("the app shell is exactly viewport height", f"{box['shell']['height']:.0f}px")

    # Long content has to scroll somewhere. If the shell is clipped and nothing scrolls,
    # the later fields are unreachable, which is a worse bug than the one being fixed.
    if not box["surfaceScrolls"]:
        fail("the field list scrolls rather than clipping",
             "setting field-surface.scrollTop left it at 0, so the fields below the fold "
             "cannot be reached inside a clipped shell")
    else:
        ok("the field list scrolls rather than clipping")

    # The command panel is a footer for a usually-one-line command. It has no business
    # taking a third of the window. It did: .command-row's height is set by the taller of
    # its two children, and a 7-item single-column button stack came to 280px, which the
    # textarea then stretched to match. 30% leaves room either side of the ~24% it now
    # measures, and would have failed on the ~38% it was.
    panel_share = box["panel"]["height"] / vh if box["panel"] else 1.0
    if panel_share > 0.30:
        fail("the command panel leaves the field list room",
             f"the panel is {box['panel']['height']:.0f}px, {panel_share:.0%} of a {vh}px "
             f"viewport, so the field list is squeezed for a one-line command")
    else:
        ok("the command panel leaves the field list room",
           f"{box['panel']['height']:.0f}px, {panel_share:.0%} of viewport")

    # Aspect ratio, because the renderer lays its grid out along the longer axis: a very
    # tall thin pane draws a handful of enormous columns instead of numColumns of them.
    # Asserted unconditionally. An earlier revision only ran this when the pane had a
    # non-zero width, which meant the fixture reporting no preview turned the check into a
    # silent skip that still printed a green run. main() now hands the page a live preview
    # origin, so a zero-width pane is a failure and says so.
    if not box["preview"] or box["preview"]["width"] <= 0:
        fail("the preview pane has a usable aspect ratio",
             "the pane has no width, so it is hidden and the check could not run")
    else:
        aspect = box["preview"]["width"] / max(1.0, box["preview"]["height"])
        if not 0.25 <= aspect <= 4.0:
            fail("the preview pane has a usable aspect ratio",
                 f"{box['preview']['width']:.0f}x{box['preview']['height']:.0f} = {aspect:.3f}")
        else:
            ok("the preview pane has a usable aspect ratio", f"{aspect:.2f}")

    # MD-38 lockdown. The light theme shipped with a dozen hardcoded dark values against a
    # --text that flips to near-black, so these read dark-on-dark. Contrast is computed the
    # WCAG way rather than by eyeballing hex values.
    for theme in ("dark", "light"):
        page.evaluate(f"document.documentElement.setAttribute('data-theme', '{theme}')")
        worst = page.evaluate("""() => {
            const lum = (c) => {
                const [r, g, b] = c.match(/\\d+(\\.\\d+)?/g).slice(0, 3).map(Number)
                    .map((v) => v / 255)
                    .map((v) => (v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4)));
                return 0.2126 * r + 0.7152 * g + 0.0722 * b;
            };
            const bgOf = (el) => {
                let n = el;
                while (n && n !== document.documentElement) {
                    const bg = getComputedStyle(n).backgroundColor;
                    if (bg && !bg.startsWith('rgba(0, 0, 0, 0)') && bg !== 'transparent') return bg;
                    n = n.parentElement;
                }
                return getComputedStyle(document.body).backgroundColor;
            };
            const sels = ['.topbar h1', '.group-nav button', '.command-header span',
                          '#randomizeButton', '#testButton', '#copyButton', '#stopButton'];
            let worst = { ratio: 99, sel: null };
            for (const sel of sels) {
                for (const el of document.querySelectorAll(sel)) {
                    const s = getComputedStyle(el);
                    if (s.display === 'none' || s.visibility === 'hidden') continue;
                    const a = lum(s.color), b = lum(bgOf(el));
                    const ratio = (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
                    if (ratio < worst.ratio) worst = { ratio, sel, color: s.color, bg: bgOf(el) };
                }
            }
            return worst;
        }""")
        # 3.0:1 is WCAG AA for large text and the honest floor for chrome like this. The
        # broken light theme measured about 1.4:1, so this has real margin either side.
        if worst["ratio"] < 3.0:
            fail(f"{theme} theme text is readable",
                 f"{worst['sel']} is {worst['ratio']:.2f}:1 ({worst['color']} on {worst['bg']})")
        else:
            ok(f"{theme} theme text is readable",
               f"worst {worst['sel']} at {worst['ratio']:.2f}:1")
    page.evaluate("document.documentElement.setAttribute('data-theme', 'dark')")

    # Scene dependencies must disable the actual compound controls, survive a
    # scene round trip, and reject late picker callbacks for a disabled field.
    problems = []

    def field(label):
        return page.locator(".field").filter(has=page.get_by_text(label, exact=True))

    def disabled(label, expected):
        values = field(label).locator("input, select, button").evaluate_all(
            "nodes => nodes.map(node => node.disabled)")
        if not values or any(value != expected for value in values):
            problems.append(f"{label}: disabled states {values}, expected {expected}")

    scene = field("Scene").locator("select")
    scene.select_option("matrix")
    disabled("Normal code color", True)
    disabled("Glint", False)
    disabled("Slant degrees", False)
    scene.select_option("uc3d")
    disabled("Normal code color", False)
    disabled("Glint", True)
    disabled("Palette gradient", True)
    disabled("Slant degrees", True)
    page.wait_for_timeout(350)  # Settle the intentional scene change's draft save.
    before = page.evaluate("window.__smokeMessages.filter(m => m.type === 'saveDraft').length")
    for label, selector, value in (("Glint", "input", "0.123"),
                                   ("Palette gradient", "input.hex-input", "#ff0000")):
        field(label).locator(selector).first.evaluate(
            "(node, value) => { node.value = value; node.dispatchEvent(new Event('input', {bubbles:true})); }", value)
    page.wait_for_timeout(350)
    after = page.evaluate("window.__smokeMessages.filter(m => m.type === 'saveDraft').length")
    if after != before:
        problems.append("a disabled color/palette callback saved a draft mutation")
    scene.select_option("matrix")
    disabled("Normal code color", True)
    disabled("Glint", False)
    disabled("Slant degrees", False)
    palette_editors = field("Palette gradient").locator("input, button.add-row").evaluate_all(
        "nodes => nodes.map(node => node.disabled)")
    if not palette_editors or any(palette_editors):
        problems.append(f"Matrix palette did not re-enable: {palette_editors}")
    if problems:
        fail("scene dependencies disable compound editors and restore editable controls", "; ".join(problems))
    else:
        ok("scene dependencies disable compound editors and restore editable controls")

    # Exercise the actual paired editors and their saved payload, rather than
    # merely asserting that range inputs exist in source or metadata.
    scene.select_option("uc3d")
    page.locator('[data-field="uc3dControls"] select').select_option("original")
    page.locator('[data-field="showPictures"] input').check()
    page.locator('[data-field="showClock"] input').check()
    page.locator('[data-field="randomForwardMovement"] input').uncheck()
    ranges = {
        "uc3dAmount": (100, 1500, 500), "uc3dGlyphSize": (10, 40, 12),
        "uc3dFallSpeed": (0, 150, 6), "uc3dForwardSpeed": (0, 120, 6),
        "uc3dPictureScale": (100, 300, 100), "uc3dPictureDelay": (0, 120, 60),
        "uc3dClockSize": (50, 170, 90),
    }
    problems = []
    for key, (minimum, maximum, current) in ranges.items():
        editor = page.locator(f'[data-field="{key}"]')
        slider = editor.locator('input[type="range"]')
        number = editor.locator('input[type="number"]')
        if slider.count() != 1 or number.count() != 1:
            problems.append(f"{key} is missing its paired slider/number editor")
            continue
        actual_range = slider.evaluate("node => [Number(node.min), Number(node.max), Number(node.step)]")
        if actual_range != [minimum, maximum, 1]:
            problems.append(f"{key} range {actual_range}")
        for value in (minimum, (minimum + maximum) // 2, maximum, current):
            slider.evaluate("(node, value) => { node.value = value; node.dispatchEvent(new Event('input', {bubbles:true})); }", value)
            if number.input_value() != str(value):
                problems.append(f"{key} slider {value} did not update its number")
            number.fill(str(maximum if value != maximum else minimum))
            if slider.input_value() != number.input_value():
                problems.append(f"{key} number did not update its slider")
        number.fill(str(current))
    amount = page.locator('[data-field="uc3dAmount"] input[type="range"]')
    amount.focus()
    amount.press("End")
    if page.locator('[data-field="uc3dAmount"] input[type="number"]').input_value() != "1500":
        problems.append("the amount slider did not support keyboard End")
    amount.press("Home")
    page.locator('[data-field="uc3dAmount"] input[type="number"]').fill("500.9")
    page.locator('[data-field="uc3dAmount"] input[type="number"]').blur()
    page.wait_for_timeout(350)
    saved = page.evaluate("window.__smokeMessages.filter(m => m.type === 'saveDraft').at(-1)?.payload.draft")
    if not saved or saved.get("uc3dAmount") != 500 or saved.get("uc3dControls") != "original":
        problems.append(f"paired edits saved the wrong amount or mode: {saved}")
    if problems:
        fail("UC3D sliders and numeric editors stay synchronized across their full ranges", "; ".join(problems))
    else:
        ok("UC3D sliders and numeric editors stay synchronized across their full ranges")

    problems = []
    def disabled_key(key, expected):
        states = page.locator(f'[data-field="{key}"]').locator("input, select, button").evaluate_all(
            "nodes => nodes.map(node => node.disabled)")
        if not states or any(value != expected for value in states):
            problems.append(f"{key} disabled states {states}, expected {expected}")
    for key in ("numColumns", "density", "fallSpeed", "forwardSpeed", "raindropLength", "pictureScale", "pictureDelay", "clockSize", "cycleSpeed", "isometric"):
        disabled_key(key, True)
    for key in ranges:
        disabled_key(key, False)
    page.locator('[data-field="randomForwardMovement"] input').check()
    disabled_key("uc3dForwardSpeed", True)
    page.locator('[data-field="randomForwardMovement"] input').uncheck()
    disabled_key("uc3dForwardSpeed", False)
    page.locator('[data-field="forwardMovement"] input').uncheck()
    disabled_key("uc3dForwardSpeed", True)
    page.locator('[data-field="forwardMovement"] input').check()
    page.locator('[data-field="volumetric"] input').uncheck()
    disabled_key("uc3dForwardSpeed", True)
    disabled_key("sidewaysMovement", True)
    page.locator('[data-field="volumetric"] input').check()
    late_number = page.locator('[data-field="uc3dAmount"] input[type="number"]').element_handle()
    late_range = page.locator('[data-field="uc3dAmount"] input[type="range"]').element_handle()
    page.locator('[data-field="uc3dControls"] select').select_option("legacy")
    page.locator('[data-field="volumetric"] input').uncheck()
    disabled_key("sidewaysMovement", False)
    page.locator('[data-field="volumetric"] input').check()
    for key in ranges:
        disabled_key(key, True)
    for key in ("numColumns", "density", "fallSpeed", "forwardSpeed", "raindropLength", "pictureScale", "pictureDelay", "clockSize", "cycleSpeed", "isometric"):
        disabled_key(key, False)
    page.wait_for_timeout(350)
    before = page.evaluate("window.__smokeMessages.filter(m => m.type === 'saveDraft').length")
    for node in (late_number, late_range):
        node.evaluate("node => { node.value = 1499; node.dispatchEvent(new Event('input', {bubbles:true})); }")
    page.wait_for_timeout(350)
    after = page.evaluate("window.__smokeMessages.filter(m => m.type === 'saveDraft').length")
    if after != before:
        problems.append("late original-control callbacks changed a legacy draft")
    page.locator('[data-field="uc3dControls"] select').select_option("original")
    if page.locator('[data-field="uc3dAmount"] input[type="number"]').input_value() != "500":
        problems.append("switching control units lost the original amount")
    if problems:
        fail("UC3D control modes disable inactive units and retain independent saved edits", "; ".join(problems))
    else:
        ok("UC3D control modes disable inactive units and retain independent saved edits")

    problems = []
    animation = page.locator('[data-field="animationSpeed"] input[type="number"]')
    if animation.get_attribute("min") != "0":
        problems.append("original animation speed did not expose a minimum of zero")
    animation.fill("-2")
    if animation.get_attribute("aria-invalid") != "true":
        problems.append("negative original animation speed did not report validation")
    animation.blur()
    if animation.input_value() != "0":
        problems.append("original animation speed did not clamp on blur")
    page.locator('[data-field="uc3dControls"] select').select_option("legacy")
    animation = page.locator('[data-field="animationSpeed"] input[type="number"]')
    animation.fill("-2")
    animation.blur()
    if animation.input_value() != "-2" or animation.get_attribute("min") != "-4":
        problems.append("legacy reverse animation was lost")
    bloom = page.locator('[data-field="bloomStrength"] input[type="number"]')
    bloom.fill("")
    bloom.blur()
    if bloom.input_value() != "0.7":
        problems.append("legacy bloom did not revert to its own default")
    if problems:
        fail("UC3D animation validation respects original and legacy motion units", "; ".join(problems))
    else:
        ok("UC3D animation validation respects original and legacy motion units")

    # AUD-55: the load check reads the collector once; theme toggles, scene dependencies and
    # late picker callbacks since then must not raise either.
    late = errors[load_errors:]
    if late:
        fail("configurator interactions raise no page errors", "; ".join(late[:3]))
    else:
        ok("configurator interactions raise no page errors")


UC3D_SLIDER_NAMES = {
    "uc3dAmount": "Line density: Low / Very high",
    "uc3dGlyphSize": "Glyph size: Small / Big",
    "uc3dFallSpeed": "Fall speed: Slow / Fast",
    "uc3dForwardSpeed": "Forward/back speed: Back / Forward",
    "uc3dPictureScale": "Picture scale: Full size / 1/3 size",
    "uc3dPictureDelay": "Picture delay: Shorter / Longer",
    "uc3dClockSize": "Clock size: Small / Large",
}


def check_scene_placements(page, state):
    """The original UC3D dialog groups Normal, Highlight and Background colour.

    The catalog's scenePlacements move the shared cursorColor and backgroundColor editors into the
    UC3D section while UC3D is selected, under the dialog's names. Each key must keep exactly one
    editor, the moved editors must write the same keys the Matrix scene edits, search must find
    them by either name, and the Matrix scene must still render the catalog unchanged.
    """
    groups = state["metadata"]["groups"]
    catalog_ids = {field["id"] for group in groups for field in group["fields"]}
    errors = []
    page.on("console", lambda m: errors.append(m.text) if m.type == "error" else None)
    page.on("pageerror", lambda e: errors.append(str(e)))

    def section_fields(group_id):
        return page.evaluate(
            """id => [...document.querySelectorAll(`#section-${id} .field-grid > .field`)]
                .map(node => ({ id: node.dataset.field, label: node.querySelector('label')?.textContent ?? '' }))""",
            group_id)

    def saved_drafts():
        return page.evaluate(
            "window.__smokeMessages.filter(m => m.type === 'saveDraft').map(m => m.payload.draft)")

    def filter_hits(text):
        page.locator("#fieldFilter").fill(text)
        return page.evaluate(
            """() => [...document.querySelectorAll('.field-section .field')]
                .map(node => node.closest('.field-section').id + ':' + node.dataset.field)""")

    scene = page.locator('[data-field="scene"] select')
    problems = []
    page.locator("#newPresetButton").click()
    scene.select_option("uc3d")
    page.wait_for_timeout(350)  # settle the scene change's debounced save

    uc3d = section_fields("uc3d")
    ids = [field["id"] for field in uc3d]
    labels = {field["id"]: field["label"] for field in uc3d}
    at = ids.index("codeColor") if "codeColor" in ids else -1
    if at < 0 or ids[at:at + 3] != ["codeColor", "cursorColor", "backgroundColor"]:
        problems.append(f"UC3D section order {ids}")
    for key, label in {"codeColor": "Normal code color", "cursorColor": "Highlight code color",
                       "backgroundColor": "Background color", **UC3D_SLIDER_NAMES}.items():
        if labels.get(key) != label:
            problems.append(f"{key} reads {labels.get(key)!r}, expected {label!r}")
    counts = page.evaluate("""() => ({
        cursor: document.querySelectorAll('[data-field="cursorColor"]').length,
        background: document.querySelectorAll('[data-field="backgroundColor"]').length,
        leftInColors: document.querySelectorAll(
            '#section-colors [data-field="cursorColor"], #section-colors [data-field="backgroundColor"]').length })""")
    if counts != {"cursor": 1, "background": 1, "leftInColors": 0}:
        problems.append(f"editor counts {counts}, expected exactly one per key and none left in Colors")
    swatches = page.evaluate("""() => ['codeColor', 'cursorColor', 'backgroundColor'].map(id => {
        const swatch = document.querySelector(`#section-uc3d [data-field="${id}"] .color-swatch`);
        return swatch ? getComputedStyle(swatch).backgroundColor : null; })""")
    if swatches != ["rgb(128, 0, 255)", "rgb(255, 128, 255)", "rgb(0, 0, 0)"]:
        problems.append(f"UC3D swatches {swatches}, expected the owner's purple, pink and black")
    if not page.evaluate("""() => ['cursorColor', 'backgroundColor'].every(id =>
            [...document.querySelectorAll(`#section-uc3d [data-field="${id}"] :is(input, button)`)].every(node => !node.disabled))"""):
        problems.append("a moved colour editor is disabled under UC3D")
    names = page.evaluate("""() => {
        const field = document.querySelector('#section-uc3d [data-field="cursorColor"]');
        return field ? [field.querySelector('.color-swatch')?.getAttribute('aria-label'),
            ...[...field.querySelectorAll('input[type="number"]')].map(node => node.getAttribute('aria-label'))] : null; }""")
    if names != ["Edit Highlight code color", "Highlight code color R", "Highlight code color G", "Highlight code color B"]:
        problems.append(f"Highlight accessible names {names}")
    intensity = page.evaluate("""() => document.querySelector('#section-colors [data-field="cursorIntensity"] label')?.textContent""")
    if intensity != "Highlight intensity":
        problems.append(f"cursorIntensity reads {intensity!r} under UC3D")
    # Renamed-in-place fields keep their catalog position; only the moved two leave Colors.
    colors_catalog = next(group for group in groups if group["id"] == "colors")
    expected_colors = [field["id"] for field in colors_catalog["fields"]
                       if field["id"] not in ("cursorColor", "backgroundColor")]
    shown_colors = [field["id"] for field in section_fields("colors")]
    if shown_colors != expected_colors:
        problems.append(f"UC3D Colors section {shown_colors}, expected catalog order {expected_colors}")

    # Guarded: a missing editor is this check's FAIL, never a harness error (exit 3), which the
    # gate reports as "the harness broke" rather than "the change broke it".
    highlight = page.locator('#section-uc3d [data-field="cursorColor"] input[type="number"]')
    before = saved_drafts()
    if highlight.count() == 0:
        problems.append("no Highlight code color editor in the UC3D section to edit")
    else:
        highlight.first.fill("0.5")
        page.wait_for_timeout(350)
    after = saved_drafts()
    if highlight.count() == 0:
        pass
    elif len(after) <= len(before):
        problems.append("editing Highlight code color saved no draft")
    else:
        draft = after[-1]
        if abs(float(draft.get("cursorColor", {}).get("r", -1)) - 0.5) > 1e-9:
            problems.append(f"Highlight edit saved cursorColor {draft.get('cursorColor')}")
        added = set(draft) - set(before[-1] if before else draft)
        unknown = set(draft) - catalog_ids
        if added or unknown:
            problems.append(f"Highlight edit added keys {sorted(added)}; non-catalog keys {sorted(unknown)}")
    # Search covers every name a moved or renamed field answers to: its visible label, its
    # catalog label, its new section and its catalog section.
    for term, hit in (("highlight", "section-uc3d:cursorColor"), ("cursor", "section-uc3d:cursorColor"),
                      ("colors", "section-uc3d:cursorColor"), ("colors", "section-uc3d:backgroundColor"),
                      ("uc3d", "section-uc3d:backgroundColor"), ("cursor intensity", "section-colors:cursorIntensity")):
        if hit not in filter_hits(term):
            problems.append(f"filter {term!r} does not find {hit}")
    filter_hits("")
    if errors:
        problems.append("console errors: " + "; ".join(errors[:3]))
    if problems:
        fail("UC3D shows Normal, Highlight and Background colors together on the shared keys", "; ".join(problems))
    else:
        ok("UC3D shows Normal, Highlight and Background colors together on the shared keys")

    problems = []

    def catalog_layout(scene_name):
        for group in groups:
            shown = [field["id"] for field in section_fields(group["id"])]
            expected = [field["id"] for field in group["fields"]]
            if shown != expected:
                problems.append(f"{scene_name} #{group['id']} shows {shown}, catalog order {expected}")
        labels = {field["id"]: field["label"] for field in section_fields("colors")}
        for key, label in {"cursorColor": "Cursor", "backgroundColor": "Background",
                           "cursorIntensity": "Cursor intensity"}.items():
            if labels.get(key) != label:
                problems.append(f"{scene_name} {key} reads {labels.get(key)!r}, expected {label!r}")

    scene.select_option("matrix")
    page.wait_for_timeout(350)
    catalog_layout("Matrix")
    # The Matrix Cursor editor shows the draft's cursorColor, the key the UC3D check saw the
    # Highlight editor write: one key, two names. Compared against the saved draft rather than a
    # literal, so this check stays independent of whether the UC3D edit succeeded.
    shown, expected = page.evaluate("""() => {
        const swatch = document.querySelector('#section-colors [data-field="cursorColor"] .color-swatch');
        const drafts = window.__smokeMessages.filter(m => m.type === 'saveDraft');
        const color = drafts.length ? drafts[drafts.length - 1].payload.draft.cursorColor : null;
        const channel = v => Math.round(Math.max(0, Math.min(1, Number(v))) * 255);
        return [swatch ? getComputedStyle(swatch).backgroundColor : null,
                color ? `rgb(${channel(color.r)}, ${channel(color.g)}, ${channel(color.b)})` : null]; }""")
    if shown is None or shown != expected:
        problems.append(f"Matrix Cursor swatch {shown}, saved cursorColor {expected}")
    scene.select_option("zmatrix")
    page.wait_for_timeout(350)
    catalog_layout("ZMatrix")
    # Placements are re-derived from the catalog on every render and never written back, so a
    # trip through UC3D must leave the Matrix layout exactly as the catalog has it.
    scene.select_option("uc3d")
    page.wait_for_timeout(350)
    scene.select_option("matrix")
    page.wait_for_timeout(350)
    catalog_layout("Matrix after a UC3D round trip")
    if errors:
        problems.append("console errors: " + "; ".join(errors[:3]))
    if problems:
        fail("Matrix and ZMatrix keep the catalog layout across scene round trips", "; ".join(problems))
    else:
        ok("Matrix and ZMatrix keep the catalog layout across scene round trips")


def mean_luma(png_bytes):
    from PIL import Image  # imported lazily; main() reports CANNOT VERIFY if it is absent
    im = Image.open(io.BytesIO(png_bytes)).convert("RGB")
    px = im.tobytes()
    total = 0.0
    for i in range(0, len(px), 3):
        total += 0.2126 * px[i] + 0.7152 * px[i + 1] + 0.0722 * px[i + 2]
    return total / (len(px) // 3)


def check_glyph_intensity(browser, origin):
    """A flag that renders identically at every value is a flag that does nothing.

    glyphIntensity was accepted by the CLI, listed in the argument guide, offered in the
    configurator and set by Randomize for the whole of v1.0.x, while being read by no shader
    at all. Nothing detected that, because nothing had ever asserted a flag CHANGES the
    output. This does, for both effects that implement it.
    """
    def luma(query):
        page = browser.new_page(viewport={"width": 480, "height": 360})
        try:
            page.goto(f"{origin}/index.html?{query}", wait_until="load")
            # Sample after two seconds of the renderer's own clock (it starts at the first frame)
            # and at least ten frames. This used to sleep 2.5 s from when the canvas appeared,
            # which happens before the renderer modules load, so a slow load sampled a page that
            # had not drawn yet and blamed the uniform (CI on fd2358f, AUD-78). The deadline turns
            # a renderer that never draws into a named failure instead of a hang.
            try:
                page.wait_for_function("window.matrixRuntimeDiagnostics?.ready === true"
                                       " && matrixRuntimeDiagnostics.frames >= 10"
                                       " && matrixRuntimeDiagnostics.simulationTime >= 2", timeout=60000)
            except Exception:
                state = page.evaluate("JSON.stringify({frames: window.matrixRuntimeDiagnostics?.frames ?? 0,"
                                      " errors: window.matrixRuntimeDiagnostics?.errors ?? []})")
                raise RuntimeError(f"{query} did not render 2 s of frames within 60 s: {state}") from None
            # Averaged over frames because rain is stochastic; one frame is not a measurement.
            return sum(mean_luma(page.screenshot()) for _ in range(3)) / 3
        finally:
            page.close()

    for effect, extra in (("palette", ""), ("stripe", "&effect=stripes")):
        base = f"suppressWarnings=true&numColumns=40{extra}"
        try:
            dark = luma(f"{base}&glyphIntensity=0")
            bright = luma(f"{base}&glyphIntensity=2")
        except RuntimeError as exc:
            fail(f"glyphIntensity changes the {effect} render", f"could not sample: {exc}")
            continue
        # Deliberately compares 0 against 2 rather than against the default. Comparing to the
        # default would also pass if the shader ignored the uniform and both runs were simply
        # the default render.
        if bright <= dark * 3:
            fail(f"glyphIntensity changes the {effect} render",
                 f"luma at 0 is {dark:.2f} and at 2 is {bright:.2f}; the uniform is not "
                 f"reaching the shader, so the flag is inert again")
        else:
            ok(f"glyphIntensity changes the {effect} render",
               f"luma {dark:.1f} at 0 vs {bright:.1f} at 2")


def check_inherited_effect_names(browser, origin):
    """An unknown effect name falls back to palette, including names every object inherits.

    The renderer picked its effect with `config.effect in effects`, which is also true for
    constructor, toString and __proto__, so effect=constructor handed Object to the pipeline
    as a pass factory and the renderer failed to start instead of falling back (AUD-79).
    """
    problems, frames = [], []
    for name in ("constructor", "toString", "__proto__"):
        page = browser.new_page(viewport={"width": 320, "height": 240})
        errors = []
        page.on("console", lambda m: errors.append(m.text) if m.type == "error" else None)
        page.on("pageerror", lambda e: errors.append(str(e)))
        try:
            page.goto(f"{origin}/index.html?suppressWarnings=true&numColumns=20&effect={name}", wait_until="load")
            try:
                page.wait_for_function("window.matrixRuntimeDiagnostics?.frames > 3"
                                       " || window.matrixRuntimeDiagnostics?.errors.length > 0"
                                       " || document.querySelector('.notice') != null", timeout=30000)
            except Exception:
                pass  # reported below from the state the page reached
            state = page.evaluate("""() => ({
                frames: window.matrixRuntimeDiagnostics?.frames ?? 0,
                errors: window.matrixRuntimeDiagnostics?.errors ?? [],
                notice: document.querySelector('.notice')?.textContent.trim() ?? null })""")
        finally:
            page.close()
        found = [(text.splitlines() or [""])[0] for text in
                 errors + state["errors"] + ([state["notice"]] if state["notice"] else [])]
        frames.append(f"{name} {state['frames']}")
        if state["frames"] <= 3 or found:
            problems.append(f"effect={name}: {state['frames']} frames; " + "; ".join(found[:2]))
    if problems:
        fail("inherited effect names fall back to the palette render", " | ".join(problems))
    else:
        ok("inherited effect names fall back to the palette render", "frames: " + ", ".join(frames))


def check_matrix_web(page, origin):
    """The vendored bundle. Asserts it boots and draws, which nothing in CI covered before."""
    errors = []
    page.on("console", lambda m: errors.append(m.text) if m.type == "error" else None)
    page.on("pageerror", lambda e: errors.append(str(e)))

    # An explicit resolution, so the expected size below does not depend on config.js's default.
    resolution = 0.5
    page.goto(f"{origin}/index.html?suppressWarnings=true&numColumns=40&resolution={resolution}", wait_until="load")
    try:
        page.wait_for_function("document.querySelector('canvas') != null", timeout=20000)
    except Exception:
        fail("matrix web bundle creates a canvas", "no canvas after 20s; " + "; ".join(errors[:3]) + "; url=" + page.url)
        return

    # R2-50: the canvas exists, at the element's default 300x150, before any renderer sizes it,
    # so a fixed 3 s wait read an unsized canvas as sized under load. ready is set from the
    # first frame, after the first resize. Two seconds of the renderer's own clock keep the
    # console-error window as long as the old wait, counted from the renderer's start, and the
    # deadline makes a renderer that never starts a named failure, not a hang.
    try:
        page.wait_for_function(READY + " && window.matrixRuntimeDiagnostics.simulationTime >= 2", timeout=60000)
        stalled = None
    except Exception:
        stalled = page.evaluate("JSON.stringify({frames: window.matrixRuntimeDiagnostics?.frames ?? 0,"
                                " errors: window.matrixRuntimeDiagnostics?.errors ?? []})")

    if errors:
        fail("matrix web bundle loads without console errors", "; ".join(errors[:3]))
    else:
        ok("matrix web bundle loads without console errors")

    if stalled is not None:
        fail("matrix web bundle sizes its canvas", f"no renderer drew 2 s of frames within 60 s: {stalled}")
        return
    size = page.evaluate("""r => {
        const c = document.querySelector('canvas');
        return { width: c.width, height: c.height,
                 expectedWidth: Math.ceil(c.clientWidth * devicePixelRatio * r),
                 expectedHeight: Math.ceil(c.clientHeight * devicePixelRatio * r) };
    }""", resolution)
    actual, expected = f"{size['width']}x{size['height']}", f"{size['expectedWidth']}x{size['expectedHeight']}"
    if abs(size["width"] - size["expectedWidth"]) <= 1 and abs(size["height"] - size["expectedHeight"]) <= 1:
        ok("matrix web bundle sizes its canvas", f"{actual} at resolution {resolution}")
    else:
        fail("matrix web bundle sizes its canvas",
             f"{actual}, expected {expected} (client size x devicePixelRatio x resolution {resolution})")


NOTICE_IN_VIEW = ("text => { const n = [...document.querySelectorAll('.notice')].find(n => n.textContent.includes(text)); "
                  "const r = n.getBoundingClientRect(); "
                  "return { top: Math.round(r.top), height: innerHeight, inView: r.top >= 0 && r.top < innerHeight }; }")


def check_config_error_boundary(browser, origin):
    """A configuration exception must produce the visible startup notice, not a silent page."""
    name = "a configuration exception shows the visible startup error"
    page = browser.new_page(viewport={"width": 480, "height": 360})
    served = []

    def stub(route):
        served.append(route.request.url)
        route.fulfill(content_type="text/javascript",
                      body="export default () => { throw new Error('injected configuration failure'); };")

    try:
        page.route("**/js/config.js", stub)
        page.goto(f"{origin}/index.html?suppressWarnings=true", wait_until="load")
        try:
            page.wait_for_function("[...document.querySelectorAll('.notice')].some(n => "
                                   "n.textContent.includes('injected configuration failure'))", timeout=10000)
        except Exception:
            if not served:
                fail(name, "the config.js stub was never requested, so the boundary was not exercised")
            else:
                fail(name, "makeConfig threw and no .notice explains it; the page stays black")
            return
        where = page.evaluate(NOTICE_IN_VIEW, "injected configuration failure")
        if not where["inView"]:
            fail(name, f"the notice is outside the viewport (top {where['top']}px of {where['height']}px)")
            return
        ok(name, f"stub served {len(served)}x")
    finally:
        page.close()


READY = "window.matrixRuntimeDiagnostics?.ready === true && window.matrixRuntimeDiagnostics.frames > 2"


def open_software_notice(browser, origin, query="numColumns=40&resolution=1"):
    """The SwiftShader notice path: no suppressWarnings, so the page must wait for a click."""
    page = browser.new_page(viewport={"width": 480, "height": 360})
    page.goto(f"{origin}/index.html?{query}", wait_until="load")
    try:
        page.wait_for_function("document.querySelector('.blue.pill') != null", timeout=10000)
    except Exception:
        page.close()
        return None
    reached = page.evaluate("() => document.querySelector('.blue.pill').offsetParent != null && "
                            "getComputedStyle(document.querySelector('canvas')).display === 'none'")
    if not reached:
        page.close()
        return None
    return page


def check_plug_me_in(browser, origin):
    """'Plug me in' must start one renderer, sized to the canvas it has just made visible."""
    name = "Plug me in starts a renderer sized to the visible canvas"
    page = open_software_notice(browser, origin)
    if page is None:
        fail(name, "notice path not reached: no visible .blue.pill over a hidden canvas")
    else:
        try:
            page.click(".blue.pill")
            page.wait_for_function(READY, timeout=20000)
            size = page.evaluate("() => { const c = document.querySelector('canvas'); "
                                 "return { width: c.width, height: c.height, expected: Math.ceil(c.clientWidth * devicePixelRatio) }; }")
            if size["width"] > 1 and size["width"] == size["expected"]:
                ok(name, f"{size['width']}x{size['height']}")
            else:
                fail(name, f"canvas backing store is {size['width']}x{size['height']}, expected width {size['expected']}")
        except Exception as exc:
            fail(name, f"no ready renderer with frames > 2 after the click: {type(exc).__name__}")
        finally:
            page.close()

    name = "Plug me in clicked twice starts exactly one renderer"
    page = open_software_notice(browser, origin)
    if page is None:
        fail(name, "notice path not reached: no visible .blue.pill over a hidden canvas")
        return
    try:
        page.evaluate("() => { const pill = document.querySelector('.blue.pill'); pill.click(); pill.click(); }")
        page.wait_for_function(READY, timeout=20000)
        page.wait_for_timeout(1000)
        seen = page.evaluate("() => ({ scripts: document.querySelectorAll(\"script[src$='lib/regl.min.js']\").length, "
                             "notices: document.querySelectorAll('.notice').length })")
        if seen == {"scripts": 1, "notices": 0}:
            ok(name)
        else:
            fail(name, f"{seen['scripts']} regl.min.js script tag(s) and {seen['notices']} .notice element(s); expected 1 and 0")
    except Exception as exc:
        fail(name, f"no ready renderer with frames > 2 after two clicks: {type(exc).__name__}")
    finally:
        page.close()


def check_noninteractive_software_start(browser, origin):
    """Wallpaper, screensaver and preview hosts cannot click: they must start, not block."""
    name = "a non-interactive host on SwiftShader starts rendering instead of blocking"
    page = browser.new_page(viewport={"width": 480, "height": 360})
    try:
        page.goto(f"{origin}/index.html?suppressWarnings=true&numColumns=40", wait_until="load")
        renderer = page.evaluate("() => { const gl = document.createElement('canvas').getContext('webgl'); "
                                 "const info = gl?.getExtension('WEBGL_debug_renderer_info'); "
                                 "return info ? String(gl.getParameter(info.UNMASKED_RENDERER_WEBGL)) : ''; }")
    finally:
        page.close()
    if "swiftshader" not in renderer.lower():
        fail(name, f"witness failed: this Chromium renders with '{renderer}', not SwiftShader, so the notice path is unreachable")
        return

    page = browser.new_page(viewport={"width": 480, "height": 360})
    warnings = []
    page.on("console", lambda m: warnings.append(m.text) if m.type == "warning" else None)
    try:
        page.add_init_script("window.matrixHostBootstrap = { interactive: false }; "
                             "window.matrixHostBootstrapReady = Promise.resolve(window.matrixHostBootstrap);")
        page.goto(f"{origin}/index.html?numColumns=40", wait_until="load")
        page.wait_for_function(READY, timeout=20000)
        first = page.evaluate("window.matrixRuntimeDiagnostics.frames")
        page.wait_for_timeout(700)
        seen = page.evaluate("() => ({ frames: window.matrixRuntimeDiagnostics.frames, "
                             "pill: document.querySelector('.blue.pill') != null, "
                             "display: getComputedStyle(document.querySelector('canvas')).display })")
        problems = []
        if seen["pill"]:
            problems.append("the blocking notice is shown")
        if seen["display"] == "none":
            problems.append("the canvas is hidden")
        if seen["frames"] <= first:
            problems.append(f"frames did not advance ({first} -> {seen['frames']})")
        if not any("hardware acceleration" in text.lower() for text in warnings):
            problems.append("no console warning says hardware acceleration is disabled")
        if problems:
            fail(name, "; ".join(problems))
        else:
            ok(name, f"frames {first} -> {seen['frames']}")
    except Exception as exc:
        fail(name, f"no ready renderer with frames > 2 behind interactive:false: {type(exc).__name__}")
    finally:
        page.close()

    for label, script in (("no bootstrap", None),
                          ("interactive:true", "window.matrixHostBootstrap = { interactive: true };")):
        name = f"an interactive host ({label}) on SwiftShader still shows the blocking notice"
        page = browser.new_page(viewport={"width": 480, "height": 360})
        try:
            if script:
                page.add_init_script(script)
            page.goto(f"{origin}/index.html?numColumns=40", wait_until="load")
            try:
                page.wait_for_function("document.querySelector('.blue.pill') != null", timeout=10000)
                held = page.evaluate("() => getComputedStyle(document.querySelector('canvas')).display === 'none' "
                                     "&& window.matrixRuntimeDiagnostics == null")
            except Exception:
                held = False
            if held:
                ok(name)
            else:
                fail(name, "no .blue.pill over a hidden canvas with no renderer started")
        finally:
            page.close()


def bright_fraction(png_bytes, threshold=40):
    from PIL import Image
    histogram = Image.open(io.BytesIO(png_bytes)).convert("L").histogram()
    return sum(histogram[threshold + 1:]) / max(1, sum(histogram))


LOSE = ("() => { window.__beforeLoss = true; const gl = document.querySelector('canvas').getContext('webgl'); "
        "window.__lose = gl.getExtension('WEBGL_lose_context'); window.__lose.loseContext(); }")


LIT = 0.002  # a lost-and-restored canvas is exactly black; ZMatrix rain at 480x360 lights about 0.8%


def check_context_loss_recovery(browser, origin):
    """A lost WebGL context must come back as rendering rain, not a black page that looks healthy."""
    for scene in ("matrix", "uc3d", "zmatrix"):
        name = f"{scene} renders again after a WebGL context loss and restore"
        page = browser.new_page(viewport={"width": 480, "height": 360})
        try:
            page.goto(f"{origin}/index.html?suppressWarnings=true&scene={scene}&numColumns=40", wait_until="load")
            page.wait_for_function(READY, timeout=30000)
            page.wait_for_timeout(1500)
            before = bright_fraction(page.screenshot())
            if before <= LIT:
                fail(name, f"witness failed: only {before:.2%} bright pixels before the loss")
                continue
            page.evaluate(LOSE)
            page.wait_for_timeout(300)
            try:
                with page.expect_navigation(timeout=8000):
                    page.evaluate("window.__lose.restoreContext()")
            except Exception:
                fail(name, "the page did not reload within 8 s of restoreContext()")
                continue
            page.wait_for_function(READY + " && window.__beforeLoss === undefined", timeout=30000)
            first = page.evaluate("window.matrixRuntimeDiagnostics.frames")
            for _ in range(12):  # rain builds up after the reload; ZMatrix starts sparse
                page.wait_for_timeout(500)
                after = bright_fraction(page.screenshot())
                if after > LIT:
                    break
            frames = page.evaluate("window.matrixRuntimeDiagnostics.frames")
            if frames > first and after > LIT:
                ok(name, f"bright {before:.1%} -> {after:.1%}, frames {first} -> {frames}")
            else:
                fail(name, f"after the reload: frames {first} -> {frames}, {after:.2%} bright pixels")
        except Exception as exc:
            fail(name, f"{type(exc).__name__}: {str(exc).splitlines()[0]}")
        finally:
            page.close()

    name = "a WebGL context that is never restored still reloads"
    page = browser.new_page(viewport={"width": 480, "height": 360})
    try:
        page.goto(f"{origin}/index.html?suppressWarnings=true&numColumns=40", wait_until="load")
        page.wait_for_function(READY, timeout=30000)
        with page.expect_navigation(timeout=14000):
            page.evaluate(LOSE)
        ok(name)
    except Exception as exc:
        fail(name, f"no reload within 14 s of an unrestored loss: {type(exc).__name__}")
    finally:
        page.close()

    name = "a renderer loss past the reload budget leaves a visible notice"
    page = browser.new_page(viewport={"width": 480, "height": 360})
    try:
        page.goto(f"{origin}/index.html?suppressWarnings=true&numColumns=40", wait_until="load")
        page.wait_for_function(READY, timeout=30000)
        page.evaluate("() => sessionStorage.setItem('matrixRendererRecoveries', JSON.stringify([Date.now(), Date.now(), Date.now()]))")
        page.evaluate("() => { window.__beforeLoss = true; window.matrixRendererLost('WebGPU device lost (smoke)'); }")
        page.wait_for_function("[...document.querySelectorAll('.notice')].some(n => n.textContent.includes('could not be recovered'))", timeout=5000)
        where = page.evaluate(NOTICE_IN_VIEW, "could not be recovered")
        if page.evaluate("window.__beforeLoss") is not True:
            fail(name, "the page reloaded although the budget was spent")
        elif not where["inView"]:
            fail(name, f"the notice is outside the viewport (top {where['top']}px of {where['height']}px)")
        else:
            ok(name)
    except Exception as exc:
        fail(name, f"no notice after the budget was spent: {type(exc).__name__}")
    finally:
        page.close()


def check_image_effect_offline(browser, origin):
    """effect=image must render with no network (AUD-21): with no url it draws the bundled default
    image without any external request, and an image that cannot load leaves the rain uncoloured
    with a console warning instead of failing the renderer."""
    unreachable = "https://example.invalid/background.png"
    cases = (
        ("effect=image with no url renders its bundled default with no external request", "", None),
        ("effect=image with an unreachable url still renders and names it in a warning",
         "&url=https%3A%2F%2Fexample.invalid%2Fbackground.png", unreachable),
    )
    for name, query, expected_block in cases:
        page = browser.new_page(viewport={"width": 480, "height": 360})
        blocked, warnings = [], []
        page.on("console", lambda m, warnings=warnings: warnings.append(m.text) if m.type == "warning" else None)

        def offline_for(blocked):
            def offline(route):
                if route.request.url.startswith(origin + "/"):
                    route.continue_()
                else:
                    blocked.append(route.request.url)
                    route.abort("internetdisconnected")
            return offline

        try:
            page.route("**/*", offline_for(blocked))
            page.goto(f"{origin}/index.html?suppressWarnings=true&numColumns=40&effect=image{query}", wait_until="load")
            try:
                page.wait_for_function(READY, timeout=20000)
            except Exception:
                notice = page.evaluate("() => [...document.querySelectorAll('.notice')].map(n => n.textContent.trim()).join(' | ')")
                fail(name, f"no ready renderer within 20 s ({len(blocked)} external request(s) blocked); notice: {notice or 'none'}")
                continue
            page.wait_for_timeout(1500)
            lit = bright_fraction(page.screenshot())
            image_warnings = [text for text in warnings if "background image" in text.lower()]
            problems = []
            if page.evaluate("document.querySelectorAll('.notice').length") > 0:
                problems.append("a .notice is shown")
            if lit <= LIT:
                problems.append(f"only {lit:.2%} bright pixels, so the rain is not visible")
            if expected_block is None:
                if blocked:
                    problems.append("the default image needs the network: " + ", ".join(blocked))
                if image_warnings:
                    problems.append("the bundled default image did not load: " + image_warnings[0])
            else:
                if blocked != [expected_block]:
                    problems.append(f"expected only {expected_block} to be blocked, got {blocked}")
                if not any(expected_block in text for text in image_warnings):
                    problems.append("no console warning names the unreachable background image")
            if problems:
                fail(name, "; ".join(problems))
            else:
                ok(name, f"{len(blocked)} external request(s) blocked, {lit:.1%} bright pixels")
        finally:
            page.close()


# Every WebGL context request returns null and WebGPU is absent, as in a page Chromium blocks
# from 3D APIs for the rest of the session after repeated GPU resets.
REFUSE_3D = """(() => {
  const getContext = HTMLCanvasElement.prototype.getContext;
  HTMLCanvasElement.prototype.getContext = function (type, ...rest) {
    return /webgl/i.test(String(type)) ? null : getContext.call(this, type, ...rest);
  };
  Object.defineProperty(Navigator.prototype, 'gpu', { configurable: true, get: () => undefined });
})();"""


def check_refused_webgl_context(browser, origin):
    """A page refused every 3D context gets the restart notice, not a TypeError (R2-55).

    The AUD-19 recovery reload after a second GPU reset lands in such a page; REGL then failed
    on its null context and the page said "Cannot read properties of null (reading 'on')"
    instead of asking for the restart that a new browser process needs.
    """
    name = "a page refused every WebGL context shows the restart notice"
    problems, seen = [], []
    for scene, extra in (("matrix", ""), ("matrix", "&renderer=webgpu"), ("uc3d", ""), ("zmatrix", "")):
        leg = f"{scene}{extra}"
        page = browser.new_page(viewport={"width": 480, "height": 360})
        errors, navigations = [], []
        page.on("pageerror", lambda e, errors=errors: errors.append(f"page error: {e}"))
        page.on("console", lambda m, errors=errors: errors.append(m.text) if "Cannot read properties" in m.text else None)
        page.on("framenavigated", lambda f, page=page, navigations=navigations: navigations.append(f.url) if f == page.main_frame else None)
        try:
            page.add_init_script(REFUSE_3D)
            page.goto(f"{origin}/index.html?suppressWarnings=true&numColumns=40&scene={scene}{extra}", wait_until="load")
            try:
                page.wait_for_function("document.querySelector('.notice') != null", timeout=20000)
            except Exception:
                problems.append(f"{leg}: no notice within 20 s")
                continue
            page.wait_for_timeout(500)
            state = page.evaluate("""() => ({
                refused: document.createElement('canvas').getContext('webgl') === null,
                notice: [...document.querySelectorAll('.notice')].map(n => n.textContent.trim()).join(' | '),
                diagnostics: window.matrixRuntimeDiagnostics?.errors ?? null,
                stamps: sessionStorage.getItem('matrixRendererRecoveries') })""")
            if not state["refused"]:
                problems.append(f"{leg}: witness failed, the page still gets a WebGL context")
                continue
            restart = "could not be recovered automatically" in state["notice"] and "Restart MatrixDesktop" in state["notice"]
            if not restart:
                problems.append(f"{leg}: the notice says '{state['notice']}'")
            if scene == "matrix" and len(state["diagnostics"] or []) != 1:
                problems.append(f"{leg}: diagnostics recorded {state['diagnostics']}, expected one error")
            leaked = [text for text in [state["notice"], *(state["diagnostics"] or []), *errors]
                      if "Cannot read properties" in text or text.startswith("page error:")]
            if leaked:
                problems.append(f"{leg}: {leaked[0]}")
            if len(navigations) != 1 or state["stamps"] is not None:
                problems.append(f"{leg}: {len(navigations)} navigation(s) and recovery stamps {state['stamps']}; expected no reload")
            if restart and not page.evaluate(NOTICE_IN_VIEW, "Restart MatrixDesktop")["inView"]:
                problems.append(f"{leg}: the notice is outside the viewport")
            seen.append(leg)
        finally:
            page.close()
    if problems:
        fail(name, " | ".join(problems))
    else:
        ok(name, ", ".join(seen))


def check_no_favicon_request(browser, origin):
    """index.html declares an empty icon, so the browser never asks for /favicon.ico (R2-56).

    web/ ships no favicon, so every app launch logged net::ERR_FILE_NOT_FOUND for it beside the
    errors that matter. The headless shell never fetches favicons and Playwright's request events
    miss the browser's own fetch, so this check runs the full Chromium build headless and reads
    CDP. The same page with its icon link stripped must make the request, or a pass means nothing.
    """
    import re

    name = "index.html makes no /favicon.ico request"
    try:
        full = browser.browser_type.launch(channel="chromium", args=["--use-gl=angle", "--use-angle=swiftshader",
                                                                     "--enable-unsafe-swiftshader"])
    except Exception as exc:
        fail(name, f"the full Chromium build would not start ({type(exc).__name__}), and the headless shell never "
                   "requests a favicon; install it with: python -m playwright install chromium")
        return

    def strip_icon(route):
        response = route.fetch()
        route.fulfill(response=response, body=re.sub(r'\s*<link rel="icon"[^>]*>', "", response.text()))

    requested = {}
    try:
        # The shipped page first: a favicon that failed once may not be fetched again.
        for label in ("as shipped", "icon link stripped"):
            page = full.new_page(viewport={"width": 480, "height": 360})
            seen = requested[label] = []
            try:
                if label != "as shipped":
                    page.route("**/index.html*", strip_icon)
                cdp = page.context.new_cdp_session(page)
                cdp.on("Network.requestWillBeSent", lambda e, seen=seen: seen.append(e["request"]["url"])
                       if e["request"]["url"].split("?")[0].endswith("/favicon.ico") else None)
                cdp.send("Network.enable")
                page.goto(f"{origin}/index.html?suppressWarnings=true&numColumns=40", wait_until="load")
                for _ in range(12):  # the fetch follows the load event within milliseconds
                    if seen:
                        break
                    page.wait_for_timeout(250)
            finally:
                page.close()
    finally:
        full.close()
    if requested["as shipped"]:
        fail(name, f"the shipped page requested {requested['as shipped'][0]}")
    elif not requested["icon link stripped"]:
        fail(name, "witness failed: without its icon link the page made no /favicon.ico request either")
    else:
        ok(name, f"the stripped page requested it {len(requested['icon link stripped'])}x")


def check_startup_paths(browser, origin):
    check_config_error_boundary(browser, origin)
    check_plug_me_in(browser, origin)
    check_noninteractive_software_start(browser, origin)
    check_context_loss_recovery(browser, origin)
    check_image_effect_offline(browser, origin)
    check_refused_webgl_context(browser, origin)
    check_no_favicon_request(browser, origin)


def main():
    state_path = REPO / "artifacts" / "gate" / "loadState.json"
    if not state_path.exists():
        print(f"  loadState fixture missing: {state_path}", file=sys.stderr)
        print("  run: tests\\MatrixDesktop.Tests\\bin\\Release\\MatrixDesktop.Tests.exe "
              "--dump-state artifacts\\gate\\loadState.json", file=sys.stderr)
        return 3
    state = json.loads(state_path.read_text(encoding="utf-8"))

    try:
        from playwright.sync_api import sync_playwright
        from PIL import Image  # noqa: F401  -- mean_luma needs it; fail fast, not mid-run
    except ImportError as exc:
        print(f"  a dependency is missing for this interpreter: {exc}", file=sys.stderr)
        return 2

    print("=== web smoke: harness ===")
    check_server_is_quiet()

    cfg_httpd, cfg_origin = serve(CONFIGURATOR)
    web_httpd, web_origin = serve(WEB)

    # The C# fixture cannot know these ports, so the preview origin is filled in here. This
    # is not cosmetic: with preview.available false the page hides the pane, and the
    # aspect-ratio assertion then has nothing to measure. Pointing it at the real bundle
    # also means the iframe loads the actual renderer, so the configurator's live preview
    # is covered end to end rather than as an empty box.
    state["preview"] = {"available": True, "origin": f"{web_origin}/"}
    state_json = json.dumps(state)

    try:
        with sync_playwright() as pw:
            try:
                browser = pw.chromium.launch(args=["--use-gl=angle", "--use-angle=swiftshader",
                                                   "--enable-unsafe-swiftshader"])
            except Exception as exc:
                print(f"  chromium is unavailable: {exc}", file=sys.stderr)
                return 2

            print("=== web smoke: configurator ===")
            page = browser.new_page(viewport=VIEWPORT)
            check_configurator(page, cfg_origin, state_json)
            check_scene_placements(page, state)
            page.close()

            print("=== web smoke: matrix bundle ===")
            page = browser.new_page(viewport=VIEWPORT)
            check_matrix_web(page, web_origin)
            page.close()
            check_inherited_effect_names(browser, web_origin)

            print("=== web smoke: flags actually do something ===")
            check_glyph_intensity(browser, web_origin)

            print("=== web smoke: renderer startup paths ===")
            check_startup_paths(browser, web_origin)

            browser.close()
    finally:
        close_server(cfg_httpd)
        close_server(web_httpd)
    if server_errors:
        fail("the static servers raise nothing but client hang-ups",
             f"{len(server_errors)} handler error(s), first: {server_errors[0]}")

    print()
    if failures:
        print(f"WEB SMOKE FAILED: {len(failures)} of {len(failures) + len(passes)} checks")
        return 1
    print(f"WEB SMOKE PASSED: {len(passes)} checks")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:  # harness error, distinct from a product failure
        print(f"  harness error: {type(exc).__name__}: {exc}", file=sys.stderr)
        sys.exit(3)
