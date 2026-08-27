package com.codex.phonedeck;

import android.Manifest;
import android.content.Context;
import android.content.pm.PackageManager;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;

import java.io.OutputStream;
import java.net.HttpURLConnection;

final class AudioStreamer implements AutoCloseable {
    interface Listener {
        void onReady(String sessionId);
        void onLevel(int percent);
        void onStopped(String sessionId, String reason);
    }

    private static final int SAMPLE_RATE = 48_000;
    private static final int PAUSE_KEEPALIVE_INTERVAL_MS = 100;
    private static final int PAUSE_SILENCE_BYTES =
            SAMPLE_RATE * 2 * PAUSE_KEEPALIVE_INTERVAL_MS / 1_000;
    private final Context context;
    private final Listener listener;
    private final Object syncRoot = new Object();
    private volatile boolean shouldRun;
    private volatile boolean streaming;
    private volatile boolean paused;
    private volatile boolean recorderNeedsRestart;
    private volatile AudioRecord recorder;
    private volatile HttpURLConnection activeConnection;
    private Thread worker;

    AudioStreamer(Context context, Listener listener) {
        this.context = context.getApplicationContext();
        this.listener = listener;
    }

    boolean isStreaming() {
        return streaming;
    }

    boolean isRunning() {
        synchronized (syncRoot) {
            return worker != null && worker.isAlive();
        }
    }

    boolean isPaused() {
        return paused;
    }

    boolean start(
            String sessionId,
            String targetComputerId,
            PhoneDeckEndpoint endpoint) {
        if (sessionId == null || sessionId.isBlank() || sessionId.length() > 128) {
            throw new IllegalArgumentException("无效的音频 sessionId");
        }
        if (targetComputerId != null
                && (targetComputerId.isBlank() || targetComputerId.length() > 128)) {
            throw new IllegalArgumentException("无效的目标电脑 ID");
        }
        if (endpoint == null) {
            throw new IllegalArgumentException("目标电脑没有可用连接");
        }
        synchronized (syncRoot) {
            if (worker != null && worker.isAlive()) {
                return false;
            }
            shouldRun = true;
            paused = false;
            recorderNeedsRestart = false;
            worker = new Thread(
                    () -> runStream(sessionId, targetComputerId, endpoint),
                    "PhoneDeck-Microphone");
            worker.start();
            return true;
        }
    }

    boolean pause() {
        if (!streaming || !isRunning()) {
            return false;
        }
        paused = true;
        recorderNeedsRestart = true;
        AudioRecord current = recorder;
        if (current != null) {
            try {
                current.stop();
            } catch (IllegalStateException ignored) {
                // 工作线程可能已经完成暂停。
            }
        }
        return true;
    }

    boolean resume() {
        if (!streaming || !isRunning()) {
            return false;
        }
        paused = false;
        return true;
    }

    void stop() {
        shouldRun = false;
        paused = false;
        recorderNeedsRestart = false;
        AudioRecord current = recorder;
        if (current != null) {
            try {
                current.stop();
            } catch (IllegalStateException ignored) {
                // 录音尚未完全启动或已经停止。
            }
        }
        HttpURLConnection connection = activeConnection;
        if (connection != null) {
            connection.disconnect();
        }
    }

