package com.codex.phonedeck;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.AtomicFile;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.UUID;

final class ShortcutConfigRepository {
    private static final int SCHEMA_VERSION = 1;
    private static final int MAX_BUTTONS = 100;
    private static final String SYNC_PREFS = "phonedeck_shortcut_sync";
    private static final String AGENT_SYNC_REVISION = "agent_sync_revision";
    private final AtomicFile atomicFile;
    private final SharedPreferences syncPreferences;
    private String recoveryNotice;

    ShortcutConfigRepository(Context context) {
        atomicFile = new AtomicFile(new File(context.getFilesDir(), "shortcut-config.json"));
        syncPreferences = context.getSharedPreferences(SYNC_PREFS, Context.MODE_PRIVATE);
    }

    synchronized ArrayList<ShortcutButtonConfig> load() {
        File file = atomicFile.getBaseFile();
        if (!file.exists()) {
            ArrayList<ShortcutButtonConfig> defaults = defaults();
            save(defaults);
            return copyList(defaults);
        }
        try (InputStream input = atomicFile.openRead()) {
            JSONObject root = new JSONObject(readUtf8(input));
            int schemaVersion = root.getInt("schemaVersion");
            if (schemaVersion != SCHEMA_VERSION) {
                throw new IllegalStateException("不支持的配置版本：" + schemaVersion);
            }
            JSONArray profiles = root.getJSONArray("profiles");
            if (profiles.length() == 0) {
                throw new IllegalStateException("配置缺少 default profile");
            }
            JSONObject profile = profiles.getJSONObject(0);
            JSONArray buttons = profile.getJSONArray("buttons");
            if (buttons.length() > MAX_BUTTONS) {
                throw new IllegalStateException("配置按钮数量超过限制");
            }
            ArrayList<ShortcutButtonConfig> values = new ArrayList<>();
            for (int index = 0; index < buttons.length(); index++) {
                values.add(ShortcutButtonConfig.fromJson(buttons.getJSONObject(index)));
            }
            values.sort(Comparator.comparingInt(value -> value.sortIndex));
            validateAndNormalize(values);
            if (mergeAgentDefaults(values)) {
                save(values);
            }
            return values;
        } catch (Exception exception) {
            backupCorruptFile();
            recoveryNotice = exception.getMessage() != null
                    && exception.getMessage().startsWith("不支持的配置版本")
                    ? "配置版本不兼容，已备份原文件并使用默认布局"
                    : "快捷键配置损坏，已备份原文件并恢复默认布局";
            ArrayList<ShortcutButtonConfig> defaults = defaults();
            save(defaults);
            return copyList(defaults);
        }
    }

    synchronized void save(List<ShortcutButtonConfig> source) {
        ArrayList<ShortcutButtonConfig> normalized = copyList(source);
        validateAndNormalize(normalized);
        FileOutputStream output = null;
        try {
            JSONObject root = toRootJson(normalized);
            byte[] bytes = root.toString(2).getBytes(StandardCharsets.UTF_8);
            output = atomicFile.startWrite();
            output.write(bytes);
            output.flush();
            output.getFD().sync();
            atomicFile.finishWrite(output);
        } catch (Exception exception) {
            if (output != null) {
                atomicFile.failWrite(output);
            }
            throw new IllegalStateException("保存快捷键配置失败", exception);
        }
    }

    synchronized void resetAll() {
        save(defaults());
    }

