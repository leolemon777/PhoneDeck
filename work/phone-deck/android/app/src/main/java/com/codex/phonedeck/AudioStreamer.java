package com.codex.phonedeck;

import android.Manifest;
import android.content.Context;
import android.content.pm.PackageManager;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.os.SystemClock;
import android.util.Log;

import java.io.OutputStream;
import java.net.HttpURLConnection;

final class AudioStreamer implements AutoCloseable {
    interface Listener {
        void onReady(String sessionId);
        void onLevel(int percent);
        void onStopped(String sessionId, String reason);
    }

    private static final String LOG_TAG = "PhoneDeckAudio";

    private static final int SAMPLE_RATE = 48_000;

    /// 每次上传的 PCM 帧长：20ms 一帧，首帧不必等满整个系统缓冲。
    private static final int READ_CHUNK_MS = 20;
    private static final int READ_CHUNK_BYTES = SAMPLE_RATE * 2 * READ_CHUNK_MS / 1_000;

    /// 点击后先录音、连接建立前最多暂存 1 秒 PCM，保证第一音节不丢。
    private static final int PRE_ROLL_MS = 1_000;
    private static final int PRE_ROLL_BYTES = SAMPLE_RATE * 2 * PRE_ROLL_MS / 1_000;

    private static final int PAUSE_KEEPALIVE_INTERVAL_MS = 100;
    private static final int PAUSE_SILENCE_BYTES =
            SAMPLE_RATE * 2 * PAUSE_KEEPALIVE_INTERVAL_MS / 1_000;

    /// 停止时追加的尾部静音时长：把真实尾音推过 网络→WASAPI→CABLE→Typeless
    /// 整条管道，避免停止键到达时尾音还躺在缓冲里未被识别。
    private static final int TAIL_SILENCE_MS = 500;
    private static final int TAIL_SILENCE_BYTES = SAMPLE_RATE * 2 * TAIL_SILENCE_MS / 1_000;
    /// 关流后的沉降等待：让 chunked 终止块和尾部数据完成发送、服务器有机会
    /// 读完流并进入排空，再断开连接（过早 disconnect 的 RST 会打断服务器排空）。
    private static final int TAIL_SETTLE_MS = 400;

