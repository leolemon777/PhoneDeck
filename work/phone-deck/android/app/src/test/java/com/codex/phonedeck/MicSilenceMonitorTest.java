package com.codex.phonedeck;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class MicSilenceMonitorTest {
    private static final byte[] ZEROS = new byte[1_920];
    private static final byte[] NOISE = noise();

    @Test
    public void quietRoomNoiseIsNeverReportedAsSilenced() {
        MicSilenceMonitor monitor = new MicSilenceMonitor();
        for (long now = 0; now <= 10_000; now += 20) {
            assertFalse(monitor.onFrame(NOISE, NOISE.length, now, false));
        }
        assertFalse(monitor.isSilenced());
    }

    @Test
    public void digitalZerosForThreeSecondsAreSilencedUntilAudioReturns() {
        MicSilenceMonitor monitor = new MicSilenceMonitor();
        long now = 0;
        for (; now < MicSilenceMonitor.ZERO_RUN_MS; now += 20) {
            assertFalse(monitor.onFrame(ZEROS, ZEROS.length, now, false));
        }
        assertTrue(monitor.onFrame(ZEROS, ZEROS.length, now, false));
        assertTrue(monitor.isSilenced());

        assertTrue(monitor.onFrame(NOISE, NOISE.length, now + 20, false));
        assertFalse(monitor.isSilenced());
    }

    @Test
    public void shortZeroRunsDoNotTrigger() {
        MicSilenceMonitor monitor = new MicSilenceMonitor();
        for (long now = 0; now < 20_000; now += 20) {
            byte[] frame = (now / 1_000) % 2 == 0 ? ZEROS : NOISE;
            assertFalse(monitor.onFrame(frame, frame.length, now, false));
        }
    }

    @Test
    public void systemFlagIsReportedImmediately() {
        MicSilenceMonitor monitor = new MicSilenceMonitor();
        assertTrue(monitor.onFrame(NOISE, NOISE.length, 0, true));
        assertTrue(monitor.isSilenced());
        assertTrue(monitor.onFrame(NOISE, NOISE.length, 20, false));
    }

    @Test
    public void captureRetryBacksOffThenGivesUp() {
        assertEquals(300L, SharedAudioPolicies.captureRetryDelayMs(1));
        assertEquals(3_000L, SharedAudioPolicies.captureRetryDelayMs(
                SharedAudioPolicies.CAPTURE_RETRY_BACKOFF_MS.length));
        assertEquals(-1L, SharedAudioPolicies.captureRetryDelayMs(
                SharedAudioPolicies.CAPTURE_RETRY_BACKOFF_MS.length + 1));
        assertEquals(-1L, SharedAudioPolicies.captureRetryDelayMs(0));
    }

    private static byte[] noise() {
        byte[] pcm = new byte[1_920];
        for (int index = 0; index < pcm.length; index += 2) {
            pcm[index] = (byte) ((index * 7) % 5 - 2); // ±2 LSB noise floor
        }
        return pcm;
    }
}
