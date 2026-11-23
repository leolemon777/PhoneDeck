package com.codex.phonedeck;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ExecutorCompletionService;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Future;
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
        /// 候选地址明确返回 401/403；证书或电脑身份不匹配可能只是旧 IP
        /// 被重新分配，不把它们推断成用户配对被撤销。
        final boolean pairingRejected;
        /// Local executor contention is unknown, not a receiver failure.
        final boolean skipped;

        ProbeOutcome(ProbeResult result, boolean pairingRejected) {
            this(result, pairingRejected, false);
        }

        ProbeOutcome(ProbeResult result, boolean pairingRejected, boolean skipped) {
            this.result = result;
            this.pairingRejected = pairingRejected;
            this.skipped = skipped;
        }
    }

    /// 并行探测的连接/读取超时：短超时让死地址快速失败，
    /// 同时兼容随身 Wi-Fi / 热点在节电唤醒时的抖动。
    private static final int PROBE_CONNECT_TIMEOUT_MS = 800;
    private static final int PROBE_READ_TIMEOUT_MS = 1000;

    /// 共享地址探测池（守护线程）：直接移交、不排队。固定小线程池在多台电脑同时探测时
    /// 会让后一台的地址任务排在前一台未结束的任务之后，等不到截止时间被误判离线。
    private static final ExecutorService SHARED_PROBE_POOL =
            new java.util.concurrent.ThreadPoolExecutor(0, 64, 30,
                    java.util.concurrent.TimeUnit.SECONDS,
                    new java.util.concurrent.SynchronousQueue<>(), runnable -> {
                Thread thread = new Thread(runnable, "PhoneDeck-LanProbe");
                thread.setDaemon(true);
                return thread;
            }, new java.util.concurrent.ThreadPoolExecutor.AbortPolicy());

    private PhoneDeckLanClient() {
    }

    /// 只关心在线结果的便捷入口（输入指令的 Wi-Fi 优先通道等场景）。
    static ProbeResult probe(TargetDeviceManager.Device device) {
        return probe(device, SHARED_PROBE_POOL).result;
    }

    /// 含“配对被拒”信号的探测，使用共享地址探测池（ConnectionMonitor 默认探测器）。
    static ProbeOutcome probeOutcome(TargetDeviceManager.Device device) {
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
            if (!manager.canAdd(pairing.optString("computerId", ""))) {
                throw new IllegalStateException("已配对 " + TargetDeviceManager.MAX_DEVICES
                        + " 台电脑，请先删除一台再配对");
            }
            throw new IllegalStateException("电脑返回的 Wi-Fi 配对资料不完整");
        }
        return paired;
    }

    /// 探测候选地址，任一地址返回匹配的 computerId 即在线。有上次可用地址时先只探测它，
    /// 失败或 0.6 秒无应答再并行探测其余地址；否则全部并行（last-good → 与手机同子网 → 其余）。
    /// 同时收集“地址可达但配对被拒”信号，供上层提示重新配对。
    static ProbeOutcome probe(TargetDeviceManager.Device device, ExecutorService pool) {
        if (device == null || !device.hasLanPairing()) {
            return new ProbeOutcome(null, false);
        }
        List<String> candidates = orderedCandidates(device);
        if (candidates.isEmpty()) {
            return new ProbeOutcome(null, false);
        }
        ExecutorCompletionService<ProbeResult> completed = new ExecutorCompletionService<>(pool);
        List<Future<ProbeResult>> attempts = new ArrayList<>(candidates.size());
        java.util.concurrent.atomic.AtomicBoolean rejected =
                new java.util.concurrent.atomic.AtomicBoolean(false);
        long deadline = android.os.SystemClock.elapsedRealtime()
                + PROBE_CONNECT_TIMEOUT_MS + PROBE_READ_TIMEOUT_MS + 400;
        boolean admissionSkipped = false;
        boolean lastGoodFirst = shouldProbeLastGoodFirst(device.lastGoodAddress, candidates);
        long fanOutAt = android.os.SystemClock.elapsedRealtime() + LAST_GOOD_HEAD_START_MS;
        try {
            List<String> firstWave = lastGoodFirst ? candidates.subList(0, 1) : candidates;
            for (String address : firstWave) {
                if (Thread.currentThread().isInterrupted()) {
                    return new ProbeOutcome(null, rejected.get());
                }
                admissionSkipped |= !submitProbe(completed, attempts, device, address, rejected);
            }
            boolean fannedOut = !lastGoodFirst;
            int outstanding = attempts.size();
            while (true) {
                long now = android.os.SystemClock.elapsedRealtime();
                if (!fannedOut && (outstanding == 0 || now >= fanOutAt)) {
                    // 上次可用地址失败或 0.6 秒没应答，才去试其余旧地址。
                    fannedOut = true;
                    int before = attempts.size();
                    for (String address : candidates.subList(1, candidates.size())) {
                        admissionSkipped |= !submitProbe(completed, attempts, device, address, rejected);
                    }
                    outstanding += attempts.size() - before;
                }
                if (outstanding == 0) {
                    break;
                }
                long waitUntil = fannedOut ? deadline : Math.min(deadline, fanOutAt);
                long remainingMs = waitUntil - now;
                if (remainingMs <= 0) {
                    if (fannedOut) {
                        break;
                    }
                    continue;
                }
                Future<ProbeResult> finished = completed.poll(remainingMs, TimeUnit.MILLISECONDS);
                if (finished == null) {
                    if (fannedOut) {
                        break;
                    }
                    continue;
                }
                outstanding--;
                try {
                    ProbeResult result = finished.get();
                    if (result != null) {
                        return new ProbeOutcome(result, rejected.get());
                    }
                } catch (java.util.concurrent.ExecutionException
                         | java.util.concurrent.CancellationException ignored) {
                    // Failure of one address cannot hide another valid path.
                }
            }
            // Refused/auth-failed addresses no longer consume the entire
            // timeout when all candidates have already completed.
            // If a valid address never got an execution slot, a stale address
            // rejecting its token cannot prove the whole pairing is invalid.
            return new ProbeOutcome(null, !admissionSkipped && rejected.get(), admissionSkipped);
        } catch (InterruptedException exception) {
            Thread.currentThread().interrupt();
            return new ProbeOutcome(null, rejected.get());
        } finally {
            for (Future<ProbeResult> attempt : attempts) {
                if (!attempt.isDone()) {
                    attempt.cancel(true);
                }
            }
        }
    }

    /// 先只探测上次可用地址的时长；正常情况下它几十毫秒内应答（复用 keep-alive），
    /// 其余历史地址就不必每 2 秒各发一次连接——同网段的失效地址每次都会让手机发 ARP 广播。
    static final long LAST_GOOD_HEAD_START_MS = 600;

    /// 纯函数供单测：有多个候选且第一个就是上次可用地址时，先单独探测它。
    static boolean shouldProbeLastGoodFirst(String lastGood, List<String> orderedCandidates) {
        return lastGood != null && orderedCandidates != null && orderedCandidates.size() > 1
                && lastGood.equals(orderedCandidates.get(0));
    }

    /// 提交一个地址探测；线程池满时返回 false（不能把套接字 I/O 挪到调用线程上）。
    private static boolean submitProbe(ExecutorCompletionService<ProbeResult> completed,
                                       List<Future<ProbeResult>> attempts,
                                       TargetDeviceManager.Device device,
                                       String address,
                                       java.util.concurrent.atomic.AtomicBoolean rejected) {
        try {
            attempts.add(completed.submit(() -> probeAddress(device, address, rejected)));
            return true;
        } catch (java.util.concurrent.RejectedExecutionException busy) {
            return false;
        }
    }

    /// 判定一次探测失败是否意味着“需要重新配对”而不是单纯离线：只有服务端明确拒绝
    /// 令牌（401/403）才算。证书不一致说明旧 IP 上换成了另一台电脑（IP 会被重新分配，
    /// 不能当身份），按“这台电脑不在这个地址”处理，不提示重新配对。
    static boolean isPairingRejection(Exception exception) {
        Throwable current = exception;
        while (current != null) {
            if (current instanceof PhoneDeckHttp.ResponseException) {
                int status = ((PhoneDeckHttp.ResponseException) current).status;
                return status == 401 || status == 403;
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
                    device.computerId,
                    // M1-A：有 clientId 的设备（Wi-Fi 配对/rotate 升级）必须走
                    // Bearer + X-PhoneDeck-Client；旧共享令牌头只服务 legacy 设备。
                    device.clientId);
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
                // 只记地址与状态码，不含令牌：用于排查“配对已失效”误报来自哪个地址。
                android.util.Log.w("PhoneDeckNet", "Wi-Fi 探测被拒：" + address + " · "
                        + (device.clientId != null ? "逐手机凭据" : "共享令牌") + " · "
                        + exception.getMessage());
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
