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

    static final class Snapshot {
        final boolean running;
        final int connected;
        final int total;
        final int level;
        final String detail;
        final Map<String, String> receiverStates;

        Snapshot(boolean running, int connected, int total, int level, String detail,
                 Map<String, String> receiverStates) {
            this.running = running;
            this.connected = connected;
            this.total = total;
            this.level = level;
            this.detail = detail;
            this.receiverStates = java.util.Collections.unmodifiableMap(
                    new HashMap<>(receiverStates));
        }
    }

    private static volatile Snapshot snapshot = new Snapshot(
            false, 0, 0, 0, "未开启", java.util.Collections.emptyMap());

    static Snapshot getSnapshot() {
        return snapshot;
    }

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService probeExecutor = Executors.newSingleThreadExecutor();
    private final ExecutorService probePool = Executors.newFixedThreadPool(6);
    private TargetDeviceManager deviceManager;
    private SharedAudioBroadcaster broadcaster;
    private WifiManager.WifiLock wifiLock;
    private PowerManager.WakeLock wakeLock;
    private volatile boolean desiredRunning;
    /// 本次共享是由电脑 shared.requested 联动开启（而不是用户在手机上手动开启）；
    /// 只有联动开启的会话才会在电脑全部取消请求后自动停止。
    private volatile boolean startedByLinkage;
    private volatile boolean probeInFlight;
    private volatile int knownTotal;
    private volatile int connectedCount;
    private volatile int lastLevel;
    private volatile String statusDetail = "正在准备";
    private final ConcurrentHashMap<String, String> receiverStates =
            new ConcurrentHashMap<>();

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
        deviceManager = new TargetDeviceManager(this);
        createNotificationChannel();
        broadcaster = new SharedAudioBroadcaster(this, new SharedAudioBroadcaster.Listener() {
            @Override
            public void onLevel(int percent) {
                lastLevel = percent;
                publishStatus();
            }

            @Override
            public void onConnectionsChanged(Map<String, Boolean> connections) {
                int connected = 0;
                for (Map.Entry<String, Boolean> entry : connections.entrySet()) {
                    if (Boolean.TRUE.equals(entry.getValue())) {
                        connected++;
                        receiverStates.put(entry.getKey(), "正在供音");
                    } else if (!"配置错误".equals(receiverStates.get(entry.getKey()))
                            && !"需要更新电脑端".equals(receiverStates.get(entry.getKey()))) {
                        receiverStates.put(entry.getKey(), "连接中");
                    }
                }
                connectedCount = connected;
                statusDetail = connected > 0
                        ? "正在向 " + connected + " 台电脑供音"
                        : "正在等待音频接收端";
                publishStatus();
                updateNotification();
            }

            @Override
            public void onStopped(String reason) {
                if (desiredRunning && reason != null) {
                    statusDetail = "共享麦克风已停止：" + reason;
                }
                if (desiredRunning) {
                    stopShared();
                } else {
                    publishStatus();
                }
            }
        });
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
        knownTotal = deviceManager.list().size();
        try {
            acquireLocks();
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
        desiredRunning = false;
        startedByLinkage = false;
        handler.removeCallbacks(probeTick);
        if (broadcaster != null) {
            broadcaster.stop();
        }
        connectedCount = 0;
        receiverStates.clear();
        lastLevel = 0;
        statusDetail = "共享麦克风已关闭";
        releaseLocks();
        stopForeground(STOP_FOREGROUND_REMOVE);
        publishStatus();
        stopSelf();
    }

    private void probeReceivers() {
        if (probeInFlight) {
            return;
        }
        probeInFlight = true;
        probeExecutor.execute(() -> {
            try {
                Map<String, SharedAudioBroadcaster.Target> targets = new HashMap<>();
                Map<String, String> states = new HashMap<>();
                java.util.List<TargetDeviceManager.Device> devices = deviceManager.list();
                knownTotal = devices.size();
                Map<String, LanDiscoveryClient.DiscoveredComputer> discovered = null;
                int incompatible = 0;
                int reachable = 0;
                boolean anyRequested = false;
                for (TargetDeviceManager.Device device : devices) {
                    states.put(device.computerId, "离线");
                    if (!device.hasLanPairing()) {
                        continue;
                    }
                    PhoneDeckLanClient.ProbeResult result =
                            PhoneDeckLanClient.probe(device, probePool);
                    if (result == null) {
                        if (discovered == null) {
                            discovered = LanDiscoveryClient.discover(this, 650);
                        }
                        LanDiscoveryClient.DiscoveredComputer found =
                                discovered.get(device.computerId);
                        if (found != null && found.port == device.lanPort) {
                            deviceManager.mergeDiscoveredAddress(
                                    device.computerId, found.hostAddress);
                            TargetDeviceManager.Device updated =
                                    deviceManager.find(device.computerId);
                            result = updated == null ? null
                                    : PhoneDeckLanClient.probe(updated, probePool);
                        }
                    }
                    if (result == null) {
                        continue;
                    }
                    reachable++;
                    if (isSharedRequested(result.health)) {
                        anyRequested = true;
                    }
                    deviceManager.recordLastGoodAddress(device.computerId, result.hostAddress);
                    if (isSharedAudioReady(result.health)) {
                        targets.put(device.computerId, new SharedAudioBroadcaster.Target(
                                device.computerId, device.displayName, result.endpoint));
                        states.put(device.computerId, "连接中");
                    } else {
                        incompatible++;
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
                    if (!computerId.isEmpty()) {
                        reachable++;
                        if (isSharedRequested(health)) {
                            anyRequested = true;
                        }
                    }
                    if (!computerId.isEmpty() && !targets.containsKey(computerId)
                            && isSharedAudioReady(health)) {
                        if (!states.containsKey(computerId)) {
                            knownTotal++;
                        }
                        targets.put(computerId, new SharedAudioBroadcaster.Target(
                                computerId,
                                health.optString("displayName", "USB 电脑"),
                                PhoneDeckEndpoint.USB));
                        states.put(computerId, "连接中");
                    } else if (!computerId.isEmpty() && !targets.containsKey(computerId)) {
                        if (!states.containsKey(computerId)) {
                            knownTotal++;
                        }
                        incompatible++;
                        states.put(computerId, hasSharedCapability(health)
                                ? "配置错误" : "需要更新电脑端");
                    }
                } catch (Exception ignored) {
                }

                receiverStates.clear();
                receiverStates.putAll(states);
                broadcaster.updateTargets(targets);
                if (startedByLinkage && reachable > 0 && !anyRequested) {
                    // 联动开启的共享：所有在线电脑都已取消请求，自动停止。
                    // 全部电脑离线时保持等待，网络恢复后再判定。
                    statusDetail = "电脑已关闭共享，自动停止";
                    publishStatus();
                    handler.post(this::stopShared);
                    return;
                }
                if (targets.isEmpty()) {
                    statusDetail = incompatible > 0
                            ? "在线电脑需要更新接收端或配置虚拟麦克风"
                            : "正在等待在线电脑";
                } else if (connectedCount > 0) {
                    statusDetail = "正在向 " + connectedCount + " 台电脑供音";
                } else {
                    statusDetail = "已发现 " + targets.size() + " 台音频接收端";
                }
                publishStatus();
                updateNotification();
            } finally {
                probeInFlight = false;
            }
        });
    }

    private static boolean isSharedAudioReady(JSONObject health) {
        JSONArray capabilities = health.optJSONArray("capabilities");
        boolean supported = false;
        if (capabilities != null) {
            for (int index = 0; index < capabilities.length(); index++) {
                if ("sharedMicrophone".equals(capabilities.optString(index))) {
                    supported = true;
                    break;
                }
            }
        }
        JSONObject audio = health.optJSONObject("audio");
        return supported && audio != null && audio.optBoolean("available", false);
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
        boolean active = desiredRunning && broadcaster != null && broadcaster.isRunning();
        snapshot = new Snapshot(active, connectedCount, knownTotal, lastLevel,
                statusDetail, receiverStates);
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
        String text = connectedCount + "/" + knownTotal + " 台电脑正在接收";
        return new Notification.Builder(this, CHANNEL_ID)
                .setSmallIcon(android.R.drawable.ic_btn_speak_now)
                .setContentTitle("PhoneDeck 共享麦克风已开启")
                .setContentText(text)
                .setContentIntent(open)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .addAction(new Notification.Action.Builder(
                        android.R.drawable.ic_media_pause, "停止共享", stop).build())
                .build();
    }

    private void updateNotification() {
        if (!desiredRunning) {
            return;
        }
        getSystemService(NotificationManager.class).notify(
                NOTIFICATION_ID, buildNotification());
    }

    @Override
    public void onDestroy() {
        desiredRunning = false;
        handler.removeCallbacks(probeTick);
        if (broadcaster != null) {
            broadcaster.close();
        }
        releaseLocks();
        probeExecutor.shutdownNow();
        probePool.shutdownNow();
        snapshot = new Snapshot(false, 0, knownTotal, 0,
                "共享麦克风已关闭", java.util.Collections.emptyMap());
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
