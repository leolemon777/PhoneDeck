import test from 'node:test';
import assert from 'node:assert/strict';
import { VoiceSession, PhoneSocket } from '../PhoneDeck.Desktop/PhoneWeb/session.js';

const deferred = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };
const flush = async () => { for (let i = 0; i < 20; i++) await Promise.resolve(); };
const pcm = (value = 1, size = 1920) => new Uint8Array(size).fill(value).buffer;
function sessionFixture(options = {}) {
  const events = [], notices = [], levels = [], requests = [], sent = [], callbacks = [];
  let captureStops = 0, disconnects = 0;
  const capture = {
    start(callback) { callbacks.push(callback); events.push('capture.start'); return options.captureStart?.(callback) ?? Promise.resolve(); },
    stop() { captureStops++; events.push('capture.stop'); return options.captureStop?.(callbacks.at(-1)) ?? Promise.resolve(); }
  };
  const transport = {
    request(type, payload) {
      events.push(type); const response = deferred(); requests.push({ type, ...payload, response });
      const override = options.request?.(type, payload, response);
      if (override !== undefined) return override;
      if (type === 'stop' || !options.delayedStart) response.resolve({ type: 'ack', sessionId: payload.sessionId });
      return response.promise;
    },
    sendPCM(bytes) { options.sendPCM?.(bytes); sent.push(new Uint8Array(bytes)[0]); events.push(`pcm:${new Uint8Array(bytes)[0]}`); },
    disconnect() { disconnects++; events.push('disconnect'); }
  };
  if (options.waitForPCM) transport.waitForPCM = options.waitForPCM;
  const voice = new VoiceSession({ capture, transport, interrupted: message => notices.push(message),
    level: value => levels.push(value), changed: op => options.changed?.(op, voice) });
  return { voice, capture, transport, events, notices, levels, requests, sent, callbacks,
    get captureStops() { return captureStops; }, get disconnects() { return disconnects; } };
}

test('capture starts synchronously before network and a held-button release cancels delayed permission', async () => {
  const permission = deferred();
  const f = sessionFixture({ captureStart: () => permission.promise,
    captureStop: () => { const error = new Error('canceled'); error.name = 'AbortError'; permission.reject(error); } });
  const start = f.voice.start('managed', ['one']);
  assert.deepEqual(f.events.slice(0, 2), ['capture.start', 'start']);
  const id = f.voice.current.id;
  await f.voice.stop();
  assert.equal(await start, false);
  assert.equal(f.captureStops, 1); assert.equal(f.voice.busy, false);
  assert.equal(f.requests.at(-1).sessionId, id);
  assert.equal(f.requests.at(-1).cancel, true);
});

test('desktop stop before delayed start ACK cannot resurrect the operation', async () => {
  const f = sessionFixture({ delayedStart: true });
  const start = f.voice.start('managed', ['one']), op = f.voice.current;
  assert.equal(f.voice.remoteStopped(op.id), true);
  await op.stop;
  f.requests[0].response.resolve({ type: 'ack', sessionId: op.id });
  assert.equal(await start, false);
  assert.equal(f.voice.busy, false); assert.equal(f.captureStops, 1);
});

test('old replies, audio callbacks and stopped events cannot end a new operation', async () => {
  const f = sessionFixture({ delayedStart: true });
  const firstStart = f.voice.start('managed', ['one']), old = f.voice.current;
  await f.voice.stop({ cancel: true });
  const secondStart = f.voice.start('managed', ['two']), current = f.voice.current;
  const secondRequest = f.requests.find(item => item.type === 'start' && item.sessionId === current.id);
  secondRequest.response.resolve({ sessionId: current.id }); await secondStart;
  f.requests[0].response.reject(new Error('old timeout')); await firstStart;
  f.callbacks[0].onChunk(pcm(7)); f.callbacks[0].onInterrupted('old failure');
  assert.equal(f.voice.remoteStopped(old.id), false);
  assert.equal(f.voice.current, current); assert.equal(current.phase, 'recording');
  assert.deepEqual(f.sent, []);
  await f.voice.stop();
});