    /// 应用当前电脑控制台发布的 Agent 文本指令。revision 未变化时零写入；
    /// 普通快捷键、宏和手机本地布局不受影响。
    synchronized boolean applyAgentOverrides(JSONObject root) {
        try {
            if (root.optInt("schemaVersion", 0) != 1) {
                return false;
            }
            long revision = root.optLong("updatedAt", 0);
            if (revision <= syncPreferences.getLong(AGENT_SYNC_REVISION, 0)) {
                return false;
            }
            JSONArray overrides = root.getJSONArray("buttons");
            if (overrides.length() == 0 || overrides.length() > 16) {
                return false;
            }
            ArrayList<ShortcutButtonConfig> current = load();
            Set<String> supportedIds = new HashSet<>(Arrays.asList(
                    "agentPlan", "agentGoal", "agentCompact", "agentClear"));
            Set<String> seenIds = new HashSet<>();
            for (int index = 0; index < overrides.length(); index++) {
                JSONObject override = overrides.getJSONObject(index);
                String id = override.getString("id");
                String label = override.getString("label").trim();
                String text = override.getString("text").trim();
                if (!supportedIds.contains(id) || !seenIds.add(id)
                        || label.isEmpty() || label.length() > 24
                        || text.isEmpty() || text.length() > 512
                        || text.contains("\r") || text.contains("\n")) {
                    return false;
                }
                ShortcutButtonConfig target = null;
                for (ShortcutButtonConfig candidate : current) {
                    if (candidate.id.equals(id)) {
                        target = candidate;
                        break;
                    }
                }
                if (target == null) {
                    return false;
                }
                target.label = label;
                target.actionType = ShortcutButtonConfig.ACTION_TEXT;
                target.text = text;
                target.submitText = override.optBoolean("submit", true);
                target.visible = override.optBoolean("visible", true);
                target.keys = new ArrayList<>();
                target.steps = new ArrayList<>();
                target.holdMs = 45;
                target.updatedAt = revision;
            }
            if (seenIds.size() != supportedIds.size()) {
                return false;
            }
            save(current);
            syncPreferences.edit().putLong(AGENT_SYNC_REVISION, revision).apply();
            return true;
        } catch (Exception exception) {
            return false;
        }
    }

    synchronized String consumeRecoveryNotice() {
        String notice = recoveryNotice;
        recoveryNotice = null;
        return notice;
    }

    ShortcutButtonConfig defaultForId(String id) {
        for (ShortcutButtonConfig value : defaults()) {
            if (value.id.equals(id)) {
                return value.copy();
            }
        }
        return null;
    }

    ShortcutButtonConfig newCustomButton() {
        ArrayList<ShortcutButtonConfig> current = load();
        int sortIndex = current.stream().mapToInt(value -> value.sortIndex).max().orElse(-1) + 1;
        return new ShortcutButtonConfig(
                "custom-" + UUID.randomUUID(), "新按钮", "", "blue", true,
                sortIndex, false, Arrays.asList("F1"), 45, System.currentTimeMillis());
    }

    private static JSONObject toRootJson(List<ShortcutButtonConfig> buttons) throws Exception {
        JSONArray buttonArray = new JSONArray();
        for (ShortcutButtonConfig button : buttons) {
            buttonArray.put(button.toJson());
        }
        JSONObject profile = new JSONObject();
        profile.put("id", "default");
        profile.put("name", "通用");
        profile.put("columns", 3);
        profile.put("buttons", buttonArray);
        JSONObject root = new JSONObject();
        root.put("schemaVersion", SCHEMA_VERSION);
        root.put("activeProfileId", "default");
        root.put("profiles", new JSONArray().put(profile));
        return root;
    }

