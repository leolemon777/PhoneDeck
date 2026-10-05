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
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;

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
}
