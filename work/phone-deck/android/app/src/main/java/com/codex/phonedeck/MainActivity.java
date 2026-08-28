package com.codex.phonedeck;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.StateListDrawable;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.HapticFeedbackConstants;
import android.view.MotionEvent;
import android.view.SoundEffectConstants;
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
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class MainActivity extends Activity {
    private static final int REQUEST_BLUETOOTH = 1002;
    private static final int REQUEST_MICROPHONE = 1003;
    private static final String SERVER = "http://127.0.0.1:8765";
    private static final String PREFS_NAME = "PhoneDeckSettings";
    private static final String PREF_VOICE_MODE = "voice_mode";
    private static final String MODE_TAP = "tap";
    private static final String MODE_HOLD = "hold";
    private static final long HEALTH_CHECK_INTERVAL_MS = 2_000;
    private final ExecutorService actionExecutor = Executors.newSingleThreadExecutor();
    private final ExecutorService connectionExecutor = Executors.newSingleThreadExecutor();
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private PhoneDeckTheme theme;
    private String appliedThemeId;
    private TextView statusText;
    private View statusDot;
    private TextView actionFeedback;
    private TextView microphoneLevel;
    private TextView voiceModeText;
    private Button typelessButton;
    private MicrophoneGlyphDrawable voiceIcon;
    private VoiceLevelView voiceMeter;
    private GridLayout shortcutGrid;
    private LinearLayout targetDeviceRow;
    private boolean typelessInFlight;
    private boolean audioStartPending;
    private boolean dictationActive;
    private boolean dictationPaused;
    private boolean holdGestureActive;
    private boolean holdReleasePending;
    private String voiceBusyLabel;
    private String voiceMode = MODE_TAP;
    private String currentSessionId;
    private boolean currentSessionManaged;
    private String currentSessionTargetComputerId;
    private PhoneDeckEndpoint currentSessionEndpoint;
    private volatile String intentionalAudioStopSessionId;
    private volatile boolean usbConnected;
    private volatile boolean usbRecoveryFeedbackPending;
    private volatile boolean lanCheckInFlight;
    private volatile boolean managedDictationSupported;
    private volatile boolean phoneAudioAvailable;
    private volatile boolean typelessVirtualCableSelected;
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
    private final Runnable periodicHealthCheck = new Runnable() {
        @Override
        public void run() {
            if (isFinishing() || isDestroyed()) {
                return;
            }
            testConnection();
            testLanConnections();
            mainHandler.postDelayed(this, HEALTH_CHECK_INTERVAL_MS);
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        theme = PhoneDeckTheme.load(this);
        appliedThemeId = theme.id;
        theme.applyWindow(this);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);

        configRepository = new ShortcutConfigRepository(this);
        targetDeviceManager = new TargetDeviceManager(this);
        setContentView(createInterface());
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
        testConnection();
        testLanConnections();
        mainHandler.postDelayed(periodicHealthCheck, HEALTH_CHECK_INTERVAL_MS);
    }

    @Override
    protected void onResume() {
        super.onResume();
        PhoneDeckTheme latestTheme = PhoneDeckTheme.load(this);
        if (!latestTheme.id.equals(appliedThemeId)) {
            recreate();
            return;
        }
        SharedPreferences preferences = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
        voiceMode = preferences.getString(PREF_VOICE_MODE, MODE_TAP);
        if (!MODE_HOLD.equals(voiceMode)) {
            voiceMode = MODE_TAP;
        }
        if (voiceModeText != null && typelessButton != null) {
            updateVoiceModeInterface();
        }
        if (shortcutGrid != null && configRepository != null) {
            refreshShortcutGrid();
        }
    }

    private View createInterface() {
        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(theme.background);
        if (theme.isFrost()) {
            root.addView(new FrostedBackdropView(this), new FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.MATCH_PARENT,
                    FrameLayout.LayoutParams.MATCH_PARENT));
        }

        ScrollView scrollView = new ScrollView(this);
        scrollView.setFillViewport(true);
        scrollView.setBackgroundColor(theme.contentBackground());
        scrollView.setClipToPadding(false);

        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(18), dp(18), dp(18), dp(286));
        scrollView.addView(page, new ScrollView.LayoutParams(
                ScrollView.LayoutParams.MATCH_PARENT,
                ScrollView.LayoutParams.WRAP_CONTENT));

        TextView eyebrow = text("PHONE DECK  ·  PERSONAL CONSOLE", 11,
                theme.primary, Typeface.BOLD);
        eyebrow.setLetterSpacing(0.12f);
        page.addView(eyebrow);

        TextView title = text("PhoneDeck 手机控制台", 27, theme.text, Typeface.BOLD);
        page.addView(title, marginTop(dp(4)));

        TextView subtitle = text("语音优先 · 快捷操作 · 本地连接", 13,
                theme.muted, Typeface.NORMAL);
        page.addView(subtitle, marginTop(dp(2)));

        LinearLayout connection = new LinearLayout(this);
        connection.setOrientation(LinearLayout.HORIZONTAL);
        connection.setGravity(Gravity.CENTER_VERTICAL);
        connection.setPadding(dp(16), dp(12), dp(12), dp(12));
        connection.setBackground(theme.shape(this, theme.surface, 18, 1, theme.outline));
        connection.setElevation(dp(theme.isFrost() ? 5 : 0));
        connection.setOnClickListener(view -> {
            startBluetoothTransport();
            testConnection();
        });
        connection.setContentDescription("电脑连接状态；点击重新检测连接");
        installTouchFeedback(connection);
        page.addView(connection, margins(dp(0), dp(18), dp(0), dp(14),
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        statusDot = new View(this);
        statusDot.setBackground(roundRect(theme.muted, 20));
        connection.addView(statusDot, new LinearLayout.LayoutParams(dp(12), dp(12)));

        statusText = text("正在检测电脑端…", 15, theme.text, Typeface.BOLD);
        statusText.setSingleLine(true);
        statusText.setEllipsize(TextUtils.TruncateAt.END);
        LinearLayout.LayoutParams statusParams = new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        statusParams.leftMargin = dp(12);
        connection.addView(statusText, statusParams);

        TextView retryChevron = text("›", 25, theme.muted, Typeface.NORMAL);
        retryChevron.setGravity(Gravity.CENTER);
        retryChevron.setContentDescription("重新检测电脑连接");
        connection.addView(retryChevron, new LinearLayout.LayoutParams(dp(26), dp(38)));

        Button settings = smallButton("设置");
        settings.setOnClickListener(view -> startActivity(new Intent(this, SettingsActivity.class)));
        installTouchFeedback(settings);
        connection.addView(settings, new LinearLayout.LayoutParams(dp(60), dp(38)));

        TextView shortcutTitle = text("快捷操作", 18, theme.text, Typeface.BOLD);
        page.addView(shortcutTitle, marginTop(dp(2)));

        TextView shortcutHint = text("点击发送到当前窗口 · 长按可编辑", 12,
                theme.muted, Typeface.NORMAL);
        page.addView(shortcutHint, marginTop(dp(3)));

        GridLayout grid = new GridLayout(this);
        grid.setColumnCount(3);
        grid.setUseDefaultMargins(false);
        shortcutGrid = grid;
        page.addView(grid, margins(dp(-4), dp(10), dp(-4), dp(0),
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        refreshShortcutGrid();

        root.addView(scrollView, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));

        LinearLayout voiceDock = new LinearLayout(this);
        voiceDock.setOrientation(LinearLayout.VERTICAL);
        voiceDock.setPadding(dp(12), dp(10), dp(12), dp(12));
        voiceDock.setBackground(theme.shape(this, theme.voiceDock, 24, 1, theme.outline));
        voiceDock.setElevation(dp(theme.isFrost() ? 18 : 14));

        LinearLayout dockHeader = new LinearLayout(this);
        dockHeader.setOrientation(LinearLayout.HORIZONTAL);
        dockHeader.setGravity(Gravity.CENTER_VERTICAL);
        voiceDock.addView(dockHeader, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        TextView voiceTitle = text("语音输入", 15, theme.text, Typeface.BOLD);
        dockHeader.addView(voiceTitle, new LinearLayout.LayoutParams(0,
                LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        voiceModeText = text("点击说话模式", 11, theme.primary, Typeface.BOLD);
        voiceModeText.setGravity(Gravity.END);
        dockHeader.addView(voiceModeText);

        typelessButton = new Button(this);
        typelessButton.setText("点击开始说话");
        typelessButton.setTextSize(18);
        typelessButton.setTextColor(theme.onPrimary);
        typelessButton.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        typelessButton.setAllCaps(false);
        typelessButton.setGravity(Gravity.CENTER);
        voiceIcon = new MicrophoneGlyphDrawable(this, theme.onPrimary);
        typelessButton.setCompoundDrawablesWithIntrinsicBounds(voiceIcon, null, null, null);
        typelessButton.setCompoundDrawablePadding(dp(12));
        typelessButton.setPadding(dp(12), 0, dp(12), 0);
        typelessButton.setBackground(pressableRoundRect(
                theme.primary, theme.primaryPressed, 22));
        typelessButton.setElevation(dp(theme.isFrost() ? 8 : 0));
        typelessButton.setStateListAnimator(null);
        typelessButton.setContentDescription("Typeless 语音输入");
        installVoiceGesture();
        voiceDock.addView(typelessButton, margins(dp(0), dp(7), dp(0), dp(0),
                LinearLayout.LayoutParams.MATCH_PARENT, dp(72)));

        voiceMeter = new VoiceLevelView(this, theme);
        voiceDock.addView(voiceMeter, margins(dp(8), dp(5), dp(8), dp(0),
                LinearLayout.LayoutParams.MATCH_PARENT, dp(30)));

        actionFeedback = text("●  准备就绪 · 操作状态会显示在这里",
                12, theme.muted, Typeface.BOLD);
        actionFeedback.setGravity(Gravity.CENTER_VERTICAL);
        actionFeedback.setPadding(dp(15), dp(12), dp(15), dp(12));
        actionFeedback.setBackground(roundRect(theme.surface, 14));
        voiceDock.addView(actionFeedback, margins(dp(0), dp(7), dp(0), dp(0),
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT));

        microphoneLevel = text("手机麦克风  ·  未启动", 12, theme.muted, Typeface.BOLD);
        microphoneLevel.setPadding(dp(15), dp(7), dp(15), dp(7));
        voiceDock.addView(microphoneLevel, margins(dp(0), dp(2), dp(0), dp(0),
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT));

        LinearLayout targetDockRow = new LinearLayout(this);
        targetDockRow.setOrientation(LinearLayout.HORIZONTAL);
        targetDockRow.setGravity(Gravity.CENTER_VERTICAL);

        TextView targetTitle = text("输入到", 12, theme.muted, Typeface.BOLD);
        targetDockRow.addView(targetTitle, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT,
                LinearLayout.LayoutParams.WRAP_CONTENT));

        HorizontalScrollView targetScroller = new HorizontalScrollView(this);
        targetScroller.setHorizontalScrollBarEnabled(false);
        targetScroller.setFillViewport(true);
        targetDeviceRow = new LinearLayout(this);
        targetDeviceRow.setOrientation(LinearLayout.HORIZONTAL);
        targetDeviceRow.setGravity(Gravity.CENTER_VERTICAL | Gravity.END);
        targetScroller.addView(targetDeviceRow, new HorizontalScrollView.LayoutParams(
                HorizontalScrollView.LayoutParams.MATCH_PARENT,
                HorizontalScrollView.LayoutParams.WRAP_CONTENT));
        LinearLayout.LayoutParams targetScrollerParams = new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        targetScrollerParams.leftMargin = dp(10);
        targetDockRow.addView(targetScroller, targetScrollerParams);
        voiceDock.addView(targetDockRow, margins(dp(8), dp(4), dp(0), dp(0),
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT));
        refreshTargetSwitcher();

        FrameLayout.LayoutParams dockParams = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.BOTTOM);
        dockParams.setMargins(dp(12), dp(0), dp(12), dp(10));
        root.addView(voiceDock, dockParams);

        page.setFocusableInTouchMode(true);
        page.requestFocus();
        scrollView.post(() -> scrollView.scrollTo(0, 0));
        return root;
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
        button.setContentDescription(config.label + "，" + config.subtitle()
                + "。长按编辑");
        installTouchFeedback(button);
        button.setOnClickListener(view -> triggerShortcut(button, config));
        button.setOnLongClickListener(view -> {
            view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
            Intent intent = new Intent(this, ShortcutEditActivity.class);
            intent.putExtra(ShortcutEditActivity.EXTRA_BUTTON_ID, config.id);
            startActivity(intent);
            return true;
        });

        GridLayout.LayoutParams params = new GridLayout.LayoutParams();
        params.width = 0;
        params.height = dp(88);
        params.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
        params.setMargins(dp(4), dp(4), dp(4), dp(4));
        grid.addView(button, params);
    }

    private void triggerShortcut(ShortcutKeyView source, ShortcutButtonConfig config) {
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
                String legacyAction = legacyActionForId(config.id);
                if (legacyAction == null) {
                    source.showFailure();
                    showActionFeedback("✕  自定义按键需要电脑端升级到 1.5.0", theme.danger);
                    return;
                }
                body.put("action", legacyAction);
            }
        } catch (Exception exception) {
            source.showFailure();
            showActionFeedback("✕  快捷键配置无效，请进入设置修复", theme.danger);
            return;
        }

        source.showSending();
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
                    source.showSuccess();
                });
            } catch (Exception exception) {
                mainHandler.post(() -> {
                    showConnection("发送失败，请连接 USB 或蓝牙", theme.danger);
                    showActionFeedback("✕  电脑未确认快捷键：" + config.label,
                            theme.danger);
                    performResultHaptic(source, false);
                    source.showFailure();
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
        if (devices.isEmpty()) {
            TextView empty = text("连接电脑后，会自动出现在这里", 12,
                    theme.muted, Typeface.NORMAL);
            targetDeviceRow.addView(empty, new LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.WRAP_CONTENT, dp(38)));
            return;
        }

        String activeComputerId = targetDeviceManager.getActiveComputerId();
        for (TargetDeviceManager.Device device : devices) {
            boolean selected = sameComputer(device.computerId, activeComputerId);
            boolean online = isDeviceOnline(device.computerId);
            Button chip = smallButton(device.slot + "号");
            chip.setAllCaps(false);
            chip.setSingleLine(true);
            chip.setTextColor(selected ? theme.onPrimary : theme.text);
            chip.setBackground(theme.shape(
                    this,
                    selected ? theme.primary : theme.surfaceRaised,
                    16,
                    1,
                    selected ? theme.primary : theme.outline));
            chip.setAlpha(online ? 1f : 0.48f);
            chip.setEnabled(online);
            chip.setContentDescription(device.slot + "号电脑 " + device.displayName
                    + (online ? selected ? "，当前目标" : "，在线" : "，离线"));
            chip.setOnClickListener(view -> selectTargetDevice(device, chip));
            installTouchFeedback(chip);
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.WRAP_CONTENT, dp(40));
            params.rightMargin = dp(8);
            targetDeviceRow.addView(chip, params);
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
            showActionFeedback("✕  " + device.displayName + " 当前未连接", theme.danger);
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

    private boolean activeTypelessVirtualCableSelected() {
        LanTargetStatus lanStatus = targetComputerId == null
                ? null : lanTargets.get(targetComputerId);
        return lanStatus != null ? lanStatus.typelessVirtualCableSelected
                : isUsbTargetOnline() && typelessVirtualCableSelected;
    }

    private boolean activeManagedDictationSupported() {
        LanTargetStatus lanStatus = targetComputerId == null
                ? null : lanTargets.get(targetComputerId);
        return lanStatus != null ? lanStatus.managedDictationSupported
                : isUsbTargetOnline() && managedDictationSupported;
    }

    private void prepareBluetooth() {
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
        if (!usbConnected && !bluetoothConnected && lanTargets.isEmpty()) {
            showConnection("正在检测电脑端…", theme.muted);
        }
        connectionExecutor.execute(() -> {
            HttpURLConnection connection = null;
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
                    JSONObject typeless = health.optJSONObject("typeless");
                    typelessVirtualCableSelected = typeless != null
                            && typeless.optBoolean("virtualCableSelected", false);
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
                    mainHandler.post(() -> {
                        targetDeviceManager.upsert(
                                healthComputerId,
                                healthDisplayName,
                                healthPlatform);
                        applyStoredTarget();
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
                typelessVirtualCableSelected = false;
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
        connectionExecutor.execute(() -> {
            try {
                ConcurrentHashMap<String, LanTargetStatus> discovered =
                        new ConcurrentHashMap<>();
                for (TargetDeviceManager.Device device : targetDeviceManager.list()) {
                    PhoneDeckLanClient.ProbeResult result = PhoneDeckLanClient.probe(device);
                    if (result == null) {
                        continue;
                    }
                    JSONObject health = result.health;
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
                    JSONObject typeless = health.optJSONObject("typeless");
                    discovered.put(device.computerId, new LanTargetStatus(
                            result.endpoint,
                            health.optInt("protocolVersion", 0),
                            supportsManagedDictation,
                            audio != null && audio.optBoolean("available", false),
                            typeless != null
                                    && typeless.optBoolean("virtualCableSelected", false)));
                }
                lanTargets.clear();
                lanTargets.putAll(discovered);
                mainHandler.post(this::applyStoredTarget);
            } finally {
                lanCheckInFlight = false;
            }
        });
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
        showActionFeedback("✕  USB 已断开；电脑端会自动尝试复位 Typeless",
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
        actionExecutor.execute(() -> {
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
            if (MODE_TAP.equals(voiceMode)) {
                toggleTypelessWithPhoneMic();
            }
        });
        typelessButton.setOnTouchListener((view, event) -> {
            int action = event.getActionMasked();
            if (action == MotionEvent.ACTION_DOWN) {
                if (!view.isEnabled()) {
                    return true;
                }
                view.animate().scaleX(0.965f).scaleY(0.965f).setDuration(55).start();
                view.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY);
                view.playSoundEffect(SoundEffectConstants.CLICK);
                if (MODE_HOLD.equals(voiceMode)) {
                    holdGestureActive = true;
                    holdReleasePending = false;
                    if (!dictationActive && !audioStartPending && !typelessInFlight) {
                        beginPhoneDictation();
                    }
                    return true;
                }
            } else if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_CANCEL) {
                view.animate().scaleX(1f).scaleY(1f).setDuration(90).start();
                if (MODE_HOLD.equals(voiceMode)) {
                    boolean wasHolding = holdGestureActive;
                    holdGestureActive = false;
                    if (wasHolding) {
                        if (dictationActive && !typelessInFlight) {
                            stopPhoneDictation();
                        } else if (audioStartPending || typelessInFlight) {
                            holdReleasePending = true;
                            showActionFeedback("●  已松开，连接完成后会自动结束", theme.warning);
                        }
                    }
                    view.performClick();
                    return true;
                }
            }
            return false;
        });
    }

    private void updateVoiceModeInterface() {
        boolean holdMode = MODE_HOLD.equals(voiceMode);
        voiceModeText.setText(holdMode ? "按住说话模式" : "点击说话模式");
        voiceModeText.setTextColor(holdMode ? theme.warning : theme.primary);
        typelessButton.setContentDescription(holdMode
                ? "按住开始 Typeless 语音输入，松开结束"
                : "点击开始语音输入；再次点击同一按钮停止");
        updateVoiceControls();
    }

    private String voiceButtonLabel() {
        if (MODE_HOLD.equals(voiceMode)) {
            if (voiceBusyLabel != null) {
                return voiceBusyLabel;
            }
            return dictationActive ? "松开即可结束" : "按住说话";
        }
        if (dictationActive && typelessInFlight) {
            return "正在停止…";
        }
        if (isVoiceStarting()) {
            return "取消启动";
        }
        if (dictationActive) {
            return "停止说话";
        }
        if (voiceBusyLabel != null) {
            return voiceBusyLabel;
        }
        return "点击开始说话";
    }

    private void updateVoiceControls() {
        if (typelessButton == null) {
            return;
        }
        typelessButton.setText(voiceButtonLabel());
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
        typelessButton.setBackground(stopState
                ? pressableRoundRect(
                        PhoneDeckTheme.blend(theme.voiceDock, theme.danger, 0.72f),
                        theme.danger, 22)
                : pressableRoundRect(theme.primary, theme.primaryPressed, 22));
        typelessButton.setTextColor(stopState ? theme.text : theme.onPrimary);
        if (voiceIcon != null) {
            voiceIcon.setColor(stopState ? theme.text : theme.onPrimary);
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

    private void beginPhoneDictation() {
        PhoneDeckEndpoint endpoint = endpointForActiveTarget();
        if (endpoint == null) {
            if (isBluetoothTargetOnline()) {
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
            showConnection(targetDisplayName + " · 缺少 VB-CABLE", theme.warning);
            showActionFeedback("✕  电脑未检测到 VB-CABLE，未启动 Typeless", theme.danger);
            microphoneLevel.setText("手机麦克风  ○ 未启动");
            microphoneLevel.setTextColor(theme.muted);
            testConnection();
            return;
        }
        if (!activeTypelessVirtualCableSelected()) {
            showConnection(targetDisplayName + " · Typeless 麦克风未配置", theme.warning);
            showActionFeedback("✕  请先在 Typeless 中选择 CABLE Output", theme.danger);
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
        currentSessionManaged = activeManagedDictationSupported();
        currentSessionTargetComputerId = serverProtocolVersion >= 2
                ? targetComputerId : null;
        currentSessionEndpoint = endpoint;
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
        showActionFeedback("●  手机麦克风已连接，正在唤醒 Typeless…", theme.warning);
        setTypelessBusy("正在唤醒 Typeless…");
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
        actionExecutor.execute(() -> {
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
                    audioStreamer.stop();
                }
                mainHandler.post(() -> {
                    if (!sessionId.equals(currentSessionId)) {
                        return;
                    }
                    dictationActive = starting;
                    if (starting) {
                        dictationPaused = false;
                    }
                    showConnection(transport + " 已连接", theme.success);
                    showActionFeedback(starting
                                    ? "✓  Typeless 正在使用手机麦克风听写 · " + transport
                                    : "✓  Typeless 已停止并正在输入文字 · " + transport,
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
                intentionalAudioStopSessionId = sessionId;
                audioStreamer.stop();
                mainHandler.post(() -> {
                    if (!sessionId.equals(currentSessionId)) {
                        return;
                    }
                    holdReleasePending = false;
                    clearVoiceSessionState();
                    showConnection("Typeless 指令发送失败", theme.danger);
                    showActionFeedback("✕  电脑没有确认，请检查 Wi-Fi/USB 连接后重试", theme.danger);
                    performResultHaptic(typelessButton, false);
                    flashResult(typelessButton, theme.danger);
                    finishGuardedAction(typelessButton, true);
                });
            }
        });
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
        String endpoint = starting ? "/api/dictation/start" : "/api/dictation/stop";
        if (!postEndpointWithRetry(sessionEndpoint, endpoint, body, 2)) {
            throw new IllegalStateException("电脑端未确认 Typeless 会话");
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
        actionExecutor.execute(() -> {
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
            if (postEndpoint(connectionEndpoint, endpoint, body)) {
                return true;
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

    private boolean postEndpoint(
            PhoneDeckEndpoint connectionEndpoint,
            String endpoint,
            JSONObject body) {
        try {
            PhoneDeckHttp.postJson(connectionEndpoint, endpoint, body, 1800);
            return true;
        } catch (Exception exception) {
            return false;
        }
    }

    private void updateConnectionDisplay() {
        if (isLanTargetOnline()) {
            if (!activePhoneAudioAvailable()) {
                showConnection(targetDisplayName + " · Wi-Fi · 缺少 VB-CABLE", theme.warning);
            } else if (!activeTypelessVirtualCableSelected()) {
                showConnection(targetDisplayName + " · Wi-Fi · 麦克风未配置", theme.warning);
            } else {
                showConnection(targetDisplayName + " · Wi-Fi 在线", theme.success);
            }
        } else if (isUsbTargetOnline()) {
            if (!activePhoneAudioAvailable()) {
                showConnection(targetDisplayName + " · 缺少 VB-CABLE", theme.warning);
            } else if (!activeTypelessVirtualCableSelected()) {
                showConnection(targetDisplayName + " · 麦克风未配置", theme.warning);
            } else {
                showConnection(targetDisplayName + " · USB 在线", theme.success);
            }
        } else if (isBluetoothTargetOnline()) {
            showConnection(targetDisplayName + " · 蓝牙快捷键在线", theme.success);
        } else if (targetComputerId != null) {
            showConnection(targetDisplayName + " · 当前离线", theme.muted);
        } else {
            showConnection("等待电脑连接", theme.muted);
        }
    }

    private void showConnection(String title, int color) {
        statusText.setText(title);
        statusText.setTextColor(theme.text);
        statusDot.setBackground(roundRect(color, 20));
    }

    private void showActionFeedback(String message, int color) {
        actionFeedback.setText(message);
        actionFeedback.setTextColor(color);
        actionFeedback.setBackground(theme.shape(
                this, theme.feedbackSurface(color), 14, 1,
                PhoneDeckTheme.blend(theme.outline, color, 0.35f)));
        actionFeedback.announceForAccessibility(message);
    }

    private void finishGuardedAction(Button source, boolean guarded) {
        if (!guarded) {
            return;
        }
        typelessInFlight = false;
        voiceBusyLabel = null;
        updateVoiceControls();
    }

    private void installTouchFeedback(View control) {
        control.setSoundEffectsEnabled(true);
        control.setHapticFeedbackEnabled(true);
        control.setOnTouchListener((view, event) -> {
            if (event.getActionMasked() == MotionEvent.ACTION_DOWN && view.isEnabled()) {
                view.animate().scaleX(0.965f).scaleY(0.965f).setDuration(55).start();
                view.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY);
                view.playSoundEffect(SoundEffectConstants.CLICK);
            } else if (event.getActionMasked() == MotionEvent.ACTION_UP
                    || event.getActionMasked() == MotionEvent.ACTION_CANCEL) {
                view.animate().scaleX(1f).scaleY(1f).setDuration(90).start();
            }
            return false;
        });
    }

    private void performResultHaptic(View view, boolean success) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            view.performHapticFeedback(success
                    ? HapticFeedbackConstants.CONFIRM
                    : HapticFeedbackConstants.REJECT);
        } else {
            view.performHapticFeedback(success
                    ? HapticFeedbackConstants.VIRTUAL_KEY
                    : HapticFeedbackConstants.LONG_PRESS);
        }
    }

    private void flashResult(View view, int color) {
        view.animate().cancel();
        view.setScaleX(1f);
        view.setScaleY(1f);
        view.animate().alpha(0.62f).setDuration(80).withEndAction(() ->
                view.animate().alpha(view.isEnabled() ? 1f : 0.74f).setDuration(140).start()
        ).start();
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
        }
    }

    @Override
    protected void onDestroy() {
        mainHandler.removeCallbacks(periodicHealthCheck);
        if (bluetoothTransport != null) {
            bluetoothTransport.close();
        }
        if (audioStreamer != null) {
            intentionalAudioStopSessionId = currentSessionId;
            audioStreamer.close();
        }
        clearVoiceSessionState();
        actionExecutor.shutdownNow();
        connectionExecutor.shutdownNow();
        super.onDestroy();
    }

    private Button smallButton(String label) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextSize(12);
        button.setTextColor(theme.text);
        button.setAllCaps(false);
        button.setPadding(dp(4), 0, dp(4), 0);
        button.setBackground(theme.pressable(
                this, theme.key, PhoneDeckTheme.blend(theme.key, theme.primary, 0.18f), 12));
        button.setStateListAnimator(null);
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

    private StateListDrawable pressableRoundRect(int normalColor, int pressedColor, int radiusDp) {
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
        final boolean typelessVirtualCableSelected;

        LanTargetStatus(
                PhoneDeckEndpoint endpoint,
                int protocolVersion,
                boolean managedDictationSupported,
                boolean phoneAudioAvailable,
                boolean typelessVirtualCableSelected) {
            this.endpoint = endpoint;
            this.protocolVersion = protocolVersion;
            this.managedDictationSupported = managedDictationSupported;
            this.phoneAudioAvailable = phoneAudioAvailable;
            this.typelessVirtualCableSelected = typelessVirtualCableSelected;
        }
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
