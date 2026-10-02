package com.codex.phonedeck;

import android.content.Context;

/** Phone-local layout preference, independent of theme, computer, and voice mode. */
enum HomeStyle {
    CENTER("center", "极简居中", "大话筒居中，状态和操作分区"),
    DOCK("dock", "单手底座", "话筒靠近底部，单手更顺手"),
    PANEL("panel", "紧凑面板", "话筒和三个按键并排，操作更集中");

    final String id;
    final String title;
    final String detail;

    HomeStyle(String id, String title, String detail) {
        this.id = id;
        this.title = title;
        this.detail = detail;
    }

    static HomeStyle load(Context context) {
        String id = context.getSharedPreferences("PhoneDeckSettings", Context.MODE_PRIVATE)
                .getString("home_style_id", CENTER.id);
        for (HomeStyle style : values()) if (style.id.equals(id)) return style;
        return CENTER;
    }

    void save(Context context) {
        context.getSharedPreferences("PhoneDeckSettings", Context.MODE_PRIVATE).edit()
                .putString("home_style_id", id).apply();
    }
}
