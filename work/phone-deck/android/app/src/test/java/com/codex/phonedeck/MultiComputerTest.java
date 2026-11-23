package com.codex.phonedeck;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;

/** 一台手机 × 多台电脑：并行探测、单台退避、共享发现与结果复用（无真实网络）。 */
public final class MultiComputerTest {
    private static TargetDeviceManager.Device device(String id, int slot, String... addresses) {
        return new TargetDeviceManager.Device(id, "电脑" + slot, "windows", slot, 0L,
                Arrays.asList(addresses), 8766, "token", "client-" + id, true,
                "ab".repeat(32), null);
    }

    private static PhoneDeckLanClient.ProbeOutcome online(TargetDeviceManager.Device device) {
        PhoneDeckEndpoint endpoint = new PhoneDeckEndpoint("https://" + device.lanAddresses.get(0)
                + ":8766", "token", "ab".repeat(32), "Wi-Fi", device.computerId);
        return new PhoneDeckLanClient.ProbeOutcome(new PhoneDeckLanClient.ProbeResult(
                endpoint, null, device.lanAddresses.get(0)), false);
    }

    /// 可控的假网络：每台电脑的延迟、在线状态与发现结果。
    private static final class FakeNetwork {
        final Map<String, Long> delayMs = new ConcurrentHashMap<>();
        final Map<String, Boolean> onlineAt = new ConcurrentHashMap<>();
        final Map<String, AtomicInteger> probes = new ConcurrentHashMap<>();
        final Map<String, LanDiscoveryClient.DiscoveredComputer> discovered = new HashMap<>();
        final AtomicInteger discoveries = new AtomicInteger();

        PhoneDeckLanClient.ProbeOutcome probe(TargetDeviceManager.Device device) {
            probes.computeIfAbsent(device.computerId, key -> new AtomicInteger()).incrementAndGet();
            long delay = delayMs.getOrDefault(device.computerId, 20L);
            try {
                Thread.sleep(delay);
            } catch (InterruptedException interrupted) {
                Thread.currentThread().interrupt();
            }
            String address = device.lanAddresses.get(device.lanAddresses.size() - 1);
            boolean reachable = Boolean.TRUE.equals(onlineAt.get(device.computerId + "@" + address));
            return reachable ? online(device) : new PhoneDeckLanClient.ProbeOutcome(null, false);
        }

        int probesOf(String id) {
            AtomicInteger count = probes.get(id);
            return count == null ? 0 : count.get();
        }
    }

    @Test
    public void fiveComputersProbeInParallelAndSlowOneIsNotMarkedOffline() {
        FakeNetwork network = new FakeNetwork();
        List<TargetDeviceManager.Device> devices = new ArrayList<>();
        for (int slot = 1; slot <= 5; slot++) {
            devices.add(device("pc" + slot, slot, "192.168.1." + slot));
        }
        network.onlineAt.put("pc1@192.168.1.1", true);
        network.onlineAt.put("pc2@192.168.1.2", true);
        network.onlineAt.put("pc3@192.168.1.3", true);
        // pc4、pc5 离线且每次探测都要等满超时；pc3 很慢但在线。
        network.delayMs.put("pc3", 900L);
        network.delayMs.put("pc4", 1_500L);
        network.delayMs.put("pc5", 1_500L);
        ConnectionMonitor monitor = new ConnectionMonitor(network::probe,
                Collections::emptyMap, (id, address) -> null, System::currentTimeMillis);

        long started = System.currentTimeMillis();
        Map<String, ConnectionMonitor.Outcome> outcomes = monitor.probeAll(devices, false);
        long elapsed = System.currentTimeMillis() - started;

        assertTrue("一轮应约等于最慢一台，而不是逐台累加：" + elapsed + "ms", elapsed < 2_200);
        assertTrue(outcomes.get("pc1").online());
        assertTrue(outcomes.get("pc2").online());
        assertTrue("慢但在线的电脑不能被误判离线", outcomes.get("pc3").online());
        assertFalse(outcomes.get("pc4").online());
        assertFalse(outcomes.get("pc5").online());
    }

