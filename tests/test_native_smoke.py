"""Native smoke ownership regressions without launching Windows applications."""
import asyncio
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
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


class ProfileOwnershipTests(unittest.TestCase):
    """AUD-26: every launch's private WebView2 profile is deleted when the case reaps it."""

    def launch(self, artifacts):
        process = OwnedProcess()

        def popen(command, env=None, **kwargs):
            # The real application creates the profile; the stand-in does the same.
            state = Path(env["WEBVIEW2_USER_DATA_FOLDER"]) / "EBWebView" / "Default"
            state.mkdir(parents=True)
            (state / "Preferences").write_text("{}", encoding="utf-8")
            return process

        driver = Mock()
        driver.chromium.connect_over_cdp.return_value = object()
        response = io.BytesIO(json.dumps({"webSocketDebuggerUrl": "ws://synthetic"}).encode())
        with patch.object(native, "ARTIFACTS", artifacts), \
             patch.object(native.subprocess, "Popen", side_effect=popen), \
             patch.object(native.urllib.request, "urlopen", return_value=response):
            native.open_application(driver, Path("synthetic-not-executed.exe"), [], "profile")
        profiles = list(artifacts.glob("profile-profile-*"))
        self.assertEqual(len(profiles), 1, "the launch did not create exactly one private profile")
        return process, profiles[0]

    def test_stop_application_deletes_the_profile_whether_or_not_the_app_already_exited(self):
        # Killing mutations: delete the cleanup (both subtests fail); run it only after the
        # already-exited early return (only already_exited=True fails).
        for already_exited in (False, True):
            with self.subTest(already_exited=already_exited):
                with tempfile.TemporaryDirectory() as directory:
                    process, profile = self.launch(Path(directory))
                    if already_exited:
                        # close_window succeeded, so the case's finally finds a reaped app.
                        process.alive, process.returncode = False, 0
                    native.stop_application(process)
                    self.assertFalse(profile.exists(), f"profile survived stop_application: {profile}")

    def test_profile_removal_retries_while_webview_children_hold_files(self):
        # Killing mutation: a single attempt; the transient PermissionError leaves the profile.
        with tempfile.TemporaryDirectory() as directory:
            process, profile = self.launch(Path(directory))
            real_rmtree, calls = native.shutil.rmtree, []

            def held_once(path):
                calls.append(path)
                if len(calls) == 1:
                    raise PermissionError("held by msedgewebview2")
                real_rmtree(path)

            with patch.object(native.shutil, "rmtree", side_effect=held_once), patch.object(native.time, "sleep"):
                native.stop_application(process)
            self.assertFalse(profile.exists(), "a transient sharing violation left the profile behind")
            self.assertEqual(len(calls), 2)

    def test_removal_that_keeps_failing_warns_instead_of_raising(self):
        with tempfile.TemporaryDirectory() as directory:
            process, profile = self.launch(Path(directory))
            output = io.StringIO()
            # Its own leak list, so the at-exit LEAKED line of this run reports nothing.
            with patch.object(native, "LEAKED_PROFILES", []), \
                 patch.object(native.shutil, "rmtree", side_effect=PermissionError("held")), \
                 patch.object(native.time, "sleep"), patch("sys.stdout", output):
                native.stop_application(process)
            self.assertIn(f"WARN could not remove WebView2 profile {profile}", output.getvalue())

    def test_prune_removes_only_profiles_older_than_a_day(self):
        # Killing mutation: drop the age filter; a concurrent run's fresh profile is deleted.
        with tempfile.TemporaryDirectory() as directory:
            artifacts = Path(directory)
            old, fresh, other = artifacts / "profile-old-1", artifacts / "profile-fresh-2", artifacts / "pictures"
            for path in (old, fresh, other):
                (path / "keep").mkdir(parents=True)
            two_days_ago = native.time.time() - 2 * 24 * 3600
            for path in (old, other):
                os.utime(path, (two_days_ago, two_days_ago))
            with patch.object(native, "ARTIFACTS", artifacts), patch("sys.stdout", io.StringIO()):
                native.prune_stale_profiles()
            self.assertEqual(sorted(path.name for path in artifacts.iterdir()), ["pictures", "profile-fresh-2"])


