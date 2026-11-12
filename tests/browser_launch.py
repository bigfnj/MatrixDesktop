"""One Playwright Chromium launch for every browser suite under tests/.

Why this exists
---------------
The headless web smoke stalled about one full gate run in five: a module import on the
smoke server's own loopback origin failed with net::ERR_NO_BUFFER_SPACE, the renderer never
loaded, and the readiness wait ran out its 60 s. Chromium on Windows asks for a randomized
local port on each TCP connect (feature TcpPortRandomizationWin, SO_RANDOMIZE_PORT), and that
connect can fail with WSAENOBUFS (10055) while most of the port range is free. Playwright added
the feature to its own disabled list in microsoft/playwright#43605; the 1.62.0 this box runs
does not have it, and CI pins the older 1.60.0. A bounded A/B on this box (commit message)
reproduced the failure only with randomization on.

The disable has to MERGE with Playwright's own --disable-features, never sit beside it.
Playwright emits one --disable-features (16 tokens in 1.62.0) and does not merge a second one
a caller passes. Chromium 151 happens to union duplicates, but nothing promises that, and a
build that keeps only the last switch would silently drop PaintHolding and the rest. So the
helper reads the switch Playwright actually passed (from a probe launch's argv, not a copy of
Playwright's list), launches once more with ONE combined switch and Playwright's own removed
via ignore_default_args, and then checks the browser's argv again.

Argument verification and runtime feature state are reported separately on every launch.
Browser.getBrowserCommandLine would be the natural argv witness, but it answers only with
--enable-automation, which Playwright 1.62 does not pass, so the argv is read from the
browser process itself (pid from SystemInfo.getProcessInfo). SystemInfo.getFeatureState is
not a general query: Chromium 151 answers "Unknown feature" for any name its embedder does
not special-case (TcpPortRandomizationWin and PaintHolding included), so the runtime state is
printed as "not queryable on this build" unless the browser really answers.

A launch whose arguments fail verification raises LaunchArgumentsError, a harness error: the
browser it started is not the configuration any suite was written against, so neither a pass
nor a fail from it is evidence, and the cause (a Playwright or helper change) is
deterministic, so it cannot flake. When the argv cannot be read at all, the launch goes ahead
with the suite's own arguments and prints a DEGRADED line every time.

The native suites (native-uc3d-smoke.py, wallpaper-smoke.py and the scripts built on them)
attach to the real app's WebView2 with connect_over_cdp and must keep testing the product's
own launch, so they do not use this.
"""

import re
import sys

TARGET = "TcpPortRandomizationWin"
_SWITCH = re.compile(r"^(?:--|-|/)disable-features(?:=(.*))?$", re.DOTALL)
_baselines = {}


class LaunchArgumentsError(RuntimeError):
    """The browser did not receive the --disable-features this helper meant to give it."""


def disable_features_switches(argv):
    """Every --disable-features argument in an argv, in order (Chromium also accepts - and /)."""
    return [a for a in argv if _SWITCH.match(a)]


def switch_tokens(switches):
    tokens = []
    for switch in switches:
        value = _SWITCH.match(switch).group(1) or ""
        tokens += [t for t in value.split(",") if t]
    return tokens


def verify_arguments(argv, baseline_tokens, target=TARGET):
    """Problems with a launched browser's argv; empty when it holds exactly one
    --disable-features naming the target and every token Playwright passed."""
    switches = disable_features_switches(argv)
    tokens = set(switch_tokens(switches))
    problems = []
    if len(switches) != 1:
        problems.append(f"{len(switches)} --disable-features arguments, expected exactly one")
    if target not in tokens:
        problems.append(f"{target} is missing")
    missing = [t for t in baseline_tokens if t not in tokens]
    if missing:
        problems.append("Playwright default token(s) missing: " + ", ".join(missing))
    return problems


