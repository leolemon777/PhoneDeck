// A unique operation owns capture, startup, bounded PCM and stop. Replies and
// callbacks from an older operation can never change the next operation.
const quiet = (callback, ...args) => { try { callback?.(...args); } catch { /* UI does not own resources. */ } };

export class VoiceSession {
  constructor({ capture, transport, changed = () => {}, interrupted = () => {}, level = () => {} }) {
    Object.assign(this, { capture, transport, changed, interrupted, level });
    this.current = null;
  }
  get busy() { return this.current !== null; }
  notify() { quiet(this.changed, this.current); }
  start(mode, targetIds) {
    if (this.current) return Promise.resolve(false);
    const op = { id: crypto.randomUUID(), mode, targetIds: [...targetIds], phase: 'starting', ready: false,
      queue: [], bytes: 0, stopping: false, flushing: false, cancelRequested: false, pump: null,
      startedAt: performance.now() };
    this.current = op; this.notify();
    if (op.stopping) return Promise.resolve(false);
    let capture, remote;
    try {
      // Before every await: preserve Safari user activation even if the network is slow.
      capture = Promise.resolve(this.capture.start({
        onChunk: bytes => this.chunk(op, bytes), onLevel: value => { if (this.current === op) quiet(this.level, value); },
        onInterrupted: reason => { if (this.current === op && !op.stopping) this.fail(op, reason); }
      }));
      capture.catch(() => {});
      remote = Promise.resolve(this.transport.request('start', { sessionId: op.id, mode, targetIds: op.targetIds }));
    } catch (error) {
      this.fail(op, error.message || '启动未完成，请重试');
      return Promise.resolve(false);
    }
    op.start = Promise.all([capture, remote]).then(async () => {
      if (this.current !== op || op.stopping) return false;
      op.ready = true;
      op.phase = mode === 'shared' ? 'sharing' : 'recording';
      op.readyAt = performance.now(); this.notify();
      await this.pump(op);
      return this.current === op && !op.stopping;
    }).catch(error => {
      if (this.current === op && !op.stopping) this.fail(op, error.message || '启动未完成，请重试');
      return false;
    });
    return op.start;
  }
  chunk(op, bytes) {
    if (this.current !== op || (op.stopping && !op.flushing)) return;
    if (!(bytes instanceof ArrayBuffer) || bytes.byteLength < 2 || bytes.byteLength > 1920 || bytes.byteLength % 2) {
      this.audioFailed(op, '音频帧无效，本段已取消'); return;
    }
    // Preserve first words or explicitly cancel. Never silently discard arbitrary
    // audio while waiting for a slow connection, and never grow beyond one second.
    if (op.bytes + bytes.byteLength > 96000) {
      this.audioFailed(op, '电脑连接较慢，本段已取消，请重新开始'); return;
    }
    op.queue.push(bytes); op.bytes += bytes.byteLength;
    if (op.ready) void this.pump(op).catch(() => {});
  }
  pump(op) {
    if (op.pump) return op.pump;
    if (!op.ready || op.cancelRequested || this.current !== op) return Promise.resolve();
    // Start on a microtask so op.pump is assigned before failure/stop callbacks run.
    const pumping = Promise.resolve().then(async () => {
      while (this.current === op && !op.cancelRequested && op.queue.length) {
        const bytes = op.queue[0];
        // A pre-roll burst can exceed the socket's live-audio limit. Wait for bounded
        // writable space before each frame instead of treating that burst as failure.
        if (this.transport.waitForPCM) await this.transport.waitForPCM(bytes.byteLength);
        if (this.current !== op || op.cancelRequested) break;
        this.transport.sendPCM(bytes);
        op.queue.shift(); op.bytes -= bytes.byteLength;
      }
    });
    op.pump = pumping;
    pumping.then(() => { if (op.pump === pumping) op.pump = null; }, error => {
      if (op.pump === pumping) op.pump = null;
      this.audioFailed(op, error.message || '音频发送未完成，本段已取消');
    });
    return pumping;
  }
  audioFailed(op, message) {
    if (this.current !== op) return;
    op.cancelRequested = true; op.flushing = false;
    if (op.stopping) {
      quiet(this.interrupted, message);
      this.transport.disconnect({ reconnect: this.transport.enabled !== false });
    } else this.fail(op, message);
  }
  fail(op, message) {
    if (this.current !== op || op.stopping) return;
    quiet(this.interrupted, message);
    void this.stop({ cancel: true });
  }
  remoteStopped(sessionId, reason = '电脑端已停止，手机已同步停录') {
    if (this.current?.id !== sessionId || this.current.stopping) return false;
    // Only a whole-session event comes here. Shared per-computer recording state
    // is a normal state update, so one computer stopping never closes shared supply.
    quiet(this.interrupted, reason);
    void this.stop({ cancel: false }); return true;
  }
  stop({ cancel = false } = {}) {
    const op = this.current;
    if (!op) return Promise.resolve();
    op.cancelRequested ||= cancel || !op.ready;
    if (op.cancelRequested) op.flushing = false;
    if (op.stop) return op.stop;
    let finish;
    op.stop = new Promise(resolve => { finish = resolve; });
    op.stopping = true; op.flushing = op.ready && !op.cancelRequested; op.phase = 'stopping'; this.notify();
    void (async () => {
      try {
        // AudioCapture delivers its final short frame before resolving stop.
        await this.capture.stop(); op.flushing = false;
        if (!op.cancelRequested) {
          await this.pump(op);
          // Wait until queued binary frames have left the browser before protocol stop.
          if (this.transport.waitForPCM) await this.transport.waitForPCM(19200, 1000);
        }
        // The gateway allows 15s for receiver drain and engine-stop confirmation.
        // Capture is already closed; only this operation's acknowledgement waits.
        await this.transport.request('stop', { sessionId: op.id, cancel: op.cancelRequested }, 16000);
      } catch (error) {
        // Never leave an uncertain remote session attached to a reusable socket.
        this.transport.disconnect({ reconnect: this.transport.enabled !== false });
        quiet(this.interrupted, `${error?.message || '语音收尾未确认'}；手机已停止，重新连接后可再次开始`);
      } finally {
        for (const bytes of op.queue) new Uint8Array(bytes).fill(0);
        op.queue.length = 0; op.bytes = 0;
        if (this.current === op) { this.current = null; quiet(this.level, 0); this.notify(); }
      }
    })().then(finish, finish);
    return op.stop;
  }
}

