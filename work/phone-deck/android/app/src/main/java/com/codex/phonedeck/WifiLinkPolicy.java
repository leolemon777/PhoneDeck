package com.codex.phonedeck;

/**
 * Pure decisions shared by the Wi-Fi lifecycle observer and its consumers.
 *
 * <p>The Android callback objects are deliberately kept out of this class so
 * the important stale-result and callback-coalescing rules remain unit
 * testable on the JVM.</p>
 */
final class WifiLinkPolicy {
    static final long REFRESH_DEBOUNCE_MS = 1_000L;

    private WifiLinkPolicy() {
    }

    static boolean isRelevantTransport(boolean hasWifi, boolean hasVpn) {
        return hasWifi && !hasVpn;
    }

    static boolean stateChanged(boolean wasConnected, String oldSignature,
                                boolean isConnected, String newSignature) {
        if (wasConnected != isConnected) {
            return true;
        }
        if (!isConnected) {
            return false;
        }
        return !java.util.Objects.equals(oldSignature, newSignature);
    }

    static boolean shouldApply(long observedGeneration, long currentGeneration,
                               boolean stillRunning) {
        return stillRunning && observedGeneration == currentGeneration;
    }

    static boolean shouldApply(long observedGeneration, long currentGeneration,
                               long observedSession, long currentSession,
                               boolean stillRunning) {
        return shouldApply(observedGeneration, currentGeneration, stillRunning)
                && observedSession == currentSession;
    }

    static boolean isCurrentSessionEvent(String eventSession, String currentSession,
                                         boolean ownerAlive) {
        return ownerAlive && eventSession != null && eventSession.equals(currentSession);
    }

    /**
     * Small event reducer for one active Wi-Fi identity.  A
     * registerNetworkCallback registration can briefly expose two networks;
     * only an available/promotion event may change identity, while late link
     * properties from a non-active network are ignored.
     */
    static final class ActiveNetworkTracker {
        static final class Transition {
            final long generation;
            final boolean connected;
            final String signature;

            Transition(long generation, boolean connected, String signature) {
                this.generation = generation;
                this.connected = connected;
                this.signature = signature;
            }
        }

        private Object activeNetwork;
        private String signature;
        private boolean connected;
        private long generation;

        Transition onAvailable(Object network) {
            if (network == null) {
                return null;
            }
            if (connected && java.util.Objects.equals(activeNetwork, network)) {
                return null;
            }
            activeNetwork = network;
            signature = "";
            connected = true;
            generation++;
            return new Transition(generation, true, signature);
        }

        Transition onLinkProperties(Object network, String nextSignature) {
            if (!connected || !java.util.Objects.equals(activeNetwork, network)) {
                return null;
            }
            String next = nextSignature == null ? signature : nextSignature;
            if (!stateChanged(true, signature, true, next)) {
                return null;
            }
            signature = next;
            generation++;
            return new Transition(generation, true, signature);
        }

        Transition onLost(Object network) {
            if (!connected || !java.util.Objects.equals(activeNetwork, network)) {
                return null;
            }
            activeNetwork = null;
            signature = null;
            connected = false;
            generation++;
            return new Transition(generation, false, "");
        }

        boolean isActive(Object network) {
            return connected && java.util.Objects.equals(activeNetwork, network);
        }

        boolean hasActive() {
            return connected;
        }

        long generation() {
            return generation;
        }
    }

    /**
     * Coalesces lifecycle refresh requests without making the runnable call
     * the request layer again.  The caller may hold the request while a probe
     * is in flight and drain it exactly once from that probe's finally block.
     */
    static final class PendingRefresh {
        static final class Request {
            final String reason;
            final boolean probeUsb;

            Request(String reason, boolean probeUsb) {
                this.reason = reason;
                this.probeUsb = probeUsb;
            }
        }

        private boolean pending;
        private String reason;
        private boolean probeUsb;
        private long dueAt = Long.MAX_VALUE;

        void request(String nextReason, boolean nextProbeUsb,
                     long now, long lastStartedAt) {
            pending = true;
            reason = nextReason;
            probeUsb |= nextProbeUsb;
            long nextDueAt = Math.max(now, lastStartedAt + REFRESH_DEBOUNCE_MS);
            dueAt = Math.min(dueAt, nextDueAt);
        }

        boolean isPending() {
            return pending;
        }

        long dueAt() {
            return dueAt;
        }

        Request takeIfDue(long now) {
            if (!pending || now < dueAt) {
                return null;
            }
            Request result = new Request(reason, probeUsb);
            clear();
            return result;
        }

        void clear() {
            pending = false;
            reason = null;
            probeUsb = false;
            dueAt = Long.MAX_VALUE;
        }
    }
}
