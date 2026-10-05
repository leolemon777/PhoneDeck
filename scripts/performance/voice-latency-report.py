#!/usr/bin/env python3
"""Join phone and receiver voice logs by sessionId into a latency report.

Usage:
  adb logcat -d -s PhoneDeckAudio:I PhoneDeckVoice:I > phone.log
  # Receiver console output (Windows PhoneDeck.Server or macOS PhoneDeck.Receiver) > receiver.log
  python3 scripts/performance/voice-latency-report.py --phone phone.log --receiver receiver.log [--json out.json]

Both sides already log per-session stage timings against their own monotonic
clocks (phone: AudioStreamer / voice command; receiver: [audio:...] and
[dictation:...]).  Clocks differ between devices, so this report never
subtracts a phone timestamp from a receiver timestamp; it lines both
timelines up per session and derives the metrics each side can measure alone.

The optional --self-test flag runs the parser against built-in samples.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from collections import defaultdict

SESSION = r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"
PHONE_LINE = re.compile(rf"({SESSION}) (\w+)((?: \w+=\S+)*) \+(\d+)ms")
RECEIVER_LINE = re.compile(rf"\[(audio|dictation):({SESSION})\]\s+(\w+)(?:=\+?(-?\d+)ms|=(\w+))?((?:\s+\+?\w*=?\S+)*)")
PAIR = re.compile(r"(\w+)=\+?(-?\d+)(?:ms)?\b|(\w+)=(\S+)")
BYTES_PER_MS = 48_000 * 2 / 1_000

# Targets from the latency plan; adjust after real-device baselines exist.
TARGETS_MS = {
    "phone.captureReadyMs": 150,
    "receiver.prerollReleasedMs": 800,
    "receiver.backlogAtReleaseMs": 300,
}


def _pairs(text: str) -> dict:
    values = {}
    for match in PAIR.finditer(text or ""):
        if match.group(1):
            values[match.group(1)] = int(match.group(2))
        else:
            raw = match.group(4)
            values[match.group(3)] = int(raw) if raw.lstrip("-").isdigit() else raw
    return values


def parse_phone(lines):
    sessions = defaultdict(dict)
    for line in lines:
        match = PHONE_LINE.search(line)
        if not match:
            continue
        session, stage, extra, elapsed = match.groups()
        key = stage
        attributes = _pairs(extra)
        if stage in ("commandQueued", "commandConfirmed", "commandFailed", "commandStarted"):
            key = f"{stage}.{'start' if attributes.get('starting') == 'true' else 'stop'}"
        sessions[session.lower()].setdefault(key, int(elapsed))
        for name, value in attributes.items():
            if name != "starting":
                sessions[session.lower()].setdefault(f"{stage}.{name}", value)
    return sessions


def parse_receiver(lines):
    sessions = defaultdict(dict)
    for line in lines:
        match = RECEIVER_LINE.search(line)
        if not match:
            continue
        _, session, stage, elapsed, word, extra = match.groups()
        record = sessions[session.lower()]
        if elapsed is not None:
            record.setdefault(stage, int(elapsed))
        elif word is not None:
            record.setdefault(stage, word)
        else:
            record.setdefault(stage, True)
        for name, value in _pairs(extra).items():
            record.setdefault(f"{stage}.{name}", value)
    return sessions


def derive(phone: dict, receiver: dict) -> dict:
    metrics = {}
    if "audioRecordStarted" in phone:
        metrics["phone.captureReadyMs"] = phone["audioRecordStarted"]
    if "recorderReady.reused" in phone:
        metrics["phone.recorderPrepared"] = phone["recorderReady.reused"] == "true"
    if "serverHeaders" in phone:
        metrics["phone.audioLinkMs"] = phone["serverHeaders"]
    if "prerollFlushed.bytes" in phone:
        metrics["phone.prerollFlushedMs"] = round(phone["prerollFlushed.bytes"] / BYTES_PER_MS)
    if "commandConfirmed.start" in phone:
        metrics["phone.startConfirmedMs"] = phone["commandConfirmed.start"]
    if "commandConfirmed.stop" in phone:
        metrics["phone.stopConfirmedMs"] = phone["commandConfirmed.stop"]
    if "tailConfirmed" in phone and "sessionStopped" in phone:
        metrics["phone.tailToStopMs"] = phone["sessionStopped"] - phone["tailConfirmed"]

    if isinstance(receiver.get("engineCapturing"), int):
        metrics["receiver.engineCapturingMs"] = receiver["engineCapturing"]
    if isinstance(receiver.get("engineStartRequested"), int):
        metrics["receiver.engineTriggeredMs"] = receiver["engineStartRequested"]
    if "wasapiStarted.warm" in receiver:
        metrics["receiver.outputWarm"] = str(receiver["wasapiStarted.warm"]).lower() == "true"
    if isinstance(receiver.get("playbackReleased"), int):
        metrics["receiver.prerollReleasedMs"] = receiver["playbackReleased"]
    preroll = receiver.get("playbackReleased.preRollBytes")
    trimmed = receiver.get("playbackReleased.leadingSilenceTrimmedBytes", 0)
    if isinstance(preroll, int):
        metrics["receiver.backlogAtReleaseMs"] = round((preroll - (trimmed or 0)) / BYTES_PER_MS)
        metrics["receiver.leadingSilenceTrimmedMs"] = round((trimmed or 0) / BYTES_PER_MS)
    skipped = receiver.get("sessionStopped.skippedSilenceBytes")
    if isinstance(skipped, int):
        metrics["receiver.skippedSilenceMs"] = round(skipped / BYTES_PER_MS)
    dropped = receiver.get("sessionStopped.droppedStaleBytes")
    if isinstance(dropped, int):
        metrics["receiver.droppedSpeechMs"] = round(dropped / BYTES_PER_MS)
    if isinstance(receiver.get("drained"), int) and isinstance(receiver.get("sessionStopped"), int):
        metrics["receiver.tailMs"] = receiver["sessionStopped"] - receiver["drained"]
    if receiver.get("sharedReconnectTakeover"):
        metrics["receiver.sharedReconnectTakeover"] = True
    if receiver.get("desktopStopObserved"):
        metrics["receiver.desktopStopReceipt"] = True
    return metrics


def build_report(phone_lines, receiver_lines) -> dict:
    phone = parse_phone(phone_lines)
    receiver = parse_receiver(receiver_lines)
    sessions = []
    for session in sorted(set(phone) | set(receiver)):
        metrics = derive(phone.get(session, {}), receiver.get(session, {}))
        over = {name: metrics[name] for name, limit in TARGETS_MS.items()
                if isinstance(metrics.get(name), (int, float)) and metrics[name] > limit}
        sessions.append({"sessionId": session, "metrics": metrics, "overTarget": over,
                         "phone": phone.get(session, {}), "receiver": receiver.get(session, {})})
    return {"targetsMs": TARGETS_MS, "sessions": sessions,
            "note": "Phone and receiver clocks are independent; metrics are per-side only."}


def print_table(report: dict) -> None:
    columns = ["phone.captureReadyMs", "phone.audioLinkMs", "phone.startConfirmedMs",
               "receiver.engineCapturingMs", "receiver.backlogAtReleaseMs",
               "receiver.leadingSilenceTrimmedMs", "receiver.skippedSilenceMs",
               "receiver.droppedSpeechMs", "phone.stopConfirmedMs"]
    print("session   " + " ".join(f"{name.split('.')[-1][:14]:>14}" for name in columns))
    for entry in report["sessions"]:
        values = [entry["metrics"].get(name, "") for name in columns]
        print(f"{entry['sessionId'][:8]}  " + " ".join(f"{str(value):>14}" for value in values)
              + ("  !" + ",".join(entry["overTarget"]) if entry["overTarget"] else ""))


SAMPLE_PHONE = """\
10-05 03:00:00.100 I PhoneDeckAudio: 11111111-2222-3333-4444-555555555555 recorderReady reused=true +4ms
10-05 03:00:00.110 I PhoneDeckAudio: 11111111-2222-3333-4444-555555555555 audioRecordStarted +12ms
10-05 03:00:00.200 I PhoneDeckAudio: 11111111-2222-3333-4444-555555555555 serverHeaders +85ms
10-05 03:00:00.300 I PhoneDeckAudio: 11111111-2222-3333-4444-555555555555 prerollFlushed bytes=9600 +90ms
10-05 03:00:00.900 I PhoneDeckVoice: 11111111-2222-3333-4444-555555555555 commandConfirmed starting=true +420ms
10-05 03:00:04.000 I PhoneDeckVoice: 11111111-2222-3333-4444-555555555555 commandConfirmed starting=false +950ms
"""
SAMPLE_RECEIVER = """\
[audio:11111111-2222-3333-4444-555555555555] wasapiStarted=+1ms warm=True
[dictation:11111111-2222-3333-4444-555555555555] audioWarmup=ready +0ms
[dictation:11111111-2222-3333-4444-555555555555] engineStartRequested=+3ms
[dictation:11111111-2222-3333-4444-555555555555] engineCapturing=+310ms
[audio:11111111-2222-3333-4444-555555555555] playbackReleased=+330ms reason=engine preRollBytes=48000 leadingSilenceTrimmedBytes=28800
[audio:11111111-2222-3333-4444-555555555555] drained=+4200ms
[audio:11111111-2222-3333-4444-555555555555] sessionStopped=+4600ms bytes=400000 droppedStaleBytes=0 skippedSilenceBytes=19200
"""


def self_test() -> int:
    report = build_report(SAMPLE_PHONE.splitlines(), SAMPLE_RECEIVER.splitlines())
    metrics = report["sessions"][0]["metrics"]
    expected = {
        "phone.captureReadyMs": 12, "phone.recorderPrepared": True, "phone.audioLinkMs": 85,
        "phone.prerollFlushedMs": 100, "phone.startConfirmedMs": 420, "phone.stopConfirmedMs": 950,
        "receiver.engineCapturingMs": 310, "receiver.outputWarm": True,
        "receiver.prerollReleasedMs": 330, "receiver.backlogAtReleaseMs": 200,
        "receiver.leadingSilenceTrimmedMs": 300, "receiver.skippedSilenceMs": 200,
        "receiver.droppedSpeechMs": 0, "receiver.tailMs": 400,
    }
    failures = {key: (metrics.get(key), value) for key, value in expected.items() if metrics.get(key) != value}
    if failures:
        print("self-test failed:", failures, file=sys.stderr)
        return 1
    print("self-test passed")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--phone", help="adb logcat output containing PhoneDeckAudio/PhoneDeckVoice lines")
    parser.add_argument("--receiver", help="receiver console log")
    parser.add_argument("--json", help="write the full report as JSON")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    if not args.phone and not args.receiver:
        parser.error("provide --phone and/or --receiver")
    read = lambda path: open(path, encoding="utf-8", errors="replace").read().splitlines() if path else []
    report = build_report(read(args.phone), read(args.receiver))
    print_table(report)
    if args.json:
        with open(args.json, "w", encoding="utf-8") as output:
            json.dump(report, output, ensure_ascii=False, indent=2)
    return 0


if __name__ == "__main__":
    sys.exit(main())
