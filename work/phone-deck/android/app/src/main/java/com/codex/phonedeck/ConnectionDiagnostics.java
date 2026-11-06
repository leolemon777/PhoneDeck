package com.codex.phonedeck;

import android.content.Context;
import android.net.wifi.WifiInfo;
import android.net.wifi.WifiManager;
import android.util.Log;

/**
 * 共享供音每条连接的断线日志（logcat 标签 PhoneDeckNet）。
 * 之前手机端几乎不记日志，电脑端出现“5000ms 未收到手机音频”时无法判断手机当时在做什么；
 * 这里记录断在哪个阶段、卡了多久、异常是什么，以及当时的 Wi-Fi 信号与速率。
 * 行内容不含令牌或证书指纹。
 */
final class ConnectionDiagnostics {
    static final String TAG = "PhoneDeckNet";

    private ConnectionDiagnostics() {}

    /** 当前连接尝试所处阶段，由各阶段的时间戳推出（Long.MIN_VALUE 表示尚未发生）。 */
    static String stage(long outputOpenedAt, long firstWriteAt, long finishingAt) {
        if (finishingAt != Long.MIN_VALUE) {
            return "收尾";
        }
        if (outputOpenedAt == Long.MIN_VALUE) {
            return "建立连接";
        }
        if (firstWriteAt == Long.MIN_VALUE) {
            return "等待首次写入";
        }
        return "持续写入";
    }

    /** 异常的一行描述：类名 + 消息，带一层原因；消息过长时截断。 */
    static String describe(Throwable failure) {
        if (failure == null) {
            return "未知";
        }
        StringBuilder text = new StringBuilder(failure.getClass().getSimpleName());
        if (failure.getMessage() != null && !failure.getMessage().isEmpty()) {
            text.append(": ").append(failure.getMessage());
        }
        Throwable cause = failure.getCause();
        if (cause != null && cause != failure) {
            text.append(" ← ").append(cause.getClass().getSimpleName());
            if (cause.getMessage() != null && !cause.getMessage().isEmpty()) {
                text.append(": ").append(cause.getMessage());
            }
        }
        return text.length() > 160 ? text.substring(0, 160) + "…" : text.toString();
    }

    /** 一行日志：目标、事件、阶段、已持续与无进展时长、详情、Wi-Fi 概况。 */
    static String line(String target, String event, String stage,
                       long aliveMs, long idleMs, String detail, String wifi) {
        StringBuilder text = new StringBuilder("共享供音 ").append(target).append(' ').append(event);
        if (stage != null) {
            text.append(" · 阶段=").append(stage);
        }
        if (aliveMs >= 0) {
            text.append(" · 已连 ").append(aliveMs).append("ms");
        }
        if (idleMs >= 0) {
            text.append(" · 无进展 ").append(idleMs).append("ms");
        }
        if (detail != null && !detail.isEmpty()) {
            text.append(" · ").append(detail);
        }
        if (wifi != null && !wifi.isEmpty()) {
            text.append(" · ").append(wifi);
        }
        return text.toString();
    }

    /** 目标的简短标识：名称加 computerId 前 8 位。 */
    static String targetLabel(String displayName, String computerId) {
        String id = computerId == null ? "" : computerId.substring(0, Math.min(8, computerId.length()));
        return (displayName == null || displayName.isEmpty() ? "电脑" : displayName) + "(" + id + ")";
    }

    @SuppressWarnings("deprecation")
    static String wifiSummary(Context context) {
        try {
            WifiManager manager = context.getApplicationContext().getSystemService(WifiManager.class);
            WifiInfo info = manager == null ? null : manager.getConnectionInfo();
            if (info == null || info.getNetworkId() == -1) {
                return "Wi-Fi 未连接";
            }
            int frequency = info.getFrequency();
            return "Wi-Fi " + info.getRssi() + "dBm " + info.getLinkSpeed() + "Mbps "
                    + (frequency >= 5925 ? "6GHz" : frequency >= 4900 ? "5GHz" : "2.4GHz");
        } catch (RuntimeException ignored) {
            return null;
        }
    }

    static void log(Context context, String target, String event, String stage,
                    long aliveMs, long idleMs, Throwable failure) {
        Log.i(TAG, line(target, event, stage, aliveMs, idleMs,
                failure == null ? null : describe(failure), wifiSummary(context)));
    }
}
