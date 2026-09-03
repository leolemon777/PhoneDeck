package com.codex.phonedeck;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

final class ShortcutButtonConfig {
    static final String ACTION_KEY_CHORD = "keyChord";
    static final String ACTION_TEXT = "text";
    static final String ACTION_MACRO = "macro";

    /// 多步宏的一个步骤：keyChord 或 text，可带前置延迟。
    static final class MacroStep {
        String type;
        ArrayList<String> keys;
        int holdMs;
        String text;
        boolean submit;
        int delayBeforeMs;

        static MacroStep keyChord(List<String> keys, int holdMs, int delayBeforeMs) {
            MacroStep step = new MacroStep();
            step.type = ACTION_KEY_CHORD;
            step.keys = new ArrayList<>(keys);
            step.holdMs = holdMs;
            step.delayBeforeMs = delayBeforeMs;
            return step;
        }

        static MacroStep text(String text, boolean submit, int delayBeforeMs) {
            MacroStep step = new MacroStep();
            step.type = ACTION_TEXT;
            step.text = text;
            step.submit = submit;
            step.delayBeforeMs = delayBeforeMs;
            return step;
        }

        boolean isText() {
            return ACTION_TEXT.equals(type);
        }

        String description() {
            String prefix = delayBeforeMs > 0 ? "等" + delayBeforeMs + "ms · " : "";
            if (isText()) {
                return prefix + (submit ? text + " ⏎" : text);
            }
            return prefix + KeyCatalog.displayChord(keys);
        }

        MacroStep copy() {
            return isText()
                    ? text(text, submit, delayBeforeMs)
                    : keyChord(keys, holdMs, delayBeforeMs);
        }

        JSONObject toJson() throws JSONException {
            JSONObject value = new JSONObject();
            value.put("type", type);
            value.put("delayBeforeMs", delayBeforeMs);
            if (isText()) {
                value.put("text", text);
                value.put("submit", submit);
            } else {
                JSONArray keyArray = new JSONArray();
                for (String key : keys) {
                    keyArray.put(key);
                }
                value.put("keys", keyArray);
                value.put("holdMs", holdMs);
            }
            return value;
        }

        static MacroStep fromJson(JSONObject value) throws JSONException {
            String type = value.getString("type");
            int delay = value.optInt("delayBeforeMs", 0);
            if (ACTION_TEXT.equals(type)) {
                return text(value.getString("text"),
                        value.optBoolean("submit", false), delay);
            }
            if (!ACTION_KEY_CHORD.equals(type)) {
                throw new JSONException("不支持的宏步骤类型");
            }
            JSONArray keyArray = value.getJSONArray("keys");
            ArrayList<String> keys = new ArrayList<>();
            for (int index = 0; index < keyArray.length(); index++) {
                keys.add(keyArray.getString(index));
            }
            return keyChord(keys, value.optInt("holdMs", 45), delay);
        }
    }

    final String id;
    final boolean builtIn;
    String label;
    String icon;
    String color;
    boolean visible;
    int sortIndex;
    long updatedAt;
    String actionType;
    ArrayList<String> keys;
    int holdMs;
    String text;
    boolean submitText;
    ArrayList<MacroStep> steps;

    ShortcutButtonConfig(
            String id,
            String label,
            String icon,
            String color,
            boolean visible,
            int sortIndex,
            boolean builtIn,
            List<String> keys,
            int holdMs,
            long updatedAt) {
        this(id, label, icon, color, visible, sortIndex, builtIn,
                ACTION_KEY_CHORD, keys, holdMs, "", false, updatedAt);
    }

    private ShortcutButtonConfig(
            String id,
            String label,
            String icon,
            String color,
            boolean visible,
            int sortIndex,
            boolean builtIn,
            String actionType,
            List<String> keys,
            int holdMs,
            String text,
            boolean submitText,
            long updatedAt) {
        this.id = id;
        this.label = label;
        this.icon = icon;
        this.color = color;
        this.visible = visible;
        this.sortIndex = sortIndex;
        this.builtIn = builtIn;
        this.actionType = actionType;
        this.keys = ACTION_TEXT.equals(actionType)
                ? new ArrayList<>() : KeyCatalog.normalizeChord(keys);
        this.holdMs = holdMs;
        this.text = text == null ? "" : text;
        this.submitText = submitText;
        this.updatedAt = updatedAt;
        this.steps = new ArrayList<>();
    }

    static ShortcutButtonConfig textAction(
            String id,
            String label,
            String color,
            boolean visible,
            int sortIndex,
            boolean builtIn,
            String text,
            boolean submitText,
            long updatedAt) {
        return new ShortcutButtonConfig(
                id, label, "", color, visible, sortIndex, builtIn,
                ACTION_TEXT, new ArrayList<>(), 45, text, submitText, updatedAt);
    }

