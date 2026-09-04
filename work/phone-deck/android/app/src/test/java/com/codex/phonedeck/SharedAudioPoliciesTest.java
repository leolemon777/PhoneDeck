package com.codex.phonedeck;

import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertEquals;

import org.junit.Test;

public final class SharedAudioPoliciesTest {
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
}
