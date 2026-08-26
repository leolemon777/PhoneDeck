package com.codex.phonedeck;

import android.content.Context;
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
    private final AtomicFile atomicFile;
    private String recoveryNotice;

    ShortcutConfigRepository(Context context) {
        atomicFile = new AtomicFile(new File(context.getFilesDir(), "shortcut-config.json"));
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
                "custom-" + UUID.randomUUID(), "新按钮", "✨", "blue", true,
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
            value.icon = value.icon == null ? "" : value.icon.trim();
            if (value.icon.length() > 8) {
                throw new IllegalArgumentException("图标或 Emoji 不能超过 8 个字符");
            }
            if (!KeyCatalog.COLORS.contains(value.color)) {
                value.color = "blue";
            }
            value.keys = KeyCatalog.normalizeChord(value.keys);
            if (value.holdMs < 20 || value.holdMs > 500) {
                throw new IllegalArgumentException("按键持续时间超出安全范围");
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
        addDefault(values, "copy", "复制", "📋", "blue", "CTRL", "C");
        addDefault(values, "paste", "粘贴", "📥", "green", "CTRL", "V");
        addDefault(values, "cut", "剪切", "✂", "orange", "CTRL", "X");
        addDefault(values, "undo", "撤销", "↶", "slate", "CTRL", "Z");
        addDefault(values, "redo", "重做", "↷", "slate", "CTRL", "Y");
        addDefault(values, "selectAll", "全选", "☑", "purple", "CTRL", "A");
        addDefault(values, "save", "保存", "💾", "green", "CTRL", "S");
        addDefault(values, "altTab", "切换窗口", "▣", "purple", "ALT", "TAB");
        addDefault(values, "screenshot", "截图", "▧", "blue", "WIN", "SHIFT", "S");
        addDefault(values, "enter", "回车", "↵", "green", "ENTER");
        addDefault(values, "backspace", "退格", "⌫", "slate", "BACKSPACE");
        addDefault(values, "escape", "退出", "Esc", "red", "ESC");
        addDefault(values, "switchInputMethod", "切换输入法", "文", "purple", "WIN", "SPACE");
        addDefault(values, "volumeDown", "音量 −", "🔉", "slate", "VOLUMEDOWN");
        addDefault(values, "volumeMute", "静音", "🔇", "orange", "VOLUMEMUTE");
        addDefault(values, "volumeUp", "音量 +", "🔊", "blue", "VOLUMEUP");
        addDefault(values, "left", "向左", "←", "slate", "LEFT");
        addDefault(values, "right", "向右", "→", "slate", "RIGHT");
        return values;
    }

    private static void addDefault(
            ArrayList<ShortcutButtonConfig> target,
            String id,
            String label,
            String icon,
            String color,
            String... keys) {
        target.add(new ShortcutButtonConfig(
                id, label, icon, color, true, target.size(), true,
                Arrays.asList(keys), 45, 0));
    }
}
