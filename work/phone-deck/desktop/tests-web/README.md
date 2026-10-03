# Phone web tests

Run the dependency-free audio DSP/lifecycle and session/transport tests with Node 22 or later:

```sh
node --test work/phone-deck/desktop/tests-web/*.test.mjs
```

The separate integration runner starts the real receiver with an in-memory silent
speech engine, isolated credentials and ports, and Chromium's generated microphone.
It does not access a real microphone, register global hotkeys, insert generated text,
download a model, or save audio. It needs .NET 10, Playwright, and Playwright's
Chromium headless shell (`npx playwright install chromium --only-shell`).

```sh
node work/phone-deck/desktop/tests-web/browser.integration.mjs
```

When dependencies are installed outside the project, set `PHONEDECK_DOTNET` to the
dotnet executable, `PHONEDECK_PLAYWRIGHT_MODULE` to Playwright's `index.mjs`, and
`PLAYWRIGHT_BROWSERS_PATH` to its browser cache. `PHONEDECK_BROWSER_EXECUTABLE` can
select an explicit Chromium executable. Use `PHONEDECK_TEST_BASE_PORT` to change
the default test ports 18765, 18766 and 18768. The harness refuses production ports.
`PHONEDECK_SKIP_HARNESS_BUILD=1` reuses a previously built fixture; rebuild after
changing C# or embedded PhoneWeb assets.

Screenshots and `results.json` are written under ignored
`outputs/iphone-pwa/integration`. Temporary private certificates and pairing
credentials are deleted after the run. The checked-in harness contains no secrets.

Coverage includes desktop confirmation of browser pairing, actual AudioWorklet
PCM frames over WebSocket, tap and hold, desktop stop/cancel, shared desktop segments
without stopping phone supply, final partial frame flushing, permission rejection
and retry, socket interruption/reconnection, page closure, initial offline recovery,
pairing a fresh cookie-free home-screen context by pasting its connection link,
and clearing stale shared-supply indicators while disconnected. A final 20-cycle
capture/desktop-stop run checks for live-track, AudioContext, and receiver residues
after every cycle. `results.json` reports median/P95/max request-to-observed-release
latency, including local HTTP and browser automation overhead; it is not a phone
hardware performance claim. The 20-cycle run does not take per-cycle screenshots.

Chromium uses both `--use-fake-device-for-media-stream` and
`--use-fake-ui-for-media-stream`. Its fake permission UI always grants, so only the
permission-denial case injects a single `NotAllowedError`; successful captures all
use the actual generated browser device. The isolated browser bypasses TLS certificate
validation. These tests do **not** verify iPhone certificate installation, actual
Safari permissions, background/lock-screen capture, or speech recognition on hardware.
