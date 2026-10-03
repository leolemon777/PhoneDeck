import { PcmEncoder, PcmPreRollQueue } from "./audio.js";

class YanduPcmProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    // At most 4 transferred frames plus 920 ms queued locally: <= 1 second.
    this.pending = new PcmPreRollQueue(920);
    this.outstanding = 0;
    this.stopping = false;
    this.finished = false;
    this.encoder = new PcmEncoder(sampleRate, buffer => {
      this.pending.push(buffer);
      this.sendPending();
    }, value => {
      // Do not build a second unbounded message queue when the main thread stalls.
      if (this.outstanding < 4) this.port.postMessage({ type: "level", value });
    });
    this.port.onmessage = event => {
      if (event.data?.type === "ack") {
        this.outstanding = Math.max(0, this.outstanding - 1);
        this.sendPending();
      } else if (event.data?.type === "stop" && !this.stopping) {
        this.stopping = true;
        this.encoder.flush();
        this.sendPending();
      }
    };
  }
  sendPending() {
    while (this.outstanding < 4 && this.pending.byteLength) {
      const buffer = this.pending.shift();
      this.outstanding++;
      this.port.postMessage({ type: "pcm", buffer }, [buffer]);
    }
    if (this.stopping && !this.pending.byteLength && !this.outstanding && !this.finished) {
      this.finished = true;
      this.port.postMessage({ type: "flushed" });
    }
  }
  process(inputs, outputs) {
    for (const output of outputs) for (const channel of output) channel.fill(0);
    if (!this.stopping) this.encoder.push(inputs[0]);
    return !this.finished;
  }
}

registerProcessor("yandu-pcm", YanduPcmProcessor);
