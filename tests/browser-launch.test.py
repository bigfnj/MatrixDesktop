"""The shared Playwright launch (tests/browser_launch.py) merges its disable and verifies it.

All on real headless launches. This test's own probe reads the switch Playwright passes. A
launch through the helper must pass argument verification. With the argv reader made to fail,
the helper must print DEGRADED and launch the suite's arguments unchanged.

The rest runs once for each shape Playwright's own switch can take: its tokens without the
target (Playwright before microsoft/playwright#43605) and with it (from then on). Both shapes
are built from the probe's tokens with the target taken out, so neither depends on which
Playwright this is (MDST-03: the test used to expect the defaults to lack the target, and so
failed a correct helper on a Playwright that passes it), and a stand-in browser type makes
pw.chromium pass that shape's switch. For each shape: the helper's launch passes
verification; a Playwright that already disables the target gets the suite's arguments
untouched (no browser); Playwright's defaults alone are named exactly (the target missing, or
nothing when the shape holds it); and three wrong launches made without the helper, one per
way the merge can break, are each named exactly:
  - one switch without the target: the target is missing
  - the merged switch with one Playwright token dropped (not PaintHolding, the token a
    lazy check would look at): that token is missing
  - Playwright's switch left in place and a second --disable-features appended: two switches
If this test cannot read a browser's argv itself, it is a CANNOT VERIFY (exit 2), never a pass.

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


def switch(tokens):
    return "--disable-features=" + ",".join(tokens)


def launch_with(chromium, real, args, **kwargs):
    """A launch made without the helper whose --disable-features arguments are exactly those in
    args. real holds the switch(es) Playwright itself passes: one that args holds is left for
    Playwright to pass, and the others are ignored. ignore_default_args filters the caller's
    args too, so nothing ignored may also be wanted."""
    return chromium.launch(args=[a for a in args if a not in real],
                           ignore_default_args=[s for s in real if s not in args] or None, **kwargs)


class Passing:
    """pw.chromium standing in for a Playwright whose own --disable-features is `own`, for the
    helper's launch(). As in Playwright, ignore_default_args removes that switch and any caller
    argument equal to one it names."""

    def __init__(self, chromium, real, own):
        self.chromium, self.real, self.own, self.name = chromium, real, own, "chromium passing " + own

    def launch(self, *, args=(), ignore_default_args=None, **kwargs):
        ignored = list(ignore_default_args or [])
        return launch_with(self.chromium, self.real, [a for a in [self.own, *args] if a not in ignored], **kwargs)


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

        without = [t for t in tokens if t != bl.TARGET]
        print(f"SHAPES this Playwright's own switch {'holds' if bl.TARGET in tokens else 'lacks'} the target; "
              "the other shape is the stand-in's alone", flush=True)
        for shape, shape_tokens in (("Playwright without the target", without),
                                    ("Playwright that already disables the target", without + [bl.TARGET])):
            own, holds = switch(shape_tokens), bl.TARGET in shape_tokens
            printed = io.StringIO()
            with contextlib.redirect_stdout(printed):
                try:
                    browser = bl.launch(Passing(pw.chromium, baseline, own), args=GL)
                except bl.LaunchArgumentsError as exc:
                    browser = None
                    print(f"raised: {exc}")
            line = printed.getvalue().strip()
            print(line, flush=True)
            if browser:
                got = bl.verify_arguments(bl.read_browser_argv(browser), shape_tokens)
                browser.close()
            else:
                got = ["the helper raised"]
            check(f"{shape}: the helper's launch passes argument verification",
                  browser is not None and got == [] and "argument verification PASS" in line, f"{got}; printed {line!r}")

            # A Playwright that already disables the target (microsoft/playwright#43605) must get the
            # suite's arguments untouched: ignore_default_args also filters the caller's args, so the
            # identical merged switch would be stripped along with Playwright's and leave none. The
            # switch holds the target once whichever shape it is built from: the target appended to
            # tokens that already hold it is a switch launch_options deduplicates, not that one.
            already = [switch([t for t in shape_tokens if t != bl.TARGET] + [bl.TARGET])]
            untouched = bl.launch_options(already, GL, {})
            check(f"{shape}: a Playwright that already disables the target gets the suite's arguments untouched",
                  untouched == {"args": GL}, f"launch options were {untouched}")

            dropped = next(t for t in shape_tokens[len(shape_tokens) // 2:] + shape_tokens
                           if t not in ("PaintHolding", bl.TARGET))
            controls = [
                ("Playwright defaults alone", [own], [] if holds else [f"{bl.TARGET} is missing"]),
                ("one switch without the target", [switch(without)],
                 [f"{bl.TARGET} is missing"] + ([f"Playwright default token(s) missing: {bl.TARGET}"] if holds else [])),
                (f"merged switch without {dropped}", [switch([t for t in without if t != dropped] + [bl.TARGET])],
                 [f"Playwright default token(s) missing: {dropped}"]),
                ("a second, separate --disable-features", [own, switch([bl.TARGET])],
                 ["2 --disable-features arguments, expected exactly one"]),
            ]
            for label, switches, expected in controls:
                wrong = launch_with(pw.chromium, baseline, GL + switches)
                try:
                    problems = bl.verify_arguments(bl.read_browser_argv(wrong), shape_tokens)
                finally:
                    wrong.close()
                check(f"{shape}, control {label}: verification names exactly {'that defect' if expected else 'nothing'}",
                      problems == expected, f"expected {expected}, got {problems}")
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