    @Test
    public void offlineComputerBacksOffAloneWhileOnlineOnesKeepTheirPace() {
        FakeNetwork network = new FakeNetwork();
        TargetDeviceManager.Device up = device("up", 1, "10.0.0.1");
        TargetDeviceManager.Device down = device("down", 2, "10.0.0.2");
        network.onlineAt.put("up@10.0.0.1", true);
        AtomicLong now = new AtomicLong(100_000);
        ConnectionMonitor monitor = new ConnectionMonitor(network::probe,
                Collections::emptyMap, (id, address) -> null, now::get);
        List<TargetDeviceManager.Device> devices = Arrays.asList(up, down);

        monitor.probeAll(devices, false);                 // down 失败 1 次：2 s 后再试
        now.addAndGet(2_000);
        monitor.probeAll(devices, false);                 // down 失败 2 次：4 s 后再试
        now.addAndGet(2_000);
        Map<String, ConnectionMonitor.Outcome> skipped = monitor.probeAll(devices, false);

        assertTrue(skipped.get("down").skipped);
        assertEquals(2, network.probesOf("down"));
        assertEquals("在线电脑每轮都实际探测", 3, network.probesOf("up"));

        monitor.reset();                                  // 网络变化：立即重新探测
        monitor.probeAll(devices, false);
        assertEquals(3, network.probesOf("down"));
    }

    @Test
    public void oneDiscoveryRefreshesEveryMissingComputer() {
        FakeNetwork network = new FakeNetwork();
        TargetDeviceManager.Device first = device("a", 1, "10.0.0.10");
        TargetDeviceManager.Device second = device("b", 2, "10.0.0.20");
        network.onlineAt.put("a@10.0.0.11", true);
        network.onlineAt.put("b@10.0.0.21", true);
        network.discovered.put("a", new LanDiscoveryClient.DiscoveredComputer("a", "A", "10.0.0.11", 8766));
        network.discovered.put("b", new LanDiscoveryClient.DiscoveredComputer("b", "B", "10.0.0.21", 8766));
        Map<String, TargetDeviceManager.Device> merged = new HashMap<>();
        ConnectionMonitor monitor = new ConnectionMonitor(network::probe, () -> {
            network.discoveries.incrementAndGet();
            return network.discovered;
        }, (id, address) -> {
            TargetDeviceManager.Device base = id.equals("a") ? first : second;
            List<String> addresses = new ArrayList<>(base.lanAddresses);
            addresses.add(address);
            TargetDeviceManager.Device updated = base.withAddresses(addresses, null);
            merged.put(id, updated);
            return updated;
        }, System::currentTimeMillis);

        Map<String, ConnectionMonitor.Outcome> outcomes =
                monitor.probeAll(Arrays.asList(first, second), false);

        assertEquals("一次广播覆盖所有离线电脑", 1, network.discoveries.get());
        assertTrue(outcomes.get("a").online());
        assertTrue(outcomes.get("b").online());
        assertEquals(2, merged.size());
    }

    @Test
    public void recentSuccessIsSharedBetweenActivityAndServiceRounds() {
        FakeNetwork network = new FakeNetwork();
        TargetDeviceManager.Device pc = device("pc", 1, "10.0.0.5");
        network.onlineAt.put("pc@10.0.0.5", true);
        AtomicLong now = new AtomicLong(50_000);
        ConnectionMonitor monitor = new ConnectionMonitor(network::probe,
                Collections::emptyMap, (id, address) -> null, now::get);
        List<TargetDeviceManager.Device> one = Collections.singletonList(pc);

        ConnectionMonitor.Outcome first = monitor.probeAll(one, false).get("pc");
        now.addAndGet(500);
        ConnectionMonitor.Outcome reused = monitor.probeAll(one, false).get("pc");
        now.addAndGet(ConnectionMonitor.CACHE_MS);
        monitor.probeAll(one, false);

        assertEquals(2, network.probesOf("pc"));
        assertEquals("复用结果保留原采样时刻，不能当作新样本", first.startedAt, reused.startedAt);
        assertNotNull(monitor.probeNow(pc).result);
        assertEquals("切换确认总是实际探测", 3, network.probesOf("pc"));
    }

