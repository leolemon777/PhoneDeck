package com.codex.phonedeck;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.security.cert.CertificateException;

import org.junit.Test;

public final class LanPairingRejectionTest {
    @Test
    public void unauthorizedResponsesArePairingRejections() {
        assertTrue(PhoneDeckLanClient.isPairingRejection(
                new PhoneDeckHttp.ResponseException(401, "局域网连接尚未配对或密钥无效")));
        assertTrue(PhoneDeckLanClient.isPairingRejection(
                new PhoneDeckHttp.ResponseException(403, "禁止访问")));
    }

    @Test
    public void transientNetworkFailuresAreNotPairingRejections() {
        assertFalse(PhoneDeckLanClient.isPairingRejection(
                new java.io.IOException("ETIMEDOUT")));
        assertFalse(PhoneDeckLanClient.isPairingRejection(
                new PhoneDeckHttp.ResponseException(500, "内部错误")));
        assertFalse(PhoneDeckLanClient.isPairingRejection(
                new PhoneDeckHttp.ResponseException(503, "服务不可用")));
        assertFalse(PhoneDeckLanClient.isPairingRejection(null));
    }

    @Test
    public void wrappedCertificatePinningFailuresArePairingRejections() {
        Exception wrapped = (Exception) new javax.net.ssl.SSLHandshakeException(
                "握手失败")
                .initCause(new CertificateException("电脑证书与 USB 配对记录不一致"));
        assertTrue(PhoneDeckLanClient.isPairingRejection(wrapped));
    }
}