test('stop flushes capture tail and queued audio in order before protocol stop', async () => {
  const drain = deferred(); let block = true;
  const f = sessionFixture({ captureStop: callback => callback.onChunk(pcm(3, 14)),
    waitForPCM: () => block ? drain.promise : Promise.resolve() });
  await f.voice.start('managed', ['one']);
  f.callbacks[0].onChunk(pcm(1)); f.callbacks[0].onChunk(pcm(2));
  const stopping = f.voice.stop();
  await flush(); assert.equal(f.requests.filter(item => item.type === 'stop').length, 0);
  block = false; drain.resolve(); await stopping;
  assert.deepEqual(f.sent, [1, 2, 3]);
  assert.ok(f.events.indexOf('pcm:3') < f.events.indexOf('stop'));
  assert.equal(f.requests.at(-1).cancel, false); assert.equal(f.levels.at(-1), 0);
});

test('stop command failure always disconnects and leaves microphone/UI idle', async () => {
  const f = sessionFixture({ request: type => type === 'stop' ? Promise.reject(new Error('语音收尾未确认')) : undefined });
  await f.voice.start('managed', ['one']); await f.voice.stop();
  assert.equal(f.captureStops, 1); assert.equal(f.disconnects, 1);
  assert.equal(f.voice.current, null); assert.equal(f.levels.at(-1), 0);
  assert.match(f.notices.at(-1), /语音收尾未确认/);
});

test('a failed tail send is canceled/disconnected, never silently submitted as success', async () => {
  const f = sessionFixture({ captureStop: callback => callback.onChunk(pcm(9, 14)),
    sendPCM: bytes => { if (new Uint8Array(bytes)[0] === 9) throw new Error('tail failed'); } });
  await f.voice.start('managed', ['one']); await f.voice.stop();
  assert.ok(f.disconnects >= 1);
  assert.equal(f.requests.some(item => item.type === 'stop' && item.cancel === false), false);
  assert.equal(f.voice.current, null);
});

test('startup buffer is bounded to one second and overflow explicitly cancels', async () => {
  const f = sessionFixture({ delayedStart: true });
  const start = f.voice.start('managed', ['one']), op = f.voice.current;
  for (let i = 0; i < 50; i++) f.callbacks[0].onChunk(pcm(i));
  assert.equal(op.bytes, 96000);
  f.callbacks[0].onChunk(pcm(51));
  assert.ok(op.bytes <= 96000);
  await op.stop;
  assert.equal(f.requests.at(-1).cancel, true); assert.equal(op.bytes, 0);
  assert.equal(op.queue.length, 0); assert.equal(f.voice.busy, false);
  f.requests[0].response.resolve({}); assert.equal(await start, false);
});

test('cancel upgrades a stop already waiting for capture tail; repeated stop is idempotent', async () => {
  const tail = deferred(); const f = sessionFixture({ captureStop: () => tail.promise });
  await f.voice.start('managed', ['one']);
  const first = f.voice.stop(), second = f.voice.stop({ cancel: true });
  assert.equal(first, second);
  f.callbacks[0].onChunk(pcm()); tail.resolve(); await first;
  assert.equal(f.captureStops, 1); assert.equal(f.requests.at(-1).cancel, true); assert.deepEqual(f.sent, []);
});

test('concurrent start and reentrant stop do not acquire a second microphone', async () => {
  const f = sessionFixture({ changed: (op, voice) => { if (op?.phase === 'stopping') void voice.stop(); } });
  await f.voice.start('shared', ['one', 'two']);
  assert.equal(await f.voice.start('managed', ['one']), false);
  await f.voice.stop();
  assert.equal(f.callbacks.length, 1); assert.equal(f.captureStops, 1);
});

