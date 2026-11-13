"""web-smoke.py's verdict when a check cannot run in this environment (R3-16).

check_no_favicon_request needs the full Chromium build, because the headless shell never
requests a favicon. A browsers folder that holds only the shell ('playwright install
--only-shell chromium') runs every other check. A missing full build is therefore an
environment gap, reported like a missing chromium (R2-35): an UNVERIFIED line at the start of a
stderr line and exit 2, which the gate reads as UNCHECKED. It must never be a product FAIL
(exit 1). No browser starts here: the launch is a stand-in that raises the way Playwright does
for a channel whose executable is not installed.

EntryPointTests (MDR3-01) run web-smoke.py's own entry block over its real main() with the
launches as stand-ins: a FAIL found before the run stopped, on a typed launch refusal or on a
chromium that cannot start, stays the verdict, exit 1. Two of them write to stdout the way
Python on a cp1252 Windows runner opens the gate's redirected log, with an exception message
that code page cannot encode: the harness error is still exit 3, and still exit 1 after a FAIL.

Exit codes: 0 pass, 1 fail (unittest).
"""
import ast
import contextlib
import functools
import importlib.util
import io
from pathlib import Path
import sys
import tempfile
import types
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))  # web-smoke.py imports browser_launch beside it
import browser_launch  # noqa: E402

SOURCE = Path(__file__).with_name("web-smoke.py")
spec = importlib.util.spec_from_file_location("web_smoke", SOURCE)
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


class StandInBrowser:
    """The headless shell main() launched: pages that do nothing, for checks that are stand-ins."""
    browser_type = "the headless shell's browser type"

    def new_page(self, **kwargs):
        return StandInBrowser()

    def close(self):
        pass


def refuse_the_full_build(browser_type, **kwargs):
    """main()'s launch starts; the favicon check's full-build launch fails argument verification."""
    if kwargs.get("channel") == "chromium":
        raise browser_launch.LaunchArgumentsError("browser launch (chromium) 151.0: argument verification FAILED: stub refusal")
    return StandInBrowser()


def no_chromium(browser_type, **kwargs):
    raise MissingExecutable("BrowserType.launch: Executable doesn't exist at stub")


def unverified_chromium(err):
    return [line for line in err.splitlines() if line.startswith("UNVERIFIED: chromium is unavailable: ")]


def a_working_launch(browser_type, **kwargs):
    return StandInBrowser()


# A character the cp1252 code page cannot encode (KATAKANA LETTER A), and the text a
# backslashreplace stream writes for it.
UNENCODABLE = chr(0x30A2)
ESCAPED = UNENCODABLE.encode("ascii", "backslashreplace").decode("ascii")


def a_late_exception(namespace, *args):
    """A check late in main() raises, with a message that cp1252 cannot encode."""
    raise RuntimeError(f"a late check broke on {UNENCODABLE}")


def cp1252_streams():
    """stdout and stderr as CPython on Windows opens them for the gate's child on a cp1252 runner,
    both redirected to log files with neither PYTHONIOENCODING nor PYTHONUTF8 set (CI's setup):
    the code page with surrogateescape for stdout, which raises on any character that page lacks
    other than a lone surrogate, and with backslashreplace for stderr."""
    return (io.TextIOWrapper(io.BytesIO(), encoding="cp1252", errors="surrogateescape"),
            io.TextIOWrapper(io.BytesIO(), encoding="cp1252", errors="backslashreplace"))


def written(stream):
    """What a stream holds: a StringIO's text, or the bytes a TextIOWrapper wrote, decoded."""
    if isinstance(stream, io.StringIO):
        return stream.getvalue()
    stream.flush()
    return stream.buffer.getvalue().decode(stream.encoding)


