package com.codex.phonedeck;

import android.app.Activity;
import android.content.Context;
import android.content.SharedPreferences;
import android.content.res.Configuration;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.StateListDrawable;
import android.os.Build;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;

/**
 * 软色纸卡主题（soft neo)：暖奶油/暖黑底色、纯色大圆角卡片、柔和投影、
 * 单一蓝色主操作，深浅两套并跟随系统昼夜。历史上旧的玻璃/多主题皮肤
 * 已移除；isFrost()/isMonochrome() 仅作为兼容入口保留，恒为 false。
 */
final class PhoneDeckTheme {
    static final String PREFS_NAME = "PhoneDeckSettings";
    static final String PREF_THEME_ID = "theme_id";
    static final String SOFT = "soft";

    final String id;
    final String name;
    final String description;
    final boolean light;
    final int background;
    final int surface;
    final int surfaceRaised;
    final int key;
    final int primary;
    final int primaryPressed;
    final int onPrimary;
    final int text;
    final int muted;
    final int success;
    final int warning;
    final int danger;
    final int outline;
    final int voiceDock;

    private PhoneDeckTheme(
            String id,
            String name,
            String description,
            boolean light,
            int background,
            int surface,
            int surfaceRaised,
            int key,
            int primary,
            int primaryPressed,
            int onPrimary,
            int text,
            int muted,
            int success,
            int warning,
            int danger,
            int outline,
            int voiceDock) {
        this.id = id;
        this.name = name;
        this.description = description;
        this.light = light;
        this.background = background;
        this.surface = surface;
        this.surfaceRaised = surfaceRaised;
        this.key = key;
        this.primary = primary;
        this.primaryPressed = primaryPressed;
        this.onPrimary = onPrimary;
        this.text = text;
        this.muted = muted;
        this.success = success;
        this.warning = warning;
        this.danger = danger;
        this.outline = outline;
        this.voiceDock = voiceDock;
    }

    static PhoneDeckTheme load(Context context) {
        int nightMode = context.getResources().getConfiguration().uiMode
                & Configuration.UI_MODE_NIGHT_MASK;
        PhoneDeckTheme theme = nightMode == Configuration.UI_MODE_NIGHT_YES
                ? espresso()
                : ivory();
        SharedPreferences preferences = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        if (!SOFT.equals(preferences.getString(PREF_THEME_ID, null))) {
            preferences.edit().putString(PREF_THEME_ID, SOFT).apply();
        }
        return theme;
    }