    private static void validateAndNormalize(ArrayList<ShortcutButtonConfig> values) {
        if (values.size() > MAX_BUTTONS) {
            throw new IllegalArgumentException("快捷键按钮最多 100 个");
        }
        Set<String> ids = new HashSet<>();
        for (int index = 0; index < values.size(); index++) {
            ShortcutButtonConfig value = values.get(index);
            if (value.id == null || value.id.isBlank() || value.id.length() > 128
                    || !ids.add(value.id)) {
                throw new IllegalArgumentException("快捷键配置包含无效或重复 ID");
            }
            value.label = value.label == null ? "" : value.label.trim();
            if (value.label.isEmpty() || value.label.length() > 24) {
                throw new IllegalArgumentException("按钮名称必须为 1–24 个字符");
            }
            value.icon = "";
            if (!KeyCatalog.COLORS.contains(value.color)) {
                value.color = "blue";
            }
            if (value.isTextAction()) {
                value.text = value.text == null ? "" : value.text.trim();
                if (value.text.isEmpty() || value.text.length() > 512
                        || value.text.contains("\r") || value.text.contains("\n")) {
                    throw new IllegalArgumentException("文本指令必须为 1–512 个字符的单行文本");
                }
                value.keys = new ArrayList<>();
                value.holdMs = 45;
            } else if (value.isMacroAction()) {
                if (value.steps == null || value.steps.isEmpty()
                        || value.steps.size() > 8) {
                    throw new IllegalArgumentException("宏必须包含 1–8 个步骤");
                }
                for (ShortcutButtonConfig.MacroStep step : value.steps) {
                    step.delayBeforeMs = Math.max(0, Math.min(2000, step.delayBeforeMs));
                    if (step.isText()) {
                        step.text = step.text == null ? "" : step.text.trim();
                        if (step.text.isEmpty() || step.text.length() > 512
                                || step.text.contains("\r") || step.text.contains("\n")) {
                            throw new IllegalArgumentException("宏的文本步骤必须为 1–512 个字符的单行文本");
                        }
                    } else {
                        step.type = ShortcutButtonConfig.ACTION_KEY_CHORD;
                        step.keys = KeyCatalog.normalizeChord(step.keys);
                        if (step.holdMs < 20 || step.holdMs > 500) {
                            throw new IllegalArgumentException("宏步骤按键持续时间超出安全范围");
                        }
                    }
                }
                value.keys = new ArrayList<>();
                value.text = "";
            } else {
                value.actionType = ShortcutButtonConfig.ACTION_KEY_CHORD;
                value.keys = KeyCatalog.normalizeChord(value.keys);
                if (value.holdMs < 20 || value.holdMs > 500) {
                    throw new IllegalArgumentException("按键持续时间超出安全范围");
                }
            }
            value.sortIndex = index;
        }
    }

    private void backupCorruptFile() {
        File source = atomicFile.getBaseFile();
        if (!source.exists()) {
            return;
        }
        File backup = new File(source.getParentFile(),
                "shortcut-config.corrupt-" + System.currentTimeMillis() + ".json");
        try (FileInputStream input = new FileInputStream(source);
             FileOutputStream output = new FileOutputStream(backup)) {
            byte[] buffer = new byte[8192];
            int count;
            while ((count = input.read(buffer)) >= 0) {
                output.write(buffer, 0, count);
            }
        } catch (Exception ignored) {
            // 即使备份失败，也必须让 App 用默认配置恢复启动。
        }
    }

