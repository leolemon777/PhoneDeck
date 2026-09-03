package com.codex.phonedeck;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.TimeUnit;

final class PhoneDeckLanClient {
    static final class ProbeResult {
        final PhoneDeckEndpoint endpoint;
        final JSONObject health;
        final String hostAddress;

        ProbeResult(PhoneDeckEndpoint endpoint, JSONObject health, String hostAddress) {
            this.endpoint = endpoint;
            this.health = health;
            this.hostAddress = hostAddress;
        }
    }

    /// 并行探测的连接/读取超时：短超时让死地址快速失败，
    /// 不再像串行探测那样每个地址最多拖 2.2 秒。
    private static final int PROBE_CONNECT_TIMEOUT_MS = 600;
    private static final int PROBE_READ_TIMEOUT_MS = 900;

    /// 无调用方线程池时的共享探测池（守护线程，不阻止进程退出）。
    private static final ExecutorService SHARED_PROBE_POOL =
            java.util.concurrent.Executors.newFixedThreadPool(4, runnable -> {
                Thread thread = new Thread(runnable, "PhoneDeck-LanProbe");
                thread.setDaemon(true);
                return thread;
            });

    private PhoneDeckLanClient() {
    }

    static ProbeResult probe(TargetDeviceManager.Device device) {
        return probe(device, SHARED_PROBE_POOL);
    }

    static TargetDeviceManager.Device pairOverUsb(TargetDeviceManager manager)
            throws Exception {
        JSONObject pairing = PhoneDeckHttp.postJson(
                PhoneDeckEndpoint.USB, "/api/lan/pair", new JSONObject(), 1800);
        JSONArray addressArray = pairing.optJSONArray("addresses");
        ArrayList<String> addresses = new ArrayList<>();
        if (addressArray != null) {
            for (int index = 0; index < addressArray.length(); index++) {
                String address = addressArray.optString(index, "").trim();
                if (!address.isEmpty()) {
                    addresses.add(address);
                }
            }
        }
        TargetDeviceManager.Device paired = manager.saveLanPairing(
                pairing.optString("computerId", null),
                pairing.optString("displayName", null),
                pairing.optString("platform", "windows"),
                addresses,
                pairing.optInt("port", 0),
                pairing.optString("accessToken", null),
                pairing.optString("certificateSha256", null));
        if (paired == null) {
            throw new IllegalStateException("电脑返回的 Wi-Fi 配对资料不完整");
        }
        return paired;
    }

    /// 并行探测全部候选地址，任一地址返回匹配的 computerId 即在线。
    /// 顺序仅用于平局时优先选择（last-good → 与手机同子网 → 其余）。
    static ProbeResult probe(TargetDeviceManager.Device device, ExecutorService pool) {
        if (device == null || !device.hasLanPairing()) {
            return null;
        }
        List<String> candidates = orderedCandidates(device);
        if (candidates.isEmpty()) {
            return null;
        }
        BlockingQueue<ProbeResult> results = new ArrayBlockingQueue<>(candidates.size());
        for (String address : candidates) {
            pool.execute(() -> {
                ProbeResult result = probeAddress(device, address);
                if (result != null) {
                    results.offer(result);
                }
            });
        }
        long deadline = android.os.SystemClock.elapsedRealtime()
                + PROBE_CONNECT_TIMEOUT_MS + PROBE_READ_TIMEOUT_MS + 400;
        try {
            ProbeResult first = results.poll(
                    Math.max(1, deadline - android.os.SystemClock.elapsedRealtime()),
                    TimeUnit.MILLISECONDS);
            return first;
        } catch (InterruptedException exception) {
            Thread.currentThread().interrupt();
            return null;
        }
    }

    private static ProbeResult probeAddress(
            TargetDeviceManager.Device device, String address) {
        try {
            PhoneDeckEndpoint endpoint = new PhoneDeckEndpoint(
                    "https://" + address + ":" + device.lanPort,
                    device.lanToken,
                    device.certificateSha256,
                    "Wi-Fi",
                    device.computerId);
            JSONObject health = PhoneDeckHttp.getJson(
                    endpoint, "/api/health",
                    PROBE_CONNECT_TIMEOUT_MS, PROBE_READ_TIMEOUT_MS);
            if (device.computerId.equalsIgnoreCase(
                    health.optString("computerId", ""))) {
                return new ProbeResult(endpoint, health, address);
            }
            return null;
        } catch (Exception ignored) {
            // 并行探测下其余地址可能仍可达。
            return null;
        }
    }

    /// last-good 优先，其次与手机当前子网相同的地址，最后是其余历史地址。
    static List<String> orderedCandidates(TargetDeviceManager.Device device) {
        String subnet = LanDiscoveryClient.currentSubnetPrefix();
        List<String> ordered = new ArrayList<>(device.lanAddresses.size());
        if (device.lastGoodAddress != null
                && device.lanAddresses.contains(device.lastGoodAddress)) {
            ordered.add(device.lastGoodAddress);
        }
        if (subnet != null) {
            for (String address : device.lanAddresses) {
                if (address.startsWith(subnet) && !ordered.contains(address)) {
                    ordered.add(address);
                }
            }
        }
        for (String address : device.lanAddresses) {
            if (!ordered.contains(address)) {
                ordered.add(address);
            }
        }
        return ordered;
    }
}
