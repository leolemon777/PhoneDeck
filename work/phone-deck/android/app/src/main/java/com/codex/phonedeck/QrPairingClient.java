package com.codex.phonedeck;

import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;
import java.util.UUID;

/**
 * M1-A A3：扫码配对客户端（docs/M1A_PAIRING_DESIGN.md §3）。
 * QR 载荷严格校验（版本/指纹/GUID/材料长度）→ 经发现层或手动地址定位电脑 →
 * 钉扎 TLS 提交材料（请求挂起等待电脑本机确认，读超时 40s）→ 保存逐手机凭据。
 */
final class QrPairingClient {
    static final int PAIRING_READ_TIMEOUT_MS = 40_000;

    static final class QrPayload {
        final String computerId;
        final String displayName;
        final int httpsPort;
        final String certificateSha256;
        final String pairingId;
        final String oneTimeMaterial;

        private QrPayload(
                String computerId,
                String displayName,
                int httpsPort,
                String certificateSha256,
                String pairingId,
                String oneTimeMaterial) {
            this.computerId = computerId;
            this.displayName = displayName;
            this.httpsPort = httpsPort;
            this.certificateSha256 = certificateSha256;
            this.pairingId = pairingId;
            this.oneTimeMaterial = oneTimeMaterial;
        }
    }

    private QrPairingClient() {
    }

    /** 解析并严格校验 QR 文本；任何字段不合契约即抛 IllegalArgumentException（错误文案面向用户）。 */
    static QrPayload parseQr(String qrText) {
        if (qrText == null || qrText.isBlank()) {
            throw new IllegalArgumentException("二维码内容为空");
        }
        JSONObject root;
        try {
            root = new JSONObject(qrText);
        } catch (Exception exception) {
            throw new IllegalArgumentException("不是有效的 PhoneDeck 配对码");
        }
        if (root.optInt("version", -1) != 1) {
            throw new IllegalArgumentException("配对码版本不支持");
        }
        String computerId = root.optString("computerId", "").trim();
        if (computerId.isEmpty() || computerId.length() > 128) {
            throw new IllegalArgumentException("配对码缺少电脑身份");
        }
        String displayName = root.optString("displayName", computerId).trim();
        int httpsPort = root.optInt("httpsPort", -1);
        if (httpsPort < 1 || httpsPort > 65535) {
            throw new IllegalArgumentException("配对码端口无效");
        }
        String certificateSha256 = root.optString("certificateSha256", "").trim().toLowerCase();
        if (!certificateSha256.matches("[0-9a-f]{64}")) {
            throw new IllegalArgumentException("配对码证书指纹无效");
        }
        String pairingId = root.optString("pairingId", "").trim();
        try {
            UUID.fromString(pairingId);
        } catch (IllegalArgumentException exception) {
            throw new IllegalArgumentException("配对码配对标识无效");
        }
        String material = root.optString("oneTimeMaterial", "");
        if (material.length() < 32 || material.length() > 512) {
            throw new IllegalArgumentException("配对码一次性材料无效");
        }
        return new QrPayload(computerId, displayName, httpsPort,
                certificateSha256, pairingId, material);
    }

    /**
     * 提交配对（请求在服务端挂起等待本机确认，最长 30s + 余量）。
     * 返回服务端 JSON（ok/clientId/clientToken/scopes…）；失败抛 Exception，message 面向用户。
     */
    static JSONObject submit(
            String host,
            QrPayload payload,
            String clientId,
            String clientLabel) throws Exception {
        PhoneDeckEndpoint endpoint = new PhoneDeckEndpoint(
                "https://" + host + ":" + payload.httpsPort,
                null,
                payload.certificateSha256,
                "配对",
                payload.computerId);
        JSONObject body = new JSONObject()
                .put("pairingId", payload.pairingId)
                .put("oneTimeMaterial", payload.oneTimeMaterial)
                .put("clientId", clientId)
                .put("clientLabel", clientLabel);
        try {
            return PhoneDeckHttp.postJson(endpoint, "/api/lan/pair/qr", body,
                    PAIRING_READ_TIMEOUT_MS);
        } catch (PhoneDeckHttp.ResponseException exception) {
            throw new IllegalArgumentException(friendlyError(exception.status, exception.getMessage()));
        }
    }

    /** 凭据验证：Bearer 头请求 health，能过鉴权即证明逐手机凭据有效（V09 证据）。 */
    static boolean verifyCredential(String host, String computerId, int port,
                                    String clientToken, String clientId,
                                    String certificateSha256) {
        try {
            PhoneDeckEndpoint endpoint = new PhoneDeckEndpoint(
                    "https://" + host + ":" + port,
                    clientToken,
                    certificateSha256,
                    "验证",
                    computerId,
                    clientId);
            JSONObject health = PhoneDeckHttp.getJson(endpoint, "/api/health", 1500, 2500);
            return health.optBoolean("ok", false);
        } catch (Exception exception) {
            return false;
        }
    }

    /** 与服务端错误路径对齐的用户文案（401/403/404/408/429）。 */
    private static String friendlyError(int status, String serverMessage) {
        switch (status) {
            case 401: return "配对材料无效或已使用，请在电脑上重新生成";
            case 403: return "电脑端已拒绝本次配对";
            case 404: return "配对窗口未开启，请在电脑上先开启配对";
            case 408: return "电脑端确认超时，请重试";
            case 429: return "失败次数过多，请在电脑上重新开启配对";
            default: return serverMessage == null || serverMessage.isBlank()
                    ? "配对失败（HTTP " + status + "）" : serverMessage;
        }
    }

    /** 发现结果按 computerId 匹配并返回安全地址候选（找不到返回空表）。 */
    static List<String> matchDiscovery(android.content.Context context, String computerId) {
        List<String> addresses = new ArrayList<>();
        try {
            java.util.Map<String, LanDiscoveryClient.DiscoveredComputer> discovered =
                    LanDiscoveryClient.discover(context, 2500);
            LanDiscoveryClient.DiscoveredComputer match = discovered == null
                    ? null : discovered.get(computerId);
            if (match != null && TargetDeviceManager.isAddressCandidateSafe(match.hostAddress)) {
                addresses.add(match.hostAddress);
            }
        } catch (Exception ignored) {
            // 发现失败走手动地址输入。
        }
        return addresses;
    }
}
