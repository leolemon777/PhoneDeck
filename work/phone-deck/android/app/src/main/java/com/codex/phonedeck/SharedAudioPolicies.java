package com.codex.phonedeck;

import java.util.ArrayDeque;

/** Pure-Java policies shared by the Android audio fan-out and its unit tests. */
final class SharedAudioPolicies {
    private SharedAudioPolicies() {}

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
                frames.removeFirst();
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
