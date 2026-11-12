"""The shared Playwright launch (tests/browser_launch.py) merges its disable and verifies it.

All on real headless launches. This test's own probe reads the switch Playwright passes. A
launch through the helper must pass argument verification. Three deliberately wrong launches
made without the helper, one per way the merge can break, must each be named exactly:
  - Playwright's defaults alone: the target token is missing
  - the merged switch with one Playwright token dropped (not PaintHolding, the token a
    lazy check would look at): that token is missing
  - Playwright's switch left in place and a second --disable-features appended: two switches
With the argv reader made to fail, the helper must print DEGRADED and launch the suite's
arguments unchanged. One check needs no browser: a Playwright that already disables the target
gets the suite's arguments untouched. If this test cannot read a browser's argv itself, it is a
CANNOT VERIFY (exit 2), never a pass.

Exit codes: 0 pass, 1 fail, 2 cannot verify, 3 harness error.
"""

import contextlib
import hashlib
import io
import sys
from pathlib import Path

import browser_launch as bl

GL = ["--use-gl=angle", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"]
failures = []
passes = []


def check(name, condition, detail):
    print(f"{'PASS' if condition else 'FAIL'} {name}" + ("" if condition else f": {detail}"), flush=True)
    (passes if condition else failures).append(name)


def main():
    helper = Path(bl.__file__).resolve()
    print(f"HELPER {helper} sha256={hashlib.sha256(helper.read_bytes()).hexdigest()}", flush=True)
    try:
        from playwright.sync_api import sync_playwright
    except ImportError as exc:
        print(f"UNVERIFIED: playwright is missing for this interpreter: {exc}")
        return 2
    with sync_playwright() as pw:
        try:
            probe = pw.chromium.launch(args=GL)
        except Exception as exc:
            print(f"UNVERIFIED: Chromium is unavailable: {bl._first_line(exc)}")
            return 2
        try:
            argv = bl.read_browser_argv(probe)
        except OSError as exc:
            print(f"CANNOT VERIFY browser launch arguments: the browser's argv cannot be read ({exc})")
            return 2
        finally:
            probe.close()
        baseline = bl.disable_features_switches(argv)
        tokens = bl.switch_tokens(baseline)
        print(f"BASELINE {len(baseline)} switch(es), {len(tokens)} token(s): {','.join(tokens)}", flush=True)
        check("Playwright passes its own --disable-features, so there is something to keep",
              len(baseline) == 1 and "PaintHolding" in tokens and len(tokens) > 2, f"argv held {baseline}")

        printed = io.StringIO()
        with contextlib.redirect_stdout(printed):
            try:
                browser = bl.launch(pw.chromium, args=GL)
            except bl.LaunchArgumentsError as exc:
                browser = None
                print(f"raised: {exc}")
        line = printed.getvalue().strip()
        print(line, flush=True)
        if browser:
            got = bl.verify_arguments(bl.read_browser_argv(browser), tokens)
            browser.close()
        else:
            got = ["the helper raised"]
        check("the helper's launch passes argument verification", browser is not None and got == [] and
              "argument verification PASS" in line and "runtime feature state: " in line, f"{got}; printed {line!r}")

        dropped = next((t for t in tokens[len(tokens) // 2:] + tokens if t != "PaintHolding"), None)
        controls = [
            ("Playwright defaults alone", dict(args=GL), [f"{bl.TARGET} is missing"]),
            (f"merged switch without {dropped}",
             dict(args=GL + ["--disable-features=" + ",".join([t for t in tokens if t != dropped] + [bl.TARGET])],
                  ignore_default_args=baseline),
             [f"Playwright default token(s) missing: {dropped}"]),
            ("a second, separate --disable-features",
             dict(args=GL + ["--disable-features=" + bl.TARGET]),
             ["2 --disable-features arguments, expected exactly one"]),
        ]
        # A Playwright that already disables the target (microsoft/playwright#43605) must get the
        # suite's arguments untouched: ignore_default_args also filters the caller's args, so the
        # identical merged switch would be stripped along with Playwright's and leave none.
        already = [baseline[0] + "," + bl.TARGET] if baseline else []
        untouched = bl.launch_options(already, GL, {})
        check("a Playwright that already disables the target gets the suite's arguments untouched",
              untouched == {"args": GL}, f"launch options were {untouched}")

        # An argv that cannot be read is a degraded launch, and it must say so: the real reader is
        # swapped for one that fails, so the probe cannot learn Playwright's switch.
        def unreadable(browser):
            raise OSError("synthetic unreadable argv")

        real_reader = bl.read_browser_argv
        bl._baselines.clear()
        printed = io.StringIO()
        try:
            bl.read_browser_argv = unreadable
            with contextlib.redirect_stdout(printed):
                degraded = bl.launch(pw.chromium, args=GL)
        finally:
            bl.read_browser_argv = real_reader
            bl._baselines.clear()
        try:
            degraded_switches = bl.disable_features_switches(real_reader(degraded))
        finally:
            degraded.close()
        said = printed.getvalue().strip()
        print(said, flush=True)
        check("an unreadable argv prints DEGRADED and leaves the suite's launch as it was",
              said.startswith("DEGRADED ") and "synthetic unreadable argv" in said and "NOT disabled" in said
              and degraded_switches == baseline, f"printed {said!r}; switches {degraded_switches}")

        for label, options, expected in controls:
            wrong = pw.chromium.launch(**options)
            try:
                problems = bl.verify_arguments(bl.read_browser_argv(wrong), tokens)
            finally:
                wrong.close()
            check(f"negative control, {label}: verification names exactly that defect", problems == expected,
                  f"expected {expected}, got {problems}")
    if failures:
        print(f"BROWSER LAUNCH SELF-TEST FAILED: {len(failures)} check(s)")
        return 1
    print(f"BROWSER LAUNCH SELF-TEST PASSED ({len(passes)} checks)")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:
        print(f"HARNESS ERROR: {type(exc).__name__}: {exc}", file=sys.stderr)
        sys.exit(3)