    @Test
    public void unpairedComputersAreLeftOut() {
        FakeNetwork network = new FakeNetwork();
        TargetDeviceManager.Device usbOnly = new TargetDeviceManager.Device("usb", "USB", "windows",
                1, 0L, Collections.emptyList(), 0, null, null, null);
        ConnectionMonitor monitor = new ConnectionMonitor(network::probe,
                Collections::emptyMap, (id, address) -> null, System::currentTimeMillis);

        assertNull(monitor.probeAll(Collections.singletonList(usbOnly), false).get("usb"));
    }

    @Test
    public void addressHistoryIsCappedButKeepsLastGood() {
        List<String> history = Arrays.asList("a", "b", "c", "d", "e", "f", "g", "h");

        assertEquals(Arrays.asList("c", "d", "e", "f", "g", "h"),
                TargetDeviceManager.capAddresses(history, null, 6));
        assertEquals(Arrays.asList("a", "d", "e", "f", "g", "h"),
                TargetDeviceManager.capAddresses(history, "a", 6));
    }

    @Test
    public void sharedSummaryCountsSupplyAndNamesEachMissingComputer() {
        Map<String, String> states = new HashMap<>();
        states.put("pc1", MultiComputerStatus.SUPPLYING);
        states.put("pc2", MultiComputerStatus.SUPPLYING);
        states.put("pc5", "离线");
        states.put("pc3", "连接中");
        Map<String, Integer> slots = new HashMap<>();
        slots.put("pc1", 1);
        slots.put("pc2", 2);
        slots.put("pc3", 3);
        slots.put("pc5", 5);

        assertEquals("正在向 2/4 台电脑供音 · 3号连接中 · 5号离线",
                MultiComputerStatus.summarize(states, slots));
        assertEquals("共享组还没有电脑，请在电脑列表中勾选",
                MultiComputerStatus.summarize(Collections.emptyMap(), slots));
    }

    @Test
    public void timedOutProbeIsReleasedSoTheNextForcedRoundCanStartFresh() throws Exception {
        TargetDeviceManager.Device pc = device("pc", 1, "10.0.0.5");
        CountDownLatch firstStarted = new CountDownLatch(1);
        AtomicInteger probes = new AtomicInteger();

        ConnectionMonitor monitor = new ConnectionMonitor(device -> {
            int call = probes.incrementAndGet();
            if (call == 1) {
                firstStarted.countDown();
                try {
                    Thread.sleep(10_000);
                } catch (InterruptedException interrupted) {
                    Thread.currentThread().interrupt();
                }
            }
            return online(device);
        }, Collections::emptyMap, (id, address) -> null, System::currentTimeMillis);

        ExecutorService caller = Executors.newSingleThreadExecutor(runnable -> {
            Thread thread = new Thread(runnable, "timeout-round-caller");
            thread.setDaemon(true);
            return thread;
        });
        try {
            Future<Map<String, ConnectionMonitor.Outcome>> firstRound =
                    caller.submit(() -> monitor.probeAll(Collections.singletonList(pc), false));
            assertTrue(firstStarted.await(1, TimeUnit.SECONDS));
            Map<String, ConnectionMonitor.Outcome> timedOut =
                    firstRound.get(5, TimeUnit.SECONDS);
            assertFalse(timedOut.get("pc").online());

            // The first round timed out and was cancelled/removed.  Force
            // bypasses the independent offline backoff for this assertion.
            Map<String, ConnectionMonitor.Outcome> secondRound =
                    monitor.probeAll(Collections.singletonList(pc), true);
            assertTrue(secondRound.get("pc").online());
            assertTrue("第二轮必须启动新的探测", probes.get() >= 2);
        } finally {
            caller.shutdownNow();
        }
    }