class FakeClock {
  time = 0; next = 1; timers = new Map();
  setTimeout = (callback, delay = 0) => this.add(callback, delay, 0);
  clearTimeout = id => this.timers.delete(id);
  setInterval = (callback, delay) => this.add(callback, delay, delay);
  clearInterval = id => this.timers.delete(id);
  add(callback, delay, repeat) { const id = this.next++; this.timers.set(id, { callback, at: this.time + delay, repeat }); return id; }
  async advance(amount) {
    const end = this.time + amount;
    while (true) {
      const next = [...this.timers].filter(([, timer]) => timer.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
      if (!next) break;
      const [id, timer] = next; this.time = timer.at;
      if (timer.repeat) timer.at += timer.repeat; else this.timers.delete(id);
      timer.callback(); await flush();
    }
    this.time = end; await flush();
  }
}
function socketFixture(options = {}) {
  const instances = [], states = [], stopped = [], connections = [], clock = new FakeClock();
  class Socket {
    readyState = 0; bufferedAmount = 0; sent = []; closeCount = 0;
    constructor(url) { this.url = url; instances.push(this); }
    send(message) {
      if (options.throwSend) throw new Error('send failed');
      this.sent.push(message);
      if (message instanceof ArrayBuffer) this.bufferedAmount += message.byteLength;
    }
    close() { this.closeCount++; this.readyState = 3; /* Intentionally no browser onclose callback. */ }
    open() { this.readyState = 1; this.onopen?.(); }
    message(message) { this.onmessage?.({ data: typeof message === 'string' ? message : JSON.stringify(message) }); }
    requests() { return this.sent.filter(item => typeof item === 'string').map(item => JSON.parse(item)); }
  }
  const transport = new PhoneSocket({ Socket, clock, now: () => clock.time, url: 'wss://example.test/phone/socket',
    state: state => { states.push(state); options.state?.(state); },
    stopped: message => { stopped.push(message); options.stopped?.(message); },
    connection: state => { connections.push(state); options.connection?.(state); } });
  transport.connect(); instances[0].open();
  return { transport, clock, instances, states, stopped, connections, get socket() { return instances.at(-1); } };
}

test('microphone stops immediately while a healthy connection waits for receiver tail drain', async () => {
  const f = socketFixture(), socket = f.socket; let captureStops = 0; const notices = [];
  const voice = new VoiceSession({
    capture: { start: async () => {}, stop: async () => { captureStops++; } },
    transport: f.transport, interrupted: message => notices.push(message)
  });
  const starting = voice.start('managed', ['one']); await flush();
  const start = f.socket.requests().find(item => item.type === 'start');
  f.socket.message({ type: 'ack', requestId: start.requestId, sessionId: start.sessionId });
  await starting;
  const stopping = voice.stop(); await flush();
  assert.equal(captureStops, 1);
  await f.clock.advance(2500);
  const ping = f.socket.requests().find(item => item.type === 'ping');
  f.socket.message({ type: 'pong', requestId: ping.requestId });
  await f.clock.advance(1000);
  assert.equal(socket.closeCount, 0, 'valid tail drain must not trigger the old three-second timeout');
  const stop = f.socket.requests().find(item => item.type === 'stop');
  f.socket.message({ type: 'ack', requestId: stop.requestId, sessionId: stop.sessionId, state: 'stopped' });
  await stopping;
  assert.equal(voice.busy, false); assert.deepEqual(notices, []);
  f.transport.disconnect();
});

test('server pong settles heartbeat ping without closing a healthy connection', async () => {
  const f = socketFixture();
  await f.clock.advance(2500);
  const ping = f.socket.requests().at(-1); assert.equal(ping.type, 'ping');
  f.socket.message({ type: 'pong', requestId: ping.requestId }); await flush();
  assert.equal(f.transport.pending.size, 0); assert.equal(f.transport.pingInFlight, false);
  await f.clock.advance(2500);
  const next = f.socket.requests().at(-1);
  f.socket.message({ type: 'pong', requestId: next.requestId }); await flush();
  assert.equal(f.instances.length, 1); assert.equal(f.transport.connected, true);
  f.transport.disconnect({ reconnect: false }); assert.equal(f.clock.timers.size, 0);
});

test('request timeout rejects once; late reply does not resolve another request', async () => {
  const f = socketFixture();
  const first = f.transport.request('select', { targetId: 'one' }, 20), old = f.socket.requests().at(-1);
  const rejected = assert.rejects(first, /超时/); await f.clock.advance(20); await rejected;
  const second = f.transport.request('select', { targetId: 'two' }, 20), fresh = f.socket.requests().at(-1);
  f.socket.message({ type: 'ack', requestId: old.requestId });
  assert.equal(f.transport.pending.size, 1);
  f.socket.message({ type: 'ack', requestId: fresh.requestId, targetId: 'two' });
  assert.equal((await second).targetId, 'two');
  f.transport.disconnect({ reconnect: false });
});

test('wrong-session ACK and unrelated pong cannot acknowledge a start', async () => {
  const f = socketFixture();
  const requested = f.transport.request('start', { sessionId: 'current', type: 'injected', requestId: 'injected' });
  const wire = f.socket.requests().at(-1);
  assert.equal(wire.type, 'start'); assert.notEqual(wire.requestId, 'injected');
  f.socket.message({ type: 'ack', requestId: wire.requestId, sessionId: 'old' });
  f.socket.message({ type: 'pong', requestId: wire.requestId });
  assert.equal(f.transport.pending.size, 1);
  f.socket.message({ type: 'ack', requestId: wire.requestId, sessionId: 'current' }); await requested;
  f.transport.disconnect({ reconnect: false });
});

test('disconnect cleans pending requests immediately even when native onclose never arrives', async () => {
  const f = socketFixture();
  const pending = f.transport.request('start', { sessionId: 'one' });
  const rejected = assert.rejects(pending, /已断开/);
  f.transport.disconnect({ reconnect: false }); await rejected;
  assert.equal(f.transport.socket, null); assert.equal(f.transport.pending.size, 0);
  assert.equal(f.clock.timers.size, 0); assert.equal(f.socket.closeCount, 1);
  await f.clock.advance(10000); assert.equal(f.instances.length, 1);
});

test('stale socket events and rejected old heartbeat cannot disconnect a new socket', async () => {
  const f = socketFixture();
  await f.clock.advance(2500);
  const old = f.socket, oldMessage = old.onmessage, oldClose = old.onclose;
  f.transport.disconnect();
  f.transport.connect(); f.socket.open(); await flush();
  const current = f.socket;
  oldMessage({ data: JSON.stringify({ type: 'stopped', sessionId: 'old' }) }); oldClose();
  await f.clock.advance(500);
  assert.equal(f.transport.socket, current); assert.equal(f.transport.connected, true);
  assert.equal(f.stopped.length, 0); assert.equal(f.instances.length, 2);
  f.transport.disconnect({ reconnect: false });
});

test('missing pong times out, reconnects once, and manual disconnect stops retries', async () => {
  const f = socketFixture();
  await f.clock.advance(6500); assert.equal(f.transport.socket, null);
  await f.clock.advance(500); assert.equal(f.instances.length, 2);
  assert.equal(f.connections.filter(state => state === 'disconnected').length, 1);
  f.transport.disconnect({ reconnect: false }); await f.clock.advance(20000);
  assert.equal(f.instances.length, 2); assert.equal(f.clock.timers.size, 0);
});

test('opening handshake also has a timeout and malformed messages close safely', async () => {
  const f = socketFixture();
  f.transport.disconnect(); await f.clock.advance(500);
  const hanging = f.socket;
  await f.clock.advance(8000); assert.equal(hanging.closeCount, 1);
  await f.clock.advance(1000); f.socket.open();
  f.socket.message('null'); assert.equal(f.transport.socket, null);
  f.transport.disconnect({ reconnect: false });
});

test('200 ms socket buffer includes the next frame and a stalled drain rejects', async () => {
  const f = socketFixture();
  for (let i = 0; i < 10; i++) f.transport.sendPCM(pcm());
  assert.equal(f.socket.bufferedAmount, 19200);
  assert.throws(() => f.transport.sendPCM(pcm()), /网络拥堵/);
  const waiting = f.transport.waitForPCM(1920, 24), rejected = assert.rejects(waiting, /网络拥堵/);
  await f.clock.advance(24); await rejected;
  f.transport.disconnect({ reconnect: false });
});

test('one second of pre-roll drains in order without overflowing a healthy 200 ms socket window', async () => {
  const f = socketFixture(); let callbacks;
  const capture = { start: value => { callbacks = value; return Promise.resolve(); }, stop: () => Promise.resolve() };
  const voice = new VoiceSession({ capture, transport: f.transport });
  const started = voice.start('managed', ['one']);
  for (let i = 0; i < 50; i++) callbacks.onChunk(pcm(i));
  const request = f.socket.requests().at(-1);
  f.socket.message({ type: 'ack', requestId: request.requestId, sessionId: request.sessionId }); await flush();
  for (let i = 0; i < 6; i++) {
    assert.ok(f.socket.bufferedAmount <= 19200);
    f.socket.bufferedAmount = 0; await f.clock.advance(8);
  }
  assert.equal(await started, true);
  assert.deepEqual(f.socket.sent.filter(item => item instanceof ArrayBuffer).map(bytes => new Uint8Array(bytes)[0]), [...Array(50).keys()]);
  const stopping = voice.stop(); await flush();
  const stop = f.socket.requests().at(-1); assert.equal(stop.type, 'stop');
  f.socket.message({ type: 'ack', requestId: stop.requestId, sessionId: stop.sessionId }); await stopping;
  f.transport.disconnect({ reconnect: false });
});

test('shared per-computer stop state preserves supply; only whole-session stopped closes it', async () => {
  let voice;
  const f = socketFixture({ stopped: message => voice.remoteStopped(message.sessionId, message.reason) });
  let captureStops = 0;
  voice = new VoiceSession({ capture: { start: () => Promise.resolve(), stop: () => { captureStops++; return Promise.resolve(); } }, transport: f.transport });
  const started = voice.start('shared', ['one', 'two']), id = voice.current.id;
  const start = f.socket.requests().at(-1);
  f.socket.message({ type: 'ack', requestId: start.requestId, sessionId: id }); await started;
  f.socket.message({ type: 'state', targets: [{ id: 'one', recording: false, streaming: true }, { id: 'two', recording: true, streaming: true }] });
  assert.equal(captureStops, 0); assert.equal(voice.current.phase, 'sharing');
  f.socket.message({ type: 'stopped', sessionId: 'previous-managed' });
  assert.equal(captureStops, 0);
  f.socket.message({ type: 'stopped', sessionId: id, reason: '全部电脑离线' });
  const stopPromise = voice.current.stop; await flush();
  const stop = f.socket.requests().at(-1);
  f.socket.message({ type: 'ack', requestId: stop.requestId, sessionId: id }); await stopPromise;
  assert.equal(captureStops, 1); assert.equal(voice.busy, false);
  f.transport.disconnect({ reconnect: false });
});

test('pagehide/manual disable remains disabled when an in-flight voice stop fails', async () => {
  const f = socketFixture();
  const voice = new VoiceSession({ capture: { start: () => Promise.resolve(), stop: () => Promise.resolve() }, transport: f.transport });
  const started = voice.start('managed', ['one']);
  const start = f.socket.requests().at(-1);
  f.socket.message({ type: 'ack', requestId: start.requestId, sessionId: start.sessionId }); await started;
  const stopping = voice.stop({ cancel: true });
  f.transport.disconnect({ reconnect: false });
  await stopping; await f.clock.advance(20000);
  assert.equal(f.transport.enabled, false);
  assert.equal(f.instances.length, 1); assert.equal(f.clock.timers.size, 0);
  assert.equal(voice.busy, false);
});

test('a connection callback can disable the socket without leaving a heartbeat behind', async () => {
  const f = socketFixture();
  f.transport.disconnect({ reconnect: false });
  f.transport.connection = state => { if (state === 'connected') f.transport.disconnect({ reconnect: false }); };
  f.transport.enabled = true; f.transport.connect(); f.socket.open();
  assert.equal(f.transport.socket, null); assert.equal(f.clock.timers.size, 0);
  await f.clock.advance(20000); assert.equal(f.instances.length, 2);
});
