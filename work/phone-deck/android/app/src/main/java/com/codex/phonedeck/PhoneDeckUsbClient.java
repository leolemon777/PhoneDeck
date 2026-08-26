package com.codex.phonedeck;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.UUID;

final class PhoneDeckUsbClient {
    private static final String SERVER = "http://127.0.0.1:8765";

    static final class ServerInfo {
        final int protocolVersion;
        final String computerId;

        ServerInfo(int protocolVersion, String computerId) {
            this.protocolVersion = protocolVersion;
            this.computerId = computerId;
        }
    }

    private PhoneDeckUsbClient() {
    }

    static ServerInfo health() throws Exception {
        HttpURLConnection connection = null;
        try {
            connection = (HttpURLConnection) new URL(SERVER + "/api/health").openConnection();
            connection.setConnectTimeout(800);
            connection.setReadTimeout(1200);
            connection.setRequestMethod("GET");
            int status = connection.getResponseCode();
            if (status != 200) {
                throw new IllegalStateException("电脑端健康检查失败：HTTP " + status);
            }
            JSONObject body = readJson(connection.getInputStream());
            return new ServerInfo(
                    body.optInt("protocolVersion", 0),
                    body.optString("computerId", ""));
        } finally {
            if (connection != null) {
                connection.disconnect();
            }
        }
    }

    static String sendKeyChord(List<String> keys, int holdMs) throws Exception {
        ServerInfo server = health();
        if (server.protocolVersion < 2 || server.computerId.isEmpty()) {
            throw new IllegalStateException("电脑端需要升级到 PhoneDeck 1.5.0");
        }
        JSONArray keyArray = new JSONArray();
        for (String key : KeyCatalog.normalizeChord(keys)) {
            keyArray.put(key);
        }
        JSONObject body = new JSONObject();
        body.put("protocolVersion", 2);
        body.put("requestId", UUID.randomUUID().toString());
        body.put("sessionId", UUID.randomUUID().toString());
        body.put("targetComputerId", server.computerId);
        body.put("action", "keyChord");
        body.put("keys", keyArray);
        body.put("holdMs", holdMs);
        return post("/api/input", body);
    }

    private static String post(String endpoint, JSONObject body) throws Exception {
        HttpURLConnection connection = null;
        try {
            byte[] bytes = body.toString().getBytes(StandardCharsets.UTF_8);
            connection = (HttpURLConnection) new URL(SERVER + endpoint).openConnection();
            connection.setConnectTimeout(800);
            connection.setReadTimeout(1800);
            connection.setRequestMethod("POST");
            connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
            connection.setFixedLengthStreamingMode(bytes.length);
            connection.setDoOutput(true);
            try (OutputStream output = connection.getOutputStream()) {
                output.write(bytes);
            }
            int status = connection.getResponseCode();
            InputStream response = status >= 200 && status < 300
                    ? connection.getInputStream() : connection.getErrorStream();
            JSONObject result = response == null ? new JSONObject() : readJson(response);
            if (status < 200 || status >= 300 || !result.optBoolean("ok", false)) {
                throw new IllegalStateException(result.optString("error", "HTTP " + status));
            }
            return result.optString("message", "电脑已确认");
        } finally {
            if (connection != null) {
                connection.disconnect();
            }
        }
    }

    private static JSONObject readJson(InputStream input) throws Exception {
        StringBuilder content = new StringBuilder();
        try (BufferedReader reader = new BufferedReader(
                new InputStreamReader(input, StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) {
                content.append(line);
            }
        }
        return new JSONObject(content.toString());
    }
}
