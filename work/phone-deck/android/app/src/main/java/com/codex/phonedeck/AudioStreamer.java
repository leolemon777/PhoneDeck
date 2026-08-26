package com.codex.phonedeck;

import android.Manifest;
import android.content.Context;
import android.content.pm.PackageManager;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;

import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;

final class AudioStreamer implements AutoCloseable {
    interface Listener {
        void onReady();
        void onLevel(int percent);
        void onStopped(String reason);
    }

    private static final int SAMPLE_RATE = 48_000;
    private final Context context;
    private final String server;
    private final Listener listener;
    private final Object syncRoot = new Object();
    private volatile boolean shouldRun;
    private volatile boolean streaming;
    private volatile AudioRecord recorder;
    private Thread worker;

    AudioStreamer(Context context, String server, Listener listener) {
        this.context = context.getApplicationContext();
        this.server = server;
        this.listener = listener;
    }

    boolean isStreaming() {
        return streaming;
    }

    void start() {
        synchronized (syncRoot) {
            if (worker != null && worker.isAlive()) {
                return;
            }
            shouldRun = true;
            worker = new Thread(this::runStream, "PhoneDeck-Microphone");
            worker.start();
        }
    }

    void stop() {
        shouldRun = false;
        AudioRecord current = recorder;
        if (current != null) {
            try {
                current.stop();
            } catch (IllegalStateException ignored) {
                // 录音尚未完全启动或已经停止。
            }
        }
    }

    private void runStream() {
        HttpURLConnection connection = null;
        AudioRecord localRecorder = null;
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

            connection = (HttpURLConnection) new URL(server + "/api/audio/stream").openConnection();
            connection.setConnectTimeout(1800);
            connection.setReadTimeout(2500);
            connection.setRequestMethod("POST");
            connection.setRequestProperty("Content-Type", "audio/L16; rate=48000; channels=1");
            connection.setRequestProperty("X-PhoneDeck-Audio", "pcm-s16le");
            connection.setChunkedStreamingMode(8192);
            connection.setDoOutput(true);

            byte[] buffer = new byte[bufferSize];
            try (OutputStream output = connection.getOutputStream()) {
                localRecorder.startRecording();
                streaming = true;
                listener.onReady();
                long lastLevelUpdate = 0;
                while (shouldRun) {
                    int count = localRecorder.read(
                            buffer, 0, buffer.length, AudioRecord.READ_BLOCKING);
                    if (count <= 0) {
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
            recorder = null;
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
            listener.onStopped(stoppedReason);
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
