package com.codex.phonedeck;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.io.IOException;

import org.junit.Test;

/** 断线日志的阶段判断与一行格式；不触碰 Android 类。 */
public final class ConnectionDiagnosticsTest {
    private static final long NONE = Long.MIN_VALUE;

    @Test
    public void stageFollowsTheFurthestProgressReached() {
        assertEquals("建立连接", ConnectionDiagnostics.stage(NONE, NONE, NONE));
        assertEquals("等待首次写入", ConnectionDiagnostics.stage(100, NONE, NONE));
        assertEquals("持续写入", ConnectionDiagnostics.stage(100, 200, NONE));
        assertEquals("收尾", ConnectionDiagnostics.stage(100, 200, 900));
    }

    @Test
    public void describeIncludesClassMessageAndOneCauseLevel() {
        IOException failure = new IOException("Broken pipe", new IllegalStateException("socket closed"));
        String text = ConnectionDiagnostics.describe(failure);
        assertTrue(text, text.startsWith("IOException: Broken pipe"));
        assertTrue(text, text.contains("IllegalStateException: socket closed"));
        assertEquals("未知", ConnectionDiagnostics.describe(null));
        assertTrue(ConnectionDiagnostics.describe(new RuntimeException("x".repeat(500))).length() <= 161);
    }

    @Test
    public void lineOmitsUnknownFieldsAndNeverNeedsSecrets() {
        String line = ConnectionDiagnostics.line("办公室(1a2b3c4d)", "连接中断，将重试", "持续写入",
                5230, 5004, "SocketTimeoutException: timeout", "Wi-Fi -71dBm 52Mbps 2.4GHz");
        assertTrue(line, line.contains("阶段=持续写入"));
        assertTrue(line, line.contains("已连 5230ms"));
        assertTrue(line, line.contains("无进展 5004ms"));
        assertTrue(line, line.contains("-71dBm"));

        String bare = ConnectionDiagnostics.line("电脑(x)", "等待重连 500ms", null, -1, -1, null, null);
        assertEquals("共享供音 电脑(x) 等待重连 500ms", bare);
        assertFalse(bare.contains("阶段"));
    }

    @Test
    public void targetLabelUsesNameAndShortId() {
        assertEquals("办公室(0c24d2de)", ConnectionDiagnostics.targetLabel(
                "办公室", "0c24d2de-4dda-4d0e-b74d-56c489601d2e"));
        assertEquals("电脑(abc)", ConnectionDiagnostics.targetLabel(null, "abc"));
    }
}
