export const PCM_RATE = 48000;
export const PCM_FRAME_SAMPLES = 960;

/** Streaming mono conversion/resampling. Phase survives arbitrary render-block boundaries. */
export class PcmEncoder {
  constructor(inputRate, onChunk, onLevel = () => {}) {
    if (!Number.isFinite(inputRate) || inputRate < 8000 || inputRate > 384000)
      throw new RangeError("Unsupported microphone sample rate");
    this.inputRate = inputRate;
    this.onChunk = onChunk;
    this.onLevel = onLevel;
    this.frames = 0;
    this.nextPosition = 0;
    this.previous = 0;
    this.closed = false;
    this.levelSum = 0;
    this.levelCount = 0;
    this.levelEvery = Math.max(1, Math.round(inputRate / 10));
    this.buffer = new ArrayBuffer(PCM_FRAME_SAMPLES * 2);
    this.view = new DataView(this.buffer);
    this.used = 0;
  }

  push(channels) {
    if (this.closed || !channels?.length || !channels[0]?.length) return;
    const length = channels[0].length;
    for (let offset = 0; offset < length; offset++) {
      let value = 0;
      for (const channel of channels) {
        const sample = channel[offset];
        value += Number.isFinite(sample) ? Math.max(-1, Math.min(1, sample)) : 0;
      }
      value /= channels.length;
      const index = this.frames++;
      // Integer-rate numerator avoids accumulating a fractional phase per block.
      while (this.nextPosition <= index * PCM_RATE) {
        const fraction = this.nextPosition / PCM_RATE - (index - 1);
        this.write(index === 0 ? value : this.previous + (value - this.previous) * fraction);
        this.nextPosition += this.inputRate;
      }
      this.previous = value;
      this.levelSum += value * value;
      if (++this.levelCount >= this.levelEvery) {
        this.onLevel(Math.min(1, Math.sqrt(this.levelSum / this.levelCount)));
        this.levelSum = this.levelCount = 0;
      }
    }
  }

  write(value) {
    value = Math.max(-1, Math.min(1, value));
    this.view.setInt16(this.used * 2, Math.round(value * (value < 0 ? 32768 : 32767)), true);
    if (++this.used === PCM_FRAME_SAMPLES) this.emit();
  }

  emit() {
    if (!this.used) return;
    const chunk = this.used === PCM_FRAME_SAMPLES ? this.buffer : this.buffer.slice(0, this.used * 2);
    this.buffer = new ArrayBuffer(PCM_FRAME_SAMPLES * 2);
    this.view = new DataView(this.buffer);
    this.used = 0;
    this.onChunk(chunk);
  }

  flush() {
    if (this.closed) return;
    this.closed = true;
    // Extend only the final fractional sample, never pad a short frame with silence.
    while (this.nextPosition < this.frames * PCM_RATE) {
      this.write(this.previous);
      this.nextPosition += this.inputRate;
    }
    this.emit();
    this.previous = this.levelSum = this.levelCount = 0;
  }
}

/** Takes ownership of chunks; only the newest <= 1 second stays in memory. */
export class PcmPreRollQueue {
  constructor(milliseconds = 1000) {
    if (!Number.isFinite(milliseconds) || milliseconds < 0 || milliseconds > 1000)
      throw new RangeError("Pre-roll must be between 0 and 1000 milliseconds");
    this.maxBytes = Math.floor(PCM_RATE * milliseconds / 1000) * 2;
    this.chunks = [];
    this.byteLength = 0;
  }
  push(chunk) {
    if (!(chunk instanceof ArrayBuffer) || chunk.byteLength % 2)
      throw new TypeError("Expected PCM16 ArrayBuffer");
    if (!chunk.byteLength || !this.maxBytes) return;
    if (chunk.byteLength > this.maxBytes) chunk = chunk.slice(chunk.byteLength - this.maxBytes);
    this.chunks.push(chunk);
    this.byteLength += chunk.byteLength;
    while (this.byteLength > this.maxBytes) {
      const excess = this.byteLength - this.maxBytes;
      const first = this.chunks[0];
      if (first.byteLength <= excess) new Uint8Array(this.shift()).fill(0);
      else {
        this.chunks[0] = first.slice(excess);
        new Uint8Array(first).fill(0);
        this.byteLength -= excess;
      }
    }
  }
  shift() {
    const chunk = this.chunks.shift();
    if (chunk) this.byteLength -= chunk.byteLength;
    return chunk;
  }
  drain() {
    const result = this.chunks;
    this.chunks = [];
    this.byteLength = 0;
    return result;
  }
  clear() {
    for (const chunk of this.drain()) new Uint8Array(chunk).fill(0);
  }
}

function aborted() {
  const error = new Error("已取消麦克风启动");
  error.name = "AbortError";
  return error;
}

function stopTracks(stream) {
  for (const track of stream?.getTracks() || []) {
    try { track.stop(); } catch { /* Continue releasing the other tracks. */ }
  }
}

