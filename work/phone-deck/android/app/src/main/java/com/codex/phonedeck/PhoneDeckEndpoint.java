package com.codex.phonedeck;

final class PhoneDeckEndpoint {
    static final PhoneDeckEndpoint USB = new PhoneDeckEndpoint(
            "http://127.0.0.1:8765", null, null, "USB", null);

    final String baseUrl;
    final String accessToken;
    final String certificateSha256;
    final String label;
    final String computerId;

    PhoneDeckEndpoint(
            String baseUrl,
            String accessToken,
            String certificateSha256,
            String label,
            String computerId) {
        this.baseUrl = baseUrl;
        this.accessToken = accessToken;
        this.certificateSha256 = certificateSha256;
        this.label = label;
        this.computerId = computerId;
    }

    boolean isLan() {
        return accessToken != null && certificateSha256 != null;
    }
}
