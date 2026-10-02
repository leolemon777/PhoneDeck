package com.codex.phonedeck;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.BroadcastReceiver;
import android.content.ClipData;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.content.res.Configuration;
import android.graphics.Typeface;
import android.graphics.Color;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.NetworkRequest;
import android.net.wifi.WifiManager;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.text.TextUtils;
import android.util.Log;
import android.view.Gravity;
import android.view.DragEvent;
import android.view.HapticFeedbackConstants;
import android.view.MotionEvent;
import android.view.View;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.GridLayout;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.UUID;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class MainActivity extends Activity {
    private static final int REQUEST_BLUETOOTH = 1002;
    private static final int REQUEST_MICROPHONE = 1003;
    private static final int REQUEST_SHARED_MICROPHONE = 1004;
    private static final String SERVER = "http://127.0.0.1:8765";
    private static final String PREFS_NAME = "PhoneDeckSettings";
    private static final String PREF_VOICE_WORK_MODE = "voice_work_mode";
    private static final String PREF_VOICE_MODE = "voice_mode";
    /// M1-A A4：legacy-only 电脑的升级提示 per-computerId 只弹一次（取消也算已提示）。
    private static final String PREF_CREDENTIAL_UPGRADE_PROMPT = "credential_upgrade_prompted_";
    /// M1-A A4：每台电脑的升级 clientId 持久化沿用，重试不再签发全新凭据
    /// （rotate 对同 clientId 只回 already-upgraded；且 per-clientId 限速对本机重试生效）。
    private static final String PREF_CREDENTIAL_UPGRADE_CLIENT_ID = "credential_upgrade_client_id_";
    private static final String WORK_MANAGED = "managed";
    private static final String WORK_SHARED = "shared";
    private static final String MODE_TAP = "tap";
    private static final String MODE_HOLD = "hold";
    private static final long HEALTH_CHECK_INTERVAL_MS = 2_000;
    private static final long VOICE_START_WATCHDOG_MS = 6_000;
    private final ExecutorService actionExecutor = Executors.newSingleThreadExecutor();
    private final ExecutorService voiceExecutor = Executors.newSingleThreadExecutor();
    private final ExecutorService voiceRecoveryExecutor = Executors.newSingleThreadExecutor();
    private final ExecutorService voiceStatusExecutor = Executors.newSingleThreadExecutor();
    private final ExecutorService connectionExecutor = Executors.newSingleThreadExecutor();
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private PhoneDeckTheme theme;
    private String appliedThemeId;
    private TextView statusText;
    private TextView statusDetailText;
    private LinearLayout connectionCard;
    private View statusDot;
    private TextView actionFeedback;
    private TextView microphoneLevel;
    private VoiceModeSwitch voiceModeSwitch;
    private TextView targetTitleText;
    private Button typelessButton;
    private TextView voiceButtonCaption;
    private MicrophoneGlyphDrawable voiceIcon;
    private VoiceLevelView voiceMeter;
    private GridLayout shortcutGrid;
    private LinearLayout targetDeviceRow;
    private LinearLayout targetDockRow;
    private ScrollView shortcutScroll;
    private android.app.Dialog shortcutDialog;
    private android.app.Dialog homeStyleDialog;
    private HomeStyle homeStyle = HomeStyle.CENTER;
    private TextView shortcutPanelFeedback;
    private boolean typelessInFlight;
    private boolean audioStartPending;
    private boolean dictationActive;
    private boolean dictationPaused;
    private boolean holdGestureActive;
    private boolean holdReleasePending;
    private String voiceBusyLabel;
    private Runnable voiceStartWatchdog;
    private String voiceWorkMode = WORK_MANAGED;
    private String voiceMode = MODE_TAP;
    private boolean sharedStartPending;
    private boolean sharedStatusReceiverRegistered;
    private String lastSharedDetail;
    private Map<String, String> lastSharedReceiverStates = java.util.Collections.emptyMap();
    private String currentSessionId;
    private boolean currentSessionManaged;
    private String currentSessionTargetComputerId;
    private PhoneDeckEndpoint currentSessionEndpoint;
    private volatile String intentionalAudioStopSessionId;
    private volatile boolean usbConnected;
    private volatile boolean usbRecoveryFeedbackPending;
    private volatile boolean lanCheckInFlight;
    private volatile boolean managedDictationSupported;
    private volatile EngineMode[] usbTypelessModes =
            new EngineMode[]{new EngineMode("dictation", "听写")};
    private volatile String usbEngineName = "Typeless";
    private String selectedTypelessMode = "dictation";
    private String currentSessionMode;
    private long voiceStartConfirmedAt;
    private boolean voiceStatusInFlight;
    private LinearLayout typelessModeRow;
    private WifiManager.WifiLock wifiLock;
    private boolean keepConnectionAlive = true;
    private String lastFeedbackMessage;
    private int lastFeedbackColor;

    private static final long REPEAT_INTERVAL_MS = 150;
    private enum PostAttemptResult { SUCCESS, RETRYABLE_FAILURE, REJECTED }

    private static final java.util.Set<String> REPEATABLE_KEYS = new java.util.HashSet<>(
            java.util.Arrays.asList("DELETE", "LEFT", "RIGHT", "UP", "DOWN"));
    private Runnable repeatRunnable;
    private ShortcutButtonConfig repeatConfig;
    private ShortcutKeyView repeatSource;
    private boolean gridEditMode;
    private Button gridEditButton;
    private TextView shortcutHintText;
    private volatile String usbForegroundApp;
    private volatile boolean phoneAudioAvailable;
    /// null = 电脑端引擎无可读配置、无法校验麦克风（不阻断启动）。
    private volatile Boolean typelessVirtualCableSelected;
    private volatile int serverProtocolVersion;
    private volatile String targetComputerId;
    private volatile String targetDisplayName = "当前电脑";
    private volatile String usbComputerId;
    private volatile String usbDisplayName;
    private volatile int usbProtocolVersion;
    private final String clientSessionId = UUID.randomUUID().toString();
    private volatile boolean bluetoothConnected;
    private volatile String bluetoothDetail = "等待电脑蓝牙连接";
    private BluetoothTransport bluetoothTransport;
    private AudioStreamer audioStreamer;
    private ShortcutConfigRepository configRepository;
    private TargetDeviceManager targetDeviceManager;
    private final ConcurrentHashMap<String, LanTargetStatus> lanTargets =
            new ConcurrentHashMap<>();
    /// 地址可达但配对令牌/证书被拒（401/403/指纹不一致）的电脑集合；
    /// 与“离线”区分展示，提示用户插一次 USB 即可自动重新配对。
    private final ConcurrentHashMap<String, Boolean> lanPairingRejected =
            new ConcurrentHashMap<>();
    /// 并行探测所有候选地址；死地址短超时快速失败，不互相排队。
    private final ExecutorService lanProbePool = Executors.newFixedThreadPool(6);
    /// 单轮 LAN 探测全部失败时的连续计数，用于探测间隔退避。
    private int lanCheckFailStreak;
    /// 立即探测的最小间隔，避免网络回调风暴。
    private long lastLanCheckAt;
    /// UDP 发现冷却计时（elapsedRealtime）。
    private volatile long lastDiscoveryAt;
    /// 单台设备探测失败后到判离线的宽限期。
    private static final long OFFLINE_GRACE_MS = 6_000;
    private static final long DISCOVERY_COOLDOWN_MS = 10_000;
    private ConnectivityManager.NetworkCallback networkCallback;

    // Microphone activation is owned by explicit actions on the phone.
    private final BroadcastReceiver sharedStatusReceiver = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) {
            if (PhoneAudioService.ACTION_STATUS.equals(intent.getAction()))
                renderSharedAudioStatus(PhoneAudioService.getSnapshot());
        }
    };

    private final Runnable periodicHealthCheck = new Runnable() {
        @Override
        public void run() {
            if (isFinishing() || isDestroyed()) {
                return;
            }
            testConnection();
            testLanConnections();
            mainHandler.postDelayed(this, nextHealthCheckDelayMs());
        }
    };

    // Poll only the managed voice target while speaking. An offline second computer
    // in the general discovery queue must not delay stopping the microphone.
    private final Runnable managedVoiceCheck = new Runnable() {
        @Override public void run() {
            if (isFinishing() || isDestroyed() || !currentSessionManaged
                    || currentSessionId == null || currentSessionEndpoint == null) return;
            if (!voiceStatusInFlight) {
                final String session = currentSessionId;
                final PhoneDeckEndpoint endpoint = currentSessionEndpoint;
                final long probeStartedAt = SystemClock.elapsedRealtime();
                voiceStatusInFlight = true;
                voiceStatusExecutor.execute(() -> {
                    try {
                        JSONObject health = PhoneDeckHttp.getJson(endpoint, "/api/health", 600, 900);
                        RemoteVoiceState remote = RemoteVoiceState.fromHealth(health, probeStartedAt);
                        String computer = health.optString("computerId", null);
                        mainHandler.post(() -> {
                            if (session.equals(currentSessionId))
                                reconcileRemoteVoiceState(endpoint, computer, remote);
                        });
                    } catch (Exception ignored) {
                        // Connection failure is unknown, never evidence of a desktop stop.
                    } finally {
                        mainHandler.post(() -> voiceStatusInFlight = false);
                    }
                });
            }
            mainHandler.postDelayed(this, 500);
        }
    };

    /// 一切正常时 2 秒一轮；连续失败按 4/8/16/30 秒退避并加 ±20% 抖动，
    /// 避免请求风暴与高频耗电；成功后立即恢复正常节奏。
    private long nextHealthCheckDelayMs() {
        long base = HEALTH_CHECK_INTERVAL_MS;
        if (lanCheckFailStreak > 0) {
            long[] backoff = {HEALTH_CHECK_INTERVAL_MS, 4_000, 8_000, 16_000, 30_000};
            base = backoff[Math.min(backoff.length - 1, lanCheckFailStreak)];
        }
        long jitter = (long) (base * 0.2 * Math.random());
        return base + jitter;
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        TranscriptRelay.acquire(this);
        theme = PhoneDeckTheme.load(this);
        appliedThemeId = theme.id;
        theme.applyWindow(this);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);

        configRepository = new ShortcutConfigRepository(this);
        targetDeviceManager = new TargetDeviceManager(this);
        setContentView(createInterface());
        maybeHandlePairingTestHook(getIntent());
        registerSharedAudioStatusReceiver();
        audioStreamer = new AudioStreamer(this, new AudioStreamer.Listener() {
            @Override
            public void onReady(String sessionId) {
                mainHandler.post(() -> onAudioReady(sessionId));
            }

            @Override
            public void onLevel(int percent) {
                mainHandler.post(() -> updateMicrophoneLevel(percent));
            }

            @Override
            public void onStopped(String sessionId, String reason) {
                mainHandler.post(() -> onAudioStopped(sessionId, reason));
            }
        });
        prepareBluetooth();
        registerNetworkCallbacks();
        testConnection();
        testLanConnections();
        mainHandler.postDelayed(periodicHealthCheck, HEALTH_CHECK_INTERVAL_MS);
    }

    @android.annotation.SuppressLint("UnspecifiedRegisterReceiverFlag")
    private void registerSharedAudioStatusReceiver() {
        IntentFilter filter = new IntentFilter(PhoneAudioService.ACTION_STATUS);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(sharedStatusReceiver, filter, Context.RECEIVER_NOT_EXPORTED);
        } else {
            registerReceiver(sharedStatusReceiver, filter);
        }
        sharedStatusReceiverRegistered = true;
    }

    private void reconcileVoiceWorkMode() {
        if (WORK_SHARED.equals(voiceWorkMode)) {
            if (isVoiceStarting() || dictationActive || typelessInFlight) {
                stopOrCancelDictation();
                showActionFeedback("●  已停止手机控制听写；可手动开启共享麦克风", theme.warning);
            }
        } else if (PhoneAudioService.getSnapshot().running) {
            stopSharedMicrophoneService();
        }
    }

    /// Wi-Fi 切换、DHCP 变化、网络恢复时立即重新探测与发现，不需要 USB。
    private void registerNetworkCallbacks() {
        ConnectivityManager manager = (ConnectivityManager) getApplicationContext()
                .getSystemService(Context.CONNECTIVITY_SERVICE);
        if (manager == null || networkCallback != null) {
            return;
        }
        networkCallback = new ConnectivityManager.NetworkCallback() {
            @Override
            public void onAvailable(Network network) {
                requestImmediateLanCheck("网络可用");
            }

            @Override
            public void onLost(Network network) {
                requestImmediateLanCheck("网络变化");
            }
        };
        try {
            manager.registerNetworkCallback(
                    new NetworkRequest.Builder()
                            .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                            .build(),
                    networkCallback);
        } catch (Exception exception) {
            Log.w("PhoneDeckNet", "注册网络回调失败：" + exception.getMessage());
            networkCallback = null;
        }
    }

    private void requestImmediateLanCheck(String reason) {
        mainHandler.post(() -> {
            if (isFinishing() || isDestroyed()) {
                return;
            }
            long now = SystemClock.elapsedRealtime();
            if (now - lastLanCheckAt < 1_000) {
                return;
            }
            Log.i("PhoneDeckNet", "立即重新探测：" + reason);
            lanCheckFailStreak = 0;
            testConnection();
            testLanConnections();
        });
    }

    @Override
    protected void onResume() {
        super.onResume();
        PhoneDeckTheme latestTheme = PhoneDeckTheme.load(this);
        if (!latestTheme.id.equals(appliedThemeId)) {
            // Rebind views only: recreating would destroy active audio/connection owners.
            stopKeyRepeat();
            lastFeedbackColor = lastFeedbackColor == theme.success ? latestTheme.success
                    : lastFeedbackColor == theme.warning ? latestTheme.warning
                    : lastFeedbackColor == theme.danger ? latestTheme.danger : latestTheme.muted;
            theme = latestTheme;
            appliedThemeId = theme.id;
            theme.applyWindow(this);
            setContentView(createInterface());
            applyUiState();
        }
        SharedPreferences preferences = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
        String previousWorkMode = voiceWorkMode;
        voiceWorkMode = WORK_SHARED.equals(
                preferences.getString(PREF_VOICE_WORK_MODE, WORK_MANAGED))
                ? WORK_SHARED : WORK_MANAGED;
        voiceMode = preferences.getString(PREF_VOICE_MODE, MODE_TAP);
        if (!MODE_HOLD.equals(voiceMode)) {
            voiceMode = MODE_TAP;
        }
        // 语音模式随引擎变化；旧键 voice_typeless_mode 迁移到 voice_engine_mode，
        // 具体取值在渲染 chips 时按当前引擎的模式列表校验。
        String storedMode = preferences.getString("voice_engine_mode", null);
        if (storedMode == null) {
            storedMode = preferences.getString("voice_typeless_mode", "dictation");
        }
        selectedTypelessMode = storedMode == null || storedMode.isBlank()
                ? "dictation" : storedMode;
        keepConnectionAlive = preferences.getBoolean("keep_connection_alive", true);
        if (!previousWorkMode.equals(voiceWorkMode)) {
            reconcileVoiceWorkMode();
        } else if (WORK_MANAGED.equals(voiceWorkMode)
                && PhoneAudioService.getSnapshot().running) {
            stopSharedMicrophoneService();
        }
        applyWifiLock();
        requestImmediateLanCheck("App 回到前台");
        if (voiceModeSwitch != null && typelessButton != null) {
            updateVoiceModeInterface();
            refreshTypelessModeChips();
            if (WORK_SHARED.equals(voiceWorkMode)) {
                renderSharedAudioStatus(PhoneAudioService.getSnapshot());
            }
        }
        if (shortcutGrid != null && configRepository != null) {
            refreshShortcutGrid();
        }
    }

    @Override
    public void onConfigurationChanged(Configuration newConfig) {
        super.onConfigurationChanged(newConfig);
        // 旋转不重建 Activity：语音会话、连接状态都在字段里，只重建视图。
        setContentView(createInterface());
        applyUiState();
    }

    private void applyUiState() {
        updateConnectionDisplay();
        refreshShortcutGrid();
        refreshTargetSwitcher();
        if (voiceModeSwitch != null && typelessButton != null) {
            updateVoiceModeInterface();
        }
        if (lastFeedbackMessage != null) {
            showActionFeedback(lastFeedbackMessage, lastFeedbackColor);
        }
        if (microphoneLevel != null) {
            if (WORK_SHARED.equals(voiceWorkMode)) {
                renderSharedAudioStatus(PhoneAudioService.getSnapshot());
            } else if (audioStreamer != null && audioStreamer.isPaused()) {
                microphoneLevel.setText("手机麦克风  Ⅱ 已暂停（未采集声音）");
                microphoneLevel.setTextColor(theme.warning);
            } else if (dictationActive) {
                microphoneLevel.setText("手机麦克风  ·  使用中");
                microphoneLevel.setTextColor(theme.muted);
            } else if (audioStartPending) {
                microphoneLevel.setText("手机麦克风  ◌ 正在连接");
                microphoneLevel.setTextColor(theme.warning);
            } else {
                microphoneLevel.setText("手机麦克风  ○ 已停止");
                microphoneLevel.setTextColor(theme.muted);
            }
        }
        updateVoiceControls();
    }

    private void applyWifiLock() {
        if (keepConnectionAlive) {
            if (wifiLock == null) {
                WifiManager wifiManager = (WifiManager) getApplicationContext()
                        .getSystemService(Context.WIFI_SERVICE);
                if (wifiManager == null) {
                    return;
                }
                int mode = Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q
                        ? WifiManager.WIFI_MODE_FULL_LOW_LATENCY
                        : WifiManager.WIFI_MODE_FULL_HIGH_PERF;
                wifiLock = wifiManager.createWifiLock(mode, "PhoneDeckKeepAlive");
                wifiLock.setReferenceCounted(false);
            }
            if (!wifiLock.isHeld()) {
                wifiLock.acquire();
            }
        } else if (wifiLock != null && wifiLock.isHeld()) {
            wifiLock.release();
        }
    }

    private void releaseWifiLock() {
        if (wifiLock != null && wifiLock.isHeld()) {
            wifiLock.release();
        }
    }

    private View createInterface() {
        if (homeStyleDialog != null) {
            homeStyleDialog.dismiss();
            homeStyleDialog = null;
        }
        homeStyle = HomeStyle.load(this);
        if (shortcutDialog != null) {
            shortcutDialog.dismiss();
            shortcutDialog = null;
        }
        boolean landscape = getResources().getConfiguration().orientation
                == Configuration.ORIENTATION_LANDSCAPE;
        boolean compact = getResources().getConfiguration().screenHeightDp < 740;
        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(theme.background);

        LinearLayout header = new LinearLayout(this);
        header.setOrientation(LinearLayout.VERTICAL);
        header.setPadding(dp(24), dp(12), dp(24), dp(8));
        LinearLayout brandRow = new LinearLayout(this);
        brandRow.setGravity(Gravity.CENTER_VERTICAL);
        TextView brand = text(getString(R.string.brand_name), 24, theme.text, Typeface.BOLD);
        brandRow.addView(brand, new LinearLayout.LayoutParams(0,
                LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        Button appearance = smallButton("界面");
        appearance.setBackground(theme.pressable(this, Color.TRANSPARENT, theme.surfaceRaised, 16));
        appearance.setTextColor(theme.muted);
        appearance.setContentDescription("切换首页 UI，当前：" + homeStyle.title);
        appearance.setOnClickListener(view -> showHomeStylePicker(appearance));
        brandRow.addView(appearance, new LinearLayout.LayoutParams(dp(52), dp(48)));
        Button shortcuts = smallButton("快捷键");
        shortcuts.setBackground(theme.pressable(this, Color.TRANSPARENT, theme.surfaceRaised, 16));
        shortcuts.setTextColor(theme.muted);
        shortcuts.setContentDescription("打开完整快捷键面板");
        shortcuts.setOnClickListener(view -> showShortcutPanel());
        brandRow.addView(shortcuts, new LinearLayout.LayoutParams(dp(64), dp(48)));
        Button settings = smallButton("设置");
        settings.setBackground(theme.pressable(this, Color.TRANSPARENT, theme.surfaceRaised, 16));
        settings.setTextColor(theme.muted);
        settings.setContentDescription("打开设置");
        settings.setOnClickListener(view -> startActivity(new Intent(this, SettingsActivity.class)));
        brandRow.addView(settings, new LinearLayout.LayoutParams(dp(60), dp(48)));
        header.addView(brandRow);

        connectionCard = new LinearLayout(this);
        connectionCard.setGravity(Gravity.CENTER_VERTICAL);
        connectionCard.setPadding(dp(14), dp(4), dp(2), dp(4));
        connectionCard.setBackground(theme.pressable(this, theme.surface,
                theme.surfaceRaised, 16));
        connectionCard.setOnClickListener(view -> showDeviceList());
        connectionCard.setContentDescription("连接电脑，点击查看全部电脑");
        connectionCard.setFocusable(true);
        installTouchFeedback(connectionCard);
        statusDot = new View(this);
        statusDot.setBackground(roundRect(theme.muted, 20));
        connectionCard.addView(statusDot, new LinearLayout.LayoutParams(dp(6), dp(6)));
        LinearLayout connectionCopy = new LinearLayout(this);
        connectionCopy.setOrientation(LinearLayout.VERTICAL);
        LinearLayout.LayoutParams copyParams = new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        copyParams.leftMargin = dp(10);
        connectionCard.addView(connectionCopy, copyParams);
        statusText = text("连接你的电脑", 14, theme.text, Typeface.NORMAL);
        statusText.setMaxLines(1);
        statusText.setEllipsize(TextUtils.TruncateAt.END);
        connectionCopy.addView(statusText);
        statusDetailText = text("正在检测连接…", 11, theme.muted, Typeface.NORMAL);
        statusDetailText.setMaxLines(2);
        statusDetailText.setEllipsize(TextUtils.TruncateAt.END);
        connectionCopy.addView(statusDetailText);
        Button retry = smallButton("↻");
        retry.setBackground(theme.pressable(this, Color.TRANSPARENT, theme.surfaceRaised, 24));
        retry.setTextColor(theme.muted);
        retry.setTextSize(21);
        retry.setContentDescription("重新检测电脑连接");
        retry.setOnClickListener(view -> { startBluetoothTransport(); testConnection(); });
        connectionCard.addView(retry, new LinearLayout.LayoutParams(dp(48), dp(48)));
        header.addView(connectionCard, margins(0, dp(10), 0, 0,
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        // Keep the existing editable grid in a secondary panel. The home screen
        // remains focused on speech without changing any shortcut dispatch logic.
        shortcutScroll = new ScrollView(this);
        shortcutScroll.setVerticalScrollBarEnabled(true);
        LinearLayout shortcutPage = new LinearLayout(this);
        shortcutPage.setOrientation(LinearLayout.VERTICAL);
        shortcutPage.setPadding(dp(16), 0, dp(16), dp(12));
        shortcutScroll.addView(shortcutPage);
        LinearLayout shortcutHeader = new LinearLayout(this);
        shortcutHeader.setGravity(Gravity.CENTER_VERTICAL);
        shortcutHeader.addView(text("快捷键", 18, theme.text, Typeface.BOLD),
                new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        gridEditButton = smallButton(gridEditMode ? "完成" : "编辑布局");
        gridEditButton.setBackgroundColor(Color.TRANSPARENT);
        gridEditButton.setTextColor(theme.muted);
        gridEditButton.setSingleLine(true);
        gridEditButton.setOnClickListener(view -> toggleGridEditMode());
        installTouchFeedback(gridEditButton);
        shortcutHeader.addView(gridEditButton, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, dp(48)));
        shortcutPage.addView(shortcutHeader);
        shortcutHintText = text("点击按钮编辑 · 长按拖动调换位置", 12,
                theme.muted, Typeface.NORMAL);
        shortcutHintText.setVisibility(gridEditMode ? View.VISIBLE : View.GONE);
        shortcutPage.addView(shortcutHintText, marginTop(dp(3)));
        shortcutGrid = new GridLayout(this);
        shortcutGrid.setColumnCount(3);
        shortcutGrid.setUseDefaultMargins(false);
        shortcutPage.addView(shortcutGrid, margins(dp(-4), dp(4), dp(-4), 0,
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        LinearLayout voiceDock = new LinearLayout(this);
        voiceDock.setOrientation(LinearLayout.VERTICAL);
        voiceDock.setGravity(Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        voiceDock.setPadding(dp(24), dp(16), dp(24), dp(20));
        LinearLayout voiceBody = new LinearLayout(this);
        voiceBody.setOrientation(landscape ? LinearLayout.HORIZONTAL : LinearLayout.VERTICAL);
        voiceBody.setGravity(Gravity.CENTER);
        voiceDock.addView(voiceBody, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        int micSize = homeStyle == HomeStyle.PANEL ? (landscape || !compact ? 128 : 104)
                : homeStyle == HomeStyle.DOCK ? (landscape || compact ? 112 : 160)
                : landscape || compact ? 128 : 184;
        FrameLayout voiceRing = new FrameLayout(this);
        voiceRing.setBackground(theme.shape(this,
                theme.mix(theme.background, theme.primaryContainer, 0.65f), (micSize + 28) / 2));
        voiceIcon = new MicrophoneGlyphDrawable(this, theme.onPrimary);
        typelessButton = new RoundVoiceButton(this, voiceIcon);
        typelessButton.setTextColor(theme.onPrimary);
        typelessButton.setAllCaps(false);
        typelessButton.setGravity(Gravity.CENTER);
        typelessButton.setPadding(0, 0, 0, 0);
        typelessButton.setBackground(voiceButtonBackground(theme.primary, theme.primaryPressed));
        typelessButton.setElevation(0);
        typelessButton.setStateListAnimator(null);
        typelessButton.setContentDescription("语音输入");
        installVoiceGesture();
        voiceRing.addView(typelessButton, new FrameLayout.LayoutParams(
                dp(micSize), dp(micSize), Gravity.CENTER));
        voiceBody.addView(voiceRing, new LinearLayout.LayoutParams(dp(micSize + 28), dp(micSize + 28)));

        LinearLayout voiceCopy = new LinearLayout(this);
        voiceCopy.setOrientation(LinearLayout.VERTICAL);
        voiceCopy.setGravity(Gravity.CENTER_HORIZONTAL);
        LinearLayout.LayoutParams voiceCopyParams = landscape
                ? new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
                : new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT,
                        LinearLayout.LayoutParams.WRAP_CONTENT);
        voiceCopyParams.topMargin = landscape ? 0 : dp(compact ? 16 : 24);
        voiceCopyParams.leftMargin = landscape ? dp(18) : 0;
        voiceBody.addView(voiceCopy, voiceCopyParams);
        int captionSize = landscape ? 20
                : homeStyle == HomeStyle.DOCK ? (compact ? 24 : 32)
                : homeStyle == HomeStyle.PANEL ? (compact ? 20 : 24) : 24;
        voiceButtonCaption = text("点击开始说话", captionSize, theme.text, Typeface.NORMAL);
        voiceButtonCaption.setGravity(Gravity.CENTER);
        voiceButtonCaption.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        voiceCopy.addView(voiceButtonCaption);
        microphoneLevel = text("手机麦克风 · 未启动", 12, theme.muted, Typeface.NORMAL);
        microphoneLevel.setGravity(Gravity.CENTER);
        voiceCopy.addView(microphoneLevel, marginTop(dp(7)));
        voiceMeter = new VoiceLevelView(this, theme);
        voiceCopy.addView(voiceMeter, margins(0, dp(10), 0, 0, dp(128), dp(14)));

        voiceModeSwitch = new VoiceModeSwitch(this, theme, this::selectVoiceInputMode);
        voiceModeSwitch.setMode(WORK_SHARED.equals(voiceWorkMode) ? WORK_SHARED : voiceMode, false);
        voiceDock.addView(voiceModeSwitch, margins(0, dp(landscape ? 10 : compact ? 16 : 28), 0, 0,
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        typelessModeRow = new LinearLayout(this);
        typelessModeRow.setGravity(Gravity.CENTER);
        typelessModeRow.setVisibility(View.GONE);
        voiceDock.addView(typelessModeRow, margins(0, dp(6), 0, 0,
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        actionFeedback = text("", 12, theme.muted, Typeface.NORMAL);
        actionFeedback.setGravity(Gravity.CENTER);
        actionFeedback.setPadding(dp(12), dp(8), dp(12), dp(8));
        actionFeedback.setVisibility(View.GONE);
        actionFeedback.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        voiceDock.addView(actionFeedback, margins(0, dp(12), 0, 0,
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        LinearLayout voiceEditRow = new LinearLayout(this);
        voiceEditRow.setGravity(Gravity.CENTER);
        Button goal = voiceEditButton("Goal");
        goal.setContentDescription("发送配置的 Goal 指令");
        goal.setOnClickListener(view -> triggerConfiguredGoal(goal));
        installTouchFeedback(goal);
        voiceEditRow.addView(goal, new LinearLayout.LayoutParams(0, dp(48), 1f));
        Button backspace = voiceEditButton("退格");
        backspace.setContentDescription("退格。点按删除一个字符，长按全部删除");
        backspace.setOnClickListener(view -> triggerVoiceEditAction(
                backspace, "退格", "BACKSPACE", "backspace"));
        backspace.setOnLongClickListener(view -> {
            view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
            triggerDeleteAll(backspace);
            return true;
        });
        installTouchFeedback(backspace);
        LinearLayout.LayoutParams backspaceParams = new LinearLayout.LayoutParams(0, dp(48), 1f);
        backspaceParams.leftMargin = dp(8);
        voiceEditRow.addView(backspace, backspaceParams);
        Button enter = voiceEditButton("回车");
        enter.setContentDescription("向电脑发送回车键");
        enter.setOnClickListener(view -> triggerVoiceEditAction(enter, "回车", "ENTER", "enter"));
        installTouchFeedback(enter);
        LinearLayout.LayoutParams enterParams = new LinearLayout.LayoutParams(0, dp(48), 1f);
        enterParams.leftMargin = dp(8);
        voiceEditRow.addView(enter, enterParams);
        voiceDock.addView(voiceEditRow, margins(0, dp(landscape ? 10 : 24), 0, 0,
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        targetDockRow = new LinearLayout(this);
        targetDockRow.setGravity(Gravity.CENTER_VERTICAL);
        targetTitleText = text("输入到", 12, theme.muted, Typeface.NORMAL);
        targetTitleText.setMinHeight(dp(48));
        targetTitleText.setGravity(Gravity.CENTER_VERTICAL);
        targetTitleText.setOnClickListener(view -> showDeviceList());
        targetTitleText.setContentDescription("查看全部电脑并选择输入目标");
        targetDockRow.addView(targetTitleText);
        HorizontalScrollView targetScroller = new HorizontalScrollView(this);
        targetScroller.setHorizontalScrollBarEnabled(false);
        targetScroller.setFillViewport(true);
        targetDeviceRow = new LinearLayout(this);
        targetDeviceRow.setGravity(Gravity.CENTER_VERTICAL | Gravity.END);
        targetScroller.addView(targetDeviceRow, new HorizontalScrollView.LayoutParams(
                HorizontalScrollView.LayoutParams.MATCH_PARENT,
                HorizontalScrollView.LayoutParams.WRAP_CONTENT));
        LinearLayout.LayoutParams targetParams = new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        targetParams.leftMargin = dp(10);
        targetDockRow.addView(targetScroller, targetParams);
        voiceDock.addView(targetDockRow, margins(0, dp(8), 0, 0,
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        refreshTargetSwitcher();
        refreshShortcutGrid();

        if (homeStyle == HomeStyle.PANEL) {
            // One compact control surface: status above, mic left, editing keys right.
            voiceBody.removeView(voiceCopy);
            voiceDock.removeView(voiceBody);
            voiceDock.removeView(voiceEditRow);
            voiceBody.setOrientation(LinearLayout.HORIZONTAL);
            voiceEditRow.setOrientation(LinearLayout.VERTICAL);
            for (int index = 0; index < voiceEditRow.getChildCount(); index++) {
                LinearLayout.LayoutParams keyParams = new LinearLayout.LayoutParams(
                        LinearLayout.LayoutParams.MATCH_PARENT, dp(48));
                keyParams.topMargin = index == 0 ? 0 : dp(8);
                voiceEditRow.getChildAt(index).setLayoutParams(keyParams);
            }
            voiceBody.addView(voiceEditRow, margins(dp(12), 0, 0, 0,
                    0, LinearLayout.LayoutParams.WRAP_CONTENT));
            ((LinearLayout.LayoutParams) voiceEditRow.getLayoutParams()).weight = 1f;
            LinearLayout panel = new LinearLayout(this);
            panel.setOrientation(LinearLayout.VERTICAL);
            panel.setPadding(dp(16), dp(16), dp(16), dp(16));
            panel.setBackground(theme.shape(this, theme.surface, 28, 1, theme.outline));
            voiceCopy.setGravity(Gravity.START);
            voiceButtonCaption.setGravity(Gravity.START);
            microphoneLevel.setGravity(Gravity.START);
            panel.addView(voiceCopy, new LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
            panel.addView(voiceBody, margins(0, dp(14), 0, 0,
                    LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
            voiceDock.addView(panel, 0, new LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        } else if (homeStyle == HomeStyle.DOCK && !landscape) {
            voiceBody.removeView(voiceRing);
            voiceCopyParams.topMargin = 0;
            voiceCopy.setGravity(Gravity.START);
            voiceButtonCaption.setGravity(Gravity.START);
            microphoneLevel.setGravity(Gravity.START);
        }

        ScrollView voiceScroll = new ScrollView(this);
        voiceScroll.setFillViewport(true);
        voiceScroll.setVerticalScrollBarEnabled(false);
        voiceScroll.addView(voiceDock);
        LinearLayout console = new LinearLayout(this);
        console.setOrientation(landscape ? LinearLayout.HORIZONTAL : LinearLayout.VERTICAL);
        if (landscape) {
            ScrollView headerScroll = new ScrollView(this);
            headerScroll.addView(header);
            console.addView(headerScroll, new LinearLayout.LayoutParams(
                    getResources().getDisplayMetrics().widthPixels * 34 / 100,
                    LinearLayout.LayoutParams.MATCH_PARENT));
            console.addView(voiceScroll, new LinearLayout.LayoutParams(0,
                    LinearLayout.LayoutParams.MATCH_PARENT, 1f));
        } else {
            // Keep the small editing controls reachable while the microphone area
            // can scroll independently on a short screen or with enlarged text.
            LinearLayout footer = new LinearLayout(this);
            footer.setOrientation(LinearLayout.VERTICAL);
            footer.setGravity(Gravity.CENTER_HORIZONTAL);
            footer.setPadding(dp(24), dp(8), dp(24), dp(20));
            View[] footerControls = homeStyle == HomeStyle.PANEL
                    ? new View[] {voiceModeSwitch, typelessModeRow, actionFeedback, targetDockRow}
                    : new View[] {voiceModeSwitch, typelessModeRow, actionFeedback, voiceEditRow, targetDockRow};
            for (View control : footerControls) {
                voiceDock.removeView(control);
                footer.addView(control);
            }
            ((LinearLayout.LayoutParams) voiceModeSwitch.getLayoutParams()).topMargin = 0;
            if (homeStyle != HomeStyle.PANEL) {
                ((LinearLayout.LayoutParams) voiceEditRow.getLayoutParams()).topMargin = dp(16);
            }
            if (homeStyle == HomeStyle.DOCK) {
                LinearLayout.LayoutParams ringParams = new LinearLayout.LayoutParams(
                        dp(micSize + 28), dp(micSize + 28));
                ringParams.topMargin = dp(12);
                footer.addView(voiceRing, footer.indexOfChild(voiceEditRow), ringParams);
            }
            voiceDock.setGravity(homeStyle == HomeStyle.CENTER ? Gravity.CENTER
                    : Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
            voiceDock.setPadding(dp(24), dp(12), dp(24), dp(compact ? 8 : 24));
            console.addView(header);
            console.addView(voiceScroll, new LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));
            console.addView(footer);
        }
        root.addView(console, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));
        return root;
    }

    private void showHomeStylePicker(View source) {
        if (isVoiceInteractionBusy()) {
            showActionFeedback("请先结束语音，再切换首页 UI", theme.warning);
            performResultHaptic(source, false);
            return;
        }
        if (homeStyleDialog != null && homeStyleDialog.isShowing()) return;
        homeStyleDialog = HomeStylePicker.show(this, theme, homeStyle, style -> {
            if (homeStyle == style) return;
            // Audio may have started remotely while the picker was open.
            if (isVoiceInteractionBusy()) {
                showActionFeedback("请先结束语音，再切换首页 UI", theme.warning);
                performResultHaptic(source, false);
                return;
            }
            // Rebind the existing controls; never recreate connection/audio owners.
            style.save(this);
            stopKeyRepeat();
            setContentView(createInterface());
            applyUiState();
        });
    }

    private android.graphics.drawable.Drawable voiceButtonBackground(int color, int pressed) {
        // Large and compact microphone targets keep the same circular shape in every state.
        return pressableRoundRect(color, pressed, 120);
    }

    private void showShortcutPanel() {
        if (shortcutDialog != null && shortcutDialog.isShowing()) return;
        if (shortcutScroll.getParent() instanceof android.view.ViewGroup) {
            ((android.view.ViewGroup) shortcutScroll.getParent()).removeView(shortcutScroll);
        }
        shortcutDialog = new android.app.Dialog(this);
        shortcutDialog.requestWindowFeature(android.view.Window.FEATURE_NO_TITLE);
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(0, dp(12), 0, dp(8));
        shortcutPanelFeedback = text("发送到当前电脑", 12, theme.muted, Typeface.NORMAL);
        shortcutPanelFeedback.setPadding(dp(20), dp(8), dp(20), dp(8));
        shortcutPanelFeedback.setMaxLines(3);
        shortcutPanelFeedback.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        panel.addView(shortcutPanelFeedback);
        panel.addView(shortcutScroll, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));
        Button close = smallButton("返回语音");
        close.setOnClickListener(view -> shortcutDialog.dismiss());
        panel.addView(close, margins(dp(16), dp(8), dp(16), 0,
                LinearLayout.LayoutParams.MATCH_PARENT, dp(48)));
        shortcutDialog.setContentView(panel);
        shortcutDialog.setCanceledOnTouchOutside(true);
        shortcutDialog.setOnDismissListener(dialog -> {
            stopKeyRepeat();
            if (gridEditMode) toggleGridEditMode();
        });
        shortcutDialog.show();
        android.view.Window window = shortcutDialog.getWindow();
        if (window != null) {
            window.setGravity(Gravity.BOTTOM);
            window.setBackgroundDrawable(theme.shape(this, theme.background, 24));
            window.setLayout(android.view.ViewGroup.LayoutParams.MATCH_PARENT,
                    getResources().getDisplayMetrics().heightPixels * 78 / 100);
        }
    }

    private void triggerConfiguredGoal(Button source) {
        if (targetComputerId == null || targetComputerId.isBlank()) {
            showActionFeedback("请先连接电脑，再使用 Goal", theme.muted);
            performResultHaptic(source, false);
            showShortcutFailure(source);
            return;
        }
        for (ShortcutButtonConfig config : configRepository.load()) {
            if ("agentGoal".equals(config.id)) {
                triggerShortcut(source, config);
                return;
            }
        }
        showActionFeedback("找不到 Goal 配置，请在快捷键中检查", theme.warning);
        performResultHaptic(source, false);
        showShortcutFailure(source);
    }

    private void refreshShortcutGrid() {
        shortcutGrid.removeAllViews();
        try {
            for (ShortcutButtonConfig config : configRepository.load()) {
                if (config.visible) {
                    addKey(shortcutGrid, config);
                }
            }
            if (actionFeedback != null) {
                String recoveryNotice = configRepository.consumeRecoveryNotice();
                if (recoveryNotice != null) {
                    showActionFeedback("●  " + recoveryNotice, theme.warning);
                }
            }
        } catch (Exception exception) {
            if (actionFeedback != null) {
                showActionFeedback("✕  无法读取快捷键配置；语音输入仍可使用", theme.danger);
            }
        }
    }

    private void addKey(GridLayout grid, ShortcutButtonConfig config) {
        ShortcutKeyView button = new ShortcutKeyView(this, theme, config);
        boolean deleteAllOnLongPress = isBackspaceKey(config);
        boolean repeatable = isRepeatableKey(config);
        button.setTag(config.id);
        button.setContentDescription(config.label + "，" + config.subtitle()
                + (deleteAllOnLongPress
                ? "。点按删除一个字符，长按全部删除"
                : repeatable ? "。点按发送一次，长按连续发送" : ""));
        button.setOnClickListener(view -> {
            if (gridEditMode) {
                openButtonEditor(config.id);
            } else {
                triggerShortcut(button, config);
            }
        });
        button.setOnLongClickListener(view -> {
            if (gridEditMode) {
                view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
                ClipData data = ClipData.newPlainText("PhoneDeckButtonId", config.id);
                view.startDragAndDrop(data, new View.DragShadowBuilder(view), null, 0);
                return true;
            }
            if (deleteAllOnLongPress) {
                view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
                triggerDeleteAll(button);
                return true;
            }
            if (repeatable) {
                view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
                startKeyRepeat(button, config);
                return true;
            }
            return false;
        });
        button.setOnDragListener((view, event) -> handleGridDrag(view, event));
        if (gridEditMode) {
            button.setAlpha(0.78f);
        }
        installTouchFeedback(button, this::stopKeyRepeat);

        GridLayout.LayoutParams params = new GridLayout.LayoutParams();
        params.width = 0;
        params.height = dp(Math.round(88 * Math.max(1f,
                getResources().getConfiguration().fontScale)));
        params.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
        params.setMargins(dp(4), dp(4), dp(4), dp(4));
        grid.addView(button, params);
    }

    private void toggleGridEditMode() {
        gridEditMode = !gridEditMode;
        stopKeyRepeat();
        if (gridEditButton != null) {
            gridEditButton.setText(gridEditMode ? "完成" : "编辑布局");
            performResultHaptic(gridEditButton, true);
        }
        if (shortcutHintText != null) {
            shortcutHintText.setVisibility(gridEditMode ? View.VISIBLE : View.GONE);
            shortcutHintText.setText(gridEditMode
                    ? "点击按钮编辑 · 长按拖动调换位置"
                    : "发送到当前电脑 · 编辑可调整按钮与顺序");
        }
        if (shortcutGrid != null) {
            for (int i = 0; i < shortcutGrid.getChildCount(); i++) {
                View child = shortcutGrid.getChildAt(i);
                child.setAlpha(gridEditMode ? 0.78f : 1f);
            }
        }
        showActionFeedback(gridEditMode
                ? "✎  编辑模式：点击按钮编辑，长按拖动调位置"
                : "●  已退出编辑模式", gridEditMode ? theme.warning : theme.muted);
    }

    private boolean handleGridDrag(View target, DragEvent event) {
        if (event.getAction() == DragEvent.ACTION_DRAG_STARTED) {
            return event.getClipDescription() != null
                    && "PhoneDeckButtonId".equals(event.getClipDescription().getLabel());
        }
        if (event.getAction() == DragEvent.ACTION_DRAG_ENTERED) {
            target.setAlpha(0.45f);
            return true;
        }
        if (event.getAction() == DragEvent.ACTION_DRAG_EXITED
                || event.getAction() == DragEvent.ACTION_DRAG_ENDED) {
            target.setAlpha(gridEditMode ? 0.78f : 1f);
            return true;
        }
        if (event.getAction() == DragEvent.ACTION_DROP) {
            target.setAlpha(gridEditMode ? 0.78f : 1f);
            if (event.getClipData() == null || event.getClipData().getItemCount() == 0) {
                return false;
            }
            String sourceId = event.getClipData().getItemAt(0).getText().toString();
            String targetId = String.valueOf(target.getTag());
            swapGridButtons(sourceId, targetId);
            return true;
        }
        return true;
    }

    private void swapGridButtons(String sourceId, String targetId) {
        if (sourceId.equals(targetId)) {
            return;
        }
        try {
            java.util.ArrayList<ShortcutButtonConfig> buttons = configRepository.load();
            int sourceIndex = -1;
            int targetIndex = -1;
            for (int i = 0; i < buttons.size(); i++) {
                if (buttons.get(i).id.equals(sourceId)) {
                    sourceIndex = i;
                }
                if (buttons.get(i).id.equals(targetId)) {
                    targetIndex = i;
                }
            }
            if (sourceIndex < 0 || targetIndex < 0) {
                return;
            }
            java.util.Collections.swap(buttons, sourceIndex, targetIndex);
            configRepository.save(buttons);
            refreshShortcutGrid();
            showActionFeedback("✓  已调换位置", theme.success);
        } catch (Exception exception) {
            showActionFeedback("✕  保存按钮顺序失败", theme.danger);
        }
    }

    private void openButtonEditor(String buttonId) {
        Intent intent = new Intent(this, ShortcutEditActivity.class);
        intent.putExtra(ShortcutEditActivity.EXTRA_BUTTON_ID, buttonId);
        startActivity(intent);
    }

    private boolean isRepeatableKey(ShortcutButtonConfig config) {
        if (config.isTextAction() || config.keys == null || config.keys.size() != 1) {
            return false;
        }
        return REPEATABLE_KEYS.contains(config.keys.get(0));
    }

    private boolean isBackspaceKey(ShortcutButtonConfig config) {
        return !config.isTextAction()
                && !config.isMacroAction()
                && config.keys != null
                && config.keys.size() == 1
                && "BACKSPACE".equals(config.keys.get(0));
    }

    private void startKeyRepeat(ShortcutKeyView source, ShortcutButtonConfig config) {
        stopKeyRepeat();
        repeatSource = source;
        repeatConfig = config;
        showActionFeedback("●  连发中：" + config.label + "（松手停止）", theme.warning);
        sendRepeatOnce();
        repeatRunnable = new Runnable() {
            @Override
            public void run() {
                if (repeatConfig == null) {
                    return;
                }
                sendRepeatOnce();
                mainHandler.postDelayed(this, REPEAT_INTERVAL_MS);
            }
        };
        mainHandler.postDelayed(repeatRunnable, REPEAT_INTERVAL_MS);
    }

    private void sendRepeatOnce() {
        ShortcutButtonConfig config = repeatConfig;
        if (config == null) {
            return;
        }
        JSONObject body = new JSONObject();
        try {
            body.put("requestId", UUID.randomUUID().toString());
            if (serverProtocolVersion >= 2 && targetComputerId != null
                    && !targetComputerId.isBlank()) {
                body.put("protocolVersion", 2);
                body.put("sessionId", clientSessionId);
                body.put("targetComputerId", targetComputerId);
                body.put("action", "keyChord");
                org.json.JSONArray keys = new org.json.JSONArray();
                for (String key : config.keys) {
                    keys.put(key);
                }
                body.put("keys", keys);
                body.put("holdMs", config.holdMs);
            } else {
                String legacyAction = legacyActionForKey(config.keys.get(0));
                if (legacyAction == null) {
                    return;
                }
                body.put("action", legacyAction);
            }
        } catch (Exception ignored) {
            return;
        }
        if (repeatSource != null) {
            repeatSource.showSending();
        }
        actionExecutor.execute(() -> {
            try {
                sendCommand(body);
            } catch (Exception ignored) {
                // 连发中单次失败不打断，松手即停。
            }
        });
    }

    private void stopKeyRepeat() {
        repeatConfig = null;
        if (repeatRunnable != null) {
            mainHandler.removeCallbacks(repeatRunnable);
            repeatRunnable = null;
        }
        if (repeatSource != null) {
            repeatSource.showSuccess();
            repeatSource = null;
        }
    }

    private static String legacyActionForKey(String key) {
        switch (key) {
            case "BACKSPACE": return "backspace";
            case "DELETE": return "delete";
            case "LEFT": return "left";
            case "RIGHT": return "right";
            case "UP": return "up";
            case "DOWN": return "down";
            default: return null;
        }
    }

    /// 退格长按只执行一次“全选 + 退格”，避免旧的 150ms 连发产生大量请求。
    /// v2 使用单个受控宏，保证两步固定发送到同一目标；旧协议仍按顺序发送
    /// 两个既有白名单动作，保留 1.4.0 兼容性。
    private void triggerDeleteAll(View source) {
        final JSONObject firstBody = new JSONObject();
        JSONObject fallbackSecondBody = null;
        try {
            if (serverProtocolVersion >= 2 && targetComputerId != null
                    && !targetComputerId.isBlank()) {
                firstBody.put("requestId", UUID.randomUUID().toString());
                firstBody.put("protocolVersion", 2);
                firstBody.put("sessionId", clientSessionId);
                firstBody.put("targetComputerId", targetComputerId);
                firstBody.put("action", "macro");

                org.json.JSONArray steps = new org.json.JSONArray();
                JSONObject selectAll = new JSONObject();
                selectAll.put("type", "keyChord");
                selectAll.put("delayBeforeMs", 0);
                selectAll.put("holdMs", 45);
                selectAll.put("keys", new org.json.JSONArray()
                        .put("CTRL").put("A"));
                steps.put(selectAll);

                JSONObject backspace = new JSONObject();
                backspace.put("type", "keyChord");
                backspace.put("delayBeforeMs", 40);
                backspace.put("holdMs", 45);
                backspace.put("keys", new org.json.JSONArray().put("BACKSPACE"));
                steps.put(backspace);
                firstBody.put("steps", steps);
            } else {
                firstBody.put("requestId", UUID.randomUUID().toString());
                firstBody.put("action", "selectAll");
                fallbackSecondBody = new JSONObject();
                fallbackSecondBody.put("requestId", UUID.randomUUID().toString());
                fallbackSecondBody.put("action", "backspace");
            }
        } catch (Exception exception) {
            showShortcutFailure(source);
            showActionFeedback("✕  全部删除指令生成失败", theme.danger);
            return;
        }

        final JSONObject secondBody = fallbackSecondBody;
        showShortcutSending(source);
        showActionFeedback("●  正在全选并删除…", theme.warning);
        actionExecutor.execute(() -> {
            try {
                String transport = sendCommand(firstBody);
                if (secondBody != null) {
                    Thread.sleep(40);
                    transport = sendCommand(secondBody);
                }
                String confirmedTransport = transport;
                mainHandler.post(() -> {
                    showConnection(confirmedTransport + " 已连接", theme.success);
                    showActionFeedback("✓  已全部删除 · " + confirmedTransport,
                            theme.success);
                    performResultHaptic(source, true);
                    showShortcutSuccess(source);
                });
            } catch (Exception exception) {
                mainHandler.post(() -> {
                    showConnection("发送失败，请连接 USB 或蓝牙", theme.danger);
                    showActionFeedback("✕  电脑未确认全部删除", theme.danger);
                    performResultHaptic(source, false);
                    showShortcutFailure(source);
                });
            }
        });
    }

    private void showShortcutSending(View source) {
        if (source instanceof ShortcutKeyView) {
            ((ShortcutKeyView) source).showSending();
        }
    }

    private void showShortcutSuccess(View source) {
        if (source instanceof ShortcutKeyView) {
            ((ShortcutKeyView) source).showSuccess();
        } else {
            TouchFeedback.result(source, true);
        }
    }

    private void showShortcutFailure(View source) {
        if (source instanceof ShortcutKeyView) {
            ((ShortcutKeyView) source).showFailure();
        } else {
            TouchFeedback.result(source, false);
        }
    }

    private void triggerShortcut(View source, ShortcutButtonConfig config) {
        JSONObject body = new JSONObject();
        try {
            body.put("requestId", UUID.randomUUID().toString());
            if (serverProtocolVersion >= 2 && targetComputerId != null
                    && !targetComputerId.isBlank()) {
                body.put("protocolVersion", 2);
                body.put("sessionId", clientSessionId);
                body.put("targetComputerId", targetComputerId);
                if (config.isTextAction()) {
                    body.put("action", "text");
                    body.put("text", config.textForSend());
                } else if (config.isMacroAction()) {
                    body.put("action", "macro");
                    org.json.JSONArray stepArray = new org.json.JSONArray();
                    for (ShortcutButtonConfig.MacroStep step : config.steps) {
                        stepArray.put(step.toJson());
                    }
                    body.put("steps", stepArray);
                } else {
                    body.put("action", "keyChord");
                    org.json.JSONArray keys = new org.json.JSONArray();
                    for (String key : config.keys) {
                        keys.put(key);
                    }
                    body.put("keys", keys);
                    body.put("holdMs", config.holdMs);
                }
            } else {
                String legacyAction = legacyActionForId(config.id);
                if (legacyAction == null) {
                    showShortcutFailure(source);
                    showActionFeedback("✕  自定义按键需要电脑端升级到 1.5.0", theme.danger);
                    return;
                }
                body.put("action", legacyAction);
            }
        } catch (Exception exception) {
            showShortcutFailure(source);
            showActionFeedback("✕  快捷键配置无效，请进入设置修复", theme.danger);
            return;
        }

        showShortcutSending(source);
        showActionFeedback("●  正在发送：" + config.label + " · " + config.subtitle(),
                theme.warning);
        actionExecutor.execute(() -> {
            try {
                String transport = sendCommand(body);
                mainHandler.post(() -> {
                    showConnection(transport + " 已连接", theme.success);
                    showActionFeedback("✓  已发送：" + config.label + " · " + transport,
                            theme.success);
                    performResultHaptic(source, true);
                    showShortcutSuccess(source);
                });
            } catch (Exception exception) {
                mainHandler.post(() -> {
                    showConnection("发送失败，请连接 USB 或蓝牙", theme.danger);
                    showActionFeedback("✕  电脑未确认快捷键：" + config.label,
                            theme.danger);
                    performResultHaptic(source, false);
                    showShortcutFailure(source);
                });
            }
        });
    }

    private void triggerVoiceEditAction(
            Button source, String label, String key, String legacyAction) {
        JSONObject body = new JSONObject();
        try {
            body.put("requestId", UUID.randomUUID().toString());
            if (serverProtocolVersion >= 2 && targetComputerId != null
                    && !targetComputerId.isBlank()) {
                body.put("protocolVersion", 2);
                body.put("sessionId", clientSessionId);
                body.put("targetComputerId", targetComputerId);
                body.put("action", "keyChord");
                body.put("keys", new org.json.JSONArray().put(key));
                body.put("holdMs", 40);
            } else {
                body.put("action", legacyAction);
            }
        } catch (Exception exception) {
            showActionFeedback("✕  无法创建" + label + "操作", theme.danger);
            performResultHaptic(source, false);
            return;
        }

        showActionFeedback("●  正在发送：" + label, theme.warning);
        actionExecutor.execute(() -> {
            try {
                String transport = sendCommand(body);
                mainHandler.post(() -> {
                    showConnection(transport + " 已连接", theme.success);
                    showActionFeedback("✓  已发送：" + label + " · " + transport,
                            theme.success);
                    performResultHaptic(source, true);
                    TouchFeedback.result(source, true);
                });
            } catch (Exception exception) {
                mainHandler.post(() -> {
                    showConnection("发送失败，请检查当前电脑连接", theme.danger);
                    showActionFeedback("✕  电脑未确认" + label + "操作", theme.danger);
                    performResultHaptic(source, false);
                    TouchFeedback.result(source, false);
                });
            }
        });
    }

    private static String legacyActionForId(String id) {
        switch (id) {
            case "copy": case "paste": case "cut": case "undo": case "redo":
            case "selectAll": case "save": case "altTab": case "screenshot":
            case "enter": case "backspace": case "escape": case "switchInputMethod":
            case "volumeDown": case "volumeMute": case "volumeUp":
            case "left": case "right":
                return id;
            default:
                return null;
        }
    }

    private void refreshTargetSwitcher() {
        if (targetDeviceRow == null || targetDeviceManager == null) {
            return;
        }
        targetDeviceRow.removeAllViews();
        java.util.List<TargetDeviceManager.Device> devices = targetDeviceManager.list();
        targetDockRow.setVisibility(devices.size() > 1 ? View.VISIBLE : View.GONE);
        if (devices.isEmpty()) {
            TextView empty = text("添加电脑  +", 12,
                    theme.muted, Typeface.NORMAL);
            empty.setGravity(Gravity.CENTER);
            empty.setBackground(theme.pressable(this, theme.surfaceRaised,
                    theme.primaryContainer, 12));
            empty.setPadding(dp(14), 0, dp(14), 0);
            empty.setFocusable(true);
            empty.setOnClickListener(view -> showDeviceList());
            targetDeviceRow.addView(empty, new LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.WRAP_CONTENT, dp(48)));
            return;
        }

        String activeComputerId = targetDeviceManager.getActiveComputerId();
        for (TargetDeviceManager.Device device : devices) {
            boolean selected = sameComputer(device.computerId, activeComputerId);
            boolean online = isDeviceOnline(device.computerId);
            boolean pairingRejected = Boolean.TRUE.equals(
                    lanPairingRejected.get(device.computerId));
            String sharedState = WORK_SHARED.equals(voiceWorkMode)
                    ? PhoneAudioService.getSnapshot().receiverStates.get(device.computerId)
                    : null;
            String chipLabel = device.slot + "号";
            if (sharedState != null) {
                chipLabel += "\n" + sharedState.replace("正在", "").replace("电脑端", "");
            } else if (pairingRejected) {
                chipLabel += "\n重配对";
            } else if (!online) {
                chipLabel += "\n离线";
            } else {
                chipLabel += selected ? "\n当前" : "\n在线";
            }
            Button chip = smallButton(chipLabel);
            chip.setAllCaps(false);
            chip.setSingleLine(false);
            chip.setMaxLines(2);
            chip.setEllipsize(TextUtils.TruncateAt.END);
            chip.setTextSize(10);
            chip.setMinWidth(dp(48));
            chip.setMinimumWidth(dp(48));
            chip.setTextColor(selected ? theme.onPrimary : theme.text);
            chip.setBackground(theme.shape(
                    this,
                    selected ? theme.primary : theme.surfaceRaised,
                    12,
                    0,
                    selected ? theme.primaryContainer : theme.outline));
            chip.setAlpha(1f);
            // Offline entries remain actionable for diagnosis and removal.
            chip.setEnabled(true);
            chip.setContentDescription(device.slot + "号电脑 " + device.displayName
                    + (sharedState == null ? "" : "，共享状态" + sharedState)
                    + (pairingRejected ? "，配对已失效，用 USB 连接该电脑一次可自动修复" : "")
                    + (online ? selected ? "，当前快捷键目标" : "，在线" : "，离线")
                    + "，长按删除这台电脑");
            chip.setOnClickListener(view -> selectTargetDevice(device, chip));
            chip.setOnLongClickListener(view -> {
                confirmDeleteTargetDevice(device);
                return true;
            });
            installTouchFeedback(chip);
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                    devices.size() <= 5 ? 0 : dp(64), dp(48), devices.size() <= 5 ? 1f : 0f);
            params.rightMargin = targetDeviceRow.getChildCount() == devices.size() - 1 ? 0 : dp(4);
            targetDeviceRow.addView(chip, params);
        }
    }

    private void showDeviceList() {
        java.util.List<TargetDeviceManager.Device> devices = targetDeviceManager.list();
        if (devices.isEmpty()) {
            new android.app.AlertDialog.Builder(this).setTitle("连接第一台电脑")
                    .setMessage("方式一：在电脑托盘菜单选择「配对新手机…」，用本机扫码配对。\n方式二：用 USB 线连接电脑并允许调试，自动完成首次配对。")
                    .setPositiveButton("扫码配对", (dialog, which) -> launchQrPairingScan())
                    .setNegativeButton("知道了", null).show();
            return;
        }
        String[] labels = new String[devices.size() + 1];
        for (int i = 0; i < devices.size(); i++) {
            TargetDeviceManager.Device device = devices.get(i);
            String state = Boolean.TRUE.equals(lanPairingRejected.get(device.computerId))
                    ? "需重新配对" : isDeviceOnline(device.computerId) ? "在线" : "离线";
            // M1-A A4：legacy-only 设备仅展示「可升级」后缀，提示入口在主界面在线刷新。
            labels[i] = device.slot + "号 · " + device.displayName + "\n" + state
                    + (device.sharedGroup ? " · 共享组" : "")
                    + (sameComputer(device.computerId, targetComputerId) ? " · 当前目标" : "")
                    + (CredentialUpgrader.needsUpgrade(device) ? " · 可升级" : "");
        }
        // M1-B/DEV-03：共享组管理入口（显式集合；新增配对不自动入组）。
        labels[devices.size()] = "⚙ 管理共享组（勾选接收共享麦克风的电脑）";
        new android.app.AlertDialog.Builder(this).setTitle("选择输入电脑")
                .setItems(labels, (dialog, which) -> {
                    if (which == devices.size()) {
                        showSharedGroupDialog();
                        return;
                    }
                    selectTargetDevice(devices.get(which), statusText);
                })
                .setNeutralButton("电脑设置", (dialog, which) -> startActivity(new Intent(this, ComputerSettingsActivity.class)))
                .setPositiveButton("扫码配对新电脑", (dialog, which) -> launchQrPairingScan())
                .setNegativeButton("取消", null).show();
    }

    /// M1-B/DEV-03：共享组多选；切换即持久化，下一轮探测生效（移除即停发该目标流）。
    private void showSharedGroupDialog() {
        java.util.List<TargetDeviceManager.Device> devices = targetDeviceManager.list();
        if (devices.isEmpty()) {
            showActionFeedback("先配对至少一台电脑，再管理共享组", theme.muted);
            return;
        }
        String[] labels = new String[devices.size()];
        boolean[] checked = new boolean[devices.size()];
        for (int i = 0; i < devices.size(); i++) {
            TargetDeviceManager.Device device = devices.get(i);
            labels[i] = device.slot + "号 · " + device.displayName;
            checked[i] = device.sharedGroup;
        }
        new AlertDialog.Builder(this).setTitle("共享组（共享麦克风发送目标）")
                .setMultiChoiceItems(labels, checked, (dialog, which, isChecked) ->
                        targetDeviceManager.setSharedGroup(
                                devices.get(which).computerId, isChecked))
                .setPositiveButton("完成", (dialog, which) -> showActionFeedback(
                        WORK_SHARED.equals(voiceWorkMode)
                                && PhoneAudioService.getSnapshot().running
                                ? "✓ 共享组已更新，下一轮探测生效" : "✓ 共享组已更新",
                        theme.success))
                .setNegativeButton("取消", null).show();
    }

    /// M1-A A3：启动扫码（zxing-android-embedded 的 CaptureActivity 自行处理相机权限与取景）。
    private void launchQrPairingScan() {
        new com.google.zxing.integration.android.IntentIntegrator(this)
                .setDesiredBarcodeFormats(com.google.zxing.integration.android.IntentIntegrator.QR_CODE)
                .setPrompt("对准电脑上的 PhoneDeck 配对二维码")
                .setBeepEnabled(false)
                .setOrientationLocked(true)
                .initiateScan();
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, android.content.Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        com.google.zxing.integration.android.IntentResult scan =
                com.google.zxing.integration.android.IntentIntegrator.parseActivityResult(
                        requestCode, resultCode, data);
        if (scan != null) {
            handleQrPairingScanResult(scan.getContents());
        }
    }

    /// uiPreview 验收通道专用（包名 .preview 门槛，生产 debug/release 不响应）：
    /// --es phonedeck_qr_test_b64 <base64(QR JSON)> [--es phonedeck_qr_address <host>]
    /// 直接注入扫码结果，用于无相机或网络受限环境的自动化协议验收（真 TLS/材料/确认链路不变）。
    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        maybeHandlePairingTestHook(intent);
    }

    private void maybeHandlePairingTestHook(android.content.Intent intent) {
        if (intent == null || !getPackageName().endsWith(".preview")
                || !intent.hasExtra("phonedeck_qr_test_b64")) {
            return;
        }
        String qr;
        try {
            qr = new String(java.util.Base64.getDecoder().decode(
                    intent.getStringExtra("phonedeck_qr_test_b64")),
                    java.nio.charset.StandardCharsets.UTF_8);
        } catch (IllegalArgumentException exception) {
            showActionFeedback("✕  测试注入参数无效", theme.danger);
            return;
        }
        try {
            QrPairingClient.QrPayload payload = QrPairingClient.parseQr(qr);
            String address = intent.getStringExtra("phonedeck_qr_address");
            if (address == null || address.isBlank()) {
                resolveAddressAndPair(payload, null);
            } else {
                runQrPairing(payload, address.trim());
            }
        } catch (IllegalArgumentException exception) {
            showActionFeedback("✕  " + exception.getMessage(), theme.danger);
        }
    }

    private void handleQrPairingScanResult(String qrText) {
        if (qrText == null || qrText.isBlank()) {
            showActionFeedback("✕  未扫码或已取消", theme.muted);
            return;
        }
        QrPairingClient.QrPayload payload;
        try {
            payload = QrPairingClient.parseQr(qrText);
        } catch (IllegalArgumentException exception) {
            showActionFeedback("✕  " + exception.getMessage(), theme.danger);
            return;
        }
        if (targetDeviceManager.find(payload.computerId) != null) {
            new AlertDialog.Builder(this).setTitle("该电脑已配对")
                    .setMessage("「" + payload.displayName + "」已在设备列表中。继续将签发新的独立凭据。")
                    .setPositiveButton("继续", (dialog, which) -> resolveAddressAndPair(payload, null))
                    .setNegativeButton("取消", null).show();
            return;
        }
        resolveAddressAndPair(payload, null);
    }

    /// 发现层匹配 QR 的 computerId；不可用时提示手动输入地址（设计 §4）。
    private void resolveAddressAndPair(QrPairingClient.QrPayload payload, String knownAddress) {
        AlertDialog progress = new AlertDialog.Builder(this)
                .setTitle("扫码配对")
                .setMessage("正在定位「" + payload.displayName + "」…\n请在电脑上点击「确认配对」。")
                .setCancelable(false)
                .show();
        new Thread(() -> {
            java.util.List<String> addresses = knownAddress != null
                    ? java.util.Collections.singletonList(knownAddress)
                    : QrPairingClient.matchDiscovery(this, payload.computerId);
            runOnUiThread(() -> {
                progress.dismiss();
                if (addresses.isEmpty()) {
                    promptManualAddressAndPair(payload);
                    return;
                }
                runQrPairing(payload, addresses.get(0));
            });
        }, "qr-pairing-discovery").start();
    }

    private void promptManualAddressAndPair(QrPairingClient.QrPayload payload) {
        android.widget.EditText input = new android.widget.EditText(this);
        input.setHint("例如 192.168.1.23");
        new AlertDialog.Builder(this).setTitle("输入电脑地址")
                .setMessage("未在局域网发现「" + payload.displayName
                        + "」。请输入电脑的局域网 IP（电脑端状态窗可见）。")
                .setView(input)
                .setPositiveButton("连接", (dialog, which) -> {
                    String address = input.getText().toString().trim();
                    if (!TargetDeviceManager.isAddressCandidateSafe(address)) {
                        showActionFeedback("✕  地址无效", theme.danger);
                        return;
                    }
                    runQrPairing(payload, address);
                })
                .setNegativeButton("取消", null).show();
    }

    /// 提交配对并保存凭据；请求最长挂起 30s 等待电脑本机确认。
    private void runQrPairing(QrPairingClient.QrPayload payload, String host) {
        String clientId = java.util.UUID.randomUUID().toString();
        String label = android.os.Build.MODEL == null ? "Android 手机" : android.os.Build.MODEL;
        AlertDialog waiting = new AlertDialog.Builder(this)
                .setTitle("等待电脑确认")
                .setMessage("已向「" + payload.displayName + "」提交配对。\n请在电脑上点击「确认配对」（30 秒内）。")
                .setCancelable(false)
                .show();
        new Thread(() -> {
            String failure = null;
            org.json.JSONObject issued = null;
            try {
                issued = QrPairingClient.submit(host, payload, clientId, label);
            } catch (Exception exception) {
                failure = exception.getMessage();
            }
            final org.json.JSONObject result = issued;
            final String error = failure;
            runOnUiThread(() -> {
                waiting.dismiss();
                if (error != null || result == null || !result.optBoolean("ok", false)) {
                    showActionFeedback("✕  " + (error == null ? "配对失败" : error), theme.danger);
                    return;
                }
                TargetDeviceManager.Device saved = targetDeviceManager.saveQrPairing(
                        payload.computerId,
                        payload.displayName,
                        "windows",
                        java.util.Collections.singletonList(host),
                        payload.httpsPort,
                        result.optString("clientToken"),
                        result.optString("clientId", clientId),
                        payload.certificateSha256);
                if (saved == null) {
                    showActionFeedback("✕  凭据保存失败", theme.danger);
                    return;
                }
                new Thread(() -> {
                    boolean verified = QrPairingClient.verifyCredential(
                            host, payload.computerId, payload.httpsPort,
                            result.optString("clientToken"),
                            result.optString("clientId", clientId),
                            payload.certificateSha256);
                    runOnUiThread(() -> {
                        new AlertDialog.Builder(this).setTitle(verified ? "配对成功" : "配对完成")
                                .setMessage("「" + payload.displayName + "」已保存独立凭据"
                                        + (verified ? "，并已通过连接验证。" : "。连接验证未通过，稍后可在设置中重试。"))
                                .setPositiveButton("好的", null).show();
                        refreshTargetSwitcher();
                        testConnection();
                    });
                }, "qr-pairing-verify").start();
            });
        }, "qr-pairing-submit").start();
    }

    /// M1-A A4：当前目标 legacy-only 且 LAN 在线时，弹一次「升级为独立凭据」
    /// （设计 §5.2 首连强提示）。取消也算已提示（per-computerId 标志），之后
    /// 仍可经电脑端配对窗口扫码获得独立凭据。语音进行中不打断，留到下一轮。
    private void maybePromptCredentialUpgrade() {
        if (isFinishing() || isDestroyed() || targetComputerId == null) {
            return;
        }
        LanTargetStatus lanStatus = lanTargets.get(targetComputerId);
        if (lanStatus == null) {
            return;
        }
        if (isVoiceStarting() || dictationActive || typelessInFlight
                || audioStreamer != null && audioStreamer.isRunning()
                || WORK_SHARED.equals(voiceWorkMode)
                && PhoneAudioService.getSnapshot().running) {
            return;
        }
        TargetDeviceManager.Device device = targetDeviceManager.find(targetComputerId);
        if (!CredentialUpgrader.needsUpgrade(device)) {
            return;
        }
        SharedPreferences preferences = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
        String promptKey = PREF_CREDENTIAL_UPGRADE_PROMPT + device.computerId;
        if (preferences.getBoolean(promptKey, false)) {
            return;
        }
        preferences.edit().putBoolean(promptKey, true).apply();
        final TargetDeviceManager.Device target = device;
        final PhoneDeckEndpoint endpoint = lanStatus.endpoint;
        new AlertDialog.Builder(this)
                .setTitle("升级为独立凭据")
                .setMessage("「" + device.displayName + "」仍在使用旧版共享令牌。\n\n"
                        + "升级为这台手机专属的独立凭据：\n"
                        + "· 不影响现有使用，无需重新扫码\n"
                        + "· 电脑端可按手机逐个撤销授权")
                .setPositiveButton("升级", (dialog, which) -> runCredentialUpgrade(target, endpoint))
                .setNegativeButton("取消", null)
                .show();
    }

    /// M1-A A4：后台执行 rotate → 新凭据验证 → 保存（设计 §5.2/§5.5）。
    /// 旧共享令牌保留到新凭据验证成功（TargetDeviceManager.applyCredentialUpgrade
    /// 负责先验证后替换）；结果回 UI 线程反馈「已升级为独立凭据」或失败原因。
    private void runCredentialUpgrade(TargetDeviceManager.Device device, PhoneDeckEndpoint endpoint) {
        String host = hostOf(endpoint);
        // 稳定升级 GUID（schema credentialRotateRequest.clientId：“升级后凭据沿用”）：
        // 首次升级生成并持久化，此后重试沿用同一 clientId——服务端对它只回
        // already-upgraded 而不再签发新凭据，避免孤儿凭据累积，也让本机重试
        // 落入 per-clientId 限速桶。凭据丢失的找回路径仍是重新扫码配对（G-1/G-2）。
        SharedPreferences preferences = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
        String upgradeKey = PREF_CREDENTIAL_UPGRADE_CLIENT_ID + device.computerId;
        String clientId = preferences.getString(upgradeKey, null);
        if (clientId == null || clientId.isBlank()) {
            clientId = java.util.UUID.randomUUID().toString();
            preferences.edit().putString(upgradeKey, clientId).apply();
        }
        final String rotateClientId = clientId;
        new Thread(() -> {
            String error = null;
            CredentialUpgrader.RotateResult rotated = null;
            TargetDeviceManager.CredentialUpgradeResult saved = null;
            try {
                rotated = CredentialUpgrader.rotate(endpoint, rotateClientId);
                if (!rotated.alreadyUpgraded) {
                    saved = targetDeviceManager.applyCredentialUpgrade(
                            device.computerId, rotated.clientToken, rotated.clientId, host);
                }
            } catch (Exception exception) {
                error = exception.getMessage() == null ? "升级失败" : exception.getMessage();
            }
            final CredentialUpgrader.RotateResult result = rotated;
            final TargetDeviceManager.CredentialUpgradeResult saveResult = saved;
            final String failure = error;
            runOnUiThread(() -> {
                if (failure != null) {
                    showActionFeedback("✕  升级失败：" + failure, theme.danger);
                    return;
                }
                if (result.alreadyUpgraded) {
                    // rotate 永不重发（G-1）：凭据丢失只能重新扫码配对找回。
                    showActionFeedback("该电脑已为此手机签发过独立凭据，不再重发；"
                            + "如本机凭据丢失，请重新扫码配对", theme.warning);
                    return;
                }
                if (saveResult == null || saveResult.device == null) {
                    showActionFeedback("✕  升级失败：" + (saveResult != null
                            && saveResult.failure != null ? saveResult.failure : "未知原因"),
                            theme.danger);
                    return;
                }
                showActionFeedback("✓  已升级为独立凭据", theme.success);
                refreshTargetSwitcher();
                requestImmediateLanCheck("凭据升级完成");
            });
        }, "credential-upgrade").start();
    }

    /// LAN 端点 baseUrl → 主机地址（供升级验证优先使用 rotate 刚成功的主机）。
    private static String hostOf(PhoneDeckEndpoint endpoint) {
        if (endpoint == null || endpoint.baseUrl == null) {
            return null;
        }
        try {
            return java.net.URI.create(endpoint.baseUrl).getHost();
        } catch (Exception exception) {
            return null;
        }
    }

    private void selectTargetDevice(TargetDeviceManager.Device device, View source) {
        if (isVoiceStarting() || dictationActive || typelessInFlight
                || audioStreamer != null && audioStreamer.isRunning()) {
            showActionFeedback("✕  请先停止当前语音，再切换目标电脑", theme.warning);
            performResultHaptic(source, false);
            return;
        }
        if (!isDeviceOnline(device.computerId)) {
            if (Boolean.TRUE.equals(lanPairingRejected.get(device.computerId))) {
                showActionFeedback("✕  " + device.displayName
                        + " 的配对已失效；用 USB 连接该电脑一次即可自动修复", theme.warning);
            } else {
                showActionFeedback("✕  " + device.displayName + " 当前未连接", theme.danger);
            }
            performResultHaptic(source, false);
            return;
        }
        if (!targetDeviceManager.select(device.computerId)) {
            showActionFeedback("✕  无法选择目标电脑", theme.danger);
            performResultHaptic(source, false);
            return;
        }
        applyStoredTarget();
        String channel = isLanTargetOnline()
                ? "Wi-Fi" : isUsbTargetOnline() ? "USB" : "蓝牙快捷键";
        showActionFeedback("✓  已切换到 " + device.slot + "号电脑 · "
                + device.displayName + " · " + channel, theme.success);
        performResultHaptic(source, true);
    }

    /// 长按目标切换芯片：解除与一台电脑的配对。
    /// 被删电脑再用 USB 连接时会重新自动配对，所以误删可以低成本恢复。
    private void confirmDeleteTargetDevice(TargetDeviceManager.Device device) {
        if (WORK_SHARED.equals(voiceWorkMode)
                && PhoneAudioService.getSnapshot().running) {
            showActionFeedback("✕  请先关闭共享麦克风，再删除电脑", theme.warning);
            return;
        }
        if (isVoiceStarting() || dictationActive || typelessInFlight
                || (audioStreamer != null && audioStreamer.isRunning())) {
            showActionFeedback("✕  请先停止当前语音，再删除电脑", theme.warning);
            return;
        }
        String message = "解除与「" + device.displayName + "」的配对？\n"
                + "之后用 USB 线连接该电脑时会重新自动配对。";
        if (Boolean.TRUE.equals(lanPairingRejected.get(device.computerId))) {
            message = "「" + device.displayName + "」的配对已失效。\n" + message;
        }
        new AlertDialog.Builder(this)
                .setTitle("删除 " + device.slot + "号电脑")
                .setMessage(message)
                .setPositiveButton("删除", (dialog, which) -> deleteTargetDevice(device))
                .setNegativeButton("取消", null)
                .show();
    }

    private void deleteTargetDevice(TargetDeviceManager.Device device) {
        if (!targetDeviceManager.remove(device.computerId)) {
            return;
        }
        lanTargets.remove(device.computerId);
        lanPairingRejected.remove(device.computerId);
        applyStoredTarget();
        showActionFeedback("✓  已删除 " + device.slot + "号电脑 · " + device.displayName,
                theme.success);
    }

    private void applyStoredTarget() {
        TargetDeviceManager.Device active = targetDeviceManager == null
                ? null : targetDeviceManager.find(targetDeviceManager.getActiveComputerId());
        if (active == null) {
            targetComputerId = null;
            targetDisplayName = "当前电脑";
            serverProtocolVersion = 0;
        } else {
            targetComputerId = active.computerId;
            targetDisplayName = active.displayName;
            LanTargetStatus lanStatus = lanTargets.get(active.computerId);
            if (lanStatus != null) {
                serverProtocolVersion = lanStatus.protocolVersion;
            } else if (isUsbTargetOnline()) {
                serverProtocolVersion = usbProtocolVersion;
            } else if (isBluetoothTargetOnline() && bluetoothTransport != null) {
                serverProtocolVersion = bluetoothTransport.getProtocolVersion();
            } else {
                serverProtocolVersion = 0;
            }
        }
        refreshTargetSwitcher();
        refreshTypelessModeChips();
        updateConnectionDisplay();
    }

    private boolean isDeviceOnline(String computerId) {
        return lanTargets.containsKey(computerId)
                || usbConnected && sameComputer(computerId, usbComputerId)
                || bluetoothConnected && bluetoothTransport != null
                && sameComputer(computerId, bluetoothTransport.getComputerId());
    }

    private boolean isLanTargetOnline() {
        return targetComputerId != null && lanTargets.containsKey(targetComputerId);
    }

    private boolean isUsbTargetOnline() {
        return usbConnected && sameComputer(targetComputerId, usbComputerId);
    }

    private boolean isDesktopPreviewChannel() {
        return "com.codex.phonedeck.desktoppreview".equals(getPackageName());
    }

    private boolean isBluetoothTargetOnline() {
        return bluetoothConnected && bluetoothTransport != null
                && bluetoothTransport.isConnected()
                && sameComputer(targetComputerId, bluetoothTransport.getComputerId());
    }

    private static boolean sameComputer(String left, String right) {
        return left != null && right != null && left.equalsIgnoreCase(right);
    }

    private PhoneDeckEndpoint endpointForActiveTarget() {
        LanTargetStatus lanStatus = targetComputerId == null
                ? null : lanTargets.get(targetComputerId);
        if (lanStatus != null) {
            return lanStatus.endpoint;
        }
        return isUsbTargetOnline() ? PhoneDeckEndpoint.USB : null;
    }

    private boolean activePhoneAudioAvailable() {
        LanTargetStatus lanStatus = targetComputerId == null
                ? null : lanTargets.get(targetComputerId);
        return lanStatus != null ? lanStatus.phoneAudioAvailable
                : isUsbTargetOnline() && phoneAudioAvailable;
    }

    /// 当前引擎是否已确认选择虚拟声卡；null = 无法校验（不阻断启动）。
    private Boolean activeTypelessVirtualCableSelected() {
        LanTargetStatus lanStatus = targetComputerId == null
                ? null : lanTargets.get(targetComputerId);
        if (lanStatus != null) {
            return lanStatus.typelessVirtualCableSelected;
        }
        return isUsbTargetOnline() ? typelessVirtualCableSelected : null;
    }

    private String activeEngineName() {
        LanTargetStatus lanStatus = targetComputerId == null
                ? null : lanTargets.get(targetComputerId);
        if (lanStatus != null && lanStatus.engineName != null) {
            return lanStatus.engineName;
        }
        return isUsbTargetOnline() && usbEngineName != null ? usbEngineName
                : isDesktopPreviewChannel() ? "内置识别" : "语音引擎";
    }

    private boolean activeManagedDictationSupported() {
        LanTargetStatus lanStatus = targetComputerId == null
                ? null : lanTargets.get(targetComputerId);
        return lanStatus != null ? lanStatus.managedDictationSupported
                : isUsbTargetOnline() && managedDictationSupported;
    }

    private EngineMode[] activeTypelessModes() {
        LanTargetStatus lanStatus = targetComputerId == null
                ? null : lanTargets.get(targetComputerId);
        if (lanStatus != null) {
            return lanStatus.typelessModes != null && lanStatus.typelessModes.length > 0
                    ? lanStatus.typelessModes
                    : new EngineMode[]{new EngineMode("dictation", "听写")};
        }
        return isUsbTargetOnline() ? usbTypelessModes : new EngineMode[0];
    }

    /// 当前选中模式若不被电脑端引擎支持（如切换了引擎），回退到第一个模式。
    private String effectiveSelectedMode() {
        EngineMode[] modes = activeTypelessModes();
        for (EngineMode mode : modes) {
            if (mode.id.equals(selectedTypelessMode)) {
                return selectedTypelessMode;
            }
        }
        return modes.length > 0 ? modes[0].id : "dictation";
    }

    /// 语音引擎模式：优先读 voiceEngine 块（多引擎协议 v2），
    /// 旧接收端回退 typeless 块的 shortcuts 槽位。返回 null 表示健康信息里没有。
    private static EngineMode[] parseEngineModes(JSONObject health) {
        if (health == null) {
            return null;
        }
        JSONObject engine = health.optJSONObject("voiceEngine");
        if (engine != null) {
            org.json.JSONArray modes = engine.optJSONArray("modes");
            if (modes != null) {
                java.util.ArrayList<EngineMode> configured = new java.util.ArrayList<>();
                for (int index = 0; index < modes.length(); index++) {
                    JSONObject mode = modes.optJSONObject(index);
                    if (mode == null || !mode.optBoolean("configured", false)) {
                        continue;
                    }
                    String id = mode.optString("id", null);
                    if (id == null || id.isBlank()) {
                        continue;
                    }
                    String label = mode.optString("label", null);
                    configured.add(new EngineMode(id,
                            label == null || label.isBlank() ? id : label));
                }
                return configured.toArray(new EngineMode[0]);
            }
        }
        JSONObject typeless = health.optJSONObject("typeless");
        if (typeless == null) {
            return null;
        }
        JSONObject shortcuts = typeless.optJSONObject("shortcuts");
        if (shortcuts == null) {
            // 旧版电脑端没有 shortcuts 字段，只保留听写模式。
            return null;
        }
        java.util.ArrayList<EngineMode> modes = new java.util.ArrayList<>();
        appendLegacyMode(modes, shortcuts, "dictation", "听写");
        appendLegacyMode(modes, shortcuts, "translation", "翻译");
        appendLegacyMode(modes, shortcuts, "ask", "问答");
        return modes.toArray(new EngineMode[0]);
    }

    private static void appendLegacyMode(
            java.util.List<EngineMode> modes, JSONObject shortcuts,
            String id, String label) {
        org.json.JSONArray keys = shortcuts.optJSONArray(id);
        if (keys != null && keys.length() > 0) {
            modes.add(new EngineMode(id, label));
        }
    }

    private static String parseEngineDisplayName(JSONObject health) {
        JSONObject engine = health == null ? null : health.optJSONObject("voiceEngine");
        if (engine != null) {
            String name = engine.optString("displayName", null);
            if (name != null && !name.isBlank()) {
                return name;
            }
        }
        return "Typeless";
    }

    /// 引擎虚拟声卡选择状态：voiceEngine.virtualCableSelected 为 null 时表示
    /// 该引擎无可读配置（旧 typeless 块无此值时按 false 处理）。
    private static Boolean parseVirtualCableSelected(JSONObject health) {
        JSONObject node = health == null ? null : health.optJSONObject("voiceEngine");
        if (node == null) {
            node = health == null ? null : health.optJSONObject("typeless");
        }
        if (node == null || node.isNull("virtualCableSelected")) {
            return null;
        }
        return node.optBoolean("virtualCableSelected", false);
    }

    private static String typelessModeLabel(String mode) {
        if ("translation".equals(mode)) {
            return "翻译";
        }
        if ("ask".equals(mode)) {
            return "问答";
        }
        return "听写";
    }

    private String typelessIdleActionLabel() {
        if ("translation".equals(selectedTypelessMode)) {
            return "点击开始翻译";
        }
        if ("ask".equals(selectedTypelessMode)) {
            return "点击开始提问";
        }
        return "点击开始说话";
    }

    private void refreshTypelessModeChips() {
        if (typelessModeRow == null) {
            return;
        }
        if (WORK_SHARED.equals(voiceWorkMode)) {
            typelessModeRow.setVisibility(View.GONE);
            return;
        }
        EngineMode[] modes = activeTypelessModes();
        boolean usable = activeManagedDictationSupported() && modes.length > 1;
        typelessModeRow.setVisibility(usable ? View.VISIBLE : View.GONE);
        if (!usable) {
            return;
        }
        typelessModeRow.removeAllViews();
        for (EngineMode mode : modes) {
            boolean selected = mode.id.equals(effectiveSelectedMode());
            Button chip = smallButton(mode.label);
            chip.setAllCaps(false);
            chip.setSingleLine(true);
            chip.setTextSize(12);
            chip.setTextColor(selected ? theme.primary : theme.muted);
            chip.setBackground(pressableRoundRect(
                    selected ? theme.feedbackSurface(theme.primary) : Color.TRANSPARENT,
                    theme.surfaceRaised, 12));
            chip.setEnabled(!dictationActive);
            chip.setAlpha(dictationActive ? 0.55f : 1f);
            chip.setContentDescription("切换语音模式：" + mode.label);
            chip.setOnClickListener(view -> {
                if (dictationActive) {
                    return;
                }
                selectedTypelessMode = mode.id;
                getSharedPreferences(PREFS_NAME, MODE_PRIVATE)
                        .edit().putString("voice_engine_mode", mode.id).apply();
                refreshTypelessModeChips();
                updateVoiceControls();
                showActionFeedback("●  语音模式已切换为" + mode.label, theme.muted);
            });
            LinearLayout.LayoutParams chipParams = new LinearLayout.LayoutParams(
                    0, dp(48), 1f);
            chipParams.leftMargin = typelessModeRow.getChildCount() == 0 ? 0 : dp(6);
            installTouchFeedback(chip);
            typelessModeRow.addView(chip, chipParams);
        }
    }

    private void prepareBluetooth() {
        // This standalone channel starts with QR + TLS. Optional legacy transport permissions
        // must not interrupt first use or attach the new app to an unrelated old USB receiver.
        if (isDesktopPreviewChannel()) return;
        bluetoothTransport = new BluetoothTransport(this, (connected, detail) -> mainHandler.post(() -> {
            bluetoothConnected = connected;
            bluetoothDetail = detail;
            if (connected && bluetoothTransport != null
                    && bluetoothTransport.getComputerId() != null) {
                targetDeviceManager.upsert(
                        bluetoothTransport.getComputerId(),
                        bluetoothTransport.getDisplayName(),
                        "windows");
            }
            applyStoredTarget();
        }));

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S
                && checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT)
                != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.BLUETOOTH_CONNECT}, REQUEST_BLUETOOTH);
        } else {
            startBluetoothTransport();
        }
    }

    private void startBluetoothTransport() {
        if (bluetoothTransport != null) {
            bluetoothTransport.start();
            updateConnectionDisplay();
        }
    }

    private void testConnection() {
        if (isDesktopPreviewChannel()) return;
        if (!usbConnected && !bluetoothConnected && lanTargets.isEmpty()) {
            showConnection("正在检测电脑端…", theme.muted);
        }
        connectionExecutor.execute(() -> {
            HttpURLConnection connection = null;
            final long probeStartedAt = SystemClock.elapsedRealtime();
            try {
                connection = (HttpURLConnection) new URL(SERVER + "/api/health").openConnection();
                connection.setConnectTimeout(1200);
                connection.setReadTimeout(1200);
                connection.setRequestMethod("GET");
                int response = connection.getResponseCode();
                if (response == 200) {
                    JSONObject health = readJsonResponse(connection);
                    boolean supportsManagedDictation = false;
                    boolean supportsSecureLan = false;
                    org.json.JSONArray capabilities = health.optJSONArray("capabilities");
                    if (capabilities != null) {
                        for (int index = 0; index < capabilities.length(); index++) {
                            if ("managedDictation".equals(capabilities.optString(index))) {
                                supportsManagedDictation = true;
                            }
                            if ("secureLan".equals(capabilities.optString(index))) {
                                supportsSecureLan = true;
                            }
                        }
                    }
                    managedDictationSupported = supportsManagedDictation;
                    usbProtocolVersion = health.optInt("protocolVersion", 0);
                    JSONObject audio = health.optJSONObject("audio");
                    phoneAudioAvailable = audio != null
                            && audio.optBoolean("available", false);
                    RemoteVoiceState remoteVoiceState =
                            RemoteVoiceState.fromHealth(health, probeStartedAt);
                    typelessVirtualCableSelected = parseVirtualCableSelected(health);
                    usbEngineName = parseEngineDisplayName(health);
                    String healthForegroundApp = health.optString("foregroundApp", null);
                    usbForegroundApp = healthForegroundApp == null
                            || healthForegroundApp.isBlank() ? null : healthForegroundApp;
                    EngineMode[] engineModes = parseEngineModes(health);
                    if (engineModes != null) {
                        usbTypelessModes = engineModes;
                    }
                    String healthComputerId = health.optString("computerId", null);
                    if (healthComputerId != null && !healthComputerId.isBlank()) {
                        usbComputerId = healthComputerId;
                    }
                    String healthDisplayName = health.optString("displayName", null);
                    if (healthDisplayName != null && !healthDisplayName.isBlank()) {
                        usbDisplayName = healthDisplayName;
                    }
                    String healthPlatform = health.optString("platform", "windows");
                    TargetDeviceManager.Device existingDevice =
                            targetDeviceManager.find(healthComputerId);
                    boolean lanPairingNeedsRefresh = existingDevice == null
                            || !existingDevice.hasLanPairing()
                            || !lanTargets.containsKey(healthComputerId);
                    if (supportsSecureLan && lanPairingNeedsRefresh) {
                        try {
                            PhoneDeckLanClient.pairOverUsb(targetDeviceManager);
                        } catch (Exception ignored) {
                            // USB 主功能继续可用；配对失败会在界面保持 USB 状态。
                        }
                    }
                    boolean recoveredAfterVoiceDisconnect = usbRecoveryFeedbackPending;
                    usbRecoveryFeedbackPending = false;
                    usbConnected = true;
                    String activeComputerId = targetDeviceManager.getActiveComputerId();
                    if (activeComputerId == null
                            || sameComputer(healthComputerId, activeComputerId)) {
                        // Layout is owned by the phone; a PC must not overwrite it.
                    }
                    mainHandler.post(() -> {
                        targetDeviceManager.upsert(
                                healthComputerId,
                                healthDisplayName,
                                healthPlatform);
                        applyStoredTarget();
                        reconcileRemoteVoiceState(
                                PhoneDeckEndpoint.USB,
                                healthComputerId,
                                remoteVoiceState);
                        maybeFollowUpdateRequest(healthComputerId, health);
                        if (recoveredAfterVoiceDisconnect
                                && !audioStartPending && !dictationActive) {
                            showActionFeedback("✓  USB 已恢复，可以继续使用", theme.success);
                            microphoneLevel.setText("手机麦克风  ○ 已停止");
                            microphoneLevel.setTextColor(theme.muted);
                        }
                    });
                } else {
                    throw new IllegalStateException("HTTP " + response);
                }
            } catch (Exception exception) {
                boolean wasConnected = usbConnected;
                usbConnected = false;
                managedDictationSupported = false;
                phoneAudioAvailable = false;
                typelessVirtualCableSelected = null;
                usbEngineName = "Typeless";
                usbForegroundApp = null;
                usbTypelessModes = new EngineMode[]{new EngineMode("dictation", "听写")};
                mainHandler.post(() -> {
                    applyStoredTarget();
                    if (wasConnected) {
                        handleUsbConnectionLost();
                    }
                });
            } finally {
                if (connection != null) {
                    connection.disconnect();
                }
            }
        });
    }

    private void testLanConnections() {
        if (lanCheckInFlight) {
            return;
        }
        lanCheckInFlight = true;
        lastLanCheckAt = SystemClock.elapsedRealtime();
        connectionExecutor.execute(() -> {
            try {
                ConcurrentHashMap<String, LanTargetStatus> next =
                        new ConcurrentHashMap<>();
                // 防抖：上一轮在线、本轮失败但仍在宽限期内的设备保持在线，
                // 不再出现“Wi-Fi 在线/离线”来回跳动、按钮变灰。
                for (java.util.Map.Entry<String, LanTargetStatus> entry
                        : lanTargets.entrySet()) {
                    if (SystemClock.elapsedRealtime() - entry.getValue().lastOnlineAt
                            < OFFLINE_GRACE_MS) {
                        next.put(entry.getKey(), entry.getValue());
                    }
                }
                boolean anyPaired = false;
                boolean anySuccess = false;
                ConcurrentHashMap<String, Boolean> rejectedNext = new ConcurrentHashMap<>();
                for (TargetDeviceManager.Device device : targetDeviceManager.list()) {
                    if (lanProbePool.isShutdown() || Thread.currentThread().isInterrupted()) {
                        return; // Activity was destroyed during a font/orientation change.
                    }
                    if (!device.hasLanPairing()) {
                        continue;
                    }
                    anyPaired = true;
                    final long probeStartedAt = SystemClock.elapsedRealtime();
                    PhoneDeckLanClient.ProbeOutcome outcome =
                            PhoneDeckLanClient.probe(device, lanProbePool);
                    PhoneDeckLanClient.ProbeResult result = outcome.result;
                    if (result == null) {
                        // 缓存地址全部失败：触发一次 UDP 自动发现（带冷却），
                        // 把新地址并入候选后重试；全程不需要重新插 USB。
                        result = probeWithDiscovery(device);
                    }
                    if (result == null) {
                        if (outcome.pairingRejected) {
                            rejectedNext.put(device.computerId, Boolean.TRUE);
                        }
                        continue;
                    }
                    anySuccess = true;
                    if (targetDeviceManager.recordLastGoodAddress(
                            device.computerId, result.hostAddress)) {
                        Log.i("PhoneDeckNet", "last-good 地址更新："
                                + device.displayName + " → " + result.hostAddress);
                    }
                    JSONObject health = result.health;
                    if (sameComputer(device.computerId,
                            targetDeviceManager.getActiveComputerId())) {
                        // Keep the phone layout when moving between computers.
                    }
                    boolean supportsManagedDictation = false;
                    org.json.JSONArray capabilities = health.optJSONArray("capabilities");
                    if (capabilities != null) {
                        for (int index = 0; index < capabilities.length(); index++) {
                            if ("managedDictation".equals(capabilities.optString(index))) {
                                supportsManagedDictation = true;
                                break;
                            }
                        }
                    }
                    JSONObject audio = health.optJSONObject("audio");
                    RemoteVoiceState remoteVoiceState =
                            RemoteVoiceState.fromHealth(health, probeStartedAt);
                    String lanForegroundApp = health.optString("foregroundApp", null);
                    next.put(device.computerId, new LanTargetStatus(
                            result.endpoint,
                            health.optInt("protocolVersion", 0),
                            supportsManagedDictation,
                            audio != null && audio.optBoolean("available", false),
                            parseVirtualCableSelected(health),
                            parseEngineModes(health),
                            parseEngineDisplayName(health),
                            lanForegroundApp == null || lanForegroundApp.isBlank()
                                    ? null : lanForegroundApp));
                    PhoneDeckEndpoint healthEndpoint = result.endpoint;
                    String healthComputerId = device.computerId;
                    mainHandler.post(() -> {
                        reconcileRemoteVoiceState(
                                healthEndpoint, healthComputerId, remoteVoiceState);
                        maybeFollowUpdateRequest(healthComputerId, health);
                    });
                }
                lanCheckFailStreak = anyPaired && !anySuccess
                        ? lanCheckFailStreak + 1 : 0;
                lanTargets.clear();
                lanTargets.putAll(next);
                lanPairingRejected.clear();
                lanPairingRejected.putAll(rejectedNext);
                mainHandler.post(this::applyStoredTarget);
            } catch (java.util.concurrent.RejectedExecutionException exception) {
                // onDestroy may shut the pool down between the check and submission.
                // Cancel this obsolete screen's probe without crashing the replacement.
                if (!lanProbePool.isShutdown()) {
                    throw exception;
                }
            } finally {
                lanCheckInFlight = false;
            }
        });
    }

    /// 手机只反向同步自己创建的 managedDictation 会话。电脑端独立启动语音引擎
    /// 不会触发手机录音；但当前会话已由电脑完成时，可靠健康快照会让手机
    /// 停止 AudioRecord、关闭 PCM 流并清理本地按钮状态。
    private void reconcileRemoteVoiceState(
            PhoneDeckEndpoint observedEndpoint,
            String observedComputerId,
            RemoteVoiceState remote) {
        if (!currentSessionManaged || currentSessionId == null
                || currentSessionTargetComputerId == null || currentSessionEndpoint == null
                || currentSessionId.equals(intentionalAudioStopSessionId)) return;

        // Ignore other computers/transports. A stop receipt must name this session;
        // legacy probes must be fresh enough to follow its start acknowledgement.
        if (remote == null
                || !sameComputer(currentSessionTargetComputerId, observedComputerId)
                || !sameSessionTransport(currentSessionEndpoint, observedEndpoint)
                || !RemoteStopPolicy.shouldStop(currentSessionId, remote.stopRequestedSessionId,
                        voiceStartConfirmedAt, remote.probeStartedAt, remote.sampleAgeMs,
                        remote.stale, remote.dictationActive, remote.sessionId, remote.capturing)) return;

        String sessionId = currentSessionId;
        String sessionTargetComputerId = currentSessionTargetComputerId;
        PhoneDeckEndpoint sessionEndpoint = currentSessionEndpoint;
        intentionalAudioStopSessionId = sessionId;
        if (audioStreamer != null) {
            audioStreamer.stop();
        }
        clearVoiceSessionState();
        microphoneLevel.setText("手机麦克风  ○ 已停止");
        microphoneLevel.setTextColor(theme.muted);
        if (voiceMeter != null) {
            voiceMeter.setVoiceState(VoiceLevelView.IDLE);
        }
        showActionFeedback("✓  电脑端已停止，手机已同步停止", theme.success);
        performResultHaptic(typelessButton, true);
        flashResult(typelessButton, theme.success);
        bestEffortStopManagedDictation(
                sessionId, sessionTargetComputerId, sessionEndpoint);
    }

    private static boolean sameSessionTransport(
            PhoneDeckEndpoint expected, PhoneDeckEndpoint observed) {
        if (expected == null || observed == null) {
            return false;
        }
        if (expected == PhoneDeckEndpoint.USB || observed == PhoneDeckEndpoint.USB) {
            return expected == PhoneDeckEndpoint.USB && observed == PhoneDeckEndpoint.USB;
        }
        return expected.isLan() && observed.isLan();
    }

    /// UDP 广播发现（10 秒冷却）。发现结果只并入候选地址；
    /// 建立连接仍必须通过 HTTPS + 配对令牌 + 证书固定验证。
    private PhoneDeckLanClient.ProbeResult probeWithDiscovery(
            TargetDeviceManager.Device device) {
        long now = SystemClock.elapsedRealtime();
        if (now - lastDiscoveryAt < DISCOVERY_COOLDOWN_MS) {
            return null;
        }
        lastDiscoveryAt = now;
        java.util.Map<String, LanDiscoveryClient.DiscoveredComputer> found =
                LanDiscoveryClient.discover(this, 700);
        LanDiscoveryClient.DiscoveredComputer discovered = found.get(device.computerId);
        if (discovered == null
                || discovered.port != device.lanPort
                || !targetDeviceManager.mergeDiscoveredAddress(
                        device.computerId, discovered.hostAddress)) {
            return null;
        }
        Log.i("PhoneDeckNet", "UDP 发现新地址："
                + device.displayName + " → " + discovered.hostAddress);
        TargetDeviceManager.Device updated = targetDeviceManager.find(device.computerId);
        return updated == null
                ? null : PhoneDeckLanClient.probe(updated, lanProbePool).result;
    }

    private static JSONObject readJsonResponse(HttpURLConnection connection) throws Exception {
        StringBuilder json = new StringBuilder();
        try (BufferedReader reader = new BufferedReader(new InputStreamReader(
                connection.getInputStream(), StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) {
                json.append(line);
            }
        }
        return new JSONObject(json.toString());
    }

    private void handleUsbConnectionLost() {
        if (currentSessionEndpoint != PhoneDeckEndpoint.USB) {
            return;
        }
        if (!audioStartPending && !dictationActive && !typelessInFlight
                && (audioStreamer == null || !audioStreamer.isRunning())) {
            return;
        }
        usbRecoveryFeedbackPending = true;
        intentionalAudioStopSessionId = currentSessionId;
        if (audioStreamer != null) {
            audioStreamer.stop();
        }
        clearVoiceSessionState();
        microphoneLevel.setText("手机麦克风  ✕ USB 已断开");
        microphoneLevel.setTextColor(theme.danger);
        showActionFeedback("✕  USB 已断开；电脑端会自动尝试复位语音引擎",
                theme.danger);
    }

    private void toggleTypelessWithPhoneMic() {
        if (isVoiceStarting() || dictationActive) {
            stopOrCancelDictation();
        } else if (!typelessInFlight) {
            beginPhoneDictation();
        }
    }

    private boolean isVoiceStarting() {
        return !dictationActive && (audioStartPending || typelessInFlight);
    }

    private void stopOrCancelDictation() {
        if (isVoiceStarting()) {
            cancelPendingDictation();
        } else if (dictationActive) {
            stopPhoneDictation();
        } else {
            showActionFeedback("●  当前没有正在进行的听写", theme.muted);
        }
    }

    private void cancelPendingDictation() {
        String sessionId = currentSessionId;
        boolean managed = currentSessionManaged;
        String sessionTargetComputerId = currentSessionTargetComputerId;
        PhoneDeckEndpoint sessionEndpoint = currentSessionEndpoint;
        intentionalAudioStopSessionId = sessionId;
        if (audioStreamer != null) {
            audioStreamer.stop();
        }
        clearVoiceSessionState();
        microphoneLevel.setText("手机麦克风  ○ 已取消");
        microphoneLevel.setTextColor(theme.muted);
        showActionFeedback("✓  已立即取消语音启动", theme.muted);
        performResultHaptic(typelessButton, true);
        if (managed && sessionId != null) {
            bestEffortStopManagedDictation(
                    sessionId, sessionTargetComputerId, sessionEndpoint);
        }
    }

    private void bestEffortStopManagedDictation(
            String sessionId,
            String sessionTargetComputerId,
            PhoneDeckEndpoint sessionEndpoint) {
        voiceRecoveryExecutor.execute(() -> {
            try {
                JSONObject body = new JSONObject();
                body.put("protocolVersion", 2);
                body.put("sessionId", sessionId);
                body.put("requestId", UUID.randomUUID().toString());
                body.put("targetComputerId", sessionTargetComputerId);
                postEndpointWithRetry(
                        sessionEndpoint, "/api/dictation/stop", body, 2);
            } catch (Exception ignored) {
                // 本地音频已经停止；电脑端也会在音频断流时复位 managed 会话。
            }
        });
    }

    private void installVoiceGesture() {
        typelessButton.setSoundEffectsEnabled(true);
        typelessButton.setHapticFeedbackEnabled(true);
        typelessButton.setOnClickListener(view -> {
            if (WORK_SHARED.equals(voiceWorkMode)) {
                toggleSharedMicrophone();
            } else if (MODE_TAP.equals(voiceMode)) {
                toggleTypelessWithPhoneMic();
            }
        });
        typelessButton.setOnTouchListener((view, event) -> {
            int action = event.getActionMasked();
            if (action == MotionEvent.ACTION_DOWN) {
                if (!view.isEnabled()) {
                    return true;
                }
                TouchFeedback.press(view, 0.93f, true);
                if (WORK_MANAGED.equals(voiceWorkMode) && MODE_HOLD.equals(voiceMode)) {
                    // Consuming the hold gesture bypasses Button's own pressed/ripple state.
                    view.drawableHotspotChanged(event.getX(), event.getY());
                    view.setPressed(true);
                    view.getParent().requestDisallowInterceptTouchEvent(true);
                    holdGestureActive = true;
                    holdReleasePending = false;
                    if (!dictationActive && !audioStartPending && !typelessInFlight) {
                        beginPhoneDictation();
                    }
                    return true;
                }
            } else if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_CANCEL) {
                TouchFeedback.release(view);
                if (WORK_MANAGED.equals(voiceWorkMode) && MODE_HOLD.equals(voiceMode)) {
                    view.setPressed(false);
                    view.getParent().requestDisallowInterceptTouchEvent(false);
                    boolean wasHolding = holdGestureActive;
                    holdGestureActive = false;
                    if (wasHolding) {
                        TouchFeedback.selection(view);
                        if (dictationActive && !typelessInFlight) {
                            stopPhoneDictation();
                        } else if (audioStartPending || typelessInFlight) {
                            holdReleasePending = true;
                            showActionFeedback("●  已松开，连接完成后会自动结束", theme.warning);
                        }
                    }
                    if (action == MotionEvent.ACTION_UP) view.performClick();
                    return true;
                }
            }
            return false;
        });
    }

    private void selectVoiceInputMode(String mode, View source) {
        if (!MODE_TAP.equals(mode) && !MODE_HOLD.equals(mode) && !WORK_SHARED.equals(mode)) return;
        String currentMode = WORK_SHARED.equals(voiceWorkMode) ? WORK_SHARED : voiceMode;
        if (currentMode.equals(mode)) {
            TouchFeedback.selection(source);
            return;
        }
        if (isVoiceInteractionBusy()) {
            showActionFeedback("请先结束语音，再切换说话方式", theme.warning);
            performResultHaptic(source, false);
            TouchFeedback.result(source, false);
            return;
        }
        voiceWorkMode = WORK_SHARED.equals(mode) ? WORK_SHARED : WORK_MANAGED;
        if (!WORK_SHARED.equals(mode)) voiceMode = mode;
        getSharedPreferences(PREFS_NAME, MODE_PRIVATE).edit()
                .putString(PREF_VOICE_WORK_MODE, voiceWorkMode)
                .putString(PREF_VOICE_MODE, voiceMode).apply();
        lastFeedbackMessage = null;
        actionFeedback.setVisibility(View.GONE);
        lastSharedDetail = null;
        microphoneLevel.setText(WORK_SHARED.equals(mode)
                ? "手机麦克风 · 共享未开启" : "手机麦克风 · 未启动");
        microphoneLevel.setTextColor(theme.muted);
        voiceModeSwitch.setMode(mode, true);
        updateVoiceModeInterface();
        refreshTargetSwitcher();
        TouchFeedback.selection(source);
    }

    private boolean isVoiceInteractionBusy() {
        return isVoiceStarting() || dictationActive || typelessInFlight || holdGestureActive
                || sharedStartPending || currentSessionId != null
                || PhoneAudioService.getSnapshot().busy
                || (audioStreamer != null && audioStreamer.isRunning());
    }

    private void updateVoiceModeInterface() {
        voiceModeSwitch.setMode(WORK_SHARED.equals(voiceWorkMode) ? WORK_SHARED : voiceMode, false);
        if (WORK_SHARED.equals(voiceWorkMode)) {
            if (targetTitleText != null) {
                targetTitleText.setText("快捷键到");
            }
            typelessButton.setContentDescription("开启或关闭共享麦克风");
            updateVoiceControls();
            return;
        }
        boolean holdMode = MODE_HOLD.equals(voiceMode);
        if (targetTitleText != null) {
            targetTitleText.setText("输入到");
        }
        typelessButton.setContentDescription(holdMode
                ? "按住开始语音输入，松开结束"
                : "点击开始语音输入；再次点击同一按钮停止");
        updateVoiceControls();
    }

    private String voiceButtonLabel() {
        if (WORK_SHARED.equals(voiceWorkMode)) {
            return PhoneAudioService.getSnapshot().running
                    ? "关闭共享麦克风" : "开启共享麦克风";
        }
        if (MODE_HOLD.equals(voiceMode)) {
            if (voiceBusyLabel != null) {
                return voiceBusyLabel;
            }
            String holdAction = "translation".equals(selectedTypelessMode) ? "翻译"
                    : "ask".equals(selectedTypelessMode) ? "提问" : "说话";
            return dictationActive ? "松开即可结束" : "按住" + holdAction;
        }
        if (dictationActive && typelessInFlight) {
            return "正在停止…";
        }
        if (isVoiceStarting()) {
            return "取消启动";
        }
        if (dictationActive) {
            return "停止";
        }
        if (voiceBusyLabel != null) {
            return voiceBusyLabel;
        }
        return typelessIdleActionLabel();
    }

    private void updateVoiceControls() {
        if (typelessButton == null) {
            return;
        }
        voiceButtonCaption.setText(voiceButtonLabel());
        refreshTypelessModeChips();
        if (WORK_SHARED.equals(voiceWorkMode)) {
            PhoneAudioService.Snapshot state = PhoneAudioService.getSnapshot();
            boolean stopState = state.running;
            int stopFill = theme.live;
            int stopInk = theme.onLive;
            typelessButton.setBackground(stopState
                    ? voiceButtonBackground(stopFill,
                            theme.mix(theme.live, theme.onLive, 0.2f))
                    : voiceButtonBackground(theme.primary, theme.primaryPressed));
            typelessButton.setTextColor(stopState ? stopInk : theme.onPrimary);
            if (voiceIcon != null) {
                voiceIcon.setColor(stopState ? stopInk : theme.onPrimary);
                voiceIcon.setStopGlyph(stopState);
            }
            typelessButton.setContentDescription(stopState
                    ? "关闭共享麦克风" : "开启共享麦克风");
            if (voiceMeter != null) {
                voiceMeter.setVoiceState(stopState
                        ? VoiceLevelView.ACTIVE : VoiceLevelView.IDLE);
                voiceMeter.setLevel(stopState ? state.level : 0);
            }
            setControlEnabled(typelessButton,
                    !sharedStartPending && !isVoiceStarting()
                            && !dictationActive && !typelessInFlight);
            return;
        }
        boolean holdMode = MODE_HOLD.equals(voiceMode);
        boolean starting = isVoiceStarting();
        boolean stopping = dictationActive && typelessInFlight;
        boolean stopState = starting || dictationActive;
        if (voiceMeter != null) {
            voiceMeter.setVoiceState(dictationPaused
                    ? VoiceLevelView.PAUSED
                    : dictationActive
                    ? VoiceLevelView.ACTIVE
                    : starting
                    ? VoiceLevelView.CONNECTING
                    : VoiceLevelView.IDLE);
        }
        int stopFill = theme.live;
        int stopInk = theme.onLive;
        typelessButton.setBackground(stopState
                ? voiceButtonBackground(stopFill,
                        theme.mix(theme.live, theme.onLive, 0.2f))
                : voiceButtonBackground(theme.primary, theme.primaryPressed));
        typelessButton.setTextColor(stopState ? stopInk : theme.onPrimary);
        if (voiceIcon != null) {
            voiceIcon.setColor(stopState ? stopInk : theme.onPrimary);
            voiceIcon.setStopGlyph(stopState);
        }
        typelessButton.setContentDescription(holdMode
                ? "按住开始手机语音输入，松开停止"
                : starting
                ? "取消语音启动"
                : dictationActive
                ? "停止并完成手机语音输入"
                : "开始手机语音输入");
        if (holdMode) {
            setControlEnabled(typelessButton, !typelessInFlight || holdGestureActive);
            return;
        }

        setControlEnabled(typelessButton, !stopping);
    }

    private void setControlEnabled(Button button, boolean enabled) {
        button.setEnabled(enabled);
        button.setAlpha(enabled ? 1f : 0.45f);
    }

    /// 保留旧电脑的更新请求兼容；共享麦克风只能从手机主动开启。
    private void maybeFollowUpdateRequest(String computerId, JSONObject health) {
        if (computerId == null || computerId.isBlank() || health == null) {
            return;
        }
        if (FleetUpdateActivity.opened) return;
        JSONObject update = health.optJSONObject("updates");
        SharedPreferences updatePrefs = getSharedPreferences("PhoneDeckUpdates", MODE_PRIVATE);
        boolean retryUpdate = update != null && update.optBoolean("supported")
                && updatePrefs.getStringSet("pending_devices", java.util.Collections.emptySet()).contains(computerId)
                && update.optLong("sequence", 0) < updatePrefs.getLong("pending_sequence", 0)
                && System.currentTimeMillis() - updatePrefs.getLong("last_auto_retry", 0) > 60000;
        if (retryUpdate && hasWindowFocus() && !isVoiceStarting() && !dictationActive && !typelessInFlight
                && (audioStreamer == null || !audioStreamer.isRunning())) {
            updatePrefs.edit().putLong("last_auto_retry", System.currentTimeMillis()).apply();
            startActivity(new Intent(this, FleetUpdateActivity.class).putExtra("resume", true));
            return;
        }
        String updateRequest = update == null ? "" : update.optString("requestId", "");
        if (!updateRequest.isBlank() && !"null".equals(updateRequest) && hasWindowFocus()
                && !isVoiceStarting() && !dictationActive && !typelessInFlight
                && (audioStreamer == null || !audioStreamer.isRunning())) {
            SharedPreferences seen = getSharedPreferences("PhoneDeckUpdates", MODE_PRIVATE);
            if (!updateRequest.equals(seen.getString("seen_" + computerId, ""))) {
                seen.edit().putString("seen_" + computerId, updateRequest).apply();
                startActivity(new Intent(this, FleetUpdateActivity.class).putExtra("sourceId", computerId));
                return;
            }
        }
    }

    private void toggleSharedMicrophone() {
        if (!WORK_SHARED.equals(voiceWorkMode)) {
            return;
        }
        PhoneAudioService.Snapshot state = PhoneAudioService.getSnapshot();
        if (state.running) {
            stopSharedMicrophoneService();
            showActionFeedback("■  正在关闭共享麦克风…", theme.warning);
            return;
        }
        if (isVoiceStarting() || dictationActive || typelessInFlight
                || (audioStreamer != null && audioStreamer.isRunning())) {
            showActionFeedback("✕  请等待手机控制听写完全结束后再开启共享", theme.warning);
            return;
        }
        java.util.ArrayList<String> missing = new java.util.ArrayList<>();
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO)
                != PackageManager.PERMISSION_GRANTED) {
            missing.add(Manifest.permission.RECORD_AUDIO);
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU
                && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED) {
            missing.add(Manifest.permission.POST_NOTIFICATIONS);
        }
        if (!missing.isEmpty()) {
            sharedStartPending = true;
            updateVoiceControls();
            requestPermissions(missing.toArray(new String[0]), REQUEST_SHARED_MICROPHONE);
            return;
        }
        startSharedMicrophoneService();
    }

    private void startSharedMicrophoneService() {
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO)
                != PackageManager.PERMISSION_GRANTED) {
            sharedStartPending = false;
            showActionFeedback("✕  未授予麦克风权限，无法开启共享", theme.danger);
            updateVoiceControls();
            return;
        }
        sharedStartPending = true;
        showActionFeedback("●  正在开启共享麦克风并查找电脑…", theme.warning);
        Intent start = new Intent(this, PhoneAudioService.class)
                .setAction(PhoneAudioService.ACTION_START);
        try {
            startForegroundService(start);
        } catch (Exception exception) {
            sharedStartPending = false;
            showActionFeedback("✕  无法开启共享麦克风：" + exception.getMessage(), theme.danger);
            updateVoiceControls();
        }
    }

    private void stopSharedMicrophoneService() {
        sharedStartPending = false;
        Intent stop = new Intent(this, PhoneAudioService.class)
                .setAction(PhoneAudioService.ACTION_STOP);
        startService(stop);
        updateVoiceControls();
        refreshTargetSwitcher();
    }

    private void renderSharedAudioStatus(PhoneAudioService.Snapshot state) {
        if (!WORK_SHARED.equals(voiceWorkMode) || state == null) {
            return;
        }
        sharedStartPending = false;
        boolean receiverStatesChanged = !lastSharedReceiverStates.equals(state.receiverStates);
        if (receiverStatesChanged) {
            lastSharedReceiverStates = new java.util.HashMap<>(state.receiverStates);
        }
        if (voiceMeter != null) {
            voiceMeter.setVoiceState(state.running
                    ? VoiceLevelView.ACTIVE : VoiceLevelView.IDLE);
            voiceMeter.setLevel(state.running ? state.level : 0);
        }
        if (microphoneLevel != null) {
            if (state.running) {
                microphoneLevel.setText("手机麦克风  ·  " + state.level + "%  ·  "
                        + state.connected + "/" + state.total + " 正在供音");
                microphoneLevel.setTextColor(state.connected > 0
                        ? theme.success : theme.warning);
            } else {
                microphoneLevel.setText("手机麦克风  ○ 共享已关闭");
                microphoneLevel.setTextColor(theme.muted);
            }
        }
        String detail = state.detail == null ? "" : state.detail;
        if (!detail.equals(lastSharedDetail)) {
            lastSharedDetail = detail;
            showActionFeedback((state.running ? "●  " : "■  ") + detail,
                    state.running && state.connected == 0 ? theme.warning : theme.muted);
        }
        updateVoiceControls();
        if (receiverStatesChanged) {
            refreshTargetSwitcher();
        }
    }

    private void beginPhoneDictation() {
        if (WORK_SHARED.equals(voiceWorkMode)) {
            showActionFeedback("✕  当前是共享麦克风模式", theme.warning);
            return;
        }
        PhoneDeckEndpoint endpoint = endpointForActiveTarget();
        if (endpoint == null) {
            if (targetComputerId != null && Boolean.TRUE.equals(
                    lanPairingRejected.get(targetComputerId))) {
                showConnection(targetDisplayName + " · 需要重新配对", theme.warning);
                showActionFeedback(isDesktopPreviewChannel()
                        ? "✕  配对已失效，请重新扫描电脑二维码"
                        : "✕  配对已失效；用 USB 连接 " + targetDisplayName + " 一次即可自动修复", theme.danger);
            } else if (isBluetoothTargetOnline()) {
                showConnection(targetDisplayName + " · 仅蓝牙在线", theme.warning);
                showActionFeedback("✕  当前电脑的蓝牙只能发送快捷键；请连接 Wi-Fi 或 USB",
                        theme.danger);
            } else {
                showConnection(targetDisplayName + " · 当前离线", theme.danger);
                showActionFeedback("✕  目标电脑未连接，请选择一台在线电脑", theme.danger);
            }
            testConnection();
            testLanConnections();
            return;
        }
        if (!activePhoneAudioAvailable()) {
            showConnection(targetDisplayName + (isDesktopPreviewChannel()
                    ? " · 语音模型未就绪" : " · 虚拟麦克风未就绪"), theme.warning);
            showActionFeedback(isDesktopPreviewChannel()
                    ? "✕  请先在电脑下载语音模型并等待就绪"
                    : "✕  电脑未检测到 VB-CABLE，未启动语音输入", theme.danger);
            microphoneLevel.setText("手机麦克风  ○ 未启动");
            microphoneLevel.setTextColor(theme.muted);
            testConnection();
            if (isDesktopPreviewChannel()) testLanConnections();
            return;
        }
        // 引擎无可读配置时（virtualCableSelected 为 null）不阻断：
        // 由服务端在 start 校验；可校验但未配置时给出指向性提示。
        if (Boolean.FALSE.equals(activeTypelessVirtualCableSelected())) {
            String engineName = activeEngineName();
            showConnection(targetDisplayName + " · " + engineName + " 麦克风未配置", theme.warning);
            showActionFeedback("✕  请先在 " + engineName + " 中选择 CABLE Output", theme.danger);
            microphoneLevel.setText("手机麦克风  ○ 未启动");
            microphoneLevel.setTextColor(theme.muted);
            testConnection();
            return;
        }
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO)
                != PackageManager.PERMISSION_GRANTED) {
            audioStartPending = true;
            voiceBusyLabel = "等待麦克风权限…";
            updateVoiceControls();
            requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO}, REQUEST_MICROPHONE);
            return;
        }

        audioStartPending = true;
        dictationPaused = false;
        currentSessionId = UUID.randomUUID().toString();
        // 单调时钟 tapAt：端到端延迟测量的起点（AudioStreamer 各阶段日志同源）。
        Log.i("PhoneDeckVoice", currentSessionId + " tap "
                + SystemClock.elapsedRealtime());
        currentSessionManaged = activeManagedDictationSupported();
        currentSessionTargetComputerId = serverProtocolVersion >= 2
                ? targetComputerId : null;
        currentSessionEndpoint = endpoint;
        currentSessionMode = effectiveSelectedMode();
        if (currentSessionManaged
                && (currentSessionTargetComputerId == null
                || currentSessionTargetComputerId.isBlank())) {
            clearVoiceSessionState();
            showActionFeedback("✕  尚未确认目标电脑，请重新检测连接", theme.danger);
            testConnection();
            return;
        }
        intentionalAudioStopSessionId = null;
        setTypelessBusy("正在连接手机麦克风…");
        showActionFeedback("●  正在建立 " + endpoint.label + " 音频通道…", theme.warning);
        microphoneLevel.setText("手机麦克风  ◌ 正在连接");
        microphoneLevel.setTextColor(theme.warning);
        if (!audioStreamer.start(
                currentSessionId,
                currentSessionTargetComputerId,
                currentSessionEndpoint)) {
            clearVoiceSessionState();
            showActionFeedback("✕  上一个手机音频会话仍在收尾，请稍后重试", theme.danger);
            return;
        }
        if (currentSessionManaged) {
            mainHandler.removeCallbacks(managedVoiceCheck);
            mainHandler.post(managedVoiceCheck);
            // 协议 v2 接收端会在 start 内等待同一 sessionId 的音频会话，
            // 因此无需先等 HTTPS 音频通道完成。录音、TLS/WASAPI 与
            // 语音引擎唤醒并行进行，首段 PCM 由手机和服务端 pre-roll 保留。
            setTypelessBusy("正在快速唤醒语音输入…");
            showActionFeedback("●  正在并行启动麦克风与语音引擎…", theme.warning);
            sendTypelessToggle(true);
        }
    }

    private void stopPhoneDictation() {
        if (currentSessionId == null) {
            clearVoiceSessionState();
            showActionFeedback("●  当前没有需要停止的听写会话", theme.muted);
            return;
        }
        intentionalAudioStopSessionId = currentSessionId;
        dictationPaused = false;
        setTypelessBusy("正在结束听写…");
        showActionFeedback("■  手机录音已停止，正在让电脑完成文字…", theme.warning);
        if (audioStreamer != null) {
            audioStreamer.stop();
        }
        sendTypelessToggle(false);
    }

    private void onAudioReady(String sessionId) {
        if (!audioStartPending || !sessionId.equals(currentSessionId)) {
            return;
        }
        audioStartPending = false;
        if (currentSessionManaged) {
            showActionFeedback("●  手机麦克风已连接，正在等待电脑端确认…", theme.warning);
            return;
        }
        showActionFeedback("●  手机麦克风已连接，正在唤醒语音输入…", theme.warning);
        setTypelessBusy("正在唤醒语音输入…");
        sendTypelessToggle(true);
    }

    private void sendTypelessToggle(boolean starting) {
        final String sessionId = currentSessionId;
        if (sessionId == null) {
            clearVoiceSessionState();
            showActionFeedback("✕  听写会话已失效，请重新开始", theme.danger);
            return;
        }
        final boolean managed = currentSessionManaged;
        final String sessionTargetComputerId = currentSessionTargetComputerId;
        final PhoneDeckEndpoint sessionEndpoint = currentSessionEndpoint;
        final long queuedAt = SystemClock.elapsedRealtime();
        if (starting) {
            armVoiceStartWatchdog(
                    sessionId, managed, sessionTargetComputerId, sessionEndpoint);
        } else {
            disarmVoiceStartWatchdog();
        }
        Log.i("PhoneDeckVoice", sessionId + " commandQueued starting=" + starting);
        voiceExecutor.execute(() -> {
            Log.i("PhoneDeckVoice", sessionId + " commandStarted +"
                    + (SystemClock.elapsedRealtime() - queuedAt) + "ms");
            try {
                String transport;
                if (managed) {
                    transport = sendManagedDictationCommand(
                            starting, sessionId, sessionTargetComputerId, sessionEndpoint);
                } else {
                    JSONObject body = new JSONObject();
                    body.put("action", "typeless");
                    body.put("requestId", UUID.randomUUID().toString());
                    transport = sendCommand(body);
                }
                if (!starting) {
                    if (!managed) {
                        Thread.sleep(280);
                    }
                    audioStreamer.stop(sessionId);
                }
                Log.i("PhoneDeckVoice", sessionId + " commandConfirmed starting="
                        + starting + " +"
                        + (SystemClock.elapsedRealtime() - queuedAt) + "ms");
                mainHandler.post(() -> {
                    if (!sessionId.equals(currentSessionId)) {
                        return;
                    }
                    disarmVoiceStartWatchdog();
                    dictationActive = starting;
                    if (starting) {
                        voiceStartConfirmedAt = SystemClock.elapsedRealtime();
                        dictationPaused = false;
                    }
                    showConnection(transport + " 已连接", theme.success);
                    showActionFeedback(starting
                                    ? "✓  " + activeEngineName() + " 正在使用手机麦克风听写 · " + transport
                                    : "✓  听写已停止 · 请在电脑确认文字 · " + transport,
                            theme.success);
                    performResultHaptic(typelessButton, true);
                    flashResult(typelessButton, theme.success);
                    finishGuardedAction(typelessButton, true);
                    if (starting && holdReleasePending && !holdGestureActive) {
                        holdReleasePending = false;
                        mainHandler.postDelayed(this::stopPhoneDictation, 80);
                    }
                    if (!starting) {
                        clearVoiceSessionState();
                        microphoneLevel.setText("手机麦克风  ○ 已停止");
                        microphoneLevel.setTextColor(theme.muted);
                    }
                });
            } catch (Exception exception) {
                Log.w("PhoneDeckVoice", sessionId + " commandFailed starting="
                        + starting + " +"
                        + (SystemClock.elapsedRealtime() - queuedAt) + "ms", exception);
                mainHandler.post(() -> {
                    if (!sessionId.equals(currentSessionId)) {
                        return;
                    }
                    intentionalAudioStopSessionId = sessionId;
                    audioStreamer.stop(sessionId);
                    disarmVoiceStartWatchdog();
                    holdReleasePending = false;
                    clearVoiceSessionState();
                    showConnection("语音指令发送失败", theme.danger);
                    showActionFeedback("✕  电脑没有确认，请检查 Wi-Fi/USB 连接后重试", theme.danger);
                    performResultHaptic(typelessButton, false);
                    flashResult(typelessButton, theme.danger);
                    finishGuardedAction(typelessButton, true);
                });
            }
        });
    }

    private void armVoiceStartWatchdog(
            String sessionId,
            boolean managed,
            String sessionTargetComputerId,
            PhoneDeckEndpoint sessionEndpoint) {
        disarmVoiceStartWatchdog();
        voiceStartWatchdog = () -> {
            if (!sessionId.equals(currentSessionId)
                    || dictationActive || !isVoiceStarting()) {
                return;
            }
            Log.w("PhoneDeckVoice", sessionId + " startWatchdogTimeout +"
                    + VOICE_START_WATCHDOG_MS + "ms");
            intentionalAudioStopSessionId = sessionId;
            if (audioStreamer != null) {
                audioStreamer.stop();
            }
            clearVoiceSessionState();
            microphoneLevel.setText("手机麦克风  ✕ 启动超时");
            microphoneLevel.setTextColor(theme.danger);
            showConnection("语音启动超时", theme.danger);
            showActionFeedback("✕  语音引擎长时间没有确认，已自动取消，请重试",
                    theme.danger);
            performResultHaptic(typelessButton, false);
            flashResult(typelessButton, theme.danger);
            if (managed && sessionTargetComputerId != null && sessionEndpoint != null) {
                bestEffortStopManagedDictation(
                        sessionId, sessionTargetComputerId, sessionEndpoint);
            }
        };
        mainHandler.postDelayed(voiceStartWatchdog, VOICE_START_WATCHDOG_MS);
    }

    private void disarmVoiceStartWatchdog() {
        if (voiceStartWatchdog == null) {
            return;
        }
        mainHandler.removeCallbacks(voiceStartWatchdog);
        voiceStartWatchdog = null;
    }

    private String sendManagedDictationCommand(
            boolean starting,
            String sessionId,
            String sessionTargetComputerId,
            PhoneDeckEndpoint sessionEndpoint)
            throws Exception {
        if (sessionTargetComputerId == null || sessionTargetComputerId.isBlank()) {
            throw new IllegalStateException("尚未确认目标电脑");
        }
        if (sessionEndpoint == null) {
            throw new IllegalStateException("目标电脑连接已经失效");
        }
        JSONObject body = new JSONObject();
        body.put("protocolVersion", 2);
        body.put("sessionId", sessionId);
        body.put("requestId", UUID.randomUUID().toString());
        body.put("targetComputerId", sessionTargetComputerId);
        body.put("mode", currentSessionMode == null ? "dictation" : currentSessionMode);
        String endpoint = starting ? "/api/dictation/start" : "/api/dictation/stop";
        if (!postEndpointWithRetry(sessionEndpoint, endpoint, body, 2)) {
            throw new IllegalStateException("电脑端未确认语音会话");
        }
        return sessionEndpoint.label;
    }

    private void setTypelessBusy(String label) {
        typelessInFlight = true;
        voiceBusyLabel = label;
        updateVoiceControls();
    }

    private void updateMicrophoneLevel(int percent) {
        if (!audioStreamer.isStreaming()) {
            return;
        }
        if (audioStreamer.isPaused()) {
            microphoneLevel.setText("手机麦克风  Ⅱ 已暂停（未采集声音）");
            microphoneLevel.setTextColor(theme.warning);
            if (voiceMeter != null) {
                voiceMeter.setVoiceState(VoiceLevelView.PAUSED);
            }
            return;
        }
        if (voiceMeter != null) {
            voiceMeter.setVoiceState(VoiceLevelView.ACTIVE);
            voiceMeter.setLevel(percent);
        }
        microphoneLevel.setText("手机麦克风  ·  " + percent + "%");
        microphoneLevel.setTextColor(percent > 0 ? theme.success : theme.muted);
    }

    private void onAudioStopped(String sessionId, String reason) {
        boolean intentional = sessionId.equals(intentionalAudioStopSessionId);
        if (intentional) {
            intentionalAudioStopSessionId = null;
        }
        if (!sessionId.equals(currentSessionId)) {
            return;
        }
        if (intentional) {
            audioStartPending = false;
            return;
        }
        if (reason == null && !audioStartPending && !dictationActive) {
            return;
        }
        boolean wasDictationActive = dictationActive;
        boolean wasManaged = currentSessionManaged;
        audioStartPending = false;
        clearVoiceSessionState();
        microphoneLevel.setText("手机麦克风  ✕ 音频中断");
        microphoneLevel.setTextColor(theme.danger);
        if (voiceMeter != null) {
            voiceMeter.setVoiceState(VoiceLevelView.ERROR);
        }
        showActionFeedback("✕  手机音频中断" + (reason == null ? "" : "：" + reason),
                theme.danger);
        if (wasDictationActive && !wasManaged) {
            bestEffortStopLegacyTypeless();
        }
    }

    private void bestEffortStopLegacyTypeless() {
        voiceRecoveryExecutor.execute(() -> {
            try {
                JSONObject body = new JSONObject();
                body.put("action", "typeless");
                body.put("requestId", UUID.randomUUID().toString());
                sendCommand(body);
            } catch (Exception ignored) {
                // 旧电脑端无法保证状态；新版 managedDictation 会在断流时自动复位。
            }
        });
    }

    private void clearVoiceSessionState() {
        disarmVoiceStartWatchdog();
        mainHandler.removeCallbacks(managedVoiceCheck);
        voiceStartConfirmedAt = 0;
        audioStartPending = false;
        dictationActive = false;
        dictationPaused = false;
        typelessInFlight = false;
        holdGestureActive = false;
        holdReleasePending = false;
        currentSessionId = null;
        currentSessionManaged = false;
        currentSessionTargetComputerId = null;
        currentSessionEndpoint = null;
        currentSessionMode = null;
        voiceBusyLabel = null;
        if (typelessButton != null) {
            updateVoiceControls();
        }
    }

    private String sendCommand(JSONObject body) throws Exception {
        boolean sent = false;
        String transport = "";

        LanTargetStatus lanStatus = targetComputerId == null
                ? null : lanTargets.get(targetComputerId);
        if (lanStatus != null) {
            sent = postEndpointWithRetry(
                    lanStatus.endpoint, "/api/input", body, 2);
            if (sent) {
                transport = "Wi-Fi";
            }
        }

        if (!sent && isUsbTargetOnline()) {
            sent = postUsbWithRetry("/api/input", body, 2);
            if (sent) {
                transport = "USB";
            } else {
                usbConnected = false;
            }
        }

        if (!sent && isBluetoothTargetOnline()) {
            sent = bluetoothTransport.sendAndWaitForAck(body, 1400);
            if (sent) {
                transport = "蓝牙";
            }
        }

        if (!sent) {
            throw new IllegalStateException("目标电脑当前没有可用连接");
        }
        return transport;
    }

    private boolean postUsbWithRetry(String endpoint, JSONObject body, int attempts) {
        return postEndpointWithRetry(PhoneDeckEndpoint.USB, endpoint, body, attempts);
    }

    private boolean postEndpointWithRetry(
            PhoneDeckEndpoint connectionEndpoint,
            String endpoint,
            JSONObject body,
            int attempts) {
        if (connectionEndpoint == null) {
            return false;
        }
        for (int attempt = 0; attempt < attempts; attempt++) {
            PostAttemptResult result = postEndpoint(connectionEndpoint, endpoint, body);
            if (result == PostAttemptResult.SUCCESS) {
                return true;
            }
            // A valid HTTP error means the receiver processed and rejected the
            // request. Retrying the same requestId cannot repair the state and,
            // for Typeless toggles, can turn a recovered failure into a second
            // misleading failure. Only transport errors are retryable.
            if (result == PostAttemptResult.REJECTED) {
                return false;
            }
            if (attempt + 1 < attempts) {
                try {
                    Thread.sleep(140);
                } catch (InterruptedException exception) {
                    Thread.currentThread().interrupt();
                    return false;
                }
            }
        }
        return false;
    }

    private PostAttemptResult postEndpoint(
            PhoneDeckEndpoint connectionEndpoint,
            String endpoint,
            JSONObject body) {
        long startedAt = SystemClock.elapsedRealtime();
        int readTimeout = endpoint.equals("/api/dictation/stop") ? 12_000
                : endpoint.startsWith("/api/dictation/") ? 7_000 : 1_800;
        try {
            PhoneDeckHttp.postJson(connectionEndpoint, endpoint, body, readTimeout);
            return PostAttemptResult.SUCCESS;
        } catch (PhoneDeckHttp.ResponseException exception) {
            Log.w("PhoneDeckNet", endpoint + " rejected via "
                    + connectionEndpoint.label + " +"
                    + (SystemClock.elapsedRealtime() - startedAt) + "ms: HTTP "
                    + exception.status + " " + exception.getMessage());
            return PostAttemptResult.REJECTED;
        } catch (Exception exception) {
            Log.w("PhoneDeckNet", endpoint + " failed via "
                    + connectionEndpoint.label + " +"
                    + (SystemClock.elapsedRealtime() - startedAt) + "ms: "
                    + exception.getClass().getSimpleName() + ": "
                    + exception.getMessage());
            return PostAttemptResult.RETRYABLE_FAILURE;
        }
    }

    private String activeForegroundApp() {
        LanTargetStatus lanStatus = targetComputerId == null
                ? null : lanTargets.get(targetComputerId);
        if (lanStatus != null) {
            return lanStatus.foregroundApp;
        }
        return isUsbTargetOnline() ? usbForegroundApp : null;
    }

    private String foregroundSuffix() {
        String app = UiText.optional(activeForegroundApp());
        return app.isEmpty() ? "" : " · " + app;
    }

    private void updateConnectionDisplay() {
        if (isLanTargetOnline()) {
            if (!activePhoneAudioAvailable()) {
                showConnection(targetDisplayName + (isDesktopPreviewChannel()
                        ? " · 语音模型未就绪，请在电脑下载" : " · Wi-Fi · 虚拟麦克风未就绪"), theme.warning);
            } else if (Boolean.FALSE.equals(activeTypelessVirtualCableSelected())) {
                showConnection(targetDisplayName + " · Wi-Fi · 麦克风未配置", theme.warning);
            } else {
                showConnection(targetDisplayName + " · Wi-Fi 在线" + foregroundSuffix(),
                        theme.success);
            }
            // M1-A A4：legacy-only 目标在线时提示一次升级（rotate 走 LAN 旧令牌通道）。
            maybePromptCredentialUpgrade();
        } else if (isUsbTargetOnline()) {
            if (!activePhoneAudioAvailable()) {
                showConnection(targetDisplayName + " · 虚拟麦克风未就绪", theme.warning);
            } else if (Boolean.FALSE.equals(activeTypelessVirtualCableSelected())) {
                showConnection(targetDisplayName + " · 麦克风未配置", theme.warning);
            } else {
                showConnection(targetDisplayName + " · USB 在线" + foregroundSuffix(),
                        theme.success);
            }
        } else if (isBluetoothTargetOnline()) {
            showConnection(targetDisplayName + " · 蓝牙快捷键在线", theme.success);
        } else if (targetComputerId != null) {
            showConnection(targetDisplayName + " · 当前离线", theme.muted);
        } else {
            showConnection(isDesktopPreviewChannel() ? "点击这里扫码连接电脑" : "等待电脑连接", theme.muted);
        }
    }

    private void showConnection(String title, int color) {
        statusText.setText(targetComputerId == null ? "连接你的电脑" : targetDisplayName);
        statusDetailText.setText(UiText.connectionDetail(targetDisplayName, title));
        connectionCard.setContentDescription(statusText.getText() + "，" + statusDetailText.getText()
                + "。点击查看全部电脑");
        statusText.setTextColor(theme.text);
        statusDetailText.setTextColor(theme.muted);
        statusDetailText.setVisibility(targetComputerId == null ? View.GONE : View.VISIBLE);
        statusDot.setBackground(roundRect(color, 20));
    }

    private void showActionFeedback(String message, int color) {
        lastFeedbackMessage = message;
        lastFeedbackColor = color;
        actionFeedback.setText(message);
        if (shortcutDialog != null && shortcutDialog.isShowing()) {
            shortcutPanelFeedback.setText(message);
            shortcutPanelFeedback.setTextColor(color);
        }
        actionFeedback.setVisibility(View.VISIBLE);
        actionFeedback.setTextColor(color);
        actionFeedback.setBackground(theme.shape(
                this, theme.feedbackSurface(color), 14, 1,
                theme.mix(theme.outline, color, 0.35f)));
    }

    private void finishGuardedAction(Button source, boolean guarded) {
        if (!guarded) {
            return;
        }
        disarmVoiceStartWatchdog();
        typelessInFlight = false;
        voiceBusyLabel = null;
        updateVoiceControls();
    }

    private void installTouchFeedback(View control) {
        installTouchFeedback(control, null);
    }

    private void installTouchFeedback(View control, Runnable onRelease) {
        TouchFeedback.install(control, onRelease);
    }

    private void performResultHaptic(View view, boolean success) {
        TouchFeedback.resultHaptic(view, success);
    }

    private void flashResult(View view, int color) {
        TouchFeedback.result(view, color == theme.success);
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == REQUEST_BLUETOOTH) {
            if (grantResults.length > 0 && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
                startBluetoothTransport();
            } else {
                bluetoothDetail = "未授予蓝牙权限；USB 仍可使用";
                updateConnectionDisplay();
            }
        } else if (requestCode == REQUEST_MICROPHONE) {
            if (grantResults.length > 0 && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
                beginPhoneDictation();
            } else {
                clearVoiceSessionState();
                showActionFeedback("✕  未授予麦克风权限，无法传输手机声音", theme.danger);
            }
        } else if (requestCode == REQUEST_SHARED_MICROPHONE) {
            if (checkSelfPermission(Manifest.permission.RECORD_AUDIO)
                    == PackageManager.PERMISSION_GRANTED) {
                startSharedMicrophoneService();
            } else {
                sharedStartPending = false;
                showActionFeedback("✕  未授予麦克风权限，无法开启共享", theme.danger);
                updateVoiceControls();
            }
        }
    }

    @Override
    protected void onDestroy() {
        if (homeStyleDialog != null) homeStyleDialog.dismiss();
        if (shortcutDialog != null) shortcutDialog.dismiss();
        TranscriptRelay.release();
        mainHandler.removeCallbacks(periodicHealthCheck);
        mainHandler.removeCallbacks(managedVoiceCheck);
        voiceStatusExecutor.shutdownNow();
        if (sharedStatusReceiverRegistered) {
            unregisterReceiver(sharedStatusReceiver);
            sharedStatusReceiverRegistered = false;
        }
        stopKeyRepeat();
        releaseWifiLock();
        unregisterNetworkCallbacks();
        if (bluetoothTransport != null) {
            bluetoothTransport.close();
        }
        if (audioStreamer != null) {
            intentionalAudioStopSessionId = currentSessionId;
            audioStreamer.close();
        }
        clearVoiceSessionState();
        lanProbePool.shutdownNow();
        actionExecutor.shutdownNow();
        voiceExecutor.shutdownNow();
        voiceRecoveryExecutor.shutdownNow();
        connectionExecutor.shutdownNow();
        super.onDestroy();
    }

    private void unregisterNetworkCallbacks() {
        if (networkCallback == null) {
            return;
        }
        try {
            ConnectivityManager manager = (ConnectivityManager) getApplicationContext()
                    .getSystemService(Context.CONNECTIVITY_SERVICE);
            if (manager != null) {
                manager.unregisterNetworkCallback(networkCallback);
            }
        } catch (Exception ignored) {
            // 系统可能已随网络服务注销。
        }
        networkCallback = null;
    }

    private Button smallButton(String label) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextSize(12);
        button.setTextColor(theme.text);
        button.setAllCaps(false);
        button.setPadding(dp(4), 0, dp(4), 0);
        button.setBackground(theme.pressable(
                this, theme.key, theme.mix(theme.key, theme.primary, 0.18f), 19));
        button.setStateListAnimator(null);
        installTouchFeedback(button);
        return button;
    }

    private Button voiceEditButton(String label) {
        Button button = smallButton(label);
        button.setTextSize(14);
        button.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        button.setBackground(theme.pressable(
                this, theme.surfaceRaised,
                theme.mix(theme.surface, theme.primary, 0.16f), 14));
        return button;
    }

    private TextView text(String value, int sizeSp, int color, int style) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(sizeSp);
        view.setTextColor(color);
        view.setTypeface(Typeface.DEFAULT, style);
        return view;
    }

    private GradientDrawable roundRect(int color, int radiusDp) {
        return theme.shape(this, color, radiusDp);
    }

    private Drawable pressableRoundRect(int normalColor, int pressedColor, int radiusDp) {
        return theme.pressable(this, normalColor, pressedColor, radiusDp);
    }

    private LinearLayout.LayoutParams marginTop(int top) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT,
                LinearLayout.LayoutParams.WRAP_CONTENT);
        params.topMargin = top;
        return params;
    }

    private LinearLayout.LayoutParams margins(
            int left, int top, int right, int bottom, int width, int height) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(width, height);
        params.setMargins(left, top, right, bottom);
        return params;
    }

    private static final class LanTargetStatus {
        final PhoneDeckEndpoint endpoint;
        final int protocolVersion;
        final boolean managedDictationSupported;
        final boolean phoneAudioAvailable;
        /// null = 引擎无可读配置、无法校验虚拟声卡（不阻断启动）。
        final Boolean typelessVirtualCableSelected;
        final EngineMode[] typelessModes;
        final String engineName;
        final String foregroundApp;
        /// 本状态确认在线的时刻（elapsedRealtime），供离线判定宽限使用。
        final long lastOnlineAt;

        LanTargetStatus(
                PhoneDeckEndpoint endpoint,
                int protocolVersion,
                boolean managedDictationSupported,
                boolean phoneAudioAvailable,
                Boolean typelessVirtualCableSelected,
                EngineMode[] typelessModes,
                String engineName,
                String foregroundApp) {
            this.endpoint = endpoint;
            this.protocolVersion = protocolVersion;
            this.managedDictationSupported = managedDictationSupported;
            this.phoneAudioAvailable = phoneAudioAvailable;
            this.typelessVirtualCableSelected = typelessVirtualCableSelected;
            this.typelessModes = typelessModes;
            this.engineName = engineName;
            this.foregroundApp = foregroundApp;
            this.lastOnlineAt = android.os.SystemClock.elapsedRealtime();
        }
    }

    /// 语音引擎的一种工作模式（id + 显示名），来自 /api/health 的
    /// voiceEngine.modes（多引擎协议）或旧 typeless 块的快捷键槽位。
    private static final class EngineMode {
        final String id;
        final String label;

        EngineMode(String id, String label) {
            this.id = id;
            this.label = label;
        }
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
