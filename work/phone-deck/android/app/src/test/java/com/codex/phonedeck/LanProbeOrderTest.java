package com.codex.phonedeck;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.util.Arrays;
import java.util.Collections;

import org.junit.Test;

/** 探测先只试上次可用地址，避免每 2 秒向全部历史地址发连接（失效地址会触发 ARP 广播）。 */
public final class LanProbeOrderTest {
    @Test
    public void probesLastGoodAloneWhenItLeadsSeveralCandidates() {
        assertTrue(PhoneDeckLanClient.shouldProbeLastGoodFirst("192.168.1.20",
                Arrays.asList("192.168.1.20", "192.168.1.31", "172.22.144.1")));
    }

    @Test
    public void probesInParallelWithoutUsableLastGood() {
        assertFalse(PhoneDeckLanClient.shouldProbeLastGoodFirst(null,
                Arrays.asList("192.168.1.20", "192.168.1.31")));
        // last-good 已不在候选里时 orderedCandidates 不会把它排第一。
        assertFalse(PhoneDeckLanClient.shouldProbeLastGoodFirst("192.168.1.99",
                Arrays.asList("192.168.1.20", "192.168.1.31")));
        // 只有一个候选时没有可省的探测。
        assertFalse(PhoneDeckLanClient.shouldProbeLastGoodFirst("192.168.1.20",
                Collections.singletonList("192.168.1.20")));
        assertFalse(PhoneDeckLanClient.shouldProbeLastGoodFirst("192.168.1.20", null));
    }
}