    private void runStream(
            String sessionId,
            String targetComputerId,
            PhoneDeckEndpoint endpoint) {
        HttpURLConnection connection = null;
        AudioRecord localRecorder = null;
        boolean recorderStarted = false;
        String stoppedReason = null;
        try {
            if (context.checkSelfPermission(Manifest.permission.RECORD_AUDIO)
                    != PackageManager.PERMISSION_GRANTED) {
                throw new SecurityException("没有手机麦克风权限");
            }
            int minimum = AudioRecord.getMinBufferSize(
                    SAMPLE_RATE,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT);
            if (minimum <= 0) {
                throw new IllegalStateException("手机不支持 48 kHz 麦克风采集");
            }
            int bufferSize = Math.max(minimum * 2, 8192);
            localRecorder = new AudioRecord(
                    MediaRecorder.AudioSource.VOICE_RECOGNITION,
                    SAMPLE_RATE,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT,
                    bufferSize);
            if (localRecorder.getState() != AudioRecord.STATE_INITIALIZED) {
                throw new IllegalStateException("无法初始化手机麦克风");
            }
            recorder = localRecorder;

            connection = PhoneDeckHttp.open(
                    endpoint, "/api/audio/stream", 1800, 2500);
            connection.setRequestMethod("POST");
            connection.setRequestProperty("Content-Type", "audio/L16; rate=48000; channels=1");
            connection.setRequestProperty("X-PhoneDeck-Audio", "pcm-s16le");
            connection.setRequestProperty("X-PhoneDeck-Session", sessionId);
            if (targetComputerId != null && !targetComputerId.isBlank()) {
                connection.setRequestProperty("X-PhoneDeck-Protocol", "2");
                connection.setRequestProperty(
                        "X-PhoneDeck-Computer-Id", targetComputerId);
            }
            connection.setChunkedStreamingMode(8192);
            connection.setDoOutput(true);
            activeConnection = connection;

            byte[] buffer = new byte[bufferSize];
            byte[] silence = new byte[PAUSE_SILENCE_BYTES];
            try (OutputStream output = connection.getOutputStream()) {
                localRecorder.startRecording();
                recorderStarted = true;
                streaming = true;
                listener.onReady(sessionId);
                long lastLevelUpdate = 0;
                while (shouldRun) {
                    if (paused) {
                        if (recorderStarted) {
                            try {
                                localRecorder.stop();
                            } catch (IllegalStateException ignored) {
                                // pause() 可能已经停止录音。
                            }
                            recorderStarted = false;
                        }
                        output.write(silence);
                        output.flush();
                        long now = android.os.SystemClock.elapsedRealtime();
                        if (now - lastLevelUpdate >= 200) {
                            listener.onLevel(0);
                            lastLevelUpdate = now;
                        }
                        Thread.sleep(PAUSE_KEEPALIVE_INTERVAL_MS);
                        continue;
                    }
                    if (!recorderStarted || recorderNeedsRestart) {
                        localRecorder.startRecording();
                        recorderStarted = true;
                        recorderNeedsRestart = false;
                    }
                    int count = localRecorder.read(
                            buffer, 0, buffer.length, AudioRecord.READ_BLOCKING);
                    if (count <= 0) {
                        if (shouldRun && (paused || recorderNeedsRestart)) {
                            recorderStarted = false;
                            continue;
                        }
                        if (shouldRun) {
                            throw new IllegalStateException("手机麦克风读取中断：" + count);
                        }
                        break;
                    }
                    output.write(buffer, 0, count);
                    long now = android.os.SystemClock.elapsedRealtime();
                    if (now - lastLevelUpdate >= 100) {
                        listener.onLevel(calculateLevel(buffer, count));
                        lastLevelUpdate = now;
                    }
                }
                output.flush();
            }
        } catch (Exception exception) {
            if (shouldRun) {
                stoppedReason = exception.getMessage();
                if (stoppedReason == null || stoppedReason.isBlank()) {
                    stoppedReason = exception.getClass().getSimpleName();
                }
            }
        } finally {
            shouldRun = false;
            streaming = false;
            paused = false;
            recorderNeedsRestart = false;
            recorder = null;
            activeConnection = null;
            if (localRecorder != null) {
                try {
                    if (localRecorder.getRecordingState() == AudioRecord.RECORDSTATE_RECORDING) {
                        localRecorder.stop();
                    }
                } catch (IllegalStateException ignored) {
                    // 已由 stop() 结束。
                }
                localRecorder.release();
            }
            if (connection != null) {
                connection.disconnect();
            }
            synchronized (syncRoot) {
                worker = null;
            }
            listener.onStopped(sessionId, stoppedReason);
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
        double rms = Math.sqrt(sumSquares / sampleCount);
        return (int) Math.min(100, Math.round(rms * 420));
    }

    @Override
    public void close() {
        stop();
    }
}
