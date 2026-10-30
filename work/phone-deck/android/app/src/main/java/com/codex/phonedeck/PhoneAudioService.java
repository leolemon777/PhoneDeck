package com.codex.phonedeck;

import android.Manifest;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ServiceInfo;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.HashMap;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Foreground owner for the opt-in, continuously broadcasting microphone mode. */
public final class PhoneAudioService extends Service {
    static final String ACTION_START = "com.codex.phonedeck.action.START_SHARED_AUDIO";
    static final String ACTION_STOP = "com.codex.phonedeck.action.STOP_SHARED_AUDIO";
    static final String ACTION_STATUS = "com.codex.phonedeck.action.SHARED_AUDIO_STATUS";
    static final String EXTRA_LINKED = "linked";
    static final String EXTRA_RUNNING = "running";
    static final String EXTRA_CONNECTED = "connected";
    static final String EXTRA_TOTAL = "total";
    static final String EXTRA_LEVEL = "level";
    static final String EXTRA_DETAIL = "detail";

    private static final int NOTIFICATION_ID = 1701;
    private static final String CHANNEL_ID = "phonedeck_shared_microphone";
    private static final long PROBE_INTERVAL_MS = 2_000;
    /**
     * A health probe is advisory while the audio sink is already running. Keep
     * the last accepted target for a short, bounded window so one Wi-Fi roam or
     * a slow HTTPS request does not tear down a working stream. A sink that has
     * demonstrably made progress may refresh this window; the broadcaster's
     * write watchdog bounds that exception when progress stops. The sink itself
     * owns its reconnect backoff; this window only prevents the probe loop from
     * replacing/removing it on every transient failure.
     */
    private static final int MAX_TRANSIENT_TARGET_FAILURES = 4;
    private static final long MAX_TRANSIENT_TARGET_RETENTION_MS = 15_000;

    private static final class RetainedTarget {
        final SharedAudioBroadcaster.Target target;
        /** Last successful health response or observed live sink progress. */
        final long lastGoodAt;
        final int transientFailures;

        RetainedTarget(SharedAudioBroadcaster.Target target, long lastGoodAt,
                       int transientFailures) {
            this.target = target;
            this.lastGoodAt = lastGoodAt;
            this.transientFailures = transientFailures;
        }
    }

    private enum SharedAudioReadiness {
        READY,
        EXPLICITLY_UNAVAILABLE,
        CAPABILITY_MISSING,
        UNKNOWN
    }

    static final class Snapshot {
        final boolean running;
        final boolean busy;
        final int connected;
        final int total;
        final int level;
        final String detail;
        final Map<String, String> receiverStates;

        Snapshot(boolean running, boolean busy, int connected, int total, int level, String detail,
                 Map<String, String> receiverStates) {
            this.running = running;
            this.busy = busy;
            this.connected = connected;
            this.total = total;
            this.level = level;
            this.detail = detail;
            this.receiverStates = java.util.Collections.unmodifiableMap(
                    new HashMap<>(receiverStates));
        }
    }

    private static volatile Snapshot snapshot = new Snapshot(
            false, false, 0, 0, 0, "未开启", java.util.Collections.emptyMap());

    static Snapshot getSnapshot() {
        return snapshot;
    }

    /// 进程内状态监听：音量每 100 ms 变化一次，只更新快照并回调，不再发系统广播。
    private static volatile Runnable inProcessListener;

    static void setInProcessListener(Runnable listener) {
        inProcessListener = listener;
    }

    private volatile Map<String, Integer> slotByComputer = java.util.Collections.emptyMap();

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService probeExecutor = Executors.newSingleThreadExecutor();
    private TargetDeviceManager deviceManager;
    private SharedAudioBroadcaster broadcaster;
    private WifiManager.WifiLock wifiLock;
    private PowerManager.WakeLock wakeLock;
    private volatile boolean desiredRunning;
    private boolean stopping;
    private boolean serviceDestroyed;
    /// 独立于 Wi-Fi 代次的服务会话代次；stop→start 不能接纳上一会话的迟到批次。
    private volatile long serviceSessionRevision;
    /// 本次共享是由电脑 shared.requested 联动开启（而不是用户在手机上手动开启）；
    /// 只有联动开启的会话才会在电脑全部取消请求后自动停止。
    private volatile boolean startedByLinkage;
    private volatile boolean probeInFlight;
    private volatile int knownTotal;
    private volatile int connectedCount;
    /// M1-B/MIC-10：共享组零可达目标的起始时刻（elapsedRealtime；0=组内有目标）。
    private volatile long emptyTargetsSinceElapsed;
    private volatile int lastLevel;
    private volatile String statusDetail = "正在准备";
    /** Last text posted to the ongoing notification; repeats are skipped (main thread only). */
    private String postedNotificationText;
    private final ConcurrentHashMap<String, String> receiverStates =
            new ConcurrentHashMap<>();
    /** Last target map accepted by the current broadcaster session. */
    private final ConcurrentHashMap<String, SharedAudioBroadcaster.Target> appliedTargets =
            new ConcurrentHashMap<>();
    /** Per-target health window used to bound transient-failure retention. */
    private final ConcurrentHashMap<String, RetainedTarget> retainedTargets =
            new ConcurrentHashMap<>();
    private final Object targetStateLock = new Object();
    private WifiLinkObserver.Registration wifiLinkRegistration;

