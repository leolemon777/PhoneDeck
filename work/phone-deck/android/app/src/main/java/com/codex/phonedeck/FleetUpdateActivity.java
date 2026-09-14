package com.codex.phonedeck;

import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Bundle;
import android.provider.Settings;
import android.view.WindowManager;
import android.widget.*;
import org.json.JSONObject;
import java.io.*;
import java.net.HttpURLConnection;
import java.nio.charset.StandardCharsets;
import java.util.*;

/** One foreground coordinator owns a rollout; every receiver remains independently recoverable. */
public final class FleetUpdateActivity extends Activity {
    static volatile boolean running;
    static volatile boolean opened;
    private TextView status;
    private Button start, install;
    private final LinkedHashMap<String, String> states = new LinkedHashMap<>();
    private final Set<String> pending = new HashSet<>();
    private volatile boolean cancelled;
    private String sourceId;
    private UpdateBundle release;
    private File directory;

    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        opened = true;
        PhoneDeckTheme theme = PhoneDeckTheme.load(this);
        theme.applyWindow(this);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        directory = new File(getCacheDir(), "fleet-update");
        directory.mkdirs();
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        int spacing = PhoneDeckTheme.dp(this, 20);
        page.setPadding(spacing, spacing, spacing, spacing);
        page.setBackgroundColor(theme.contentBackground());
        LinearLayout header = new LinearLayout(this);
        header.setGravity(android.view.Gravity.CENTER_VERTICAL);
        Button back = new Button(this); back.setText("←"); back.setTextColor(theme.text);
        back.setContentDescription("返回"); back.setPadding(0, 0, 0, 0);
        back.setBackground(theme.pressable(this, theme.surface, theme.surfaceRaised, 14));
        back.setOnClickListener(v -> onBackPressed());
        header.addView(back, new LinearLayout.LayoutParams(PhoneDeckTheme.dp(this, 48), PhoneDeckTheme.dp(this, 48)));
        TextView title = new TextView(this); title.setText("设备更新"); title.setTextSize(22); title.setTextColor(theme.text);
        title.setTypeface(android.graphics.Typeface.DEFAULT, android.graphics.Typeface.BOLD);
        title.setPadding(PhoneDeckTheme.dp(this, 12), 0, 0, 0);
        header.addView(title); page.addView(header);
        TextView hint = new TextView(this);
        hint.setText("在任一已配对电脑导入签名更新包，然后点击下方按钮。其他电脑依次更新，手机最后安装。请保持此页打开；离线或失败的设备可稍后重试。\n正在听写时会等待，空闲后会暂停共享麦克风。安卓安装需按系统提示确认。");
        hint.setTextColor(theme.muted); hint.setTextSize(14);
        hint.setLineSpacing(PhoneDeckTheme.dp(this, 3), 1f);
        hint.setPadding(0, spacing, 0, spacing); page.addView(hint);
        start = updateButton(page, theme, "检查并更新所有设备", true);
        install = updateButton(page, theme, "安装手机更新", false); install.setEnabled(false);
        Button cancel = updateButton(page, theme, "停止后续更新", false);
        cancel.setTextColor(theme.danger);
        status = new TextView(this); status.setTextColor(theme.text); status.setTextSize(14);
        status.setPadding(spacing, spacing, spacing, spacing);
        status.setBackground(theme.shape(this, theme.surface, 16));
        status.setAccessibilityLiveRegion(android.view.View.ACCESSIBILITY_LIVE_REGION_POLITE);
        page.addView(status);
        ScrollView scroll = new ScrollView(this); scroll.addView(page); setContentView(theme.wrapContent(this, scroll));
        status.setText(getSharedPreferences("PhoneDeckUpdates", MODE_PRIVATE).getString("last_result", "尚未检查"));
        sourceId = getIntent().getStringExtra("sourceId");
        start.setOnClickListener(v -> begin()); install.setOnClickListener(v -> installPhone());
        cancel.setOnClickListener(v -> {
            cancelled = true;
            getSharedPreferences("PhoneDeckUpdates", MODE_PRIVATE).edit().remove("pending_devices").apply();
            show("更新任务", "将停止后续设备；已开始安装的电脑会自行完成或回退");
        });
        if ((sourceId != null || getIntent().getBooleanExtra("resume", false)) && saved == null) begin();
    }

    private Button updateButton(LinearLayout page, PhoneDeckTheme theme, String label, boolean primary) {
        Button button = new Button(this);
        button.setText(label); button.setTextSize(15); button.setAllCaps(false);
        button.setTextColor(primary ? theme.onPrimary : theme.text);
        button.setBackground(theme.pressable(this, primary ? theme.primary : theme.surfaceRaised,
                primary ? theme.primaryPressed : theme.key, 14));
        button.setStateListAnimator(null);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, PhoneDeckTheme.dp(this, 52));
        params.bottomMargin = PhoneDeckTheme.dp(this, 12);
        page.addView(button, params);
        return button;
    }

    @Override public void onBackPressed() {
        if (running) { Toast.makeText(this, "请先停止后续更新并等待当前操作结束", Toast.LENGTH_SHORT).show(); return; }
        super.onBackPressed();
    }
    @Override public void onDestroy() { cancelled = true; opened = false; super.onDestroy(); }

    private void begin() {
        if (running) return;
        running = true; cancelled = false; start.setEnabled(false); install.setEnabled(false);
        pending.clear();
        new Thread(() -> {
            try { rollout(); }
            catch (Exception e) { show("更新任务", "未完成：" + e.getMessage()); }
            finally {
                running = false;
                runOnUiThread(() -> { if (!isDestroyed()) start.setEnabled(true); });
            }
        }, "PhoneDeck-FleetUpdate").start();
    }

    private void checkCancelled() throws IOException { if (cancelled) throw new IOException("已停止，可重新检查并继续"); }

    private void rollout() throws Exception {
        TargetDeviceManager devices = new TargetDeviceManager(this);
        List<TargetDeviceManager.Device> all = devices.list();
        Map<String, PhoneDeckEndpoint> online = new LinkedHashMap<>();
        Map<String, PhoneDeckEndpoint> audioPeers = new LinkedHashMap<>();
        PhoneDeckEndpoint source = null;
        long newest = -1;
        for (TargetDeviceManager.Device device : all) {
            checkCancelled(); show(deviceLabel(device), "正在连接…");
            PhoneDeckLanClient.ProbeResult probe = PhoneDeckLanClient.probe(device);
            if (probe == null) { pending.add(device.computerId); show(deviceLabel(device), "离线或配对失效；恢复连接并打开主页后自动补更"); continue; }
            audioPeers.put(device.computerId, probe.endpoint);
            JSONObject updates = probe.health.optJSONObject("updates");
            if (updates == null || !updates.optBoolean("supported")) {
                show(deviceLabel(device), "当前版本尚不支持统一更新，需首次安装新版"); continue;
            }
            online.put(device.computerId, probe.endpoint);
            JSONObject state;
            try { state = PhoneDeckHttp.getJson(probe.endpoint, "/api/updates"); }
            catch (Exception e) { online.remove(device.computerId); pending.add(device.computerId); show(deviceLabel(device), "读取更新状态失败，稍后重试"); continue; }
            JSONObject manifest = state.optJSONObject("manifest");
            if (manifest != null && (sourceId == null || device.computerId.equals(sourceId))
                    && manifest.optLong("sequence", -1) > newest) {
                source = probe.endpoint; newest = manifest.getLong("sequence");
            }
            show(deviceLabel(device), "已连接 · " + state.optString("version"));
        }
        if (source == null) {
            // A retained, verified bundle allows retry when the original source is offline.
            if (!new File(directory, "bundle.zip").isFile()) throw new IOException("请先在电脑「设备 → 更新所有设备」导入签名更新包");
            show("更新包", "使用上次已缓存的更新包");
        } else {
            show("更新包", "正在下载并校验…");
            download(source);
        }
        String key;
        try (InputStream input = getResources().openRawResource(R.raw.update_publisher);
             ByteArrayOutputStream output = new ByteArrayOutputStream()) {
            UpdateBundle.copy(input, output, 4096); key = output.toString(StandardCharsets.UTF_8.name()).trim();
        }
        release = UpdateBundle.verify(new File(directory, "bundle.zip"), key, new File(directory, "PhoneDeck.apk"));
        savePending();
        show("更新包", "签名验证通过 · " + release.windowsVersion);
        // Wait before pausing the microphone: do not cut off a managed session or a PC's dictation.
        for (int wait = 0; ; wait++) {
            checkCancelled(); boolean busy = false;
            java.util.Iterator<Map.Entry<String, PhoneDeckEndpoint>> iterator = audioPeers.entrySet().iterator();
            while (iterator.hasNext()) {
                Map.Entry<String, PhoneDeckEndpoint> connection = iterator.next();
                PhoneDeckEndpoint endpoint = connection.getValue();
                try {
                    JSONObject health = PhoneDeckHttp.getJson(endpoint, "/api/health");
                    JSONObject engine = health.optJSONObject("voiceEngine"), dictation = health.optJSONObject("dictation");
                    if ((engine != null && engine.optBoolean("capturing"))
                            || (dictation != null && dictation.optBoolean("active"))) busy = true;
                } catch (Exception e) {
                    TargetDeviceManager.Device device = devices.find(connection.getKey());
                    show(device == null ? connection.getKey() : deviceLabel(device), "连接中断，稍后重试");
                    pending.add(connection.getKey()); savePending();
                    online.remove(connection.getKey());
                    iterator.remove();
                }
            }
            if (!busy) break;
            show("更新任务", "等待电脑结束听写或恢复连接…");
            if (wait >= 300) throw new IOException("等待空闲超时，请稍后重试");
            Thread.sleep(2000);
        }
        if (PhoneAudioService.getSnapshot().running) {
            runOnUiThread(() -> startService(new Intent(this, PhoneAudioService.class).setAction(PhoneAudioService.ACTION_STOP)));
            for (int i = 0; i < 100 && PhoneAudioService.getSnapshot().running; i++) { checkCancelled(); Thread.sleep(100); }
            if (PhoneAudioService.getSnapshot().running) throw new IOException("请先关闭共享麦克风");
        }
        // Cached bundle makes the source restart safe. Keep the source last to minimize disruption.
        final String sourceComputer = source == null ? "" : source.computerId;
        all.sort(Comparator.comparing(d -> d.computerId.equals(sourceComputer)));
        for (TargetDeviceManager.Device device : all) {
            checkCancelled();
            PhoneDeckEndpoint endpoint = online.get(device.computerId);
            if (endpoint == null) continue;
            try { updateComputer(device, endpoint); }
            catch (Exception e) { show(deviceLabel(device), "未完成：" + e.getMessage() + "；可重试"); }
        }
        checkCancelled();
        if (release.androidVersion > installedVersion()) {
            validateApk(); show("这台手机", "电脑更新流程结束，等待系统安装确认");
            runOnUiThread(() -> { if (!isDestroyed()) { install.setEnabled(true); installPhone(); } });
        } else show("这台手机", "已经是此版本或更高版本");
        show("更新任务", "本轮结束，请查看各设备结果。共享麦克风可在主页重新开启。");
        sourceId = null;
    }

    private void savePending() {
        if (cancelled || release == null) return;
        getSharedPreferences("PhoneDeckUpdates", MODE_PRIVATE).edit()
                .putStringSet("pending_devices", new HashSet<>(pending))
                .putLong("pending_sequence", release.sequence).apply();
    }

    private void updateComputer(TargetDeviceManager.Device device, PhoneDeckEndpoint endpoint) throws Exception {
        JSONObject current = PhoneDeckHttp.getJson(endpoint, "/api/updates");
        if (current.getLong("sequence") >= release.sequence) { show(deviceLabel(device), "已经是此版本或更高版本"); return; }
        show(deviceLabel(device), "正在传送更新包…");
        upload(endpoint);
        for (int i = 0; ; i++) {
            checkCancelled();
            JSONObject applied = post(endpoint, "/api/updates/apply", null);
            if (!"waiting-idle".equals(applied.optString("state"))) break;
            show(deviceLabel(device), "已准备好，等待麦克风空闲…");
            if (i >= 300) throw new IOException("等待空闲超时");
            Thread.sleep(2000);
        }
        show(deviceLabel(device), "正在安装并重启…");
        for (int i = 0; i < 100; i++) {
            Thread.sleep(1500);
            try {
                JSONObject state = PhoneDeckHttp.getJson(endpoint, "/api/updates");
                JSONObject outcome = state.optJSONObject("status");
                if (state.getLong("sequence") == release.sequence
                        && release.windowsVersion.equals(state.optString("version"))
                        && outcome != null && "completed".equals(outcome.optString("state"))) {
                    show(deviceLabel(device), "更新完成 · " + release.windowsVersion); return;
                }
                if (outcome != null && ("failed".equals(outcome.optString("state"))
                        || "recovery-required".equals(outcome.optString("state"))))
                    throw new UpdateFailed(outcome.optString("detail"));
            } catch (UpdateFailed failure) { throw failure; }
            catch (Exception ignored) { }
        }
        throw new IOException("未收到完成确认，请检查该电脑后重试");
    }

    private static final class UpdateFailed extends IOException { UpdateFailed(String text) { super(text); } }

    private void download(PhoneDeckEndpoint endpoint) throws Exception {
        HttpURLConnection connection = PhoneDeckHttp.open(endpoint, "/api/updates/bundle", 3000, 30000);
        File temporary = new File(directory, "bundle.partial");
        try {
            if (connection.getResponseCode() != 200) throw new IOException("无法下载更新包");
            try (InputStream input = connection.getInputStream(); OutputStream output = new FileOutputStream(temporary)) {
                byte[] buffer = new byte[65536]; int count; long total = 0, reported = 0;
                long length = connection.getContentLengthLong();
                while ((count = input.read(buffer)) != -1) {
                    checkCancelled(); total += count;
                    if (total > UpdateBundle.MAX_BYTES) throw new IOException("更新包过大");
                    output.write(buffer, 0, count);
                    if (total - reported >= 1024 * 1024) {
                        reported = total;
                        show("更新包", "已下载 " + (total / 1024 / 1024) + " MB"
                                + (length > 0 ? " / " + (length / 1024 / 1024) + " MB" : ""));
                    }
                }
                show("更新包", "下载完成，正在校验签名和文件…");
            }
            java.nio.file.Files.move(temporary.toPath(), new File(directory, "bundle.zip").toPath(), java.nio.file.StandardCopyOption.REPLACE_EXISTING);
        } finally { connection.disconnect(); temporary.delete(); }
    }
    private void upload(PhoneDeckEndpoint endpoint) throws Exception { post(endpoint, "/api/updates/bundle", new File(directory, "bundle.zip")); }

    private JSONObject post(PhoneDeckEndpoint endpoint, String path, File file) throws Exception {
        HttpURLConnection connection = PhoneDeckHttp.open(endpoint, path, 3000, 120000);
        try {
            connection.setRequestMethod("POST"); connection.setDoOutput(true);
            connection.setRequestProperty("Content-Type", "application/octet-stream");
            connection.setRequestProperty("X-PhoneDeck-Update", "1");
            connection.setRequestProperty("X-PhoneDeck-Target", endpoint.computerId);
            connection.setFixedLengthStreamingMode(file == null ? 0 : file.length());
            try (OutputStream output = connection.getOutputStream()) {
                if (file != null) try (InputStream input = new FileInputStream(file)) { UpdateBundle.copy(input, output, UpdateBundle.MAX_BYTES); }
            }
            int code = connection.getResponseCode();
            InputStream body = code < 400 ? connection.getInputStream() : connection.getErrorStream();
            try (InputStream input = body; ByteArrayOutputStream output = new ByteArrayOutputStream()) {
                if (input != null) UpdateBundle.copy(input, output, 32768);
                JSONObject result = new JSONObject(output.toString(StandardCharsets.UTF_8.name()));
                if (code != 200 || !result.optBoolean("ok")) throw new IOException(result.optString("error", "HTTP " + code));
                return result;
            }
        } finally { connection.disconnect(); }
    }

    private int installedVersion() throws PackageManager.NameNotFoundException {
        return getPackageManager().getPackageInfo(getPackageName(), 0).versionCode;
    }
    @SuppressWarnings("deprecation")
    private void validateApk() throws Exception {
        PackageInfo candidate = getPackageManager().getPackageArchiveInfo(new File(directory, "PhoneDeck.apk").getPath(), PackageManager.GET_SIGNATURES);
        PackageInfo installed = getPackageManager().getPackageInfo(getPackageName(), PackageManager.GET_SIGNATURES);
        if (candidate == null || !getPackageName().equals(candidate.packageName)
                || candidate.versionCode != release.androidVersion || candidate.signatures == null
                || !Arrays.equals(candidate.signatures, installed.signatures))
            throw new IOException("手机安装包的版本、应用身份或签名与本机不匹配");
    }
    private void installPhone() {
        try {
            if (release == null) throw new IOException("请先检查更新");
            validateApk();
            if (!getPackageManager().canRequestPackageInstalls()) {
                show("这台手机", "请允许 PhoneDeck 安装应用，返回后点击「安装手机更新」");
                startActivity(new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + getPackageName()))); return;
            }
            Intent intent = new Intent(Intent.ACTION_VIEW);
            intent.setDataAndType(Uri.parse("content://" + getPackageName() + ".updates/PhoneDeck.apk"), "application/vnd.android.package-archive");
            intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            startActivity(intent);
            show("这台手机", "等待系统安装；下次检查将按实际版本确认结果");
        } catch (Exception e) { show("这台手机", "安装未完成：" + e.getMessage()); }
    }
    private static String deviceLabel(TargetDeviceManager.Device device) {
        return device.displayName + "（" + device.slot + "号）";
    }

    private void show(String device, String detail) {
        synchronized (states) {
            states.put(device, detail);
            StringBuilder text = new StringBuilder();
            for (Map.Entry<String, String> item : states.entrySet()) text.append(item.getKey()).append("\n").append(item.getValue()).append("\n\n");
            String value = text.toString();
            getSharedPreferences("PhoneDeckUpdates", MODE_PRIVATE).edit().putString("last_result", value).apply();
            runOnUiThread(() -> { if (!isDestroyed()) status.setText(value); });
        }
    }
}
