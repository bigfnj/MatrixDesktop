"""Bounded focus observation and strict owned-window input authorization."""
import time


def input_availability_reason(snapshot):
    if snapshot.get("sessionState") != "Active":
        return "the test session is not WTSActive"
    if snapshot.get("inputDesktopOpenError") != 0:
        return "the input desktop is inaccessible"
    if str(snapshot.get("threadDesktop", "")).casefold() != "default":
        return "the test thread is not on the Default desktop"
    if str(snapshot.get("inputDesktop", "")).casefold() != "default":
        return "the active input desktop is not Default"
    return None


def is_owned_foreground(snapshot, pid, window):
    return (snapshot["foregroundRoot"] == window
            and snapshot["rootPid"] == pid and snapshot["windowPid"] == pid)


def is_owned_hit_point(snapshot, pid, window):
    return (snapshot["hitRoot"] == window
            and snapshot["rootPid"] == pid and snapshot["windowPid"] == pid
            and snapshot["left"] <= snapshot["x"] < snapshot["right"]
            and snapshot["top"] <= snapshot["y"] < snapshot["bottom"]
            and (snapshot["x"], snapshot["y"]) != (snapshot["cursorX"], snapshot["cursorY"]))


def exit_input_mode(foreground, hit_point, pid, window):
    if is_owned_foreground(foreground, pid, window):
        return "escape"
    assert hit_point and is_owned_hit_point(hit_point, pid, window), "mouse target is not a different proven-owned point; no input sent"
    return "mouse-move"


def wait_for_owned_foreground(process, window, read_snapshot, pump, timeout=3.5, now=time.monotonic):
    started = now()
    deadline = started + timeout
    history = []
    while True:
        assert process.poll() is None, "owned screensaver exited before foreground ownership; no input sent"
        snapshot = read_snapshot(window)
        if not history or snapshot != history[-1]:
            history.append(snapshot)
        if is_owned_foreground(snapshot, process.pid, window):
            return {"elapsedSeconds": now() - started, "history": history}
        if now() >= deadline:
            error = AssertionError(f"owned screensaver did not acquire foreground; no input sent: {history}")
            error.focus_history = history
            raise error
        pump(100)