def _windows_command_line(pid):
    import ctypes
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    ntdll = ctypes.WinDLL("ntdll")
    shell32 = ctypes.WinDLL("shell32", use_last_error=True)
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.LocalFree.argtypes = [ctypes.c_void_p]
    ntdll.NtQueryInformationProcess.restype = ctypes.c_long
    ntdll.NtQueryInformationProcess.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.ULONG,
                                                ctypes.POINTER(wintypes.ULONG)]
    shell32.CommandLineToArgvW.restype = ctypes.POINTER(wintypes.LPWSTR)
    shell32.CommandLineToArgvW.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(ctypes.c_int)]

    class UnicodeString(ctypes.Structure):
        _fields_ = [("Length", wintypes.USHORT), ("MaximumLength", wintypes.USHORT), ("Buffer", ctypes.c_void_p)]

    handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        raise OSError(ctypes.get_last_error(), f"OpenProcess({pid}) failed")
    try:
        size = wintypes.ULONG(0)
        ntdll.NtQueryInformationProcess(handle, 60, None, 0, ctypes.byref(size))  # ProcessCommandLineInformation
        if not size.value:
            raise OSError(f"NtQueryInformationProcess({pid}) reported no command line size")
        buffer = ctypes.create_string_buffer(size.value)
        status = ntdll.NtQueryInformationProcess(handle, 60, buffer, size, ctypes.byref(size))
        if status < 0:
            raise OSError(f"NtQueryInformationProcess({pid}) NTSTATUS 0x{status & 0xFFFFFFFF:08X}")
        text = UnicodeString.from_buffer(buffer)
        line = ctypes.wstring_at(text.Buffer, text.Length // 2)
    finally:
        kernel32.CloseHandle(handle)
    count = ctypes.c_int(0)
    argv = shell32.CommandLineToArgvW(line, ctypes.byref(count))
    if not argv:
        raise OSError(ctypes.get_last_error(), "CommandLineToArgvW failed")
    try:
        return [argv[i] for i in range(count.value)]
    finally:
        kernel32.LocalFree(argv)


def _first_line(exc):
    text = str(exc).strip()
    return text.splitlines()[0] if text else type(exc).__name__


def read_browser_argv(browser):
    """The argv of a launched browser's own process. Raises OSError naming why it cannot be read."""
    try:
        cdp = browser.new_browser_cdp_session()
        try:
            info = cdp.send("SystemInfo.getProcessInfo")["processInfo"]
        finally:
            cdp.detach()
        pids = [p["id"] for p in info if p.get("type") == "browser"]
        if len(pids) != 1:
            raise OSError(f"SystemInfo.getProcessInfo named {len(pids)} browser processes")
        if sys.platform == "win32":
            return _windows_command_line(pids[0])
        if sys.platform.startswith("linux"):
            with open(f"/proc/{pids[0]}/cmdline", "rb") as handle:
                return [a.decode("utf-8", "replace") for a in handle.read().split(b"\0") if a]
        raise OSError(f"no command-line reader for {sys.platform}")
    except OSError:
        raise
    except Exception as exc:  # a refused CDP call, a ctypes failure
        raise OSError(f"{type(exc).__name__}: {_first_line(exc)}") from exc


def runtime_feature_state(browser, feature=TARGET):
    """(enabled, label): what the browser itself says about the feature. enabled is None, never
    False, when it cannot say."""
    try:
        cdp = browser.new_browser_cdp_session()
        try:
            enabled = cdp.send("SystemInfo.getFeatureState", {"featureState": feature})["featureEnabled"]
        finally:
            cdp.detach()
    except Exception as exc:
        reason = _first_line(exc)
        if "Unknown feature" in reason:
            return None, "not queryable on this build (SystemInfo.getFeatureState answers 'Unknown feature')"
        return None, f"not queryable on this build ({reason})"
    return enabled, "enabled (SystemInfo.getFeatureState)" if enabled else "disabled (SystemInfo.getFeatureState)"


def _without_switches(args):
    return [a for a in args if not _SWITCH.match(a)]


def _baseline(browser_type, args, kwargs):
    """The --disable-features switches Playwright itself passes for this launch shape, read
    from a probe browser's argv. Cached per browser type, channel, headless and executable."""
    key = (browser_type.name, kwargs.get("channel"), kwargs.get("headless"), kwargs.get("executable_path"))
    if key not in _baselines:
        probe = browser_type.launch(args=_without_switches(args), **kwargs)
        try:
            _baselines[key] = disable_features_switches(read_browser_argv(probe))
        finally:
            probe.close()
    return _baselines[key]


def launch_options(baseline, args, kwargs, target=TARGET):
    """Arguments for the real launch: ONE --disable-features holding Playwright's tokens, the
    suite's own, and the target; Playwright's switch removed so it is not passed twice."""
    tokens = []
    for token in switch_tokens(baseline) + switch_tokens(disable_features_switches(args)) + [target]:
        if token not in tokens:
            tokens.append(token)
    options = dict(kwargs)
    if len(baseline) == 1 and tokens == switch_tokens(baseline):
        # Playwright already disables the target (it does from microsoft/playwright#43605 on).
        # Passing the identical switch would not help: ignore_default_args filters the caller's
        # args too, so it would strip both copies and leave none.
        options["args"] = list(args)
        return options
    ignored = options.pop("ignore_default_args", None)
    if ignored is True:
        raise ValueError("ignore_default_args=True leaves no Playwright defaults to merge with")
    options["ignore_default_args"] = list(ignored or []) + [s for s in baseline if s not in (ignored or [])]
    options["args"] = _without_switches(args) + ["--disable-features=" + ",".join(tokens)]
    return options


def launch(browser_type, *, args=(), **kwargs):
    """browser_type.launch(args=args, **kwargs) with TcpPortRandomizationWin disabled and
    Playwright's own disabled features kept, verified on the launched browser's argv."""
    args = list(args)
    label = f"browser launch ({kwargs.get('channel') or 'default channel'})"
    try:
        baseline = _baseline(browser_type, args, kwargs)
    except OSError as exc:
        print(f"DEGRADED {label}: argument verification unavailable ({exc}); {TARGET} NOT disabled, "
              "because the switch Playwright passes could not be read to merge with", flush=True)
        return browser_type.launch(args=args, **kwargs)
    browser = browser_type.launch(**launch_options(baseline, args, kwargs))
    label += " " + browser.version
    try:
        argv = read_browser_argv(browser)
    except OSError as exc:
        print(f"DEGRADED {label}: argument verification unavailable ({exc}); "
              f"{TARGET} was requested but the browser's argv could not be read back", flush=True)
        return browser
    baseline_tokens = switch_tokens(baseline)
    problems = verify_arguments(argv, baseline_tokens)
    enabled, state = runtime_feature_state(browser)
    if enabled:
        problems.append(f"the browser reports {TARGET} {state}")
    if problems:
        browser.close()
        raise LaunchArgumentsError(f"{label}: argument verification FAILED: " + "; ".join(problems))
    print(f"{label}: argument verification PASS (one --disable-features with {TARGET} and all "
          f"{len(baseline_tokens)} tokens Playwright passed); runtime feature state: {state}", flush=True)
    return browser