def run_entry_point(launch, stand_ins, streams=None):
    """web-smoke.py's own `if __name__ == "__main__":` block, the code the gate's child process
    ends in, run over the real main(): (exit code, stdout, stderr, the launch calls' channels).

    The module is executed fresh without that block, so its result lists start empty; then
    browser_launch.launch is replaced by `launch`, every check_* function main() reaches becomes a
    no-op except check_startup_paths and check_no_favicon_request (the favicon launch's own path)
    and the ones `stand_ins` names, and REPO points at a stub loadState fixture. No browser or
    Playwright driver starts: sync_playwright() is a stand-in too. `streams` is the (stdout,
    stderr) pair the block writes to, two StringIOs when not given. An exception the block lets
    out, where the child would exit 1 with a traceback, comes back as the code
    'raised <type>: <message>'.
    """
    tree = ast.parse(SOURCE.read_text(encoding="utf-8"), str(SOURCE))
    entry = tree.body[-1]
    if not (isinstance(entry, ast.If) and ast.unparse(entry.test) == "__name__ == '__main__'"):
        raise AssertionError("web-smoke.py no longer ends in its `if __name__ == \"__main__\":` block")
    namespace = {"__name__": "web_smoke_entry", "__file__": str(SOURCE)}
    exec(compile(ast.Module(body=tree.body[:-1], type_ignores=[]), str(SOURCE), "exec"), namespace)
    for name in [name for name in namespace if name.startswith("check_")]:
        if name not in ("check_startup_paths", "check_no_favicon_request"):
            namespace[name] = lambda *args, **kwargs: None
    for name, stand_in in stand_ins.items():
        namespace[name] = functools.partial(stand_in, namespace)
    channels = []

    def recorded(browser_type, **kwargs):
        channels.append(kwargs.get("channel"))
        return launch(browser_type, **kwargs)

    @contextlib.contextmanager
    def no_driver():  # main()'s sync_playwright(): the launches above never reach a driver
        yield types.SimpleNamespace(chromium="the chromium browser type")

    from playwright import sync_api
    out, err = streams or (io.StringIO(), io.StringIO())
    saved = (browser_launch.launch, sync_api.sync_playwright)
    with tempfile.TemporaryDirectory() as root:
        (Path(root) / "artifacts" / "gate").mkdir(parents=True)
        (Path(root) / "artifacts" / "gate" / "loadState.json").write_text("{}", encoding="utf-8")
        namespace["REPO"] = Path(root)
        browser_launch.launch, sync_api.sync_playwright = recorded, no_driver
        try:
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                namespace["__name__"] = "__main__"
                exec(compile(ast.Module(body=[entry], type_ignores=[]), str(SOURCE), "exec"), namespace)
            code = "no sys.exit"
        except SystemExit as stop:
            code = stop.code
        except Exception as exc:
            code = f"raised {type(exc).__name__}: {ascii(str(exc))}"
        finally:
            browser_launch.launch, sync_api.sync_playwright = saved
    return code, written(out), written(err), channels


def a_product_fail(namespace, *args):
    namespace["fail"]("a product check", "it broke")


