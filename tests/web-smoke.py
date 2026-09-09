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


def serve(directory):
    """A localhost origin, because <script type="module"> will not load over file://."""
    handler = functools.partial(http.server.SimpleHTTPRequestHandler, directory=str(directory))
    handler.log_message = lambda *a, **k: None
    sock = socket.socket()
    sock.bind(("127.0.0.1", 0))
    port = sock.getsockname()[1]
    sock.close()
    httpd = http.server.ThreadingHTTPServer(("127.0.0.1", port), handler)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    return httpd, f"http://127.0.0.1:{port}"


STUB = """
(() => {
  const state = __STATE__;
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage: (msg) => {
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
            page.wait_for_function("document.querySelector('canvas') != null", timeout=20000)
            page.wait_for_timeout(2500)
            # Averaged over frames because rain is stochastic; one frame is not a measurement.
            return sum(mean_luma(page.screenshot()) for _ in range(3)) / 3
        finally:
            page.close()

    for effect, extra in (("palette", ""), ("stripe", "&effect=stripes")):
        base = f"suppressWarnings=true&numColumns=40{extra}"
        dark = luma(f"{base}&glyphIntensity=0")
        bright = luma(f"{base}&glyphIntensity=2")
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


def check_matrix_web(page, origin):
    """The vendored bundle. Asserts it boots and draws, which nothing in CI covered before."""
    errors = []
    page.on("console", lambda m: errors.append(m.text) if m.type == "error" else None)
    page.on("pageerror", lambda e: errors.append(str(e)))

    page.goto(f"{origin}/index.html?suppressWarnings=true&numColumns=40", wait_until="load")
    try:
        page.wait_for_function("document.querySelector('canvas') != null", timeout=20000)
    except Exception:
        fail("matrix web bundle creates a canvas", "no canvas after 20s")
        return

    page.wait_for_timeout(3000)

    if errors:
        fail("matrix web bundle loads without console errors", "; ".join(errors[:3]))
    else:
        ok("matrix web bundle loads without console errors")

    drawn = page.evaluate("""() => {
        const c = document.querySelector('canvas');
        if (!c) return { ok: false, why: 'no canvas' };
        if (c.width < 2 || c.height < 2) return { ok: false, why: `canvas is ${c.width}x${c.height}` };
        return { ok: true, why: `${c.width}x${c.height}` };
    }""")
    if drawn["ok"]:
        ok("matrix web bundle sizes its canvas", drawn["why"])
    else:
        fail("matrix web bundle sizes its canvas", drawn["why"])


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
            page.close()

            print("=== web smoke: matrix bundle ===")
            page = browser.new_page(viewport=VIEWPORT)
            check_matrix_web(page, web_origin)
            page.close()

            print("=== web smoke: flags actually do something ===")
            check_glyph_intensity(browser, web_origin)

            browser.close()
    finally:
        cfg_httpd.shutdown()
        web_httpd.shutdown()

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
