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
    private static final int REQUEST_SHARED_MICROPHONE = 1004;
    private static final String SERVER = "http://127.0.0.1:8765";
    private static final String PREFS_NAME = "PhoneDeckSettings";
    /// M1-A A4：legacy-only 电脑的升级提示 per-computerId 只弹一次（取消也算已提示）。
    private static final String PREF_CREDENTIAL_UPGRADE_PROMPT = "credential_upgrade_prompted_";
    /// M1-A A4：每台电脑的升级 clientId 持久化沿用，重试不再签发全新凭据
    /// （rotate 对同 clientId 只回 already-upgraded；且 per-clientId 限速对本机重试生效）。
    private static final String PREF_CREDENTIAL_UPGRADE_CLIENT_ID = "credential_upgrade_client_id_";
    private static final long HEALTH_CHECK_INTERVAL_MS = 2_000;
    private final ExecutorService actionExecutor = Executors.newSingleThreadExecutor();
    private final ExecutorService connectionExecutor = Executors.newSingleThreadExecutor();
    private final ExecutorService targetSwitchExecutor = Executors.newSingleThreadExecutor();
    private final java.util.concurrent.atomic.AtomicBoolean sharedRenderPending =
            new java.util.concurrent.atomic.AtomicBoolean();
    private boolean targetSwitchInFlight;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private PhoneDeckTheme theme;
    private String appliedThemeId;
    private TextView statusText;
    private TextView statusDetailText;
    private LinearLayout connectionCard;
    private View statusDot;
    private TextView actionFeedback;
    private TextView microphoneLevel;
    private Button typelessButton;
    private TextView voiceButtonCaption;
    private TextView voiceStatusCaption;
    private TextView voiceHint;
    private FrameLayout voiceHalo;
    private MicrophoneGlyphDrawable voiceIcon;
    private VoiceLevelView voiceMeter;
    private GridLayout shortcutGrid;
    /// 对话白首页的“你的电脑”卡片区。
    private LinearLayout computerSection;
    private LinearLayout computerCardRow;
    private TextView fleetSummaryBadge;
    private String lastComputerCardsSignature;
    /// 居中的卡片（停在哪台就是在跟哪台说话）：已连接电脑的 id；附近/添加卡片为 null。
    private String carouselFocusId;
    /// 滑到后确认没连上的电脑：中部提示，点按卡片重试，不弹回原来那台。
    private String carouselFailedId;
    /// 正在确认的电脑（滑到或点到它后，实时探测成功才真正切换）。
    private String confirmingComputerId;
    /// 同一 Wi-Fi 里发现、尚未连接的电脑（computerId → 发现结果）。
    private final java.util.Map<String, LanDiscoveryClient.DiscoveredComputer> nearbyComputers =
            new java.util.concurrent.ConcurrentHashMap<>();
    /// 发现与重新配对共用一个按需线程，空闲 20 秒后释放。
    private final java.util.concurrent.ExecutorService nearbyExecutor =
            new java.util.concurrent.ThreadPoolExecutor(0, 1, 20, java.util.concurrent.TimeUnit.SECONDS,
                    new java.util.concurrent.LinkedBlockingQueue<>());
    private volatile boolean nearbyScanInFlight;
    private boolean nearbyPairingInFlight;
    private final Runnable nearbyScan = this::scanNearbyComputers;
    private View quickRow;
    private ScrollView shortcutScroll;
    private android.app.Dialog shortcutDialog;
    private TextView shortcutPanelFeedback;
    private boolean sharedStartPending;
    private boolean sharedStatusReceiverRegistered;
    private String lastSharedDetail;
    /** running/connected/total last rendered into the voice controls; level-only ticks skip them. */
    private String lastSharedControlsKey;
    private Map<String, String> lastSharedReceiverStates = java.util.Collections.emptyMap();
    private volatile boolean usbConnected;
    private volatile boolean lanCheckInFlight;
    private volatile String usbEngineName = "Typeless";
    private WifiManager.WifiLock wifiLock;
    private boolean keepConnectionAlive = true;
    private String lastFeedbackMessage;
    private int lastFeedbackColor;

    private static final long REPEAT_INTERVAL_MS = 150;
    private enum PostAttemptResult { SUCCESS, RETRYABLE_FAILURE, REJECTED }

    private static final java.util.Set<String> REPEATABLE_KEYS = new java.util.HashSet<>(
            java.util.Arrays.asList("DELETE", "LEFT", "RIGHT", "UP", "DOWN"));
    /// 长按连发的组合键（按词移动与按词删除），键序与 normalizeChord 一致：修饰键在前。
    private static final java.util.Set<String> REPEATABLE_CHORDS = new java.util.HashSet<>(
            java.util.Arrays.asList("CTRL,LEFT", "CTRL,RIGHT", "CTRL,BACKSPACE", "CTRL,DELETE"));
    private Runnable repeatRunnable;
    private ShortcutButtonConfig repeatConfig;
    private ShortcutKeyView repeatSource;
    private boolean gridEditMode;
    private Button gridEditButton;
    private GridLayout homeShortcutGrid;
    private Button homeEditButton;
    private TextView homeShortcutSubtitle;
    private java.util.ArrayList<ShortcutButtonConfig> lastRemovedSnapshot;
    private final Runnable clearUndoSnapshot = () -> lastRemovedSnapshot = null;
    private TextView shortcutHintText;
    private TextView shortcutNormalHintText;
    private volatile String usbForegroundApp;
    private volatile boolean phoneAudioAvailable;
    private volatile boolean usbEngineCapturing;
    private volatile boolean usbAudioStreaming;
    /// null = 电脑端引擎无可读配置、无法校验麦克风（不阻断启动）。
    private volatile Boolean typelessVirtualCableSelected;
    private volatile int serverProtocolVersion;
    private volatile String targetComputerId;
    private volatile String targetDisplayName = "当前电脑";
    private volatile String usbComputerId;
    private volatile String usbDisplayName;
    private volatile int usbProtocolVersion;
    private final String clientSessionId = UUID.randomUUID().toString();
    private ShortcutConfigRepository configRepository;
    private TargetDeviceManager targetDeviceManager;
    private final ConcurrentHashMap<String, LanTargetStatus> lanTargets =
            new ConcurrentHashMap<>();
    /// 地址可达但配对令牌/证书被拒（401/403/指纹不一致）的电脑集合；
    /// 与“离线”区分展示，提示用户插一次 USB 即可自动重新配对。
    private final ConcurrentHashMap<String, Boolean> lanPairingRejected =
            new ConcurrentHashMap<>();
    /// 并行探测所有候选地址；死地址短超时快速失败，不互相排队。
    /// 单轮 LAN 探测全部失败时的连续计数，用于探测间隔退避。
    private int lanCheckFailStreak;
    /// 立即探测的最小间隔，避免网络回调风暴。
    private long lastLanCheckAt;
    /// 网络事件在当前探测/防抖窗口内合并，窗口结束后一定补发一轮，不能丢掉切网。
    private final WifiLinkPolicy.PendingRefresh pendingLanRefresh =
            new WifiLinkPolicy.PendingRefresh();
    private final Runnable pendingLanCheck = () -> {
        if (isFinishing() || isDestroyed() || lanCheckInFlight) {
            return;
        }
        WifiLinkPolicy.PendingRefresh.Request request = pendingLanRefresh
                .takeIfDue(SystemClock.elapsedRealtime());
        if (request == null) {
            schedulePendingLanCheck();
            return;
        }
        runImmediateLanCheck(request.reason == null
                ? "Wi-Fi 链路变化" : request.reason, request.probeUsb);
    };
    /// 单台设备探测失败后到判离线的宽限期。
    private static final long OFFLINE_GRACE_MS = 6_000;
    private WifiLinkObserver.Registration wifiLinkRegistration;

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
            if (!PhoneDeckHttp.appInForeground || isFinishing() || isDestroyed()) {
                return;
            }
            testConnection();
            testLanConnections();
            mainHandler.postDelayed(this, nextHealthCheckDelayMs());
        }
    };

    private final Runnable inProcessAudioListener = () -> {
        if (sharedRenderPending.compareAndSet(false, true)) {
            mainHandler.post(() -> {
                sharedRenderPending.set(false);
                if (PhoneDeckHttp.appInForeground && !isDestroyed()) {
                    renderSharedAudioStatus(PhoneAudioService.getSnapshot());
                }
            });
        }
    };

    /// 一切正常时 2 秒一轮；连续失败按 4/8/16/30 秒退避并加 ±20% 抖动，
    /// 避免请求风暴与高频耗电；成功后立即恢复正常节奏。
    private long nextHealthCheckDelayMs() {
        long base = HEALTH_CHECK_INTERVAL_MS;
        PhoneAudioService.Snapshot shared = PhoneAudioService.getSnapshot();
        if (shared.running && shared.total > 0 && shared.connected >= shared.total) {
            // 共享供音全部正常时界面也不必 2 秒一刷；服务那边另有同样的放慢。
            base = SharedAudioPolicies.STEADY_PROBE_INTERVAL_MS;
        }
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
        theme = PhoneDeckTheme.load(this);
        appliedThemeId = theme.id;
        theme.applyWindow(this);
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);

        configRepository = new ShortcutConfigRepository(this);
        targetDeviceManager = TargetDeviceManager.get(this);
        setContentView(createInterface());
        registerSharedAudioStatusReceiver();
        PhoneAudioService.setInProcessListener(inProcessAudioListener);
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

    /// Wi-Fi 切换、DHCP 变化、网络恢复时立即重新探测与发现，不需要 USB。
    private void registerNetworkCallbacks() {
        if (wifiLinkRegistration != null) {
            return;
        }
        try {
            wifiLinkRegistration = WifiLinkObserver.register(this, state ->
                    requestImmediateLanCheck(state.connected
                            ? "Wi-Fi 链路可用或地址变化" : "Wi-Fi 链路断开", false));
        } catch (Exception exception) {
            Log.w("PhoneDeckNet", "注册网络回调失败：" + exception.getMessage());
            wifiLinkRegistration = null;
        }
    }

    private void requestImmediateLanCheck(String reason) {
        requestImmediateLanCheck(reason, true);
    }

    private void requestImmediateLanCheck(String reason, boolean invalidate) {
        if (invalidate) {
            // Non-network callers (foreground resume, pairing/credential
            // changes) still need the old explicit reset behavior.  Wi-Fi
            // observer callbacks pass false because the observer already
            // invalidated this generation exactly once.
            ConnectionMonitor.get(this).reset();
        }
        mainHandler.post(() -> {
            if (!PhoneDeckHttp.appInForeground || isFinishing() || isDestroyed()) {
                return;
            }
            pendingLanRefresh.request(reason, invalidate,
                    SystemClock.elapsedRealtime(), lastLanCheckAt);
            schedulePendingLanCheck();
        });
    }

    private void schedulePendingLanCheck() {
        if (!PhoneDeckHttp.appInForeground || isFinishing() || isDestroyed() || lanCheckInFlight
                || !pendingLanRefresh.isPending()) {
            return;
        }
        long delay = Math.max(1L,
                pendingLanRefresh.dueAt() - SystemClock.elapsedRealtime());
        mainHandler.removeCallbacks(pendingLanCheck);
        mainHandler.postDelayed(pendingLanCheck, delay);
    }

    private void runImmediateLanCheck(String reason, boolean probeUsb) {
        if (!PhoneDeckHttp.appInForeground || isFinishing() || isDestroyed()) {
            return;
        }
        long waitForDebounce = Math.max(0L,
                1_000L - (SystemClock.elapsedRealtime() - lastLanCheckAt));
        if (lanCheckInFlight || waitForDebounce > 0L) {
            pendingLanRefresh.request(reason, probeUsb,
                    SystemClock.elapsedRealtime(), lastLanCheckAt);
            schedulePendingLanCheck();
            return;
        }
        Log.i("PhoneDeckNet", "立即重新探测：" + reason);
        lanCheckFailStreak = 0;
        // WifiLinkObserver has already invalidated ConnectionMonitor once for
        // Wi-Fi generations.  Only the old non-network callers request a USB
        // health probe; a Wi-Fi callback must not tear down a healthy fallback.
        lastLanCheckAt = SystemClock.elapsedRealtime();
        if (probeUsb) {
            testConnection();
        }
        testLanConnections();
        mainHandler.post(nearbyScan);
    }

    @Override
    protected void onPause() {
        super.onPause();
        PhoneDeckHttp.appInForeground = false;
        PhoneAudioService.setInProcessListener(null);
        mainHandler.removeCallbacks(nearbyScan);
        mainHandler.removeCallbacks(periodicHealthCheck);
        mainHandler.removeCallbacks(pendingLanCheck);
        pendingLanRefresh.clear();
        releaseWifiLock();
    }

    @Override
    protected void onResume() {
        super.onResume();
        PhoneDeckHttp.appInForeground = true;
        PhoneAudioService.setInProcessListener(inProcessAudioListener);
        mainHandler.removeCallbacks(periodicHealthCheck);
        mainHandler.postDelayed(periodicHealthCheck, HEALTH_CHECK_INTERVAL_MS);
        mainHandler.post(nearbyScan);
        // 调试/自动化入口：adb 启动时带 open_shortcut_panel=true 直接打开快捷键面板，
        // 或 open_shortcut_settings=true 直接打开「快捷键与布局」管理页。
        if (getIntent().getBooleanExtra("open_shortcut_panel", false)) {
            getIntent().removeExtra("open_shortcut_panel");
            mainHandler.postDelayed(this::showShortcutPanel, 600);
        } else if (getIntent().getBooleanExtra("open_shortcut_settings", false)) {
            getIntent().removeExtra("open_shortcut_settings");
            startActivity(new android.content.Intent(this, ShortcutSettingsActivity.class));
        }
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
        keepConnectionAlive = preferences.getBoolean("keep_connection_alive", true);
        if (preferences.getBoolean("keep_screen_on", false)) {
            getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        } else {
            getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        }
        applyWifiLock();
        requestImmediateLanCheck("App 回到前台");
        if (typelessButton != null) {
            updateVoiceModeInterface();
            renderSharedAudioStatus(PhoneAudioService.getSnapshot());
        }
        if (configRepository != null) {
            refreshAllShortcutViews();
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
        refreshAllShortcutViews();
        refreshTargetSwitcher();
        if (typelessButton != null) {
            updateVoiceModeInterface();
        }
        if (lastFeedbackMessage != null) {
            showActionFeedback(lastFeedbackMessage, lastFeedbackColor);
        }
        if (microphoneLevel != null) {
            renderSharedAudioStatus(PhoneAudioService.getSnapshot());
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
        voiceStatusCaption = null;
        voiceHint = null;
        fleetSummaryBadge = null;
        homeShortcutGrid = null;
        homeEditButton = null;
        homeShortcutSubtitle = null;
        if (shortcutDialog != null) {
            shortcutDialog.dismiss();
            shortcutDialog = null;
        }
        boolean landscape = getResources().getConfiguration().orientation
                == Configuration.ORIENTATION_LANDSCAPE;
        boolean compact = getResources().getConfiguration().screenHeightDp < 740;
        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(theme.background);
        // 横屏时部分机型让内容延伸到状态栏和挖孔下面：按实际系统栏/挖孔留白，竖屏未延伸时为 0。
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            int left;
            int top;
            int right;
            int bottom;
            if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.R) {
                android.graphics.Insets bars = insets.getInsets(android.view.WindowInsets.Type.systemBars()
                        | android.view.WindowInsets.Type.displayCutout());
                left = bars.left;
                top = bars.top;
                right = bars.right;
                bottom = bars.bottom;
            } else {
                left = insets.getSystemWindowInsetLeft();
                top = insets.getSystemWindowInsetTop();
                right = insets.getSystemWindowInsetRight();
                bottom = insets.getSystemWindowInsetBottom();
            }
            view.setPadding(left, top, right, bottom);
            return insets;
        });

        LinearLayout header = new LinearLayout(this);
        header.setOrientation(LinearLayout.VERTICAL);
        header.setPadding(dp(18), dp(10), dp(18), dp(6));
        LinearLayout brandRow = new LinearLayout(this);
        brandRow.setGravity(Gravity.CENTER_VERTICAL);
        TextView brand = text(getString(R.string.brand_name), 21, theme.text, Typeface.BOLD);
        brandRow.addView(brand, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        View headerSpacer = new View(this);
        brandRow.addView(headerSpacer, new LinearLayout.LayoutParams(0, dp(1), 1f));

        fleetSummaryBadge = text("检测中…", 12, theme.muted, Typeface.BOLD);
        fleetSummaryBadge.setSingleLine(true);
        fleetSummaryBadge.setPadding(dp(10), dp(4), dp(10), dp(4));
        fleetSummaryBadge.setBackground(theme.pressable(this, Color.TRANSPARENT, theme.surfaceRaised, 12));
        fleetSummaryBadge.setContentDescription("电脑在线状态，点击立即刷新");
        fleetSummaryBadge.setOnClickListener(view -> {
            showActionFeedback("●  正在刷新电脑状态…", theme.muted);
            runImmediateLanCheck("手动刷新", true);
            scanNearbyComputers();
        });
        brandRow.addView(fleetSummaryBadge, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        Button settings = smallButton("⋯");
        settings.setBackground(theme.pressable(this, Color.TRANSPARENT, theme.surfaceRaised, 16));
        settings.setTextColor(theme.muted);
        settings.setContentDescription("更多：快捷键管理、管理电脑和设置");
        settings.setTextSize(22);
        settings.setOnClickListener(view -> showHomeMenu(settings));
        brandRow.addView(settings, new LinearLayout.LayoutParams(dp(40), dp(38)));
        header.addView(brandRow);

        // 连接文字控件保留用于内部状态与无障碍读屏。
        connectionCard = new LinearLayout(this);
        statusDot = new View(this);
        statusDot.setBackground(roundRect(theme.muted, 20));
        statusText = text("连接你的电脑", 14, theme.text, Typeface.NORMAL);
        statusDetailText = text("正在检测连接…", 11, theme.muted, Typeface.NORMAL);

        computerSection = buildComputerCarousel();
        computerCardRow = (LinearLayout) computerSection.getChildAt(0);
        lastComputerCardsSignature = null;

        // 全部快捷指令二级抽屉面板
        shortcutScroll = new ScrollView(this);
        shortcutScroll.setVerticalScrollBarEnabled(true);
        LinearLayout shortcutPage = new LinearLayout(this);
        shortcutPage.setOrientation(LinearLayout.VERTICAL);
        shortcutPage.setPadding(dp(16), 0, dp(16), dp(12));
        shortcutScroll.addView(shortcutPage);
        LinearLayout shortcutHeader = new LinearLayout(this);
        shortcutHeader.setGravity(Gravity.CENTER_VERTICAL);
        shortcutHeader.addView(text("全部快捷指令", 18, theme.text, Typeface.BOLD),
                new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        Button panelAddBtn = smallButton("＋ 快捷键");
        panelAddBtn.setTextColor(theme.primary);
        panelAddBtn.setSingleLine(true);
        panelAddBtn.setPadding(dp(10), 0, dp(10), 0);
        panelAddBtn.setOnClickListener(view -> startActivity(new Intent(this, ShortcutEditActivity.class)));
        shortcutHeader.addView(panelAddBtn, margins(0, 0, dp(6), 0,
                LinearLayout.LayoutParams.WRAP_CONTENT, dp(36)));
        Button panelAddSlashBtn = smallButton("＋ /指令");
        panelAddSlashBtn.setTextColor(theme.primary);
        panelAddSlashBtn.setSingleLine(true);
        panelAddSlashBtn.setPadding(dp(10), 0, dp(10), 0);
        panelAddSlashBtn.setOnClickListener(view -> {
            Intent intent = new Intent(this, ShortcutEditActivity.class);
            intent.putExtra(ShortcutEditActivity.EXTRA_PRESET_SLASH, true);
            startActivity(intent);
        });
        shortcutHeader.addView(panelAddSlashBtn, margins(0, 0, dp(6), 0,
                LinearLayout.LayoutParams.WRAP_CONTENT, dp(36)));
        gridEditButton = smallButton(gridEditMode ? "完成" : "编辑/删除");
        gridEditButton.setTextColor(gridEditMode ? theme.onPrimary : theme.text);
        gridEditButton.setBackground(theme.pressable(this,
                gridEditMode ? theme.primary : theme.key, theme.primaryPressed, 18));
        gridEditButton.setSingleLine(true);
        gridEditButton.setPadding(dp(12), 0, dp(12), 0);
        gridEditButton.setOnClickListener(view -> toggleGridEditMode());
        installTouchFeedback(gridEditButton);
        shortcutHeader.addView(gridEditButton, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, dp(36)));
        shortcutPage.addView(shortcutHeader);
        shortcutHintText = text("✎ 编辑模式：点右上角 × 删除/隐藏 · 点击卡片修改 · 长按像手机图标拖动调序", 12,
                theme.warning, Typeface.NORMAL);
        shortcutHintText.setVisibility(gridEditMode ? View.VISIBLE : View.GONE);
        shortcutPage.addView(shortcutHintText, marginTop(dp(4)));
        shortcutNormalHintText = text("点按立即发送 · 长按任意图标直接像桌面 App 一样拖动排序并进入编辑", 12,
                theme.muted, Typeface.NORMAL);
        shortcutNormalHintText.setVisibility(gridEditMode ? View.GONE : View.VISIBLE);
        shortcutPage.addView(shortcutNormalHintText, marginTop(dp(4)));
        shortcutGrid = new GridLayout(this);
        shortcutGrid.setColumnCount(3);
        shortcutGrid.setUseDefaultMargins(false);
        shortcutPage.addView(shortcutGrid, margins(dp(-4), dp(6), dp(-4), 0,
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        voiceHalo = null;
        voiceIcon = new MicrophoneGlyphDrawable(this, theme.onPrimary);
        typelessButton = new RoundVoiceButton(this, voiceIcon);
        ((RoundVoiceButton) typelessButton).setGlyphScale(0.30f);
        typelessButton.setTextColor(theme.onPrimary);
        typelessButton.setAllCaps(false);
        typelessButton.setGravity(Gravity.CENTER);
        typelessButton.setPadding(0, 0, 0, 0);
        typelessButton.setBackground(voiceButtonBackground(theme.primary, theme.primaryPressed));
        typelessButton.setElevation(0);
        typelessButton.setStateListAnimator(null);
        typelessButton.setContentDescription("开启或关闭共享麦克风");
        installVoiceGesture();

        voiceButtonCaption = text("开启共享麦克风", 16, theme.text, Typeface.NORMAL);
        voiceStatusCaption = text("●  准备好了", 12, theme.muted, Typeface.NORMAL);
        voiceHint = text("轻点开始，再点结束", 13, theme.muted, Typeface.NORMAL);
        microphoneLevel = text("手机麦克风 · 未启动", 12, theme.muted, Typeface.NORMAL);
        voiceMeter = new VoiceLevelView(this, theme);

        actionFeedback = text("", 12, theme.muted, Typeface.NORMAL);
        actionFeedback.setGravity(Gravity.CENTER);
        actionFeedback.setPadding(dp(12), dp(8), dp(12), dp(8));
        actionFeedback.setVisibility(View.GONE);
        actionFeedback.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        actionFeedback.setOnClickListener(view -> tryUndoRemoveShortcut());

        RoundVoiceButton voiceBar = (RoundVoiceButton) typelessButton;
        voiceBar.setTextSize(16);
        voiceBar.setLetterSpacing(0.05f);
        int barHeight = dp(landscape || compact ? 50 : 56);

        refreshShortcutGrid();
        quickRow = buildQuickRow();
        View thumbEditRow = buildThumbEditRow();
        if (!landscape) {
            LinearLayout middle = new LinearLayout(this);
            middle.setOrientation(LinearLayout.VERTICAL);
            middle.setPadding(0, dp(2), 0, dp(4));
            middle.addView(computerSection, new LinearLayout.LayoutParams(-1, -2));
            middle.addView(actionFeedback, margins(dp(16), dp(6), dp(16), 0, -1, -2));

            FrameLayout quickHolder = new FrameLayout(this);
            FrameLayout.LayoutParams qrParams = new FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.MATCH_PARENT,
                    FrameLayout.LayoutParams.WRAP_CONTENT,
                    Gravity.CENTER_VERTICAL);
            qrParams.setMargins(dp(16), dp(6), dp(16), dp(6));
            quickHolder.addView(quickRow, qrParams);
            middle.addView(quickHolder, new LinearLayout.LayoutParams(-1, 0, 1f));

            ScrollView middleScroll = new ScrollView(this);
            middleScroll.setFillViewport(true);
            middleScroll.setVerticalScrollBarEnabled(false);
            middleScroll.addView(middle);

            LinearLayout footer = new LinearLayout(this);
            footer.setOrientation(LinearLayout.VERTICAL);
            footer.setGravity(Gravity.CENTER_HORIZONTAL);
            footer.setPadding(dp(16), dp(4), dp(16), dp(compact ? 10 : 14));
            footer.addView(voiceMeter, margins(0, 0, 0, dp(6), dp(120), dp(10)));
            footer.addView(thumbEditRow, new LinearLayout.LayoutParams(-1, dp(48)));
            footer.addView(voiceBar, margins(0, dp(8), 0, 0, -1, barHeight));

            LinearLayout page = new LinearLayout(this);
            page.setOrientation(LinearLayout.VERTICAL);
            page.addView(header);
            page.addView(middleScroll, new LinearLayout.LayoutParams(-1, 0, 1f));
            page.addView(footer);
            root.addView(page, new FrameLayout.LayoutParams(-1, -1));
        } else {
            header.setPadding(dp(16), dp(6), dp(12), dp(2));
            LinearLayout left = new LinearLayout(this);
            left.setOrientation(LinearLayout.VERTICAL);
            left.addView(header);
            left.addView(computerSection, new LinearLayout.LayoutParams(-1, -2));
            ScrollView leftScroll = new ScrollView(this);
            leftScroll.setVerticalScrollBarEnabled(false);
            leftScroll.addView(left);

            LinearLayout rightTop = new LinearLayout(this);
            rightTop.setOrientation(LinearLayout.VERTICAL);
            rightTop.addView(actionFeedback, margins(0, 0, 0, dp(6), -1, -2));
            rightTop.addView(quickRow, new LinearLayout.LayoutParams(-1, -2));
            ScrollView rightScroll = new ScrollView(this);
            rightScroll.setVerticalScrollBarEnabled(false);
            rightScroll.addView(rightTop);

            LinearLayout right = new LinearLayout(this);
            right.setOrientation(LinearLayout.VERTICAL);
            right.setPadding(dp(14), dp(8), dp(14), dp(10));
            right.addView(rightScroll, new LinearLayout.LayoutParams(-1, 0, 1f));
            right.addView(thumbEditRow, margins(0, dp(6), 0, 0, -1, dp(46)));
            right.addView(voiceBar, margins(0, dp(8), 0, 0, -1, barHeight));
            View divider = new View(this);
            divider.setBackgroundColor(theme.outline);
            LinearLayout console = new LinearLayout(this);
            console.addView(leftScroll, new LinearLayout.LayoutParams(
                    getResources().getDisplayMetrics().widthPixels * 46 / 100, -1));
            console.addView(divider, new LinearLayout.LayoutParams(dp(1), -1));
            console.addView(right, new LinearLayout.LayoutParams(0, -1, 1f));
            root.addView(console, new FrameLayout.LayoutParams(-1, -1));
        }
        refreshComputerCards();
        return root;
    }

    // ---------- 对话白（全览仪表盘版）：全部电脑状态一屏并列展示 ----------

    private LinearLayout buildComputerCarousel() {
        LinearLayout section = new LinearLayout(this);
        section.setOrientation(LinearLayout.VERTICAL);
        computerCardRow = new LinearLayout(this);
        computerCardRow.setOrientation(LinearLayout.VERTICAL);
        computerCardRow.setPadding(dp(16), dp(2), dp(16), dp(2));
        section.addView(computerCardRow, new LinearLayout.LayoutParams(-1, -2));
        return section;
    }

    private void onComputerCardTap(String computerId) {
        if (targetDeviceManager == null || computerId == null) return;
        TargetDeviceManager.Device device = targetDeviceManager.find(computerId);
        if (device == null) return;
        boolean online = isDeviceOnline(device.computerId);
        boolean rejected = Boolean.TRUE.equals(lanPairingRejected.get(device.computerId));
        if (rejected && !online) {
            showManageComputerActions(device);
            return;
        }
        if (sameComputer(computerId, targetDeviceManager.getActiveComputerId())) {
            showActionFeedback("✓  当前快捷键目标：" + device.slot + "号 · "
                    + device.displayName + "（长按卡片可重命名/管理）", theme.success);
            performResultHaptic(computerCardRow, true);
            requestImmediateLanCheck("点按当前电脑");
            return;
        }
        if (confirmingComputerId != null) {
            return;
        }
        carouselFailedId = null;
        carouselFocusId = device.computerId;
        if (device.hasLanPairing() || online) {
            confirmingComputerId = device.computerId;
            lastComputerCardsSignature = null;
            refreshComputerCards();
            updateVoiceControls();
        }
        selectTargetDevice(device, computerCardRow);
        if (!targetSwitchInFlight && confirmingComputerId != null) {
            finishCarouselConfirmation();
        }
    }

    /// 切换确认结束（成功或失败）：恢复卡片状态并刷新提示。
    private void finishCarouselConfirmation() {
        String tried = confirmingComputerId;
        confirmingComputerId = null;
        carouselFailedId = tried != null && sameComputer(tried, carouselFocusId)
                && !sameComputer(tried, targetDeviceManager.getActiveComputerId()) ? tried : null;
        lastComputerCardsSignature = null;
        refreshComputerCards();
        updateVoiceControls();
    }

    private void updateFleetSummaryAndTargetBadge(java.util.List<TargetDeviceManager.Device> devices) {
        int total = devices.size();
        int onlineCount = 0;
        int capturingCount = 0;
        for (TargetDeviceManager.Device d : devices) {
            if (isDeviceOnline(d.computerId)) {
                onlineCount++;
            }
            if (isEngineCapturingFor(d.computerId)) {
                capturingCount++;
            }
        }
        if (fleetSummaryBadge != null) {
            PhoneAudioService.Snapshot snap = PhoneAudioService.getSnapshot();
            String badgeText;
            int badgeInk;
            if (total == 0) {
                badgeText = "未连接";
                badgeInk = theme.muted;
            } else if (capturingCount > 0) {
                badgeText = "🔴 听写中";
                badgeInk = theme.success;
            } else if (snap.running) {
                badgeText = "● 供音 " + snap.connected + "/" + snap.total;
                badgeInk = snap.connected > 0 ? theme.success : theme.warning;
            } else {
                badgeText = "● " + onlineCount + "/" + total + " 在线";
                badgeInk = onlineCount > 0 ? theme.success : theme.muted;
            }
            fleetSummaryBadge.setText(badgeText);
            fleetSummaryBadge.setTextColor(badgeInk);
        }
    }

    private String computerCardsSignature(java.util.List<TargetDeviceManager.Device> devices) {
        PhoneAudioService.Snapshot audioSnap = PhoneAudioService.getSnapshot();
        java.util.Map<String, String> sharedStates = audioSnap.receiverStates;
        StringBuilder signature = new StringBuilder("S2|")
                .append(targetDeviceManager.getActiveComputerId()).append('|')
                .append(confirmingComputerId).append('|')
                .append(audioSnap.running).append('|')
                .append(audioSnap.connected).append('/').append(audioSnap.total).append('|')
                .append(isVoiceInteractionBusy()).append('|');
        for (String nearbyId : new java.util.TreeSet<>(nearbyComputers.keySet())) {
            signature.append("N:").append(nearbyId).append(',');
        }
        for (TargetDeviceManager.Device device : devices) {
            LanTargetStatus lan = lanTargets.get(device.computerId);
            boolean usbThis = usbConnected && sameComputer(device.computerId, usbComputerId);
            signature.append(device.computerId).append(',').append(device.slot).append(',')
                    .append(device.displayName).append(',').append(device.platform).append(',')
                    .append(device.sharedGroup).append(',').append(lan != null).append(',')
                    .append(usbThis).append(',')
                    .append(lanPairingRejected.get(device.computerId)).append(',')
                    .append(engineNameFor(device.computerId)).append(',')
                    .append(audioReadyFor(device.computerId)).append(',')
                    .append(virtualCableSelectedFor(device.computerId)).append(',')
                    .append(isEngineCapturingFor(device.computerId)).append(',')
                    .append(isAudioStreamingFor(device.computerId)).append(',')
                    .append(lan != null && lan.supports16k).append(',')
                    .append(foregroundAppFor(device.computerId)).append(',')
                    .append(sharedStates.get(device.computerId)).append(';');
        }
        return signature.toString();
    }

    /// 每 2 秒的健康检查都会调用：内容没变就不重建，避免打断点击、长按与读屏焦点。
    private void refreshComputerCards() {
        if (targetDeviceManager == null) return;
        java.util.List<TargetDeviceManager.Device> devices = targetDeviceManager.list();
        updateFleetSummaryAndTargetBadge(devices);
        if (computerCardRow == null) return;
        String signature = computerCardsSignature(devices);
        if (signature.equals(lastComputerCardsSignature) && computerCardRow.getChildCount() > 0) return;
        lastComputerCardsSignature = signature;
        computerCardRow.removeAllViews();
        if (devices.isEmpty() && nearbyComputers.isEmpty()) {
            computerCardRow.addView(emptyComputerCard(), new LinearLayout.LayoutParams(-1, -2));
            return;
        }
        String activeId = targetDeviceManager.getActiveComputerId();
        java.util.Map<String, String> sharedStates = PhoneAudioService.getSnapshot().receiverStates;
        for (int i = 0; i < devices.size(); i++) {
            TargetDeviceManager.Device device = devices.get(i);
            boolean selected = sameComputer(device.computerId, activeId);
            boolean confirming = sameComputer(device.computerId, confirmingComputerId);
            View card = buildComputerStatusCard(
                    device, selected, confirming, sharedStates.get(device.computerId));
            int bottomMargin = (i < devices.size() - 1 || !nearbyComputers.isEmpty()) ? dp(8) : 0;
            computerCardRow.addView(card, margins(0, 0, 0, bottomMargin, -1, -2));
        }
        java.util.List<LanDiscoveryClient.DiscoveredComputer> nearbyList = sortedNearby();
        for (int i = 0; i < nearbyList.size(); i++) {
            int bottomMargin = i < nearbyList.size() - 1 ? dp(6) : 0;
            computerCardRow.addView(nearbyComputerCard(nearbyList.get(i)),
                    margins(0, 0, 0, bottomMargin, -1, -2));
        }
    }

    private String platformLabel(String platform) {
        return "macos".equalsIgnoreCase(platform) ? "macOS"
                : "windows".equalsIgnoreCase(platform) ? "Windows" : platform;
    }

    private String engineNameFor(String computerId) {
        if (computerId == null) return null;
        LanTargetStatus status = lanTargets.get(computerId);
        if (status != null && status.engineName != null) return status.engineName;
        return usbConnected && sameComputer(computerId, usbComputerId) ? usbEngineName : null;
    }

    private boolean audioReadyFor(String computerId) {
        if (computerId == null) return false;
        LanTargetStatus status = lanTargets.get(computerId);
        if (status != null) return status.phoneAudioAvailable;
        return usbConnected && sameComputer(computerId, usbComputerId) && phoneAudioAvailable;
    }

    private Boolean virtualCableSelectedFor(String computerId) {
        if (computerId == null) return null;
        LanTargetStatus status = lanTargets.get(computerId);
        if (status != null) return status.typelessVirtualCableSelected;
        return usbConnected && sameComputer(computerId, usbComputerId) ? typelessVirtualCableSelected : null;
    }

    private boolean isEngineCapturingFor(String computerId) {
        if (computerId == null) return false;
        LanTargetStatus status = lanTargets.get(computerId);
        if (status != null) return status.engineCapturing;
        return usbConnected && sameComputer(computerId, usbComputerId) && usbEngineCapturing;
    }

    private boolean isAudioStreamingFor(String computerId) {
        if (computerId == null) return false;
        LanTargetStatus status = lanTargets.get(computerId);
        if (status != null) return status.audioStreaming;
        return usbConnected && sameComputer(computerId, usbComputerId) && usbAudioStreaming;
    }

    private String foregroundAppFor(String computerId) {
        if (computerId == null) return null;
        LanTargetStatus status = lanTargets.get(computerId);
        if (status != null && status.foregroundApp != null) return status.foregroundApp;
        return usbConnected && sameComputer(computerId, usbComputerId) ? usbForegroundApp : null;
    }

    private static String cleanComputerName(String raw) {
        if (raw == null || raw.isBlank()) return "电脑";
        String s = raw.trim();
        if (s.endsWith(".local")) {
            s = s.substring(0, s.length() - 6);
        }
        if (s.startsWith("wanglizoude") && s.length() > "wanglizoude".length()) {
            s = s.substring("wanglizoude".length());
        }
        return s.replace('-', ' ');
    }

    private static boolean isSystemNoiseWindow(String app) {
        if (app == null || app.isBlank()) return true;
        String lower = app.trim().toLowerCase(java.util.Locale.ROOT);
        return "dwm".equals(lower)
                || "explorer".equals(lower)
                || "searchhost".equals(lower)
                || "shellexperiencehost".equals(lower)
                || "applicationframehost".equals(lower)
                || "lockapp".equals(lower)
                || "idle".equals(lower);
    }

    /// 极简全状态电脑卡片：无冗余边框与次要按钮，一眼区分当前目标、在线状态、虚拟声卡与前台窗口。
    private View buildComputerStatusCard(
            TargetDeviceManager.Device device,
            boolean selected,
            boolean confirming,
            String sharedState) {
        LanTargetStatus lanStatus = lanTargets.get(device.computerId);
        boolean usbThis = usbConnected && sameComputer(device.computerId, usbComputerId);
        boolean online = lanStatus != null || usbThis;
        boolean rejected = !online && Boolean.TRUE.equals(lanPairingRejected.get(device.computerId));
        boolean audioReady = audioReadyFor(device.computerId);
        Boolean cableSelected = virtualCableSelectedFor(device.computerId);
        boolean hasAudioWarning = online && (!audioReady || Boolean.FALSE.equals(cableSelected));
        boolean capturing = isEngineCapturingFor(device.computerId);
        boolean micRunning = PhoneAudioService.getSnapshot().running;

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(14), dp(11), dp(14), dp(11));

        int cardFill = selected || confirming
                ? theme.mix(theme.surface, theme.primaryContainer, 0.35f)
                : theme.surface;
        int borderStroke = selected || confirming ? 2 : 0;
        int borderColor = confirming ? theme.warning
                : selected ? theme.primary
                : Color.TRANSPARENT;
        card.setBackground(theme.pressable(
                this, cardFill, theme.surfaceRaised, 16, borderStroke, borderColor));
        card.setAlpha(online || selected || confirming || rejected ? 1f : 0.72f);

        // 第一行：状态圆点 + 电脑名 +（当前标签）+ 右侧单个共享开关胶囊
        LinearLayout top = new LinearLayout(this);
        top.setGravity(Gravity.CENTER_VERTICAL);

        int dotColor = confirming || rejected || hasAudioWarning ? theme.warning
                : online ? theme.success : theme.muted;
        View dot = new View(this);
        dot.setBackground(roundRect(dotColor, 20));
        top.addView(dot, new LinearLayout.LayoutParams(dp(8), dp(8)));

        String cleanName = device.nameLocked ? device.displayName : cleanComputerName(device.displayName);
        TextView nameView = text(
                device.slot + " · " + cleanName,
                15,
                selected || online ? theme.text : theme.muted,
                Typeface.BOLD);
        nameView.setSingleLine(true);
        nameView.setEllipsize(TextUtils.TruncateAt.END);
        LinearLayout.LayoutParams nameParams = new LinearLayout.LayoutParams(0, -2, 1f);
        nameParams.leftMargin = dp(8);
        nameParams.rightMargin = dp(8);
        top.addView(nameView, nameParams);

        if (selected || confirming) {
            TextView activeBadge = text(
                    confirming ? "切换中" : "当前",
                    10,
                    confirming ? theme.warning : theme.onPrimary,
                    Typeface.BOLD);
            activeBadge.setSingleLine(true);
            activeBadge.setPadding(dp(7), dp(2), dp(7), dp(2));
            activeBadge.setBackground(roundRect(confirming ? theme.warningContainer : theme.primary, 8));
            top.addView(activeBadge, margins(0, 0, dp(6), 0, -2, -2));
        }

        boolean streamingNow = device.sharedGroup && micRunning
                && ((sharedState != null && sharedState.contains("正在供音"))
                || isAudioStreamingFor(device.computerId));
        String sharedLabel;
        int sharedInk;
        int sharedBg;
        if (streamingNow) {
            sharedLabel = "🎙 供音中";
            sharedInk = theme.success;
            sharedBg = theme.successContainer;
        } else if (device.sharedGroup) {
            sharedLabel = "✓ 共享";
            sharedInk = online ? theme.success : theme.muted;
            sharedBg = online
                    ? theme.mix(theme.surfaceRaised, theme.successContainer, 0.55f)
                    : theme.surfaceRaised;
        } else {
            sharedLabel = "未共享";
            sharedInk = theme.muted;
            sharedBg = theme.surfaceRaised;
        }
        TextView sharedPill = text(sharedLabel, 11, sharedInk, Typeface.BOLD);
        sharedPill.setSingleLine(true);
        sharedPill.setPadding(dp(8), dp(3), dp(8), dp(3));
        sharedPill.setBackground(theme.pressable(this, sharedBg, theme.outline, 10));
        sharedPill.setContentDescription((device.sharedGroup ? "移出共享组：" : "加入共享组：") + device.displayName);
        sharedPill.setOnClickListener(view -> toggleSharedGroupFromCard(device));
        top.addView(sharedPill, new LinearLayout.LayoutParams(-2, -2));

        card.addView(top, new LinearLayout.LayoutParams(-1, -2));

        // 第二行：极简状态行（连接通道 · 语音状态 · 活动应用）
        java.util.List<String> telemetry = new java.util.ArrayList<>();
        if (lanStatus != null && usbThis) {
            telemetry.add("Wi-Fi + USB");
        } else if (lanStatus != null) {
            telemetry.add("Wi-Fi");
        } else if (usbThis) {
            telemetry.add("USB");
        } else if (rejected) {
            telemetry.add("配对失效 · 点按重配");
        } else {
            telemetry.add("离线");
        }

        if (online) {
            if (capturing) {
                telemetry.add("🔴 正在听写");
            } else if (!audioReady) {
                telemetry.add("声卡未就绪");
            } else if (Boolean.FALSE.equals(cableSelected)) {
                telemetry.add("Typeless 未选虚拟麦");
            } else {
                telemetry.add("就绪");
            }
            String fg = foregroundAppFor(device.computerId);
            if (!isSystemNoiseWindow(fg)) {
                telemetry.add(fg.trim());
            }
        }

        String telemetryLine = String.join("  ·  ", telemetry);
        int telemetryColor = capturing ? theme.success
                : (rejected || hasAudioWarning) ? theme.warning
                : online ? theme.muted : theme.muted;
        TextView detailView = text(
                telemetryLine,
                12,
                telemetryColor,
                capturing ? Typeface.BOLD : Typeface.NORMAL);
        detailView.setSingleLine(true);
        detailView.setEllipsize(TextUtils.TruncateAt.END);
        card.addView(detailView, margins(dp(16), dp(3), 0, 0, -1, -2));

        final String tappedId = device.computerId;
        card.setContentDescription(device.slot + "号电脑 " + device.displayName + "，"
                + (selected ? "当前目标，" : "") + sharedLabel + "，" + telemetryLine);
        card.setFocusable(true);
        card.setOnClickListener(view -> onComputerCardTap(tappedId));
        card.setOnLongClickListener(view -> {
            view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
            showManageComputerActions(device);
            return true;
        });
        installTouchFeedback(card);
        return card;
    }

    private View emptyComputerCard() {
        LinearLayout empty = new LinearLayout(this);
        empty.setOrientation(LinearLayout.VERTICAL);
        empty.setPadding(dp(16), dp(14), dp(16), dp(14));
        GradientDrawable dashed = theme.shape(this, Color.TRANSPARENT, 18);
        dashed.setStroke(dp(1), theme.outline, dp(6), dp(4));
        empty.setBackground(dashed);
        LinearLayout top = new LinearLayout(this);
        top.setGravity(Gravity.CENTER_VERTICAL);
        top.addView(new DeckIconView(this, "laptop", theme.muted), new LinearLayout.LayoutParams(dp(22), dp(22)));
        top.addView(text("连接第一台电脑", 16, theme.text, Typeface.BOLD), margins(dp(8), 0, 0, 0, 0, -2));
        ((LinearLayout.LayoutParams) top.getChildAt(1).getLayoutParams()).weight = 1f;
        Button retry = smallButton("查找电脑");
        retry.setTextColor(theme.onPrimary);
        retry.setBackground(theme.pressable(this, theme.primary, theme.primaryPressed, 14));
        retry.setPadding(dp(14), 0, dp(14), 0);
        retry.setOnClickListener(view -> {
            showActionFeedback("●  正在查找同一 Wi-Fi 里的电脑…", theme.muted);
            scanNearbyComputers();
        });
        installTouchFeedback(retry);
        top.addView(retry, new LinearLayout.LayoutParams(-2, dp(34)));
        empty.addView(top);
        TextView body = text("在电脑上打开言渡接收端并连同一 Wi-Fi，电脑会自动出现在这里。", 12, theme.muted, Typeface.NORMAL);
        empty.addView(body, marginTop(dp(6)));
        return empty;
    }

    /// 附近（同一 Wi-Fi）尚未连接的电脑：紧凑虚线横条，点按请求连接，电脑上点「允许」后加入。
    private View nearbyComputerCard(LanDiscoveryClient.DiscoveredComputer computer) {
        LinearLayout card = new LinearLayout(this);
        card.setGravity(Gravity.CENTER_VERTICAL);
        card.setPadding(dp(12), dp(9), dp(12), dp(9));
        GradientDrawable dashed = theme.shape(this, Color.TRANSPARENT, 14);
        dashed.setStroke(dp(1), theme.primary, dp(6), dp(4));
        card.setBackground(new android.graphics.drawable.RippleDrawable(
                android.content.res.ColorStateList.valueOf(theme.surfaceRaised), dashed, null));
        card.addView(new DeckIconView(this, "macos".equalsIgnoreCase(computer.platform) ? "laptop" : "devices",
                theme.primary), new LinearLayout.LayoutParams(dp(18), dp(18)));
        TextView name = text("发现附近电脑：" + computer.displayName, 13, theme.text, Typeface.BOLD);
        name.setSingleLine(true);
        name.setEllipsize(TextUtils.TruncateAt.END);
        card.addView(name, margins(dp(8), 0, dp(8), 0, 0, -2));
        ((LinearLayout.LayoutParams) name.getLayoutParams()).weight = 1f;
        TextView connectBadge = text("点按配对 ›", 11, theme.primary, Typeface.BOLD);
        card.addView(connectBadge);
        card.setContentDescription("附近的电脑 " + computer.displayName + "，点按连接，然后在电脑上点允许");
        card.setFocusable(true);
        card.setOnClickListener(view -> startNearbyPairing(computer));
        installTouchFeedback(card);
        return card;
    }

    // ---------- 快捷与指令矩阵（4×3 完整对称网格：含常用按键与常用斜杠指令，无孤立空位） ----------

    private View buildQuickRow() {
        LinearLayout section = new LinearLayout(this);
        section.setOrientation(LinearLayout.VERTICAL);

        LinearLayout headerRow = new LinearLayout(this);
        headerRow.setGravity(Gravity.CENTER_VERTICAL);
        TextView title = text("快捷指令", 14, theme.muted, Typeface.BOLD);
        headerRow.addView(title, new LinearLayout.LayoutParams(0, -2, 1f));

        homeEditButton = smallButton(gridEditMode ? "完成" : "编辑");
        homeEditButton.setTextSize(12);
        homeEditButton.setTextColor(gridEditMode ? theme.primary : theme.muted);
        homeEditButton.setBackground(theme.pressable(this, Color.TRANSPARENT, theme.surfaceRaised, 10));
        homeEditButton.setPadding(dp(10), dp(2), dp(10), dp(2));
        homeEditButton.setOnClickListener(view -> toggleGridEditMode());
        headerRow.addView(homeEditButton, new LinearLayout.LayoutParams(-2, dp(30)));

        Button allBtn = smallButton("全部 ›");
        allBtn.setTextSize(12);
        allBtn.setTextColor(theme.muted);
        allBtn.setBackground(theme.pressable(this, Color.TRANSPARENT, theme.surfaceRaised, 10));
        allBtn.setPadding(dp(8), dp(2), dp(4), dp(2));
        allBtn.setOnClickListener(view -> showShortcutPanel());
        headerRow.addView(allBtn, new LinearLayout.LayoutParams(-2, dp(30)));

        section.addView(headerRow, new LinearLayout.LayoutParams(-1, -2));

        homeShortcutSubtitle = text(
                "✎ 拖动图标调序 · 点右上角 × 删除 · 点击图标改键",
                11, theme.warning, Typeface.NORMAL);
        homeShortcutSubtitle.setSingleLine(true);
        homeShortcutSubtitle.setEllipsize(TextUtils.TruncateAt.END);
        homeShortcutSubtitle.setVisibility(gridEditMode ? View.VISIBLE : View.GONE);
        section.addView(homeShortcutSubtitle, margins(0, dp(4), 0, 0, -1, -2));

        homeShortcutGrid = new GridLayout(this);
        homeShortcutGrid.setColumnCount(4);
        homeShortcutGrid.setUseDefaultMargins(false);
        section.addView(homeShortcutGrid, margins(dp(-4), dp(4), dp(-4), 0, -1, -2));

        refreshHomeShortcuts();
        return section;
    }

    private void refreshHomeShortcuts() {
        if (homeShortcutGrid == null || configRepository == null) {
            return;
        }
        homeShortcutGrid.removeAllViews();
        int maxHomeShortcuts = 12;
        java.util.List<ShortcutButtonConfig> regularPicks = new java.util.ArrayList<>();
        java.util.List<ShortcutButtonConfig> slashPicks = new java.util.ArrayList<>();
        try {
            for (ShortcutButtonConfig config : configRepository.load()) {
                if (!config.visible
                        || "enter".equals(config.id)
                        || "backspace".equals(config.id)) {
                    continue;
                }
                boolean isSlash = config.isTextAction() && config.text != null
                        && config.text.trim().startsWith("/");
                if (isSlash) {
                    if (!"/".equals(config.text.trim())) {
                        slashPicks.add(config);
                    }
                } else {
                    regularPicks.add(config);
                }
            }
        } catch (Exception ignored) {
            // 配置损坏时保留添加入口。
        }
        java.util.List<ShortcutButtonConfig> picks = new java.util.ArrayList<>();
        for (ShortcutButtonConfig config : regularPicks) {
            if (picks.size() >= maxHomeShortcuts) break;
            picks.add(config);
        }
        for (ShortcutButtonConfig config : slashPicks) {
            if (picks.size() >= maxHomeShortcuts) break;
            picks.add(config);
        }
        for (ShortcutButtonConfig config : picks) {
            addHomeKey(homeShortcutGrid, config);
        }
        if (gridEditMode || picks.isEmpty()) {
            addPlusTile(homeShortcutGrid, true);
        }
    }

    private void addHomeKey(GridLayout grid, ShortcutButtonConfig config) {
        ShortcutKeyView button = new ShortcutKeyView(this, theme, config, true);
        button.setTag(config.id);
        button.setContentDescription(config.label + "，" + config.subtitle()
                + "。点按发送，长按拖动排序或编辑删除");
        button.setEditMode(gridEditMode, () -> removeShortcutWithUndo(config));
        button.setOnClickListener(view -> {
            if (gridEditMode) {
                openButtonEditor(config.id);
            } else {
                triggerShortcut(button, config);
            }
        });
        button.setOnDragListener((view, event) -> handleGridDrag(view, event));
        TouchFeedback.installHoldable(button, this::stopKeyRepeat,
                () -> onGridButtonHeld(button, config),
                () -> startDragFromRepeat(button, config));

        int tileHeightDp = getResources().getConfiguration().screenHeightDp >= 780 ? 74 : 64;
        GridLayout.LayoutParams params = new GridLayout.LayoutParams();
        params.width = 0;
        params.height = dp(Math.round(tileHeightDp * Math.max(1f,
                getResources().getConfiguration().fontScale)));
        params.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
        params.setMargins(dp(4), dp(4), dp(4), dp(4));
        grid.addView(button, params);
    }

    /// 底部拇指快捷编辑栏：大尺寸退格（长按全删）、回车与 `/ 指令` 面板入口，无横向截断。
    private View buildThumbEditRow() {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);

        Button slashBtn = smallButton("/ 指令");
        slashBtn.setTextSize(13);
        slashBtn.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        slashBtn.setTextColor(theme.text);
        slashBtn.setBackground(theme.pressable(this, theme.surface, theme.surfaceRaised, 14));
        slashBtn.setContentDescription("打开全部斜杠指令与新增");
        slashBtn.setOnClickListener(view -> showCommandSheet());
        row.addView(slashBtn, new LinearLayout.LayoutParams(dp(76), LinearLayout.LayoutParams.MATCH_PARENT));

        Button backspace = smallButton("⌫  退格");
        backspace.setTextSize(14);
        backspace.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        backspace.setTextColor(theme.text);
        backspace.setBackground(theme.pressable(this, theme.surface, theme.surfaceRaised, 14));
        backspace.setContentDescription("退格。点按删除一个字符，长按全部删除");
        backspace.setOnClickListener(view -> triggerVoiceEditAction(backspace, "退格", "BACKSPACE", "backspace"));
        backspace.setOnLongClickListener(view -> {
            view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
            triggerDeleteAll(backspace);
            return true;
        });
        row.addView(backspace, margins(dp(8), 0, dp(8), 0, 0, LinearLayout.LayoutParams.MATCH_PARENT));
        ((LinearLayout.LayoutParams) backspace.getLayoutParams()).weight = 1f;

        Button enter = smallButton("↵  回车");
        enter.setTextSize(14);
        enter.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        enter.setTextColor(theme.text);
        enter.setBackground(theme.pressable(this, theme.surface, theme.surfaceRaised, 14));
        enter.setContentDescription("发送回车键");
        enter.setOnClickListener(view -> triggerVoiceEditAction(enter, "回车", "ENTER", "enter"));
        row.addView(enter, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MATCH_PARENT, 1f));

        return row;
    }

    private void addPlusTile(GridLayout grid, boolean compact) {
        LinearLayout add = new LinearLayout(this);
        add.setOrientation(LinearLayout.VERTICAL);
        add.setGravity(Gravity.CENTER);
        GradientDrawable dashed = theme.shape(this, Color.TRANSPARENT, compact ? 14 : 18);
        dashed.setStroke(dp(1), theme.outline, dp(5), dp(4));
        add.setBackground(new android.graphics.drawable.RippleDrawable(
                android.content.res.ColorStateList.valueOf(theme.surfaceRaised), dashed, null));
        TextView plus = text("＋", compact ? 17 : 22, theme.primary, Typeface.BOLD);
        plus.setGravity(Gravity.CENTER);
        add.addView(plus);
        TextView label = text("添加指令", compact ? 10 : 12, theme.muted, Typeface.NORMAL);
        label.setGravity(Gravity.CENTER);
        add.addView(label, marginTop(dp(1)));
        add.setContentDescription("添加自定义快捷指令");
        add.setOnClickListener(view -> startActivity(new Intent(this, ShortcutEditActivity.class)));
        installTouchFeedback(add);

        GridLayout.LayoutParams params = new GridLayout.LayoutParams();
        params.width = 0;
        params.height = dp(Math.round((compact ? 58 : 88) * Math.max(1f,
                getResources().getConfiguration().fontScale)));
        params.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
        params.setMargins(dp(compact ? 3 : 4), dp(compact ? 3 : 4), dp(compact ? 3 : 4), dp(compact ? 3 : 4));
        grid.addView(add, params);
    }

    // ---------- 斜杠指令菜单 ----------

    private java.util.List<ShortcutButtonConfig> slashCommands() {
        java.util.List<ShortcutButtonConfig> commands = new java.util.ArrayList<>();
        try {
            for (ShortcutButtonConfig config : configRepository.load()) {
                if (config.visible && config.isTextAction() && config.text != null
                        && config.text.trim().startsWith("/")) {
                    commands.add(config);
                }
            }
        } catch (Exception ignored) {
            // 配置损坏时指令栏只保留退格与回车。
        }
        return commands;
    }

    private void showCommandSheet() {
        java.util.List<ShortcutButtonConfig> commands = slashCommands();
        String target = targetDisplayName == null ? "当前电脑" : targetDisplayName;
        String[] labels = new String[commands.size()];
        for (int i = 0; i < commands.size(); i++) {
            labels[i] = commands.get(i).text.trim() + "    " + commands.get(i).label
                    + (commands.get(i).submitText ? "" : "（只输入）");
        }
        AlertDialog.Builder builder = new AlertDialog.Builder(this).setTitle("斜杠指令 · 输入到 " + target);
        if (commands.isEmpty()) {
            builder.setMessage("还没有 /指令。点击「＋ 新增 /指令」即可一键添加常用斜杠指令。");
        } else {
            builder.setItems(labels, (dialog, which) -> triggerShortcut(
                    computerCardRow != null ? computerCardRow : typelessButton, commands.get(which)));
        }
        builder.setPositiveButton("＋ 新增 /指令", (dialog, which) -> {
                    Intent intent = new Intent(this, ShortcutEditActivity.class);
                    intent.putExtra(ShortcutEditActivity.EXTRA_PRESET_SLASH, true);
                    startActivity(intent);
                })
                .setNeutralButton("排序与管理", (dialog, which) ->
                        startActivity(new Intent(this, ShortcutSettingsActivity.class)))
                .setNegativeButton("关闭", null).show();
    }

    private void showHomeMenu(View anchor) {
        android.widget.PopupMenu menu = new android.widget.PopupMenu(this, anchor);
        menu.getMenu().add(1, 1, 1, "全部快捷指令面板");
        menu.getMenu().add(1, 5, 2, "快捷键与布局管理");
        menu.getMenu().add(1, 3, 3, "管理电脑");
        menu.getMenu().add(1, 2, 4, "设置");
        menu.setOnMenuItemClickListener(item -> {
            if (item.getItemId() == 1) showShortcutPanel();
            else if (item.getItemId() == 5) startActivity(new Intent(this, ShortcutSettingsActivity.class));
            else if (item.getItemId() == 3) showManageComputersDialog();
            else if (item.getItemId() == 2) startActivity(new Intent(this, SettingsActivity.class));
            return true;
        });
        menu.show();
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
        refreshShortcutGrid();
        shortcutDialog = new android.app.Dialog(this);
        shortcutDialog.requestWindowFeature(android.view.Window.FEATURE_NO_TITLE);
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(0, dp(12), 0, dp(8));
        shortcutPanelFeedback = text("点按发送 · 长按任意图标可拖动排序或点 × 删除", 12, theme.muted, Typeface.NORMAL);
        shortcutPanelFeedback.setPadding(dp(20), dp(8), dp(20), dp(8));
        shortcutPanelFeedback.setMaxLines(3);
        shortcutPanelFeedback.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        shortcutPanelFeedback.setOnClickListener(view -> tryUndoRemoveShortcut());
        panel.addView(shortcutPanelFeedback);
        panel.addView(shortcutScroll, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));
        LinearLayout bottomRow = new LinearLayout(this);
        bottomRow.setOrientation(LinearLayout.HORIZONTAL);
        Button manageAll = smallButton("列表管理 / 恢复默认");
        manageAll.setTextColor(theme.body);
        manageAll.setBackground(theme.pressable(this, theme.background, theme.outline, 14, 1, theme.outline));
        manageAll.setOnClickListener(view -> {
            shortcutDialog.dismiss();
            startActivity(new Intent(this, ShortcutSettingsActivity.class));
        });
        bottomRow.addView(manageAll, new LinearLayout.LayoutParams(0, dp(48), 1f));
        Button close = smallButton("返回语音");
        close.setTextColor(theme.onPrimary);
        close.setBackground(theme.pressable(this, theme.primary, theme.primaryPressed, 14));
        close.setOnClickListener(view -> shortcutDialog.dismiss());
        bottomRow.addView(close, margins(dp(8), 0, 0, 0, 0, dp(48)));
        ((LinearLayout.LayoutParams) close.getLayoutParams()).weight = 1f;
        panel.addView(bottomRow, margins(dp(16), dp(8), dp(16), 0,
                LinearLayout.LayoutParams.MATCH_PARENT, dp(48)));
        shortcutDialog.setContentView(panel);
        shortcutDialog.setCanceledOnTouchOutside(true);
        shortcutDialog.setOnDismissListener(dialog -> stopKeyRepeat());
        shortcutDialog.show();
        android.view.Window window = shortcutDialog.getWindow();
        if (window != null) {
            window.setGravity(Gravity.BOTTOM);
            theme.applySheetGlass(this, window, 20);
            window.setLayout(android.view.ViewGroup.LayoutParams.MATCH_PARENT,
                    getResources().getDisplayMetrics().heightPixels * 82 / 100);
        }
    }

    private void refreshAllShortcutViews() {
        refreshShortcutGrid();
        refreshHomeShortcuts();
        syncEditModeChrome();
    }

    private void refreshShortcutGrid() {
        if (shortcutGrid == null || configRepository == null) {
            return;
        }
        shortcutGrid.removeAllViews();
        try {
            for (ShortcutButtonConfig config : configRepository.load()) {
                if (config.visible) {
                    addKey(shortcutGrid, config);
                }
            }
            addPlusTile(shortcutGrid, false);
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
        ShortcutKeyView button = new ShortcutKeyView(this, theme, config, false);
        boolean deleteAllOnLongPress = isBackspaceKey(config);
        boolean repeatable = isRepeatableKey(config);
        button.setTag(config.id);
        button.setContentDescription(config.label + "，" + config.subtitle()
                + (deleteAllOnLongPress
                ? "。点按删除一个字符，长按全部删除"
                : repeatable
                ? "。点按发送一次，长按连续发送，按住后滑动可直接拖动调位"
                : "。点按发送，长按拖动调换位置"));
        button.setEditMode(gridEditMode, () -> removeShortcutWithUndo(config));
        button.setOnClickListener(view -> {
            if (gridEditMode) {
                openButtonEditor(config.id);
            } else {
                triggerShortcut(button, config);
            }
        });
        button.setOnDragListener((view, event) -> handleGridDrag(view, event));
        // 手势自管：系统长按遇微动即取消并退化为滚动，改为自行计时（见 TouchFeedback.installHoldable）。
        TouchFeedback.installHoldable(button, this::stopKeyRepeat,
                () -> onGridButtonHeld(button, config),
                () -> startDragFromRepeat(button, config));

        GridLayout.LayoutParams params = new GridLayout.LayoutParams();
        params.width = 0;
        params.height = dp(Math.round(88 * Math.max(1f,
                getResources().getConfiguration().fontScale)));
        params.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
        params.setMargins(dp(4), dp(4), dp(4), dp(4));
        grid.addView(button, params);
    }

    /// 按钮按住到点：退格全删、连发键连发、其余直接像手机桌面图标一样进入编辑并浮起拖动调位。
    private void onGridButtonHeld(ShortcutKeyView button, ShortcutButtonConfig config) {
        android.util.Log.d("PhoneDeckDrag", "held " + config.id + " edit=" + gridEditMode);
        button.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
        if (!gridEditMode && isBackspaceKey(config)) {
            triggerDeleteAll(button);
            return;
        }
        if (!gridEditMode && isRepeatableKey(config)) {
            startKeyRepeat(button, config);
            return;
        }
        enableEditModeInPlace();
        startGridButtonDrag(button, config);
    }

    /// 以手机桌面图标的方式拖动调位：普通与编辑模式共用，落点换位由 handleGridDrag 处理。
    private void startGridButtonDrag(View source, ShortcutButtonConfig config) {
        ClipData data = ClipData.newPlainText("PhoneDeckButtonId", config.id);
        android.util.Log.d("PhoneDeckDrag", "startDragAndDrop " + config.id);
        source.startDragAndDrop(data, new View.DragShadowBuilder(source), null, 0);
    }

    /// 连发进行中手指滑出按钮：停止连发，转为拖动调位。
    private void startDragFromRepeat(ShortcutKeyView source, ShortcutButtonConfig config) {
        android.util.Log.d("PhoneDeckDrag", "drag-from-repeat " + config.id
                + " repeatActive=" + (repeatSource != null));
        if (repeatSource == null) {
            return;
        }
        stopKeyRepeat();
        source.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
        enableEditModeInPlace();
        startGridButtonDrag(source, config);
    }

    /// 长按启动拖动时原地开启编辑态（显示 × 角标），不重建当前正在被拖动的 View。
    private void enableEditModeInPlace() {
        if (gridEditMode) {
            return;
        }
        gridEditMode = true;
        syncEditModeChrome();
        updateExistingKeysEditMode();
    }

    private void updateExistingKeysEditMode() {
        java.util.Map<String, ShortcutButtonConfig> map = new java.util.HashMap<>();
        try {
            for (ShortcutButtonConfig config : configRepository.load()) {
                map.put(config.id, config);
            }
        } catch (Exception ignored) {
        }
        for (GridLayout grid : new GridLayout[]{homeShortcutGrid, shortcutGrid}) {
            if (grid == null) continue;
            for (int i = 0; i < grid.getChildCount(); i++) {
                View child = grid.getChildAt(i);
                if (child instanceof ShortcutKeyView && child.getTag() instanceof String) {
                    ShortcutButtonConfig cfg = map.get((String) child.getTag());
                    if (cfg != null) {
                        ((ShortcutKeyView) child).setEditMode(gridEditMode, () -> removeShortcutWithUndo(cfg));
                    }
                }
            }
        }
    }

    private void syncEditModeChrome() {
        if (gridEditButton != null) {
            gridEditButton.setText(gridEditMode ? "完成" : "编辑/删除");
            gridEditButton.setTextColor(gridEditMode ? theme.onPrimary : theme.body);
            gridEditButton.setBackground(theme.pressable(this,
                    gridEditMode ? theme.primary : theme.surface, theme.primaryPressed, 14, 1, theme.outline));
        }
        if (homeEditButton != null) {
            homeEditButton.setText(gridEditMode ? "完成" : "编辑");
            homeEditButton.setTextColor(gridEditMode ? theme.onPrimary : theme.muted);
            homeEditButton.setBackground(theme.pressable(this,
                    gridEditMode ? theme.primary : Color.TRANSPARENT, theme.surfaceRaised, 10));
        }
        if (homeShortcutSubtitle != null) {
            homeShortcutSubtitle.setText("✎ 拖动图标调序 · 点右上角 × 删除 · 点击图标改键");
            homeShortcutSubtitle.setTextColor(theme.warning);
            homeShortcutSubtitle.setVisibility(gridEditMode ? View.VISIBLE : View.GONE);
        }
        if (shortcutHintText != null) {
            shortcutHintText.setVisibility(gridEditMode ? View.VISIBLE : View.GONE);
        }
        if (shortcutNormalHintText != null) {
            shortcutNormalHintText.setVisibility(gridEditMode ? View.GONE : View.VISIBLE);
        }
    }

    private void toggleGridEditMode() {
        gridEditMode = !gridEditMode;
        stopKeyRepeat();
        if (gridEditButton != null) {
            performResultHaptic(gridEditButton, true);
        }
        refreshAllShortcutViews();
        showActionFeedback(gridEditMode
                ? "✎  编辑模式：点右上角 × 可删除，点击图标可改键，长按可拖动排序"
                : "●  已完成快捷指令编辑", gridEditMode ? theme.warning : theme.muted);
    }

    private boolean handleGridDrag(View target, DragEvent event) {
        if (event.getAction() == DragEvent.ACTION_DRAG_STARTED) {
            return event.getClipDescription() != null
                    && "PhoneDeckButtonId".equals(event.getClipDescription().getLabel());
        }
        if (event.getAction() == DragEvent.ACTION_DRAG_ENTERED) {
            target.setAlpha(0.45f);
            target.setScaleX(0.95f);
            target.setScaleY(0.95f);
            return true;
        }
        if (event.getAction() == DragEvent.ACTION_DRAG_EXITED
                || event.getAction() == DragEvent.ACTION_DRAG_ENDED) {
            target.setAlpha(1f);
            target.setScaleX(1f);
            target.setScaleY(1f);
            return true;
        }
        if (event.getAction() == DragEvent.ACTION_DROP) {
            target.setAlpha(1f);
            target.setScaleX(1f);
            target.setScaleY(1f);
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
        if (sourceId == null || targetId == null || sourceId.equals(targetId)) {
            return;
        }
        try {
            if (configRepository.moveToSlot(sourceId, targetId)) {
                refreshAllShortcutViews();
                showActionFeedback("✓  已调整指令顺序", theme.success);
            }
        } catch (Exception exception) {
            showActionFeedback("✕  保存按钮顺序失败", theme.danger);
        }
    }

    private void removeShortcutWithUndo(ShortcutButtonConfig config) {
        try {
            java.util.ArrayList<ShortcutButtonConfig> snapshot = configRepository.removeOrHide(config.id);
            if (snapshot == null) {
                return;
            }
            lastRemovedSnapshot = snapshot;
            mainHandler.removeCallbacks(clearUndoSnapshot);
            mainHandler.postDelayed(clearUndoSnapshot, 6_000);
            refreshAllShortcutViews();
            String actionLabel = config.builtIn ? "已隐藏" : "已删除";
            showActionFeedback("✓  " + actionLabel + "「" + config.label + "」 · 点此撤销", theme.warning);
        } catch (Exception exception) {
            showActionFeedback("✕  删除快捷指令失败", theme.danger);
        }
    }

    private void tryUndoRemoveShortcut() {
        if (lastRemovedSnapshot == null) {
            return;
        }
        try {
            configRepository.save(lastRemovedSnapshot);
            lastRemovedSnapshot = null;
            mainHandler.removeCallbacks(clearUndoSnapshot);
            refreshAllShortcutViews();
            showActionFeedback("✓  已恢复刚才移除的指令", theme.success);
        } catch (Exception exception) {
            showActionFeedback("✕  撤销失败", theme.danger);
        }
    }

    private void openButtonEditor(String buttonId) {
        Intent intent = new Intent(this, ShortcutEditActivity.class);
        intent.putExtra(ShortcutEditActivity.EXTRA_BUTTON_ID, buttonId);
        startActivity(intent);
    }

    private boolean isRepeatableKey(ShortcutButtonConfig config) {
        if (config.isTextAction() || config.isMacroAction()
                || config.keys == null || config.keys.isEmpty()) {
            return false;
        }
        if (config.keys.size() == 1) {
            return REPEATABLE_KEYS.contains(config.keys.get(0));
        }
        return config.keys.size() == 2
                && REPEATABLE_CHORDS.contains(String.join(",", config.keys));
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
                    showConnection("发送失败，请检查 Wi-Fi 或 USB 连接", theme.danger);
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
                    showConnection("发送失败，请检查 Wi-Fi 或 USB 连接", theme.danger);
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
        refreshComputerCards();
    }

    private void showDeviceList() {
        java.util.List<TargetDeviceManager.Device> devices = targetDeviceManager.list();
        if (devices.isEmpty()) {
            new android.app.AlertDialog.Builder(this).setTitle("连接第一台电脑")
                    .setMessage("在电脑上打开言渡接收端，手机连同一个 Wi-Fi，电脑会自动出现在首页；也可以用 USB 线连接电脑并允许调试，自动完成首次配对。")
                    .setPositiveButton("查找附近电脑", (dialog, which) -> showAddComputerHelp())
                    .setNegativeButton("知道了", null).show();
            return;
        }
        String[] labels = new String[devices.size() + 2];
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
        labels[devices.size() + 1] = "✎ 管理电脑（重命名、排序、删除、重新配对）";
        new android.app.AlertDialog.Builder(this).setTitle("选择输入电脑")
                .setItems(labels, (dialog, which) -> {
                    if (which == devices.size()) {
                        showSharedGroupDialog();
                        return;
                    }
                    if (which == devices.size() + 1) {
                        showManageComputersDialog();
                        return;
                    }
                    selectTargetDevice(devices.get(which), statusText);
                })
                .setNeutralButton("电脑设置", (dialog, which) -> startActivity(new Intent(this, ComputerSettingsActivity.class)))
                .setPositiveButton("添加电脑", (dialog, which) -> showAddComputerHelp())
                .setNegativeButton("取消", null).show();
    }

    /// 多电脑管理：编号即顺序，名称只改手机上的显示，不影响电脑身份与配对。
    private void showManageComputersDialog() {
        java.util.List<TargetDeviceManager.Device> devices = targetDeviceManager.list();
        if (devices.isEmpty()) {
            showDeviceList();
            return;
        }
        String[] labels = new String[devices.size()];
        for (int i = 0; i < devices.size(); i++) {
            TargetDeviceManager.Device device = devices.get(i);
            String state = Boolean.TRUE.equals(lanPairingRejected.get(device.computerId))
                    ? "需重新配对" : isDeviceOnline(device.computerId) ? "在线" : "离线";
            labels[i] = device.slot + "号 · " + device.displayName + "\n" + state
                    + (device.nameLocked ? " · 手机自定义名称" : "");
        }
        new AlertDialog.Builder(this)
                .setTitle("管理电脑（" + devices.size() + "/" + TargetDeviceManager.MAX_DEVICES + "）")
                .setItems(labels, (dialog, which) -> showManageComputerActions(devices.get(which)))
                .setNegativeButton("完成", null).show();
    }

    private void showManageComputerActions(TargetDeviceManager.Device device) {
        String[] actions = {"重命名", "上移一位", "下移一位",
                device.sharedGroup ? "移出共享组" : "加入共享组", "重新配对", "删除这台电脑"};
        new AlertDialog.Builder(this).setTitle(device.slot + "号 · " + device.displayName)
                .setItems(actions, (dialog, which) -> {
                    if (which == 0) {
                        showRenameComputerDialog(device);
                    } else if (which == 1 || which == 2) {
                        if (targetDeviceManager.move(device.computerId, which == 1 ? -1 : 1)) {
                            applyStoredTarget();
                        }
                        showManageComputersDialog();
                    } else if (which == 3) {
                        targetDeviceManager.setSharedGroup(device.computerId, !device.sharedGroup);
                        refreshComputerCards();
                        showActionFeedback(device.sharedGroup ? "✓  已移出共享组" : "✓  已加入共享组",
                                theme.success);
                    } else if (which == 4) {
                        repairComputer(device);
                    } else {
                        confirmDeleteTargetDevice(device);
                    }
                })
                .setNegativeButton("返回", (dialog, which) -> showManageComputersDialog()).show();
    }

    private void showRenameComputerDialog(TargetDeviceManager.Device device) {
        android.widget.EditText input = new android.widget.EditText(this);
        input.setSingleLine(true);
        input.setText(device.displayName);
        input.setSelection(input.getText().length());
        input.setFilters(new android.text.InputFilter[]{new android.text.InputFilter.LengthFilter(40)});
        new AlertDialog.Builder(this).setTitle("重命名 " + device.slot + "号电脑")
                .setMessage("只修改手机上的显示名称。留空则恢复电脑自己的名称。")
                .setView(input)
                .setPositiveButton("保存", (dialog, which) -> {
                    targetDeviceManager.rename(device.computerId, input.getText().toString());
                    applyStoredTarget();
                    showManageComputersDialog();
                })
                .setNegativeButton("取消", (dialog, which) -> showManageComputersDialog()).show();
    }

    private boolean hasSharedGroupMember() {
        for (TargetDeviceManager.Device device : targetDeviceManager.list()) {
            if (device.sharedGroup) return true;
        }
        return false;
    }

    /// 共享组为空时开启共享：先请用户勾选接收电脑（默认勾选当前电脑），确认后才入组并开启。
    /// 共享组仍是显式集合，只是不再静默等待 15 秒后自动停止。
    private void showJoinSharedGroupDialog() {
        java.util.List<TargetDeviceManager.Device> devices = targetDeviceManager.list();
        if (devices.isEmpty()) {
            showActionFeedback("✕  先连接一台电脑，再开启共享麦克风", theme.warning);
            return;
        }
        String activeId = targetDeviceManager.getActiveComputerId();
        String[] labels = new String[devices.size()];
        boolean[] checked = new boolean[devices.size()];
        for (int i = 0; i < devices.size(); i++) {
            TargetDeviceManager.Device device = devices.get(i);
            labels[i] = device.slot + "号 · " + device.displayName
                    + (isDeviceOnline(device.computerId) ? "" : "（离线）");
            checked[i] = devices.size() == 1 || sameComputer(device.computerId, activeId);
        }
        new AlertDialog.Builder(this).setTitle("选择接收共享声音的电脑")
                .setMultiChoiceItems(labels, checked, (dialog, which, isChecked) -> checked[which] = isChecked)
                .setPositiveButton("加入并开启", (dialog, which) -> {
                    boolean any = false;
                    for (int i = 0; i < devices.size(); i++) {
                        if (checked[i]) {
                            targetDeviceManager.setSharedGroup(devices.get(i).computerId, true);
                            any = true;
                        }
                    }
                    lastComputerCardsSignature = null;
                    refreshComputerCards();
                    if (!any) {
                        showActionFeedback("✕  没有选择电脑，共享麦克风未开启", theme.warning);
                        return;
                    }
                    toggleSharedMicrophone();
                })
                .setNegativeButton("取消", null).show();
    }

    private void toggleSharedGroupFromCard(TargetDeviceManager.Device device) {
        boolean join = !device.sharedGroup;
        targetDeviceManager.setSharedGroup(device.computerId, join);
        lastComputerCardsSignature = null;
        refreshComputerCards();
        showActionFeedback(join ? "✓  " + device.displayName + " 已加入共享组"
                : "✓  " + device.displayName + " 已移出共享组", theme.success);
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
                        PhoneAudioService.getSnapshot().running
                                ? "✓ 共享组已更新，下一轮探测生效" : "✓ 共享组已更新",
                        theme.success))
                .setNegativeButton("取消", null).show();
    }

    // ---------- 同一 Wi-Fi 免扫码连接 ----------

    private static final int NEARBY_SCAN_INTERVAL_MS = 15_000;

    /// 首页在前台时约每 15 秒查找一次附近的言渡电脑（NSD + UDP，约 1.2 秒）；语音进行中暂停。
    private void scanNearbyComputers() {
        mainHandler.removeCallbacks(nearbyScan);
        if (isFinishing() || isDestroyed()) return;
        if (!nearbyScanInFlight && !nearbyPairingInFlight && !isVoiceInteractionBusy()) {
            nearbyScanInFlight = true;
            final android.content.Context app = getApplicationContext();
            nearbyExecutor.execute(() -> {
                java.util.Map<String, LanDiscoveryClient.DiscoveredComputer> found;
                try {
                    found = LanDiscoveryClient.discover(app, 1200);
                } catch (Exception exception) {
                    found = java.util.Collections.emptyMap();
                }
                final java.util.Map<String, LanDiscoveryClient.DiscoveredComputer> result = found;
                mainHandler.post(() -> {
                    nearbyScanInFlight = false;
                    applyNearbyResults(result);
                });
            });
        }
        mainHandler.postDelayed(nearbyScan, NEARBY_SCAN_INTERVAL_MS);
    }

    private void applyNearbyResults(java.util.Map<String, LanDiscoveryClient.DiscoveredComputer> found) {
        if (isFinishing() || isDestroyed() || targetDeviceManager == null) return;
        java.util.Map<String, LanDiscoveryClient.DiscoveredComputer> next = new java.util.HashMap<>();
        for (LanDiscoveryClient.DiscoveredComputer computer : found.values()) {
            if (targetDeviceManager.find(computer.computerId) == null && computer.port > 0
                    && TargetDeviceManager.isAddressCandidateSafe(computer.hostAddress)) {
                next.put(computer.computerId, computer);
            }
        }
        boolean changed = !next.keySet().equals(nearbyComputers.keySet());
        nearbyComputers.clear();
        nearbyComputers.putAll(next);
        if (changed) {
            lastComputerCardsSignature = null;
            refreshComputerCards();
        }
    }

    private java.util.List<LanDiscoveryClient.DiscoveredComputer> sortedNearby() {
        java.util.List<LanDiscoveryClient.DiscoveredComputer> list = new java.util.ArrayList<>(nearbyComputers.values());
        list.sort((a, b) -> a.displayName.compareToIgnoreCase(b.displayName));
        return list;
    }

    /// “添加电脑”：列出附近未连接的电脑；没有时说明怎么让电脑出现。
    private void showAddComputerHelp() {
        java.util.List<LanDiscoveryClient.DiscoveredComputer> nearby = sortedNearby();
        AlertDialog.Builder builder = new AlertDialog.Builder(this).setTitle("添加电脑");
        if (nearby.isEmpty()) {
            builder.setMessage("1. 在电脑上打开言渡接收端。\n2. 手机和电脑连同一个 Wi-Fi。\n\n电脑会自动出现在首页，点一下再到电脑上点「允许」即可。"
                    + "\n\n也可以用 USB 线连接电脑并允许调试，自动完成首次配对。");
        } else {
            String[] labels = new String[nearby.size()];
            for (int i = 0; i < nearby.size(); i++) {
                labels[i] = nearby.get(i).displayName + "  ·  " + platformLabel(nearby.get(i).platform);
            }
            builder.setItems(labels, (dialog, which) -> startNearbyPairing(nearby.get(which)));
        }
        builder.setPositiveButton("重新查找", (dialog, which) -> {
            showActionFeedback("●  正在查找同一 Wi-Fi 里的电脑…", theme.muted);
            scanNearbyComputers();
        }).setNeutralButton("输入地址", (dialog, which) -> promptComputerAddress())
                .setNegativeButton("关闭", null).show();
    }

    /// 手机与电脑隔着路由器（不同网段）时发现不到，可输入电脑的局域网 IP 直接连接。
    private void promptComputerAddress() {
        android.widget.EditText input = new android.widget.EditText(this);
        input.setSingleLine(true);
        input.setHint("例如 192.168.0.102");
        input.setInputType(android.text.InputType.TYPE_CLASS_TEXT
                | android.text.InputType.TYPE_TEXT_VARIATION_URI);
        input.setFilters(new android.text.InputFilter[]{new android.text.InputFilter.LengthFilter(64)});
        new AlertDialog.Builder(this).setTitle("输入电脑地址")
                .setMessage("在电脑的言渡接收端窗口或网络设置里可以看到局域网 IP。")
                .setView(input)
                .setPositiveButton("连接", (dialog, which) -> {
                    String host = input.getText().toString().trim();
                    if (!TargetDeviceManager.isAddressCandidateSafe(host)) {
                        showActionFeedback("✕  请输入局域网 IP，例如 192.168.0.102", theme.warning);
                        return;
                    }
                    showActionFeedback("●  正在联系 " + host + "…", theme.muted);
                    nearbyExecutor.execute(() -> {
                        LanDiscoveryClient.DiscoveredComputer found = LanDiscoveryClient.queryHost(host, 1200);
                        mainHandler.post(() -> {
                            if (found == null || found.port <= 0) {
                                showActionFeedback("✕  " + host + " 上没有找到言渡电脑，请确认接收端已打开", theme.warning);
                            } else {
                                startNearbyPairing(found);
                            }
                        });
                    });
                })
                .setNegativeButton("取消", null).show();
    }

    /// 长按卡片“重新配对”：在 Wi-Fi 里找到这台电脑后重新请求连接，换发独立凭据。
    private void repairComputer(TargetDeviceManager.Device device) {
        showActionFeedback("●  正在 Wi-Fi 里查找 " + device.displayName + "…", theme.muted);
        final android.content.Context app = getApplicationContext();
        nearbyExecutor.execute(() -> {
            LanDiscoveryClient.DiscoveredComputer match = null;
            try {
                match = LanDiscoveryClient.discover(app, 1500).get(device.computerId);
            } catch (Exception ignored) {
                // 下方提示 USB 修复。
            }
            final LanDiscoveryClient.DiscoveredComputer found = match;
            mainHandler.post(() -> {
                if (found != null && TargetDeviceManager.isAddressCandidateSafe(found.hostAddress)) {
                    startNearbyPairing(found);
                } else {
                    showActionFeedback("✕  没在 Wi-Fi 里找到 " + device.displayName
                            + "；确认电脑已打开言渡并连同一个 Wi-Fi，或用 USB 连接一次自动修复", theme.warning);
                }
            });
        });
    }

    private void startNearbyPairing(LanDiscoveryClient.DiscoveredComputer computer) {
        if (nearbyPairingInFlight) return;
        if (isVoiceInteractionBusy()) {
            showActionFeedback("✕  请先结束语音，再连接新电脑", theme.warning);
            return;
        }
        if (!targetDeviceManager.canAdd(computer.computerId)) {
            showActionFeedback("✕  已连接 " + TargetDeviceManager.MAX_DEVICES
                    + " 台电脑，请先长按删除一台", theme.danger);
            return;
        }
        nearbyPairingInFlight = true;
        final String clientId = java.util.UUID.randomUUID().toString();
        final String nonce = NearbyPairingClient.newNonce();
        final String label = Build.MODEL == null || Build.MODEL.isBlank() ? "Android 手机" : Build.MODEL;
        AlertDialog waiting = new AlertDialog.Builder(this)
                .setTitle("连接 " + computer.displayName)
                .setMessage("正在联系电脑…")
                .setNegativeButton("关闭", null)
                .setCancelable(false)
                .show();
        new Thread(() -> {
            String failure = null;
            String certificate = null;
            org.json.JSONObject issued = null;
            try {
                certificate = PhoneDeckHttp.fetchCertificateSha256(computer.hostAddress, computer.port, 3000);
                final String code = NearbyPairingClient.checkCode(certificate, clientId, nonce);
                runOnUiThread(() -> waiting.setMessage("请到「" + computer.displayName + "」上点「允许」。\n\n校验码  " + code
                        + "\n电脑上显示的数字应与这里相同。"));
                issued = NearbyPairingClient.request(computer.hostAddress, computer.port,
                        computer.computerId, certificate, clientId, label, nonce);
            } catch (java.io.IOException exception) {
                failure = "连不上这台电脑，请确认手机和电脑在同一个 Wi-Fi";
            } catch (Exception exception) {
                failure = exception.getMessage();
            }
            final String error = failure;
            final String pin = certificate;
            final org.json.JSONObject result = issued;
            runOnUiThread(() -> {
                nearbyPairingInFlight = false;
                if (waiting.isShowing()) waiting.dismiss();
                if (isFinishing() || isDestroyed()) return;
                if (error != null || result == null || !result.optBoolean("ok", false)) {
                    showActionFeedback("✕  " + (error == null ? "连接失败" : error), theme.danger);
                    return;
                }
                String name = result.optString("displayName", computer.displayName);
                TargetDeviceManager.Device saved = targetDeviceManager.savePairedComputer(
                        computer.computerId, name,
                        result.optString("platform", computer.platform),
                        java.util.Collections.singletonList(computer.hostAddress),
                        computer.port,
                        result.optString("clientToken"),
                        result.optString("clientId", clientId),
                        pin);
                if (saved == null) {
                    showActionFeedback("✕  凭据保存失败", theme.danger);
                    return;
                }
                nearbyComputers.remove(computer.computerId);
                lanPairingRejected.remove(computer.computerId);
                if (!isVoiceInteractionBusy()) {
                    targetDeviceManager.select(computer.computerId);
                    applyStoredTarget();
                }
                lastComputerCardsSignature = null;
                refreshTargetSwitcher();
                showActionFeedback("✓  已连接 " + saved.slot + "号电脑 · " + name, theme.success);
                ConnectionMonitor.get(this).reset();
                testConnection();
                testLanConnections();
            });
        }, "nearby-pairing").start();
    }

    /// M1-A A4：当前目标 legacy-only 且 LAN 在线时，弹一次「升级为独立凭据」
    /// （设计 §5.2 首连强提示）。取消也算已提示（per-computerId 标志），之后
    /// 仍可经电脑端重新配对获得独立凭据。语音进行中不打断，留到下一轮。
    private void maybePromptCredentialUpgrade() {
        if (isFinishing() || isDestroyed() || targetComputerId == null) {
            return;
        }
        LanTargetStatus lanStatus = lanTargets.get(targetComputerId);
        if (lanStatus == null) {
            return;
        }
        if (PhoneAudioService.getSnapshot().running) {
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
                        + "· 不影响现有使用，无需重新配对\n"
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
        // 落入 per-clientId 限速桶。凭据丢失的找回路径仍是重新配对（G-1/G-2）。
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
                    // rotate 永不重发（G-1）：凭据丢失只能重新配对找回。
                    showActionFeedback("该电脑已为此手机签发过独立凭据，不再重发；"
                            + "如本机凭据丢失，请长按电脑卡片选「重新配对」", theme.warning);
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
        if (device.hasLanPairing() && !sameComputer(device.computerId, targetComputerId)) {
            confirmAndSelectLanTarget(device, source);
            return;
        }
        if (!isDeviceOnline(device.computerId)) {
            if (Boolean.TRUE.equals(lanPairingRejected.get(device.computerId))) {
                showActionFeedback("✕  " + device.displayName
                        + " 的配对已失效；请长按电脑卡片选「重新配对」，或用 USB 连接一次自动修复", theme.warning);
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
                ? "Wi-Fi" : isUsbTargetOnline() ? "USB" : "暂时离线";
        showActionFeedback("✓  已切换到 " + device.slot + "号电脑 · "
                + device.displayName + " · " + channel, theme.success);
        performResultHaptic(source, true);
    }

    /// 切换目标必须得到新电脑确认：立即单独探测（核对 computerId、证书与凭据），
    /// 成功才切换；不依赖可能已过期数秒的缓存在线状态，也不改变共享组。
    private void confirmAndSelectLanTarget(TargetDeviceManager.Device device, View source) {
        if (targetSwitchInFlight) {
            return;
        }
        targetSwitchInFlight = true;
        showActionFeedback("●  正在确认 " + device.slot + "号电脑 · " + device.displayName + "…",
                theme.warning);
        targetSwitchExecutor.execute(() -> {
            ConnectionMonitor.Outcome outcome = ConnectionMonitor.get(this).probeNow(device);
            mainHandler.post(() -> {
                targetSwitchInFlight = false;
                mainHandler.post(this::finishCarouselConfirmation);
                if (isFinishing() || isDestroyed()) {
                    return;
                }
                if (outcome.result == null) {
                    if (outcome.pairingRejected) {
                        lanPairingRejected.put(device.computerId, Boolean.TRUE);
                        showActionFeedback("✕  " + device.displayName
                                + " 的配对已失效，请长按电脑卡片选「重新配对」", theme.warning);
                    } else if (isUsbTargetOnline() && isDeviceOnline(device.computerId)) {
                        // 局域网不可达但 USB 仍连着这台电脑：沿用已校验的 USB 通道。
                        finishTargetSelection(device, source);
                        return;
                    } else {
                        showActionFeedback("✕  " + device.displayName
                                + " 没有回应，仍保持当前电脑", theme.danger);
                    }
                    performResultHaptic(source, false);
                    refreshTargetSwitcher();
                    return;
                }
                lanPairingRejected.remove(device.computerId);
                lanTargets.put(device.computerId, lanStatusFrom(outcome.result));
                targetDeviceManager.recordLastGoodAddress(
                        device.computerId, outcome.result.hostAddress);
                finishTargetSelection(device, source);
            });
        });
    }

    private void finishTargetSelection(TargetDeviceManager.Device device, View source) {
        if (!targetDeviceManager.select(device.computerId)) {
            showActionFeedback("✕  无法选择目标电脑", theme.danger);
            performResultHaptic(source, false);
            return;
        }
        carouselFocusId = device.computerId;
        lastComputerCardsSignature = null;
        applyStoredTarget();
        String channel = isLanTargetOnline()
                ? "Wi-Fi" : isUsbTargetOnline() ? "USB" : "暂时离线";
        showActionFeedback("✓  已切换到 " + device.slot + "号电脑 · "
                + device.displayName + " · " + channel, theme.success);
        performResultHaptic(source, true);
    }

    /// 长按目标切换芯片：解除与一台电脑的配对。
    /// 被删电脑再用 USB 连接时会重新自动配对，所以误删可以低成本恢复。
    private void confirmDeleteTargetDevice(TargetDeviceManager.Device device) {
        if (PhoneAudioService.getSnapshot().running) {
            showActionFeedback("✕  请先关闭共享麦克风，再删除电脑", theme.warning);
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
        // 只在“还没有任何默认电脑”时自动选一台在线的。已选电脑短暂离线（Wi-Fi 抖动、探测超时）
        // 绝不能被静默换成另一台：多电脑下默认目标会来回跳，手机语音键把听写发到别的电脑上，
        // 也违反“切换目标必须经新电脑确认”。f29f03d 曾在离线时自动换目标，已收回。
        if (targetDeviceManager != null
                && !isVoiceInteractionBusy()
                && confirmingComputerId == null
                && active == null) {
            TargetDeviceManager.Device onlineCandidate = null;
            if (usbConnected && usbComputerId != null) {
                onlineCandidate = targetDeviceManager.find(usbComputerId);
            }
            if (onlineCandidate == null) {
                for (TargetDeviceManager.Device candidate : targetDeviceManager.list()) {
                    if (isDeviceOnline(candidate.computerId)) {
                        onlineCandidate = candidate;
                        break;
                    }
                }
            }
            if (onlineCandidate != null && targetDeviceManager.select(onlineCandidate.computerId)) {
                active = onlineCandidate;
                carouselFocusId = onlineCandidate.computerId;
                lastComputerCardsSignature = null;
            }
        }
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
            } else {
                serverProtocolVersion = 0;
            }
        }
        refreshTargetSwitcher();
        updateConnectionDisplay();
    }

    private boolean isDeviceOnline(String computerId) {
        return lanTargets.containsKey(computerId)
                || usbConnected && sameComputer(computerId, usbComputerId);
    }

    private boolean isLanTargetOnline() {
        return targetComputerId != null && lanTargets.containsKey(targetComputerId);
    }

    private boolean isUsbTargetOnline() {
        return usbConnected && sameComputer(targetComputerId, usbComputerId);
    }

    private static boolean sameComputer(String left, String right) {
        return left != null && right != null && left.equalsIgnoreCase(right);
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

    private static boolean parseEngineCapturing(JSONObject health) {
        if (health == null) return false;
        JSONObject node = health.optJSONObject("voiceEngine");
        if (node != null && node.optBoolean("capturing", false)) {
            return true;
        }
        JSONObject typeless = health.optJSONObject("typeless");
        return typeless != null && typeless.optBoolean("capturing", false);
    }

    private static boolean parseSupports16k(JSONObject health) {
        if (health == null) return false;
        org.json.JSONArray capabilities = health.optJSONArray("capabilities");
        if (capabilities == null) return false;
        for (int index = 0; index < capabilities.length(); index++) {
            if ("sharedPcm16kV1".equals(capabilities.optString(index))) {
                return true;
            }
        }
        return false;
    }

    private void testConnection() {
        if (!usbConnected && lanTargets.isEmpty()) {
            showConnection("正在检测电脑端…", theme.muted);
        }
        connectionExecutor.execute(() -> {
            HttpURLConnection connection = null;
            final long probeStartedAt = SystemClock.elapsedRealtime();
            try {
                connection = (HttpURLConnection) new URL(SERVER + "/api/health").openConnection();
                connection.setRequestProperty("X-PhoneDeck-Foreground", PhoneDeckHttp.appInForeground ? "1" : "0");
                connection.setRequestProperty("X-PhoneDeck-Device", PhoneDeckHttp.DEVICE_LABEL);
                connection.setConnectTimeout(1200);
                connection.setReadTimeout(1200);
                connection.setRequestMethod("GET");
                int response = connection.getResponseCode();
                if (response == 200) {
                    JSONObject health = readJsonResponse(connection);
                    boolean supportsSecureLan = false;
                    org.json.JSONArray capabilities = health.optJSONArray("capabilities");
                    if (capabilities != null) {
                        for (int index = 0; index < capabilities.length(); index++) {
                            if ("secureLan".equals(capabilities.optString(index))) {
                                supportsSecureLan = true;
                            }
                        }
                    }
                    usbProtocolVersion = health.optInt("protocolVersion", 0);
                    JSONObject audio = health.optJSONObject("audio");
                    phoneAudioAvailable = audio != null
                            && audio.optBoolean("available", false);
                    usbAudioStreaming = audio != null
                            && audio.optBoolean("streaming", false);
                    usbEngineCapturing = parseEngineCapturing(health);
                    typelessVirtualCableSelected = parseVirtualCableSelected(health);
                    usbEngineName = parseEngineDisplayName(health);
                    String healthForegroundApp = health.isNull("foregroundApp")
                            ? null : health.optString("foregroundApp", null);
                    usbForegroundApp = healthForegroundApp == null
                            || healthForegroundApp.isBlank()
                            || "null".equalsIgnoreCase(healthForegroundApp.trim())
                            ? null : healthForegroundApp;
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
                    // 有独立凭据的电脑只是 Wi-Fi 不通时保留凭据；被电脑拒绝（401/403）才用 USB 换回共享令牌。
                    boolean lanPairingNeedsRefresh = existingDevice == null
                            || !existingDevice.hasLanPairing()
                            || !lanTargets.containsKey(healthComputerId)
                            && (!existingDevice.hasClientCredential()
                            || Boolean.TRUE.equals(lanPairingRejected.get(healthComputerId)));
                    if (supportsSecureLan && lanPairingNeedsRefresh) {
                        try {
                            TargetDeviceManager.Device repaired =
                                    PhoneDeckLanClient.pairOverUsb(targetDeviceManager);
                            mainHandler.post(() -> {
                                if (lanPairingRejected.remove(repaired.computerId) != null) {
                                    lastComputerCardsSignature = null;
                                    refreshComputerCards();
                                }
                            });
                        } catch (Exception ignored) {
                            // USB 主功能继续可用；配对失败会在界面保持 USB 状态。
                        }
                    }
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
                    });
                } else {
                    throw new IllegalStateException("HTTP " + response);
                }
            } catch (Exception exception) {
                usbConnected = false;
                phoneAudioAvailable = false;
                usbAudioStreaming = false;
                usbEngineCapturing = false;
                typelessVirtualCableSelected = null;
                usbEngineName = "Typeless";
                usbForegroundApp = null;
                mainHandler.post(this::applyStoredTarget);
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
        final long probeGeneration = WifiLinkObserver.currentGeneration();
        connectionExecutor.execute(() -> {
            try {
                ConcurrentHashMap<String, LanTargetStatus> next =
                        new ConcurrentHashMap<>();
                java.util.Map<String, String> goodAddresses = new java.util.HashMap<>();
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
                java.util.List<TargetDeviceManager.Device> devices = targetDeviceManager.list();
                // 全部电脑并行探测，离线电脑按各自退避降频；与共享麦克风服务共用结果。
                java.util.Map<String, ConnectionMonitor.Outcome> outcomes =
                        ConnectionMonitor.get(this).probeAll(devices, false);
                if (ConnectionMonitor.isInvalidated(outcomes)) {
                    // 网络切换已使本轮结果失效；保留上一轮 UI/目标状态，
                    // 不把“无结果”误当成“所有电脑离线”。
                    return;
                }
                if (isDestroyed() || Thread.currentThread().isInterrupted()) {
                    return; // Activity was destroyed during a font/orientation change.
                }
                for (TargetDeviceManager.Device device : devices) {
                    ConnectionMonitor.Outcome outcome = outcomes.get(device.computerId);
                    if (outcome == null) {
                        continue;
                    }
                    anyPaired = true;
                    PhoneDeckLanClient.ProbeResult result = outcome.result;
                    if (result == null && outcome.pairingRejected
                            && usbConnected && sameComputer(device.computerId, usbComputerId)) {
                        try {
                            TargetDeviceManager.Device repaired =
                                    PhoneDeckLanClient.pairOverUsb(targetDeviceManager);
                            if (repaired != null) {
                                outcome = ConnectionMonitor.get(this).probeNow(repaired);
                                result = outcome.result;
                            }
                        } catch (Exception ignored) {
                            // USB 自愈失败时保留原有配对失效提示。
                        }
                    }
                    if (result == null) {
                        if (outcome != null && outcome.pairingRejected) {
                            rejectedNext.put(device.computerId, Boolean.TRUE);
                        }
                        continue;
                    }
                    anySuccess = true;
                    goodAddresses.put(device.computerId, result.hostAddress);
                    next.put(device.computerId, lanStatusFrom(result));
                }
                if (!WifiLinkPolicy.shouldApply(probeGeneration,
                        WifiLinkObserver.currentGeneration(), !isDestroyed()
                                && !Thread.currentThread().isInterrupted())) {
                    return;
                }
                final int nextFailStreak = anyPaired && !anySuccess
                        ? lanCheckFailStreak + 1 : 0;
                mainHandler.post(() -> {
                    if (!WifiLinkPolicy.shouldApply(probeGeneration,
                            WifiLinkObserver.currentGeneration(), !isDestroyed())) {
                        return;
                    }
                    for (java.util.Map.Entry<String, String> entry : goodAddresses.entrySet()) {
                        if (targetDeviceManager.recordLastGoodAddress(
                                entry.getKey(), entry.getValue())) {
                            Log.i("PhoneDeckNet", "last-good 地址更新："
                                    + entry.getKey() + " → " + entry.getValue());
                        }
                    }
                    lanCheckFailStreak = nextFailStreak;
                    lanTargets.clear();
                    lanTargets.putAll(next);
                    lanPairingRejected.clear();
                    lanPairingRejected.putAll(rejectedNext);
                    if (usbConnected && usbComputerId != null
                            && Boolean.TRUE.equals(lanPairingRejected.get(usbComputerId))) {
                        testConnection();
                    }
                    applyStoredTarget();
                });
            } finally {
                lanCheckInFlight = false;
                mainHandler.post(() -> {
                    schedulePendingLanCheck();
                });
            }
        });
    }

    private LanTargetStatus lanStatusFrom(PhoneDeckLanClient.ProbeResult result) {
        JSONObject health = result.health;
        JSONObject audio = health.optJSONObject("audio");
        String lanForegroundApp = health.isNull("foregroundApp")
                ? null : health.optString("foregroundApp", null);
        return new LanTargetStatus(
                result.endpoint,
                health.optInt("protocolVersion", 0),
                audio != null && audio.optBoolean("available", false),
                parseVirtualCableSelected(health),
                parseEngineDisplayName(health),
                lanForegroundApp == null || lanForegroundApp.isBlank()
                        || "null".equalsIgnoreCase(lanForegroundApp.trim())
                        ? null : lanForegroundApp,
                parseEngineCapturing(health),
                audio != null && audio.optBoolean("streaming", false),
                parseSupports16k(health));
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

    private void installVoiceGesture() {
        typelessButton.setSoundEffectsEnabled(true);
        typelessButton.setHapticFeedbackEnabled(true);
        typelessButton.setOnClickListener(view -> toggleSharedMicrophone());
        typelessButton.setOnTouchListener((view, event) -> {
            int action = event.getActionMasked();
            if (action == MotionEvent.ACTION_DOWN) {
                if (!view.isEnabled()) {
                    return true;
                }
                // 长条整体缩放 0.93 太晃，只轻微下压。
                TouchFeedback.press(view, voiceStatusCaption != null ? 0.98f : 0.93f, true);
            } else if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_CANCEL) {
                TouchFeedback.release(view);
            }
            return false;
        });
    }

    private boolean isVoiceInteractionBusy() {
        return sharedStartPending || PhoneAudioService.getSnapshot().busy;
    }

    private void updateVoiceModeInterface() {
        typelessButton.setContentDescription("开启或关闭共享麦克风");
        updateVoiceControls();
    }

    private String voiceButtonLabel() {
        return PhoneAudioService.getSnapshot().running ? "关闭共享麦克风" : "开启共享麦克风";
    }

    private void updateVoiceControls() {
        if (typelessButton == null) {
            return;
        }
        voiceButtonCaption.setText(voiceButtonLabel());
        updateDialogueCopy();
        refreshComputerCards();
        PhoneAudioService.Snapshot state = PhoneAudioService.getSnapshot();
        boolean stopState = state.running;
        // 底部长条：共享中用成功色浅底。
        boolean bar = voiceStatusCaption != null;
        int stopFill = bar ? theme.successContainer : theme.live;
        int stopInk = bar ? theme.success : theme.onLive;
        typelessButton.setBackground(stopState
                ? voiceButtonBackground(stopFill, theme.mix(stopFill, stopInk, 0.2f))
                : voiceButtonBackground(theme.primary, theme.primaryPressed));
        typelessButton.setTextColor(stopState ? stopInk : theme.onPrimary);
        if (voiceIcon != null) {
            voiceIcon.setColor(stopState ? stopInk : theme.onPrimary);
            voiceIcon.setStopGlyph(stopState && !bar);
        }
        typelessButton.setContentDescription(stopState ? "关闭共享麦克风" : "开启共享麦克风");
        if (voiceMeter != null) {
            voiceMeter.setVoiceState(stopState ? VoiceLevelView.ACTIVE : VoiceLevelView.IDLE);
            voiceMeter.setLevel(stopState ? state.level : 0);
        }
        setControlEnabled(typelessButton, !sharedStartPending);
    }

    private void updateDialogueCopy() {
        if (voiceStatusCaption == null || voiceHint == null) return;
        PhoneAudioService.Snapshot state = PhoneAudioService.getSnapshot();
        boolean recording = state.running;
        boolean starting = sharedStartPending;
        boolean connected = !lanTargets.isEmpty() || usbConnected;
        voiceStatusCaption.setText(recording ? "● 正在供音" : starting ? "● 正在连接"
                : connected ? "● 准备就绪" : "● 等待连接");
        int chipInk = recording ? theme.success : starting ? theme.warning
                : connected ? theme.success : theme.muted;
        int chipFill = recording ? theme.successContainer
                : starting ? theme.warningContainer : theme.surfaceRaised;
        voiceStatusCaption.setTextColor(chipInk);
        voiceStatusCaption.setBackground(roundRect(chipFill, 10));
        voiceButtonCaption.setText(recording ? "正在共享声音" : "共享麦克风");
        voiceHint.setText(starting ? "正在准备手机麦克风…"
                : recording ? ("已供音 " + state.connected + "/" + state.total + " 台 · 在电脑按快捷键说话")
                : !connected ? (targetDeviceManager != null && targetDeviceManager.list().isEmpty()
                        ? "手机与电脑连同一 Wi-Fi 自动配对" : "等待电脑上线（支持 Wi-Fi / USB）")
                : "共享后各电脑用各自快捷键触发听写");
        ((RoundVoiceButton) typelessButton).setBarLabel(state.running
                ? "共享供音中 (" + state.connected + "/" + state.total + ") · 点击关闭"
                : sharedStartPending ? "正在开启共享麦克风…" : "开启共享麦克风");
        if (voiceMeter != null) {
            voiceMeter.setVisibility(recording || starting ? View.VISIBLE : View.GONE);
        }
        if (voiceHalo != null) voiceHalo.setBackground(theme.shape(this, recording
                ? theme.successContainer : Color.TRANSPARENT, 120));
    }

    private void setControlEnabled(Button button, boolean enabled) {
        button.setEnabled(enabled);
        button.setAlpha(enabled ? 1f : 0.45f);
    }

    private void toggleSharedMicrophone() {
        PhoneAudioService.Snapshot state = PhoneAudioService.getSnapshot();
        if (state.running) {
            stopSharedMicrophoneService();
            showActionFeedback("■  正在关闭共享麦克风…", theme.warning);
            return;
        }
        if (!hasSharedGroupMember()) {
            showJoinSharedGroupDialog();
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
        if (state == null) {
            return;
        }
        // Level ticks arrive every 100 ms. Rebuilding cards, chips and the recent
        // list on each tick kept the UI redrawing continuously; only the meter and
        // its caption follow the level, everything else waits for a real change.
        String controlsKey = state.running + "|" + state.connected + "|" + state.total;
        boolean controlsChanged = sharedStartPending || !controlsKey.equals(lastSharedControlsKey);
        lastSharedControlsKey = controlsKey;
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
            String levelText = state.running
                    ? "手机麦克风  ·  " + state.level + "%  ·  "
                            + state.connected + "/" + state.total + " 正在供音"
                    : "手机麦克风  ○ 共享已关闭";
            if (!levelText.contentEquals(microphoneLevel.getText())) {
                microphoneLevel.setText(levelText);
                microphoneLevel.setTextColor(!state.running ? theme.muted
                        : state.connected > 0 ? theme.success : theme.warning);
            }
        }
        String detail = state.detail == null ? "" : state.detail;
        if (!detail.equals(lastSharedDetail)) {
            lastSharedDetail = detail;
            controlsChanged = true;
            showActionFeedback((state.running ? "●  " : "■  ") + detail,
                    state.running && state.connected == 0 ? theme.warning : theme.muted);
        }
        if (controlsChanged || receiverStatesChanged) {
            updateVoiceControls();
        }
        if (receiverStatesChanged) {
            refreshTargetSwitcher();
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
        try {
            PhoneDeckHttp.postJson(connectionEndpoint, endpoint, body, 1_800);
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
                showConnection(targetDisplayName + " · Wi-Fi · 虚拟麦克风未就绪", theme.warning);
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
        } else if (targetComputerId != null) {
            showConnection(targetDisplayName + " · 当前离线", theme.muted);
        } else {
            showConnection("等待电脑连接", theme.muted);
        }
    }

    private void showConnection(String title, int color) {
        statusText.setText(targetComputerId == null ? "连接你的电脑" : targetDisplayName);
        statusDetailText.setText(UiText.connectionDetail(targetDisplayName, title));
        connectionCard.setContentDescription(statusText.getText() + "，" + statusDetailText.getText()
                + "。点击查看全部电脑");
        statusText.setTextColor(theme.text);
        statusDetailText.setTextColor(theme.muted);
        statusDetailText.setVisibility(View.VISIBLE);
        statusDot.setBackground(roundRect(color, 20));
        updateDialogueCopy();
    }

    private final Runnable hideActionFeedbackRunnable = () -> {
        if (actionFeedback != null && !gridEditMode && !isVoiceInteractionBusy()) {
            actionFeedback.setVisibility(View.GONE);
            lastFeedbackMessage = null;
        }
    };

    private void showActionFeedback(String message, int color) {
        lastFeedbackMessage = message;
        lastFeedbackColor = color;
        mainHandler.removeCallbacks(hideActionFeedbackRunnable);
        actionFeedback.setText(message);
        if (shortcutDialog != null && shortcutDialog.isShowing()) {
            shortcutPanelFeedback.setText(message);
            shortcutPanelFeedback.setTextColor(color);
        }
        actionFeedback.setVisibility(View.VISIBLE);
        actionFeedback.setTextColor(color);
        actionFeedback.setBackground(theme.shape(
                this, theme.feedbackSurface(color), 14, 0,
                theme.mix(theme.outline, color, 0.35f)));
        if (message != null && !message.contains("正在") && !message.contains("编辑模式")) {
            mainHandler.postDelayed(hideActionFeedbackRunnable,
                    message.contains("撤销") ? 5_000L : 2_800L);
        }
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

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == REQUEST_SHARED_MICROPHONE) {
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
        nearbyExecutor.shutdownNow();
        if (shortcutDialog != null) shortcutDialog.dismiss();
        mainHandler.removeCallbacks(periodicHealthCheck);
        mainHandler.removeCallbacks(pendingLanCheck);
        pendingLanRefresh.clear();
        if (sharedStatusReceiverRegistered) {
            unregisterReceiver(sharedStatusReceiver);
            sharedStatusReceiverRegistered = false;
        }
        stopKeyRepeat();
        releaseWifiLock();
        unregisterNetworkCallbacks();
        actionExecutor.shutdownNow();
        connectionExecutor.shutdownNow();
        targetSwitchExecutor.shutdownNow();
        PhoneAudioService.setInProcessListener(null);
        super.onDestroy();
    }

    private void unregisterNetworkCallbacks() {
        if (wifiLinkRegistration != null) {
            wifiLinkRegistration.close();
            wifiLinkRegistration = null;
        }
    }

    private Button smallButton(String label) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextSize(12);
        button.setTextColor(theme.text);
        button.setAllCaps(false);
        button.setMinWidth(0);
        button.setMinimumWidth(0);
        button.setMinHeight(0);
        button.setMinimumHeight(0);
        button.setPadding(dp(4), 0, dp(4), 0);
        button.setBackground(theme.pressable(
                this, theme.key, theme.mix(theme.key, theme.primary, 0.18f), 19));
        button.setStateListAnimator(null);
        installTouchFeedback(button);
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
        final boolean phoneAudioAvailable;
        /// null = 引擎无可读配置、无法校验虚拟声卡（不阻断启动）。
        final Boolean typelessVirtualCableSelected;
        final String engineName;
        final String foregroundApp;
        final boolean engineCapturing;
        final boolean audioStreaming;
        final boolean supports16k;
        /// 本状态确认在线的时刻（elapsedRealtime），供离线判定宽限使用。
        final long lastOnlineAt;

        LanTargetStatus(
                PhoneDeckEndpoint endpoint,
                int protocolVersion,
                boolean phoneAudioAvailable,
                Boolean typelessVirtualCableSelected,
                String engineName,
                String foregroundApp,
                boolean engineCapturing,
                boolean audioStreaming,
                boolean supports16k) {
            this.endpoint = endpoint;
            this.protocolVersion = protocolVersion;
            this.phoneAudioAvailable = phoneAudioAvailable;
            this.typelessVirtualCableSelected = typelessVirtualCableSelected;
            this.engineName = engineName;
            this.foregroundApp = foregroundApp;
            this.engineCapturing = engineCapturing;
            this.audioStreaming = audioStreaming;
            this.supports16k = supports16k;
            this.lastOnlineAt = android.os.SystemClock.elapsedRealtime();
        }
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
