package com.codex.phonedeck;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.res.ColorStateList;
import android.content.res.Configuration;
import android.graphics.Typeface;
import android.os.Bundle;
import android.text.Editable;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.View;
import android.widget.*;
import org.json.*;
import java.io.IOException;
import java.util.*;
import java.util.concurrent.*;

/** The phone edits settings owned by one explicitly selected computer. */
public final class ComputerSettingsActivity extends Activity {
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private PhoneDeckTheme theme;
    private TargetDeviceManager devices;
    private LinearLayout page, modes, voicePage, connectionPage, footer, identityCard;
    private TextView status, saveHint, headerTitle;
    private Button saveButton, voiceTab, connectionTab;
    private TargetDeviceManager.Device selected;
    private PhoneDeckEndpoint endpoint;
    private JSONObject snapshot, overrides;
    private JSONArray engines;
    private String engineId;
    private final Map<String, EditText> fields = new LinkedHashMap<>();
    private final Map<String, RadioButton> engineChoices = new LinkedHashMap<>();
    private final List<View> controls = new ArrayList<>();
    private Switch usb, lan, autoStart;
    private boolean pending, binding, voiceSelected = true, voiceDirty, connectionDirty;
    private int generation;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        theme = PhoneDeckTheme.load(this); theme.applyWindow(this);
        devices = new TargetDeviceManager(this);
        list();
    }
    private int dp(int value) { return PhoneDeckTheme.dp(this, value); }
    private LinearLayout column() {
        LinearLayout view = new LinearLayout(this); view.setOrientation(LinearLayout.VERTICAL); return view;
    }
    private TextView text(String value, int size, int color, boolean bold) {
        TextView view = new TextView(this); view.setText(value); view.setTextSize(size); view.setTextColor(color);
        view.setTypeface(Typeface.DEFAULT, bold ? Typeface.BOLD : Typeface.NORMAL);
        view.setLineSpacing(dp(2), 1f); return view;
    }
    private void add(LinearLayout parent, View view, int margin) {
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(-1, -2); p.topMargin = dp(margin); parent.addView(view, p);
    }
    private LinearLayout card(LinearLayout parent) {
        LinearLayout view = column(); view.setPadding(dp(18), dp(16), dp(18), dp(16));
        view.setBackground(theme.shape(this, theme.surface, 20)); add(parent, view, 12); return view;
    }
    private Button action(String title, boolean primary, Runnable run) {
        Button b = new Button(this); b.setText(title); b.setAllCaps(false); b.setTextSize(15);
        b.setTypeface(Typeface.DEFAULT, Typeface.BOLD); b.setMinHeight(dp(52));
        b.setTextColor(primary ? theme.onPrimary : theme.text);
        b.setBackground(theme.pressable(this, primary ? theme.primary : theme.surface, primary ? theme.primaryPressed : theme.surfaceRaised, 14));
        b.setStateListAnimator(null); b.setPadding(dp(16), dp(12), dp(16), dp(12));
        b.setOnClickListener(v -> run.run()); return b;
    }
    private void shell() {
        controls.clear(); fields.clear(); engineChoices.clear(); identityCard = null; generation++;
        LinearLayout root = column(); root.setBackgroundColor(theme.background);
        LinearLayout header = new LinearLayout(this); header.setGravity(Gravity.CENTER_VERTICAL);
        header.setPadding(dp(20), dp(12), dp(20), dp(12));
        Button back = action("‹", false, this::onBackPressed); back.setTextSize(28); back.setMinHeight(0); back.setPadding(0,0,0,0);
        back.setContentDescription("返回"); header.addView(back, new LinearLayout.LayoutParams(dp(48), dp(48)));
        TextView title = text(selected == null ? "电脑与输入法" : "电脑设置", 21, theme.text, true);
        headerTitle = title; title.setMaxLines(2); title.setEllipsize(android.text.TextUtils.TruncateAt.END);
        LinearLayout.LayoutParams titleParams = new LinearLayout.LayoutParams(0, -2, 1); titleParams.leftMargin = dp(14);
        header.addView(title, titleParams);
        if (selected != null) {
            Button reload = action("刷新", false, () -> discardThen(this::load));
            reload.setMinWidth(0); reload.setPadding(dp(12), dp(8), dp(12), dp(8));
            reload.setContentDescription("重新读取这台电脑的设置"); controls.add(reload); header.addView(reload);
        }
        root.addView(header);
        page = column(); page.setPadding(dp(20), 0, dp(20), dp(24));
        ScrollView scroll = new ScrollView(this); scroll.setFillViewport(true); scroll.setClipToPadding(false); scroll.addView(page);
        root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        footer = column(); footer.setPadding(dp(20), dp(12), dp(20), dp(16)); footer.setBackgroundColor(theme.surface);
        footer.setVisibility(View.GONE); root.addView(footer);
        setContentView(theme.wrapContent(this, root));
        status = text("", 13, theme.muted, false); status.setPadding(dp(14), dp(12), dp(14), dp(12));
        status.setBackground(theme.shape(this, theme.surfaceRaised, 12));
        status.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
    }
    private void notice(String message, int color) { status.setText(message); status.setTextColor(color); updateCompactLayout(); }
    private void list() {
        selected = null; endpoint = null; snapshot = null; pending = false;
        voiceDirty = connectionDirty = false; shell();
        add(page, text("你的电脑", 28, theme.text, true), 12);
        add(page, text("输入法按电脑保存，使用习惯跟随手机。", 14, theme.muted, false), 8);
        List<TargetDeviceManager.Device> known = devices.list();
        add(page, text(known.size() + " 台已配对设备", 12, theme.muted, true), 26);
        for (TargetDeviceManager.Device d : known) {
            LinearLayout row = new LinearLayout(this); row.setGravity(Gravity.CENTER_VERTICAL);
            row.setPadding(dp(16), dp(18), dp(16), dp(18));
            row.setBackground(theme.pressable(this, theme.surface, theme.surfaceRaised, 18));
            row.setFocusable(true); row.setOnClickListener(v -> open(d));
            TextView number = text(String.format(Locale.ROOT, "%02d", d.slot), 18, theme.primary, true);
            number.setGravity(Gravity.CENTER); number.setBackground(theme.shape(this, theme.surfaceRaised, 14));
            row.addView(number, new LinearLayout.LayoutParams(dp(48), dp(48)));
            LinearLayout copy = column(); add(copy, text(d.displayName, 16, theme.text, true), 0);
            boolean current = d.computerId.equalsIgnoreCase(devices.getActiveComputerId());
            add(copy, text(("windows".equalsIgnoreCase(d.platform) ? "Windows" : "macos".equalsIgnoreCase(d.platform) ? "macOS" : d.platform)
                    + (current ? " · 当前输入电脑" : " · 点击管理"), 12, current ? theme.primary : theme.muted, false), 4);
            LinearLayout.LayoutParams cp = new LinearLayout.LayoutParams(0, -2, 1); cp.leftMargin = dp(14); cp.rightMargin = dp(8); row.addView(copy, cp);
            row.addView(text("›", 25, theme.muted, false)); add(page, row, 10);
        }
        if (known.isEmpty()) {
            LinearLayout empty = card(page); add(empty, text("连接第一台电脑", 18, theme.text, true), 0);
            add(empty, text("打开电脑接收器，用 USB 连接手机并完成首次配对。", 14, theme.muted, false), 8);
        }
        add(page, text("在这里管理设置不会切换当前输入目标。\n布局、主题和语音工作方式在手机设置中统一调整。", 12, theme.muted, false), 24);
    }
    private void identity() {
        LinearLayout card = card(page);
        identityCard = card;
        add(card, text(String.format(Locale.ROOT, "电脑 %02d", selected.slot), 12, theme.primary, true), 0);
        add(card, text(selected.displayName, 22, theme.text, true), 6);
        add(card, text("设置仅应用到这台电脑", 12, theme.muted, false), 5);
        add(page, status, 12);
        updateCompactLayout();
    }
    private void open(TargetDeviceManager.Device d) {
        selected = d; snapshot = null; endpoint = null; voiceDirty = connectionDirty = false; voiceSelected = true;
        shell(); identity(); load();
    }
    private void busy(boolean value) {
        pending = value;
        for (View v : controls) { v.setEnabled(!value); v.setAlpha(value ? .55f : 1f); }
        updateSave();
    }
    private static void requireTarget(JSONObject value, String target) throws IOException {
        if (!target.equalsIgnoreCase(value.optString("computerId"))) throw new IOException("连接的电脑发生变化，请重新读取设置");
    }
    private void load() {
        if (pending || selected == null) return;
        busy(true); notice("正在连接，读取这台电脑的设置…", theme.muted);
        int ticket = generation; TargetDeviceManager.Device d = selected;
        worker.execute(() -> {
            try {
                PhoneDeckLanClient.ProbeResult probe = PhoneDeckLanClient.probe(d);
                PhoneDeckEndpoint target; JSONObject health;
                if (probe != null) { target = probe.endpoint; health = probe.health; }
                else { target = PhoneDeckEndpoint.USB; health = PhoneDeckHttp.getJson(target, "/api/health"); }
                requireTarget(health, d.computerId);
                JSONArray capabilities = health.optJSONArray("capabilities"); boolean supported = false;
                if (capabilities != null) for (int i = 0; i < capabilities.length(); i++)
                    if ("phoneManagedSettingsV1".equals(capabilities.optString(i))) supported = true;
                if (!supported) throw new IOException("此接收器暂不支持手机配置，请升级到支持该功能的版本");
                JSONObject data = PhoneDeckHttp.getJson(target, "/api/config/desktop"); requireTarget(data, d.computerId);
                PhoneDeckEndpoint resolved = target;
                runOnUiThread(() -> { if (!valid(ticket)) return; endpoint = resolved; busy(false); render(data, "已连接 · 可以编辑设置"); });
            } catch (Exception e) { failure(ticket, "暂时无法读取设置\n" + e.getMessage() + "\n恢复连接后，可点击右上角刷新。"); }
        });
    }
    private boolean valid(int ticket) { return !isDestroyed() && !isFinishing() && generation == ticket; }
    private void failure(int ticket, String message) {
        runOnUiThread(() -> { if (!valid(ticket)) return; busy(false); notice(message, theme.danger); });
    }
    private void render(JSONObject data, String message) {
        binding = true; voiceDirty = connectionDirty = false; snapshot = data; shell(); identity();
        notice(data.optBoolean("busy") ? "正在供音或听写 · 停止后即可保存" : message, data.optBoolean("busy") ? theme.warning : theme.success);
        LinearLayout tabs = new LinearLayout(this); tabs.setPadding(dp(4), dp(4), dp(4), dp(4));
        tabs.setBackground(theme.shape(this, theme.surfaceRaised, 16));
        voiceTab = action("输入法", false, () -> selectTab(true)); connectionTab = action("连接", false, () -> selectTab(false));
        tabs.addView(voiceTab, new LinearLayout.LayoutParams(0, -2, 1)); tabs.addView(connectionTab, new LinearLayout.LayoutParams(0, -2, 1));
        controls.add(voiceTab); controls.add(connectionTab); add(page, tabs, 20);
        voicePage = column(); connectionPage = column(); add(page, voicePage, 0); add(page, connectionPage, 0);
        add(voicePage, text("使用哪款输入法？", 20, theme.text, true), 24);
        add(voicePage, text("选择这台电脑已安装的语音输入软件。", 13, theme.muted, false), 6);
        engines = data.optJSONArray("engines");
        try { overrides = new JSONObject(data.getJSONObject("shortcutOverrides").toString()); }
        catch (JSONException e) { overrides = new JSONObject(); }
        engineId = data.optString("activeEngine");
        for (int i = 0; engines != null && i < engines.length(); i++) {
            JSONObject engine = engines.optJSONObject(i); String id = engine.optString("id");
            RadioButton option = new RadioButton(this);
            option.setText(engine.optString("displayName") + (engine.optBoolean("experimental") ? "  ·  待验证" : "")
                    + "\n" + (engine.optBoolean("verifiesMicrophone") ? "自动读取快捷键与麦克风配置" : "快捷键需与输入法设置保持一致"));
            option.setTextSize(14); option.setTextColor(theme.text); option.setMinHeight(dp(76));
            option.setPadding(dp(16), dp(12), dp(16), dp(12)); option.setButtonTintList(ColorStateList.valueOf(theme.primary));
            option.setGravity(Gravity.CENTER_VERTICAL); option.setChecked(id.equals(engineId));
            option.setBackground(theme.shape(this, theme.surface, 16, id.equals(engineId) ? 1 : 0, theme.primary));
            option.setOnClickListener(v -> {
                if (id.equals(engineId)) return;
                capture(); engineId = id; voiceDirty = true; refreshChoices(); showModes(); updateSave();
            });
            engineChoices.put(id, option); controls.add(option); add(voicePage, option, 10);
        }
        LinearLayout keyCard = card(voicePage);
        add(keyCard, text("语音快捷键", 16, theme.text, true), 0);
        add(keyCard, text("留空使用自动读取值或默认值。自定义时，请与输入法中的设置一致。", 12, theme.muted, false), 6);
        modes = column(); add(keyCard, modes, 4); showModes();
        add(voicePage, text("只用共享麦克风？\n无需在这里选择输入法，在首页开启共享即可。", 13, theme.muted, false), 20);
        add(connectionPage, text("让连接随时就绪", 20, theme.text, true), 24);
        add(connectionPage, text("只影响这台电脑，不改变其他设备。", 13, theme.muted, false), 6);
        JSONObject connection = data.optJSONObject("connection");
        usb = toggle("USB 自动恢复", "重新接入 USB 后自动恢复连接", connection.optBoolean("usbWatchdog"));
        lan = toggle("局域网发现", "方便已配对手机在同一网络找到电脑", connection.optBoolean("lanDiscovery"));
        autoStart = toggle("登录后自动启动", "电脑登录后在托盘中待命", connection.optBoolean("autoStart"));
        add(connectionPage, text("首次使用仍需在电脑完成输入法安装、登录与系统麦克风授权。", 13, theme.muted, false), 20);
        saveButton = action("保存输入法", true, () -> save(voiceSelected)); controls.add(saveButton); add(footer, saveButton, 0);
        saveHint = text("", 12, theme.muted, false); saveHint.setGravity(Gravity.CENTER); add(footer, saveHint, 7);
        footer.setVisibility(View.VISIBLE); binding = false; selectTab(voiceSelected); updateCompactLayout();
    }
    private void updateCompactLayout() {
        boolean compact = getResources().getConfiguration().orientation == Configuration.ORIENTATION_LANDSCAPE;
        if (identityCard != null) identityCard.setVisibility(compact ? View.GONE : View.VISIBLE);
        if (status != null) status.setVisibility(compact && status.getCurrentTextColor() == theme.success ? View.GONE : View.VISIBLE);
        if (headerTitle != null) headerTitle.setText(selected == null ? "电脑与输入法" : compact ? selected.displayName : "电脑设置");
        if (saveHint != null) saveHint.setVisibility(compact ? View.GONE : View.VISIBLE);
        if (footer != null) footer.setPadding(dp(20), dp(compact ? 8 : 12), dp(20), dp(compact ? 8 : 16));
    }
    @Override public void onConfigurationChanged(Configuration configuration) {
        super.onConfigurationChanged(configuration); updateCompactLayout();
    }
    private void selectTab(boolean voice) {
        voiceSelected = voice; voicePage.setVisibility(voice ? View.VISIBLE : View.GONE); connectionPage.setVisibility(voice ? View.GONE : View.VISIBLE);
        voiceTab.setBackground(theme.shape(this, voice ? theme.surface : theme.surfaceRaised, 12));
        connectionTab.setBackground(theme.shape(this, voice ? theme.surfaceRaised : theme.surface, 12));
        voiceTab.setTextColor(voice ? theme.primary : theme.muted); connectionTab.setTextColor(voice ? theme.muted : theme.primary);
        voiceTab.setSelected(voice); connectionTab.setSelected(!voice); updateSave();
    }
    private void updateSave() {
        if (saveButton == null || footer.getVisibility() != View.VISIBLE) return;
        saveButton.setText(pending ? "正在处理…" : voiceSelected ? "保存输入法" : "保存连接设置");
        saveHint.setText((voiceSelected ? voiceDirty : connectionDirty) ? "有未保存的修改 · 保存后立即生效" : "设置按电脑保存，无需重启接收器");
    }
    private void refreshChoices() {
        for (Map.Entry<String, RadioButton> entry : engineChoices.entrySet()) {
            boolean active = entry.getKey().equals(engineId); entry.getValue().setChecked(active);
            entry.getValue().setBackground(theme.shape(this, theme.surface, 16, active ? 1 : 0, theme.primary));
        }
    }
    private Switch toggle(String title, String description, boolean checked) {
        LinearLayout row = card(connectionPage);
        Switch view = new Switch(this); view.setText(title); view.setTextSize(16); view.setTextColor(theme.text);
        view.setMinHeight(dp(48)); view.setChecked(checked); add(row, view, 0);
        add(row, text(description, 12, theme.muted, false), 3); controls.add(view);
        view.setOnCheckedChangeListener((button, value) -> { if (!binding) { connectionDirty = true; updateSave(); } }); return view;
    }
    private void capture() {
        if (engineId == null || overrides == null) return;
        JSONObject bindings = new JSONObject();
        try { for (Map.Entry<String, EditText> entry : fields.entrySet()) bindings.put(entry.getKey(), entry.getValue().getText().toString().trim());
            if (!fields.isEmpty()) overrides.put(engineId, bindings);
        } catch (JSONException ignored) { }
    }
    private void showModes() {
        controls.removeAll(fields.values()); fields.clear(); modes.removeAllViews();
        for (int i = 0; engines != null && i < engines.length(); i++) {
            JSONObject engine = engines.optJSONObject(i); if (!engineId.equals(engine.optString("id"))) continue;
            JSONArray items = engine.optJSONArray("modes"); JSONObject bindings = overrides.optJSONObject(engineId);
            for (int j = 0; items != null && j < items.length(); j++) {
                JSONObject mode = items.optJSONObject(j); String id = mode.optString("id");
                add(modes, text(mode.optString("label") + ("hold".equals(mode.optString("trigger")) ? " · 按住触发" : " · 点击切换"), 13, theme.text, true), 16);
                EditText edit = new EditText(this); edit.setSingleLine(true); edit.setTextSize(15); edit.setTextColor(theme.text); edit.setHintTextColor(theme.muted);
                edit.setMinHeight(dp(52)); edit.setPadding(dp(14), dp(12), dp(14), dp(12)); edit.setBackground(theme.shape(this, theme.surfaceRaised, 10));
                edit.setHint(mode.isNull("defaultKeys") ? "自动读取 / 未配置" : "默认 " + mode.optString("defaultKeys"));
                edit.setFilters(new android.text.InputFilter[] { new android.text.InputFilter.LengthFilter(100) });
                edit.setInputType(android.text.InputType.TYPE_CLASS_TEXT | android.text.InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
                edit.setText(bindings == null ? "" : bindings.optString(id, "")); edit.setContentDescription(mode.optString("label") + "快捷键");
                edit.addTextChangedListener(new TextWatcher() {
                    public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
                    public void onTextChanged(CharSequence s, int start, int before, int count) { if (!binding) { voiceDirty = true; updateSave(); } }
                    public void afterTextChanged(Editable e) { }
                });
                fields.put(id, edit); controls.add(edit); add(modes, edit, 8);
            }
        }
    }
    private void save(boolean voice) {
        if (pending || snapshot == null || endpoint == null) return;
        capture(); JSONObject request = new JSONObject(); final String target = selected.computerId; final PhoneDeckEndpoint destination = endpoint;
        try {
            request.put("targetComputerId", target); request.put("revision", snapshot.getString(voice ? "voiceRevision" : "connectionRevision"));
            if (voice) { request.put("activeEngine", engineId); request.put("shortcutOverrides", new JSONObject(overrides.toString())); }
            else { request.put("usbWatchdog", usb.isChecked()); request.put("lanDiscovery", lan.isChecked()); request.put("autoStart", autoStart.isChecked()); }
        } catch (JSONException e) { notice("设置不完整，请重新读取", theme.danger); return; }
        busy(true); notice("正在保存到这台电脑…", theme.muted); int ticket = generation;
        worker.execute(() -> {
            try {
                JSONObject response = PhoneDeckHttp.postJson(destination, "/api/config/desktop/" + (voice ? "voice" : "connection"), request, 5000);
                requireTarget(response, target);
                runOnUiThread(() -> {
                    if (!valid(ticket)) return;
                    try {
                        // Keep the other tab's draft AND revision. A concurrent remote edit
                        // must still produce a conflict, never a silent overwrite.
                        String revision = voice ? "voiceRevision" : "connectionRevision";
                        snapshot.put(revision, response.getString(revision));
                        if (voice) { snapshot.put("activeEngine", response.getString("activeEngine")); snapshot.put("shortcutOverrides", response.opt("shortcutOverrides")); voiceDirty = false; }
                        else { snapshot.put("connection", response.getJSONObject("connection")); connectionDirty = false; }
                        busy(false); notice("✓ 已保存，立即生效", theme.success);
                        Toast.makeText(this, "已保存到 " + selected.displayName, Toast.LENGTH_SHORT).show();
                    } catch (JSONException e) { busy(false); notice("保存结果不完整，请刷新确认", theme.warning); }
                });
            } catch (Exception e) { failure(ticket, "保存未确认\n" + e.getMessage() + "\n请刷新确认结果，再决定是否重试。"); }
        });
    }
    private void discardThen(Runnable next) {
        if (pending) { Toast.makeText(this, "请等待当前操作完成", Toast.LENGTH_SHORT).show(); return; }
        if (!voiceDirty && !connectionDirty) { next.run(); return; }
        new AlertDialog.Builder(this).setTitle("还有未保存的修改")
                .setMessage("继续后将放弃这些修改。")
                .setNegativeButton("继续编辑", null).setPositiveButton("放弃修改", (dialog, which) -> next.run()).show();
    }
    @Override public void onBackPressed() { discardThen(() -> { if (selected != null) list(); else finish(); }); }
    @Override public void onDestroy() { generation++; worker.shutdownNow(); super.onDestroy(); }
}
