"""Public-fixture check; no microphone or temporary audio/text files.
Usage: python Test-DesktopSpeech.py <package-directory> <upstream-jfk.wav>
"""
import hashlib
import io
import pathlib
import subprocess
import sys
import time
import wave

package = pathlib.Path(sys.argv[1]).resolve()
fixture = pathlib.Path(sys.argv[2]).resolve()
model = package / "models" / "ggml-small-q5_1.bin"
assert model.stat().st_size == 190085487
assert hashlib.sha256(model.read_bytes()).hexdigest() == "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb"
with wave.open(str(fixture), "rb") as reader:
    assert (reader.getnchannels(), reader.getsampwidth(), reader.getframerate()) == (1, 2, 16000)
    pcm16 = reader.readframes(reader.getnframes())
pcm48 = b"".join(pcm16[i:i+2] * 3 for i in range(0, len(pcm16), 2))
audio = io.BytesIO()
with wave.open(audio, "wb") as writer:
    writer.setnchannels(1); writer.setsampwidth(2); writer.setframerate(48000); writer.writeframes(pcm48)
runtime = package / "speech-runtime"
before = {p.relative_to(runtime) for p in runtime.rglob("*")}
native_name = sys.argv[3] if len(sys.argv) > 3 else "whisper-cli.exe" if sys.platform == "win32" else "whisper-cli"
assert native_name in {"whisper-cli", "whisper-cli.exe", "whisper-cli-avx2", "whisper-cli-avx2.exe"}
cli = runtime / native_name
started = time.monotonic()
result = subprocess.run([str(cli), "-m", str(model), "-f", "-", "-of", "PhoneDeck-memory", "-l", "auto", "-nt", "-np", "-ng", "-t", "4"],
    input=audio.getvalue(), capture_output=True, timeout=180, cwd=runtime)
assert result.returncode == 0, (result.returncode, result.stderr.decode("utf-8", errors="replace")[-2000:])
text = result.stdout.decode("utf-8").strip()
assert "country" in text.lower(), "Expected public fixture transcription on stdout"
assert before == {p.relative_to(runtime) for p in runtime.rglob("*")}, "Recognition wrote an unexpected file"
print(f"PASS: {native_name}, model, Unicode path, PCM48 stdin, stdout text, no recording/result files ({time.monotonic()-started:.1f}s)")
