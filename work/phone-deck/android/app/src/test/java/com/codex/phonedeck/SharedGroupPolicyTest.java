package com.codex.phonedeck;

import org.junit.Test;

import static org.junit.Assert.*;

/**
 * M1-B 第二片：共享组零目标停采窗口（V21 断言项，MIC-10 提案 15s）。
 */
public final class SharedGroupPolicyTest {
    @Test
    public void zeroTargetStopWindowIsFifteenSeconds() {
        long since = 100_000;
        assertFalse("计时未启动（组内有目标）不停采",
                SharedAudioPolicies.shouldStopForZeroTargets(0, since + 60_000));
        assertFalse("窗口内继续等待网络恢复（RC：慢端/断网只影响自身）",
                SharedAudioPolicies.shouldStopForZeroTargets(since, since + 14_999));
        assertTrue("满 15s 停采",
                SharedAudioPolicies.shouldStopForZeroTargets(since, since + 15_000));
        assertTrue("超窗必然停采",
                SharedAudioPolicies.shouldStopForZeroTargets(since, since + 120_000));
    }
}
