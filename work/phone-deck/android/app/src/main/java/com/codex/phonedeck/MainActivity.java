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
    private boolean typelessInFlight;
    private boolean audioStartPending;
    private boolean dictationActive;
    private boolean intentionalAudioStop;
    private boolean holdGestureActive;
    private boolean holdReleasePending;
    private String voiceMode = MODE_TAP;
    private volatile boolean usbConnected;
    private volatile boolean bluetoothConnected;
    private volatile String bluetoothDetail = "等待电脑蓝牙连接";
    private BluetoothTransport bluetoothTransport;
    private AudioStreamer audioStreamer;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        getWindow().setStatusBarColor(COLOR_BACKGROUND);
        getWindow().setNavigationBarColor(COLOR_BACKGROUND);

        setContentView(createInterface());
        audioStreamer = new AudioStreamer(this, SERVER, new AudioStreamer.Listener() {
            @Override
            public void onReady() {
                mainHandler.post(MainActivity.this::onAudioReady);
            }

            @Override
            public void onLevel(int percent) {
                mainHandler.post(() -> updateMicrophoneLevel(percent));
            }

            @Override
            public void onStopped(String reason) {
                mainHandler.post(() -> onAudioStopped(reason));
            }
        });
        prepareBluetooth();
        testConnection();
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

        TextView title = text("手机键盘", 30, COLOR_TEXT, Typeface.BOLD);
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
        page.addView(grid, margins(dp(-4), dp(10), dp(-4), dp(0),
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        addKey(grid, "复制\nCtrl+C", "copy");
        addKey(grid, "粘贴\nCtrl+V", "paste");
        addKey(grid, "剪切\nCtrl+X", "cut");
        addKey(grid, "撤销\nCtrl+Z", "undo");
        addKey(grid, "重做\nCtrl+Y", "redo");
        addKey(grid, "全选\nCtrl+A", "selectAll");
        addKey(grid, "保存\nCtrl+S", "save");
        addKey(grid, "切换窗口\nAlt+Tab", "altTab");
        addKey(grid, "截图\nWin+Shift+S", "screenshot");
        addKey(grid, "回车\nEnter", "enter");
        addKey(grid, "退格\nBackspace", "backspace");
        addKey(grid, "退出\nEsc", "escape");
        addKey(grid, "切换输入法\nWin+Space", "switchInputMethod");
        addKey(grid, "音量 −", "volumeDown");
        addKey(grid, "静音", "volumeMute");
        addKey(grid, "音量 +", "volumeUp");
        addKey(grid, "←", "left");
        addKey(grid, "→", "right");

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

    private void addKey(GridLayout grid, String label, String action) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextSize(label.length() <= 2 ? 20 : 13);
        button.setTextColor(COLOR_TEXT);
        button.setAllCaps(false);
        button.setGravity(Gravity.CENTER);
        button.setPadding(dp(4), dp(4), dp(4), dp(4));
        button.setBackground(pressableRoundRect(
                COLOR_KEY, Color.rgb(57, 73, 116), 15));
        button.setStateListAnimator(null);
        installTouchFeedback(button);
        button.setOnClickListener(view -> triggerAction(
                button,
                action,
                null,
                "正在发送：" + label.replace("\n", " "),
                "已发送：" + label.replace("\n", " "),
                false));

        GridLayout.LayoutParams params = new GridLayout.LayoutParams();
        params.width = 0;
        params.height = dp(66);
        params.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
        params.setMargins(dp(4), dp(4), dp(4), dp(4));
        grid.addView(button, params);
    }

    private void prepareBluetooth() {
        bluetoothTransport = new BluetoothTransport(this, (connected, detail) -> mainHandler.post(() -> {
            bluetoothConnected = connected;
            bluetoothDetail = detail;
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
        showConnection("正在检测电脑端…", COLOR_MUTED);
        connectionExecutor.execute(() -> {
            HttpURLConnection connection = null;
            try {
                connection = (HttpURLConnection) new URL(SERVER + "/api/health").openConnection();
                connection.setConnectTimeout(1200);
                connection.setReadTimeout(1200);
                connection.setRequestMethod("GET");
                int response = connection.getResponseCode();
                if (response == 200) {
                    usbConnected = true;
                    mainHandler.post(this::updateConnectionDisplay);
                } else {
                    throw new IllegalStateException("HTTP " + response);
                }
            } catch (Exception exception) {
                usbConnected = false;
                mainHandler.post(this::updateConnectionDisplay);
            } finally {
                if (connection != null) {
                    connection.disconnect();
                }
            }
        });
    }

    private void triggerAction(
            Button source,
            String action,
            String text,
            String pendingMessage,
            String successMessage,
            boolean guardTypeless) {
        if (guardTypeless && typelessInFlight) {
            showActionFeedback("●  Typeless 指令正在处理，请稍候", COLOR_PENDING);
            return;
        }

        if (guardTypeless) {
            typelessInFlight = true;
            source.setEnabled(false);
            source.setAlpha(0.74f);
            source.setText("正在发送，请稍候…");
        }
        showActionFeedback("●  " + pendingMessage, COLOR_PENDING);
        sendAction(action, text, successMessage, source, guardTypeless);
    }

    private void toggleTypelessWithPhoneMic() {
        if (typelessInFlight) {
            showActionFeedback("●  Typeless 指令正在处理，请稍候", COLOR_PENDING);
            return;
        }
        if (dictationActive) {
            stopPhoneDictation();
        } else {
            beginPhoneDictation();
        }
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
        if (!typelessInFlight) {
            typelessButton.setText(voiceButtonLabel());
        }
        typelessButton.setContentDescription(holdMode
                ? "按住开始 Typeless 语音输入，松开结束"
                : "点击开始 Typeless 语音输入，再点一次结束");
    }

    private String voiceButtonLabel() {
        if (MODE_HOLD.equals(voiceMode)) {
            return dictationActive ? "■  松开即可结束" : "●  按住说话";
        }
        return dictationActive ? "■  正在听写 · 点击停止" : "●  点击开始说话";
    }

    private void beginPhoneDictation() {
        if (!usbConnected) {
            showConnection("手机音频需要 USB 连接", COLOR_DANGER);
            showActionFeedback("✕  请连接 USB，并重新运行电脑端启动脚本", COLOR_DANGER);
            testConnection();
            return;
        }
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO)
                != PackageManager.PERMISSION_GRANTED) {
            audioStartPending = true;
            requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO}, REQUEST_MICROPHONE);
            return;
        }

        audioStartPending = true;
        intentionalAudioStop = false;
        setTypelessBusy("正在连接手机麦克风…");
        showActionFeedback("●  正在建立 USB 音频通道…", COLOR_PENDING);
        microphoneLevel.setText("手机麦克风  ◌ 正在连接");
        microphoneLevel.setTextColor(COLOR_PENDING);
        audioStreamer.start();
    }

    private void stopPhoneDictation() {
        setTypelessBusy("正在结束听写…");
        showActionFeedback("●  正在停止 Typeless 并收尾音频…", COLOR_PENDING);
        sendTypelessToggle(false);
    }

    private void onAudioReady() {
        if (!audioStartPending) {
            return;
        }
        audioStartPending = false;
        showActionFeedback("●  手机麦克风已连接，正在唤醒 Typeless…", COLOR_PENDING);
        sendTypelessToggle(true);
    }

    private void sendTypelessToggle(boolean starting) {
        actionExecutor.execute(() -> {
            try {
                JSONObject body = new JSONObject();
                body.put("action", "typeless");
                body.put("requestId", UUID.randomUUID().toString());
                String transport = sendCommand(body);
                if (!starting) {
                    Thread.sleep(280);
                    intentionalAudioStop = true;
                    audioStreamer.stop();
                }
                mainHandler.post(() -> {
                    dictationActive = starting;
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
                        microphoneLevel.setText("手机麦克风  ○ 已停止");
                        microphoneLevel.setTextColor(COLOR_MUTED);
                    }
                });
            } catch (Exception exception) {
                if (starting) {
                    intentionalAudioStop = true;
                    audioStreamer.stop();
                }
                mainHandler.post(() -> {
                    holdReleasePending = false;
                    showConnection("Typeless 指令发送失败", COLOR_DANGER);
                    showActionFeedback("✕  电脑没有确认，请检查 USB 连接后重试", COLOR_DANGER);
                    performResultHaptic(typelessButton, false);
                    flashResult(typelessButton, COLOR_DANGER);
                    finishGuardedAction(typelessButton, true);
                });
            }
        });
    }

    private void setTypelessBusy(String label) {
        typelessInFlight = true;
        typelessButton.setEnabled(!MODE_TAP.equals(voiceMode) && holdGestureActive);
        typelessButton.setAlpha(0.74f);
        typelessButton.setText(label);
    }

    private void updateMicrophoneLevel(int percent) {
        if (!audioStreamer.isStreaming()) {
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

    private void onAudioStopped(String reason) {
        if (intentionalAudioStop) {
            intentionalAudioStop = false;
            return;
        }
        if (reason == null && !audioStartPending && !dictationActive) {
            return;
        }
        audioStartPending = false;
        microphoneLevel.setText("手机麦克风  ✕ 音频中断");
        microphoneLevel.setTextColor(COLOR_DANGER);
        showActionFeedback("✕  手机音频中断" + (reason == null ? "" : "：" + reason),
                COLOR_DANGER);
        if (!dictationActive) {
            finishGuardedAction(typelessButton, true);
        } else {
            typelessButton.setEnabled(true);
            typelessButton.setAlpha(1f);
            typelessButton.setText(MODE_HOLD.equals(voiceMode)
                    ? "■  音频中断 · 松开结束"
                    : "■  音频中断 · 点击停止");
        }
    }

    private void sendAction(
            String action,
            String text,
            String successMessage,
            Button source,
            boolean guardTypeless) {
        actionExecutor.execute(() -> {
            try {
                JSONObject body = new JSONObject();
                body.put("action", action);
                body.put("requestId", UUID.randomUUID().toString());
                if (text != null) {
                    body.put("text", text);
                }
                String finalTransport = sendCommand(body);
                mainHandler.post(() -> {
                    showConnection(finalTransport + " 已连接", COLOR_SUCCESS);
                    showActionFeedback("✓  " + successMessage + " · " + finalTransport,
                            COLOR_SUCCESS);
                    performResultHaptic(source, true);
                    flashResult(source, COLOR_SUCCESS);
                    finishGuardedAction(source, guardTypeless);
                });
            } catch (Exception exception) {
                mainHandler.post(() -> {
                    showConnection("发送失败，请连接 USB 或蓝牙", COLOR_DANGER);
                    showActionFeedback("✕  没有收到电脑确认 · 请检查连接后重试",
                            COLOR_DANGER);
                    performResultHaptic(source, false);
                    flashResult(source, COLOR_DANGER);
                    finishGuardedAction(source, guardTypeless);
                });
            }
        });
    }

    private String sendCommand(JSONObject body) throws Exception {
        boolean sent = false;
        String transport = "";
        boolean usbAttempted = false;

        if (usbConnected) {
            usbAttempted = true;
            sent = postUsbWithRetry(body, 2);
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
            sent = postUsbWithRetry(body, 2);
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

    private boolean postUsbWithRetry(JSONObject body, int attempts) {
        for (int attempt = 0; attempt < attempts; attempt++) {
            if (postUsb(body)) {
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

    private boolean postUsb(JSONObject body) {
        HttpURLConnection connection = null;
        try {
            byte[] bytes = body.toString().getBytes(StandardCharsets.UTF_8);
            connection = (HttpURLConnection) new URL(SERVER + "/api/input").openConnection();
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
            showConnection("USB 已连接", COLOR_SUCCESS);
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
        source.setEnabled(true);
        source.setAlpha(1f);
        source.setText(voiceButtonLabel());
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
                audioStartPending = false;
                showActionFeedback("✕  未授予麦克风权限，无法传输手机声音", COLOR_DANGER);
            }
        }
    }

    @Override
    protected void onDestroy() {
        if (bluetoothTransport != null) {
            bluetoothTransport.close();
        }
        if (audioStreamer != null) {
            audioStreamer.close();
        }
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
