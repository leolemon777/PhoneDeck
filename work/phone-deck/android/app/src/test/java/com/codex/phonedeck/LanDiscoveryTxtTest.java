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

    @Test
    public void decodesPercentEncodedUtf8DisplayName() {
        assertEquals("往里走的COMPUTE",
                LanDiscoveryClient.decodeAttribute(
                        "%E5%BE%80%E9%87%8C%E8%B5%B0%E7%9A%84COMPUTE"
                                .getBytes(StandardCharsets.US_ASCII)));
    }

    @Test
    public void mdnsAddressMustShareThePhoneSubnet() {
        byte[] phone = {(byte) 192, (byte) 168, 1, 23};
        assertTrue(LanDiscoveryClient.sameIpv4Subnet(
                phone, new byte[]{(byte) 192, (byte) 168, 1, (byte) 200}, 24));
        // Hyper-V / WSL / VMware 等虚拟网卡地址不在手机网段，应被排除。
        assertFalse(LanDiscoveryClient.sameIpv4Subnet(
                phone, new byte[]{(byte) 172, 22, (byte) 144, 1}, 24));
        assertFalse(LanDiscoveryClient.sameIpv4Subnet(
                phone, new byte[]{(byte) 192, (byte) 168, 56, 1}, 24));
        // /16 网络按真实前缀判断，不能按 /24 误杀。
        assertTrue(LanDiscoveryClient.sameIpv4Subnet(
                new byte[]{10, 0, 3, 4}, new byte[]{10, 0, 5, 2}, 16));
        assertTrue(LanDiscoveryClient.sameIpv4Subnet(phone, phone, 32));
        assertTrue(LanDiscoveryClient.sameIpv4Subnet(
                phone, new byte[]{(byte) 172, 22, (byte) 144, 1}, 0));
        assertFalse(LanDiscoveryClient.sameIpv4Subnet(phone, new byte[16], 24));
    }
}
