/**
 * Real HTTP/WS/AudioWorklet integration with generated Chromium audio, never a real mic.
 * Requires .NET 10, Playwright and its Chromium headless shell. Run from any directory:
 *   node work/phone-deck/desktop/tests-web/browser.integration.mjs
 * Optional: PHONEDECK_DOTNET, PHONEDECK_PLAYWRIGHT_MODULE, PLAYWRIGHT_BROWSERS_PATH,
 * PHONEDECK_BROWSER_EXECUTABLE, PHONEDECK_SKIP_HARNESS_BUILD=1, PHONEDECK_TEST_BASE_PORT.
 * PHONEDECK_TEST_OUTPUT selects a separate evidence directory for each run.
 * TLS validation is bypassed ONLY in this isolated test browser; this does not validate
 * iPhone certificate installation, Safari background capture, or real recognition.
 */
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, writeFile, rm, access } from 'node:fs/promises';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../../../..');
const output = resolve(process.env.PHONEDECK_TEST_OUTPUT || join(root, 'outputs/iphone-pwa/integration'));
const dotnet = process.env.PHONEDECK_DOTNET || 'dotnet';
const basePort = Number(process.env.PHONEDECK_TEST_BASE_PORT || 18765);
assert.ok(Number.isInteger(basePort) && basePort > 1023 && basePort < 65531);
const localOrigin = `http://127.0.0.1:${basePort}`;
const phoneOrigin = `https://localhost:${basePort + 3}`;
const events = [], errors = [], checkpoints = [];
const redact = value => String(value).replace(/#pair=[^\s"']+/g, '#pair=[redacted]');
const expectedNetworkError = item => item.kind === 'console' &&
  ((item.label === 'phone' && item.message.includes('net::ERR_INTERNET_DISCONNECTED')) ||
   (item.label === 'fresh-home-screen' && item.message.includes('401 (Unauthorized)')));
let server, browser, desktop, phone, context, dataDirectory, failure, stopPerformance;
await mkdir(output, { recursive: true });

function run(command, args, options = {}) {
  return new Promise((resolveRun, reject) => {
    const child = spawn(command, args, { cwd: root, stdio: 'inherit', ...options });
    child.on('error', reject);
    child.on('exit', code => code === 0 ? resolveRun() : reject(new Error(`${command} exited ${code}`)));
  });
}
async function until(predicate, description, timeout = 10000) {
  const start = Date.now();
  while (Date.now() - start < timeout) {
    if (await predicate()) return;
    await delay(50);
  }
  throw new Error(`Timed out: ${description}`);
}
async function local(path, body) {
  const response = await fetch(localOrigin + path, { method: body === undefined ? 'GET' : 'POST',
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(5000) });
  assert.ok(response.ok, `${path}: HTTP ${response.status}`);
  return response.json();
}
async function phoneState(state) {
  await phone.waitForFunction(value => document.body.dataset.state === value, state);
}
async function tracksStopped() {
  await phone.waitForFunction(() => window.__integration.tracks.every(track => track.readyState === 'ended')
    && window.__integration.contexts.every(context => context.state === 'closed'));
}
async function receiverIdle() {
  await until(async () => {
    const status = await local('/local/status');
    return !status.audioStreaming && ['idle', 'error'].includes(status.speech.state);
  }, 'receiver released audio');
}
async function pcmFlow() {
  const previous = await phone.evaluate(() => window.__integration.frames);
  await phone.waitForFunction(before => window.__integration.frames >= before + 12, previous);
  assert.equal(await phone.evaluate(() => window.__integration.tracks.filter(t => t.readyState === 'live').length), 1);
}
async function checkpoint(name, screenshot = true) {
  const state = await phone.evaluate(() => ({ state: document.body.dataset.state, mode: document.body.dataset.mode,
    frames: window.__integration.frames, pcmBytes: window.__integration.bytes,
    frameSizes: [...window.__integration.frameSizes], contextRates: window.__integration.contexts.map(c => c.sampleRate),
    liveTracks: window.__integration.tracks.filter(t => t.readyState === 'live').length,
    openContexts: window.__integration.contexts.filter(c => c.state !== 'closed').length,
    notice: document.querySelector('#notice-message')?.textContent }));
  checkpoints.push({ name, ...state, engine: await local('/local/integration/stats') });
  if (screenshot) await phone.screenshot({ path: join(output, `${name}.png`), fullPage: true });
  console.log(`PASS ${name}`);
}
function watch(page, label) {
  page.on('pageerror', error => errors.push({ label, kind: 'pageerror', message: error.message }));
  page.on('console', message => {
    if (message.type() === 'error') errors.push({ label, kind: 'console', message: message.text() });
  });
  page.on('websocket', socket => {
    // Do not record cookies, pairing material, target IDs, or audio payloads.
    events.push({ label, kind: 'websocket-open', pathname: new URL(socket.url()).pathname });
    socket.on('close', () => events.push({ label, kind: 'websocket-close' }));
  });
}

try {
  let playwright;
  if (process.env.PHONEDECK_PLAYWRIGHT_MODULE) playwright = await import(pathToFileURL(resolve(process.env.PHONEDECK_PLAYWRIGHT_MODULE)));
  else playwright = await import('playwright');
  if (process.env.PHONEDECK_SKIP_HARNESS_BUILD !== '1')
    await run(dotnet, ['build', join(here, 'harness/Harness.csproj'), '-c', 'Release', '--nologo']);
  const assembly = join(here, 'harness/bin/Release/net10.0/PhoneDeck.Desktop.Tests.dll');
  await access(assembly);
  dataDirectory = await mkdtemp(join(output, 'harness-data-'));
  server = spawn(dotnet, [assembly], { cwd: root, stdio: ['ignore', 'pipe', 'pipe'], env: {
    ...process.env, PHONEDECK_INTEGRATION_HARNESS: '1', PHONEDECK_DATA_DIR: dataDirectory,
    PHONEDECK_LOCAL_PORT: String(basePort), PHONEDECK_LAN_PORT: String(basePort + 1), PHONEDECK_WEB_PORT: String(basePort + 3),
    DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_GENERATE_ASPNET_CERTIFICATE: 'false'
  } });
  server.on('error', error => events.push({ kind: 'harness-error', message: error.message }));
  server.stdout.on('data', value => events.push({ kind: 'harness-stdout', message: value.toString() }));
  server.stderr.on('data', value => events.push({ kind: 'harness-stderr', message: value.toString() }));
  await until(async () => {
    if (server.exitCode !== null) throw new Error(`Harness exited ${server.exitCode}: ${JSON.stringify(events)}`);
    try { return (await local('/local/integration/stats')).calls === 0; } catch { return false; }
  }, 'isolated harness started', 20000);
  browser = await playwright.chromium.launch({ headless: true,
    executablePath: process.env.PHONEDECK_BROWSER_EXECUTABLE || undefined,
    args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream', '--ignore-certificate-errors'] });
  context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 390, height: 844 },
    permissions: ['microphone'], reducedMotion: 'reduce' });
  context.setDefaultTimeout(15000);
  const instrumentBrowser = () => {
    const record = window.__integration = { tracks: [], contexts: [], sockets: [], frames: 0, bytes: 0, frameSizes: new Set(), audioErrors: [] };
    const errorAt = (stage, error) => { record.audioErrors.push({ stage, name: error.name, message: error.message, stack: error.stack }); throw error; };
    const getUserMedia = navigator.mediaDevices?.getUserMedia.bind(navigator.mediaDevices);
    if (getUserMedia) navigator.mediaDevices.getUserMedia = async constraints => {
      // Chromium's fake UI overrides permission settings. Fault-inject only this
      // one rejection; every successful capture still uses the real fake device.
      if (record.denyNextPermission) {
        record.denyNextPermission = false;
        errorAt('getUserMedia', new DOMException('Permission denied', 'NotAllowedError'));
      }
      const stream = await getUserMedia(constraints).catch(error => errorAt('getUserMedia', error));
      record.tracks.push(...stream.getTracks());
      return stream;
    };
    const Context = window.AudioContext;
    if (Context) window.AudioContext = new Proxy(Context, { construct(target, args) {
      const context = Reflect.construct(target, args); record.contexts.push(context);
      const addModule = context.audioWorklet.addModule.bind(context.audioWorklet);
      context.audioWorklet.addModule = (...args) => addModule(...args).catch(error => errorAt('addModule', error));
      const source = context.createMediaStreamSource.bind(context);
      context.createMediaStreamSource = (...args) => { try { return source(...args); } catch (error) { errorAt('createMediaStreamSource', error); } };
      return context;
    } });
    const Worklet = window.AudioWorkletNode;
    if (Worklet) window.AudioWorkletNode = new Proxy(Worklet, { construct(target, args) {
      try { return Reflect.construct(target, args); } catch (error) { errorAt('AudioWorkletNode', error); }
    } });
    const Socket = window.WebSocket;
    window.WebSocket = new Proxy(Socket, { construct(target, args) {
      if (record.blockSocket) throw new DOMException('Integration network outage', 'NetworkError');
      const socket = Reflect.construct(target, args); record.sockets.push(socket);
      const send = socket.send.bind(socket);
      socket.send = data => {
        if (data instanceof ArrayBuffer) { record.frames++; record.bytes += data.byteLength; record.frameSizes.add(data.byteLength); }
        return send(data);
      };
      return socket;
    } });
  };
  await context.addInitScript(instrumentBrowser);
  // Separate contexts keep phone visibility unaffected by desktop controls.
  const desktopContext = await browser.newContext({ viewport: { width: 1100, height: 1000 } });
  desktop = await desktopContext.newPage(); watch(desktop, 'desktop');
  await desktop.goto(localOrigin);
  await desktop.locator('#webPair').click();
  await desktop.locator('#webUrls a').first().waitFor({ state: 'attached' });
  const pairUrl = new URL(await desktop.locator('#webUrls a').first().getAttribute('href'));
  phone = await context.newPage(); watch(phone, 'phone');
  await phone.goto(phoneOrigin + pairUrl.pathname + pairUrl.hash);
  await desktop.locator('#webConfirm').waitFor({ state: 'visible' });
  assert.match(await desktop.locator('#webPending').textContent(), /等待确认/);
  await desktop.locator('#webConfirm').click();
  await phoneState('idle');
  await phone.locator('#mic-button').waitFor({ state: 'visible' });
  await phone.waitForFunction(() => !document.querySelector('#mic-button').disabled);
  assert.equal(await phone.evaluate(() => location.hash), '');
  await desktop.locator('#webClients').getByText(/已配对网页/).waitFor();
  await phone.waitForFunction(async () => (await navigator.serviceWorker.getRegistration('/phone/'))?.active?.state === 'activated');
  const cachedPaths = await phone.evaluate(async () => {
    const names = (await caches.keys()).filter(name => name.startsWith('yandu-phone-'));
    return (await Promise.all(names.map(async name => (await (await caches.open(name)).keys()).map(request => new URL(request.url).pathname)))).flat();
  });
  assert.ok(cachedPaths.includes('/phone/') && cachedPaths.includes('/phone/pcm-worklet.js')
    && cachedPaths.includes('/phone/icons/apple-touch-icon.png'));
  const publicFiles = new Set(['', 'style.css', 'app.js', 'ui.js', 'session.js', 'audio.js', 'pcm-worklet.js',
    'manifest.webmanifest', 'icons/icon.svg', 'icons/icon-192.png', 'icons/icon-512.png', 'icons/apple-touch-icon.png']);
  assert.ok(cachedPaths.every(path => path.startsWith('/phone/') && publicFiles.has(path.slice('/phone/'.length))), 'only public static shell cached');
  events.push({ kind: 'service-worker-activated', cachedPaths });
  await checkpoint('01-paired');
  await desktop.screenshot({ path: join(output, 'desktop-paired.png'), fullPage: true });
  if (process.env.PHONEDECK_AUDIO_PROBE === '1') {
    const probe = await phone.evaluate(async () => {
      const values = { devices: (await navigator.mediaDevices.enumerateDevices()).map(d => ({ kind: d.kind, label: d.label })), trials: [] };
      for (const audio of [true, { channelCount: { ideal: 1 }, echoCancellation: false, noiseSuppression: false, autoGainControl: false }]) {
        try { const stream = await navigator.mediaDevices.getUserMedia({ audio, video: false });
          values.trials.push({ audio, settings: stream.getAudioTracks()[0].getSettings() }); stream.getTracks().forEach(t => t.stop());
        } catch (error) { values.trials.push({ audio, name: error.name, message: error.message }); }
      }
      return values;
    });
    console.log(JSON.stringify(probe));
  }

  // Tap -> actual microphone AudioWorklet -> binary WS -> fake engine; PC button stops phone.
  await phone.locator('#mic-button').click(); await phoneState('recording'); await pcmFlow();
  await checkpoint('02-tap-recording');
  await desktop.locator('#toggle').click();
  await phoneState('idle'); await tracksStopped(); await receiverIdle();
  assert.ok((await local('/local/integration/stats')).bytes > 0);
  await checkpoint('03-pc-stop-phone-idle');

  // Phone tap stop must drain the final short PCM frame before ending transport.
  await phone.locator('#mic-button').click(); await phoneState('recording'); await pcmFlow();
  await phone.locator('#mic-button').click(); await phoneState('idle'); await tracksStopped(); await receiverIdle();
  await checkpoint('04-phone-tap-stop');

  await phone.locator('[data-mode-button="hold"]').click();
  const micBounds = await phone.locator('#mic-button').boundingBox();
  await phone.mouse.move(micBounds.x + micBounds.width / 2, micBounds.y + micBounds.height / 2);
  await phone.mouse.down(); await phoneState('recording'); await pcmFlow();
  await checkpoint('05-hold-recording');
  await phone.mouse.up(); await phoneState('idle'); await tracksStopped(); await receiverIdle();
  await checkpoint('06-hold-release');

  await phone.locator('[data-mode-button="shared"]').click();
  await phone.locator('[data-open-dialog="devices-dialog"]').first().click();
  await phone.locator('[data-shared-id]').first().check();
  await phone.locator('#devices-dialog [data-close-dialog]').click();
  await phone.locator('#mic-button').click(); await phoneState('sharing'); await pcmFlow();
  await desktop.locator('#toggle').click();
  await until(async () => (await local('/local/status')).speech.state === 'recording', 'shared desktop segment started');
  await pcmFlow(); await checkpoint('07-shared-pc-recording');
  await desktop.locator('#toggle').click();
  await until(async () => (await local('/local/status')).speech.state === 'idle', 'shared desktop segment stopped');
  await phoneState('sharing'); await pcmFlow();
  assert.equal((await local('/local/status')).audioStreaming, true);
  await checkpoint('08-shared-pc-stop-keeps-mic');
  await phone.locator('#mic-button').click(); await phoneState('idle'); await tracksStopped(); await receiverIdle();
  await checkpoint('09-close-shared');

  await phone.locator('#mic-button').click(); await phoneState('sharing'); await pcmFlow();
  // Hold reconnections off briefly so the disconnected projection is observable.
  // The active stream is a real WS; only newly attempted socket creation is rejected.
  await phone.evaluate(() => { window.__integration.blockSocket = true; window.__integration.sockets.at(-1).close(1000, 'integration shared outage'); });
  await phoneState('idle'); await tracksStopped(); await receiverIdle();
  await phone.waitForFunction(() => [...document.querySelectorAll('#shared-devices .shared-device')].length > 0
    && [...document.querySelectorAll('#shared-devices .shared-device')].every(card => card.dataset.streaming === 'false'
      && card.dataset.recording === 'false' && card.textContent.includes('状态未知') && !card.textContent.includes('供音中')));
  await checkpoint('09b-shared-disconnect-clears-status');
  await phone.evaluate(() => { window.__integration.blockSocket = false; });
  await phone.waitForFunction(() => window.__integration.sockets.at(-1)?.readyState === WebSocket.OPEN
    && !document.querySelector('#mic-button').disabled);
  assert.equal(await phone.evaluate(() => window.__integration.tracks.filter(track => track.readyState === 'live').length), 0);

  // Inject the browser's permission rejection (fake UI always grants native prompts).
  // It must leave no mic/socket operation attached, then recover with real fake audio.
  await phone.locator('[data-mode-button="tap"]').click();
  await phone.evaluate(() => { window.__integration.denyNextPermission = true; });
  await phone.locator('#mic-button').click();
  await phone.waitForFunction(() => window.__integration.audioErrors.some(error => error.name === 'NotAllowedError')
    && document.body.dataset.state === 'idle');
  await tracksStopped(); await receiverIdle(); await checkpoint('10-permission-denied');
  await phone.locator('#mic-button').click(); await phoneState('recording'); await pcmFlow();
  await desktop.locator('#cancel').click(); await phoneState('idle'); await tracksStopped(); await receiverIdle();
  await checkpoint('11-permission-retry-pc-cancel');

  // Close the real socket to exercise transport disconnect and automatic reconnect.
  await phone.locator('#mic-button').click(); await phoneState('recording'); await pcmFlow();
  await phone.evaluate(() => window.__integration.sockets.at(-1).close(1000, 'integration network interruption'));
  await phoneState('idle'); await tracksStopped(); await receiverIdle();
  await phone.waitForFunction(() => window.__integration.sockets.at(-1)?.readyState === WebSocket.OPEN
    && !document.querySelector('#mic-button').disabled);
  await checkpoint('12-socket-disconnect-reconnected');
  await phone.locator('#mic-button').click(); await phoneState('recording'); await pcmFlow();
  // Navigation triggers real pagehide and server WS teardown. No automatic resume.
  await phone.goto('about:blank'); await receiverIdle();
  await phone.goto(phoneOrigin + '/phone/'); await phoneState('idle');
  await tracksStopped(); await checkpoint('13-page-close-reopen-idle');

  // Cold reopening while the receiver is temporarily unreachable must retry auth
  // without a reload, a second user click, or reacquiring the microphone.
  await phone.goto('about:blank');
  let stateRequests = 0;
  await phone.route('**/phone/api/state', route => ++stateRequests <= 2 ? route.abort('internetdisconnected') : route.continue());
  await phone.goto(phoneOrigin + '/phone/');
  await phone.waitForFunction(() => window.__integration.sockets.at(-1)?.readyState === WebSocket.OPEN
    && document.body.dataset.state === 'idle' && !document.querySelector('#mic-button').disabled);
  assert.ok(stateRequests >= 3, 'initial authorization retried after two failures');
  assert.equal(await phone.evaluate(() => window.__integration.tracks.length), 0);
  await tracksStopped(); await checkpoint('14-cold-offline-auto-recovery');
  await phone.unroute('**/phone/api/state');

  // A separately installed home-screen app can have no Safari cookies. Start a
  // fresh browser context and pair by pasting a complete link into the live form.
  await phone.close();
  context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 390, height: 844 },
    permissions: ['microphone'], reducedMotion: 'reduce' });
  context.setDefaultTimeout(15000); await context.addInitScript(instrumentBrowser);
  assert.equal((await context.cookies()).length, 0);
  await desktop.locator('#webPair').click();
  await desktop.locator('#webUrls a').first().waitFor({ state: 'attached' });
  const homePairUrl = new URL(await desktop.locator('#webUrls a').first().getAttribute('href'));
  const sameOriginPairUrl = phoneOrigin + homePairUrl.pathname + homePairUrl.hash;
  phone = await context.newPage(); watch(phone, 'fresh-home-screen');
  await phone.goto(phoneOrigin + '/phone/'); await phoneState('unpaired');
  await phone.locator('[data-open-dialog="devices-dialog"]').first().click();
  await phone.locator('[data-open-dialog="pairing-dialog"]').click();
  await phone.locator('#pairing-code').fill(sameOriginPairUrl);
  await phone.locator('#pairing-submit').click();
  await desktop.locator('#webConfirm').waitFor({ state: 'visible' });
  await desktop.locator('#webConfirm').click();
  await phoneState('idle');
  await phone.waitForFunction(() => !document.querySelector('#mic-button').disabled);
  await desktop.locator('#webPairing').waitFor({ state: 'hidden' });
  assert.equal(await phone.evaluate(() => window.__integration.tracks.length), 0);
  await checkpoint('15-fresh-home-screen-link-pairing');

  // Resource and stop-latency soak. All successful captures use generated native
  // Chromium audio. Time is the observer's PC request dispatch -> tracks/context
  // release, including HTTP/browser automation overhead on this local machine.
  const latencies = [];
  await phone.locator('[data-mode-button="tap"]').click();
  for (let round = 1; round <= 20; round++) {
    await phone.locator('#mic-button').click(); await phoneState('recording'); await pcmFlow();
    const started = performance.now();
    await local('/local/dictation/stop', {});
    await tracksStopped();
    latencies.push(Number((performance.now() - started).toFixed(2)));
    await phoneState('idle'); await receiverIdle();
  }
  const sorted = [...latencies].sort((a, b) => a - b);
  stopPerformance = { rounds: latencies.length, milliseconds: latencies,
    medianMs: Number(((sorted[9] + sorted[10]) / 2).toFixed(2)),
    p95Ms: sorted[Math.ceil(sorted.length * 0.95) - 1], maxMs: sorted.at(-1),
    measurement: 'Local PC stop HTTP dispatch to observed all phone tracks ended and AudioContexts closed, including automation overhead',
    input: 'Chromium generated fake audio; silent fake speech engine; no real phone or recognition',
    residualLiveTracks: await phone.evaluate(() => window.__integration.tracks.filter(track => track.readyState === 'live').length),
    residualOpenContexts: await phone.evaluate(() => window.__integration.contexts.filter(context => context.state !== 'closed').length) };
  assert.equal(stopPerformance.residualLiveTracks, 0); assert.equal(stopPerformance.residualOpenContexts, 0);
  await checkpoint('16-twenty-stop-cycles', false);
  console.log(`PASS 20 stop cycles: median=${stopPerformance.medianMs} ms p95=${stopPerformance.p95Ms} ms max=${stopPerformance.maxMs} ms`);

  assert.ok(checkpoints.some(item => item.frameSizes.includes(1920)), '20 ms PCM frames crossed a real WS');
  assert.ok(checkpoints.filter(item => item.frames).every(item => item.frameSizes.every(size => size > 0 && size <= 1920 && size % 2 === 0)));
  assert.ok(checkpoints.some(item => item.frameSizes.some(size => size < 1920)), 'short tail frame was flushed');
  assert.ok(events.filter(item => item.kind === 'websocket-open').length >= 2, 'socket reconnected');
  assert.deepEqual(errors.filter(item => !expectedNetworkError(item)), [], 'no unexpected browser errors');
} catch (error) {
  failure = { message: redact(error.message), stack: redact(error.stack) };
  if (phone && !phone.isClosed()) failure.audio = await phone.evaluate(() => window.__integration?.audioErrors).catch(() => []);
  console.error(failure.message);
  if (phone && !phone.isClosed()) await phone.screenshot({ path: join(output, 'failure.png'), fullPage: true }).catch(() => {});
} finally {
  if (browser) await browser.close();
  if (server && server.exitCode === null) {
    await local('/local/quit', {}).catch(() => {});
    await until(() => server.exitCode !== null, 'harness exit', 3000).catch(() => server.kill('SIGTERM'));
  }
  // Never preserve pairing credentials or generated private certificate keys as evidence.
  if (dataDirectory) await rm(dataDirectory, { recursive: true, force: true });
  if (!failure) await rm(join(output, 'failure.png'), { force: true });
  await writeFile(join(output, 'results.json'), JSON.stringify({ passed: !failure,
    scope: 'Chromium fake audio + real DesktopApp/HTTPS/WebSocket/AudioWorklet + silent test speech engine',
    limitations: ['No real microphone', 'No iPhone/Safari hardware acceptance', 'No real speech recognition',
      'Permission denial is fault-injected: Chromium fake UI always grants native prompts',
      'Test browser bypasses certificate validation; phone certificate installation is untested'],
    checkpoints, stopPerformance, events, errors: errors.filter(item => !expectedNetworkError(item)),
    expectedNetworkErrors: errors.filter(expectedNetworkError), failure }, null, 2));
}
if (failure) process.exitCode = 1;
