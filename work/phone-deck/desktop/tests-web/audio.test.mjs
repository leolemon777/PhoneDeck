import test from "node:test";
import assert from "node:assert/strict";
import { AudioCapture, PcmEncoder, PcmPreRollQueue } from "../PhoneDeck.Desktop/PhoneWeb/audio.js";

const tick = () => new Promise(resolve => setImmediate(resolve));
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((a, b) => { resolve = a; reject = b; });
  return { promise, resolve, reject };
};
function encode(rate, samples, sizes = [128]) {
  const chunks = [], levels = [];
  const encoder = new PcmEncoder(rate, value => chunks.push(value), value => levels.push(value));
  for (let offset = 0, block = 0; offset < samples.length; block++) {
    const end = Math.min(samples.length, offset + sizes[block % sizes.length]);
    encoder.push([samples.subarray(offset, end)]);
    offset = end;
  }
  encoder.flush();
  return { chunks, levels, encoder, bytes: Buffer.concat(chunks.map(chunk => Buffer.from(chunk))) };
}

for (const rate of [8000, 16000, 44100, 48000, 96000]) {
  test(`${rate} Hz produces exactly 48000 PCM16 samples per second, independent of render blocks`, () => {
    const samples = Float32Array.from({ length: rate }, (_, i) => 0.6 * Math.sin(2 * Math.PI * 440 * i / rate));
    const block128 = encode(rate, samples);
    const uneven = encode(rate, samples, [1, 17, 333, 2, 127, 2048]);
    assert.equal(block128.bytes.length, 96000);
    assert.deepEqual(block128.bytes, uneven.bytes);
    assert.equal(block128.chunks.length, 50);
    assert.ok(block128.chunks.every(chunk => chunk.byteLength === 1920));
    assert.equal(block128.levels.length, 10);
    // 440 Hz is still 440 Hz after resampling, not the 44.1/48 speed mismatch.
    let zeroCrossings = 0;
    for (let i = 1; i < 48000; i++)
      if (block128.bytes.readInt16LE((i - 1) * 2) <= 0 && block128.bytes.readInt16LE(i * 2) > 0) zeroCrossings++;
    assert.ok(zeroCrossings >= 439 && zeroCrossings <= 441);
  });
}

test("flush emits a short final frame exactly once without padding", () => {
  const samples = new Float32Array(1001).fill(0.25);
  const { chunks, encoder } = encode(48000, samples);
  assert.deepEqual(chunks.map(chunk => chunk.byteLength), [1920, 82]);
  encoder.flush(); encoder.push([samples]);
  assert.equal(chunks.length, 2);
});

test("PCM is little endian, clipped, finite and downmixed to mono", () => {
  const chunks = [];
  const encoder = new PcmEncoder(48000, chunk => chunks.push(chunk));
  encoder.push([Float32Array.from([-2, 2, NaN, Infinity, 0.5]), Float32Array.from([-1, 1, 0, 0, -0.5])]);
  encoder.flush();
  const bytes = Buffer.from(chunks[0]);
  assert.deepEqual([...Array(5)].map((_, i) => bytes.readInt16LE(i * 2)), [-32768, 32767, 0, 0, 0]);
  assert.equal(bytes[0], 0); assert.equal(bytes[1], 128);
});

test("empty capture emits nothing and resampler rejects invalid rates", () => {
  assert.equal(encode(48000, new Float32Array()).chunks.length, 0);
  for (const rate of [0, NaN, Infinity, 7999]) assert.throws(() => new PcmEncoder(rate, () => {}));
});