    @Test
    public void oneTimedOutCallerDoesNotCancelAnotherSharedProbeWaiter() throws Exception {
        TargetDeviceManager.Device pc = device("pc", 1, "10.0.0.5");
        CountDownLatch firstStarted = new CountDownLatch(1);
        AtomicInteger probes = new AtomicInteger();
        ConnectionMonitor monitor = new ConnectionMonitor(device -> {
            if (probes.incrementAndGet() == 1) {
                firstStarted.countDown();
                long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(2_600);
                while (System.nanoTime() < deadline) {
                    try {
                        Thread.sleep(20);
                    } catch (InterruptedException ignored) {
                        // If the first caller incorrectly cancels the shared
                        // Future, this worker deliberately keeps running; the
                        // second caller will then observe the cancellation.
                    }
                }
            }
            return online(device);
        }, Collections::emptyMap, (id, address) -> null, System::currentTimeMillis);

        ExecutorService callers = Executors.newFixedThreadPool(2);
        try {
            Future<Map<String, ConnectionMonitor.Outcome>> first = callers.submit(
                    () -> monitor.probeAll(Collections.singletonList(pc), false));
            assertTrue(firstStarted.await(1, TimeUnit.SECONDS));
            Thread.sleep(300);
            Future<Map<String, ConnectionMonitor.Outcome>> second = callers.submit(
                    () -> monitor.probeAll(Collections.singletonList(pc), false));

            Map<String, ConnectionMonitor.Outcome> firstResult = first.get(5, TimeUnit.SECONDS);
            Map<String, ConnectionMonitor.Outcome> secondResult = second.get(5, TimeUnit.SECONDS);
            assertTrue("过时调用不得把离线结果交给消费者",
                    ConnectionMonitor.isInvalidated(firstResult));
            assertTrue("仍在等待的共享调用必须收到原探测结果", secondResult.get("pc").online());
            assertEquals("两个调用应共用一个探测", 1, probes.get());
        } finally {
            callers.shutdownNow();
        }
    }

    @Test
    public void resetInvalidatesOldRoundAndLateResultCannotPopulateCache() throws Exception {
        TargetDeviceManager.Device pc = device("pc", 1, "10.0.0.5");
        AtomicInteger probes = new AtomicInteger();
        CountDownLatch firstStarted = new CountDownLatch(1);
        CountDownLatch releaseFirst = new CountDownLatch(1);
        ConnectionMonitor monitor = new ConnectionMonitor(device -> {
            if (probes.incrementAndGet() == 1) {
                firstStarted.countDown();
                try {
                    releaseFirst.await();
                } catch (InterruptedException ignored) {
                    // Deliberately keep the old worker alive to prove that a
                    // late completion cannot update post-reset state.
                    try {
                        releaseFirst.await();
                    } catch (InterruptedException ignoredAgain) {
                        Thread.currentThread().interrupt();
                    }
                }
            }
            return online(device);
        }, Collections::emptyMap, (id, address) -> null, System::currentTimeMillis);

        ExecutorService caller = Executors.newSingleThreadExecutor(runnable -> {
            Thread thread = new Thread(runnable, "reset-round-caller");
            thread.setDaemon(true);
            return thread;
        });
        try {
            Future<Map<String, ConnectionMonitor.Outcome>> oldRound =
                    caller.submit(() -> monitor.probeAll(Collections.singletonList(pc), true));
            assertTrue(firstStarted.await(1, TimeUnit.SECONDS));

            monitor.reset();
            Map<String, ConnectionMonitor.Outcome> discarded = oldRound.get(1, TimeUnit.SECONDS);
            assertTrue("reset 后旧轮次结果不得回传给调用方", discarded.isEmpty());
            assertTrue("reset 后旧轮次必须标记为无效批次",
                    ConnectionMonitor.isInvalidated(discarded));

            Map<String, ConnectionMonitor.Outcome> fresh =
                    monitor.probeAll(Collections.singletonList(pc), true);
            assertTrue("reset 后应立即开启新探测", fresh.get("pc").online());
            assertTrue("reset 后必须有新的探测调用", probes.get() >= 2);
        } finally {
            releaseFirst.countDown();
            caller.shutdownNow();
        }
    }

