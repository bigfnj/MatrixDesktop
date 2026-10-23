"""Verify real .scr child-preview lifetime and full-screen input exit.

Creates an owned synthetic parent window. Never registers a screensaver, changes
the saved profile, or sends input to another application.
"""
import argparse
import ctypes
from ctypes import wintypes
import importlib.util
import io
import json
from pathlib import Path
import threading
import time

from PIL import Image, ImageStat
from playwright.sync_api import sync_playwright
from screensaver_focus import exit_input_mode, input_availability_reason, wait_for_owned_foreground

ROOT = Path(__file__).resolve().parent.parent
SPEC = importlib.util.spec_from_file_location("native_smoke", ROOT / "tests" / "native-uc3d-smoke.py")
NATIVE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(NATIVE)
U = ctypes.WinDLL("user32", use_last_error=True)
K = ctypes.WinDLL("kernel32", use_last_error=True)
WTS = ctypes.WinDLL("wtsapi32", use_last_error=True)
K.ProcessIdToSessionId.argtypes = [wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
K.WTSGetActiveConsoleSessionId.restype = wintypes.DWORD
U.GetThreadDesktop.argtypes = [wintypes.DWORD]
U.GetThreadDesktop.restype = wintypes.HANDLE
U.OpenInputDesktop.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
U.OpenInputDesktop.restype = wintypes.HANDLE
U.CloseDesktop.argtypes = [wintypes.HANDLE]
U.GetUserObjectInformationW.argtypes = [wintypes.HANDLE, ctypes.c_int, wintypes.LPVOID,
                                      wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
WTS.WTSQuerySessionInformationW.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.c_int,
                                           ctypes.POINTER(wintypes.LPVOID), ctypes.POINTER(wintypes.DWORD)]
WTS.WTSFreeMemory.argtypes = [wintypes.LPVOID]
U.CreateWindowExW.argtypes = [wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD,
                            ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                            wintypes.HWND, wintypes.HMENU, wintypes.HINSTANCE, wintypes.LPVOID]
U.CreateWindowExW.restype = wintypes.HWND
U.GetParent.argtypes = [wintypes.HWND]
U.GetParent.restype = wintypes.HWND
U.GetClientRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
U.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
U.MoveWindow.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.BOOL]
U.DestroyWindow.argtypes = [wintypes.HWND]
U.GetForegroundWindow.restype = wintypes.HWND
U.EnumChildWindows.argtypes = [wintypes.HWND, NATIVE.CALLBACK, wintypes.LPARAM]
U.EnumWindows.argtypes = [NATIVE.CALLBACK, wintypes.LPARAM]
U.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
U.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
U.IsWindowVisible.argtypes = [wintypes.HWND]
U.GetAncestor.argtypes = [wintypes.HWND, wintypes.UINT]
U.GetAncestor.restype = wintypes.HWND
U.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
U.GetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int]
U.GetWindowLongPtrW.restype = ctypes.c_ssize_t
U.WindowFromPoint.argtypes = [wintypes.POINT]
U.WindowFromPoint.restype = wintypes.HWND
U.GetCursorPos.argtypes = [ctypes.POINTER(wintypes.POINT)]
U.SendMessageTimeoutW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM,
                                wintypes.UINT, wintypes.UINT, ctypes.POINTER(ctypes.c_size_t)]
U.SendMessageTimeoutW.restype = wintypes.LPARAM


class LastInput(ctypes.Structure):
    _fields_ = [("size", wintypes.UINT), ("tick", wintypes.DWORD)]


U.GetLastInputInfo.argtypes = [ctypes.POINTER(LastInput)]


def last_input_tick():
    value = LastInput(ctypes.sizeof(LastInput), 0)
    assert U.GetLastInputInfo(ctypes.byref(value))
    return value.tick


def input_availability_snapshot():
    def desktop_name(handle):
        if not handle:
            return None
        text, needed = ctypes.create_unicode_buffer(256), wintypes.DWORD()
        return text.value if U.GetUserObjectInformationW(handle, 2, text, ctypes.sizeof(text), ctypes.byref(needed)) else None
    session = wintypes.DWORD()
    assert K.ProcessIdToSessionId(K.GetCurrentProcessId(), ctypes.byref(session))
    buffer, size = wintypes.LPVOID(), wintypes.DWORD()
    state = None
    if WTS.WTSQuerySessionInformationW(None, session.value, 8, ctypes.byref(buffer), ctypes.byref(size)):
        try:
            raw = ctypes.cast(buffer, ctypes.POINTER(wintypes.DWORD)).contents.value
            state = {0: "Active", 4: "Disconnected"}.get(raw, str(raw))
        finally:
            WTS.WTSFreeMemory(buffer)
    desktop = U.OpenInputDesktop(0, False, 1)
    error = ctypes.get_last_error() if not desktop else 0
    try:
        return {"processSessionId": session.value, "activeConsoleSessionId": K.WTSGetActiveConsoleSessionId(),
                "sessionState": state, "threadDesktop": desktop_name(U.GetThreadDesktop(K.GetCurrentThreadId())),
                "inputDesktop": desktop_name(desktop), "inputDesktopOpenError": error}
    finally:
        if desktop:
            U.CloseDesktop(desktop)


