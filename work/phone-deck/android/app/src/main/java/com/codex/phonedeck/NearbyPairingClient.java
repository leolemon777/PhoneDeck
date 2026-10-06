package com.codex.phonedeck;

import org.json.JSONObject;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.Locale;

/**
 * 同一 Wi-Fi 免扫码连接：发现电脑 → 读取并钉扎它的 TLS 证书 → 提交 clientId 与随机 nonce →
 * 电脑本机弹框由人点「允许」→ 保存这台手机的独立凭据。双方各自用
 * SHA256("证书指纹|clientId|nonce") 算出四位校验码，供用户在电脑确认框上核对（防中间人）。
 */
final class NearbyPairingClient {
    /// 电脑端最多等 60 秒有人确认，读超时留出余量。
    static final int REQUEST_READ_TIMEOUT_MS = 70_000;

    private NearbyPairingClient() {
    }

    static String newNonce() {
        byte[] bytes = new byte[16];
        new SecureRandom().nextBytes(bytes);
        StringBuilder hex = new StringBuilder(32);
        for (byte value : bytes) hex.append(String.format(Locale.ROOT, "%02x", value));
        return hex.toString();
    }

    /** 与 PairingWindowManager.NearbyCheckCode 同一公式：前 4 字节大端取模 10000。 */
    static String checkCode(String certificateSha256, String clientId, String nonce) {
        try {
            byte[] hash = MessageDigest.getInstance("SHA-256").digest(
                    (certificateSha256.toLowerCase(Locale.ROOT) + "|"
                            + clientId.toLowerCase(Locale.ROOT) + "|" + nonce)
                            .getBytes(StandardCharsets.UTF_8));
            long value = ((hash[0] & 0xFFL) << 24) | ((hash[1] & 0xFFL) << 16)
                    | ((hash[2] & 0xFFL) << 8) | (hash[3] & 0xFFL);
            return String.format(Locale.ROOT, "%04d", value % 10000);
        } catch (Exception exception) {
            throw new IllegalStateException(exception);
        }
    }

    /** 提交连接请求；服务端挂起直到电脑上有人允许/拒绝或超时。失败抛异常，message 面向用户。 */
    static JSONObject request(String host, int port, String computerId, String certificateSha256,
                              String clientId, String clientLabel, String nonce) throws Exception {
        PhoneDeckEndpoint endpoint = new PhoneDeckEndpoint(
                "https://" + host + ":" + port, null, certificateSha256, "连接", computerId);
        JSONObject body = new JSONObject()
                .put("clientId", clientId)
                .put("clientLabel", clientLabel)
                .put("nonce", nonce);
        try {
            JSONObject result = PhoneDeckHttp.postJson(endpoint, "/api/lan/pair/request", body,
                    REQUEST_READ_TIMEOUT_MS);
            if (!computerId.equals(result.optString("computerId", computerId))) {
                throw new IllegalArgumentException("应答的电脑身份与发现结果不一致");
            }
            return result;
        } catch (PhoneDeckHttp.ResponseException exception) {
            throw new IllegalArgumentException(friendlyError(exception.status, exception.getMessage()));
        }
    }

    /** 凭据验证：Bearer 头请求 health，能过鉴权即证明逐手机凭据有效。 */
    static boolean verifyCredential(String host, String computerId, int port,
                                    String clientToken, String clientId,
                                    String certificateSha256) {
        try {
            PhoneDeckEndpoint endpoint = new PhoneDeckEndpoint(
                    "https://" + host + ":" + port, clientToken, certificateSha256,
                    "验证", computerId, clientId);
            JSONObject health = PhoneDeckHttp.getJson(endpoint, "/api/health", 1500, 2500);
            return health.optBoolean("ok", false);
        } catch (Exception exception) {
            return false;
        }
    }

    private static String friendlyError(int status, String serverMessage) {
        switch (status) {
            case 403: return "电脑上点了「拒绝」";
            case 404: return "这台电脑的言渡版本太旧，请先更新电脑端";
            case 408: return "电脑上没有人确认，请重试";
            case 409: return "电脑正在确认另一台手机，请稍后再试";
            case 429: return "请求太频繁，请稍后再试";
            default: return serverMessage == null || serverMessage.isBlank()
                    ? "连接失败（HTTP " + status + "）" : serverMessage;
        }
    }
}