    private final Runnable probeTick = new Runnable() {
        @Override
        public void run() {
            if (!desiredRunning) {
                return;
            }
            probeReceivers();
            handler.postDelayed(this, PROBE_INTERVAL_MS);
        }
    };

    @Override
    public void onCreate() {
        super.onCreate();
        TranscriptRelay.acquire(this);
        deviceManager = TargetDeviceManager.get(this);
        createNotificationChannel();
        broadcaster = new SharedAudioBroadcaster(this, new SharedAudioBroadcaster.Listener() {
            @Override
            public void onLevel(String sessionId, int percent) {
                handler.post(() -> {
                    if (!isCurrentAudioSession(sessionId) || !desiredRunning || stopping) {
                        return;
                    }
                    lastLevel = percent;
                    publishStatus(false);
                });
            }

            @Override
            public void onConnectionsChanged(String sessionId) {
                handler.post(() -> {
                    if (!isCurrentAudioSession(sessionId) || !desiredRunning || stopping) {
                        return;
                    }
                    mergeCurrentConnections();
                    if (connectedCount > 0) {
                        // A sink that actually completed a write is a confirmed
                        // target; do not let the no-audio recovery timer stop a
                        // session merely because another target is still offline.
                        emptyTargetsSinceElapsed = 0L;
                    }
                    statusDetail = connectedCount > 0
                            ? MultiComputerStatus.summarize(receiverStates, slotByComputer)
                            : "正在等待音频接收端";
                    publishStatus();
                    updateNotification();
                });
            }

            @Override
            public void onStopped(String sessionId, String reason) {
                handler.post(() -> {
                    if (!isCurrentAudioSession(sessionId)) {
                        return;
                    }
                    desiredRunning = false;
                    statusDetail = reason == null ? "共享麦克风已关闭"
                            : "共享麦克风已停止：" + reason;
                    finishSharedStop();
                });
            }
        });
    }

    private boolean isCurrentAudioSession(String sessionId) {
        return WifiLinkPolicy.isCurrentSessionEvent(sessionId,
                broadcaster == null ? null : broadcaster.getSessionId(), !serviceDestroyed);
    }

    /** Main-thread state merge; reads only the broadcaster's in-memory flags. */
    private void mergeCurrentConnections() {
        int connected = 0;
        for (Map.Entry<String, Boolean> entry : broadcaster.connectionSnapshot().entrySet()) {
            if (Boolean.TRUE.equals(entry.getValue())) {
                connected++;
                receiverStates.put(entry.getKey(), "正在供音");
            } else if (!"配置错误".equals(receiverStates.get(entry.getKey()))
                    && !"需要更新电脑端".equals(receiverStates.get(entry.getKey()))) {
                receiverStates.put(entry.getKey(), "连接中");
            }
        }
        connectedCount = connected;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent == null ? null : intent.getAction();
        if (ACTION_STOP.equals(action)) {
            stopShared();
            return START_NOT_STICKY;
        }
        if (ACTION_START.equals(action)) {
            startedByLinkage = intent != null && intent.getBooleanExtra(EXTRA_LINKED, false);
            startShared();
        }
        return START_NOT_STICKY;
    }

