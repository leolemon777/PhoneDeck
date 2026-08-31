package com.codex.phonedeck;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

final class KeyCatalog {
    static final List<String> MODIFIERS = Collections.unmodifiableList(
            Arrays.asList("CTRL", "SHIFT", "ALT", "WIN"));
    static final List<String> BASE_KEYS;
    static final List<String> COLORS = Collections.unmodifiableList(
            Arrays.asList("blue", "purple", "green", "orange", "red", "slate"));

    static {
        ArrayList<String> keys = new ArrayList<>();
        for (char value = 'A'; value <= 'Z'; value++) {
            keys.add(String.valueOf(value));
        }
        for (char value = '0'; value <= '9'; value++) {
            keys.add(String.valueOf(value));
        }
        for (int number = 1; number <= 24; number++) {
            keys.add("F" + number);
        }
        keys.addAll(Arrays.asList(
                "ENTER", "ESC", "TAB", "SPACE", "BACKSPACE", "DELETE", "INSERT",
                "HOME", "END", "PAGEUP", "PAGEDOWN", "UP", "DOWN", "LEFT", "RIGHT",
                "PRINTSCREEN", "BACKTICK", "VOLUMEUP", "VOLUMEDOWN", "VOLUMEMUTE",
                "MEDIAPLAYPAUSE", "MEDIAPREVIOUS", "MEDIANEXT"));
        BASE_KEYS = Collections.unmodifiableList(keys);
    }

    private KeyCatalog() {
    }

    static ArrayList<String> normalizeChord(List<String> source) {
        if (source == null || source.isEmpty() || source.size() > 4) {
            throw new IllegalArgumentException("组合键必须包含 1–4 个键");
        }
        Set<String> unique = new HashSet<>();
        ArrayList<String> modifiers = new ArrayList<>();
        String baseKey = null;
        for (String raw : source) {
            String key = normalizeKey(raw);
            if (!unique.add(key)) {
                throw new IllegalArgumentException("组合键不能包含重复按键");
            }
            if (MODIFIERS.contains(key)) {
                modifiers.add(key);
            } else if (BASE_KEYS.contains(key)) {
                if (baseKey != null) {
                    throw new IllegalArgumentException("组合键只能包含一个普通键");
                }
                baseKey = key;
            } else {
                throw new IllegalArgumentException("不支持的按键：" + key);
            }
        }
        if (baseKey == null) {
            throw new IllegalArgumentException("请选择一个普通键");
        }
        modifiers.sort((left, right) -> Integer.compare(
                MODIFIERS.indexOf(left), MODIFIERS.indexOf(right)));
        modifiers.add(baseKey);
        if (modifiers.size() > 4) {
            throw new IllegalArgumentException("组合键最多 4 个键");
        }
        return modifiers;
    }

    static String displayChord(List<String> keys) {
        ArrayList<String> labels = new ArrayList<>();
        for (String key : normalizeChord(keys)) {
            labels.add(displayKey(key));
        }
        return String.join(" + ", labels);
    }

    static String displayKey(String key) {
        switch (key) {
            case "CTRL": return "Ctrl";
            case "SHIFT": return "Shift";
            case "ALT": return "Alt";
            case "WIN": return "Win";
            case "ESC": return "Esc";
            case "PAGEUP": return "Page Up";
            case "PAGEDOWN": return "Page Down";
            case "PRINTSCREEN": return "Print Screen";
            case "BACKTICK": return "`";
            case "VOLUMEUP": return "音量 +";
            case "VOLUMEDOWN": return "音量 −";
            case "VOLUMEMUTE": return "静音";
            case "MEDIAPLAYPAUSE": return "播放 / 暂停";
            case "MEDIAPREVIOUS": return "上一首";
            case "MEDIANEXT": return "下一首";
            case "UP": return "↑";
            case "DOWN": return "↓";
            case "LEFT": return "←";
            case "RIGHT": return "→";
            default: return key.substring(0, 1) + key.substring(1).toLowerCase(Locale.ROOT);
        }
    }

    private static String normalizeKey(String raw) {
        if (raw == null) {
            return "";
        }
        String value = raw.trim().replace(" ", "")
                .replace("_", "").replace("-", "").toUpperCase(Locale.ROOT);
        if ("CONTROL".equals(value) || "PRIMARY".equals(value)) {
            return "CTRL";
        }
        if ("ESCAPE".equals(value)) {
            return "ESC";
        }
        return value;
    }
}
