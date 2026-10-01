"""Check the native Mac event loop stays alive, without synthesizing user input."""
import selectors
import subprocess
import sys
import time

with subprocess.Popen([sys.argv[1]], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL) as helper:
    try:
        with selectors.DefaultSelector() as ready:
            ready.register(helper.stdout, selectors.EVENT_READ)
            if not ready.select(timeout=10):
                raise RuntimeError("Mac hotkey helper did not report startup")
            status = helper.stdout.readline().decode("ascii").strip()
        if status not in ("ready", "shortcut-conflict"):
            raise RuntimeError("Mac hotkey helper failed to initialize: " + status)
        time.sleep(0.5)
        if helper.poll() is not None:
            raise RuntimeError("Mac hotkey application event loop exited immediately")
        print("Mac native hotkey event loop remains running: " + status)
    finally:
        if helper.poll() is None:
            helper.kill()
        helper.wait(timeout=5)