    @Test
    public void slowDiscoveryCannotOverwriteNewerForcedOnlineProbe() throws Exception {
        TargetDeviceManager.Device pc = device("pc", 1, "10.0.0.5");
        CountDownLatch discoveryStarted = new CountDownLatch(1);
        CountDownLatch releaseDiscovery = new CountDownLatch(1);
        AtomicInteger probes = new AtomicInteger();
        ConnectionMonitor monitor = new ConnectionMonitor(device -> {
            if (probes.incrementAndGet() == 1) {
                return new PhoneDeckLanClient.ProbeOutcome(null, false);
            }
            return online(device);
        }, () -> {
            discoveryStarted.countDown();
            try {
                releaseDiscovery.await(5, TimeUnit.SECONDS);
            } catch (InterruptedException interrupted) {
                Thread.currentThread().interrupt();
            }
            return Collections.emptyMap();
        }, (id, address) -> null, System::currentTimeMillis);

        ExecutorService caller = Executors.newSingleThreadExecutor(runnable -> {
            Thread thread = new Thread(runnable, "slow-discovery-round-caller");
            thread.setDaemon(true);
            return thread;
        });
        try {
            Future<Map<String, ConnectionMonitor.Outcome>> staleRound = caller.submit(
                    () -> monitor.probeAll(Collections.singletonList(pc), false));
            assertTrue("第一轮应进入慢发现", discoveryStarted.await(1, TimeUnit.SECONDS));

            Map<String, ConnectionMonitor.Outcome> newerRound =
                    monitor.probeAll(Collections.singletonList(pc), true);
            assertTrue("强制新轮次应成功上线", newerRound.get("pc").online());

            releaseDiscovery.countDown();
            Map<String, ConnectionMonitor.Outcome> staleResult =
                    staleRound.get(5, TimeUnit.SECONDS);
            assertTrue("旧轮次不得把自己的离线结果交给消费者",
                    ConnectionMonitor.isInvalidated(staleResult));

            Map<String, ConnectionMonitor.Outcome> cached =
                    monitor.probeAll(Collections.singletonList(pc), false);
            assertTrue("慢发现返回后缓存仍应保留新轮次在线结果",
                    cached.get("pc").online());
            assertEquals("旧发现不得再次触发探测", 2, probes.get());
        } finally {
            releaseDiscovery.countDown();
            caller.shutdownNow();
        }
    }

    @Test
    public void twoCallersSharingFutureBothReceiveTheSameSuccess() throws Exception {
        TargetDeviceManager.Device pc = device("pc", 1, "10.0.0.5");
        CountDownLatch firstStarted = new CountDownLatch(1);
        AtomicInteger probes = new AtomicInteger();
        ConnectionMonitor monitor = new ConnectionMonitor(device -> {
            if (probes.incrementAndGet() == 1) {
                firstStarted.countDown();
                long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(800);
                while (System.nanoTime() < deadline) {
                    try {
                        Thread.sleep(20);
                    } catch (InterruptedException ignored) {
                        Thread.currentThread().interrupt();
                        break;
                    }
                }
            }
            return online(device);
        }, Collections::emptyMap, (id, address) -> null, System::currentTimeMillis);

        ExecutorService callers = Executors.newFixedThreadPool(2);
        try {
            Future<Map<String, ConnectionMonitor.Outcome>> first = callers.submit(
                    () -> monitor.probeAll(Collections.singletonList(pc), false));
            assertTrue(firstStarted.await(1, TimeUnit.SECONDS));
            Thread.sleep(100);
            Future<Map<String, ConnectionMonitor.Outcome>> second = callers.submit(
                    () -> monitor.probeAll(Collections.singletonList(pc), false));

            Map<String, ConnectionMonitor.Outcome> firstResult =
                    first.get(5, TimeUnit.SECONDS);
            Map<String, ConnectionMonitor.Outcome> secondResult =
                    second.get(5, TimeUnit.SECONDS);
            assertFalse("共享 Future 的首个成功调用不应被后调用作废",
                    ConnectionMonitor.isInvalidated(firstResult));
            assertTrue("共享 Future 的首个调用应收到在线结果",
                    firstResult.get("pc").online());
            assertFalse("共享 Future 的后一个成功调用不应被误判过时",
                    ConnectionMonitor.isInvalidated(secondResult));
            assertTrue("共享 Future 的后一个调用应收到在线结果",
                    secondResult.get("pc").online());
            assertEquals("两个调用应共享一个底层探测", 1, probes.get());
        } finally {
            callers.shutdownNow();
        }
    }

