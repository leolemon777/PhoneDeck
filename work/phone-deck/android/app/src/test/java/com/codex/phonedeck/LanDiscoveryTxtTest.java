package com.codex.phonedeck;

import org.junit.Test;

import java.nio.charset.StandardCharsets;

import static org.junit.Assert.*;

/** M1-A A5：mDNS 候选属性解码（V10 子集：发现数据只含身份/端口字段，不含令牌/指纹）。 */
public final class LanDiscoveryTxtTest {
    @Test
    public void decodesAsciiAttributesAndTreatsEmptyAsNull() {
        assertEquals("11111111-2222",
                LanDiscoveryClient.decodeAttribute("11111111-2222".getBytes(StandardCharsets.US_ASCII)));
        assertEquals("PLACEHOLDER-DESKTOP",
                LanDiscoveryClient.decodeAttribute(" PLACEHOLDER-DESKTOP ".getBytes(StandardCharsets.US_ASCII)));
        assertNull(LanDiscoveryClient.decodeAttribute(null));
        assertNull(LanDiscoveryClient.decodeAttribute(new byte[0]));
        assertNull(LanDiscoveryClient.decodeAttribute("   ".getBytes(StandardCharsets.US_ASCII)));
    }
}