    private final Context context;
    private final Listener listener;
    private final Object syncRoot = new Object();
    private volatile boolean shouldRun;
    private volatile boolean streaming;
    private volatile boolean paused;
    private volatile boolean recorderNeedsRestart;
    private volatile AudioRecord recorder;
    private volatile HttpURLConnection activeConnection;
    private volatile OutputStream linkOutput;
    private volatile Exception linkFailure;
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
            linkOutput = null;
            linkFailure = null;
            activeConnection = null;
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
        Log.i(LOG_TAG, "stopRequested streaming=" + streaming
                + " running=" + isRunning());
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
        // 不在这里 disconnect：立即断开会丢弃 TCP 缓冲里尚未送达的尾音
        // （接收端可能收到 RST 直接清掉未读 PCM）。优雅收尾
        // （尾部静音 + 正常结束 chunked 流）由工作线程的 finishLinkTail 完成；
        // 工作线程异常退出时也由其 finally 兜底断开。
    }

    /// 点击后最前面的 PCM 暂存环：只保留最近 PRE_ROLL_BYTES，
    /// 连接建立后按原写入顺序一次性灌出（不重复、不乱序、不丢失）。
    private static final class PreRollRing {
        private final byte[] store = new byte[PRE_ROLL_BYTES];
        private int writeIndex;
        private int stored;

        void write(byte[] source, int offset, int count) {
            if (count <= 0) {
                return;
            }
            if (count >= store.length) {
                System.arraycopy(
                        source, offset + count - store.length, store, 0, store.length);
                writeIndex = 0;
                stored = store.length;
                return;
            }
            int first = Math.min(count, store.length - writeIndex);
            System.arraycopy(source, offset, store, writeIndex, first);
            if (count > first) {
                System.arraycopy(source, offset + first, store, 0, count - first);
            }
            writeIndex = (writeIndex + count) % store.length;
            stored = Math.min(stored + count, store.length);
        }

        int drainTo(OutputStream output) throws Exception {
            int total = stored;
            if (total == 0) {
                return 0;
            }
            int start = (writeIndex - stored + store.length) % store.length;
            int first = Math.min(stored, store.length - start);
            if (first > 0) {
                output.write(store, start, first);
            }
            if (stored > first) {
                output.write(store, 0, stored - first);
            }
            stored = 0;
            writeIndex = 0;
            return total;
        }
    }

    private void runStream(
            String sessionId,
            String targetComputerId,
            PhoneDeckEndpoint endpoint) {
        AudioRecord localRecorder = null;
        String stoppedReason = null;
        final long startedAt = SystemClock.elapsedRealtime();
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

            // 先开录：点击后立刻开始捕获，语音进入 pre-roll 环，
            // 不等 TLS 握手完成，用户立即开口也不会丢第一音节。
            localRecorder.startRecording();
            log(sessionId, "audioRecordStarted", startedAt);

            Thread connectThread = new Thread(
                    () -> openLink(sessionId, targetComputerId, endpoint),
                    "PhoneDeck-Microphone-Connect");
            connectThread.start();

            PreRollRing preRoll = new PreRollRing();
            byte[] chunk = new byte[READ_CHUNK_BYTES];
            byte[] silence = new byte[PAUSE_SILENCE_BYTES];
            boolean preRollFlushed = false;
            boolean firstPcmLogged = false;
            long lastLevelUpdate = 0;
            while (shouldRun) {
                if (paused) {
                    if (linkOutput != null) {
                        // 已建立连接：继续发送静音维持同一 HTTP/Typeless 会话。
                        linkOutput.write(silence);
                        linkOutput.flush();
                        long now = SystemClock.elapsedRealtime();
                        if (now - lastLevelUpdate >= 200) {
                            listener.onLevel(0);
                            lastLevelUpdate = now;
                        }
                    }
                    Thread.sleep(PAUSE_KEEPALIVE_INTERVAL_MS);
                    continue;
                }
                if (localRecorder.getRecordingState()
                        != AudioRecord.RECORDSTATE_RECORDING) {
                    // 从暂停恢复：同一 AudioRecord 实例重新开始采集。
                    localRecorder.startRecording();
                }
                int count = localRecorder.read(
                        chunk, 0, chunk.length, AudioRecord.READ_BLOCKING);
                if (count <= 0) {
                    throw new IllegalStateException("手机麦克风读取中断：" + count);
                }
                if (!firstPcmLogged) {
                    log(sessionId, "firstPcm", startedAt);
                    firstPcmLogged = true;
                }
                if (linkFailure != null) {
                    throw linkFailure;
                }
                OutputStream output = linkOutput;
                if (output == null) {
                    // 连接仍在建立：先按序暂存。
                    preRoll.write(chunk, 0, count);
                    long now = SystemClock.elapsedRealtime();
                    if (now - lastLevelUpdate >= 100) {
                        listener.onLevel(calculateLevel(chunk, count));
                        lastLevelUpdate = now;
                    }
                    continue;
                }
                if (!preRollFlushed) {
                    int preRolled = preRoll.drainTo(output);
                    output.flush();
                    preRollFlushed = true;
                    log(sessionId, "prerollFlushed bytes=" + preRolled, startedAt);
                }
                output.write(chunk, 0, count);
                output.flush();
                long now = SystemClock.elapsedRealtime();
                if (now - lastLevelUpdate >= 100) {
                    listener.onLevel(calculateLevel(chunk, count));
                    lastLevelUpdate = now;
                }
            }
        } catch (Exception exception) {
            if (shouldRun) {
                stoppedReason = exception.getMessage();
                if (stoppedReason == null || stoppedReason.isBlank()) {
                    stoppedReason = exception.getClass().getSimpleName();
                }
            }
        } finally {
            // 优雅收尾必须无论正常退出还是异常都执行：垫尾部静音并正常
            // 结束 chunked 流，让接收端读到干净 EOS 后按既有排空逻辑收尾。
            finishLinkTail();
            shouldRun = false;
            streaming = false;
            paused = false;
            recorderNeedsRestart = false;
            recorder = null;
            linkOutput = null;
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
            HttpURLConnection connection = activeConnection;
            if (connection != null) {
                connection.disconnect();
            }
            log(sessionId, "sessionStopped", startedAt);
            synchronized (syncRoot) {
                worker = null;
            }
            listener.onStopped(sessionId, stoppedReason);
        }
    }

    /// 尾垫：停止采集后追加约 500ms 静音并正常结束 chunked 流。
    /// 这段静音把真实尾音推过 网络→WASAPI→CABLE→Typeless 整条管道；
    /// 接收端读到干净 EOS 后按既有排空逻辑收尾，再停 Typeless，
    /// 结尾几个字不再因缓冲未排空而被截断。链路已断时失败静默。
    private void finishLinkTail() {
        OutputStream output = linkOutput;
        if (output == null) {
            Log.w(LOG_TAG, "tail skipped: link not established");
            return;
        }
        try {
            output.write(new byte[TAIL_SILENCE_BYTES]);
            output.flush();
            Log.i(LOG_TAG, "tail padded bytes=" + TAIL_SILENCE_BYTES);
        } catch (Exception exception) {
            Log.w(LOG_TAG, "tail write failed: " + exception);
        }
        try {
            output.close();
            Log.i(LOG_TAG, "tail closed");
        } catch (Exception exception) {
            Log.w(LOG_TAG, "tail close failed: " + exception);
        }
        try {
            Thread.sleep(TAIL_SETTLE_MS);
        } catch (InterruptedException exception) {
            Log.w(LOG_TAG, "tail settle interrupted");
            Thread.currentThread().interrupt();
        }
    }

    /// 连接线程：建立 HTTPS 音频请求；成功即回调 onReady，
    /// 让 /dictation/start 与 Typeless 启动和 pre-roll 灌入并行。
    private void openLink(
            String sessionId,
            String targetComputerId,
            PhoneDeckEndpoint endpoint) {
        final long startedAt = SystemClock.elapsedRealtime();
        log(sessionId, "requestStarted", startedAt);
        HttpURLConnection connection = null;
        try {
            connection = PhoneDeckHttp.open(
                    endpoint, "/api/audio/stream", 1800, 2500);
            connection.setRequestMethod("POST");
            connection.setRequestProperty(
                    "Content-Type", "audio/L16; rate=48000; channels=1");
            connection.setRequestProperty("X-PhoneDeck-Audio", "pcm-s16le");
            connection.setRequestProperty("X-PhoneDeck-Session", sessionId);
            if (targetComputerId != null && !targetComputerId.isBlank()) {
                connection.setRequestProperty("X-PhoneDeck-Protocol", "2");
                connection.setRequestProperty(
                        "X-PhoneDeck-Computer-Id", targetComputerId);
            }
            connection.setChunkedStreamingMode(READ_CHUNK_BYTES);
            connection.setDoOutput(true);
            activeConnection = connection;
            OutputStream output = connection.getOutputStream();
            log(sessionId, "serverHeaders", startedAt);
            linkOutput = output;
            streaming = true;
            listener.onReady(sessionId);
        } catch (Exception exception) {
            linkFailure = exception;
        } finally {
            if (connection != null && (linkFailure != null || !shouldRun)) {
                // 失败或工作线程已退出：由连接线程负责断开，避免句柄滞留。
                connection.disconnect();
            }
        }
    }

    private static void log(String sessionId, String stage, long startedAt) {
        Log.i(LOG_TAG, sessionId + " " + stage
                + " +" + (SystemClock.elapsedRealtime() - startedAt) + "ms");
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