class DefaultMediaGrantTests(unittest.TestCase):
    """AUD-10: the default launch's media check must be able to fail under per-file picture IDs."""

    @staticmethod
    def probe(served):
        def statuses(ids):
            return [200 if media_id in served else 404 for media_id in ids]
        return statuses

    def test_a_hashed_picture_grant_fails_although_image_0_is_404(self):
        # Killing mutation: drop the image-list assertion and probe a fixed 'image-0' again.
        # Under per-file IDs that probe answers 404 for a granted picture, so the check passes.
        picture = "image-3f2a9c6d1e0b4a57"
        bootstrap = {"images": [{"id": picture, "url": "https://uc3d-media.local/__uc3d/" + picture}]}
        with self.assertRaisesRegex(AssertionError, "granted pictures: \\['image-3f2a9c6d1e0b4a57'\\]"):
            native.assert_no_optional_media(bootstrap, self.probe({picture}))

    def test_a_served_fixed_name_resource_fails(self):
        with self.assertRaisesRegex(AssertionError, "'music': 200"):
            native.assert_no_optional_media({"images": []}, self.probe({"music"}))

    def test_no_grant_and_no_served_media_passes(self):
        probed = []

        def statuses(ids):
            probed.extend(ids)
            return [404] * len(ids)

        native.assert_no_optional_media({"images": []}, statuses)
        self.assertEqual(probed, ["desktop", "music", "effects", "glyphs"])


# R2-37: stand-ins for the WebView2 host and its browser run on the base interpreter, because a
# venv's python.exe is a launcher whose real interpreter is one more process down the tree.
STAND_IN = Path(sys.base_prefix) / "python.exe"
STAND_IN_HOST = (
    "import subprocess, sys\n"
    "browser = subprocess.Popen([sys.executable, '-c', sys.argv[1], sys.argv[2]], stdout=subprocess.PIPE)\n"
    "assert browser.stdout.readline().strip() == b'held'\n"
    "print(browser.pid, flush=True)\n")
STAND_IN_BROWSER = (
    "import sys, time\n"
    "with open(sys.argv[1], 'rb'):  # no FILE_SHARE_DELETE, like the DIPS file in the gate's WARN\n"
    "    print('held', flush=True)\n"
    "    time.sleep(2)\n")


