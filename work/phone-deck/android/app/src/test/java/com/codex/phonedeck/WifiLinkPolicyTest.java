package com.codex.phonedeck;

import org.junit.Test;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

public class WifiLinkPolicyTest {
    @Test
    public void onlyWifiWithoutVpnIsRelevant() {
        assertTrue(WifiLinkPolicy.isRelevantTransport(true, false));
        assertFalse(WifiLinkPolicy.isRelevantTransport(false, false));
        assertFalse(WifiLinkPolicy.isRelevantTransport(true, true));
    }

    @Test
    public void sameNetworkSameLinkDoesNotCreateAnotherEvent() {
        assertFalse(WifiLinkPolicy.stateChanged(true, "wlan0|a=[10.0.0.2/24]|r=[]",
                true, "wlan0|a=[10.0.0.2/24]|r=[]"));
        assertTrue(WifiLinkPolicy.stateChanged(true, "wlan0|a=[10.0.0.2/24]|r=[]",
                true, "wlan0|a=[10.0.0.3/24]|r=[]"));
        assertTrue(WifiLinkPolicy.stateChanged(false, "", true, ""));
        assertTrue(WifiLinkPolicy.stateChanged(true, "", false, ""));
    }

    @Test
    public void staleProbeCannotApplyAfterNetworkGenerationChangesOrStop() {
        assertTrue(WifiLinkPolicy.shouldApply(7L, 7L, true));
        assertFalse(WifiLinkPolicy.shouldApply(7L, 8L, true));
        assertFalse(WifiLinkPolicy.shouldApply(7L, 7L, false));
        assertTrue(WifiLinkPolicy.shouldApply(7L, 7L, 3L, 3L, true));
        assertFalse(WifiLinkPolicy.shouldApply(7L, 7L, 3L, 4L, true));
    }

    @Test
    public void activeNetworkTrackerIgnoresLateOldNetworkAndDuplicateCallbacks() {
        WifiLinkPolicy.ActiveNetworkTracker tracker =
                new WifiLinkPolicy.ActiveNetworkTracker();
        assertTrue(tracker.onAvailable("wifi-a") != null);
        assertTrue(tracker.onAvailable("wifi-a") == null);
        assertTrue(tracker.onLinkProperties("wifi-a", "a/24 via r1") != null);
        assertTrue(tracker.onLinkProperties("wifi-a", "a/24 via r1") == null);
        assertTrue(tracker.onAvailable("wifi-b") != null);
        assertTrue(tracker.onLinkProperties("wifi-a", "a/24 via old") == null);
        assertTrue(tracker.onLost("wifi-a") == null);
        assertTrue(tracker.onLinkProperties("wifi-b", "b/24 via r2") != null);
        assertTrue(tracker.onLost("wifi-b") != null);
        assertTrue(tracker.onLost("wifi-b") == null);
    }

    @Test
    public void pendingRefreshMergesDuringDebounceAndDrainsOnce() {
        WifiLinkPolicy.PendingRefresh pending = new WifiLinkPolicy.PendingRefresh();
        pending.request("first", false, 10_000L, 9_500L);
        pending.request("second", true, 10_050L, 9_500L);
        assertTrue(pending.isPending());
        assertTrue(pending.takeIfDue(10_499L) == null);
        WifiLinkPolicy.PendingRefresh.Request request = pending.takeIfDue(10_500L);
        assertTrue(request != null);
        assertTrue("second".equals(request.reason));
        assertTrue(request.probeUsb);
        assertTrue(!pending.isPending());
        assertTrue(pending.takeIfDue(20_000L) == null);
    }

    @Test
    public void pendingRefreshCanBeClosedBeforeWorkerFinishes() {
        WifiLinkPolicy.PendingRefresh pending = new WifiLinkPolicy.PendingRefresh();
        pending.request("network", false, 100L, 0L);
        pending.clear();
        assertTrue(!pending.isPending());
        assertTrue(pending.takeIfDue(2_000L) == null);
    }

    @Test
    public void queuedStopCannotStopReplacementSessionOrDestroyedOwner() {
        java.util.ArrayDeque<Runnable> callbacks = new java.util.ArrayDeque<>();
        java.util.concurrent.atomic.AtomicReference<String> currentSession =
                new java.util.concurrent.atomic.AtomicReference<>("first");
        java.util.concurrent.atomic.AtomicBoolean ownerAlive =
                new java.util.concurrent.atomic.AtomicBoolean(true);
        java.util.concurrent.atomic.AtomicInteger acceptedStops =
                new java.util.concurrent.atomic.AtomicInteger();
        java.util.function.Consumer<String> enqueueStop = session -> callbacks.add(() -> {
            if (WifiLinkPolicy.isCurrentSessionEvent(
                    session, currentSession.get(), ownerAlive.get())) {
                acceptedStops.incrementAndGet();
            }
        });

        enqueueStop.accept("first");
        currentSession.set("second"); // Stop/start happened before callback delivery.
        enqueueStop.accept("second");
        callbacks.remove().run();
        org.junit.Assert.assertEquals(0, acceptedStops.get());
        callbacks.remove().run();
        org.junit.Assert.assertEquals(1, acceptedStops.get());

        enqueueStop.accept("second");
        ownerAlive.set(false);
        callbacks.remove().run();
        org.junit.Assert.assertEquals(1, acceptedStops.get());
    }
}