class EntryPointTests(unittest.TestCase):
    """A FAIL the run found before it stopped stays the verdict, on every way main() ends (MDR3-01).

    The gate reads the child's exit code: 1 is a FAIL, 3 a harness error, 2 cannot verify. An
    exit 3 or 2 after a product FAIL hid that FAIL behind a harness error or an environment gap."""

    def test_a_typed_launch_refusal_after_a_fail_is_exit_1_naming_both(self):
        # Killing mutation: the entry block back to an unconditional sys.exit(3) on any exception.
        code, out, err, channels = run_entry_point(refuse_the_full_build, {"check_configurator": a_product_fail})
        print(f"WITNESS launch channels {channels}; exit {code}", flush=True)
        self.assertEqual(channels, [None, "chromium"], "main()'s launch, then the favicon check's full build")
        self.assertEqual(code, 1, f"a FAIL before the refusal is the verdict:\n{out}\n{err}")
        self.assertIn("  FAIL  a product check: it broke", out)
        harness = [line for line in out.splitlines() if line.startswith("  HARNESS  ")]
        self.assertEqual(len(harness), 1, out)
        for part in ("LaunchArgumentsError", "stub refusal"):
            self.assertIn(part, harness[0])
            self.assertIn(part, err)
        self.assertIn("WEB SMOKE FAILED: 1 of 1 checks, and 1 harness error(s)", out)

    def test_a_typed_launch_refusal_alone_stays_the_harness_error_exit_3(self):
        # Control for the test above: with no FAIL the refusal is a harness error, never a pass or a FAIL.
        code, out, err, channels = run_entry_point(refuse_the_full_build, {})
        self.assertEqual(channels, [None, "chromium"])
        self.assertEqual(code, 3, f"{out}\n{err}")
        self.assertNotIn("  FAIL  ", out)
        self.assertIn("stub refusal", out + err, "the harness error names its cause")

    def test_chromium_unavailable_after_a_failing_harness_control_is_exit_1(self):
        # Killing mutation: main()'s chromium-unavailable branch back to `return 2`.
        code, out, err, channels = run_entry_point(no_chromium, {"check_exit_mapping": a_product_fail})
        print(f"WITNESS launch channels {channels}; exit {code}", flush=True)
        self.assertEqual(channels, [None], "main()'s one launch")
        self.assertEqual(code, 1, f"a FAIL before chromium was found missing is the verdict:\n{out}\n{err}")
        self.assertIn("  FAIL  a product check: it broke", out)
        self.assertEqual(len(unverified_chromium(err)), 1, err)

    def test_chromium_unavailable_alone_stays_the_cannot_verify_exit_2(self):
        # Control for the test above (R2-35): with nothing failed, a missing chromium is exit 2 with its witness.
        code, out, err, channels = run_entry_point(no_chromium, {})
        self.assertEqual(code, 2, f"{out}\n{err}")
        self.assertNotIn("  FAIL  ", out)
        self.assertEqual(len(unverified_chromium(err)), 1, err)

    def test_an_exception_cp1252_cannot_encode_alone_is_exit_3_naming_it(self):
        # Killing mutation: the entry block without its stdout reconfigure. harness() then raises
        # UnicodeEncodeError inside the except, and the child exits 1 with no FAIL line.
        streams = cp1252_streams()
        code, out, err, channels = run_entry_point(a_working_launch, {"check_glyph_intensity": a_late_exception}, streams)
        print(f"WITNESS stdout errors {streams[0].errors}; exit {code}", flush=True)
        self.assertEqual(channels, [None], "main()'s one launch, then the late check")
        self.assertEqual(code, 3, f"a harness error alone is exit 3 whatever stdout can encode:\n{out}\n{err}")
        self.assertNotIn("  FAIL  ", out)
        harness = [line for line in out.splitlines() if line.startswith("  HARNESS  ")]
        self.assertEqual(len(harness), 1, out)
        self.assertIn(f"the web smoke runs to its end: stopped by RuntimeError: a late check broke on {ESCAPED}", harness[0])
        self.assertIn(f"harness error: the web smoke runs to its end: stopped by RuntimeError: a late check broke on {ESCAPED}", err)
        self.assertIn("WEB SMOKE HARNESS ERROR: 1 check(s) could not run; 0 passed", out)

    def test_an_exception_cp1252_cannot_encode_after_a_fail_is_exit_1_naming_both(self):
        # Killing mutation: the same as the test above.
        streams = cp1252_streams()
        code, out, err, channels = run_entry_point(
            a_working_launch, {"check_configurator": a_product_fail, "check_glyph_intensity": a_late_exception}, streams)
        print(f"WITNESS stdout errors {streams[0].errors}; exit {code}", flush=True)
        self.assertEqual(channels, [None], "main()'s one launch, then the late check")
        self.assertEqual(code, 1, f"a FAIL before the exception is the verdict whatever stdout can encode:\n{out}\n{err}")
        self.assertIn("  FAIL  a product check: it broke", out)
        harness = [line for line in out.splitlines() if line.startswith("  HARNESS  ")]
        self.assertEqual(len(harness), 1, out)
        self.assertIn(f"the web smoke runs to its end: stopped by RuntimeError: a late check broke on {ESCAPED}", harness[0])
        self.assertIn("WEB SMOKE FAILED: 1 of 1 checks, and 1 harness error(s)", out)


if __name__ == "__main__":
    unittest.main(verbosity=2)
