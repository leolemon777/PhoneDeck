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
        assertTrue(RemoteStopPolicy.shouldStop("new", null, 1000, 1200, 50,
                false, true, "new", false));
        assertTrue(RemoteStopPolicy.shouldStop("new", null, 1000, 1200, 0,
                false, false, null, false));
    }

    @Test public void PreStartAndCachedSamplesCannotStopNewRecording() {
        assertFalse(RemoteStopPolicy.shouldStop("new", null, 0, 1200, 0,
                false, false, null, false));
        assertFalse(RemoteStopPolicy.shouldStop("new", null, 1000, 999, 0,
                false, false, null, false));
        assertFalse(RemoteStopPolicy.shouldStop("new", null, 1000, 1200, 300,
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
