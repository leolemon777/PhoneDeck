package com.codex.phonedeck;

import org.junit.Test;
import static org.junit.Assert.assertEquals;

public class UiTextTest {
    @Test public void absentForegroundNeverPrintsNull() {
        for (String value : new String[]{null, "", "  ", "null", " NULL "}) {
            assertEquals("", UiText.optional(value));
        }
        assertEquals("Null Editor", UiText.optional(" Null Editor "));
    }
    @Test public void separatesOnlyExactDevicePrefix() {
        assertEquals("Wi-Fi 在线", UiText.connectionDetail("工作台 · Mac", "工作台 · Mac · Wi-Fi 在线"));
        assertEquals("发送失败", UiText.connectionDetail("工作台", "发送失败"));
        assertEquals("电脑B · 离线", UiText.connectionDetail("电脑A", "电脑B · 离线"));
    }
}