    static void save(Context context, String themeId) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
                .edit()
                .putString(PREF_THEME_ID, SOFT)
                .apply();
    }

    static PhoneDeckTheme[] all() {
        return new PhoneDeckTheme[]{ivory(), espresso()};
    }

    static PhoneDeckTheme byId(String id) {
        // 旧版本可能仍保存 frost/ocean/oled/paper/ink/gold 等主题 ID，
        // 统一回退到软色纸卡；深浅由调用方 load() 按系统决定。
        return ivory();
    }

    /** 历史玻璃皮肤的兼容判断；软色纸卡下恒为 false，相关玻璃分支不再生效。 */
    boolean isFrost() {
        return false;
    }

    boolean isMonochrome() {
        return false;
    }

    int contentBackground() {
        return background;
    }

    View wrapContent(Context context, View content) {
        return content;
    }

    void applyWindow(Activity activity) {
        activity.getWindow().setStatusBarColor(background);
        activity.getWindow().setNavigationBarColor(background);
        int flags = 0;
        if (light && Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            flags |= View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR;
        }
        if (light && Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            flags |= View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;
        }
        activity.getWindow().getDecorView().setSystemUiVisibility(flags);
    }

    /** 快捷键卡片：纸卡不再整体着色，预设色只用于图标/角标等点缀。 */
    int shortcutColor(String color) {
        return surface;
    }

    int shortcutAccent(String color) {
        if (light) {
            switch (color) {
                case "purple":
                    return Color.rgb(122, 90, 240);
                case "green":
                    return Color.rgb(62, 155, 95);
                case "orange":
                    return Color.rgb(224, 138, 69);
                case "red":
                    return Color.rgb(217, 84, 72);
                case "slate":
                    return Color.rgb(107, 103, 95);
                default:
                    return Color.rgb(61, 107, 243);
            }
        }
        switch (color) {
            case "purple":
                return Color.rgb(154, 128, 246);
            case "green":
                return Color.rgb(98, 183, 123);
            case "orange":
                return Color.rgb(233, 162, 102);
            case "red":
                return Color.rgb(232, 122, 110);
            case "slate":
                return Color.rgb(160, 155, 145);
            default:
                return Color.rgb(126, 154, 255);
        }
    }

    int shortcutPressedColor(String color) {
        return mix(surface, key, light ? 0.55f : 0.8f);
    }

    int feedbackSurface(int semanticColor) {
        return mix(surface, semanticColor, light ? 0.08f : 0.16f);
    }

    /// 混色入口：与 blend 一致；保留方法便于各页面统一调用。
    int mix(int base, int overlay, float amount) {
        return blend(base, overlay, amount);
    }

    GradientDrawable shape(Context context, int fill, int radiusDp) {
        return shape(context, fill, radiusDp, 0, Color.TRANSPARENT);
    }

    GradientDrawable shape(
            Context context, int fill, int radiusDp, int strokeWidthDp, int strokeColor) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(fill);
        drawable.setCornerRadius(dp(context, radiusDp));
        if (strokeWidthDp > 0) {
            drawable.setStroke(dp(context, strokeWidthDp), strokeColor);
        }
        return drawable;
    }

    StateListDrawable pressable(
            Context context, int normalColor, int pressedColor, int radiusDp) {
        StateListDrawable states = new StateListDrawable();
        states.addState(new int[]{android.R.attr.state_pressed},
                shape(context, pressedColor, radiusDp));
        states.addState(new int[]{}, shape(context, normalColor, radiusDp));
        return states;
    }

    static int blend(int base, int overlay, float amount) {
        float keep = 1f - Math.max(0f, Math.min(1f, amount));
        return Color.argb(
                Math.round(Color.alpha(base) * keep + Color.alpha(overlay) * amount),
                Math.round(Color.red(base) * keep + Color.red(overlay) * amount),
                Math.round(Color.green(base) * keep + Color.green(overlay) * amount),
                Math.round(Color.blue(base) * keep + Color.blue(overlay) * amount));
    }

    static int dp(Context context, int value) {
        return Math.round(value * context.getResources().getDisplayMetrics().density);
    }

    /** 象牙浅色：暖奶油底、纯白卡片、蓝色主操作、橙绿点缀。 */
    private static PhoneDeckTheme ivory() {
        return new PhoneDeckTheme(
                SOFT,
                "软色纸卡",
                "奶油底 · 白卡片 · 大圆角 · 柔投影，深浅跟随系统",
                true,
                Color.rgb(242, 240, 234),
                Color.rgb(255, 255, 255),
                Color.rgb(247, 245, 240),
                Color.rgb(235, 232, 224),
                Color.rgb(61, 107, 243),
                Color.rgb(46, 86, 208),
                Color.rgb(255, 255, 255),
                Color.rgb(27, 25, 20),
                Color.rgb(143, 139, 128),
                Color.rgb(62, 155, 95),
                Color.rgb(224, 138, 69),
                Color.rgb(217, 84, 72),
                Color.rgb(233, 230, 222),
                Color.rgb(255, 255, 255));
    }

    /** 浓缩咖啡深色：暖黑底、深咖卡片、白色主操作（参考图深色 FAB 处理）。 */
    private static PhoneDeckTheme espresso() {
        return new PhoneDeckTheme(
                SOFT,
                "软色纸卡",
                "暖黑底 · 深咖卡片 · 白色主操作，深浅跟随系统",
                false,
                Color.rgb(22, 20, 17),
                Color.rgb(38, 35, 31),
                Color.rgb(45, 42, 37),
                Color.rgb(29, 27, 24),
                Color.rgb(241, 238, 230),
                Color.rgb(214, 210, 200),
                Color.rgb(25, 23, 19),
                Color.rgb(241, 238, 230),
                Color.rgb(151, 146, 135),
                Color.rgb(98, 183, 123),
                Color.rgb(233, 162, 102),
                Color.rgb(232, 122, 110),
                Color.rgb(51, 47, 42),
                Color.rgb(38, 35, 31));
    }
}
