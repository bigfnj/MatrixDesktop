"""web-smoke.py's verdict when a check cannot run in this environment (R3-16).

check_no_favicon_request needs the full Chromium build, because the headless shell never
requests a favicon. A browsers folder that holds only the shell ('playwright install
--only-shell chromium') runs every other check. A missing full build is therefore an
environment gap, reported like a missing chromium (R2-35): an UNVERIFIED line at the start of a
stderr line and exit 2, which the gate reads as UNCHECKED. It must never be a product FAIL
(exit 1). No browser starts here: the launch is a stand-in that raises the way Playwright does
for a channel whose executable is not installed.

Exit codes: 0 pass, 1 fail (unittest).
"""
import contextlib
import importlib.util
import io
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))  # web-smoke.py imports browser_launch beside it
spec = importlib.util.spec_from_file_location("web_smoke", Path(__file__).with_name("web-smoke.py"))
web_smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(web_smoke)

FAVICON_CHECK = "index.html makes no /favicon.ico request"
LISTS = ("failures", "passes", "harness_errors", "unverified")


class MissingExecutable(Exception):
    """Stands in for Playwright's error for an executable that is not installed."""


class ShellOnlyBrowserType:
    """A browsers folder with only the headless shell: the full build's channel cannot start."""
    name = "chromium"

    def __init__(self):
        self.launched = []

    def launch(self, **kwargs):
        self.launched.append(kwargs.get("channel"))
        if kwargs.get("channel") == "chromium":
            raise MissingExecutable("BrowserType.launch: Executable doesn't exist at "
                                    "ms-playwright\\chromium-1234\\chrome-win\\chrome.exe")
        raise AssertionError(f"this stand-in only expects the full build's channel, got {kwargs}")


class ShellOnlyBrowser:
    def __init__(self):
        self.browser_type = ShellOnlyBrowserType()


class VerdictTests(unittest.TestCase):
    def setUp(self):
        # Fresh result lists for each test; web-smoke.py's functions read the module globals.
        self.saved = {name: getattr(web_smoke, name) for name in LISTS}
        for name in LISTS:
            setattr(web_smoke, name, [])

    def tearDown(self):
        for name, value in self.saved.items():
            setattr(web_smoke, name, value)

    def test_a_missing_full_chromium_build_is_unverified_and_exit_2_not_a_fail(self):
        # Killing mutations: fail() back in check_no_favicon_request's except branch (the
        # check lands in failures and the verdict is 1); 'if missing: return 2' dropped from
        # exit_code (the verdict is 0, a pass that never ran the check).
        browser = ShellOnlyBrowser()
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            web_smoke.check_no_favicon_request(browser, "http://127.0.0.1:9")
            code = web_smoke.final_verdict()
        print(f"WITNESS launched channels {browser.browser_type.launched}; verdict {code}", flush=True)
        self.assertEqual(browser.browser_type.launched, ["chromium"], "the check asked for the full build once")
        self.assertEqual(web_smoke.failures, [], f"no product FAIL: {out.getvalue()}")
        self.assertEqual(web_smoke.unverified, [FAVICON_CHECK])
        witness = [line for line in err.getvalue().splitlines() if line.startswith("UNVERIFIED: ")]
        self.assertEqual(len(witness), 1, f"one UNVERIFIED line at the start of a stderr line: {err.getvalue()!r}")
        for part in ("the full Chromium build is unavailable", FAVICON_CHECK, "MissingExecutable",
                     "python -m playwright install chromium"):
            self.assertIn(part, witness[0])
        self.assertEqual(code, 2, out.getvalue())
        self.assertIn(f"WEB SMOKE UNVERIFIED: 1 check(s) could not run: {FAVICON_CHECK}; 0 passed", out.getvalue())

    def test_a_fail_or_a_harness_error_outranks_a_check_that_could_not_run(self):
        # Killing mutation: the 'missing' test moved ahead of 'failed' or 'broken' in exit_code.
        unverified, failed, broken = [FAVICON_CHECK], [("a check", "a FAIL")], [("a check", "a harness error")]
        rows = (([], [], [], 0), ([], [], unverified, 2), (failed, [], unverified, 1), ([], broken, unverified, 3),
                (failed, broken, unverified, 1))
        wrong = [f"{len(f)} FAIL(s), {len(b)} harness error(s), {len(u)} unverified gave {web_smoke.exit_code(f, b, u)}, expected {want}"
                 for f, b, u, want in rows if web_smoke.exit_code(f, b, u) != want]
        self.assertEqual(wrong, [])


if __name__ == "__main__":
    unittest.main(verbosity=2)
