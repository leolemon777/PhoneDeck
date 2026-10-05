package com.codex.phonedeck;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/**
 * 共享组的一行状态汇总，例如“正在向 2/3 台电脑供音 · 2号连接中 · 3号离线”。
 * 供音计数只统计真正建立了音频流的电脑；其余电脑按编号列出各自原因，
 * 让用户知道是哪一台、为什么没有收到声音（规格 0.8 状态轴）。
 */
final class MultiComputerStatus {
    static final String SUPPLYING = "正在供音";

    private MultiComputerStatus() {
    }

    /// states：computerId → 状态文案；slots：computerId → 手机端编号。
    static String summarize(Map<String, String> states, Map<String, Integer> slots) {
        if (states.isEmpty()) {
            return "共享组还没有电脑，请在电脑列表中勾选";
        }
        int supplying = 0;
        List<Map.Entry<String, String>> others = new ArrayList<>();
        for (Map.Entry<String, String> entry : states.entrySet()) {
            if (SUPPLYING.equals(entry.getValue())) {
                supplying++;
            } else {
                others.add(entry);
            }
        }
        others.sort((left, right) -> Integer.compare(slotOf(slots, left.getKey()),
                slotOf(slots, right.getKey())));
        StringBuilder summary = new StringBuilder(supplying > 0
                ? "正在向 " + supplying + "/" + states.size() + " 台电脑供音"
                : "0/" + states.size() + " 台电脑在接收");
        for (Map.Entry<String, String> entry : others) {
            int slot = slotOf(slots, entry.getKey());
            summary.append(" · ")
                    .append(slot == Integer.MAX_VALUE ? "?" : String.valueOf(slot))
                    .append("号")
                    .append(entry.getValue());
        }
        return summary.toString();
    }

    private static int slotOf(Map<String, Integer> slots, String computerId) {
        Integer slot = slots.get(computerId);
        return slot == null ? Integer.MAX_VALUE : slot;
    }
}
