package com.codex.phonedeck;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.Collections;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * M1-A A4：旧共享令牌 → 逐手机凭据升级客户端（docs/M1A_PAIRING_DESIGN.md §5.2）。
 * rotate 用旧共享令牌头鉴权（PhoneDeckEndpoint.accessToken 即旧 lanToken），
 * 服务端生成该手机专属 clientId+令牌并只返回一次；同手机再次 rotate 返回
 * already-upgraded 状态而非令牌本体（rotate 永不重发，G-1 处置）。
 * 响应解析做成接受 Map 的纯函数：单元测试环境没有 android.jar 的
 * org.json 实现，测试用 MiniJson 样本驱动 parseRotateResponse。
 */
final class CredentialUpgrader {
    static final int ROTATE_READ_TIMEOUT_MS = 40_000;
    static final String STATUS_ISSUED = "issued";
    static final String STATUS_ALREADY_UPGRADED = "already-upgraded";

    static final class RotateResult {
        /** true=服务端确认已升级过且不再重发令牌，找回只能重新扫码配对。 */
        final boolean alreadyUpgraded;
        final String clientToken;
        final String clientId;
        final List<String> scopes;

        private RotateResult(boolean alreadyUpgraded, String clientToken,
                             String clientId, List<String> scopes) {
            this.alreadyUpgraded = alreadyUpgraded;
            this.clientToken = clientToken;
            this.clientId = clientId;
            this.scopes = scopes;
        }

        static RotateResult issued(String clientToken, String clientId, List<String> scopes) {
            return new RotateResult(false, clientToken, clientId, scopes);
        }

        static RotateResult alreadyUpgraded(String clientId) {
            return new RotateResult(true, null, clientId, Collections.emptyList());
        }
    }

    private CredentialUpgrader() {
    }

    /** legacy-only = 有 LAN 配对但还没有逐手机 clientId：应提示升级（§5.2 首连强提示）。 */
    static boolean needsUpgrade(TargetDeviceManager.Device device) {
        return device != null && device.hasLanPairing() && !device.hasClientCredential();
    }

    /**
     * 请求升级：POST /api/lan/credential/rotate，body {clientId}，
     * 用旧共享令牌头鉴权（endpoint 即 LAN 探测得到的旧凭据端点）。
     * 失败抛 Exception，message 面向用户。
     * 不走 PhoneDeckHttp.postJson：readResponse 强制要求 ok=true，而冻结契约
     * credentialRotateResponse 的 issued 形态不带 ok 字段（仅 already-upgraded 带），
     * 这里按 HTTP 状态判定成功，2xx 即交给 parseRotateResponse 解析。
     */
    static RotateResult rotate(PhoneDeckEndpoint endpoint, String clientId) throws Exception {
        if (endpoint == null || !endpoint.isLan()) {
            throw new IllegalArgumentException("电脑局域网不在线，无法升级");
        }
        byte[] bytes = new JSONObject()
                .put("clientId", clientId)
                .toString()
                .getBytes(java.nio.charset.StandardCharsets.UTF_8);
        java.net.HttpURLConnection connection = null;
        try {
            connection = PhoneDeckHttp.open(
                    endpoint, "/api/lan/credential/rotate", 900, ROTATE_READ_TIMEOUT_MS);
            connection.setRequestMethod("POST");
            connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
            connection.setFixedLengthStreamingMode(bytes.length);
            connection.setDoOutput(true);
            try (java.io.OutputStream output = connection.getOutputStream()) {
                output.write(bytes);
            }
            int status = connection.getResponseCode();
            java.io.InputStream stream = status >= 200 && status < 300
                    ? connection.getInputStream() : connection.getErrorStream();
            JSONObject response = stream == null
                    ? new JSONObject() : new JSONObject(readAll(stream));
            if (status < 200 || status >= 300) {
                String serverMessage = response.optString("error", "");
                throw new IllegalArgumentException(friendlyError(status,
                        serverMessage.isBlank() ? null : serverMessage));
            }
            return parseRotateResponse(toPlainMap(response));
        } finally {
            if (connection != null) {
                connection.disconnect();
            }
        }
    }

    private static String readAll(java.io.InputStream input) throws Exception {
        StringBuilder content = new StringBuilder();
        try (java.io.BufferedReader reader = new java.io.BufferedReader(
                new java.io.InputStreamReader(input, java.nio.charset.StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) {
                content.append(line);
            }
        }
        return content.toString();
    }

    /**
     * 纯解析（可单测）：issued 取 clientToken/clientId/scopes；
     * already-upgraded 不含令牌本体。响应缺令牌或缺客户端标识时抛
     * IllegalArgumentException（文案面向用户），不产生半可信凭据。
     */
    static RotateResult parseRotateResponse(Map<String, Object> body) {
        if (body == null) {
            throw new IllegalArgumentException("升级响应无效");
        }
        String status = asString(body.get("status"));
        if (STATUS_ALREADY_UPGRADED.equals(status)) {
            return RotateResult.alreadyUpgraded(asString(body.get("clientId")));
        }
        String clientToken = asString(body.get("clientToken"));
        if (clientToken == null || clientToken.isEmpty()) {
            boolean knownIssued = status == null || status.isEmpty()
                    || STATUS_ISSUED.equals(status);
            throw new IllegalArgumentException(knownIssued
                    ? "升级响应缺少新凭据" : "升级响应状态不支持：" + status);
        }
        String issuedClientId = asString(body.get("clientId"));
        if (issuedClientId == null || issuedClientId.isEmpty()) {
            throw new IllegalArgumentException("升级响应缺少客户端标识");
        }
        List<String> scopes = new ArrayList<>();
        Object scopesRaw = body.get("scopes");
        if (scopesRaw instanceof List) {
            for (Object scope : (List<?>) scopesRaw) {
                String value = asString(scope);
                if (value != null && !value.isEmpty()) {
                    scopes.add(value);
                }
            }
        }
        return RotateResult.issued(clientToken, issuedClientId, scopes);
    }

    /** 与服务端 rotate 错误路径对齐的用户文案（401/404/429）。 */
    private static String friendlyError(int status, String serverMessage) {
        switch (status) {
            case 401: return "旧共享令牌已失效，请重新扫码配对";
            case 404: return "电脑端版本过旧，不支持凭据升级，请先更新电脑端";
            case 429: return "升级请求过于频繁，请稍后再试";
            default: return serverMessage == null || serverMessage.isBlank()
                    ? "升级失败（HTTP " + status + "）" : serverMessage;
        }
    }

    /** org.json 值 → 纯 Java 值（对象→Map、数组→List），供纯函数解析与单测共用入口。 */
    private static Map<String, Object> toPlainMap(JSONObject object) {
        Map<String, Object> map = new LinkedHashMap<>();
        Iterator<String> keys = object.keys();
        while (keys.hasNext()) {
            String key = keys.next();
            map.put(key, fromJsonValue(object.opt(key)));
        }
        return map;
    }

    private static Object fromJsonValue(Object value) {
        if (value instanceof JSONObject) {
            return toPlainMap((JSONObject) value);
        }
        if (value instanceof JSONArray) {
            JSONArray array = (JSONArray) value;
            List<Object> list = new ArrayList<>();
            for (int index = 0; index < array.length(); index++) {
                list.add(fromJsonValue(array.opt(index)));
            }
            return list;
        }
        return value;
    }

    private static String asString(Object value) {
        if (!(value instanceof String)) {
            return null;
        }
        String text = ((String) value).trim();
        return text.isEmpty() ? null : text;
    }
}
