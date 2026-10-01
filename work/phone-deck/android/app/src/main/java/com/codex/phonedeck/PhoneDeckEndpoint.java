package com.codex.phonedeck;

final class PhoneDeckEndpoint {
    static final PhoneDeckEndpoint USB = new PhoneDeckEndpoint(
            "http://127.0.0.1:8765", null, null, "USB", null);

    final String baseUrl;
    final String accessToken;
    final String certificateSha256;
    final String label;
    final String computerId;
    /** M1-A：非空表示逐手机凭据，请求用 Bearer 头；空则用旧共享令牌头。 */
    final String clientId;

    PhoneDeckEndpoint(
            String baseUrl,
            String accessToken,
            String certificateSha256,
            String label,
            String computerId) {
        this(baseUrl, accessToken, certificateSha256, label, computerId, null);
    }

    PhoneDeckEndpoint(
            String baseUrl,
            String accessToken,
            String certificateSha256,
            String label,
            String computerId,
            String clientId) {
        this.baseUrl = baseUrl;
        this.accessToken = accessToken;
        this.certificateSha256 = certificateSha256;
        this.label = label;
        this.computerId = computerId;
        this.clientId = clientId == null || clientId.isBlank() ? null : clientId.trim();
    }

    boolean isLan() {
        return accessToken != null && certificateSha256 != null;
    }
}