test("pre-roll retains only newest one second, in order, and clears memory", () => {
  const queue = new PcmPreRollQueue();
  for (let i = 0; i < 60; i++) queue.push(new Uint8Array(1920).fill(i).buffer);
  assert.equal(queue.byteLength, 96000);
  const chunks = queue.drain();
  assert.equal(chunks.length, 50);
  assert.equal(new Uint8Array(chunks[0])[0], 10);
  assert.equal(new Uint8Array(chunks.at(-1))[0], 59);
  assert.equal(queue.byteLength, 0);
  const held = new Uint8Array(20).fill(1); queue.push(held.buffer); queue.clear();
  assert.ok(held.every(value => value === 0));
  assert.throws(() => new PcmPreRollQueue(1001));
  assert.throws(() => queue.push(new ArrayBuffer(3)));
});

test("pre-roll also bounds oversized and partial frames", () => {
  const queue = new PcmPreRollQueue(20);
  queue.push(new Uint8Array(4000).fill(1).buffer);
  queue.push(new Uint8Array(22).fill(2).buffer);
  assert.equal(queue.byteLength, 1920);
  const bytes = Buffer.concat(queue.drain().map(chunk => Buffer.from(chunk)));
  assert.equal(bytes.subarray(0, -22).every(value => value === 1), true);
  assert.equal(bytes.subarray(-22).every(value => value === 2), true);
});

function fakeBrowser(options = {}) {
  const events = [], contexts = [], nodes = [];
  class Track extends EventTarget {
    readyState = "live";
    muted = false;
    stopped = 0;
    stop() { events.push("track.stop"); this.stopped++; this.readyState = "ended"; }
  }
  const track = new Track();
  const stream = { getTracks: () => [track], getAudioTracks: () => [track] };
  class Context extends EventTarget {
    state = "suspended";
    sampleRate = options.rate || 44100;
    destination = {};
    constructor() {
      super(); contexts.push(this); events.push("context.create");
      this.audioWorklet = { addModule: () => options.module || Promise.resolve() };
    }
    resume() {
      events.push("context.resume");
      if (options.resume) return options.resume;
      this.state = "running"; this.dispatchEvent(new Event("statechange"));
      return Promise.resolve();
    }
    close() { events.push("context.close"); this.state = "closed"; return Promise.resolve(); }
    createMediaStreamSource() { return { connect() {}, disconnect() { events.push("source.disconnect"); } }; }
    createGain() { return { gain: {}, connect() {}, disconnect() {} }; }
  }
  class WorkletNode {
    constructor() {
      nodes.push(this);
      this.port = {
        onmessage: null,
        postMessage: message => {
          events.push(`port.${message.type}`);
          if (message.type === "stop" && !options.stalledFlush) queueMicrotask(() => {
            this.port.onmessage?.({ data: { type: "pcm", buffer: new ArrayBuffer(14) } });
            this.port.onmessage?.({ data: { type: "flushed" } });
          });
        },
        close() { events.push("port.close"); }
      };
    }
    connect() {}
    disconnect() { events.push("worklet.disconnect"); }
  }
  const platform = Object.assign(new EventTarget(), {
    AudioContext: Context, AudioWorkletNode: WorkletNode, isSecureContext: true,
    setTimeout, clearTimeout,
    navigator: { mediaDevices: { getUserMedia() {
      events.push("permission.request");
      if (options.permissionThrow) throw options.permissionThrow;
      return options.permission || Promise.resolve(stream);
    } } },
    document: Object.assign(new EventTarget(), { visibilityState: "visible" })
  });
  const capture = new AudioCapture({ platform, flushTimeoutMs: 15, setupTimeoutMs: 25 });
  return { capture, platform, contexts, nodes, stream, track, events };
}