/**
 * Call start directly inside the user gesture, before awaiting network work.
 * onChunk receives 48 kHz little-endian PCM16 mono ArrayBuffers (normally 20 ms).
 * Await stop before closing the audio transport: it delivers the final short frame.
 * Background/suspension interrupts capture; returning to the page never restarts it.
 */
export class AudioCapture {
  constructor({ platform = globalThis, workletUrl = new URL("./pcm-worklet.js", import.meta.url),
    flushTimeoutMs = 250, setupTimeoutMs = 10000 } = {}) {
    this.platform = platform;
    this.workletUrl = workletUrl;
    this.flushTimeoutMs = flushTimeoutMs;
    this.setupTimeoutMs = setupTimeoutMs;
    this.state = "idle";
    this.run = null;
    this.pendingPermissions = 0;
  }

  async start({ onChunk, onLevel = () => {}, onInterrupted = () => {}, onStateChange = () => {} } = {}) {
    if (this.run || this.pendingPermissions) throw new Error("麦克风正在启动或收尾，请稍后再试");
    if (typeof onChunk !== "function") throw new TypeError("onChunk is required");
    const p = this.platform;
    const Context = p.AudioContext || p.webkitAudioContext;
    if (p.isSecureContext === false || !Context || !p.AudioWorkletNode || !p.navigator?.mediaDevices?.getUserMedia)
      throw new Error("请使用支持麦克风的 Safari 或浏览器，并通过受信任的 HTTPS 打开");
    if (p.document?.visibilityState === "hidden") throw new Error("请保持言渡在前台再开始说话");
    const run = { onChunk, onLevel, onInterrupted, onStateChange, removers: [],
      canceled: false, stopping: false, active: false, wasRunning: false, stopPromise: null };
    this.run = run;
    const canceled = new Promise((_, reject) => { run.cancel = () => reject(aborted()); });
    // Attach a rejection handler immediately: setup may throw before the first await.
    canceled.catch(() => {});
    try {
      this.setState(run, "starting");
      this.assertCurrent(run);
      run.context = new Context({ latencyHint: "interactive" });
      const context = run.context;
      if (!context.audioWorklet) throw new Error("当前浏览器不支持语音采集，请升级 Safari 或系统");
      this.listen(run, context, "statechange", () => {
        if (context.state === "running") run.wasRunning = true;
        else if (run.wasRunning && !run.stopping)
          this.interrupt(run, "系统暂停了麦克风，请回到页面后重新开始");
      });
      this.listen(run, p.document, "visibilitychange", () => {
        if (p.document.visibilityState === "hidden")
          this.interrupt(run, "页面已进入后台，语音已停止；回到页面后可重新开始");
      });
      this.listen(run, p, "pagehide", () => this.interrupt(run, "页面已离开，语音已停止"));
      this.listen(run, p.document, "freeze", () => this.interrupt(run, "页面已暂停，语音已停止"));

      // Both calls occur in this synchronous user-gesture turn, including Safari resume.
      const resumed = Promise.resolve(context.resume());
      resumed.catch(() => {});
      run.wasRunning = context.state === "running";
      this.pendingPermissions++;
      let requested;
      try {
        requested = p.navigator.mediaDevices.getUserMedia({ video: false, audio: {
          channelCount: { ideal: 1 }, echoCancellation: false, noiseSuppression: false, autoGainControl: false
        } });
      } catch (error) {
        this.pendingPermissions--;
        resumed.catch(() => {});
        throw error;
      }
      const permission = Promise.resolve(requested).then(stream => {
        if (run.canceled || this.run !== run) { stopTracks(stream); throw aborted(); }
        run.stream = stream;
        const tracks = stream.getAudioTracks();
        if (!tracks.length || tracks.some(track => track.readyState === "ended" || track.muted))
          throw new Error("麦克风已中断，请检查系统权限后重新开始");
        for (const track of tracks) {
          this.listen(run, track, "ended", () => this.interrupt(run, "麦克风已断开，语音已停止"));
          this.listen(run, track, "mute", () => this.interrupt(run, "系统中断了麦克风，语音已停止"));
        }
        return stream;
      }).finally(() => { this.pendingPermissions--; });
      const setup = this.withDeadline(Promise.all([resumed,
        Promise.resolve().then(() => context.audioWorklet.addModule(String(this.workletUrl)))]), this.setupTimeoutMs,
      "麦克风启动超时，请检查页面连接后重试");
      await Promise.race([Promise.all([permission, setup]), canceled]);
      this.assertCurrent(run);
      if (context.state !== "running") throw new Error("系统未允许麦克风运行，请点击话筒重试");
      run.node = new p.AudioWorkletNode(context, "yandu-pcm", {
        numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1], channelCountMode: "max"
      });
      run.node.port.onmessage = event => this.receive(run, event.data);
      run.node.onprocessorerror = () => this.interrupt(run, "语音采集出现错误，已停止麦克风");
      run.source = context.createMediaStreamSource(run.stream);
      // The processor emits zeros. A connected silent output keeps Safari processing
      // without ever playing the microphone back through the phone speaker.
      run.silence = context.createGain();
      run.silence.gain.value = 0;
      run.source.connect(run.node);
      run.node.connect(run.silence);
      run.silence.connect(context.destination);
      run.active = true;
      this.setState(run, "recording");
      this.assertCurrent(run);
      return { sampleRate: PCM_RATE, channels: 1, inputSampleRate: context.sampleRate };
    } catch (error) {
      await this.stopRun(run, false);
      if (error?.name === "NotAllowedError" || error?.name === "PermissionDeniedError")
        throw new Error("麦克风权限未允许，请在 Safari / Chrome 的网站设置中允许麦克风，再点击重试");
      if (error?.name === "NotFoundError") throw new Error("没有找到可用麦克风，请检查设备后重试");
      if (error?.name === "NotReadableError") throw new Error("麦克风暂时无法使用，请结束其他录音或通话后重试");
      throw error;
    }
  }

  stop() {
    return this.run ? this.stopRun(this.run, true) : Promise.resolve();
  }

  stopRun(run, flush) {
    if (run.stopPromise) return run.stopPromise;
    let finish;
    run.stopPromise = new Promise(resolve => { finish = resolve; });
    run.stopping = run.canceled = true;
    run.cancel();
    this.setState(run, "stopping");
    void (async () => {
      if (flush && run.active && run.node && run.context.state === "running") {
        await new Promise(resolve => {
          const timer = this.platform.setTimeout(resolve, this.flushTimeoutMs);
          run.flushed = () => { this.platform.clearTimeout(timer); resolve(); };
          try { run.node.port.postMessage({ type: "stop" }); }
          catch { run.flushed(); }
        });
      }
      await this.dispose(run);
      if (this.run === run) {
        this.run = null;
        this.setState(run, "idle");
      }
    })().then(finish, finish);
    return run.stopPromise;
  }

  interrupt(run, reason) {
    if (this.run !== run) return;
    if (run.stopping) {
      // A page may be frozen between requesting the tail and receiving its ACK.
      // Do not leave release dependent on timers or worklet messages in that case.
      stopTracks(run.stream);
      run.stream = null;
      run.flushed?.();
      return run.stopPromise;
    }
    // On page suspension there may be no final worklet message. Release tracks now.
    const stopped = this.stopRun(run, false);
    try { run.onInterrupted(reason); } catch { /* UI failure cannot retain the microphone. */ }
    return stopped;
  }

  receive(run, message) {
    if (this.run !== run || !run.node) return;
    if (message?.type === "flushed") { run.flushed?.(); return; }
    if (message?.type === "level") {
      if (!run.stopping) { try { run.onLevel(message.value); } catch { /* Presentation only. */ } }
      return;
    }
    if (message?.type !== "pcm" || !(message.buffer instanceof ArrayBuffer)) return;
    try {
      run.onChunk(message.buffer);
      run.node.port.postMessage({ type: "ack" });
    } catch {
      if (run.stopping) run.flushed?.();
      else this.interrupt(run, "音频发送未完成，麦克风已停止，请重新连接后重试");
    }
  }

  assertCurrent(run) {
    if (run.canceled || this.run !== run) throw aborted();
  }
  setState(run, state) {
    this.state = state;
    try { run.onStateChange(state); } catch { /* State consumers do not own resources. */ }
  }
  listen(run, target, name, callback) {
    if (!target?.addEventListener) return;
    target.addEventListener(name, callback);
    run.removers.push(() => target.removeEventListener(name, callback));
  }
  async dispose(run) {
    for (const remove of run.removers.splice(0)) remove();
    stopTracks(run.stream);
    for (const node of [run.source, run.node, run.silence]) {
      try { node?.disconnect(); } catch { /* Already disconnected. */ }
    }
    if (run.node) {
      run.node.onprocessorerror = null;
      run.node.port.onmessage = null;
      try { run.node.port.close(); } catch { /* Already closed. */ }
    }
    if (run.context && run.context.state !== "closed") {
      try { await this.withDeadline(run.context.close(), 1000, "AudioContext close timeout"); }
      catch { /* Tracks are already released; a suspended browser may defer close. */ }
    }
    run.stream = run.node = run.source = run.silence = null;
    try { run.onLevel(0); } catch { /* Presentation only. */ }
  }
  withDeadline(promise, milliseconds, message) {
    return new Promise((resolve, reject) => {
      const timer = this.platform.setTimeout(() => reject(new Error(message)), milliseconds);
      Promise.resolve(promise).then(resolve, reject).finally(() => this.platform.clearTimeout(timer));
    });
  }
}
