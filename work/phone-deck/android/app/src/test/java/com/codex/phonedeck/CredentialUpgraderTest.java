package com.codex.phonedeck;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertTrue;
import static org.junit.Assert.fail;

import java.util.Arrays;
import java.util.Collections;
import java.util.Map;

import org.junit.Test;

/**
 * M1-A A4：升级判定（needsUpgrade）与 rotate 响应解析（parseRotateResponse）
 * 的纯函数单测。单元测试环境没有 android.jar 的 org.json 实现，
 * rotate 的解析入口做成接受 Map 的纯函数，这里用 ContractsConformanceTest
 * 的 MiniJson 把响应 JSON 解析成 Map 后驱动（issued / already-upgraded 两态，
 * 设计 §5.2：rotate 永不重发令牌本体）。
 */
public final class CredentialUpgraderTest {

    private static final String CERT_SHA256 =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static TargetDeviceManager.Device device(String lanToken, String clientId) {
        return new TargetDeviceManager.Device(
                "pc-1", "办公机", "windows", 1, 0L,
                Arrays.asList("192.168.1.10"), 8766,
                lanToken, clientId, CERT_SHA256, null);
    }

    private static Map<String, Object> parse(String json) {
        Object parsed = ContractsConformanceTest.MiniJson.parse(json);
        assertTrue("rotate 响应应为 JSON 对象", parsed instanceof Map);
        @SuppressWarnings("unchecked")
        Map<String, Object> map = (Map<String, Object>) parsed;
        return map;
    }

    @Test
    public void needsUpgradeIsTrueForLegacyOnlyDevice() {
        assertTrue(CredentialUpgrader.needsUpgrade(device("legacy-shared-token", null)));
        // 空白 clientId 视同未升级（Device 构造时会归一为 null）。
        assertTrue(CredentialUpgrader.needsUpgrade(device("legacy-shared-token", "  ")));
    }

    @Test
    public void needsUpgradeIsFalseWhenClientCredentialPresent() {
        assertFalse(CredentialUpgrader.needsUpgrade(
                device("client-token-value", "11111111-2222-3333-4444-555555555555")));
    }

    @Test
    public void needsUpgradeIsFalseWithoutLanPairing() {
        // 无共享令牌（未配对）。
        assertFalse(CredentialUpgrader.needsUpgrade(device(null, null)));
        // 无地址候选 / 无端口也不算可升级。
        assertFalse(CredentialUpgrader.needsUpgrade(new TargetDeviceManager.Device(
                "pc-1", "办公机", "windows", 1, 0L,
                Collections.emptyList(), 0,
                "legacy-shared-token", null, CERT_SHA256, null)));
        assertFalse(CredentialUpgrader.needsUpgrade(null));
    }

    @Test
    public void parseIssuedRotateResponse() {
        // 与 contracts/samples/valid/pairing-rotate-issued.json 同构
        // （冻结契约 credentialRotateResponse：issued 形态不带 ok 字段）。
        Map<String, Object> body = parse("{"
                + "\"status\": \"issued\","
                + "\"clientId\": \"55555555-5555-4555-8555-555555555555\","
                + "\"clientToken\": \"PLACEHOLDER-ROTATE-CLIENT-TOKEN-BASE64URL\","
                + "\"scopes\": [\"control\", \"audio\", \"settings\", \"update-request\"],"
                + "\"pairingId\": \"rotate-66666666-6666-4666-8666-666666666666\","
                + "\"computerId\": \"computer-placeholder-01\","
                + "\"displayName\": \"PLACEHOLDER-DESKTOP\","
                + "\"certificateSha256\": \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\""
                + "}");
        CredentialUpgrader.RotateResult result = CredentialUpgrader.parseRotateResponse(body);
        assertFalse(result.alreadyUpgraded);
        assertEquals("PLACEHOLDER-ROTATE-CLIENT-TOKEN-BASE64URL", result.clientToken);
        assertEquals("55555555-5555-4555-8555-555555555555", result.clientId);
        assertEquals(Arrays.asList("control", "audio", "settings", "update-request"),
                result.scopes);
    }

    @Test
    public void parseIssuedResponseWithoutExplicitStatus() {
        // 兼容与 /api/lan/pair/qr 同构的响应：无 status 字段但带令牌本体即 issued。
        Map<String, Object> body = parse("{"
                + "\"ok\": true,"
                + "\"clientId\": \"11111111-2222-3333-4444-555555555555\","
                + "\"clientToken\": \"token\""
                + "}");
        CredentialUpgrader.RotateResult result = CredentialUpgrader.parseRotateResponse(body);
        assertFalse(result.alreadyUpgraded);
        assertEquals("token", result.clientToken);
        assertTrue(result.scopes.isEmpty());
    }

    @Test
    public void parseAlreadyUpgradedRotateResponse() {
        Map<String, Object> body = parse("{"
                + "\"ok\": true,"
                + "\"status\": \"already-upgraded\","
                + "\"clientId\": \"11111111-2222-3333-4444-555555555555\""
                + "}");
        CredentialUpgrader.RotateResult result = CredentialUpgrader.parseRotateResponse(body);
        assertTrue(result.alreadyUpgraded);
        assertNull(result.clientToken);
        assertEquals("11111111-2222-3333-4444-555555555555", result.clientId);
        assertTrue(result.scopes.isEmpty());
    }

    @Test
    public void malformedIssuedResponsesAreRejectedWithUserFacingMessage() {
        // 缺令牌本体：不能当成 issued，也不能当成 already-upgraded。
        try {
            CredentialUpgrader.parseRotateResponse(parse("{\"ok\": true, \"status\": \"issued\"}"));
            fail("缺 clientToken 应被拒绝");
        } catch (IllegalArgumentException expected) {
            assertTrue(expected.getMessage(), expected.getMessage().contains("缺少新凭据"));
        }
        // 未知状态且无令牌。
        try {
            CredentialUpgrader.parseRotateResponse(parse("{\"ok\": true, \"status\": \"paused\"}"));
            fail("未知状态应被拒绝");
        } catch (IllegalArgumentException expected) {
            assertTrue(expected.getMessage(), expected.getMessage().contains("状态不支持"));
        }
        // 有令牌但缺客户端标识。
        try {
            CredentialUpgrader.parseRotateResponse(parse(
                    "{\"ok\": true, \"clientToken\": \"token\"}"));
            fail("缺 clientId 应被拒绝");
        } catch (IllegalArgumentException expected) {
            assertTrue(expected.getMessage(), expected.getMessage().contains("缺少客户端标识"));
        }
    }
}
