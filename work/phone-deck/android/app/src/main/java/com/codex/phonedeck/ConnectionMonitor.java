package com.codex.phonedeck;

import android.content.Context;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Future;
import java.util.concurrent.SynchronousQueue;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.function.LongSupplier;

/**
 * 进程内共享的多电脑在线探测。
 *
 * <ul>
 *   <li>所有电脑并行探测：一轮耗时约等于一次探测超时，而不是电脑数 × 超时，
 *       离线电脑不再拖慢在线电脑；</li>
 *   <li>按电脑单独退避：持续离线的电脑按 2/4/8/15 秒降频，在线电脑保持正常节奏；</li>
 *   <li>一次 UDP/mDNS 发现的结果合并给所有未找到的电脑，而不是只给第一台；</li>
 *   <li>主界面与共享麦克风服务共用结果：1.5 秒内的成功结果和进行中的探测直接复用，
 *       不再对同一台电脑重复发 HTTPS 请求。</li>
 * </ul>
 */
final class ConnectionMonitor {
    interface Prober {
        PhoneDeckLanClient.ProbeOutcome probe(TargetDeviceManager.Device device);
    }

    interface Discoverer {
        Map<String, LanDiscoveryClient.DiscoveredComputer> discover();
    }

    /// 把发现到的新地址并入设备记录，返回更新后的设备；地址未变化或设备不存在时返回 null。
    interface AddressSink {
        TargetDeviceManager.Device merge(String computerId, String address);
    }

    static final class Outcome {
        final PhoneDeckLanClient.ProbeResult result;
        final boolean pairingRejected;
        /// 本轮因退避未实际探测，沿用上次结论（离线）。
        final boolean skipped;
        /// 该结果对应的探测开始时刻（elapsedRealtime）；复用缓存时早于本轮。
        final long startedAt;

        Outcome(PhoneDeckLanClient.ProbeResult result, boolean pairingRejected, boolean skipped,
                long startedAt) {
            this.result = result;
            this.pairingRejected = pairingRejected;
            this.skipped = skipped;
            this.startedAt = startedAt;
        }

        boolean online() {
            return result != null;
        }
    }

    static final long CACHE_MS = 1_500;
    static final long DISCOVERY_COOLDOWN_MS = 10_000;
    /// 上限 15 秒：重新开机的电脑最迟约 15 秒回到共享组；网络变化时立即重置。
    static final long[] OFFLINE_BACKOFF_MS = {2_000, 4_000, 8_000, 15_000};
    /// 单台探测（含并行地址）的等待上限，与 PhoneDeckLanClient 的探测截止时间一致并留余量。
    static final long ROUND_DEADLINE_MS = 2_400;

    private static final class State {
        Outcome last;
        long lastProbedAt;
        int failStreak;
        long nextDueAt;
        Future<Outcome> inFlight;
    }

    private static ConnectionMonitor instance;

    private final Prober prober;
    private final Discoverer discoverer;
    private final AddressSink addressSink;
    private final LongSupplier clock;
    private final ExecutorService pool;
    private final Map<String, State> states = new HashMap<>();
    private long lastDiscoveryAt = Long.MIN_VALUE / 2;

    static synchronized ConnectionMonitor get(Context context) {
        if (instance == null) {
            Context app = context.getApplicationContext();
            TargetDeviceManager devices = TargetDeviceManager.get(app);
            instance = new ConnectionMonitor(
                    PhoneDeckLanClient::probeOutcome,
                    () -> LanDiscoveryClient.discover(app, 700),
                    (computerId, address) -> devices.mergeDiscoveredAddress(computerId, address)
                            ? devices.find(computerId) : null,
                    android.os.SystemClock::elapsedRealtime);
        }
        return instance;
    }

    ConnectionMonitor(Prober prober, Discoverer discoverer, AddressSink addressSink,
                      LongSupplier clock) {
        this.prober = prober;
        this.discoverer = discoverer;
        this.addressSink = addressSink;
        this.clock = clock;
        this.pool = new ThreadPoolExecutor(0, 32, 30, TimeUnit.SECONDS,
                new SynchronousQueue<>(), runnable -> {
                    Thread thread = new Thread(runnable, "PhoneDeck-Monitor");
                    thread.setDaemon(true);
                    return thread;
                }, new ThreadPoolExecutor.CallerRunsPolicy());
    }

    /// 网络变化：立即重新探测全部电脑并允许马上发现。
    synchronized void reset() {
        for (State state : states.values()) {
            state.failStreak = 0;
            state.nextDueAt = 0;
            state.lastProbedAt = 0;
        }
        lastDiscoveryAt = Long.MIN_VALUE / 2;
    }

    /// 只探测一台（例如切换目标前确认），忽略退避与缓存。
    Outcome probeNow(TargetDeviceManager.Device device) {
        List<TargetDeviceManager.Device> one = new ArrayList<>();
        one.add(device);
        Outcome outcome = probeAll(one, true).get(device.computerId);
        return outcome == null ? new Outcome(null, false, false, clock.getAsLong()) : outcome;
    }

