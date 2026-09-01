package com.codex.phonedeck;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.Locale;
import java.util.concurrent.ConcurrentHashMap;

import javax.net.ssl.HostnameVerifier;
import javax.net.ssl.HttpsURLConnection;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocketFactory;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

final class PhoneDeckHttp {
    static final class ResponseException extends IllegalStateException {
        final int status;

        ResponseException(int status, String message) {
            super(message);
            this.status = status;
        }
    }

    private static final ConcurrentHashMap<String, SSLSocketFactory> PINNED_FACTORIES =
            new ConcurrentHashMap<>();
    private static final HostnameVerifier PINNED_HOSTNAME_VERIFIER =
            (hostname, session) -> true;

    private PhoneDeckHttp() {
    }

    static HttpURLConnection open(
            PhoneDeckEndpoint endpoint,
            String path,
            int connectTimeout,
            int readTimeout) throws Exception {
        HttpURLConnection connection = (HttpURLConnection) new URL(
                endpoint.baseUrl + path).openConnection();
        connection.setConnectTimeout(connectTimeout);
        connection.setReadTimeout(readTimeout);
        connection.setUseCaches(false);
        if (endpoint.accessToken != null) {
            connection.setRequestProperty("X-PhoneDeck-Token", endpoint.accessToken);
        }
        if (connection instanceof HttpsURLConnection) {
            if (endpoint.certificateSha256 == null
                    || endpoint.certificateSha256.isBlank()) {
                throw new CertificateException("缺少电脑证书指纹");
            }
            HttpsURLConnection secure = (HttpsURLConnection) connection;
            secure.setSSLSocketFactory(factoryForPin(endpoint.certificateSha256));
            secure.setHostnameVerifier(PINNED_HOSTNAME_VERIFIER);
        }
        return connection;
    }

    static JSONObject getJson(PhoneDeckEndpoint endpoint, String path) throws Exception {
        return getJson(endpoint, path, 900, 1300);
    }

    static JSONObject getJson(
            PhoneDeckEndpoint endpoint,
            String path,
            int connectTimeout,
            int readTimeout) throws Exception {
        HttpURLConnection connection = null;
        try {
            connection = open(endpoint, path, connectTimeout, readTimeout);
            connection.setRequestMethod("GET");
            // 成功路径不 disconnect：让 keep-alive 池复用 TLS 连接，
            // 后续音频 POST 可以省掉一次握手。
            return readResponse(connection);
        } catch (Exception exception) {
            if (connection != null) {
                connection.disconnect();
            }
            throw exception;
        }
    }

    static JSONObject postJson(
            PhoneDeckEndpoint endpoint,
            String path,
            JSONObject body,
            int readTimeout) throws Exception {
        HttpURLConnection connection = null;
        try {
            byte[] bytes = body.toString().getBytes(StandardCharsets.UTF_8);
            connection = open(endpoint, path, 900, readTimeout);
            connection.setRequestMethod("POST");
            connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
            connection.setFixedLengthStreamingMode(bytes.length);
            connection.setDoOutput(true);
            try (OutputStream output = connection.getOutputStream()) {
                output.write(bytes);
            }
            return readResponse(connection);
        } catch (Exception exception) {
            if (connection != null) {
                connection.disconnect();
            }
            throw exception;
        }
    }

    private static JSONObject readResponse(HttpURLConnection connection) throws Exception {
        int status = connection.getResponseCode();
        InputStream response = status >= 200 && status < 300
                ? connection.getInputStream() : connection.getErrorStream();
        JSONObject result = response == null ? new JSONObject() : readJson(response);
        if (status < 200 || status >= 300 || !result.optBoolean("ok", false)) {
            throw new ResponseException(
                    status, result.optString("error", "HTTP " + status));
        }
        return result;
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

    private static SSLSocketFactory factoryForPin(String rawPin) throws Exception {
        String pin = normalizePin(rawPin);
        SSLSocketFactory cached = PINNED_FACTORIES.get(pin);
        if (cached != null) {
            return cached;
        }
        SSLContext context = SSLContext.getInstance("TLS");
        context.init(null, new TrustManager[]{new PinnedTrustManager(pin)}, new SecureRandom());
        SSLSocketFactory created = context.getSocketFactory();
        SSLSocketFactory previous = PINNED_FACTORIES.putIfAbsent(pin, created);
        return previous == null ? created : previous;
    }

    private static String normalizePin(String pin) {
        return pin.replace(":", "").trim().toLowerCase(Locale.ROOT);
    }

    private static final class PinnedTrustManager implements X509TrustManager {
        private final byte[] expectedHash;

        PinnedTrustManager(String pin) {
            if (pin.length() != 64) {
                throw new IllegalArgumentException("无效的电脑证书指纹");
            }
            expectedHash = new byte[32];
            for (int index = 0; index < expectedHash.length; index++) {
                expectedHash[index] = (byte) Integer.parseInt(
                        pin.substring(index * 2, index * 2 + 2), 16);
            }
        }

        @Override
        public void checkClientTrusted(X509Certificate[] chain, String authType)
                throws CertificateException {
            throw new CertificateException("PhoneDeck 手机端不接受客户端证书");
        }

        @Override
        public void checkServerTrusted(X509Certificate[] chain, String authType)
                throws CertificateException {
            if (chain == null || chain.length == 0) {
                throw new CertificateException("电脑没有提供证书");
            }
            try {
                byte[] actual = MessageDigest.getInstance("SHA-256")
                        .digest(chain[0].getEncoded());
                if (!MessageDigest.isEqual(expectedHash, actual)) {
                    throw new CertificateException("电脑证书与 USB 配对记录不一致");
                }
            } catch (CertificateException exception) {
                throw exception;
            } catch (Exception exception) {
                throw new CertificateException("无法校验电脑证书", exception);
            }
        }

        @Override
        public X509Certificate[] getAcceptedIssuers() {
            return new X509Certificate[0];
        }
    }
}
