package com.codex.phonedeck;

import android.Manifest;
import android.content.Context;
import android.content.pm.PackageManager;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.os.SystemClock;

import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.util.Arrays;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

/** One AudioRecord source fanned out to independent, bounded receiver queues. */
final class SharedAudioBroadcaster implements AutoCloseable {
    interface Listener {
        void onLevel(int percent);
        void onConnectionsChanged(Map<String, Boolean> connections);
        void onStopped(String reason);
    }

    static final class Target {
        final String computerId;
        final String displayName;
        final PhoneDeckEndpoint endpoint;

        Target(String computerId, String displayName, PhoneDeckEndpoint endpoint) {
            this.computerId = computerId;
            this.displayName = displayName;
            this.endpoint = endpoint;
        }

        boolean sameEndpoint(Target other) {
            return other != null && endpoint.baseUrl.equals(other.endpoint.baseUrl)
                    && computerId.equalsIgnoreCase(other.computerId);
        }
    }

    private static final int SAMPLE_RATE = 48_000;
    private static final int CHUNK_BYTES = SAMPLE_RATE * 2 * 20 / 1_000;
    private static final int QUEUE_FRAMES = 6; // 120 ms; shared audio must remain live.

    private final Context context;
    private final Listener listener;
    private final Object stateLock = new Object();
    private final ConcurrentHashMap<String, TargetSink> sinks = new ConcurrentHashMap<>();
    private volatile boolean running;
    private volatile AudioRecord recorder;
    private volatile String sessionId;
    private Thread captureThread;

    SharedAudioBroadcaster(Context context, Listener listener) {
        this.context = context.getApplicationContext();
        this.listener = listener;
    }

    boolean isRunning() {
        return running;
    }

    boolean isFinishing() {
        synchronized (stateLock) {
            return !running && captureThread != null;
        }
    }

    String getSessionId() {
        return sessionId;
    }

    boolean start(String newSessionId) {
        synchronized (stateLock) {
            if (captureThread != null && captureThread.isAlive()) {
                return false;
            }
            if (context.checkSelfPermission(Manifest.permission.RECORD_AUDIO)
                    != PackageManager.PERMISSION_GRANTED) {
                throw new SecurityException("没有手机麦克风权限");
            }
            sessionId = newSessionId;
            running = true;
            captureThread = new Thread(this::captureLoop, "PhoneDeck-SharedCapture");
            captureThread.start();
            return true;
        }
    }

    void updateTargets(Map<String, Target> requested) {
        synchronized (stateLock) {
            if (!running) {
                return;
            }
            Map<String, Target> desired = new HashMap<>(requested);
            for (Map.Entry<String, TargetSink> entry : sinks.entrySet()) {
                Target next = desired.remove(entry.getKey());
                TargetSink existing = entry.getValue();
                if (next == null || !existing.target.sameEndpoint(next) || !existing.isAlive()) {
                    if (sinks.remove(entry.getKey(), existing)) {
                        existing.stop();
                    }
                    if (next != null) {
                        addSink(next);
                    }
                }
            }
            for (Target target : desired.values()) {
                addSink(target);
            }
            notifyConnections();
        }
    }

    private void addSink(Target target) {
        TargetSink sink = new TargetSink(target);
        TargetSink previous = sinks.putIfAbsent(target.computerId, sink);
        if (previous == null) {
            sink.start();
        }
    }

    @android.annotation.SuppressLint("MissingPermission")
    private void captureLoop() {
        AudioRecord localRecorder = null;
        String failure = null;
        try {
            if (context.checkSelfPermission(Manifest.permission.RECORD_AUDIO)
                    != PackageManager.PERMISSION_GRANTED) {
                throw new SecurityException("手机麦克风权限已被撤销");
            }
            int minimum = AudioRecord.getMinBufferSize(
                    SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT);
            if (minimum <= 0) {
                throw new IllegalStateException("手机不支持 48 kHz 麦克风采集");
            }
            localRecorder = new AudioRecord(
                    MediaRecorder.AudioSource.VOICE_RECOGNITION,
                    SAMPLE_RATE,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT,
                    Math.max(minimum * 2, 8192));
            if (localRecorder.getState() != AudioRecord.STATE_INITIALIZED) {
                throw new IllegalStateException("无法初始化手机麦克风");
            }
            synchronized (stateLock) {
                if (!running) {
                    return;
                }
                recorder = localRecorder;
                localRecorder.startRecording();
            }
            byte[] chunk = new byte[CHUNK_BYTES];
            long lastLevelAt = 0;
            while (running) {
                int count = localRecorder.read(chunk, 0, chunk.length, AudioRecord.READ_BLOCKING);
                if (count <= 0) {
                    if (running) {
                        throw new IllegalStateException("手机麦克风读取中断：" + count);
                    }
                    break;
                }
                byte[] frame = Arrays.copyOf(chunk, count);
                for (TargetSink sink : sinks.values()) {
                    sink.offer(frame);
                }
                long now = SystemClock.elapsedRealtime();
                if (now - lastLevelAt >= 100) {
                    listener.onLevel(calculateLevel(chunk, count));
                    lastLevelAt = now;
                }
            }
        } catch (Exception exception) {
            if (running) {
                failure = exception.getMessage();
            }
        } finally {
            synchronized (stateLock) {
                running = false;
                recorder = null;
            }
            for (TargetSink sink : sinks.values()) {
                sink.finish();
            }
            if (localRecorder != null) {
                try {
                    if (localRecorder.getRecordingState() == AudioRecord.RECORDSTATE_RECORDING) {
                        localRecorder.stop();
                    }
                } catch (IllegalStateException ignored) {
                }
                localRecorder.release();
            }
            // All sinks drain in parallel; one slow receiver gets a bounded budget.
            long deadline = SystemClock.elapsedRealtime() + AudioUpload.FINISH_TIMEOUT_MS;
            for (TargetSink sink : sinks.values()) {
                try {
                    long remaining = deadline - SystemClock.elapsedRealtime();
                    if (remaining > 0 && sink.worker != null) {
                        sink.worker.join(remaining);
                    }
                } catch (InterruptedException exception) {
                    Thread.currentThread().interrupt();
                }
                if (sink.worker != null && sink.worker.isAlive()) {
                    sink.stop();
                }
            }
            sinks.clear();
            synchronized (stateLock) {
                captureThread = null;
            }
            listener.onConnectionsChanged(java.util.Collections.emptyMap());
            listener.onStopped(failure);
        }
    }

