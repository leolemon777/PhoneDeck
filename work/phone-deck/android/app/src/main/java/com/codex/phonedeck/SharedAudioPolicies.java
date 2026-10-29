package com.codex.phonedeck;

import java.util.ArrayDeque;

/** Pure-Java policies shared by the Android audio fan-out and its unit tests. */
final class SharedAudioPolicies {
    private SharedAudioPolicies() {}

    /// M1-B/MIC-10（V21 断言项）：共享组零可达目标的停采窗口（15s 提案，单调时钟）。
    static final long ZERO_TARGET_STOP_WINDOW_MS = 15_000;

    /// 每条共享目标连接都由自己的看门狗监督。HttpURLConnection 的
    /// getOutputStream()/write()/flush() 不保证受 readTimeout 约束，因此不能只
    /// 依赖连接对象上的超时；这些预算也必须足够大，避免普通 Wi-Fi/TLS 抖动把
    /// 一台电脑误判成坏连接。
    static final long OUTPUT_OPEN_TIMEOUT_MS = 5_000;
    static final long FIRST_WRITE_TIMEOUT_MS = 5_000;
    static final long WRITE_PROGRESS_TIMEOUT_MS = 5_000;
    static final long WATCHDOG_POLL_MS = 100;
    static final long WATCHDOG_JOIN_TIMEOUT_MS = 250;
    /// A blocked platform disconnect must not accumulate one task per retry.
    /// Each sink has at most one daemon cleaner and a small bounded pending set.
    static final int DISCONNECT_QUEUE_CAPACITY = 4;

    /// 共享模式停止时只给每一台接收端一个有限的尾部收尾预算；慢端不能
    /// 把手机停止共享拖成 8 秒或更久。其余目标仍独立完成各自的收尾。
    static final long SHARED_FINISH_TIMEOUT_MS = 2_000;

    static boolean watchdogExpired(long nowElapsed, long deadlineElapsed) {
        return deadlineElapsed != Long.MIN_VALUE && nowElapsed >= deadlineElapsed;
    }

    static long watchdogSleepMs(long nowElapsed, long deadlineElapsed) {
        long remaining = deadlineElapsed - nowElapsed;
        if (remaining <= 0) {
            return 0;
        }
        return Math.min(WATCHDOG_POLL_MS, remaining);
    }

    static boolean shouldStopForZeroTargets(long emptySinceElapsed, long nowElapsed) {
        return emptySinceElapsed > 0L
                && nowElapsed - emptySinceElapsed >= ZERO_TARGET_STOP_WINDOW_MS;
    }

    /// 与接收端 PcmLatency 一致：RMS 低于约 -48 dBFS 视为静音。
    static final double SILENCE_RMS = 130;

    static boolean isSilent(byte[] pcm) {
        int samples = pcm.length / 2;
        if (samples == 0) {
            return true;
        }
        double sum = 0;
        for (int index = 0; index + 1 < pcm.length; index += 2) {
            short sample = (short) ((pcm[index] & 0xff) | (pcm[index + 1] << 8));
            sum += (double) sample * sample;
        }
        return Math.sqrt(sum / samples) < SILENCE_RMS;
    }

    /// 网络短暂卡顿时的有界队列：满了优先丢最旧的静音帧，没有静音才丢最旧帧，
    /// 尽量不吞字，同时不积累成秒级延迟。
    static final class FrameQueue {
        private final ArrayDeque<byte[]> frames = new ArrayDeque<>();
        private final int capacity;
        private boolean finished;

        FrameQueue(int capacity) {
            if (capacity <= 0) {
                throw new IllegalArgumentException("capacity must be positive");
            }
            this.capacity = capacity;
        }

        synchronized void offerLatest(byte[] frame) {
            if (finished) {
                return;
            }
            if (frames.size() == capacity) {
                java.util.Iterator<byte[]> oldest = frames.iterator();
                boolean droppedSilence = false;
                while (oldest.hasNext()) {
                    if (isSilent(oldest.next())) {
                        oldest.remove();
                        droppedSilence = true;
                        break;
                    }
                }
                if (!droppedSilence) {
                    frames.removeFirst();
                }
            }
            frames.addLast(frame);
            notifyAll();
        }

        synchronized byte[] take() throws InterruptedException {
            while (frames.isEmpty() && !finished) {
                wait();
            }
            return frames.pollFirst();
        }

        synchronized byte[] poll() {
            return frames.pollFirst();
        }

        /// 最多等待 timeoutMs 取下一帧；结束或超时返回 null（用于把两帧合并成一个网络包）。
        synchronized byte[] poll(long timeoutMs) throws InterruptedException {
            long deadline = System.nanoTime() + timeoutMs * 1_000_000L;
            while (frames.isEmpty() && !finished) {
                long remaining = (deadline - System.nanoTime()) / 1_000_000L;
                if (remaining <= 0) {
                    break;
                }
                wait(remaining);
            }
            return frames.pollFirst();
        }

        synchronized int size() {
            return frames.size();
        }

        // EOF only after the already captured frames have been consumed.
        synchronized void finish() {
            finished = true;
            notifyAll();
        }
    }

    static final class ReconnectBackoff {
        private static final long MAX_DELAY_MS = 30_000;
        /// 只有连续实际写入一段时间后，才把失败退避恢复到初始值。
        /// 仅拿到 HTTP OutputStream 不能证明接收端已经接受音频。
        static final long STABLE_WRITE_WINDOW_MS = 5_000;
        /// 共享音频正常约每 20--40 ms 写一次；超过此间隔视为连接没有持续供音。
        static final long MAX_WRITE_GAP_MS = 1_000;
        private long delayMs = 500;
        private long connectionStartedAt = Long.MIN_VALUE;
        private long lastWriteAt = Long.MIN_VALUE;

        long nextDelayMs() {
            long result = delayMs;
            delayMs = Math.min(MAX_DELAY_MS, delayMs * 2);
            return result;
        }

        void reset() {
            delayMs = 500;
        }

        /// 标记一次新的连接尝试；此时不能重置退避，因为尚未写入任何音频。
        void connectionOpened(long nowElapsed) {
            // 稳定窗口从第一次成功写入开始，不能把 TLS/HTTP 建连时间算进去。
            connectionStartedAt = Long.MIN_VALUE;
            lastWriteAt = Long.MIN_VALUE;
        }

        /// 标记一次成功写入。只有连续写入达到稳定窗口后才恢复 500 ms 起步。
        void frameWritten(long nowElapsed) {
            if (connectionStartedAt == Long.MIN_VALUE) {
                connectionStartedAt = nowElapsed;
                lastWriteAt = nowElapsed;
                return;
            }
            if (nowElapsed - lastWriteAt > MAX_WRITE_GAP_MS) {
                // 连接虽未关闭，但长时间没有实际进度；重新开始稳定计时。
                connectionStartedAt = nowElapsed;
            }
            lastWriteAt = nowElapsed;
            if (nowElapsed - connectionStartedAt >= STABLE_WRITE_WINDOW_MS) {
                reset();
            }
        }

        /// 标记连接结束，避免下一轮把上一次的连接时长带进来。
        void connectionClosed() {
            connectionStartedAt = Long.MIN_VALUE;
            lastWriteAt = Long.MIN_VALUE;
        }
    }
}