    private static String readUtf8(InputStream input) throws Exception {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = input.read(buffer)) >= 0) {
            output.write(buffer, 0, count);
        }
        return output.toString(StandardCharsets.UTF_8.name());
    }

    private static ArrayList<ShortcutButtonConfig> copyList(List<ShortcutButtonConfig> source) {
        ArrayList<ShortcutButtonConfig> copy = new ArrayList<>();
        for (ShortcutButtonConfig value : source) {
            copy.add(value.copy());
        }
        return copy;
    }

    private static ArrayList<ShortcutButtonConfig> defaults() {
        ArrayList<ShortcutButtonConfig> values = new ArrayList<>();
        addAgentDefaults(values);
        addDefault(values, "copy", "复制", "blue", "CTRL", "C");
        addDefault(values, "paste", "粘贴", "green", "CTRL", "V");
        addDefault(values, "cut", "剪切", "orange", "CTRL", "X");
        addDefault(values, "undo", "撤销", "slate", "CTRL", "Z");
        addDefault(values, "redo", "重做", "slate", "CTRL", "Y");
        addDefault(values, "selectAll", "全选", "purple", "CTRL", "A");
        addDefault(values, "save", "保存", "green", "CTRL", "S");
        addDefault(values, "altTab", "切换窗口", "purple", "ALT", "TAB");
        addDefault(values, "screenshot", "截图", "blue", "WIN", "SHIFT", "S");
        addDefault(values, "enter", "回车", "green", "ENTER");
        addDefault(values, "backspace", "退格", "slate", "BACKSPACE");
        addDefault(values, "escape", "退出", "red", "ESC");
        addDefault(values, "switchInputMethod", "切换输入法", "purple", "WIN", "SPACE");
        addDefault(values, "volumeDown", "音量 −", "slate", "VOLUMEDOWN");
        addDefault(values, "volumeMute", "静音", "orange", "VOLUMEMUTE");
        addDefault(values, "volumeUp", "音量 +", "blue", "VOLUMEUP");
        addDefault(values, "left", "向左", "slate", "LEFT");
        addDefault(values, "right", "向右", "slate", "RIGHT");
        return values;
    }

    private static boolean mergeAgentDefaults(ArrayList<ShortcutButtonConfig> values) {
        ArrayList<ShortcutButtonConfig> agentDefaults = new ArrayList<>();
        addAgentDefaults(agentDefaults);
        Set<String> existingIds = new HashSet<>();
        for (ShortcutButtonConfig value : values) {
            existingIds.add(value.id);
        }
        boolean changed = false;
        for (int index = agentDefaults.size() - 1; index >= 0; index--) {
            ShortcutButtonConfig value = agentDefaults.get(index);
            if (existingIds.add(value.id)) {
                values.add(0, value);
                changed = true;
            }
        }
        if (changed) {
            validateAndNormalize(values);
        }
        return changed;
    }

    private static void addAgentDefaults(ArrayList<ShortcutButtonConfig> target) {
        addDefault(target, "agentInterrupt", "打断", "red", "ESC");
        addTextDefault(target, "agentPlan", "规划", "purple", "/plan");
        addTextDefault(target, "agentGoal", "目标", "blue", "/goal");
        addTextDefault(target, "agentCompact", "压缩上下文", "green", "/compact");
        addTextDefault(target, "agentClear", "新会话", "orange", "/clear");
        addDefault(target, "agentAcceptAll", "接受全部", "green", "CTRL", "ENTER");
        addDefault(target, "agentRejectAll", "拒绝全部", "red", "CTRL", "BACKSPACE");
    }

    /// 导出当前配置文件的原始 JSON；文件尚不存在时返回 null。
    synchronized String exportJson() {
        try (InputStream input = atomicFile.openRead()) {
            return readUtf8(input);
        } catch (Exception exception) {
            return null;
        }
    }

    /// 导入并替换当前配置。任何一步解析或校验失败都返回 false 且不改动现有文件。
    synchronized boolean importJson(String json) {
        try {
            JSONObject root = new JSONObject(json);
            int schemaVersion = root.getInt("schemaVersion");
            if (schemaVersion != SCHEMA_VERSION) {
                return false;
            }
            JSONArray profiles = root.getJSONArray("profiles");
            if (profiles.length() == 0) {
                return false;
            }
            JSONArray buttons = profiles.getJSONObject(0).getJSONArray("buttons");
            if (buttons.length() == 0 || buttons.length() > MAX_BUTTONS) {
                return false;
            }
            ArrayList<ShortcutButtonConfig> values = new ArrayList<>();
            for (int index = 0; index < buttons.length(); index++) {
                values.add(ShortcutButtonConfig.fromJson(buttons.getJSONObject(index)));
            }
            save(values);
            return true;
        } catch (Exception exception) {
            return false;
        }
    }

    private static void addDefault(
            ArrayList<ShortcutButtonConfig> target,
            String id,
            String label,
            String color,
            String... keys) {
        target.add(new ShortcutButtonConfig(
                id, label, "", color, true, target.size(), true,
                Arrays.asList(keys), 45, 0));
    }

    private static void addTextDefault(
            ArrayList<ShortcutButtonConfig> target,
            String id,
            String label,
            String color,
            String text) {
        target.add(ShortcutButtonConfig.textAction(
                id, label, color, true, target.size(), true, text, true, 0));
    }
}
