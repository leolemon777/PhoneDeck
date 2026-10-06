package com.codex.phonedeck;

/** Pure decision rules; callers must first verify the computer and session transport. */
final class RemoteStopPolicy {
    private RemoteStopPolicy() { }

    static Boolean knownBoolean(Object value) {
        return value instanceof Boolean ? (Boolean) value : null;
    }

    static boolean matchesStop(String currentSession, String requestedSession) {
        return currentSession != null && !currentSession.isBlank()
                && currentSession.equals(requestedSession);
    }

    static boolean shouldStop(String currentSession, String requestedSession,
                              long startConfirmedAt, long probeStartedAt, long sampleAgeMs,
                              boolean stale, Boolean active, String remoteSession,
                              Boolean capturing) {
        // An explicit stop is scoped to this unique session, including during startup.
        if (matchesStop(currentSession, requestedSession)) return true;
        if (currentSession == null || startConfirmedAt <= 0 || stale
                || active == null || capturing == null || sampleAgeMs < 0
                || probeStartedAt < startConfirmedAt
                || probeStartedAt - startConfirmedAt < sampleAgeMs) return false;

        // Old receivers have no stop receipt. Only trust a fresh sample taken after
        // start was acknowledged; null/unknown and another session cannot stop us.
        if (active) return currentSession.equals(remoteSession) && !capturing;
        return (remoteSession == null || remoteSession.isBlank()) && !capturing;
    }
}
