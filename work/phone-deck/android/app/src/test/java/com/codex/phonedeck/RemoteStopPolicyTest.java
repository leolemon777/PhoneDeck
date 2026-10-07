package com.codex.phonedeck;

import org.junit.Test;
import static org.junit.Assert.*;

public final class RemoteStopPolicyTest {
    @Test public void explicitStopWorksBeforeStartAckOrFirstActivePoll() {
        assertTrue(RemoteStopPolicy.shouldStop("new", "new", 0, 900, -1,
                true, null, null, null));
    }

    @Test public void oldReceiptCannotStopNewSessionOrStartAnIdlePhone() {
        assertFalse(RemoteStopPolicy.matchesStop("new", "old"));
        assertFalse(RemoteStopPolicy.matchesStop(null, "old"));
        assertFalse(RemoteStopPolicy.shouldStop("new", "old", 1000, 1100, 0,
                false, true, "new", true));
    }

    @Test public void quickLegacyStopDoesNotRequireAnEarlierCapturingPoll() {
        assertTrue(RemoteStopPolicy.shouldStop("new", null, 1000, 1400, 50,
                false, true, "new", false));
        assertTrue(RemoteStopPolicy.shouldStop("new", null, 1000, 1400, 0,
                false, false, null, false));
    }

    @Test public void receiverCacheRightAfterStartCannotStopNewRecording() {
        // Mac 接收端 150 ms 缓存、不报年龄：开始确认后 121 ms 的快照仍是开始前的“未采集”（2026-10-06 实机）。
        assertFalse(RemoteStopPolicy.shouldStop("new", null, 1000, 1121, 0,
                false, true, "new", false));
        assertFalse(RemoteStopPolicy.shouldStop("new", null, 1000, 1299, 0,
                false, true, "new", false));
        assertTrue(RemoteStopPolicy.shouldStop("new", null, 1000, 1300, 0,
                false, true, "new", false));
    }

    @Test public void PreStartAndCachedSamplesCannotStopNewRecording() {
        assertFalse(RemoteStopPolicy.shouldStop("new", null, 0, 1200, 0,
                false, false, null, false));
        assertFalse(RemoteStopPolicy.shouldStop("new", null, 1000, 999, 0,
                false, false, null, false));
        assertFalse(RemoteStopPolicy.shouldStop("new", null, 1000, 1600, 700,
                false, true, "new", false));
    }

    @Test public void unknownOrMalformedProbeIsNeverAStop() {
        assertNull(RemoteStopPolicy.knownBoolean(null));
        assertNull(RemoteStopPolicy.knownBoolean("false"));
        assertNull(RemoteStopPolicy.knownBoolean(0));
        assertEquals(Boolean.FALSE, RemoteStopPolicy.knownBoolean(false));
        assertFalse(RemoteStopPolicy.shouldStop("new", null, 1000, 1200, 0,
                false, true, "new", null));
        assertFalse(RemoteStopPolicy.shouldStop("new", null, 1000, 1200, 0,
                false, null, "new", false));
        assertFalse(RemoteStopPolicy.shouldStop("new", null, 1000, 1200, 0,
                true, true, "new", false));
        assertFalse(RemoteStopPolicy.shouldStop("new", null, 1000, 1200, -1,
                false, true, "new", false));
    }

    @Test public void otherSessionOrStillCapturingCannotStopThisRecording() {
        assertFalse(RemoteStopPolicy.shouldStop("new", null, 1000, 1200, 0,
                false, true, "other", false));
        assertFalse(RemoteStopPolicy.shouldStop("new", null, 1000, 1200, 0,
                false, false, "other", false));
        assertFalse(RemoteStopPolicy.shouldStop("new", null, 1000, 1200, 0,
                false, false, null, true));
    }
}
