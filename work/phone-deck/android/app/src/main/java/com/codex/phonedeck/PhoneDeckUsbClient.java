package com.codex.phonedeck;

import android.content.Context;

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

    static String sendKeyChord(
            Context context,
            List<String> keys,
            int holdMs,
            BluetoothTransport bluetoothTransport) throws Exception {
        JSONArray keyArray = new JSONArray();
        for (String key : KeyCatalog.normalizeChord(keys)) {
            keyArray.put(key);
        }
        return sendAction(context, bluetoothTransport,
                (requestId, sessionId, computerId) -> createKeyChordBody(
                        keyArray, holdMs, requestId, sessionId, computerId));
    }

    static String sendText(
            Context context,
            String text,
            BluetoothTransport bluetoothTransport) throws Exception {
        if (text == null || text.isEmpty() || text.length() > 513) {
            throw new IllegalArgumentException("文本指令长度无效");
        }
        return sendAction(context, bluetoothTransport,
                (requestId, sessionId, computerId) -> createTextBody(
                        text, requestId, sessionId, computerId));
    }

    private static String sendAction(
            Context context,
            BluetoothTransport bluetoothTransport,
            BodyFactory bodyFactory) throws Exception {
        String requestId = UUID.randomUUID().toString();
        String sessionId = UUID.randomUUID().toString();

        Exception usbFailure = null;
        ServerInfo usbServer;
        try {
            usbServer = health();
        } catch (Exception exception) {
            usbServer = null;
            usbFailure = exception;
        }

        TargetDeviceManager deviceManager = new TargetDeviceManager(context);
        String activeComputerId = deviceManager.getActiveComputerId();
        if (activeComputerId == null || activeComputerId.isBlank()) {
            if (usbServer != null && !usbServer.computerId.isEmpty()) {
                activeComputerId = usbServer.computerId;
            } else if (canUseBluetooth(bluetoothTransport)) {
                activeComputerId = bluetoothTransport.getComputerId();
            }
        }

        TargetDeviceManager.Device activeDevice = deviceManager.find(activeComputerId);
        PhoneDeckLanClient.ProbeResult lanResult = PhoneDeckLanClient.probe(activeDevice);
        if (lanResult != null) {
            JSONObject body = bodyFactory.create(
                    requestId, sessionId, activeComputerId);
            try {
                JSONObject result = PhoneDeckHttp.postJson(
                        lanResult.endpoint, "/api/input", body, 1800);
                return result.optString("message", "电脑已确认") + " · Wi-Fi";
            } catch (Exception ignored) {
                // 使用同一 requestId 尝试同一目标电脑的 USB/蓝牙备用通道。
            }
        }

        if (usbServer != null
                && usbServer.protocolVersion >= 2
                && !usbServer.computerId.isEmpty()
                && usbServer.computerId.equalsIgnoreCase(activeComputerId)) {
            JSONObject body = bodyFactory.create(
                    requestId, sessionId, usbServer.computerId);
            try {
                return post("/api/input", body) + " · USB";
            } catch (Exception exception) {
                if (canUseBluetooth(bluetoothTransport)
                        && usbServer.computerId.equalsIgnoreCase(
                        bluetoothTransport.getComputerId())
                        && bluetoothTransport.sendAndWaitForAck(body, 1400)) {
                    return "电脑已确认 · 蓝牙";
                }
                // USB 请求可能已经执行但响应丢失。只有同一 computerId 的
                // 蓝牙连接可以用同一 requestId 安全补收 ACK，不能误发给另一台电脑。
                throw exception;
            }
        }
        if (usbServer != null && usbServer.computerId.equalsIgnoreCase(activeComputerId)) {
            usbFailure = new IllegalStateException(
                    "USB 电脑端需要升级到 PhoneDeck 1.5.0");
        }

        if (canUseBluetooth(bluetoothTransport)
                && bluetoothTransport.getComputerId().equalsIgnoreCase(activeComputerId)) {
            JSONObject body = bodyFactory.create(
                    requestId, sessionId, bluetoothTransport.getComputerId());
            if (bluetoothTransport.sendAndWaitForAck(body, 1400)) {
                return "电脑已确认 · 蓝牙";
            }
            throw new IllegalStateException("蓝牙电脑没有确认测试按键", usbFailure);
        }
        throw new IllegalStateException(
                usbFailure == null || usbFailure.getMessage() == null
                        ? "当前目标电脑没有可用的 Wi-Fi、USB 或蓝牙连接"
                        : usbFailure.getMessage(),
                usbFailure);
    }

    private interface BodyFactory {
        JSONObject create(String requestId, String sessionId, String computerId)
                throws Exception;
    }

    private static boolean canUseBluetooth(BluetoothTransport transport) {
        return transport != null
                && transport.isConnected()
                && transport.getProtocolVersion() >= 2
                && transport.getComputerId() != null
                && !transport.getComputerId().isBlank();
    }

    private static JSONObject createKeyChordBody(
            JSONArray keys,
            int holdMs,
            String requestId,
            String sessionId,
            String computerId) throws Exception {
        JSONObject body = new JSONObject();
        body.put("protocolVersion", 2);
        body.put("requestId", requestId);
        body.put("sessionId", sessionId);
        body.put("targetComputerId", computerId);
        body.put("action", "keyChord");
        body.put("keys", keys);
        body.put("holdMs", holdMs);
        return body;
    }

    private static JSONObject createTextBody(
            String text,
            String requestId,
            String sessionId,
            String computerId) throws Exception {
        JSONObject body = new JSONObject();
        body.put("protocolVersion", 2);
        body.put("requestId", requestId);
        body.put("sessionId", sessionId);
        body.put("targetComputerId", computerId);
        body.put("action", "text");
        body.put("text", text);
        return body;
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
