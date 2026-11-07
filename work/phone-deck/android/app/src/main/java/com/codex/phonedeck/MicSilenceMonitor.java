package com.codex.phonedeck;

import android.media.AudioRecord;
import android.media.AudioRecordingConfiguration;
import android.os.Build;

/**
 * Detects capture that Android silences while another app holds the microphone
 * (Android 10+ concurrent-capture policy): reads keep succeeding but deliver zeros,
 * so the desktop input method receives empty audio without any error.
 *
 * Uses the system flag where available (API 29+) and, on every version, a run of
 * exact digital zeros: a real microphone always carries some noise floor. Not
 * thread-safe; one instance per capture loop.
 */
final class MicSilenceMonitor {
    static final long ZERO_RUN_MS = 3_000;
    static final long SYSTEM_CHECK_INTERVAL_MS = 1_000;

    private long zeroRunStartedAt = -1L;
    private long lastSystemCheckAt = Long.MIN_VALUE / 2;
    private boolean systemSilenced;
    private boolean silenced;

    boolean isSilenced() {
        return silenced;
    }

    /**
     * Feeds one successfully read frame; {@code recorder} may be null in tests.
     * Returns true when the silenced state changed.
     */
    boolean onFrame(byte[] pcm, int count, long now, AudioRecord recorder) {
        if (recorder != null && now - lastSystemCheckAt >= SYSTEM_CHECK_INTERVAL_MS) {
            lastSystemCheckAt = now;
            systemSilenced = Boolean.TRUE.equals(systemSilenced(recorder));
        }
        return onFrame(pcm, count, now, systemSilenced);
    }

    boolean onFrame(byte[] pcm, int count, long now, boolean systemFlag) {
        if (isDigitalZero(pcm, count)) {
            if (zeroRunStartedAt < 0) {
                zeroRunStartedAt = now;
            }
        } else {
            zeroRunStartedAt = -1L;
        }
        boolean next = systemFlag
                || (zeroRunStartedAt >= 0 && now - zeroRunStartedAt >= ZERO_RUN_MS);
        boolean changed = next != silenced;
        silenced = next;
        return changed;
    }

    static boolean isDigitalZero(byte[] pcm, int count) {
        for (int index = 0; index < count; index++) {
            if (pcm[index] != 0) {
                return false;
            }
        }
        return count > 0;
    }

    /** API 29+ system flag for this recorder; null when unknown. */
    static Boolean systemSilenced(AudioRecord recorder) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
            return null;
        }
        try {
            AudioRecordingConfiguration configuration =
                    recorder.getActiveRecordingConfiguration();
            return configuration == null ? null : configuration.isClientSilenced();
        } catch (RuntimeException ignored) {
            return null;
        }
    }

    static String message() {
        return "手机麦克风被其他应用占用，电脑收到的是静音";
    }
}