    /// 探测所有具备局域网配对的电脑；返回 computerId → 结论（无配对的电脑不在结果中）。
    Map<String, Outcome> probeAll(List<TargetDeviceManager.Device> devices, boolean force) {
        Map<String, Outcome> outcomes = new HashMap<>();
        Map<String, Future<Outcome>> pending = new HashMap<>();
        Map<String, TargetDeviceManager.Device> byId = new HashMap<>();
        long now = clock.getAsLong();
        synchronized (this) {
            for (TargetDeviceManager.Device device : devices) {
                if (device == null || !device.hasLanPairing()) {
                    continue;
                }
                byId.put(device.computerId, device);
                State state = stateFor(device.computerId);
                if (!force && state.last != null && state.last.online()
                        && now - state.lastProbedAt < CACHE_MS) {
                    outcomes.put(device.computerId, state.last);
                } else if (state.inFlight != null && !state.inFlight.isDone()) {
                    pending.put(device.computerId, state.inFlight);
                } else if (!force && state.failStreak > 0 && now < state.nextDueAt) {
                    outcomes.put(device.computerId, new Outcome(null,
                            state.last != null && state.last.pairingRejected, true, now));
                } else {
                    Future<Outcome> future = submit(device);
                    state.inFlight = future;
                    pending.put(device.computerId, future);
                }
            }
        }
        collect(pending, outcomes);

        List<String> missing = new ArrayList<>();
        for (Map.Entry<String, Outcome> entry : outcomes.entrySet()) {
            if (!entry.getValue().online() && !entry.getValue().skipped) {
                missing.add(entry.getKey());
            }
        }
        if (!missing.isEmpty() && claimDiscovery()) {
            Map<String, LanDiscoveryClient.DiscoveredComputer> found = discoverer.discover();
            Map<String, Future<Outcome>> retries = new HashMap<>();
            for (String computerId : missing) {
                LanDiscoveryClient.DiscoveredComputer discovered = found.get(computerId);
                TargetDeviceManager.Device device = byId.get(computerId);
                if (discovered == null || device == null || discovered.port != device.lanPort) {
                    continue;
                }
                TargetDeviceManager.Device updated = addressSink.merge(computerId, discovered.hostAddress);
                if (updated != null) {
                    synchronized (this) {
                        Future<Outcome> future = submit(updated);
                        stateFor(computerId).inFlight = future;
                        retries.put(computerId, future);
                    }
                }
            }
            collect(retries, outcomes);
        }

        synchronized (this) {
            long finishedAt = clock.getAsLong();
            for (Map.Entry<String, Outcome> entry : outcomes.entrySet()) {
                if (entry.getValue().skipped) {
                    continue;
                }
                State state = stateFor(entry.getKey());
                if (state.last == entry.getValue() && entry.getValue().online()
                        && finishedAt - state.lastProbedAt < CACHE_MS) {
                    continue; // 缓存命中，不改变节奏。
                }
                state.last = entry.getValue();
                state.lastProbedAt = finishedAt;
                if (entry.getValue().online()) {
                    state.failStreak = 0;
                    state.nextDueAt = 0;
                } else {
                    long delay = OFFLINE_BACKOFF_MS[Math.min(state.failStreak, OFFLINE_BACKOFF_MS.length - 1)];
                    state.failStreak++;
                    state.nextDueAt = finishedAt + delay;
                }
            }
        }
        return outcomes;
    }

    private Future<Outcome> submit(TargetDeviceManager.Device device) {
        return pool.submit(() -> {
            long startedAt = clock.getAsLong();
            PhoneDeckLanClient.ProbeOutcome outcome = prober.probe(device);
            return outcome == null ? new Outcome(null, false, false, startedAt)
                    : new Outcome(outcome.result, outcome.pairingRejected, false, startedAt);
        });
    }

    private void collect(Map<String, Future<Outcome>> futures, Map<String, Outcome> into) {
        long deadline = clock.getAsLong() + ROUND_DEADLINE_MS;
        for (Map.Entry<String, Future<Outcome>> entry : futures.entrySet()) {
            Outcome outcome;
            try {
                long remaining = Math.max(1, deadline - clock.getAsLong());
                outcome = entry.getValue().get(remaining, TimeUnit.MILLISECONDS);
            } catch (InterruptedException interrupted) {
                Thread.currentThread().interrupt();
                outcome = new Outcome(null, false, false, clock.getAsLong());
            } catch (Exception failed) {
                outcome = new Outcome(null, false, false, clock.getAsLong());
            }
            into.put(entry.getKey(), outcome);
        }
    }

    private synchronized boolean claimDiscovery() {
        long now = clock.getAsLong();
        if (now - lastDiscoveryAt < DISCOVERY_COOLDOWN_MS) {
            return false;
        }
        lastDiscoveryAt = now;
        return true;
    }

    private State stateFor(String computerId) {
        String key = computerId.toLowerCase(java.util.Locale.ROOT);
        State state = states.get(key);
        if (state == null) {
            state = new State();
            states.put(key, state);
        }
        return state;
    }
}
