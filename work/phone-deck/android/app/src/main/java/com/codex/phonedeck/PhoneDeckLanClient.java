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

    static final class ProbeOutcome {
        final ProbeResult result;
        /// 至少一个候选地址回了 401/403 或证书校验失败：网络是通的，
        /// 但本地保存的配对令牌/证书指纹已失效，插一次 USB 可自动修复。
        final boolean pairingRejected;

        ProbeOutcome(ProbeResult result, boolean pairingRejected) {
            this.result = result;
            this.pairingRejected = pairingRejected;
        }
    }

    /// 并行探测的连接/读取超时：短超时让死地址快速失败，
    /// 不再像串行探测那样每个地址最多拖 2.2 秒。
    private static final int PROBE_CONNECT_TIMEOUT_MS = 600;
    private static final int PROBE_READ_TIMEOUT_MS = 900;

    /// 共享探测池（守护线程，不阻止进程退出），供无自备线程池的调用方使用。
    private static final ExecutorService SHARED_PROBE_POOL =
            java.util.concurrent.Executors.newFixedThreadPool(4, runnable -> {
                Thread thread = new Thread(runnable, "PhoneDeck-LanProbe");
                thread.setDaemon(true);
                return thread;
            });

    private PhoneDeckLanClient() {
    }

    /// 只关心在线结果的便捷入口（输入指令的 Wi-Fi 优先通道等场景）。
    static ProbeResult probe(TargetDeviceManager.Device device) {
        return probe(device, SHARED_PROBE_POOL).result;
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
    /// 同时收集“地址可达但配对被拒”信号，供上层提示重新配对。
    static ProbeOutcome probe(TargetDeviceManager.Device device, ExecutorService pool) {
        if (device == null || !device.hasLanPairing()) {
            return new ProbeOutcome(null, false);
        }
        List<String> candidates = orderedCandidates(device);
        if (candidates.isEmpty()) {
            return new ProbeOutcome(null, false);
        }
        BlockingQueue<ProbeResult> results = new ArrayBlockingQueue<>(candidates.size());
        java.util.concurrent.atomic.AtomicBoolean rejected =
                new java.util.concurrent.atomic.AtomicBoolean(false);
        for (String address : candidates) {
            pool.execute(() -> {
                ProbeResult result = probeAddress(device, address, rejected);
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
            return new ProbeOutcome(first, rejected.get());
        } catch (InterruptedException exception) {
            Thread.currentThread().interrupt();
            return new ProbeOutcome(null, rejected.get());
        }
    }

    /// 判定一次探测失败是否意味着“需要重新配对”而不是单纯离线：
    /// 服务端明确拒绝令牌（401/403），或证书指纹与配对记录不一致。
    static boolean isPairingRejection(Exception exception) {
        Throwable current = exception;
        while (current != null) {
            if (current instanceof PhoneDeckHttp.ResponseException) {
                int status = ((PhoneDeckHttp.ResponseException) current).status;
                return status == 401 || status == 403;
            }
            if (current instanceof java.security.cert.CertificateException) {
                return true;
            }
            current = current.getCause();
        }
        return false;
    }

    private static ProbeResult probeAddress(
            TargetDeviceManager.Device device,
            String address,
            java.util.concurrent.atomic.AtomicBoolean rejected) {
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
        } catch (Exception exception) {
            if (isPairingRejection(exception)) {
                rejected.set(true);
            }
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
