package com.codex.phonedeck;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

final class ShortcutButtonConfig {
    static final String ACTION_KEY_CHORD = "keyChord";
    static final String ACTION_TEXT = "text";

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

    boolean isTextAction() {
        return ACTION_TEXT.equals(actionType);
    }

    String textForSend() {
        return submitText ? text + "\n" : text;
    }

    ShortcutButtonConfig copy() {
        return new ShortcutButtonConfig(
                id, label, icon, color, visible, sortIndex, builtIn,
                actionType, keys, holdMs, text, submitText, updatedAt);
    }

    String subtitle() {
        return isTextAction() ? text : KeyCatalog.displayChord(keys);
    }

    JSONObject toJson() throws JSONException {
        JSONObject action = new JSONObject();
        action.put("type", actionType);
        if (isTextAction()) {
            action.put("text", text);
            action.put("submit", submitText);
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