    static ShortcutButtonConfig macroAction(
            String id,
            String label,
            String color,
            boolean visible,
            int sortIndex,
            boolean builtIn,
            List<MacroStep> steps,
            long updatedAt) {
        ShortcutButtonConfig config = new ShortcutButtonConfig(
                id, label, "", color, visible, sortIndex, builtIn,
                ACTION_MACRO, new ArrayList<>(), 45, "", false, updatedAt);
        config.steps = new ArrayList<>();
        for (MacroStep step : steps) {
            config.steps.add(step.copy());
        }
        return config;
    }

    boolean isTextAction() {
        return ACTION_TEXT.equals(actionType);
    }

    boolean isMacroAction() {
        return ACTION_MACRO.equals(actionType);
    }

    String textForSend() {
        return submitText ? text + "\n" : text;
    }

    ShortcutButtonConfig copy() {
        ShortcutButtonConfig copy = new ShortcutButtonConfig(
                id, label, icon, color, visible, sortIndex, builtIn,
                actionType, keys, holdMs, text, submitText, updatedAt);
        for (MacroStep step : steps) {
            copy.steps.add(step.copy());
        }
        return copy;
    }

    String subtitle() {
        if (isTextAction()) {
            return text;
        }
        if (isMacroAction()) {
            return "宏 · " + steps.size() + " 步";
        }
        return KeyCatalog.displayChord(keys);
    }

    JSONObject toJson() throws JSONException {
        JSONObject action = new JSONObject();
        action.put("type", actionType);
        if (isTextAction()) {
            action.put("text", text);
            action.put("submit", submitText);
        } else if (isMacroAction()) {
            JSONArray stepArray = new JSONArray();
            for (MacroStep step : steps) {
                stepArray.put(step.toJson());
            }
            action.put("steps", stepArray);
        } else {
            JSONArray keyArray = new JSONArray();
            for (String key : keys) {
                keyArray.put(key);
            }
            action.put("keys", keyArray);
            action.put("holdMs", holdMs);
        }

        JSONObject value = new JSONObject();
        value.put("id", id);
        value.put("label", label);
        value.put("subtitle", subtitle());
        value.put("icon", icon);
        value.put("color", color);
        value.put("visible", visible);
        value.put("sortIndex", sortIndex);
        value.put("isBuiltIn", builtIn);
        value.put("updatedAt", updatedAt);
        value.put("action", action);
        return value;
    }

    static ShortcutButtonConfig fromJson(JSONObject value) throws JSONException {
        JSONObject action = value.getJSONObject("action");
        String actionType = action.getString("type");
        if (ACTION_TEXT.equals(actionType)) {
            return new ShortcutButtonConfig(
                    value.getString("id"),
                    value.getString("label"),
                    "",
                    value.optString("color", "blue"),
                    value.optBoolean("visible", true),
                    value.getInt("sortIndex"),
                    value.optBoolean("isBuiltIn", false),
                    ACTION_TEXT,
                    new ArrayList<>(),
                    45,
                    action.getString("text"),
                    action.optBoolean("submit", false),
                    value.optLong("updatedAt", 0));
        }
        if (ACTION_MACRO.equals(actionType)) {
            JSONArray stepArray = action.getJSONArray("steps");
            ArrayList<MacroStep> steps = new ArrayList<>();
            for (int index = 0; index < stepArray.length(); index++) {
                steps.add(MacroStep.fromJson(stepArray.getJSONObject(index)));
            }
            return macroAction(
                    value.getString("id"),
                    value.getString("label"),
                    value.optString("color", "blue"),
                    value.optBoolean("visible", true),
                    value.getInt("sortIndex"),
                    value.optBoolean("isBuiltIn", false),
                    steps,
                    value.optLong("updatedAt", 0));
        }
        if (!ACTION_KEY_CHORD.equals(actionType)) {
            throw new JSONException("不支持的动作类型");
        }
        JSONArray keyArray = action.getJSONArray("keys");
        ArrayList<String> keys = new ArrayList<>();
        for (int index = 0; index < keyArray.length(); index++) {
            keys.add(keyArray.getString(index));
        }
        return new ShortcutButtonConfig(
                value.getString("id"),
                value.getString("label"),
                value.optString("icon", ""),
                value.optString("color", "blue"),
                value.optBoolean("visible", true),
                value.getInt("sortIndex"),
                value.optBoolean("isBuiltIn", false),
                ACTION_KEY_CHORD,
                keys,
                action.optInt("holdMs", 45),
                "",
                false,
                value.optLong("updatedAt", 0));
    }
}
