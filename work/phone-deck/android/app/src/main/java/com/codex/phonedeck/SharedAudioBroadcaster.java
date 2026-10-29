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
        void onLevel(String sessionId, int percent);
        void onConnectionsChanged(String sessionId);
        void onStopped(String sessionId, String reason);
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
    // 240 ms：吸收常见 Wi-Fi 抖动；满了优先丢静音（见 FrameQueue），接收端再跳过积压静音。
    private static final int QUEUE_FRAMES = 12;
    /// 共享模式长时间（含锁屏）供音：两帧 20 ms 合并成一个约 40 ms 的网络包，
    /// TLS 记录与无线唤醒次数减半，代价是最多多出 20 ms 延迟。
    static final long BATCH_WAIT_MS = 25;

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
            captureThread = new Thread(() -> captureLoop(newSessionId), "PhoneDeck-SharedCapture");
            captureThread.start();
            return true;
        }
    }

    void updateTargets(Map<String, Target> requested) {
        java.util.List<TargetSink> stopped;
        synchronized (stateLock) {
            stopped = updateTargetsLocked(requested);
        }
        stopSinks(stopped);
    }

    /**
     * Applies a probe result only while the expected audio session is still
     * current.  The operation intentionally runs on the probe executor: an
     * old sink may need {@link HttpURLConnection#disconnect()} and must never
     * block the service main thread during stop or network handover.
     */
    boolean updateTargetsIfCurrent(Map<String, Target> requested,
                                   String expectedSessionId,
                                   java.util.function.BooleanSupplier guard) {
        java.util.List<TargetSink> stopped;
        synchronized (stateLock) {
            if (!running || expectedSessionId == null
                    || !expectedSessionId.equals(sessionId)
                    || guard != null && !guard.getAsBoolean()) {
                return false;
            }
            stopped = updateTargetsLocked(requested);
        }
        // Disconnecting a stale sink can touch a blocked socket.  Keep it
        // outside stateLock; this method is called on the probe executor.
        stopSinks(stopped);
        return true;
    }

    private java.util.List<TargetSink> updateTargetsLocked(Map<String, Target> requested) {
        java.util.List<TargetSink> stopped = new java.util.ArrayList<>();
        if (!running) {
            return stopped;
        }
        Map<String, Target> desired = new HashMap<>(requested);
        for (Map.Entry<String, TargetSink> entry : sinks.entrySet()) {
            Target next = desired.remove(entry.getKey());
            TargetSink existing = entry.getValue();
            if (next == null || !existing.target.sameEndpoint(next) || !existing.isAlive()) {
                if (sinks.remove(entry.getKey(), existing)) {
                    stopped.add(existing);
                }
                if (next != null) {
                    addSink(next);
                }
            }
        }
        for (Target target : desired.values()) {
            addSink(target);
        }
        notifyConnections(sessionId);
        return stopped;
    }

    private static void stopSinks(java.util.List<TargetSink> stopped) {
        for (TargetSink sink : stopped) {
            sink.stop();
        }
    }

    /** Returns only the in-memory sink connection state; never performs I/O. */
    Map<String, Boolean> connectionSnapshot() {
        synchronized (stateLock) {
            Map<String, Boolean> snapshot = new HashMap<>();
            for (Map.Entry<String, TargetSink> entry : sinks.entrySet()) {
                snapshot.put(entry.getKey(), entry.getValue().connected);
            }
            return snapshot;
        }
    }

    private void addSink(Target target) {
        TargetSink sink = new TargetSink(target, sessionId);
        TargetSink previous = sinks.putIfAbsent(target.computerId, sink);
        if (previous == null) {
            sink.start();
        }
    }

    @android.annotation.SuppressLint("MissingPermission")
    private void captureLoop(String captureSessionId) {
        android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO);
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
                    listener.onLevel(captureSessionId, calculateLevel(chunk, count));
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
            listener.onConnectionsChanged(captureSessionId);
            listener.onStopped(captureSessionId, failure);
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

    private void notifyConnections(String eventSessionId) {
        // Consumers read the current connection snapshot when handling this
        // notification, so a queued callback cannot replay obsolete flags.
        listener.onConnectionsChanged(eventSessionId);
    }

    private final class TargetSink {
        final Target target;
        final String ownerSessionId;
        final SharedAudioPolicies.FrameQueue queue =
                new SharedAudioPolicies.FrameQueue(QUEUE_FRAMES);
        volatile boolean shouldRun = true;
        volatile boolean finishing;
        volatile boolean connected;
        volatile HttpURLConnection connection;
        Thread worker;

        TargetSink(Target target, String ownerSessionId) {
            this.target = target;
            this.ownerSessionId = ownerSessionId;
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
            android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_AUDIO);
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
                        local.setRequestProperty("X-PhoneDeck-Session", ownerSessionId);
                    local.setRequestProperty("X-PhoneDeck-Computer-Id", target.computerId);
                    local.setChunkedStreamingMode(CHUNK_BYTES);
                    local.setDoOutput(true);
                    backoff.connectionOpened(SystemClock.elapsedRealtime());
                    try (OutputStream output = local.getOutputStream()) {
                        connected = true;
                        notifyConnections(ownerSessionId);
                        while (shouldRun) {
                            byte[] frame = queue.take();
                            if (frame == null) {
                                break;
                            }
                            output.write(frame);
                            byte[] next = queue.poll(BATCH_WAIT_MS);
                            if (next != null) {
                                output.write(next);
                            }
                            output.flush();
                            backoff.frameWritten(SystemClock.elapsedRealtime());
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
                    backoff.connectionClosed();
                    connected = false;
                    connection = null;
                    if (local != null) {
                        local.disconnect();
                    }
                    notifyConnections(ownerSessionId);
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
            notifyConnections(ownerSessionId);
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
