"""Native smoke ownership regressions without launching Windows applications."""
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
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


class PortablePresetOwnershipTests(unittest.TestCase):
    def test_existing_presets_and_backup_restore_exact_bytes_after_case_failure(self):
        # Killing mutation: omit the existing-file write_bytes restoration.
        with tempfile.TemporaryDirectory() as directory:
            exe = Path(directory) / "MatrixDesktopConfigurator.exe"
            preset = exe.with_name("MatrixDesktopConfigurator.presets.json")
            backup = preset.with_name(preset.name + ".bak")
            original = {preset: b'\xef\xbb\xbf{"userPresets":[{"name":"Owner preset"}]}\r\n',
                        backup: b'{"lastDraft":{"scene":"matrix"}}\n'}
            for path, contents in original.items():
                path.write_bytes(contents)

            @native.preserve_portable_presets
            def failing_case(playwright, executable):
                preset.write_bytes(b'{"lastDraft":{"scene":"uc3d","volumetric":false}}')
                backup.write_bytes(b'test-created backup rotation')
                raise RuntimeError("synthetic GUI failure after autosave")

            with self.assertRaisesRegex(RuntimeError, "synthetic GUI failure"):
                failing_case(None, exe)
            self.assertEqual({path: path.read_bytes() for path in original}, original,
                             "GUI smoke altered existing portable preset or backup bytes")

    def test_startup_failure_restores_original_absence_and_keeps_existing_backup(self):
        # Killing mutation: omit removal of files absent before startup.
        with tempfile.TemporaryDirectory() as directory:
            exe = Path(directory) / "MatrixDesktopConfigurator.exe"
            preset = exe.with_name("MatrixDesktopConfigurator.presets.json")
            backup = preset.with_name(preset.name + ".bak")
            original_backup = b'backup bytes stay exactly the same\r\n'
            backup.write_bytes(original_backup)

            def startup_failure(*args):
                preset.write_bytes(b'test-created store')
                backup.write_bytes(b'test-created backup rotation')
                raise RuntimeError("synthetic startup failure")

            with patch.object(native, "open_application", side_effect=startup_failure):
                with self.assertRaisesRegex(RuntimeError, "synthetic startup failure"):
                    native.default_configurator_case(None, exe)
            self.assertFalse(preset.exists(), "GUI smoke left a preset store absent before startup")
            self.assertEqual(backup.read_bytes(), original_backup)

    def test_successful_case_restores_missing_files_after_owned_process_cleanup(self):
        with tempfile.TemporaryDirectory() as directory:
            exe = Path(directory) / "MatrixDesktopConfigurator.exe"
            preset = exe.with_name("MatrixDesktopConfigurator.presets.json")
            backup = preset.with_name(preset.name + ".bak")
            events = []

            @native.preserve_portable_presets
            def successful_case(playwright, executable, result):
                try:
                    preset.write_bytes(b'test-created store')
                    backup.write_bytes(b'test-created backup')
                    return result
                finally:
                    self.assertTrue(preset.exists() and backup.exists())
                    events.append("owned process reaped")

            self.assertEqual(successful_case(None, exe, "finished"), "finished")
            self.assertEqual(events, ["owned process reaped"])
            self.assertFalse(preset.exists() or backup.exists(), "GUI smoke left files absent before the successful case")


if __name__ == "__main__":
    unittest.main(verbosity=2)