    void stop() {
        synchronized (stateLock) {
            running = false;
            AudioRecord current = recorder;
            if (current != null) {
                try {
                    current.stop();
                } catch (IllegalStateException ignored) {
                }
            }
        }
        // captureLoop seals queues after offering its last successful read.
    }

    private void notifyConnections() {
        Map<String, Boolean> connections = new HashMap<>();
        for (TargetSink sink : sinks.values()) {
            connections.put(sink.target.computerId, sink.connected);
        }
        listener.onConnectionsChanged(connections);
    }

    private final class TargetSink {
        final Target target;
        final SharedAudioPolicies.FrameQueue queue =
                new SharedAudioPolicies.FrameQueue(QUEUE_FRAMES);
        volatile boolean shouldRun = true;
        volatile boolean finishing;
        volatile boolean connected;
        volatile HttpURLConnection connection;
        Thread worker;

        TargetSink(Target target) {
            this.target = target;
        }

        void start() {
            worker = new Thread(this::writeLoop,
                    "PhoneDeck-Shared-" + target.computerId.substring(
                            0, Math.min(8, target.computerId.length())));
            worker.start();
        }

        boolean isAlive() {
            return shouldRun && worker != null && worker.isAlive();
        }

        void offer(byte[] frame) {
            if (!shouldRun) {
                return;
            }
            queue.offerLatest(frame);
        }

        void writeLoop() {
            SharedAudioPolicies.ReconnectBackoff backoff =
                    new SharedAudioPolicies.ReconnectBackoff();
            while (shouldRun) {
                HttpURLConnection local = null;
                try {
                    local = PhoneDeckHttp.open(
                            target.endpoint, "/api/audio/stream", 1800, 5000);
                    connection = local;
                    local.setRequestMethod("POST");
                    local.setRequestProperty("Content-Type", "audio/L16; rate=48000; channels=1");
                    local.setRequestProperty("X-PhoneDeck-Audio", "pcm-s16le");
                    local.setRequestProperty("X-PhoneDeck-Audio-Mode", "shared");
                    local.setRequestProperty("X-PhoneDeck-Protocol", "2");
                    local.setRequestProperty("X-PhoneDeck-Session", sessionId);
                    local.setRequestProperty("X-PhoneDeck-Computer-Id", target.computerId);
                    local.setChunkedStreamingMode(CHUNK_BYTES);
                    local.setDoOutput(true);
                    try (OutputStream output = local.getOutputStream()) {
                        connected = true;
                        backoff.reset();
                        notifyConnections();
                        while (shouldRun) {
                            byte[] frame = queue.take();
                            if (frame == null) {
                                break;
                            }
                            output.write(frame);
                            output.flush();
                        }
                        if (shouldRun && finishing) {
                            AudioUpload.finish(local, output);
                        }
                    }
                } catch (InterruptedException exception) {
                    Thread.currentThread().interrupt();
                    break;
                } catch (Exception ignored) {
                    // Retry only this computer; capture and other sinks continue.
                } finally {
                    connected = false;
                    connection = null;
                    if (local != null) {
                        local.disconnect();
                    }
                    notifyConnections();
                }
                if (finishing) {
                    break;
                }
                if (shouldRun && !finishing) {
                    try {
                        Thread.sleep(backoff.nextDelayMs());
                    } catch (InterruptedException exception) {
                        Thread.currentThread().interrupt();
                        break;
                    }
                }
            }
            connected = false;
            shouldRun = false;
            notifyConnections();
        }

        void stop() {
            shouldRun = false;
            connected = false;
            if (worker != null) {
                worker.interrupt();
            }
            HttpURLConnection local = connection;
            if (local != null) {
                local.disconnect();
            }
        }

        void finish() {
            finishing = true;
            queue.finish();
            AudioUpload.abortAfter(worker, AudioUpload.FINISH_TIMEOUT_MS, this::stop);
        }
    }

    private static int calculateLevel(byte[] pcm, int count) {
        int sampleCount = count / 2;
        if (sampleCount == 0) {
            return 0;
        }
        double sumSquares = 0;
        for (int index = 0; index + 1 < count; index += 2) {
            short sample = (short) ((pcm[index] & 0xff) | (pcm[index + 1] << 8));
            double normalized = sample / 32768.0;
            sumSquares += normalized * normalized;
        }
        return (int) Math.min(100,
                Math.round(Math.sqrt(sumSquares / sampleCount) * 420));
    }

    @Override
    public void close() {
        stop();
    }
}