test("resume and permission happen synchronously in the initiating gesture; stop flushes before release", async () => {
  const b = fakeBrowser(); const chunks = [], levels = [], states = [];
  const started = b.capture.start({ onChunk: chunk => { chunks.push(chunk); b.events.push("chunk"); },
    onLevel: value => levels.push(value), onStateChange: state => states.push(state) });
  assert.deepEqual(b.events, ["context.create", "context.resume", "permission.request"]);
  assert.deepEqual(await started, { sampleRate: 48000, channels: 1, inputSampleRate: 44100 });
  const firstStop = b.capture.stop(), secondStop = b.capture.stop();
  assert.equal(firstStop, secondStop);
  await firstStop;
  assert.equal(chunks[0].byteLength, 14);
  assert.ok(b.events.indexOf("chunk") < b.events.indexOf("track.stop"));
  assert.equal(b.track.stopped, 1);
  assert.equal(b.contexts[0].state, "closed");
  assert.deepEqual(states, ["starting", "recording", "stopping", "idle"]);
  assert.equal(levels.at(-1), 0);
  await b.capture.stop(); assert.equal(b.track.stopped, 1);
});

test("release before permission resolves cancels immediately, and late permission cannot resurrect capture", async () => {
  const permission = deferred(); const b = fakeBrowser({ permission: permission.promise });
  const started = b.capture.start({ onChunk() { assert.fail("no audio after cancellation"); } });
  const rejected = assert.rejects(started, { name: "AbortError" });
  await b.capture.stop(); await rejected;
  assert.equal(b.capture.state, "idle");
  await assert.rejects(b.capture.start({ onChunk() {} }), /正在启动或收尾/);
  permission.resolve(b.stream); await tick();
  assert.equal(b.track.stopped, 1);
  assert.equal(b.nodes.length, 0);
  assert.equal(b.contexts[0].state, "closed");
});

test("concurrent start is rejected without requesting a second microphone", async () => {
  const permission = deferred(); const b = fakeBrowser({ permission: permission.promise });
  const first = b.capture.start({ onChunk() {} });
  await assert.rejects(b.capture.start({ onChunk() {} }), /正在启动或收尾/);
  permission.resolve(b.stream); await first; await b.capture.stop();
  assert.equal(b.events.filter(value => value === "permission.request").length, 1);
});

for (const trigger of ["mute", "ended", "suspended", "interrupted", "pagehide", "hidden", "freeze", "processorerror"]) {
  test(`${trigger} releases microphone and never restarts on foreground/resume`, async () => {
    const b = fakeBrowser(), reasons = [];
    await b.capture.start({ onChunk() {}, onInterrupted: reason => reasons.push(reason) });
    if (["mute", "ended"].includes(trigger)) b.track.dispatchEvent(new Event(trigger));
    else if (["suspended", "interrupted"].includes(trigger)) {
      b.contexts[0].state = trigger; b.contexts[0].dispatchEvent(new Event("statechange"));
    } else if (trigger === "pagehide") b.platform.dispatchEvent(new Event("pagehide"));
    else if (trigger === "freeze") b.platform.document.dispatchEvent(new Event("freeze"));
    else if (trigger === "processorerror") b.nodes[0].onprocessorerror();
    else { b.platform.document.visibilityState = "hidden"; b.platform.document.dispatchEvent(new Event("visibilitychange")); }
    assert.equal(b.track.stopped, 1, "track release occurs synchronously on interruption");
    await b.capture.stop();
    b.platform.document.visibilityState = "visible"; b.platform.document.dispatchEvent(new Event("visibilitychange"));
    assert.equal(reasons.length, 1); assert.equal(b.capture.state, "idle");
    assert.equal(b.events.filter(value => value === "permission.request").length, 1);
  });
}

test("permission and worklet failures clean up without leaking microphone or context", async () => {
  for (const options of [{ permission: Promise.reject(new Error("denied")) },
    { permissionThrow: new Error("denied") }, { module: Promise.reject(new Error("module failed")) }]) {
    const b = fakeBrowser(options);
    await assert.rejects(b.capture.start({ onChunk() {} }));
    assert.equal(b.contexts[0].state, "closed");
    assert.equal(b.capture.state, "idle");
    if (b.events.includes("track.stop")) assert.equal(b.track.stopped, 1);
  }
});

