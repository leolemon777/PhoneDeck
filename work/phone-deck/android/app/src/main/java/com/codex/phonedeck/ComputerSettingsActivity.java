package com.codex.phonedeck;

import android.app.Activity;
import android.os.Bundle;
import android.view.View;
import android.widget.*;
import org.json.*;
import java.io.IOException;
import java.util.*;
import java.util.concurrent.*;

/** Settings are edited on the phone, but owned by the selected computer. */
public final class ComputerSettingsActivity extends Activity {
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private PhoneDeckTheme theme;
    private TargetDeviceManager devices;
    private LinearLayout page, modes;
    private TextView status;
    private TargetDeviceManager.Device selected;
    private PhoneDeckEndpoint endpoint;
    private JSONObject snapshot, overrides;
    private JSONArray engines;
    private String engineId;
    private final Map<String, EditText> fields = new LinkedHashMap<>();
    private final List<View> controls = new ArrayList<>();
    private Switch usb, lan, autoStart;
    private boolean pending;
    private int generation;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        theme = PhoneDeckTheme.load(this); theme.applyWindow(this);
        devices = new TargetDeviceManager(this);
        list();
    }
    private void shell(String title) {
        controls.clear(); fields.clear(); generation++;
        page = new LinearLayout(this); page.setOrientation(LinearLayout.VERTICAL);
        int padding = PhoneDeckTheme.dp(this, 20); page.setPadding(padding, padding, padding, padding);
        ScrollView scroll = new ScrollView(this); scroll.setFillViewport(true); scroll.addView(page);
        setContentView(theme.wrapContent(this, scroll));
        button("‹ 返回", this::onBackPressed, false);
        label(title, 24);
        status = label("", 14);
    }
    private TextView label(String text, int size) {
        TextView view = new TextView(this); view.setText(text); view.setTextSize(size);
        view.setTextColor(theme.text); view.setPadding(0, 12, 0, 12); page.addView(view); return view;
    }
    private Button button(String title, Runnable action, boolean control) {
        Button b = new Button(this); b.setText(title); b.setAllCaps(false);
        page.addView(b, new LinearLayout.LayoutParams(-1, -2)); b.setOnClickListener(v -> action.run());
        if (control) controls.add(b); return b;
    }
    private void list() {
        selected = null; endpoint = null; snapshot = null; pending = false;
        shell("电脑与输入法");
        status.setText("选择要设置的电脑。输入法和连接设置按电脑保存；按键布局与外观跟随手机。");
        List<TargetDeviceManager.Device> known = devices.list();
        for (TargetDeviceManager.Device d : known)
            button(d.slot + "号 · " + d.displayName + "\n" + d.platform, () -> open(d), false);
        if (known.isEmpty()) label("请先打开电脑接收器，用 USB 连接并完成首次配对。", 16);
    }
    private void open(TargetDeviceManager.Device d) {
        selected = d; snapshot = null; endpoint = null;
        shell(d.slot + "号 · " + d.displayName);
        button("重新读取设置", this::load, true);
        load();
    }
    private void busy(boolean value) { pending = value; for (View v : controls) v.setEnabled(!value); }
    private static void requireTarget(JSONObject value, String target) throws IOException {
        if (!target.equalsIgnoreCase(value.optString("computerId")))
            throw new IOException("连接的电脑发生变化，请重新读取设置");
    }
    private void load() {
        if (pending || selected == null) return;
        busy(true); status.setText("正在连接这台电脑…");
        int ticket = generation; TargetDeviceManager.Device d = selected;
        worker.execute(() -> {
            try {
                PhoneDeckLanClient.ProbeResult probe = PhoneDeckLanClient.probe(d);
                PhoneDeckEndpoint target;
                JSONObject health;
                if (probe != null) { target = probe.endpoint; health = probe.health; }
                else { target = PhoneDeckEndpoint.USB; health = PhoneDeckHttp.getJson(target, "/api/health"); }
                requireTarget(health, d.computerId);
                JSONArray capabilities = health.optJSONArray("capabilities");
                boolean supported = false;
                if (capabilities != null) for (int i = 0; i < capabilities.length(); i++)
                    if ("phoneManagedSettingsV1".equals(capabilities.optString(i))) supported = true;
                if (!supported) throw new IOException("这台电脑尚不支持手机设置，请先升级 Windows 接收器");
                JSONObject data = PhoneDeckHttp.getJson(target, "/api/config/desktop");
                requireTarget(data, d.computerId);
                PhoneDeckEndpoint resolved = target;
                runOnUiThread(() -> { if (!valid(ticket)) return; endpoint = resolved; busy(false); render(data, "已连接 · 设置只应用到这台电脑"); });
            } catch (Exception e) { failure(ticket, "无法读取：" + e.getMessage()); }
        });
    }
    private boolean valid(int ticket) { return !isDestroyed() && !isFinishing() && generation == ticket; }
    private void failure(int ticket, String message) {
        runOnUiThread(() -> { if (!valid(ticket)) return; busy(false); status.setText(message); });
    }
    private void render(JSONObject data, String message) {
        snapshot = data;
        shell(selected.slot + "号 · " + selected.displayName);
        status.setText(message + (data.optBoolean("busy") ? "\n正在供音或听写，请停止后再保存。" : ""));
        button("重新读取设置", this::load, true);
        label("手机控制听写 · 输入法", 19);
        label("仅选择这台电脑已安装并配置的输入法。共享麦克风不依赖这里的选择。", 14);
        engines = data.optJSONArray("engines");
        try { overrides = new JSONObject(data.getJSONObject("shortcutOverrides").toString()); }
        catch (JSONException e) { overrides = new JSONObject(); }
        engineId = data.optString("activeEngine");
        Spinner picker = new Spinner(this); controls.add(picker);
        ArrayList<String> labels = new ArrayList<>(); int active = 0;
        for (int i = 0; engines != null && i < engines.length(); i++) {
            JSONObject engine = engines.optJSONObject(i);
            labels.add(engine.optString("displayName") + (engine.optBoolean("experimental") ? " · 待验证" : ""));
            if (engineId.equals(engine.optString("id"))) active = i;
        }
        ArrayAdapter<String> adapter = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, labels);
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        picker.setAdapter(adapter); page.addView(picker); picker.setSelection(active);
        modes = new LinearLayout(this); modes.setOrientation(LinearLayout.VERTICAL); page.addView(modes);
        showModes();
        picker.setOnItemSelectedListener(new android.widget.AdapterView.OnItemSelectedListener() {
            public void onNothingSelected(android.widget.AdapterView<?> parent) { }
            public void onItemSelected(android.widget.AdapterView<?> parent, View v, int position, long id) {
                capture(); engineId = engines.optJSONObject(position).optString("id"); showModes();
            }
        });
        button("保存输入法与快捷键", () -> save(true), true);
        label("电脑连接", 19);
        JSONObject connection = data.optJSONObject("connection");
        usb = toggle("USB 断线自动恢复", connection.optBoolean("usbWatchdog"));
        lan = toggle("允许局域网发现", connection.optBoolean("lanDiscovery"));
        autoStart = toggle("电脑登录后自动启动", connection.optBoolean("autoStart"));
        button("保存连接设置", () -> save(false), true);
        label("输入法安装、登录及系统麦克风授权，需在该电脑首次完成。快捷键留空时使用输入法自动读取值或默认值；填写时必须与输入法自身设置一致。", 14);
    }
    private Switch toggle(String title, boolean checked) {
        Switch view = new Switch(this); view.setText(title); view.setTextColor(theme.text);
        view.setPadding(0, 18, 0, 18); view.setChecked(checked); page.addView(view); controls.add(view); return view;
    }
    private void capture() {
        if (engineId == null || overrides == null) return;
        JSONObject bindings = new JSONObject();
        try { for (Map.Entry<String, EditText> entry : fields.entrySet())
            bindings.put(entry.getKey(), entry.getValue().getText().toString().trim());
            if (!fields.isEmpty()) overrides.put(engineId, bindings);
        } catch (JSONException ignored) { }
    }
    private void showModes() {
        controls.removeAll(fields.values()); fields.clear(); modes.removeAllViews();
        for (int i = 0; engines != null && i < engines.length(); i++) {
            JSONObject engine = engines.optJSONObject(i);
            if (!engineId.equals(engine.optString("id"))) continue;
            JSONArray items = engine.optJSONArray("modes"); JSONObject bindings = overrides.optJSONObject(engineId);
            for (int j = 0; items != null && j < items.length(); j++) {
                JSONObject mode = items.optJSONObject(j); String id = mode.optString("id");
                TextView label = new TextView(this); label.setTextColor(theme.text);
                label.setText(mode.optString("label") + ("hold".equals(mode.optString("trigger")) ? " · 按住触发" : " · 切换触发"));
                label.setPadding(0, 18, 0, 4); modes.addView(label);
                EditText edit = new EditText(this); edit.setSingleLine(true); edit.setTextColor(theme.text);
                edit.setHintTextColor(theme.muted);
                edit.setHint(mode.isNull("defaultKeys") ? "自动读取 / 未配置" : "默认 " + mode.optString("defaultKeys"));
                edit.setFilters(new android.text.InputFilter[] { new android.text.InputFilter.LengthFilter(100) });
                edit.setText(bindings == null ? "" : bindings.optString(id, ""));
                edit.setContentDescription(mode.optString("label") + "快捷键");
                fields.put(id, edit); controls.add(edit); modes.addView(edit);
            }
        }
    }
    private void save(boolean voice) {
        if (pending || snapshot == null || endpoint == null) return;
        capture(); JSONObject request = new JSONObject();
        final String target = selected.computerId;
        final PhoneDeckEndpoint destination = endpoint;
        try {
            request.put("targetComputerId", target);
            request.put("revision", snapshot.getString(voice ? "voiceRevision" : "connectionRevision"));
            if (voice) { request.put("activeEngine", engineId); request.put("shortcutOverrides", new JSONObject(overrides.toString())); }
            else { request.put("usbWatchdog", usb.isChecked()); request.put("lanDiscovery", lan.isChecked()); request.put("autoStart", autoStart.isChecked()); }
        } catch (JSONException e) { status.setText("设置不完整，请重新读取"); return; }
        busy(true); status.setText("正在保存到这台电脑…"); int ticket = generation;
        worker.execute(() -> {
            try {
                JSONObject response = PhoneDeckHttp.postJson(destination, "/api/config/desktop/" + (voice ? "voice" : "connection"), request, 5000);
                requireTarget(response, target);
                runOnUiThread(() -> { if (!valid(ticket)) return; busy(false); render(response, "已保存并立即生效"); });
            } catch (Exception e) { failure(ticket, "保存未确认：" + e.getMessage() + "\n请重新读取确认结果，再决定是否重试。"); }
        });
    }
    @Override public void onBackPressed() {
        if (pending) { Toast.makeText(this, "请等待当前操作完成", Toast.LENGTH_SHORT).show(); return; }
        if (selected != null) list(); else super.onBackPressed();
    }
    @Override public void onDestroy() { generation++; worker.shutdownNow(); super.onDestroy(); }
}
