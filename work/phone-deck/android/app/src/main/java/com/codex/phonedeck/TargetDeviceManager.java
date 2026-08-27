package com.codex.phonedeck;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;

final class TargetDeviceManager {
    static final class Device {
        final String computerId;
        final String displayName;
        final String platform;
        final int slot;
        final long lastSeenAt;

        Device(String computerId, String displayName, String platform,
               int slot, long lastSeenAt) {
            this.computerId = computerId;
            this.displayName = displayName;
            this.platform = platform;
            this.slot = slot;
            this.lastSeenAt = lastSeenAt;
        }
    }

    private static final String PREFS_NAME = "PhoneDeckDevices";
    private static final String KEY_DEVICES = "known_devices";
    private static final String KEY_ACTIVE = "active_computer_id";
    private static final int MAX_DEVICES = 8;

    private final SharedPreferences preferences;
    private final ArrayList<Device> devices = new ArrayList<>();
    private String activeComputerId;

    TargetDeviceManager(Context context) {
        preferences = context.getApplicationContext().getSharedPreferences(
                PREFS_NAME, Context.MODE_PRIVATE);
        load();
    }

    synchronized List<Device> list() {
        ArrayList<Device> result = new ArrayList<>(devices);
        result.sort(Comparator.comparingInt(device -> device.slot));
        return result;
    }

    synchronized String getActiveComputerId() {
        return activeComputerId;
    }

    synchronized Device find(String computerId) {
        if (computerId == null) {
            return null;
        }
        for (Device device : devices) {
            if (device.computerId.equalsIgnoreCase(computerId)) {
                return device;
            }
        }
        return null;
    }

    synchronized Device upsert(String computerId, String displayName, String platform) {
        if (computerId == null || computerId.isBlank()) {
            return null;
        }
        String safeName = displayName == null || displayName.isBlank()
                ? "未命名电脑" : displayName.trim();
        String safePlatform = platform == null || platform.isBlank()
                ? "unknown" : platform.trim();
        Device existing = find(computerId);
        int slot = existing == null ? nextSlot() : existing.slot;
        Device updated = new Device(
                computerId.trim(), safeName, safePlatform, slot,
                System.currentTimeMillis());
        if (existing != null) {
            devices.remove(existing);
        } else if (devices.size() >= MAX_DEVICES) {
            Device oldest = devices.stream()
                    .min(Comparator.comparingLong(device -> device.lastSeenAt))
                    .orElse(null);
            if (oldest != null && !oldest.computerId.equalsIgnoreCase(activeComputerId)) {
                devices.remove(oldest);
            } else {
                return existing;
            }
        }
        devices.add(updated);
        if (activeComputerId == null || activeComputerId.isBlank()) {
            activeComputerId = updated.computerId;
        }
        save();
        return updated;
    }

    synchronized boolean select(String computerId) {
        Device device = find(computerId);
        if (device == null) {
            return false;
        }
        activeComputerId = device.computerId;
        save();
        return true;
    }

    private int nextSlot() {
        for (int slot = 1; slot <= MAX_DEVICES; slot++) {
            final int candidate = slot;
            if (devices.stream().noneMatch(device -> device.slot == candidate)) {
                return slot;
            }
        }
        return MAX_DEVICES;
    }

    private void load() {
        activeComputerId = preferences.getString(KEY_ACTIVE, null);
        String raw = preferences.getString(KEY_DEVICES, "[]");
        try {
            JSONArray array = new JSONArray(raw);
            for (int index = 0; index < array.length() && devices.size() < MAX_DEVICES; index++) {
                JSONObject item = array.getJSONObject(index);
                String computerId = item.optString("computerId", "").trim();
                if (computerId.isEmpty() || find(computerId) != null) {
                    continue;
                }
                devices.add(new Device(
                        computerId,
                        item.optString("displayName", "未命名电脑"),
                        item.optString("platform", "unknown"),
                        item.optInt("slot", nextSlot()),
                        item.optLong("lastSeenAt", 0L)));
            }
        } catch (Exception ignored) {
            devices.clear();
            activeComputerId = null;
            save();
        }
        if (find(activeComputerId) == null) {
            activeComputerId = devices.isEmpty() ? null : devices.get(0).computerId;
        }
    }

    private void save() {
        JSONArray array = new JSONArray();
        try {
            for (Device device : devices) {
                JSONObject item = new JSONObject();
                item.put("computerId", device.computerId);
                item.put("displayName", device.displayName);
                item.put("platform", device.platform);
                item.put("slot", device.slot);
                item.put("lastSeenAt", device.lastSeenAt);
                array.put(item);
            }
        } catch (Exception ignored) {
            return;
        }
        preferences.edit()
                .putString(KEY_DEVICES, array.toString())
                .putString(KEY_ACTIVE, activeComputerId)
                .apply();
    }
}
