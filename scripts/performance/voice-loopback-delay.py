#!/usr/bin/env python3
"""Measure phone-to-input-method audio latency from a same-clock loopback recording.

Procedure (one computer, one clock, no clock sync needed):
  1. Start a managed or shared PhoneDeck session from the phone and keep it near
     the computer speaker.
  2. Record two channels at the same time on the computer:
       channel 0 = what the speaker plays (e.g. a loopback/monitor of the output),
       channel 1 = the virtual microphone the input method hears
                   (Windows "CABLE Output", macOS "BlackHole 2ch").
  3. Play short clicks/beeps a few times (>= 1 s apart).
  4. python3 scripts/performance/voice-loopback-delay.py recording.wav
     (or --reference speaker.wav --captured cable.wav for two mono files that
      were started together).

The delay printed is speaker -> phone microphone -> network -> receiver ->
virtual device, i.e. what the input method experiences. Acoustic distance adds
about 3 ms per metre. Use --self-test to verify the analyzer.
"""
from __future__ import annotations

import argparse
import math
import statistics
import struct
import sys
import wave


def read_wav(path: str):
    with wave.open(path, "rb") as source:
        channels, width, rate = source.getnchannels(), source.getsampwidth(), source.getframerate()
        if width != 2:
            raise SystemExit(f"{path}: only 16-bit PCM WAV is supported")
        raw = source.readframes(source.getnframes())
    samples = struct.unpack(f"<{len(raw) // 2}h", raw)
    return rate, [list(samples[channel::channels]) for channel in range(channels)]


def onsets(samples, rate, threshold_ratio=0.3, refractory_ms=500, window_ms=2):
    """Return sample indexes where short-window energy first crosses the threshold."""
    window = max(1, int(rate * window_ms / 1000))
    energies = []
    for start in range(0, len(samples) - window, window):
        block = samples[start:start + window]
        energies.append(math.sqrt(sum(value * value for value in block) / window))
    if not energies:
        return []
    peak = max(energies)
    if peak <= 0:
        return []
    threshold = peak * threshold_ratio
    found, last = [], -10 ** 12
    refractory = int(rate * refractory_ms / 1000)
    for index, energy in enumerate(energies):
        position = index * window
        if energy >= threshold and position - last >= refractory:
            found.append(position)
            last = position
    return found


def pair_delays(reference, captured, rate, max_delay_ms=3000):
    delays = []
    limit = int(rate * max_delay_ms / 1000)
    remaining = list(captured)
    for onset in reference:
        candidates = [value for value in remaining if 0 <= value - onset <= limit]
        if candidates:
            match = candidates[0]
            delays.append((match - onset) * 1000 / rate)
            remaining.remove(match)
    return delays


def analyze(rate, reference, captured):
    delays = pair_delays(onsets(reference, rate), onsets(captured, rate), rate)
    if not delays:
        raise SystemExit("no matching clicks found; check channel order and click loudness")
    return {
        "clicks": len(delays),
        "medianMs": round(statistics.median(delays), 1),
        "minMs": round(min(delays), 1),
        "maxMs": round(max(delays), 1),
        "jitterMs": round(max(delays) - min(delays), 1),
    }


def self_test() -> int:
    rate = 48_000
    reference = [0] * (rate * 4)
    captured = [0] * (rate * 4)
    for second, delay_ms in ((0.5, 180), (1.5, 190), (2.5, 185)):
        start = int(second * rate)
        shifted = start + int(delay_ms * rate / 1000)
        for offset in range(240):
            reference[start + offset] = 20_000
            captured[shifted + offset] = 6_000
    result = analyze(rate, reference, captured)
    ok = result["clicks"] == 3 and abs(result["medianMs"] - 185) <= 3 and result["jitterMs"] <= 14
    print("self-test", "passed" if ok else f"failed: {result}")
    return 0 if ok else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("recording", nargs="?", help="16-bit WAV, channel 0 = speaker, channel 1 = virtual device")
    parser.add_argument("--reference", help="mono WAV of the speaker signal")
    parser.add_argument("--captured", help="mono WAV of the virtual device")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    if args.recording:
        rate, channels = read_wav(args.recording)
        if len(channels) < 2:
            parser.error("the recording needs two channels")
        reference, captured = channels[0], channels[1]
    elif args.reference and args.captured:
        rate, (reference, *_) = read_wav(args.reference)
        captured_rate, (captured, *_) = read_wav(args.captured)
        if captured_rate != rate:
            parser.error("both files must use the same sample rate")
    else:
        parser.error("provide a two-channel recording or --reference/--captured")
    result = analyze(rate, reference, captured)
    print(" ".join(f"{key}={value}" for key, value in result.items()))
    return 0


if __name__ == "__main__":
    sys.exit(main())