export class PhoneSocket {
  constructor({ state = () => {}, stopped = () => {}, connection = () => {}, url,
    Socket = globalThis.WebSocket, clock = globalThis, now = () => Date.now() }) {
    Object.assign(this, { state, stopped, connection, url, Socket, clock, now });
    this.pending = new Map(); this.socket = null; this.generation = 0; this.enabled = true;
    this.retry = 0; this.timer = null; this.lastReceived = 0; this.heartbeat = null;
    this.connectTimer = null; this.pingInFlight = false;
  }
  get connected() { return this.socket?.readyState === 1; }
  isCurrent(socket, generation) { return this.socket === socket && this.generation === generation; }
  connect() {
    if (!this.enabled || this.socket) return;
    this.clock.clearTimeout(this.timer); this.timer = null;
    const generation = ++this.generation;
    let socket;
    try { socket = new this.Socket(this.url); }
    catch { quiet(this.connection, 'disconnected'); this.scheduleReconnect(); return; }
    this.socket = socket; quiet(this.connection, 'connecting');
    if (!this.isCurrent(socket, generation)) return;
    this.connectTimer = this.clock.setTimeout(() => {
      if (this.isCurrent(socket, generation)) this.disconnect();
    }, 8000);
    socket.onopen = () => {
      if (!this.isCurrent(socket, generation)) return;
      this.clock.clearTimeout(this.connectTimer); this.connectTimer = null;
      this.retry = 0; this.lastReceived = this.now(); quiet(this.connection, 'connected');
      if (!this.isCurrent(socket, generation)) return;
      this.heartbeat = this.clock.setInterval(() => {
        if (!this.isCurrent(socket, generation)) return;
        if (this.now() - this.lastReceived > 8000) this.disconnect();
        else if (this.connected && !this.pingInFlight) {
          this.pingInFlight = true;
          this.request('ping', {}, 4000).then(() => {
            if (this.isCurrent(socket, generation)) this.pingInFlight = false;
          }, () => { if (this.isCurrent(socket, generation)) this.disconnect(); });
        }
      }, 2500);
    };
    socket.onmessage = event => {
      if (!this.isCurrent(socket, generation) || typeof event.data !== 'string') return;
      let message; try { message = JSON.parse(event.data); } catch { this.disconnect(); return; }
      if (!message || typeof message !== 'object' || typeof message.type !== 'string') { this.disconnect(); return; }
      this.lastReceived = this.now();
      if (message.type === 'state') quiet(this.state, message);
      else if (message.type === 'stopped') quiet(this.stopped, message);
      else if (message.type === 'ack' || message.type === 'pong' || message.type === 'error') {
        const pending = this.pending.get(message.requestId);
        if (!pending) {
          if (message.type === 'error' && !message.requestId) this.disconnect();
          return;
        }
        if (message.type === 'pong' && pending.type !== 'ping') return;
        if (message.type === 'ack' && pending.sessionId && pending.sessionId !== message.sessionId) return;
        this.pending.delete(message.requestId); this.clock.clearTimeout(pending.timer);
        if (message.type === 'error') pending.reject(new Error(message.error || '电脑未完成操作'));
        else pending.resolve(message);
      }
    };
    socket.onerror = () => { if (this.isCurrent(socket, generation)) this.disconnect(); };
    socket.onclose = () => { if (this.isCurrent(socket, generation)) this.lose(socket); };
  }
  scheduleReconnect() {
    if (this.enabled && !this.socket && this.timer === null)
      this.timer = this.clock.setTimeout(() => { this.timer = null; this.connect(); }, Math.min(8000, 500 * 2 ** this.retry++));
  }
  lose(socket) {
    if (this.socket !== socket) return;
    this.socket = null; this.generation++;
    this.clock.clearInterval(this.heartbeat); this.heartbeat = null;
    this.clock.clearTimeout(this.connectTimer); this.connectTimer = null; this.pingInFlight = false;
    socket.onopen = socket.onmessage = socket.onerror = socket.onclose = null;
    try { socket.close(); } catch { /* Local teardown does not depend on browser onclose. */ }
    for (const pending of this.pending.values()) {
      this.clock.clearTimeout(pending.timer); pending.reject(new Error('电脑连接已断开'));
    }
    this.pending.clear(); quiet(this.connection, 'disconnected'); this.scheduleReconnect();
  }
  request(type, payload = {}, timeout = 8000) {
    if (!this.connected) return Promise.reject(new Error('电脑尚未连接，请等待连接完成'));
    const requestId = crypto.randomUUID();
    return new Promise((resolve, reject) => {
      const timer = this.clock.setTimeout(() => { this.pending.delete(requestId); reject(new Error('电脑响应超时，请检查连接')); }, timeout);
      this.pending.set(requestId, { resolve, reject, timer, type, sessionId: payload.sessionId });
      try { this.socket.send(JSON.stringify({ ...payload, type, requestId })); }
      catch (error) { this.clock.clearTimeout(timer); this.pending.delete(requestId); reject(error); }
    });
  }
  waitForPCM(byteLength = 1920, timeout = 250) {
    const socket = this.socket, generation = this.generation, deadline = this.now() + timeout;
    return new Promise((resolve, reject) => {
      const check = () => {
        if (!this.isCurrent(socket, generation) || !this.connected) { reject(new Error('音频连接已中断')); return; }
        if (socket.bufferedAmount + byteLength <= 19200) { resolve(); return; }
        if (this.now() >= deadline) { reject(new Error('网络拥堵，本段已停止，请靠近路由器后重试')); return; }
        this.clock.setTimeout(check, 8);
      };
      check();
    });
  }
  sendPCM(bytes) {
    if (!this.connected) throw new Error('音频连接已中断');
    if (!(bytes instanceof ArrayBuffer) || bytes.byteLength < 2 || bytes.byteLength > 1920 || bytes.byteLength % 2)
      throw new Error('音频帧无效');
    if (this.socket.bufferedAmount + bytes.byteLength > 19200) throw new Error('网络拥堵，本段已停止，请靠近路由器后重试');
    this.socket.send(bytes);
  }
  disconnect({ reconnect = true } = {}) {
    this.enabled = reconnect; this.clock.clearTimeout(this.timer); this.timer = null;
    if (this.socket) this.lose(this.socket);
    else if (reconnect) this.connect();
  }
}
