"""Startup ownership regressions without launching Windows applications."""
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location("native_smoke", Path(__file__).with_name("native-uc3d-smoke.py"))
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)


class OwnedProcess:
    pid = 123

    def __init__(self, slow_termination=False):
        self.alive = True
        self.returncode = None
        self.events = []
        self.slow_termination = slow_termination

    def poll(self):
        return None if self.alive else self.returncode

    def terminate(self):
        self.events.append("terminate")
        if not self.slow_termination:
            self.alive = False
            self.returncode = 1

    def wait(self, timeout):
        self.events.append(("wait", timeout))
        if self.alive:
            raise subprocess.TimeoutExpired("synthetic-owned-process", timeout)
        return self.returncode

    def kill(self):
        self.events.append("kill")
        self.alive = False
        self.returncode = 1


class StartupOwnershipTests(unittest.TestCase):
    def test_failed_startup_reaps_owned_process_for_cdp_error_and_endpoint_timeout(self):
        # One named regression covers both failure axes. The killing mutation is
        # removing stop_application(process) from open_application's exception path.
        observed = []
        for failure in ("cdp-error", "endpoint-timeout"):
            with self.subTest(failure=failure):
                process = OwnedProcess()
                driver = Mock()
                driver.chromium.connect_over_cdp.side_effect = RuntimeError("synthetic CDP failure")
                response = io.BytesIO(json.dumps({"webSocketDebuggerUrl": "ws://synthetic"}).encode())
                with patch.object(native.subprocess, "Popen", return_value=process), \
                     patch.object(native.urllib.request, "urlopen", side_effect=OSError("not ready") if failure == "endpoint-timeout" else None,
                                  return_value=response), \
                     patch.object(native.time, "monotonic", side_effect=[0, 1, 31] if failure == "endpoint-timeout" else [0, 1]), \
                     patch.object(native.time, "sleep"):
                    expected = "debug endpoint did not appear" if failure == "endpoint-timeout" else "synthetic CDP failure"
                    with self.assertRaisesRegex((AssertionError, RuntimeError), expected):
                        native.open_application(driver, Path("synthetic-not-executed.exe"), [], failure)
                observed.append((failure, process.events, process.alive))
        self.assertEqual(observed, [
            ("cdp-error", ["terminate", ("wait", 10)], False),
            ("endpoint-timeout", ["terminate", ("wait", 10)], False),
        ], "Failed startup did not reap its owned process")

    def test_successful_handoff_keeps_owned_process_alive_for_the_caller(self):
        process = OwnedProcess()
        browser = object()
        driver = Mock()
        driver.chromium.connect_over_cdp.return_value = browser
        response = io.BytesIO(json.dumps({"webSocketDebuggerUrl": "ws://synthetic"}).encode())
        with patch.object(native.subprocess, "Popen", return_value=process), \
             patch.object(native.urllib.request, "urlopen", return_value=response):
            actual_process, actual_browser = native.open_application(driver, Path("synthetic-not-executed.exe"), [], "success")
        self.assertIs(actual_process, process)
        self.assertIs(actual_browser, browser)
        self.assertTrue(process.alive)
        self.assertEqual(process.events, [])

    def test_slow_termination_is_killed_and_reaped(self):
        process = OwnedProcess(slow_termination=True)
        native.stop_application(process)
        self.assertEqual(process.events, ["terminate", ("wait", 10), "kill", ("wait", 5)])
        self.assertFalse(process.alive)


if __name__ == "__main__":
    unittest.main(verbosity=2)
