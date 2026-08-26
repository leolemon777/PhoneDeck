package com.codex.phonedeck;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

final class ShortcutButtonConfig {
    final String id;
    final boolean builtIn;
    String label;
    String icon;
    String color;
    boolean visible;
    int sortIndex;
    long updatedAt;
    ArrayList<String> keys;
    int holdMs;

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
        this.id = id;
        this.label = label;
        this.icon = icon;
        this.color = color;
        this.visible = visible;
        this.sortIndex = sortIndex;
        this.builtIn = builtIn;
        this.keys = KeyCatalog.normalizeChord(keys);
        this.holdMs = holdMs;
        this.updatedAt = updatedAt;
    }

    ShortcutButtonConfig copy() {
        return new ShortcutButtonConfig(
                id, label, icon, color, visible, sortIndex, builtIn,
                keys, holdMs, updatedAt);
    }

    String subtitle() {
        return KeyCatalog.displayChord(keys);
    }

    JSONObject toJson() throws JSONException {
        JSONArray keyArray = new JSONArray();
        for (String key : keys) {
            keyArray.put(key);
        }
        JSONObject action = new JSONObject();
        action.put("type", "keyChord");
        action.put("keys", keyArray);
        action.put("holdMs", holdMs);

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
        if (!"keyChord".equals(action.getString("type"))) {
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
                keys,
                action.optInt("holdMs", 45),
                value.optLong("updatedAt", 0));
    }
}
