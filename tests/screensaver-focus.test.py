"""Deterministic startup-focus timing and ownership regressions."""
import unittest
from screensaver_focus import exit_input_mode, input_availability_reason, wait_for_owned_foreground


class Process:
    pid = 17
    def __init__(self, result=None): self.result = result
    def poll(self): return self.result


class Clock:
    value = 0
    def now(self): return self.value
    def pump(self, milliseconds): self.value += milliseconds / 1000


def snapshot(root=42, root_pid=17, window_pid=17):
    return {"foregroundRoot": root, "rootPid": root_pid, "windowPid": window_pid}


def hit_point(**overrides):
    result = {"hitRoot": 42, "rootPid": 17, "windowPid": 17,
              "x": 50, "y": 50, "left": 0, "top": 0, "right": 100, "bottom": 100,
              "cursorX": 12, "cursorY": 12}
    result.update(overrides)
    return result


class FocusWaitTests(unittest.TestCase):
    def test_owned_foreground_authorizes_escape_without_mouse_target(self):
        self.assertEqual(exit_input_mode(snapshot(), None, 17, 42), "escape")

    def test_foreign_foreground_authorizes_only_different_owned_mouse_point(self):
        self.assertEqual(exit_input_mode(snapshot(root=99, root_pid=88), hit_point(), 17, 42), "mouse-move")

    def test_foreign_hit_root_never_authorizes_mouse(self):
        with self.assertRaisesRegex(AssertionError, "proven-owned point; no input sent"):
            exit_input_mode(snapshot(root=99), hit_point(hitRoot=99), 17, 42)

    def test_foreign_hit_root_pid_never_authorizes_mouse(self):
        with self.assertRaisesRegex(AssertionError, "proven-owned point; no input sent"):
            exit_input_mode(snapshot(root=99), hit_point(rootPid=88), 17, 42)

    def test_reused_target_window_never_authorizes_mouse(self):
        with self.assertRaisesRegex(AssertionError, "proven-owned point; no input sent"):
            exit_input_mode(snapshot(root=99), hit_point(windowPid=88), 17, 42)

    def test_current_cursor_position_never_authorizes_mouse(self):
        with self.assertRaisesRegex(AssertionError, "proven-owned point; no input sent"):
            exit_input_mode(snapshot(root=99), hit_point(cursorX=50, cursorY=50), 17, 42)

    def test_outside_primary_monitor_never_authorizes_mouse(self):
        for coordinates in ({"x": -1}, {"x": 100}, {"y": -1}, {"y": 100}):
            with self.subTest(coordinates=coordinates), self.assertRaisesRegex(AssertionError, "proven-owned point; no input sent"):
                exit_input_mode(snapshot(root=99), hit_point(**coordinates), 17, 42)

    def test_delayed_owned_focus_waits_for_startup_retry(self):
        clock = Clock()
        observed = wait_for_owned_foreground(Process(), 42,
            lambda _: snapshot(root=99, root_pid=88) if clock.value < .25 else snapshot(),
            clock.pump, timeout=1, now=clock.now)
        self.assertGreaterEqual(observed["elapsedSeconds"], .25)
        self.assertLess(observed["elapsedSeconds"], 1)
        self.assertEqual(len(observed["history"]), 2)

    def test_current_owned_focus_returns_without_pumping(self):
        result = wait_for_owned_foreground(Process(), 42, lambda _: snapshot(),
            lambda _: self.fail("Owned focus must not need a retry."), now=lambda: 0)
        self.assertEqual(result["elapsedSeconds"], 0)

    def test_foreign_root_pid_never_authorizes_input(self):
        clock = Clock()
        with self.assertRaisesRegex(AssertionError, "did not acquire foreground; no input sent"):
            wait_for_owned_foreground(Process(), 42, lambda _: snapshot(root_pid=999),
                clock.pump, timeout=.3, now=clock.now)
        self.assertGreaterEqual(clock.value, .3)

    def test_same_process_foreign_window_never_authorizes_input(self):
        clock = Clock()
        with self.assertRaisesRegex(AssertionError, "did not acquire foreground; no input sent"):
            wait_for_owned_foreground(Process(), 42, lambda _: snapshot(root=99),
                clock.pump, timeout=.3, now=clock.now)

    def test_reused_window_with_foreign_owner_never_authorizes_input(self):
        clock = Clock()
        with self.assertRaisesRegex(AssertionError, "did not acquire foreground; no input sent"):
            wait_for_owned_foreground(Process(), 42, lambda _: snapshot(window_pid=999),
                clock.pump, timeout=.3, now=clock.now)

    def test_exited_owned_process_never_reads_or_authorizes_focus(self):
        with self.assertRaisesRegex(AssertionError, "screensaver exited before foreground ownership"):
            wait_for_owned_foreground(Process(0), 42,
                lambda _: self.fail("An exited process must not consult a stale window."),
                lambda _: self.fail("An exited process must not retry."))


class InputAvailabilityTests(unittest.TestCase):
    def available(self, **overrides):
        result = {"sessionState": "Active", "inputDesktopOpenError": 0,
                  "threadDesktop": "Default", "inputDesktop": "Default",
                  "processSessionId": 2, "activeConsoleSessionId": 2}
        result.update(overrides)
        return result

    def test_active_console_default_desktop_is_available(self):
        self.assertIsNone(input_availability_reason(self.available()))

    def test_connected_active_rdp_does_not_require_console_session_match(self):
        self.assertIsNone(input_availability_reason(self.available(processSessionId=1)))

    def test_disconnected_session_is_unavailable(self):
        self.assertIn("not WTSActive", input_availability_reason(self.available(sessionState="Disconnected")) or "")

    def test_unknown_session_state_is_unavailable(self):
        self.assertIn("not WTSActive", input_availability_reason(self.available(sessionState=None)) or "")

    def test_inaccessible_input_desktop_is_unavailable(self):
        self.assertIn("inaccessible", input_availability_reason(self.available(inputDesktopOpenError=5)) or "")

    def test_nondefault_thread_desktop_is_unavailable(self):
        self.assertIn("thread", input_availability_reason(self.available(threadDesktop="Other")) or "")

    def test_secure_input_desktop_is_unavailable(self):
        self.assertIn("input desktop", input_availability_reason(self.available(inputDesktop="Winlogon")) or "")

    def test_missing_desktop_name_is_unavailable(self):
        self.assertIn("input desktop", input_availability_reason(self.available(inputDesktop=None)) or "")


if __name__ == "__main__":
    unittest.main()
