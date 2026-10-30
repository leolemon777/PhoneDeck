package com.codex.phonedeck;

import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertEquals;

import org.junit.Test;

public final class SharedAudioPoliciesTest {
    @Test
    public void finishPreservesAllQueuedTailFramesInOrder() throws Exception {
        SharedAudioPolicies.FrameQueue queue = new SharedAudioPolicies.FrameQueue(3);
        queue.offerLatest(new byte[] {1});
        queue.offerLatest(new byte[] {2});
        queue.offerLatest(new byte[] {3});
        queue.finish();
        queue.offerLatest(new byte[] {4}); // A stale producer must not replace the tail.

        assertArrayEquals(new byte[] {1}, queue.take());
        assertArrayEquals(new byte[] {2}, queue.take());
        assertArrayEquals(new byte[] {3}, queue.take());
        org.junit.Assert.assertNull(queue.take());
    }

    @Test
    public void finishWakesEmptySenderWithoutInterruptingIt() throws Exception {
        SharedAudioPolicies.FrameQueue queue = new SharedAudioPolicies.FrameQueue(2);
        java.util.concurrent.FutureTask<byte[]> read =
                new java.util.concurrent.FutureTask<>(queue::take);
        Thread worker = new Thread(read);
        worker.start();
        try {
            queue.finish();
            org.junit.Assert.assertNull(read.get(2, java.util.concurrent.TimeUnit.SECONDS));
        } finally {
            worker.interrupt();
            worker.join(2_000);
        }
    }

    @Test
    public void slowReceiverDropsOnlyItsOwnOldestFrames() throws Exception {
        SharedAudioPolicies.FrameQueue fast = new SharedAudioPolicies.FrameQueue(2);
        SharedAudioPolicies.FrameQueue slow = new SharedAudioPolicies.FrameQueue(2);
        byte[] first = {1};
        byte[] second = {2};
        byte[] third = {3};

        fast.offerLatest(first);
        slow.offerLatest(first);
        assertArrayEquals(first, fast.take());
        fast.offerLatest(second);
        slow.offerLatest(second);
        assertArrayEquals(second, fast.take());
        fast.offerLatest(third);
        slow.offerLatest(third);

        assertEquals(1, fast.size());
        assertArrayEquals(third, fast.take());
        assertEquals(2, slow.size());
        assertArrayEquals(second, slow.take());
        assertArrayEquals(third, slow.take());
    }

    @Test
    public void reconnectBackoffIsBoundedAndResettable() {
        SharedAudioPolicies.ReconnectBackoff backoff =
                new SharedAudioPolicies.ReconnectBackoff();

        assertEquals(500, backoff.nextDelayMs());
        assertEquals(1_000, backoff.nextDelayMs());
        for (int index = 0; index < 10; index++) {
            backoff.nextDelayMs();
        }
        assertEquals(30_000, backoff.nextDelayMs());
        backoff.reset();
        assertEquals(500, backoff.nextDelayMs());
    }

    @Test
    public void rapidConnectionFailuresKeepIncreasingBackoffUntilStableWrites() {
        SharedAudioPolicies.ReconnectBackoff backoff =
                new SharedAudioPolicies.ReconnectBackoff();

        long now = 10_000;
        assertEquals(500, backoff.nextDelayMs());
        backoff.connectionOpened(now);
        backoff.frameWritten(now + 100);
        backoff.connectionClosed();

        now += 500;
        assertEquals(1_000, backoff.nextDelayMs());
        backoff.connectionOpened(now);
        backoff.frameWritten(now + 200);
        backoff.connectionClosed();

        now += 1_000;
        assertEquals(2_000, backoff.nextDelayMs());
        backoff.connectionOpened(now);
        backoff.frameWritten(now + 300);
        backoff.connectionClosed();

        // 只有连续实际写入满 5 秒后才恢复初始退避。
        now += 2_000;
        backoff.connectionOpened(now);
        backoff.frameWritten(now);
        for (int index = 1; index < 125; index++) {
            backoff.frameWritten(now + index * 40L);
        }
        assertEquals(4_000, backoff.nextDelayMs());
        backoff.frameWritten(now + 5_000);
        backoff.connectionClosed();
        assertEquals(500, backoff.nextDelayMs());
    }

    @Test
    public void connectionSetupWithoutSustainedWritesCannotResetBackoff() {
        SharedAudioPolicies.ReconnectBackoff backoff =
                new SharedAudioPolicies.ReconnectBackoff();

        assertEquals(500, backoff.nextDelayMs());
        backoff.connectionOpened(1_000);
        // 模拟 getOutputStream 成功但迟迟没有任何 PCM 写入。
        assertEquals(1_000, backoff.nextDelayMs());
        backoff.frameWritten(6_001); // 首次写入才开始稳定计时。
        assertEquals(2_000, backoff.nextDelayMs());

        backoff.connectionOpened(7_000);
        backoff.frameWritten(7_000);
        backoff.frameWritten(8_001); // 超过允许间隔，重新开始稳定计时。
        assertEquals(4_000, backoff.nextDelayMs());
    }

    private static byte[] voiced(byte marker) {
        byte[] frame = new byte[1_920];
        for (int index = 0; index + 1 < frame.length; index += 2) {
            short sample = (short) (((index / 2 / 24) % 2 == 0 ? 3_000 : -3_000) + marker);
            frame[index] = (byte) sample;
            frame[index + 1] = (byte) (sample >> 8);
        }
        return frame;
    }

    @Test
    public void fullQueueDropsOldestSilenceBeforeAnyVoice() throws Exception {
        SharedAudioPolicies.FrameQueue queue = new SharedAudioPolicies.FrameQueue(3);
        byte[] voiceA = voiced((byte) 1);
        byte[] silence = new byte[1_920];
        byte[] voiceB = voiced((byte) 2);
        byte[] voiceC = voiced((byte) 3);
        queue.offerLatest(voiceA);
        queue.offerLatest(silence);
        queue.offerLatest(voiceB);

        queue.offerLatest(voiceC);

        assertArrayEquals(voiceA, queue.take());
        assertArrayEquals(voiceB, queue.take());
        assertArrayEquals(voiceC, queue.take());
    }

    @Test
    public void fullQueueOfVoiceStillDropsOldestToStayLive() throws Exception {
        SharedAudioPolicies.FrameQueue queue = new SharedAudioPolicies.FrameQueue(2);
        byte[] first = voiced((byte) 1);
        byte[] second = voiced((byte) 2);
        byte[] third = voiced((byte) 3);
        queue.offerLatest(first);
        queue.offerLatest(second);
        queue.offerLatest(third);

        assertArrayEquals(second, queue.take());
        assertArrayEquals(third, queue.take());
    }

    @Test
    public void silenceThresholdMatchesReceivers() {
        org.junit.Assert.assertTrue(SharedAudioPolicies.isSilent(new byte[1_920]));
        org.junit.Assert.assertFalse(SharedAudioPolicies.isSilent(voiced((byte) 0)));
    }
}