test("hung module or missing worklet flush has a bounded cleanup", async () => {
  const b = fakeBrowser({ module: new Promise(() => {}) });
  await assert.rejects(b.capture.start({ onChunk() {} }), /超时/);
  assert.equal(b.track.stopped, 1);
  const stalled = fakeBrowser({ stalledFlush: true });
  await stalled.capture.start({ onChunk() {} }); await stalled.capture.stop();
  assert.equal(stalled.track.stopped, 1); assert.equal(stalled.capture.state, "idle");
});

test("transport callback failure stops capture; UI callback failure does not leak resources", async () => {
  const b = fakeBrowser(), reasons = [];
  await b.capture.start({ onChunk() { throw new Error("network full"); }, onInterrupted: reason => reasons.push(reason),
    onStateChange() { throw new Error("UI failed"); }, onLevel() { throw new Error("UI failed"); } });
  b.nodes[0].port.onmessage({ data: { type: "pcm", buffer: new ArrayBuffer(1920) } });
  await b.capture.stop();
  assert.equal(b.track.stopped, 1); assert.equal(reasons.length, 1);
});

test("reentrant stop from state callback remains idempotent", async () => {
  const b = fakeBrowser();
  await b.capture.start({ onChunk() {}, onStateChange(state) { if (state === "stopping") void b.capture.stop(); } });
  await b.capture.stop(); assert.equal(b.track.stopped, 1);
});

test("page suspension during tail drain releases tracks without waiting for a frozen timer", async () => {
  const b = fakeBrowser({ stalledFlush: true });
  await b.capture.start({ onChunk() {} });
  const stopping = b.capture.stop();
  assert.equal(b.track.stopped, 0);
  b.platform.dispatchEvent(new Event("pagehide"));
  assert.equal(b.track.stopped, 1);
  await stopping;
  assert.equal(b.track.stopped, 1);
  assert.equal(b.capture.state, "idle");
});

test("insecure or hidden pages fail before requesting permission", async () => {
  for (const kind of ["insecure", "hidden"]) {
    const b = fakeBrowser();
    if (kind === "insecure") b.platform.isSecureContext = false;
    else b.platform.document.visibilityState = "hidden";
    await assert.rejects(b.capture.start({ onChunk() {} }));
    assert.equal(b.events.length, 0);
  }
});

test("actual worklet bounds stalled delivery and flushes the final frame", async () => {
  let Processor;
  globalThis.AudioWorkletProcessor = class { constructor() {
    this.messages = []; this.port = { postMessage: message => this.messages.push(message) };
  } };
  globalThis.sampleRate = 48000;
  globalThis.registerProcessor = (name, type) => { assert.equal(name, "yandu-pcm"); Processor = type; };
  try {
    await import("../PhoneDeck.Desktop/PhoneWeb/pcm-worklet.js");
    const processor = new Processor();
    const output = new Float32Array(128).fill(1);
    for (let i = 0; i < 1000; i++) processor.process([[new Float32Array(128)]], [[output]]);
    assert.ok(output.every(value => value === 0), "microphone is never played back");
    assert.equal(processor.messages.filter(message => message.type === "pcm").length, 4);
    assert.ok(processor.pending.byteLength + 4 * 1920 <= 96000);
    processor.port.onmessage({ data: { type: "stop" } });
    let index = 0, deliveredBytes = 0;
    while (index < processor.messages.length) {
      const message = processor.messages[index++];
      if (message.type === "pcm") { deliveredBytes += message.buffer.byteLength; processor.port.onmessage({ data: { type: "ack" } }); }
    }
    assert.ok(deliveredBytes <= 96000);
    assert.equal(processor.messages.at(-1).type, "flushed");
    assert.equal(processor.process([[]], [[output]]), false);
    assert.equal(processor.messages.filter(message => message.type === "flushed").length, 1);
  } finally {
    delete globalThis.AudioWorkletProcessor; delete globalThis.sampleRate; delete globalThis.registerProcessor;
  }
});