    private void startShared() {
        if (stopping || broadcaster.isFinishing()) {
            statusDetail = "手机录音已停止，正在发送最后一段声音，请稍后重试";
            publishStatus();
            return;
        }
        if (desiredRunning) {
            publishStatus();
            return;
        }
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO)
                != PackageManager.PERMISSION_GRANTED) {
            statusDetail = "没有手机麦克风权限";
            publishStatus();
            stopSelf();
            return;
        }
        desiredRunning = true;
        ++serviceSessionRevision;
        clearTargetState();
        knownTotal = countSharedGroupDevices(deviceManager.list());
        registerWifiLinkObserver();
        try {
            acquireLocks();
            postedNotificationText = notificationText();
            Notification notification = buildNotification();
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                startForeground(NOTIFICATION_ID, notification,
                        ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE);
            } else {
                startForeground(NOTIFICATION_ID, notification);
            }
            broadcaster.start(UUID.randomUUID().toString());
        } catch (Exception exception) {
            statusDetail = exception.getMessage();
            stopShared();
            return;
        }
        statusDetail = "正在查找音频接收端";
        publishStatus();
        handler.removeCallbacks(probeTick);
        handler.post(probeTick);
    }

    private void stopShared() {
        if (stopping) {
            return;
        }
        stopping = true;
        serviceSessionRevision++;
        desiredRunning = false;
        unregisterWifiLinkObserver();
        startedByLinkage = false;
        emptyTargetsSinceElapsed = 0L;
        clearTargetState();
        handler.removeCallbacks(probeTick);
        if (broadcaster != null) {
            broadcaster.stop();
        }
        connectedCount = 0;
        receiverStates.clear();
        lastLevel = 0;
        statusDetail = "手机录音已停止，正在发送最后一段声音";
        publishStatus();
        // Keep the service and network/CPU locks until queued PCM is acknowledged.
        if (broadcaster == null || !broadcaster.isFinishing()) {
            finishSharedStop();
        }
    }

    private void finishSharedStop() {
        serviceSessionRevision++;
        desiredRunning = false;
        unregisterWifiLinkObserver();
        startedByLinkage = false;
        stopping = false;
        handler.removeCallbacks(probeTick);
        clearTargetState();
        connectedCount = 0;
        receiverStates.clear();
        lastLevel = 0;
        if (statusDetail.equals("手机录音已停止，正在发送最后一段声音")) {
            statusDetail = "共享麦克风已关闭";
        }
        releaseLocks();
        stopForeground(STOP_FOREGROUND_REMOVE);
        publishStatus();
        stopSelf();
    }

    private void registerWifiLinkObserver() {
        if (wifiLinkRegistration != null) {
            return;
        }
        try {
            wifiLinkRegistration = WifiLinkObserver.register(this, state -> {
                if (desiredRunning && !stopping) {
                    // The observer has already reset the shared monitor once
                    // for this Wi-Fi generation.  Re-probe immediately while
                    // preserving the explicit microphone opt-in state.
                    probeReceivers();
                }
            });
        } catch (Exception exception) {
            statusDetail = "Wi-Fi 状态监听不可用：" + exception.getMessage();
        }
    }

    private void unregisterWifiLinkObserver() {
        if (wifiLinkRegistration != null) {
            wifiLinkRegistration.close();
            wifiLinkRegistration = null;
        }
    }

    private void probeReceivers() {
        if (!desiredRunning || stopping || probeInFlight) {
            return;
        }
        probeInFlight = true;
        final long probeGeneration = WifiLinkObserver.currentGeneration();
        final long probeSessionRevision = serviceSessionRevision;
        final String probeSessionId = broadcaster == null ? null : broadcaster.getSessionId();
        probeExecutor.execute(() -> {
            try {
                Map<String, SharedAudioBroadcaster.Target> previousTargets;
                Map<String, RetainedTarget> previousRetained;
                synchronized (targetStateLock) {
                    previousTargets = new HashMap<>(appliedTargets);
                    previousRetained = new HashMap<>(retainedTargets);
                }
                Map<String, SharedAudioBroadcaster.Target> targets = new HashMap<>();
                Map<String, RetainedTarget> nextRetained = new HashMap<>();
                Map<String, String> states = new HashMap<>();
                Map<String, String> goodAddresses = new HashMap<>();
                java.util.List<TargetDeviceManager.Device> devices = deviceManager.list();
                int incompatible = 0;
                int reachable = 0;
                boolean anyRequested = false;
                java.util.Set<String> groupIds = new java.util.HashSet<>();
                java.util.Set<String> freshLanTargets = new java.util.HashSet<>();
                java.util.Set<String> explicitRemovals = new java.util.HashSet<>();
                Map<String, TargetDeviceManager.Device> groupDevices = new HashMap<>();
                // 只探测共享组：全部并行，与主界面共用结果与各台电脑的离线退避。
                java.util.List<TargetDeviceManager.Device> group = new java.util.ArrayList<>();
                Map<String, Integer> slots = new HashMap<>();
                for (TargetDeviceManager.Device device : devices) {
                    if (device == null || device.computerId == null) {
                        continue;
                    }
                    slots.put(device.computerId, device.slot);
                    if (device.sharedGroup) {
                        group.add(device);
                        groupIds.add(targetKey(device.computerId));
                        groupDevices.put(targetKey(device.computerId), device);
                    }
                }
                // 总数只表示显式共享组中的已配对电脑；未加入共享组的设备不应出现在
                // “正在向 x/y 台电脑供音”的分母里。
                Map<String, ConnectionMonitor.Outcome> outcomes =
                        ConnectionMonitor.get(this).probeAll(group, false);
                if (ConnectionMonitor.isInvalidated(outcomes)) {
                    // 网络切换已使本轮结果失效；保留当前 targets、状态和分母，
                    // 不把无效空批次当成共享组全部离线。
                    return;
                }
                if (!WifiLinkPolicy.shouldApply(probeGeneration,
                        WifiLinkObserver.currentGeneration(), probeSessionRevision,
                        serviceSessionRevision, desiredRunning && !stopping)) {
                    return;
                }
                Map<String, Boolean> connectionSnapshot = broadcaster == null
                        ? java.util.Collections.emptyMap() : broadcaster.connectionSnapshot();
                final long observedAt = android.os.SystemClock.elapsedRealtime();
                for (TargetDeviceManager.Device device : group) {
                    // M1-B/DEV-03：共享组是显式集合，新增配对不自动入组。
                    states.put(device.computerId, "离线");
                    ConnectionMonitor.Outcome outcome = outcomes.get(device.computerId);
                    PhoneDeckLanClient.ProbeResult result = outcome == null
                            ? null : outcome.result;
                    if (result == null) {
                        if (outcome != null && outcome.pairingRejected) {
                            // 地址可达但令牌被明确拒绝：这是显式失效，不保留旧 sink。
                            explicitRemovals.add(targetKey(device.computerId));
                            states.put(device.computerId, "需要重新配对");
                        } else {
                            RetainedTarget retained = retainTransientTarget(
                                    device, previousTargets, previousRetained,
                                    isConnected(connectionSnapshot, device.computerId), false,
                                    observedAt);
                            if (retained != null) {
                                targets.put(device.computerId, retained.target);
                                nextRetained.put(device.computerId, retained);
                                states.put(device.computerId, "连接检查暂不可用");
                            }
                        }
                        continue;
                    }
                    reachable++;
                    if (isSharedRequested(result.health)) {
                        anyRequested = true;
                    }
                    goodAddresses.put(device.computerId, result.hostAddress);
                    SharedAudioReadiness readiness = sharedAudioReadiness(result.health);
                    if (readiness == SharedAudioReadiness.READY) {
                        SharedAudioBroadcaster.Target target = new SharedAudioBroadcaster.Target(
                                device.computerId, device.displayName, result.endpoint);
                        targets.put(device.computerId, target);
                        nextRetained.put(device.computerId,
                                new RetainedTarget(target, observedAt, 0));
                        freshLanTargets.add(targetKey(device.computerId));
                        states.put(device.computerId, "连接中");
                    } else if (readiness == SharedAudioReadiness.UNKNOWN) {
                        RetainedTarget retained = retainTransientTarget(
                                device, previousTargets, previousRetained,
                                isConnected(connectionSnapshot, device.computerId), false,
                                observedAt);
                        if (retained != null) {
                            targets.put(device.computerId, retained.target);
                            nextRetained.put(device.computerId, retained);
                        }
                        states.put(device.computerId, "连接检查暂不可用");
                    } else {
                        incompatible++;
                        explicitRemovals.add(targetKey(device.computerId));
                        states.put(device.computerId,
                                hasSharedCapability(result.health)
                                        ? "配置错误" : "需要更新电脑端");
                    }
                }

                // LAN is preferred. USB only fills the current directly connected computer.
                try {
                    JSONObject health = PhoneDeckHttp.getJson(
                            PhoneDeckEndpoint.USB, "/api/health", 600, 800);
                    String computerId = health.optString("computerId", "").trim();
                    // USB is a transport fallback, never permission to expand the explicit group.
                    TargetDeviceManager.Device usbDevice = deviceManager.find(computerId);
                    if (usbDevice == null || !usbDevice.sharedGroup
                            || !groupIds.contains(targetKey(computerId))) {
                        removeUsbTargets(targets, nextRetained);
                        computerId = "";
                    } else {
                        computerId = usbDevice.computerId;
                    }
                    if (!computerId.isEmpty()) {
                        reachable++;
                        if (isSharedRequested(health)) {
                            anyRequested = true;
                        }
                    }
                    if (!computerId.isEmpty()) {
                        removeUsbTargets(targets, nextRetained);
                        SharedAudioReadiness readiness = sharedAudioReadiness(health);
                        if (readiness == SharedAudioReadiness.READY) {
                            // LAN is preferred when its probe succeeded this round;
                            // otherwise a verified USB health response may replace a
                            // retained/stale LAN endpoint immediately.
                            if (!freshLanTargets.contains(targetKey(computerId))) {
                                SharedAudioBroadcaster.Target target =
                                        new SharedAudioBroadcaster.Target(
                                                computerId,
                                                health.optString("displayName", "USB 电脑"),
                                                PhoneDeckEndpoint.USB);
                                targets.put(computerId, target);
                                nextRetained.put(computerId,
                                        new RetainedTarget(target, observedAt, 0));
                                states.put(computerId, "连接中");
                            }
                        } else if (readiness == SharedAudioReadiness.UNKNOWN) {
                            if (!freshLanTargets.contains(targetKey(computerId))) {
                                retainUsbTargets(previousTargets, previousRetained, groupIds,
                                        explicitRemovals, groupDevices, connectionSnapshot,
                                        targets, nextRetained, states, observedAt);
                            }
                            states.putIfAbsent(computerId, "USB 连接检查暂不可用");
                        } else if (!freshLanTargets.contains(targetKey(computerId))) {
                            incompatible++;
                            explicitRemovals.add(targetKey(computerId));
                            removeTarget(targets, nextRetained, computerId);
                            states.put(computerId, hasSharedCapability(health)
                                    ? "配置错误" : "需要更新电脑端");
                        }
                    }
                } catch (Exception exception) {
                    if (isExplicitPairingRejection(exception)) {
                        removeUsbTargets(targets, nextRetained);
                    } else {
                        retainUsbTargets(previousTargets, previousRetained, groupIds,
                                explicitRemovals, groupDevices, connectionSnapshot,
                                targets, nextRetained, states, observedAt);
                    }
                }
                if (!WifiLinkPolicy.shouldApply(probeGeneration,
                        WifiLinkObserver.currentGeneration(), probeSessionRevision,
                        serviceSessionRevision, desiredRunning && !stopping)) {
                    return;
                }
                final int finalIncompatible = incompatible;
                final int finalReachable = reachable;
                final boolean finalAnyRequested = anyRequested;
                boolean targetsApplied = broadcaster != null && probeSessionId != null
                        && broadcaster.updateTargetsIfCurrent(targets, probeSessionId,
                        () -> WifiLinkPolicy.shouldApply(probeGeneration,
                                WifiLinkObserver.currentGeneration(), probeSessionRevision,
                                serviceSessionRevision, desiredRunning && !stopping));
                if (!targetsApplied) {
                    return;
                }
                synchronized (targetStateLock) {
                    if (!WifiLinkPolicy.shouldApply(probeGeneration,
                            WifiLinkObserver.currentGeneration(), probeSessionRevision,
                            serviceSessionRevision, desiredRunning && !stopping)) {
                        return;
                    }
                    appliedTargets.clear();
                    appliedTargets.putAll(targets);
                    retainedTargets.clear();
                    retainedTargets.putAll(nextRetained);
                }
                handler.post(() -> applyProbeResult(
                        probeGeneration, probeSessionRevision, group.size(), slots, targets, states,
                        goodAddresses, finalIncompatible, finalReachable,
                        finalAnyRequested));
            } finally {
                probeInFlight = false;
                handler.post(() -> {
                    if (desiredRunning && !stopping
                            && serviceSessionRevision == probeSessionRevision
                            && WifiLinkObserver.currentGeneration() != probeGeneration) {
                        probeReceivers();
                    }
                });
            }
        });
    }

    private static String targetKey(String computerId) {
        return computerId == null ? "" : computerId.trim().toLowerCase(java.util.Locale.ROOT);
    }

    private static SharedAudioBroadcaster.Target findTarget(
            Map<String, SharedAudioBroadcaster.Target> targets, String computerId) {
        SharedAudioBroadcaster.Target direct = targets.get(computerId);
        if (direct != null) {
            return direct;
        }
        for (Map.Entry<String, SharedAudioBroadcaster.Target> entry : targets.entrySet()) {
            if (entry.getKey() != null && entry.getKey().equalsIgnoreCase(computerId)) {
                return entry.getValue();
            }
        }
        return null;
    }

    private static RetainedTarget findRetained(
            Map<String, RetainedTarget> retained, String computerId) {
        RetainedTarget direct = retained.get(computerId);
        if (direct != null) {
            return direct;
        }
        for (Map.Entry<String, RetainedTarget> entry : retained.entrySet()) {
            if (entry.getKey() != null && entry.getKey().equalsIgnoreCase(computerId)) {
                return entry.getValue();
            }
        }
        return null;
    }

    /**
     * Keep an already accepted endpoint through a transient monitor failure.
     * The bounded window is deliberately independent of ConnectionMonitor's
     * offline backoff: one timeout is harmless, but a target that never
     * recovers eventually leaves the desired map and starts the normal
     * zero-target stop timer.
     */
    private static RetainedTarget retainTransientTarget(
            TargetDeviceManager.Device device,
            Map<String, SharedAudioBroadcaster.Target> previousTargets,
            Map<String, RetainedTarget> previousRetained,
            boolean connected,
            boolean allowUsb,
            long now) {
        if (device == null || device.computerId == null) {
            return null;
        }
        String computerId = device.computerId;
        SharedAudioBroadcaster.Target previous = findTarget(previousTargets, computerId);
        if (previous == null || !targetIdentityMatches(device, previous, allowUsb)) {
            return null;
        }
        // A sink that has completed a write is the strongest evidence we have
        // while /health is unavailable. The broadcaster watchdog clears this
        // bit when there is no forward progress, so this does not become an
        // unbounded keep-alive for a dead stream.
        if (connected) {
            return new RetainedTarget(previous, now, 0);
        }
        RetainedTarget prior = findRetained(previousRetained, computerId);
        long lastGoodAt = now;
        int failures = 1;
        if (prior != null && prior.target != null && prior.target.sameEndpoint(previous)
                && now >= prior.lastGoodAt) {
            lastGoodAt = prior.lastGoodAt;
            failures = prior.transientFailures + 1;
        }
        if (failures > MAX_TRANSIENT_TARGET_FAILURES
                || now - lastGoodAt >= MAX_TRANSIENT_TARGET_RETENTION_MS) {
            return null;
        }
        return new RetainedTarget(previous, lastGoodAt, failures);
    }

    private static boolean targetIdentityMatches(TargetDeviceManager.Device device,
                                                 SharedAudioBroadcaster.Target target,
                                                 boolean allowUsb) {
        if (device == null || target == null || target.endpoint == null
                || device.computerId == null
                || !device.computerId.equalsIgnoreCase(target.computerId)) {
            return false;
        }
        if (isUsbTarget(target)) {
            return allowUsb;
        }
        PhoneDeckEndpoint endpoint = target.endpoint;
        return sameCredential(endpoint.accessToken, device.lanToken)
                && sameFingerprint(endpoint.certificateSha256, device.certificateSha256)
                && sameCredential(endpoint.clientId, device.clientId);
    }

    private static boolean sameCredential(String left, String right) {
        return left == null ? right == null : left.equals(right);
    }

    private static boolean sameFingerprint(String left, String right) {
        return left == null ? right == null : left.equalsIgnoreCase(right);
    }

    private static boolean isConnected(Map<String, Boolean> snapshot, String computerId) {
        if (Boolean.TRUE.equals(snapshot.get(computerId))) {
            return true;
        }
        for (Map.Entry<String, Boolean> entry : snapshot.entrySet()) {
            if (entry.getKey() != null && entry.getKey().equalsIgnoreCase(computerId)
                    && Boolean.TRUE.equals(entry.getValue())) {
                return true;
            }
        }
        return false;
    }

    private static boolean isUsbTarget(SharedAudioBroadcaster.Target target) {
        return target != null && target.endpoint != null
                && PhoneDeckEndpoint.USB.baseUrl.equals(target.endpoint.baseUrl);
    }

    private static void removeTarget(Map<String, SharedAudioBroadcaster.Target> targets,
                                     Map<String, RetainedTarget> retained,
                                     String computerId) {
        targets.remove(computerId);
        retained.remove(computerId);
        java.util.Iterator<String> targetIterator = targets.keySet().iterator();
        while (targetIterator.hasNext()) {
            String key = targetIterator.next();
            if (key != null && key.equalsIgnoreCase(computerId)) {
                targetIterator.remove();
            }
        }
        java.util.Iterator<String> retainedIterator = retained.keySet().iterator();
        while (retainedIterator.hasNext()) {
            String key = retainedIterator.next();
            if (key != null && key.equalsIgnoreCase(computerId)) {
                retainedIterator.remove();
            }
        }
    }

    private static void removeUsbTargets(
            Map<String, SharedAudioBroadcaster.Target> targets,
            Map<String, RetainedTarget> retained) {
        java.util.Iterator<Map.Entry<String, SharedAudioBroadcaster.Target>> iterator =
                targets.entrySet().iterator();
        while (iterator.hasNext()) {
            Map.Entry<String, SharedAudioBroadcaster.Target> entry = iterator.next();
            if (isUsbTarget(entry.getValue())) {
                retained.remove(entry.getKey());
                iterator.remove();
            }
        }
    }

    private static void retainUsbTargets(
            Map<String, SharedAudioBroadcaster.Target> previousTargets,
            Map<String, RetainedTarget> previousRetained,
            java.util.Set<String> groupIds,
            java.util.Set<String> explicitRemovals,
            Map<String, TargetDeviceManager.Device> groupDevices,
            Map<String, Boolean> connectionSnapshot,
            Map<String, SharedAudioBroadcaster.Target> targets,
            Map<String, RetainedTarget> retained,
            Map<String, String> states,
            long now) {
        for (Map.Entry<String, SharedAudioBroadcaster.Target> entry
                : previousTargets.entrySet()) {
            String computerId = entry.getKey();
            if (!isUsbTarget(entry.getValue())
                    || !groupIds.contains(targetKey(computerId))
                    || explicitRemovals.contains(targetKey(computerId))
                    || findTarget(targets, computerId) != null) {
                continue;
            }
            TargetDeviceManager.Device device = groupDevices.get(targetKey(computerId));
            RetainedTarget candidate = retainTransientTarget(
                    device, previousTargets, previousRetained,
                    isConnected(connectionSnapshot, computerId), true, now);
            if (candidate != null) {
                targets.put(computerId, candidate.target);
                retained.put(computerId, candidate);
                states.put(computerId, "USB 连接检查暂不可用");
            }
        }
    }

    private static boolean isExplicitPairingRejection(Exception exception) {
        // PhoneDeckLanClient intentionally exposes only explicit 401/403
        // rejections.  SSL/certificate failures are not inferred here because
        // an old address may now belong to another computer; those remain
        // bounded transient failures until the monitor can identify a result.
        return PhoneDeckLanClient.isPairingRejection(exception);
    }

    private void clearTargetState() {
        synchronized (targetStateLock) {
            appliedTargets.clear();
            retainedTargets.clear();
        }
    }

    /** Applies one probe atomically on the service's main lifecycle thread. */
    private void applyProbeResult(long probeGeneration, long probeSessionRevision,
                                  int groupSize,
                                  Map<String, Integer> slots,
                                  Map<String, SharedAudioBroadcaster.Target> targets,
                                  Map<String, String> states,
                                  Map<String, String> goodAddresses,
                                  int incompatible, int reachable, boolean anyRequested) {
        if (!WifiLinkPolicy.shouldApply(probeGeneration,
                WifiLinkObserver.currentGeneration(), probeSessionRevision,
                serviceSessionRevision, desiredRunning && !stopping)) {
            return;
        }
        knownTotal = groupSize;
        slotByComputer = slots;
        for (Map.Entry<String, String> entry : goodAddresses.entrySet()) {
            deviceManager.recordLastGoodAddress(entry.getKey(), entry.getValue());
        }
        receiverStates.clear();
        receiverStates.putAll(states);
        // updateTargets notifies the listener from the probe executor.  That
        // callback may already have observed a sink reaching getOutputStream;
        // merge the latest in-memory connection bits after the probe batch so
        // “正在供音” is not overwritten back to “连接中”.
        mergeCurrentConnections();
        if (startedByLinkage && reachable > 0 && !anyRequested) {
            // 联动开启的共享：所有在线电脑都已取消请求，自动停止。
            // 全部电脑离线时保持等待，网络恢复后再判定。
            statusDetail = "电脑已关闭共享，自动停止";
            publishStatus();
            stopShared();
            return;
        }
        // A healthy / discovered endpoint is only a candidate until a sink has
        // actually connected. This keeps the original bounded zero-audio stop
        // effective when a PC reports healthy forever but its audio stream is
        // stuck reconnecting.
        if (targets.isEmpty() || connectedCount == 0) {
            long now = android.os.SystemClock.elapsedRealtime();
            if (emptyTargetsSinceElapsed == 0L) {
                emptyTargetsSinceElapsed = now;
            } else if (SharedAudioPolicies.shouldStopForZeroTargets(
                    emptyTargetsSinceElapsed, now)) {
                // MIC-10：组内最后目标离线超过 15s 恢复窗口，停止采集；
                // 用户主动停止不受此路径影响（stopShared 即时生效）。
                statusDetail = "共享组电脑全部离线，已停止供音";
                publishStatus();
                stopShared();
                return;
            }
            if (targets.isEmpty()) {
                statusDetail = incompatible > 0
                        ? "在线电脑需要更新接收端或配置虚拟麦克风"
                        : "正在等待在线电脑";
            }
        } else {
            emptyTargetsSinceElapsed = 0L;
        }
        if (targets.size() > 0 && connectedCount > 0) {
            // 例：“正在向 2/3 台电脑供音 · 3号离线”，逐台说明未接收原因。
            statusDetail = MultiComputerStatus.summarize(receiverStates, slotByComputer);
        } else if (!targets.isEmpty()) {
            statusDetail = "已发现 " + targets.size() + " 台音频接收端，正在连接";
        }
        publishStatus();
        updateNotification();
    }

    /**
     * Distinguishes a fresh, explicit capability failure from a health sample
     * that is stale or incomplete. Unknown health must never create a new sink;
     * it may only keep an identity-matched sink that was already accepted.
     * available=true remains usable even when the older receiver marks it stale.
     */
    private static SharedAudioReadiness sharedAudioReadiness(JSONObject health) {
        JSONArray capabilities = health.optJSONArray("capabilities");
        if (capabilities == null) {
            return SharedAudioReadiness.UNKNOWN;
        }
        boolean supported = false;
        for (int index = 0; index < capabilities.length(); index++) {
            if ("sharedMicrophone".equals(capabilities.optString(index))) {
                supported = true;
                break;
            }
        }
        if (!supported) {
            return SharedAudioReadiness.CAPABILITY_MISSING;
        }
        JSONObject audio = health.optJSONObject("audio");
        if (audio == null) {
            return SharedAudioReadiness.UNKNOWN;
        }
        Object available = audio.opt("available");
        if (Boolean.TRUE.equals(available)) {
            return SharedAudioReadiness.READY;
        }
        if (!Boolean.FALSE.equals(available)) {
            return SharedAudioReadiness.UNKNOWN;
        }
        // Treat available=false as an explicit configuration state only when
        // the receiver also explicitly says the sample is fresh. Missing or
        // malformed stale metadata is not safe evidence for tearing down audio.
        Object stale = audio.opt("stale");
        return Boolean.FALSE.equals(stale)
                ? SharedAudioReadiness.EXPLICITLY_UNAVAILABLE
                : SharedAudioReadiness.UNKNOWN;
    }

    private static int countSharedGroupDevices(
            java.util.List<TargetDeviceManager.Device> devices) {
        int count = 0;
        for (TargetDeviceManager.Device device : devices) {
            if (device.sharedGroup) {
                count++;
            }
        }
        return count;
    }

    private static boolean isSharedRequested(JSONObject health) {
        JSONObject shared = health.optJSONObject("shared");
        return shared != null && shared.optBoolean("requested", false);
    }

    private static boolean hasSharedCapability(JSONObject health) {
        JSONArray capabilities = health.optJSONArray("capabilities");
        if (capabilities == null) {
            return false;
        }
        for (int index = 0; index < capabilities.length(); index++) {
            if ("sharedMicrophone".equals(capabilities.optString(index))) {
                return true;
            }
        }
        return false;
    }

    private void publishStatus() {
        publishStatus(true);
    }

    private void publishStatus(boolean broadcast) {
        boolean active = desiredRunning && broadcaster != null && broadcaster.isRunning();
        snapshot = new Snapshot(active, desiredRunning || stopping, connectedCount, knownTotal, lastLevel,
                statusDetail, receiverStates);
        Runnable listener = inProcessListener;
        if (listener != null) {
            listener.run();
        }
        if (!broadcast) {
            return;
        }
        Intent status = new Intent(ACTION_STATUS).setPackage(getPackageName());
        status.putExtra(EXTRA_RUNNING, active);
        status.putExtra(EXTRA_CONNECTED, connectedCount);
        status.putExtra(EXTRA_TOTAL, knownTotal);
        status.putExtra(EXTRA_LEVEL, lastLevel);
        status.putExtra(EXTRA_DETAIL, statusDetail);
        sendBroadcast(status);
    }

    private void acquireLocks() {
        WifiManager wifi = getApplicationContext().getSystemService(WifiManager.class);
        if (wifi != null) {
            int mode = Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q
                    ? WifiManager.WIFI_MODE_FULL_LOW_LATENCY
                    : WifiManager.WIFI_MODE_FULL_HIGH_PERF;
            wifiLock = wifi.createWifiLock(mode, "PhoneDeckSharedMicrophone");
            wifiLock.setReferenceCounted(false);
            wifiLock.acquire();
        }
        PowerManager power = getSystemService(PowerManager.class);
        if (power != null) {
            wakeLock = power.newWakeLock(
                    PowerManager.PARTIAL_WAKE_LOCK, "PhoneDeck:SharedMicrophone");
            wakeLock.setReferenceCounted(false);
            wakeLock.acquire();
        }
    }

    private void releaseLocks() {
        if (wifiLock != null && wifiLock.isHeld()) {
            wifiLock.release();
        }
        wifiLock = null;
        if (wakeLock != null && wakeLock.isHeld()) {
            wakeLock.release();
        }
        wakeLock = null;
    }

    private void createNotificationChannel() {
        NotificationChannel channel = new NotificationChannel(
                CHANNEL_ID, "共享麦克风", NotificationManager.IMPORTANCE_LOW);
        channel.setDescription("PhoneDeck 在后台向已配对电脑传输手机麦克风");
        getSystemService(NotificationManager.class).createNotificationChannel(channel);
    }

    private Notification buildNotification() {
        Intent openIntent = new Intent(this, MainActivity.class);
        PendingIntent open = PendingIntent.getActivity(this, 0, openIntent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        Intent stopIntent = new Intent(this, PhoneAudioService.class).setAction(ACTION_STOP);
        PendingIntent stop = PendingIntent.getService(this, 1, stopIntent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        String text = notificationText();
        return new Notification.Builder(this, CHANNEL_ID)
                .setSmallIcon(android.R.drawable.ic_btn_speak_now)
                .setContentTitle(getString(R.string.brand_name) + " 共享麦克风已开启")
                .setContentText(text)
                .setContentIntent(open)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .addAction(new Notification.Action.Builder(
                        android.R.drawable.ic_media_pause, "停止共享", stop).build())
                .build();
    }

    private String notificationText() {
        return connectedCount + "/" + knownTotal + " 台电脑正在接收";
    }

    private void updateNotification() {
        if (!desiredRunning) {
            return;
        }
        // Probes and connection callbacks fire every few seconds; re-posting an
        // unchanged notification wakes SystemUI, the launcher and policy services.
        String text = notificationText();
        if (text.equals(postedNotificationText)) {
            return;
        }
        postedNotificationText = text;
        getSystemService(NotificationManager.class).notify(
                NOTIFICATION_ID, buildNotification());
    }

    @Override
    public void onDestroy() {
        serviceDestroyed = true;
        TranscriptRelay.release();
        serviceSessionRevision++;
        desiredRunning = false;
        unregisterWifiLinkObserver();
        handler.removeCallbacks(probeTick);
        clearTargetState();
        if (broadcaster != null) {
            broadcaster.close();
        }
        releaseLocks();
        probeExecutor.shutdownNow();
        snapshot = new Snapshot(false, false, 0, knownTotal, 0,
                "共享麦克风已关闭", java.util.Collections.emptyMap());
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
