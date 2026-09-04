package com.codex.phonedeck;

import java.util.concurrent.ArrayBlockingQueue;

/** Pure-Java policies shared by the Android audio fan-out and its unit tests. */
final class SharedAudioPolicies {
    private SharedAudioPolicies() {}

    static final class FrameQueue {
        private final ArrayBlockingQueue<byte[]> frames;

        FrameQueue(int capacity) {
            frames = new ArrayBlockingQueue<>(capacity);
        }

        void offerLatest(byte[] frame) {
            if (!frames.offer(frame)) {
                frames.poll();
                frames.offer(frame);
            }
        }

        byte[] take() throws InterruptedException {
            return frames.take();
        }

        byte[] poll() {
            return frames.poll();
        }

        int size() {
            return frames.size();
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