def load_script(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), Path(__file__).with_name(name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ProfileDeletionTests(unittest.TestCase):
    """R2-37: a profile counts as removed only once its folder is gone, deletion waits for the
    processes the host started, and a profile that survives is reported."""

    def make_profile(self, directory):
        profile = Path(directory) / "profile-case-1"
        (profile / "EBWebView" / "Default").mkdir(parents=True)
        for name in ("Preferences", "DIPS"):
            (profile / "EBWebView" / "Default" / name).write_bytes(b"{}")
        return profile

    def test_inner_entry_vanishing_mid_walk_is_retried_not_reported_removed(self):
        # Killing mutation: return True for every FileNotFoundError again. Patching rmtree,
        # not racing os.unlink, because Python 3.13+ rmtree ignores an inner FileNotFoundError.
        with tempfile.TemporaryDirectory() as directory:
            profile = self.make_profile(directory)
            real_rmtree, calls = native.shutil.rmtree, []

            def renamed_by_exiting_browser(path):
                calls.append(path)
                if len(calls) == 1:
                    # What Python 3.11 rmtree raises when the browser renamed a file it listed.
                    (Path(path) / "EBWebView" / "Default" / "Preferences").unlink()
                    raise FileNotFoundError(2, "renamed by the exiting browser",
                                            str(Path(path) / "EBWebView" / "Default" / "Preferences.tmp"))
                real_rmtree(path)

            with patch.object(native.shutil, "rmtree", side_effect=renamed_by_exiting_browser), \
                 patch.object(native.time, "sleep"):
                returned = native.remove_profile(profile)
            self.assertFalse(profile.exists(), f"remove_profile returned {returned} and left {profile}")
            self.assertEqual((returned, len(calls)), (True, 2))

    def test_a_profile_that_was_never_created_counts_as_removed_after_one_call(self):
        # Killing mutation: retry every FileNotFoundError (20 calls and a WARN).
        with tempfile.TemporaryDirectory() as directory:
            missing = Path(directory) / "profile-never-created-1"
            real_rmtree, calls = native.shutil.rmtree, []

            def counted(path):
                calls.append(path)
                real_rmtree(path)

            output = io.StringIO()
            with patch.object(native.shutil, "rmtree", side_effect=counted), patch.object(native.time, "sleep"), \
                 patch("sys.stdout", output):
                self.assertTrue(native.remove_profile(missing))
            self.assertEqual(len(calls), 1)
            self.assertNotIn("WARN", output.getvalue())

    def test_a_profile_that_cannot_be_deleted_is_counted_and_reported_as_leaked(self):
        # Killing mutation: ignore remove_profile's result again, so nothing is counted.
        with tempfile.TemporaryDirectory() as directory:
            process, profile = ProfileOwnershipTests.launch(self, Path(directory))
            output = io.StringIO()
            with patch.object(native, "LEAKED_PROFILES", []), \
                 patch.object(native.shutil, "rmtree", side_effect=PermissionError("held")), \
                 patch.object(native.time, "sleep"), patch("sys.stdout", output):
                native.stop_application(process)
                native.report_leaked_profiles()
            self.assertIn(f"WARN could not remove WebView2 profile {profile}", output.getvalue())
            self.assertIn(f"LEAKED 1 WebView2 profile(s): {profile}", output.getvalue())

    def test_the_leaked_line_prints_when_an_importing_script_exits(self):
        # Killing mutation: drop atexit.register(report_leaked_profiles). screensaver-smoke.py
        # and native-zmatrix-configurator.py only import the helpers and never report.
        script = ("import importlib.util, sys\n"
                  "spec = importlib.util.spec_from_file_location('native_smoke', sys.argv[1])\n"
                  "native = importlib.util.module_from_spec(spec)\n"
                  "spec.loader.exec_module(native)\n"
                  "native.LEAKED_PROFILES.append('profile-held-1')\n")
        result = subprocess.run([sys.executable, "-B", "-c", script, spec.origin],
                                capture_output=True, text=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("LEAKED 1 WebView2 profile(s): profile-held-1", result.stdout)

    def test_a_file_held_by_a_process_the_host_started_is_deleted_once_that_process_exits(self):
        # Killing mutation: skip wait_for_descendants in delete_owned_profile. The stand-in
        # browser holds DIPS for 2 s after its host exits (the gate measured WinError 32 after
        # 5 s), longer than the 0.5 s of retries allowed here, so the profile would leak.
        self.assertTrue(STAND_IN.is_file(), f"no base interpreter at {STAND_IN}")
        with tempfile.TemporaryDirectory() as directory:
            profile = self.make_profile(directory)
            host = subprocess.Popen([str(STAND_IN), "-c", STAND_IN_HOST, STAND_IN_BROWSER,
                                     str(profile / "EBWebView" / "Default" / "DIPS")], stdout=subprocess.PIPE)
            out, _ = host.communicate(timeout=60)
            self.assertEqual(host.returncode, 0, "the stand-in host exited before its browser held the file")
            browser = native.KERNEL32.OpenProcess(0x00100000, False, int(out))  # SYNCHRONIZE, for cleanup
            host.webview_profile = profile
            real_remove, output = native.remove_profile, io.StringIO()
            try:
                started = time.monotonic()
                with patch.object(native, "LEAKED_PROFILES", []) as leaked, \
                     patch.object(native, "remove_profile", lambda path: real_remove(path, attempts=2, delay=0.25)), \
                     patch("sys.stdout", output):
                    native.stop_application(host)
                waited = time.monotonic() - started
            finally:
                if browser:
                    native.KERNEL32.WaitForSingleObject(browser, 10000)
                    native.KERNEL32.CloseHandle(browser)
            self.assertFalse(profile.exists(), f"profile survived its host's browser: {output.getvalue()}")
            self.assertEqual(leaked, [])
            self.assertGreater(waited, 1, "deletion did not wait for the browser that held the file")

    def test_an_older_process_that_claims_the_host_as_its_parent_is_not_waited_for(self):
        # Killing mutation: drop the started-after-its-parent check in open_descendants. This
        # test process started before the host, so it would be waited for until the timeout.
        host = subprocess.Popen([str(STAND_IN), "-c", "pass"])
        host.wait(timeout=60)
        started = time.monotonic()
        with patch.object(native, "process_parents", return_value={os.getpid(): host.pid}):
            running = native.wait_for_descendants(host, timeout=2)
        self.assertEqual(running, 0)
        self.assertLess(time.monotonic() - started, 1.5)


class OwnedProfileScriptTests(unittest.TestCase):
    """R2-37: the desktop snapshot and a manual wallpaper run delete the profiles they create."""

    def test_desktop_snapshot_deletes_its_profile_after_a_clean_close_and_after_a_failed_one(self):
        # Killing mutation: drop native.stop_application from release_application. The success
        # path then only closes the window, as every manual run did, and keeps the profile.
        snapshot = load_script("desktop-snapshot-smoke")
        for closed in (True, False):
            with self.subTest(closed=closed), tempfile.TemporaryDirectory() as directory:
                process, profile = ProfileOwnershipTests.launch(self, Path(directory))
                if closed:
                    process.alive, process.returncode = False, 0  # main's close_window succeeded
                with patch.object(native, "close_window", side_effect=AssertionError("synthetic close failure")):
                    snapshot.release_application(native, process)
                self.assertFalse(process.alive)
                self.assertFalse(profile.exists(), f"desktop snapshot kept {profile}")

    def test_a_wallpaper_case_deletes_its_profile_only_when_asked(self):
        # Killing mutation: drop the delete_profile branch from run()'s finally.
        wallpaper = load_script("wallpaper-smoke")
        for delete in (True, False):
            with self.subTest(delete=delete), tempfile.TemporaryDirectory() as directory:
                process = OwnedProcess()
                process.alive, process.returncode = False, 1  # exits before its debug endpoint opens

                def popen(command, env=None, **kwargs):
                    state = Path(env["WEBVIEW2_USER_DATA_FOLDER"]) / "EBWebView" / "Default"
                    state.mkdir(parents=True)
                    (state / "Preferences").write_text("{}", encoding="utf-8")
                    return process

                with patch.object(wallpaper.subprocess, "Popen", side_effect=popen):
                    with self.assertRaisesRegex(AssertionError, "owned application exited: 1"):
                        asyncio.run(wallpaper.run(Path("synthetic-not-executed.exe"), Path(directory), "matrix", "regl",
                                                  delete_profile=delete))
                case = Path(directory) / "matrix-regl"
                self.assertEqual((case / "owned-webview-profile").exists(), not delete)
                self.assertEqual(json.loads((case / "evidence.json").read_text(encoding="utf-8")).get("profileDeleted"),
                                 True if delete else None)

    def test_wallpaper_main_deletes_profiles_only_from_an_output_folder_it_created(self):
        # Killing mutation: pass delete_profile=True whatever --output says; the gate's fixed
        # folder would then lose the profile its next run reuses.
        wallpaper = load_script("wallpaper-smoke")
        requested = []

        async def run(app, output, scene, renderer, delete_profile=False):
            requested.append((output, delete_profile))

        with tempfile.TemporaryDirectory() as directory:
            app, created, given = Path(directory) / "MatrixDesktop.exe", Path(directory) / "made", Path(directory) / "given"
            app.write_bytes(b"")
            with patch.object(wallpaper, "run", run), patch.object(wallpaper.tempfile, "mkdtemp", return_value=str(created)), \
                 patch("sys.stdout", io.StringIO()):
                wallpaper.main(["--app", str(app), "--scene", "matrix", "--renderer", "regl"])
                wallpaper.main(["--app", str(app), "--scene", "matrix", "--renderer", "regl", "--output", str(given)])
        self.assertEqual(requested, [(created.resolve(), True), (given.resolve(), False)])


if __name__ == "__main__":
    unittest.main(verbosity=2)
