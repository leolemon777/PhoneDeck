package com.codex.phonedeck;

import android.content.Context;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
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
        /// 同一电脑的探测序号；较新的序号优先写入缓存，避免慢发现覆盖新结果。
        final long probeSequence;

        Outcome(PhoneDeckLanClient.ProbeResult result, boolean pairingRejected, boolean skipped,
                long startedAt) {
            this(result, pairingRejected, skipped, startedAt, -1L);
        }

        Outcome(PhoneDeckLanClient.ProbeResult result, boolean pairingRejected, boolean skipped,
                long startedAt, long probeSequence) {
            this.result = result;
            this.pairingRejected = pairingRejected;
            this.skipped = skipped;
            this.startedAt = startedAt;
            this.probeSequence = probeSequence;
        }

        boolean online() {
            return result != null;
        }
    }

    /**
     * 一轮探测的结果批次。
     *
     * reset() 可能在探测线程等待 HTTPS 或发现结果时发生。无效批次必须和
     * “本轮所有电脑都没有结果”的正常空 Map 区分开，否则共享麦克风会主动
     * 拆掉仍然有效的音频接收端。
     */
    static final class Batch extends HashMap<String, Outcome> {
        private final boolean invalidated;

        private Batch(boolean invalidated) {
            this.invalidated = invalidated;
        }

        static Batch normal() {
            return new Batch(false);
        }

        static Batch invalidated() {
            return new Batch(true);
        }

        static boolean isInvalidated(Map<String, Outcome> batch) {
            return batch instanceof Batch && ((Batch) batch).invalidated;
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
        long inFlightGeneration = -1L;
        long inFlightSequence = -1L;
        long latestProbeSequence = -1L;
        long nextProbeSequence;
        /// Activity and shared-audio service can wait on the same Future.  A
        /// timeout may release only the caller that timed out; do not cancel a
        /// probe another caller is still waiting for.
        int inFlightWaiters;
    }

    private static final class PendingProbe {
        final Future<Outcome> future;
        final long generation;
        final long sequence;

        PendingProbe(Future<Outcome> future, long generation, long sequence) {
            this.future = future;
            this.generation = generation;
            this.sequence = sequence;
        }
    }

    private static ConnectionMonitor instance;

    private final Prober prober;
    private final Discoverer discoverer;
    private final AddressSink addressSink;
    private final LongSupplier clock;
    private final ExecutorService pool;
    private final Map<String, State> states = new HashMap<>();
    private long lastDiscoveryAt = Long.MIN_VALUE / 2;
    /// Incremented when the network changes.  Results from an older round are
    /// never allowed to update the new network's cache/backoff state.
    private long generation;

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
        generation++;
        for (State state : states.values()) {
            // A reset invalidates the old round.  Clear each Future exactly
            // once; a second network callback sees the cleared reference and
            // cannot repeatedly cancel a shared probe.
            if (state.inFlight != null && !state.inFlight.isDone()) {
                state.inFlight.cancel(true);
            }
            state.inFlight = null;
            state.inFlightGeneration = -1L;
            state.inFlightSequence = -1L;
            state.inFlightWaiters = 0;
            state.failStreak = 0;
            state.nextDueAt = 0;
            // Invalidate the cache even during the first 1.5 seconds after
            // process start; elapsedRealtime() can legitimately be < CACHE_MS.
            state.lastProbedAt = Long.MIN_VALUE / 2;
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
    Batch probeAll(List<TargetDeviceManager.Device> devices, boolean force) {
        Batch outcomes = Batch.normal();
        Map<String, PendingProbe> pending = new HashMap<>();
        Map<String, TargetDeviceManager.Device> byId = new HashMap<>();
        Map<String, Long> roundSequences = new HashMap<>();
        Map<String, TargetDeviceManager.Device> uniqueDevices = new LinkedHashMap<>();
        for (TargetDeviceManager.Device device : devices) {
            if (device == null || !device.hasLanPairing() || device.computerId == null) {
                continue;
            }
            String key = device.computerId.trim().toLowerCase(java.util.Locale.ROOT);
            if (!key.isEmpty()) {
                // The result map is keyed by computer id, so a duplicate copy
                // could never receive its own release in collect().
                uniqueDevices.putIfAbsent(key, device);
            }
        }
        long now = clock.getAsLong();
        long roundGeneration;
        synchronized (this) {
            roundGeneration = generation;
            for (TargetDeviceManager.Device device : uniqueDevices.values()) {
                byId.put(device.computerId, device);
                State state = stateFor(device.computerId);
                if (!force && state.last != null && state.last.online()
                        && now - state.lastProbedAt < CACHE_MS) {
                    outcomes.put(device.computerId, state.last);
                } else if (state.inFlight != null && !state.inFlight.isDone()) {
                    state.inFlightWaiters++;
                    pending.put(device.computerId,
                            new PendingProbe(state.inFlight, state.inFlightGeneration,
                                    state.inFlightSequence));
                    roundSequences.put(device.computerId, state.inFlightSequence);
                } else if (!force && state.failStreak > 0 && now < state.nextDueAt) {
                    outcomes.put(device.computerId, new Outcome(null,
                            state.last != null && state.last.pairingRejected, true, now,
                            state.latestProbeSequence));
                } else {
                    // A completed Future has already been consumed by its
                    // caller(s); do not leave its waiter count attached to a
                    // new round when the completion raced this call.
                    state.inFlight = null;
                    state.inFlightGeneration = -1L;
                    state.inFlightSequence = -1L;
                    state.inFlightWaiters = 0;
                    long sequence = ++state.nextProbeSequence;
                    state.latestProbeSequence = sequence;
                    Future<Outcome> future = submit(device, sequence);
                    state.inFlight = future;
                    state.inFlightGeneration = roundGeneration;
                    state.inFlightSequence = sequence;
                    state.inFlightWaiters = 1;
                    pending.put(device.computerId,
                            new PendingProbe(future, roundGeneration, sequence));
                    roundSequences.put(device.computerId, sequence);
                }
            }
        }
        if (collect(pending, outcomes)) {
            // This caller abandoned a shared Future, but another caller still
            // owns it.  Its timeout/interruption must not enter discovery or
            // publish an offline result; the surviving caller will publish
            // the shared Future's eventual sample with the same sequence.
            return Batch.invalidated();
        }

        synchronized (this) {
            if (roundGeneration != generation) {
                // The reset caller owns the new round.  Do not feed stale
                // outcomes back to Activity/shared-audio callers either.
                return Batch.invalidated();
            }
        }

        List<String> missing = new ArrayList<>();
        for (Map.Entry<String, Outcome> entry : outcomes.entrySet()) {
            if (!entry.getValue().online() && !entry.getValue().skipped) {
                missing.add(entry.getKey());
            }
        }
        if (!missing.isEmpty() && claimDiscovery()) {
            Map<String, LanDiscoveryClient.DiscoveredComputer> found = discoverer.discover();
            synchronized (this) {
                if (roundGeneration != generation) {
                    return Batch.invalidated();
                }
            }
            Map<String, PendingProbe> retries = new HashMap<>();
            for (String computerId : missing) {
                LanDiscoveryClient.DiscoveredComputer discovered = found.get(computerId);
                TargetDeviceManager.Device device = byId.get(computerId);
                if (discovered == null || device == null || discovered.port != device.lanPort) {
                    continue;
                }
                Long sequence = roundSequences.get(computerId);
                if (sequence == null) {
                    continue;
                }
                synchronized (this) {
                    if (roundGeneration != generation) {
                        return Batch.invalidated();
                    }
                    // Discovery belongs to the original per-target probe.  A
                    // newer force round must win even if this slow discovery
                    // returns later in the same network generation.
                    if (stateFor(computerId).latestProbeSequence != sequence) {
                        continue;
                    }
                }
                TargetDeviceManager.Device updated = addressSink.merge(
                        computerId, discovered.hostAddress);
                if (updated != null) {
                    synchronized (this) {
                        if (roundGeneration != generation) {
                            return Batch.invalidated();
                        }
                        State state = stateFor(computerId);
                        if (state.latestProbeSequence != sequence) {
                            continue;
                        }
                        if (state.inFlight != null && !state.inFlight.isDone()) {
                            // Another caller still owns the original shared
                            // probe.  Do not overwrite or cancel it merely
                            // because discovery found an address refresh.
                            continue;
                        }
                        // A discovery retry is a new probe attempt.  Give it
                        // its own sequence, but only after the guard above
                        // proves that no newer caller has superseded this
                        // round while discovery was running.
                        long retrySequence = ++state.nextProbeSequence;
                        state.latestProbeSequence = retrySequence;
                        state.inFlight = null;
                        state.inFlightGeneration = -1L;
                        state.inFlightSequence = -1L;
                        state.inFlightWaiters = 0;
                        Future<Outcome> future = submit(updated, retrySequence);
                        state.inFlight = future;
                        state.inFlightGeneration = roundGeneration;
                        state.inFlightSequence = retrySequence;
                        state.inFlightWaiters = 1;
                        retries.put(computerId,
                                new PendingProbe(future, roundGeneration, retrySequence));
                    }
                }
            }
            if (collect(retries, outcomes)) {
                return Batch.invalidated();
            }
        }

        synchronized (this) {
            if (roundGeneration != generation) {
                return Batch.invalidated();
            }
            long finishedAt = clock.getAsLong();
            // Do not merely skip stale cache writes: the returned batch is
            // consumed by Activity and shared audio.  Returning an old
            // offline outcome would still make those consumers tear down a
            // target that a newer caller has already brought online.
            for (Map.Entry<String, Outcome> entry : outcomes.entrySet()) {
                State state = stateFor(entry.getKey());
                if (entry.getValue().probeSequence >= 0
                        && entry.getValue().probeSequence != state.latestProbeSequence) {
                    return Batch.invalidated();
                }
                if (!entry.getValue().online() && state.last != null && state.last.online()
                        && entry.getValue().probeSequence == state.last.probeSequence) {
                    // A shared Future can complete just as this caller times
                    // out. Another waiter may already have published success
                    // and released the last reference before releaseWaiter().
                    // An observation timeout cannot undo that same sample.
                    return Batch.invalidated();
                }
            }
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

    private Future<Outcome> submit(TargetDeviceManager.Device device, long sequence) {
        return pool.submit(() -> {
            long startedAt = clock.getAsLong();
            PhoneDeckLanClient.ProbeOutcome outcome = prober.probe(device);
            return outcome == null ? new Outcome(null, false, false, startedAt, sequence)
                    : new Outcome(outcome.result, outcome.pairingRejected, false, startedAt,
                            sequence);
        });
    }

    private boolean collect(Map<String, PendingProbe> futures, Map<String, Outcome> into) {
        long deadline = clock.getAsLong() + ROUND_DEADLINE_MS;
        boolean abandonedSharedProbe = false;
        for (Map.Entry<String, PendingProbe> entry : futures.entrySet()) {
            Outcome outcome;
            boolean abandon = false;
            try {
                long remaining = Math.max(1, deadline - clock.getAsLong());
                outcome = entry.getValue().future.get(remaining, TimeUnit.MILLISECONDS);
            } catch (InterruptedException interrupted) {
                abandon = true;
                Thread.currentThread().interrupt();
                outcome = new Outcome(null, false, false, clock.getAsLong(),
                        entry.getValue().sequence);
            } catch (java.util.concurrent.TimeoutException timeout) {
                abandon = true;
                outcome = new Outcome(null, false, false, clock.getAsLong(),
                        entry.getValue().sequence);
            } catch (Exception failed) {
                outcome = new Outcome(null, false, false, clock.getAsLong(),
                        entry.getValue().sequence);
            }
            if (outcome.probeSequence != entry.getValue().sequence) {
                // Preserve the attempt identity when normalizing an outcome.
                // All callers sharing this Future use the same sequence.
                outcome = new Outcome(outcome.result, outcome.pairingRejected,
                        outcome.skipped, outcome.startedAt, entry.getValue().sequence);
            }
            if (releaseWaiter(entry.getKey(), entry.getValue(), abandon)) {
                abandonedSharedProbe = true;
            }
            into.put(entry.getKey(), outcome);
        }
        return abandonedSharedProbe;
    }

    /**
     * Release one caller's reference to a shared probe.  An abandoned Future
     * (timeout or interrupted caller) is cancelled only when no other caller
     * is still waiting for it; this keeps Activity and PhoneAudioService from
     * cancelling each other's valid probe.  The return value tells collect()
     * whether this caller abandoned a Future that still has another waiter.
     */
    private synchronized boolean releaseWaiter(
            String computerId, PendingProbe pending, boolean abandon) {
        State state = stateFor(computerId);
        if (state.inFlight != pending.future
                || state.inFlightGeneration != pending.generation) {
            return false; // reset() or a newer round already replaced it.
        }
        if (state.inFlightWaiters > 0) {
            state.inFlightWaiters--;
        }
        boolean abandonedSharedProbe = abandon && state.inFlightWaiters > 0;
        if (abandon && state.inFlightWaiters == 0 && !pending.future.isDone()) {
            pending.future.cancel(true);
            state.inFlight = null;
            state.inFlightGeneration = -1L;
            state.inFlightSequence = -1L;
        } else if (state.inFlightWaiters == 0 && pending.future.isDone()) {
            state.inFlight = null;
            state.inFlightGeneration = -1L;
            state.inFlightSequence = -1L;
        }
        return abandonedSharedProbe;
    }

    static boolean isInvalidated(Map<String, Outcome> batch) {
        return Batch.isInvalidated(batch);
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
