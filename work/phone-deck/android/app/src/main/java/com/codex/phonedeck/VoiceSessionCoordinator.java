package com.codex.phonedeck;

import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.util.Log;

import java.util.UUID;

/**
 * 封装语音听写会话的生命周期状态、会话 ID 管理与时序看门狗。
 */
final class VoiceSessionCoordinator {
    private static final String LOG_TAG = "PhoneDeckVoice";
    private static final long WATCHDOG_TIMEOUT_MS = 6_000;

    interface WatchdogListener {
        void onWatchdogTimeout(String sessionId);
    }

    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private String currentSessionId;
    private boolean currentSessionManaged;
    private String currentSessionTargetComputerId;
    private PhoneDeckEndpoint currentSessionEndpoint;
    private String currentSessionMode = "dictation";
    private Runnable watchdogRunnable;

    synchronized String createNewSession(
            boolean managed,
            String targetComputerId,
            PhoneDeckEndpoint endpoint,
            String mode) {
        currentSessionId = UUID.randomUUID().toString();
        currentSessionManaged = managed;
        currentSessionTargetComputerId = targetComputerId;
        currentSessionEndpoint = endpoint;
        currentSessionMode = mode != null ? mode : "dictation";
        Log.i(LOG_TAG, currentSessionId + " tap " + SystemClock.elapsedRealtime());
        return currentSessionId;
    }

    synchronized String getCurrentSessionId() {
        return currentSessionId;
    }

    synchronized boolean isCurrentSessionManaged() {
        return currentSessionManaged;
    }

    synchronized String getCurrentSessionTargetComputerId() {
        return currentSessionTargetComputerId;
    }

    synchronized PhoneDeckEndpoint getCurrentSessionEndpoint() {
        return currentSessionEndpoint;
    }

    synchronized String getCurrentSessionMode() {
        return currentSessionMode;
    }

    synchronized void clearSession() {
        disarmWatchdog();
        currentSessionId = null;
        currentSessionManaged = false;
        currentSessionTargetComputerId = null;
        currentSessionEndpoint = null;
    }

    synchronized void armWatchdog(final String sessionId, final WatchdogListener listener) {
        disarmWatchdog();
        watchdogRunnable = () -> {
            synchronized (VoiceSessionCoordinator.this) {
                if (sessionId != null && sessionId.equals(currentSessionId)) {
                    Log.w(LOG_TAG, sessionId + " watchdogExpired after " + WATCHDOG_TIMEOUT_MS + "ms");
                    if (listener != null) {
                        listener.onWatchdogTimeout(sessionId);
                    }
                }
            }
        };
        mainHandler.postDelayed(watchdogRunnable, WATCHDOG_TIMEOUT_MS);
    }

    synchronized void disarmWatchdog() {
        if (watchdogRunnable != null) {
            mainHandler.removeCallbacks(watchdogRunnable);
            watchdogRunnable = null;
        }
    }
}
