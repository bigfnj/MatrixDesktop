"""Verify host pause against a real browser AudioContext at volume zero."""
import argparse
import functools
import hashlib
import http.server
import json
import threading
from pathlib import Path

from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "MatrixDesktop" / "web"


class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        if self.path == "/audio-test.html":
            content = b'<!doctype html><title>Silent audio lifecycle test</title><button id="start">Start silent audio</button><canvas></canvas>'
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(content)))
            self.end_headers()
            self.wfile.write(content)
        elif self.path == "/favicon.ico":
            self.send_response(204)
            self.end_headers()
        else:
            super().do_GET()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--channel", default="msedge")
    parser.add_argument("--web-root", type=Path, default=WEB)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts" / "uc3d-audio-browser")
    parser.add_argument("--only", choices=["running", "initial"])
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Handler, directory=str(args.web_root)))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    origin = f"http://127.0.0.1:{server.server_port}"
    results, failures = [], []
    try:
        with sync_playwright() as playwright:
            browser = playwright.chromium.launch(channel=args.channel)
            for name in ([args.only] if args.only else ["running", "initial"]):
                context = browser.new_context()
                context.add_init_script(f"window.matrixHostPlaybackState={{paused:{str(name == 'initial').lower()}}}")
                page = context.new_page()
                errors = []
                page.on("pageerror", lambda error: errors.append(str(error)))
                try:
                    page.goto(origin + "/audio-test.html")
                    page.evaluate("""async () => {
                        const {createAudio}=await import('/js/uc3d/audio.js');
                        document.querySelector('#start').onclick=()=>window.testAudio=createAudio(
                            {sound:'effects',soundVolume:0},{preview:false},document.querySelector('canvas'));
                    }""")
                    witnesses = {}
                    for relative in ("js/uc3d/audio.js", "js/hostPlayback.js", "js/hostPointer.js"):
                        deployed = bytes(page.evaluate("async path=>Array.from(new Uint8Array(await(await fetch(path,{cache:'no-store'})).arrayBuffer()))", relative))
                        assert deployed == (args.web_root / relative).read_bytes(), f"served source mismatch: {relative}"
                        witnesses[relative] = hashlib.sha256(deployed).hexdigest()
                        print(f"SOURCE {relative} sha256={witnesses[relative]}", flush=True)
                    page.locator("#start").click()
                    if name == "initial":
                        page.wait_for_function("testAudio.state.status === 'paused'")
                        assert page.evaluate("testAudio.state.contextState") == "none", "initial host pause allocated audio"
                        page.evaluate("window.dispatchEvent(new CustomEvent('matrix-host-state',{detail:{paused:false}}))")
                    page.wait_for_function("testAudio.state.status === 'playing' && testAudio.state.contextState === 'running'")
                    page.evaluate("window.dispatchEvent(new CustomEvent('matrix-host-state',{detail:{paused:true}}))")
                    page.wait_for_function("testAudio.state.status === 'paused' && testAudio.state.contextState === 'suspended'", timeout=5000)
                    page.evaluate("document.dispatchEvent(new Event('visibilitychange'))")
                    page.wait_for_timeout(100)
                    assert page.evaluate("testAudio.state.contextState") == "suspended", "visibility restoration overrode host pause"
                    page.evaluate("window.dispatchEvent(new CustomEvent('matrix-host-state',{detail:{paused:false}}))")
                    page.wait_for_function("testAudio.state.status === 'playing' && testAudio.state.contextState === 'running'")
                    page.evaluate("testAudio.dispose()")
                    page.wait_for_function("testAudio.state.status === 'disposed' && testAudio.state.contextState === 'closed'")
                    assert not errors, errors
                    results.append({"case": name, "volume": 0, "states": ["running", "suspended", "running", "closed"], "sourceWitnesses": witnesses})
                    print(f"PASS silent UC3D AudioContext {name}: paused, resumed and closed", flush=True)
                except Exception as error:
                    failure = f"{name}: {error}"
                    failures.append(failure)
                    print("FAIL " + failure, flush=True)
                finally:
                    context.close()
            browser.close()
    finally:
        server.shutdown()
        server.server_close()
    (args.output / "report.json").write_text(json.dumps({"checks": results, "failures": failures}, indent=2), encoding="utf-8")
    print(f"UC3D browser audio: {len(results)} passed, {len(failures)} failed", flush=True)
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
