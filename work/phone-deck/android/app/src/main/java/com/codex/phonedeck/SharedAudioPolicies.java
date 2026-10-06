package com.codex.phonedeck;

import java.util.ArrayDeque;

/** Pure-Java policies shared by the Android audio fan-out and its unit tests. */
final class SharedAudioPolicies {
    private SharedAudioPolicies() {}

    /// M1-B/MIC-10（V21 断言项）：共享组零可达目标的停采窗口（15s 提案，单调时钟）。
    static final long ZERO_TARGET_STOP_WINDOW_MS = 15_000;

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
        private long delayMs = 500;

        long nextDelayMs() {
            long result = delayMs;
            delayMs = Math.min(MAX_DELAY_MS, delayMs * 2);
            return result;
        }

        void reset() {
            delayMs = 500;
        }
    }
}
