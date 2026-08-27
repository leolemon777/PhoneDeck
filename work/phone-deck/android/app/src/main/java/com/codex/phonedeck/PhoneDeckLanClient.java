package com.codex.phonedeck;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;

final class PhoneDeckLanClient {
    static final class ProbeResult {
        final PhoneDeckEndpoint endpoint;
        final JSONObject health;

        ProbeResult(PhoneDeckEndpoint endpoint, JSONObject health) {
            this.endpoint = endpoint;
            this.health = health;
        }
    }

    private PhoneDeckLanClient() {
    }

    static TargetDeviceManager.Device pairOverUsb(TargetDeviceManager manager)
            throws Exception {
        JSONObject pairing = PhoneDeckHttp.postJson(
                PhoneDeckEndpoint.USB, "/api/lan/pair", new JSONObject(), 1800);
        JSONArray addressArray = pairing.optJSONArray("addresses");
        ArrayList<String> addresses = new ArrayList<>();
        if (addressArray != null) {
            for (int index = 0; index < addressArray.length(); index++) {
                String address = addressArray.optString(index, "").trim();
                if (!address.isEmpty()) {
                    addresses.add(address);
                }
            }
        }
        TargetDeviceManager.Device paired = manager.saveLanPairing(
                pairing.optString("computerId", null),
                pairing.optString("displayName", null),
                pairing.optString("platform", "windows"),
                addresses,
                pairing.optInt("port", 0),
                pairing.optString("accessToken", null),
                pairing.optString("certificateSha256", null));
        if (paired == null) {
            throw new IllegalStateException("电脑返回的 Wi-Fi 配对资料不完整");
        }
        return paired;
    }

    static ProbeResult probe(TargetDeviceManager.Device device) {
        if (device == null || !device.hasLanPairing()) {
            return null;
        }
        for (String address : device.lanAddresses) {
            try {
                PhoneDeckEndpoint endpoint = new PhoneDeckEndpoint(
                        "https://" + address + ":" + device.lanPort,
                        device.lanToken,
                        device.certificateSha256,
                        "Wi-Fi",
                        device.computerId);
                JSONObject health = PhoneDeckHttp.getJson(endpoint, "/api/health");
                if (device.computerId.equalsIgnoreCase(
                        health.optString("computerId", ""))) {
                    return new ProbeResult(endpoint, health);
                }
            } catch (Exception ignored) {
                // 尝试配对时记录的下一个网卡地址。
            }
        }
        return null;
    }
}