class MouseInput(ctypes.Structure):
    _fields_ = [("dx", wintypes.LONG), ("dy", wintypes.LONG), ("data", wintypes.DWORD),
                ("flags", wintypes.DWORD), ("time", wintypes.DWORD), ("extra", wintypes.WPARAM)]


class KeyboardInput(ctypes.Structure):
    _fields_ = [("key", wintypes.WORD), ("scan", wintypes.WORD), ("flags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("extra", wintypes.WPARAM)]


class InputValue(ctypes.Union):
    _fields_ = [("mouse", MouseInput), ("keyboard", KeyboardInput)]


class Input(ctypes.Structure):
    _fields_ = [("type", wintypes.DWORD), ("value", InputValue)]


U.SendInput.argtypes = [wintypes.UINT, ctypes.POINTER(Input), ctypes.c_int]


def find_window(pid, parent=None):
    found = []
    def visit(hwnd, _):
        value = wintypes.DWORD()
        U.GetWindowThreadProcessId(hwnd, ctypes.byref(value))
        if value.value == pid and U.IsWindowVisible(hwnd) and (not parent or U.GetParent(hwnd) == parent):
            found.append(hwnd)
        return True
    if parent:
        U.EnumChildWindows(parent, NATIVE.CALLBACK(visit), 0)
    else:
        U.EnumWindows(NATIVE.CALLBACK(visit), 0)
    return found[0] if found else None


def rect(hwnd, client=False):
    value = wintypes.RECT()
    assert (U.GetClientRect if client else U.GetWindowRect)(hwnd, ctypes.byref(value))
    return (value.left, value.top, value.right, value.bottom)


def foreground_snapshot(window):
    foreground = U.GetForegroundWindow()
    root = U.GetAncestor(foreground, 2)
    def metadata(handle):
        pid = wintypes.DWORD()
        U.GetWindowThreadProcessId(handle, ctypes.byref(pid))
        name = ctypes.create_unicode_buffer(256)
        U.GetClassNameW(handle, name, len(name))
        return pid.value, name.value
    foreground_pid, foreground_class = metadata(foreground)
    root_pid, root_class = metadata(root)
    window_pid, window_class = metadata(window)
    return {"foreground": int(foreground or 0), "foregroundRoot": int(root or 0),
            "foregroundPid": foreground_pid, "rootPid": root_pid, "foregroundClass": foreground_class,
            "rootClass": root_class, "window": int(window), "windowPid": window_pid, "windowClass": window_class}


def owned_mouse_point(window):
    cursor = wintypes.POINT()
    assert U.GetCursorPos(ctypes.byref(cursor))
    width, height = U.GetSystemMetrics(0), U.GetSystemMetrics(1)
    x, y = width // 2, height // 2
    if (x, y) == (cursor.x, cursor.y):
        x = min(width - 1, x + 17)
    hit = U.WindowFromPoint(wintypes.POINT(x, y))
    root = U.GetAncestor(hit, 2)
    root_pid, window_pid = wintypes.DWORD(), wintypes.DWORD()
    U.GetWindowThreadProcessId(root, ctypes.byref(root_pid))
    U.GetWindowThreadProcessId(window, ctypes.byref(window_pid))
    return {"x": x, "y": y, "cursorX": cursor.x, "cursorY": cursor.y,
            "left": 0, "top": 0, "right": width, "bottom": height,
            "hit": int(hit or 0), "hitRoot": int(root or 0), "rootPid": root_pid.value,
            "windowPid": window_pid.value}


class PreviewParent:
    def __init__(self):
        self.handle = None
        self.error = None
        self.ready = threading.Event()
        self.closed = threading.Event()
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.thread.start()
        assert self.ready.wait(5), "synthetic preview parent did not start"
        if self.error:
            raise self.error

    def run(self):
        try:
            self.handle = U.CreateWindowExW(0x08000000, "STATIC", "MatrixDesktop synthetic screensaver preview",
                                            0x10CF0000, 40, 40, 480, 320, None, None, None, None)
            assert self.handle, ctypes.WinError(ctypes.get_last_error())
            self.ready.set()
            message = wintypes.MSG()
            while not self.closed.is_set():
                while U.PeekMessageW(ctypes.byref(message), None, 0, 0, 1):
                    U.TranslateMessage(ctypes.byref(message))
                    U.DispatchMessageW(ctypes.byref(message))
                time.sleep(.01)
        except BaseException as error:
            self.error = error
            self.ready.set()
        finally:
            if self.handle:
                U.DestroyWindow(self.handle)

    def close(self):
        self.closed.set()
        self.thread.join(timeout=5)
        assert not self.thread.is_alive(), "synthetic preview parent did not exit"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--scr", required=True, type=Path)
    args = parser.parse_args()
    assert args.scr.exists(), f"screensaver payload missing: {args.scr}"
    # Match the application's physical-coordinate interpretation.
    U.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    output = ROOT / "artifacts" / "screensaver-smoke"
    output.mkdir(parents=True, exist_ok=True)
    profile = output / "synthetic-profile.json"
    profile.write_text(json.dumps(["--wallpaper", "--working-area", "true", "--topmost", "false", "--scene", "matrix", "--suppressWarnings", "true"]), encoding="utf-8")
    test_environment = {"MATRIXDESKTOP_SCREENSAVER_PROFILE": str(profile)}
    reports = []
    with sync_playwright() as playwright:
        parent = PreviewParent()
        process = browser = None
        try:
            foreground = U.GetForegroundWindow()
            process, browser = NATIVE.open_application(playwright, args.scr, ["/p", str(parent.handle)], "scr-preview", test_environment)
            page = NATIVE.page_at(browser, "https://appassets.local/")
            page.wait_for_function("window.matrixRuntimeDiagnostics?.ready === true && window.matrixRuntimeDiagnostics.frames > 0", timeout=20000)
            page.wait_for_timeout(1500)
            child = find_window(process.pid, parent.handle)
            assert child and U.GetParent(child) == parent.handle, "preview did not become a child of the supplied parent"
            preview_ex_style = U.GetWindowLongPtrW(child, -20)
            assert preview_ex_style & 0x08000000, "preview is missing its no-activation window style"
            assert not preview_ex_style & 8, "preview inherited the screensaver's forced topmost behavior"
            assert U.GetForegroundWindow() == foreground, "preview stole foreground focus"
            parent_rect, child_rect = rect(parent.handle, True), rect(child, True)
            assert parent_rect == child_rect, (parent_rect, child_rect)
            image = Image.open(io.BytesIO(page.locator("canvas").first.screenshot())).convert("RGB")
            assert max(ImageStat.Stat(image).stddev) > 3, "preview rendered a uniform frame"
            image.save(output / "preview.png")
            assert U.MoveWindow(parent.handle, 40, 40, 680, 420, True)
            deadline = time.monotonic() + 5
            while time.monotonic() < deadline and rect(parent.handle, True) != rect(child, True):
                page.wait_for_timeout(100)
            assert rect(parent.handle, True) == rect(child, True), "preview did not follow parent resize"
            parent.close()
            process.wait(timeout=8)
            assert process.returncode == 0, process.returncode
            reports.append({"mode": "preview", "checks": ["child-parent", "bounds", "no-activate", "non-topmost", "focus", "pixels", "resize", "parent-lifetime"]})
            print("PASS screensaver /p: parent, bounds, focus, rendered pixels, resize and parent lifetime", flush=True)
        finally:
            parent.close()
            if process:
                NATIVE.stop_application(process)
            if browser:
                browser.close()

        process = browser = focus = None
        try:
            process, browser = NATIVE.open_application(playwright, args.scr, ["/s"], "scr-fullscreen", test_environment)
            page = NATIVE.page_at(browser, "https://appassets.local/")
            page.wait_for_function("window.matrixRuntimeDiagnostics?.ready === true && window.matrixRuntimeDiagnostics.frames > 0", timeout=20000)
            runtime = page.evaluate("window.matrixRuntimeDiagnostics")
            assert not runtime.get("errors"), runtime
            window = find_window(process.pid)
            assert window and not U.GetParent(window), "fullscreen screensaver was parented to the desktop"
            left, top, width, height = (U.GetSystemMetrics(index) for index in (76, 77, 78, 79))
            assert rect(window) == (left, top, left + width, top + height), "screensaver profile window flags left part of the virtual screen uncovered"
            assert U.GetWindowLongPtrW(window, -20) & 8, "fullscreen screensaver did not override the saved non-topmost flag"
            availability = input_availability_snapshot()
            unavailable = input_availability_reason(availability)
            if unavailable:
                focus = {"passed": False, "unchecked": unavailable, "availability": availability}
                reports.append({"mode": "fullscreen", "status": "unchecked", "reason": unavailable})
                (output / "report.json").write_text(json.dumps(reports, indent=2), encoding="utf-8")
                print(f"UNCHECKED screensaver /s input exit: {unavailable}; {json.dumps(availability)}", flush=True)
                return 2
            # Windows can refuse best-effort foreground activation. A genuine
            # saver also exits through its LastInputInfo mouse policy. Keyboard
            # input needs exact foreground ownership; a lone mouse move needs a
            # different primary-monitor point hit-tested to the owned root.
            try:
                focus = wait_for_owned_foreground(process, window, foreground_snapshot, page.wait_for_timeout)
                focus["foregroundAcquired"] = True
            except AssertionError as error:
                assert process.poll() is None, "screensaver exited before input verification"
                focus = {"foregroundAcquired": False, "focusRefusal": str(error),
                         "history": getattr(error, "focus_history", [])}
            focus["passed"] = False
            focus["availability"] = availability
            inputs = (Input * 2)()
            inputs[0].type = inputs[1].type = 1
            inputs[0].value.keyboard.key = inputs[1].value.keyboard.key = 0x1B
            inputs[1].value.keyboard.flags = 2
            mouse = (Input * 1)()
            mouse[0].type = 0
            mouse[0].value.mouse.flags = 0xC001  # MOVE | VIRTUALDESK | ABSOLUTE
            target = owned_mouse_point(window)
            mouse[0].value.mouse.dx = round((target["x"] - left) * 65535 / (width - 1))
            mouse[0].value.mouse.dy = round((target["y"] - top) * 65535 / (height - 1))
            response = ctypes.c_size_t()
            focus["uiResponsive"] = bool(U.SendMessageTimeoutW(window, 0, 0, 0, 2, 500, ctypes.byref(response)))
            assert focus["uiResponsive"], "owned screensaver UI stopped responding before input"
            focus["lastInputBefore"] = last_input_tick()
            focus["availabilityBeforeInput"] = input_availability_snapshot()
            unavailable = input_availability_reason(focus["availabilityBeforeInput"])
            if unavailable:
                focus["unchecked"] = unavailable
                reports.append({"mode": "fullscreen", "status": "unchecked", "reason": unavailable})
                (output / "report.json").write_text(json.dumps(reports, indent=2), encoding="utf-8")
                print(f"UNCHECKED screensaver /s input exit: {unavailable}; {json.dumps(focus['availabilityBeforeInput'])}", flush=True)
                return 2
            # Refresh the hit test and process/window axes immediately before
            # the single native call. Never activate another window to pass.
            focus["beforeInput"] = foreground_snapshot(window)
            focus["mouseTarget"] = owned_mouse_point(window)
            try:
                assert process.poll() is None, "screensaver exited before input; no input sent"
                mode = exit_input_mode(focus["beforeInput"], focus["mouseTarget"], process.pid, window)
                assert (target["x"], target["y"]) == (focus["mouseTarget"]["x"], focus["mouseTarget"]["y"]), "mouse target changed; no input sent"
            except AssertionError:
                focus["passed"] = False
                (output / "fullscreen-focus.json").write_text(json.dumps(focus, indent=2), encoding="utf-8")
                raise
            sent = U.SendInput(2, inputs, ctypes.sizeof(Input)) if mode == "escape" else U.SendInput(1, mouse, ctypes.sizeof(Input))
            assert sent == (2 if mode == "escape" else 1), ctypes.WinError(ctypes.get_last_error())
            focus.update({"inputSent": sent, "exitInput": mode})
            deadline = time.monotonic() + 2
            while time.monotonic() < deadline and last_input_tick() == focus["lastInputBefore"]:
                time.sleep(.01)
            focus["lastInputAfter"] = last_input_tick()
            if focus["lastInputAfter"] == focus["lastInputBefore"]:
                reason = "Windows accepted SendInput but delivered no session input event (LastInputInfo unchanged)"
                focus["unchecked"] = reason
                reports.append({"mode": "fullscreen", "status": "unchecked", "reason": reason})
                (output / "report.json").write_text(json.dumps(reports, indent=2), encoding="utf-8")
                print(f"UNCHECKED screensaver /s input exit: {reason}; {json.dumps(availability)}", flush=True)
                return 2
            process.wait(timeout=8)
            focus["exitCode"] = process.returncode
            assert process.returncode == 0, process.returncode
            focus["passed"] = True
            reports.append({"mode": "fullscreen", "foregroundAcquired": focus["foregroundAcquired"], "exitInput": mode,
                            "checks": ["independent-window", "virtual-screen-bounds", "topmost", "owned-input-target", "input-exit"]})
            print(f"PASS screensaver /s: full virtual-screen bounds, topmost and {mode} input exit; foregroundAcquired={focus['foregroundAcquired']}", flush=True)
        except BaseException as error:
            if focus is not None:
                focus["error"] = str(error)
            raise
        finally:
            if focus is not None:
                (output / "fullscreen-focus.json").write_text(json.dumps(focus, indent=2), encoding="utf-8")
            if process:
                NATIVE.stop_application(process)
            if browser:
                browser.close()
    (output / "report.json").write_text(json.dumps(reports, indent=2), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