    @Test
    public void interruptedCallerReleasesUnownedProbe() throws Exception {
        TargetDeviceManager.Device pc = device("pc", 1, "10.0.0.5");
        CountDownLatch firstStarted = new CountDownLatch(1);
        AtomicInteger probes = new AtomicInteger();
        AtomicReference<Map<String, ConnectionMonitor.Outcome>> interruptedResult =
                new AtomicReference<>();
        ConnectionMonitor monitor = new ConnectionMonitor(device -> {
            if (probes.incrementAndGet() == 1) {
                firstStarted.countDown();
                try {
                    Thread.sleep(10_000);
                } catch (InterruptedException interrupted) {
                    Thread.currentThread().interrupt();
                }
            }
            return online(device);
        }, Collections::emptyMap, (id, address) -> null, System::currentTimeMillis);

        Thread caller = new Thread(() -> interruptedResult.set(
                monitor.probeAll(Collections.singletonList(pc), false)),
                "interrupted-round-caller");
        caller.setDaemon(true);
        caller.start();
        assertTrue(firstStarted.await(1, TimeUnit.SECONDS));
        caller.interrupt();
        caller.join(2_000);

        assertNotNull("被中断的调用应结束并返回本轮结论", interruptedResult.get());
        assertFalse(interruptedResult.get().get("pc").online());
        Map<String, ConnectionMonitor.Outcome> fresh =
                monitor.probeAll(Collections.singletonList(pc), true);
        assertTrue("无 waiter 的旧 Future 释放后应允许新探测", fresh.get("pc").online());
        assertTrue("中断后的强制轮次必须启动新探测", probes.get() >= 2);
    }

    @Test
    public void duplicateComputerIdsDoNotLeaveAStaleSharedWaiter() {
        TargetDeviceManager.Device pc = device("pc", 1, "10.0.0.5");
        AtomicInteger probes = new AtomicInteger();
        ConnectionMonitor monitor = new ConnectionMonitor(device -> {
            if (probes.incrementAndGet() == 1) {
                long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(2_800);
                while (System.nanoTime() < deadline) {
                    try {
                        Thread.sleep(20);
                    } catch (InterruptedException ignored) {
                        // Keep the first worker alive so a stale waiter would
                        // be observable as an incorrectly shared second round.
                    }
                }
            }
            return online(device);
        }, Collections::emptyMap, (id, address) -> null, System::currentTimeMillis);

        Map<String, ConnectionMonitor.Outcome> first = monitor.probeAll(
                Arrays.asList(pc, pc), false);
        assertEquals("重复 computerId 只应产生一个结果条目", 1, first.size());
        assertFalse(first.get("pc").online());
        Map<String, ConnectionMonitor.Outcome> fresh = monitor.probeAll(
                Collections.singletonList(pc), true);
        assertTrue("重复输入不得把下轮错误地挂到旧 Future", fresh.get("pc").online());
        assertTrue("下轮应启动新的底层探测", probes.get() >= 2);
    }

}
