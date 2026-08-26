package com.codex.phonedeck;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.StateListDrawable;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.HapticFeedbackConstants;
import android.view.MotionEvent;
import android.view.SoundEffectConstants;
import android.view.View;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.GridLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.UUID;
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
    private static final int COLOR_BACKGROUND = Color.rgb(11, 16, 32);
    private static final int COLOR_PANEL = Color.rgb(22, 29, 50);
    private static final int COLOR_KEY = Color.rgb(31, 41, 68);
    private static final int COLOR_PRIMARY = Color.rgb(121, 168, 255);
    private static final int COLOR_TEXT = Color.rgb(247, 249, 255);
    private static final int COLOR_MUTED = Color.rgb(159, 172, 202);
    private static final int COLOR_SUCCESS = Color.rgb(79, 220, 156);
    private static final int COLOR_PENDING = Color.rgb(255, 195, 92);
    private static final int COLOR_DANGER = Color.rgb(255, 112, 132);

    private final ExecutorService actionExecutor = Executors.newSingleThreadExecutor();
    private final ExecutorService connectionExecutor = Executors.newSingleThreadExecutor();
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private TextView statusText;
    private View statusDot;
    private TextView actionFeedback;
    private TextView microphoneLevel;
    private TextView voiceModeText;
    private Button typelessButton;
    private LinearLayout voiceActionRow;
    private Button pauseResumeButton;
    private GridLayout shortcutGrid;
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
    private volatile String intentionalAudioStopSessionId;
    private volatile boolean usbConnected;
    private volatile boolean managedDictationSupported;
    private volatile boolean phoneAudioAvailable;
    private volatile boolean typelessVirtualCableSelected;
    private volatile int serverProtocolVersion;
    private volatile String targetComputerId;
    private final String clientSessionId = UUID.randomUUID().toString();
    private volatile boolean bluetoothConnected;
    private volatile String bluetoothDetail = "等待电脑蓝牙连接";
    private BluetoothTransport bluetoothTransport;
    private AudioStreamer audioStreamer;
    private ShortcutConfigRepository configRepository;
    private final Runnable periodicHealthCheck = new Runnable() {
        @Override
        public void run() {
            if (isFinishing() || isDestroyed()) {
                return;
            }
            testConnection();
            mainHandler.postDelayed(this, HEALTH_CHECK_INTERVAL_MS);
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        getWindow().setStatusBarColor(COLOR_BACKGROUND);
        getWindow().setNavigationBarColor(COLOR_BACKGROUND);

        configRepository = new ShortcutConfigRepository(this);
        setContentView(createInterface());
        audioStreamer = new AudioStreamer(this, SERVER, new AudioStreamer.Listener() {
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
        mainHandler.postDelayed(periodicHealthCheck, HEALTH_CHECK_INTERVAL_MS);
    }

    @Override
    protected void onResume() {
        super.onResume();
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
        root.setBackgroundColor(COLOR_BACKGROUND);

        ScrollView scrollView = new ScrollView(this);
        scrollView.setFillViewport(true);
        scrollView.setBackgroundColor(COLOR_BACKGROUND);

        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(18), dp(20), dp(18), dp(230));
        scrollView.addView(page, new ScrollView.LayoutParams(
                ScrollView.LayoutParams.MATCH_PARENT,
                ScrollView.LayoutParams.WRAP_CONTENT));

        TextView eyebrow = text("PHONE DECK  ·  USB / BLUETOOTH", 12, COLOR_PRIMARY, Typeface.BOLD);
        eyebrow.setLetterSpacing(0.12f);
        page.addView(eyebrow);

        TextView title = text("PhoneDeck 手机控制台", 28, COLOR_TEXT, Typeface.BOLD);
        page.addView(title, marginTop(dp(4)));

        TextView subtitle = text("电脑端 Typeless · USB / 蓝牙双连接", 14, COLOR_MUTED, Typeface.NORMAL);
        page.addView(subtitle, marginTop(dp(2)));

        LinearLayout connection = new LinearLayout(this);
        connection.setOrientation(LinearLayout.HORIZONTAL);
        connection.setGravity(Gravity.CENTER_VERTICAL);
        connection.setPadding(dp(14), dp(10), dp(8), dp(10));
        connection.setBackground(roundRect(COLOR_PANEL, 16));
        page.addView(connection, margins(dp(0), dp(18), dp(0), dp(14),
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        statusDot = new View(this);
        statusDot.setBackground(roundRect(COLOR_MUTED, 20));
        connection.addView(statusDot, new LinearLayout.LayoutParams(dp(9), dp(9)));

        statusText = text("正在检测电脑端…", 13, COLOR_MUTED, Typeface.NORMAL);
        LinearLayout.LayoutParams statusParams = new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        statusParams.leftMargin = dp(9);
        connection.addView(statusText, statusParams);

        Button retry = smallButton("重试");
        retry.setOnClickListener(view -> {
            startBluetoothTransport();
            testConnection();
        });
        installTouchFeedback(retry);
        connection.addView(retry, new LinearLayout.LayoutParams(dp(60), dp(38)));

        Button settings = smallButton("设置");
        settings.setOnClickListener(view -> startActivity(new Intent(this, SettingsActivity.class)));
        installTouchFeedback(settings);
        LinearLayout.LayoutParams settingsParams = new LinearLayout.LayoutParams(dp(60), dp(38));
        settingsParams.leftMargin = dp(6);
        connection.addView(settings, settingsParams);

        TextView shortcutTitle = text("快捷键", 18, COLOR_TEXT, Typeface.BOLD);
        page.addView(shortcutTitle, marginTop(dp(4)));

        TextView shortcutHint = text("按键会发送到电脑上当前正在使用的窗口", 12, COLOR_MUTED, Typeface.NORMAL);
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
        voiceDock.setBackground(roundRect(Color.rgb(18, 25, 45), 22));
        voiceDock.setElevation(dp(14));

        LinearLayout dockHeader = new LinearLayout(this);
        dockHeader.setOrientation(LinearLayout.HORIZONTAL);
        dockHeader.setGravity(Gravity.CENTER_VERTICAL);
        voiceDock.addView(dockHeader, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        TextView voiceTitle = text("语音输入", 15, COLOR_TEXT, Typeface.BOLD);
        dockHeader.addView(voiceTitle, new LinearLayout.LayoutParams(0,
                LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        voiceModeText = text("点击说话模式", 12, COLOR_PRIMARY, Typeface.BOLD);
        voiceModeText.setGravity(Gravity.END);
        dockHeader.addView(voiceModeText);

        typelessButton = new Button(this);
        typelessButton.setText("●  点击开始说话");
        typelessButton.setTextSize(18);
        typelessButton.setTextColor(COLOR_BACKGROUND);
        typelessButton.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        typelessButton.setAllCaps(false);
        typelessButton.setGravity(Gravity.CENTER);
        typelessButton.setPadding(dp(12), 0, dp(12), 0);
        typelessButton.setBackground(pressableRoundRect(
                COLOR_PRIMARY, Color.rgb(164, 196, 255), 22));
        typelessButton.setStateListAnimator(null);
        typelessButton.setContentDescription("Typeless 语音输入");
        installVoiceGesture();
        voiceDock.addView(typelessButton, margins(dp(0), dp(7), dp(0), dp(0),
                LinearLayout.LayoutParams.MATCH_PARENT, dp(72)));

        voiceActionRow = new LinearLayout(this);
        voiceActionRow.setOrientation(LinearLayout.HORIZONTAL);
        voiceActionRow.setGravity(Gravity.CENTER);

        pauseResumeButton = smallButton("Ⅱ  暂停");
        pauseResumeButton.setTextSize(15);
        pauseResumeButton.setBackground(pressableRoundRect(
                Color.rgb(40, 58, 94), Color.rgb(55, 78, 122), 16));
        pauseResumeButton.setContentDescription("暂停或继续手机语音输入");
        pauseResumeButton.setOnClickListener(view -> toggleDictationPause());
        installTouchFeedback(pauseResumeButton);
        voiceActionRow.addView(pauseResumeButton, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(52)));
        voiceDock.addView(voiceActionRow, margins(dp(0), dp(7), dp(0), dp(0),
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT));

        actionFeedback = text("●  准备就绪 · 操作状态会显示在这里",
                13, COLOR_MUTED, Typeface.BOLD);
        actionFeedback.setGravity(Gravity.CENTER_VERTICAL);
        actionFeedback.setPadding(dp(15), dp(12), dp(15), dp(12));
        actionFeedback.setBackground(roundRect(COLOR_PANEL, 14));
        voiceDock.addView(actionFeedback, margins(dp(0), dp(7), dp(0), dp(0),
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT));

        microphoneLevel = text("手机麦克风  ○ 未启动", 13, COLOR_MUTED, Typeface.BOLD);
        microphoneLevel.setPadding(dp(15), dp(7), dp(15), dp(7));
        voiceDock.addView(microphoneLevel, margins(dp(0), dp(2), dp(0), dp(0),
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT));

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
                    showActionFeedback("●  " + recoveryNotice, COLOR_PENDING);
                }
            }
        } catch (Exception exception) {
            if (actionFeedback != null) {
                showActionFeedback("✕  无法读取快捷键配置；语音输入仍可使用", COLOR_DANGER);
            }
        }
    }

    private void addKey(GridLayout grid, ShortcutButtonConfig config) {
        Button button = new Button(this);
        String iconLine = config.icon.isEmpty() ? "" : config.icon + "  ";
        button.setText(iconLine + config.label + "\n" + config.subtitle());
        button.setTextSize(config.label.length() <= 2 ? 18 : 12);
        button.setTextColor(COLOR_TEXT);
        button.setAllCaps(false);
        button.setGravity(Gravity.CENTER);
        button.setPadding(dp(4), dp(4), dp(4), dp(4));
        button.setBackground(pressableRoundRect(
                colorForShortcut(config.color), Color.rgb(74, 91, 138), 15));
        button.setStateListAnimator(null);
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
        params.height = dp(66);
        params.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
        params.setMargins(dp(4), dp(4), dp(4), dp(4));
        grid.addView(button, params);
    }

    private int colorForShortcut(String color) {
        switch (color) {
            case "purple": return Color.rgb(76, 57, 112);
            case "green": return Color.rgb(34, 84, 72);
            case "orange": return Color.rgb(102, 69, 37);
            case "red": return Color.rgb(103, 49, 63);
            case "slate": return COLOR_KEY;
            default: return Color.rgb(39, 66, 112);
        }
    }

    private void triggerShortcut(Button source, ShortcutButtonConfig config) {
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
                    showActionFeedback("✕  自定义按键需要电脑端升级到 1.5.0", COLOR_DANGER);
                    return;
                }
                body.put("action", legacyAction);
            }
        } catch (Exception exception) {
            showActionFeedback("✕  快捷键配置无效，请进入设置修复", COLOR_DANGER);
            return;
        }

        showActionFeedback("●  正在发送：" + config.label + " · " + config.subtitle(),
                COLOR_PENDING);
        actionExecutor.execute(() -> {
            try {
                String transport = sendCommand(body);
                mainHandler.post(() -> {
                    showConnection(transport + " 已连接", COLOR_SUCCESS);
                    showActionFeedback("✓  已发送：" + config.label + " · " + transport,
                            COLOR_SUCCESS);
                    performResultHaptic(source, true);
                    flashResult(source, COLOR_SUCCESS);
                });
            } catch (Exception exception) {
                mainHandler.post(() -> {
                    showConnection("发送失败，请连接 USB 或蓝牙", COLOR_DANGER);
                    showActionFeedback("✕  电脑未确认快捷键：" + config.label,
                            COLOR_DANGER);
                    performResultHaptic(source, false);
                    flashResult(source, COLOR_DANGER);
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

    private void prepareBluetooth() {
        bluetoothTransport = new BluetoothTransport(this, (connected, detail) -> mainHandler.post(() -> {
            bluetoothConnected = connected;
            bluetoothDetail = detail;
            if (connected && bluetoothTransport != null
                    && bluetoothTransport.getComputerId() != null) {
                targetComputerId = bluetoothTransport.getComputerId();
                serverProtocolVersion = Math.max(
                        serverProtocolVersion, bluetoothTransport.getProtocolVersion());
            }
            updateConnectionDisplay();
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
        if (!usbConnected && !bluetoothConnected) {
            showConnection("正在检测电脑端…", COLOR_MUTED);
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
                    org.json.JSONArray capabilities = health.optJSONArray("capabilities");
                    if (capabilities != null) {
                        for (int index = 0; index < capabilities.length(); index++) {
                            if ("managedDictation".equals(capabilities.optString(index))) {
                                supportsManagedDictation = true;
                                break;
                            }
                        }
                    }
                    managedDictationSupported = supportsManagedDictation;
                    serverProtocolVersion = health.optInt("protocolVersion", 0);
                    JSONObject audio = health.optJSONObject("audio");
                    phoneAudioAvailable = audio != null
                            && audio.optBoolean("available", false);
                    JSONObject typeless = health.optJSONObject("typeless");
                    typelessVirtualCableSelected = typeless != null
                            && typeless.optBoolean("virtualCableSelected", false);
                    String healthComputerId = health.optString("computerId", null);
                    if (healthComputerId != null && !healthComputerId.isBlank()) {
                        targetComputerId = healthComputerId;
                    }
                    usbConnected = true;
                    mainHandler.post(this::updateConnectionDisplay);
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
                    updateConnectionDisplay();
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
        if (!audioStartPending && !dictationActive && !typelessInFlight
                && (audioStreamer == null || !audioStreamer.isRunning())) {
            return;
        }
        intentionalAudioStopSessionId = currentSessionId;
        if (audioStreamer != null) {
            audioStreamer.stop();
        }
        clearVoiceSessionState();
        microphoneLevel.setText("手机麦克风  ✕ USB 已断开");
        microphoneLevel.setTextColor(COLOR_DANGER);
        showActionFeedback("✕  USB 已断开；电脑端会自动尝试复位 Typeless",
                COLOR_DANGER);
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
            showActionFeedback("●  当前没有正在进行的听写", COLOR_MUTED);
        }
    }

    private void toggleDictationPause() {
        if (!dictationActive || typelessInFlight || audioStreamer == null) {
            return;
        }
        if (dictationPaused) {
            if (!audioStreamer.resume()) {
                showActionFeedback("✕  音频会话已经结束，请重新开始", COLOR_DANGER);
                clearVoiceSessionState();
                return;
            }
            dictationPaused = false;
            microphoneLevel.setText("手机麦克风  ◌ 正在恢复");
            microphoneLevel.setTextColor(COLOR_PENDING);
            showActionFeedback("▶  已继续，可以接着说话", COLOR_SUCCESS);
        } else {
            if (!audioStreamer.pause()) {
                showActionFeedback("✕  暂停失败，音频会话可能已经结束", COLOR_DANGER);
                return;
            }
            dictationPaused = true;
            microphoneLevel.setText("手机麦克风  Ⅱ 已暂停（未采集声音）");
            microphoneLevel.setTextColor(COLOR_PENDING);
            showActionFeedback("Ⅱ  已暂停；点击继续可接着说，点击上方主按钮可停止", COLOR_PENDING);
        }
        performResultHaptic(pauseResumeButton, true);
        updateVoiceControls();
    }

    private void cancelPendingDictation() {
        String sessionId = currentSessionId;
        boolean managed = currentSessionManaged;
        intentionalAudioStopSessionId = sessionId;
        if (audioStreamer != null) {
            audioStreamer.stop();
        }
        clearVoiceSessionState();
        microphoneLevel.setText("手机麦克风  ○ 已取消");
        microphoneLevel.setTextColor(COLOR_MUTED);
        showActionFeedback("✓  已立即取消语音启动", COLOR_MUTED);
        performResultHaptic(typelessButton, true);
        if (managed && sessionId != null) {
            bestEffortStopManagedDictation(sessionId);
        }
    }

    private void bestEffortStopManagedDictation(String sessionId) {
        actionExecutor.execute(() -> {
            try {
                JSONObject body = new JSONObject();
                body.put("sessionId", sessionId);
                body.put("requestId", UUID.randomUUID().toString());
                postUsbWithRetry("/api/dictation/stop", body, 2);
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
                            showActionFeedback("●  已松开，连接完成后会自动结束", COLOR_PENDING);
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
        voiceModeText.setTextColor(holdMode ? COLOR_PENDING : COLOR_PRIMARY);
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
            return dictationActive ? "■  松开即可结束" : "●  按住说话";
        }
        if (dictationActive && typelessInFlight) {
            return "■  正在停止…";
        }
        if (isVoiceStarting()) {
            return "×  取消启动";
        }
        if (dictationActive) {
            return "■  停止说话";
        }
        if (voiceBusyLabel != null) {
            return voiceBusyLabel;
        }
        return "●  点击开始说话";
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
        typelessButton.setBackground(stopState
                ? pressableRoundRect(Color.rgb(176, 67, 87), Color.rgb(211, 84, 105), 22)
                : pressableRoundRect(COLOR_PRIMARY, Color.rgb(164, 196, 255), 22));
        typelessButton.setTextColor(stopState ? COLOR_TEXT : COLOR_BACKGROUND);
        typelessButton.setContentDescription(holdMode
                ? "按住开始手机语音输入，松开停止"
                : starting
                ? "取消语音启动"
                : dictationActive
                ? "停止并完成手机语音输入"
                : "开始手机语音输入");
        if (voiceActionRow != null) {
            voiceActionRow.setVisibility(holdMode ? View.GONE : View.VISIBLE);
        }
        if (holdMode) {
            setControlEnabled(typelessButton, !typelessInFlight || holdGestureActive);
            return;
        }

        setControlEnabled(typelessButton, !stopping);
        if (pauseResumeButton != null) {
            pauseResumeButton.setText(dictationPaused ? "▶  继续" : "Ⅱ  暂停");
            pauseResumeButton.setContentDescription(dictationPaused
                    ? "继续手机语音输入" : "暂停手机语音输入");
            setControlEnabled(pauseResumeButton, dictationActive && !typelessInFlight);
        }
    }

    private void setControlEnabled(Button button, boolean enabled) {
        button.setEnabled(enabled);
        button.setAlpha(enabled ? 1f : 0.45f);
    }

    private void beginPhoneDictation() {
        if (!usbConnected) {
            showConnection("手机音频需要 USB 连接", COLOR_DANGER);
            showActionFeedback("✕  请连接 USB，并重新运行电脑端启动脚本", COLOR_DANGER);
            testConnection();
            return;
        }
        if (!phoneAudioAvailable) {
            showConnection("USB 已连接 · 缺少 VB-CABLE", COLOR_PENDING);
            showActionFeedback("✕  电脑未检测到 VB-CABLE，未启动 Typeless", COLOR_DANGER);
            microphoneLevel.setText("手机麦克风  ○ 未启动");
            microphoneLevel.setTextColor(COLOR_MUTED);
            testConnection();
            return;
        }
        if (!typelessVirtualCableSelected) {
            showConnection("USB 已连接 · Typeless 麦克风未配置", COLOR_PENDING);
            showActionFeedback("✕  请先在 Typeless 中选择 CABLE Output", COLOR_DANGER);
            microphoneLevel.setText("手机麦克风  ○ 未启动");
            microphoneLevel.setTextColor(COLOR_MUTED);
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
        currentSessionManaged = managedDictationSupported;
        intentionalAudioStopSessionId = null;
        setTypelessBusy("正在连接手机麦克风…");
        showActionFeedback("●  正在建立 USB 音频通道…", COLOR_PENDING);
        microphoneLevel.setText("手机麦克风  ◌ 正在连接");
        microphoneLevel.setTextColor(COLOR_PENDING);
        if (!audioStreamer.start(currentSessionId)) {
            clearVoiceSessionState();
            showActionFeedback("✕  上一个手机音频会话仍在收尾，请稍后重试", COLOR_DANGER);
        }
    }

    private void stopPhoneDictation() {
        if (currentSessionId == null) {
            clearVoiceSessionState();
            showActionFeedback("●  当前没有需要停止的听写会话", COLOR_MUTED);
            return;
        }
        intentionalAudioStopSessionId = currentSessionId;
        dictationPaused = false;
        setTypelessBusy("正在结束听写…");
        showActionFeedback("■  手机录音已停止，正在让电脑完成文字…", COLOR_PENDING);
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
        showActionFeedback("●  手机麦克风已连接，正在唤醒 Typeless…", COLOR_PENDING);
        setTypelessBusy("正在唤醒 Typeless…");
        sendTypelessToggle(true);
    }

    private void sendTypelessToggle(boolean starting) {
        final String sessionId = currentSessionId;
        if (sessionId == null) {
            clearVoiceSessionState();
            showActionFeedback("✕  听写会话已失效，请重新开始", COLOR_DANGER);
            return;
        }
        final boolean managed = currentSessionManaged;
        actionExecutor.execute(() -> {
            try {
                String transport;
                if (managed) {
                    transport = sendManagedDictationCommand(starting, sessionId);
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
                    showConnection(transport + " 已连接", COLOR_SUCCESS);
                    showActionFeedback(starting
                                    ? "✓  Typeless 正在使用手机麦克风听写 · " + transport
                                    : "✓  Typeless 已停止并正在输入文字 · " + transport,
                            COLOR_SUCCESS);
                    performResultHaptic(typelessButton, true);
                    flashResult(typelessButton, COLOR_SUCCESS);
                    finishGuardedAction(typelessButton, true);
                    if (starting && holdReleasePending && !holdGestureActive) {
                        holdReleasePending = false;
                        mainHandler.postDelayed(this::stopPhoneDictation, 80);
                    }
                    if (!starting) {
                        clearVoiceSessionState();
                        microphoneLevel.setText("手机麦克风  ○ 已停止");
                        microphoneLevel.setTextColor(COLOR_MUTED);
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
                    showConnection("Typeless 指令发送失败", COLOR_DANGER);
                    showActionFeedback("✕  电脑没有确认，请检查 USB 连接后重试", COLOR_DANGER);
                    performResultHaptic(typelessButton, false);
                    flashResult(typelessButton, COLOR_DANGER);
                    finishGuardedAction(typelessButton, true);
                });
            }
        });
    }

    private String sendManagedDictationCommand(boolean starting, String sessionId)
            throws Exception {
        JSONObject body = new JSONObject();
        body.put("sessionId", sessionId);
        body.put("requestId", UUID.randomUUID().toString());
        String endpoint = starting ? "/api/dictation/start" : "/api/dictation/stop";
        if (!postUsbWithRetry(endpoint, body, 2)) {
            usbConnected = false;
            throw new IllegalStateException("电脑端未确认 Typeless 会话");
        }
        return "USB";
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
            microphoneLevel.setTextColor(COLOR_PENDING);
            return;
        }
        int bars = Math.min(10, Math.max(0, (percent + 9) / 10));
        StringBuilder meter = new StringBuilder(10);
        for (int index = 0; index < 10; index++) {
            meter.append(index < bars ? '▮' : '▯');
        }
        microphoneLevel.setText("手机麦克风  " + meter + "  " + percent + "%");
        microphoneLevel.setTextColor(percent > 0 ? COLOR_SUCCESS : COLOR_MUTED);
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
        microphoneLevel.setTextColor(COLOR_DANGER);
        showActionFeedback("✕  手机音频中断" + (reason == null ? "" : "：" + reason),
                COLOR_DANGER);
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
        voiceBusyLabel = null;
        if (typelessButton != null) {
            updateVoiceControls();
        }
    }

    private String sendCommand(JSONObject body) throws Exception {
        boolean sent = false;
        String transport = "";
        boolean usbAttempted = false;

        if (usbConnected) {
            usbAttempted = true;
            sent = postUsbWithRetry("/api/input", body, 2);
            if (sent) {
                transport = "USB";
            } else {
                usbConnected = false;
            }
        }

        if (!sent && bluetoothTransport != null && bluetoothTransport.isConnected()) {
            sent = bluetoothTransport.sendAndWaitForAck(body, 1400);
            if (sent) {
                transport = "蓝牙";
            }
        }

        if (!sent && !usbAttempted) {
            sent = postUsbWithRetry("/api/input", body, 2);
            if (sent) {
                usbConnected = true;
                transport = "USB";
            }
        }

        if (!sent) {
            throw new IllegalStateException("没有可用连接");
        }
        return transport;
    }

    private boolean postUsbWithRetry(String endpoint, JSONObject body, int attempts) {
        for (int attempt = 0; attempt < attempts; attempt++) {
            if (postUsb(endpoint, body)) {
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

    private boolean postUsb(String endpoint, JSONObject body) {
        HttpURLConnection connection = null;
        try {
            byte[] bytes = body.toString().getBytes(StandardCharsets.UTF_8);
            connection = (HttpURLConnection) new URL(SERVER + endpoint).openConnection();
            connection.setConnectTimeout(500);
            connection.setReadTimeout(1600);
            connection.setRequestMethod("POST");
            connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
            connection.setFixedLengthStreamingMode(bytes.length);
            connection.setDoOutput(true);
            try (OutputStream output = connection.getOutputStream()) {
                output.write(bytes);
            }
            int response = connection.getResponseCode();
            return response >= 200 && response < 300;
        } catch (Exception exception) {
            return false;
        } finally {
            if (connection != null) {
                connection.disconnect();
            }
        }
    }

    private void updateConnectionDisplay() {
        if (usbConnected) {
            if (!phoneAudioAvailable) {
                showConnection("USB 已连接 · 缺少 VB-CABLE", COLOR_PENDING);
            } else if (!typelessVirtualCableSelected) {
                showConnection("USB 已连接 · Typeless 麦克风未配置", COLOR_PENDING);
            } else {
                showConnection("USB 已连接", COLOR_SUCCESS);
            }
        } else if (bluetoothConnected) {
            showConnection(bluetoothDetail, COLOR_SUCCESS);
        } else if (bluetoothTransport != null && bluetoothTransport.isSupported()) {
            showConnection(bluetoothDetail, COLOR_MUTED);
        } else {
            showConnection("等待 USB 连接", COLOR_MUTED);
        }
    }

    private void showConnection(String message, int color) {
        statusText.setText(message);
        statusText.setTextColor(color);
        statusDot.setBackground(roundRect(color, 20));
    }

    private void showActionFeedback(String message, int color) {
        actionFeedback.setText(message);
        actionFeedback.setTextColor(color);
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

    private void installTouchFeedback(Button button) {
        button.setSoundEffectsEnabled(true);
        button.setHapticFeedbackEnabled(true);
        button.setOnTouchListener((view, event) -> {
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
                showActionFeedback("✕  未授予麦克风权限，无法传输手机声音", COLOR_DANGER);
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
        button.setTextColor(COLOR_TEXT);
        button.setAllCaps(false);
        button.setPadding(dp(4), 0, dp(4), 0);
        button.setBackgroundTintList(ColorStateList.valueOf(COLOR_KEY));
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
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(color);
        drawable.setCornerRadius(dp(radiusDp));
        return drawable;
    }

    private StateListDrawable pressableRoundRect(int normalColor, int pressedColor, int radiusDp) {
        StateListDrawable states = new StateListDrawable();
        states.addState(new int[]{android.R.attr.state_pressed},
                roundRect(pressedColor, radiusDp));
        states.addState(new int[]{}, roundRect(normalColor, radiusDp));
        return states;
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

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
