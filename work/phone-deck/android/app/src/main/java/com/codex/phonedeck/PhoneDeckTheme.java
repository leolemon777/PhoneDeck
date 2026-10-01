package com.codex.phonedeck;

import android.app.Activity;
import android.content.Context;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.view.View;

import java.util.Arrays;
import java.util.HashSet;
import java.util.Set;

/// 墨水屏外观的语义令牌：暖纸浅色与反转深色，沿用原主题 ID。
/// 槽位分四组：
/// - 底面：background / surface / surfaceRaised（凹陷与按压）/ key（键帽）/ voiceDock / outline；
/// - 内容：text / muted；
/// - 品牌：primary（交互填充）/ primaryPressed / onPrimary / accent（仅文字与焦点）/
///   primaryContainer / onPrimaryContainer（选中容器）；
/// - 状态：live / onLive（"正在采音"的墨色，与错误 danger 分离）及
///   success / warning / danger 的容器色。
/// 当前外观及验证记录见 docs/UI_REFRESH.md。
final class PhoneDeckTheme {
    static final String PREFS_NAME = "PhoneDeckSettings";
    static final String PREF_THEME_ID = "theme_id";
    static final String NATIVE_LIGHT = "native_light";
    static final String NATIVE_DARK = "native_dark";

    /// 历史深色主题 ID：迁移时落深色，其余历史 ID 一律落浅色。
    private static final Set<String> LEGACY_DARK_IDS = new HashSet<>(Arrays.asList(
            "espresso", "cocoa", "mono", "ocean", "oled", "inkdark",
            "goldamber", "goldforest", "gptdark", "claudedark", "grokdark"));

    final String id;
    final String name;
    final String description;
    final boolean light;
    final int background;
    final int surface;
    final int surfaceRaised;
    final int key;
    final int voiceDock;
    final int outline;
    final int text;
    final int muted;
    final int primary;
    final int primaryPressed;
    final int onPrimary;
    final int accent;
    final int primaryContainer;
    final int onPrimaryContainer;
    final int live;
    final int onLive;
    final int success;
    final int successContainer;
    final int warning;
    final int warningContainer;
    final int danger;
    final int dangerContainer;

    private PhoneDeckTheme(
            String id,
            String name,
            String description,
            boolean light,
            int background,
            int surface,
            int surfaceRaised,
            int key,
            int voiceDock,
            int outline,
            int text,
            int muted,
            int primary,
            int primaryPressed,
            int onPrimary,
            int accent,
            int primaryContainer,
            int onPrimaryContainer,
            int live,
            int onLive,
            int success,
            int successContainer,
            int warning,
            int warningContainer,
            int danger,
            int dangerContainer) {
        this.id = id;
        this.name = name;
        this.description = description;
        this.light = light;
        this.background = background;
        this.surface = surface;
        this.surfaceRaised = surfaceRaised;
        this.key = key;
        this.voiceDock = voiceDock;
        this.outline = outline;
        this.text = text;
        this.muted = muted;
        this.primary = primary;
        this.primaryPressed = primaryPressed;
        this.onPrimary = onPrimary;
        this.accent = accent;
        this.primaryContainer = primaryContainer;
        this.onPrimaryContainer = onPrimaryContainer;
        this.live = live;
        this.onLive = onLive;
        this.success = success;
        this.successContainer = successContainer;
        this.warning = warning;
        this.warningContainer = warningContainer;
        this.danger = danger;
        this.dangerContainer = dangerContainer;
    }

    static PhoneDeckTheme load(Context context) {
        return byId(ensureThemeId(context));
    }

    static void save(Context context, String themeId) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
                .edit()
                .putString(PREF_THEME_ID, validId(themeId))
                // 顺手清掉 dev.10 的品牌族残留，避免迁移分支反复触发。
                .remove("theme_brand")
                .remove("theme_mode")
                .apply();
    }

    /** 用户选择仅保留简洁浅色与深色。 */
    static PhoneDeckTheme[] all() {
        return new PhoneDeckTheme[]{
                nativeTheme(true), nativeTheme(false)};
    }

    static PhoneDeckTheme byId(String id) {
        return nativeTheme(!NATIVE_DARK.equals(validId(id)));
    }

    /**
     * 读取并一次性迁移历史存储（dev.10 品牌族两级存储、dev.8/9 及更早的单级 theme_id）。
     * 历史主题已全部下线：暖深底族迁移到简洁深色，其余迁移到简洁浅色。
     */
    private static String ensureThemeId(Context context) {
        SharedPreferences preferences =
                context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        String stored = preferences.getString(PREF_THEME_ID, null);
        boolean legacyPairPending = preferences.contains("theme_brand")
                || preferences.contains("theme_mode");
        if (stored != null && stored.equals(validId(stored)) && !legacyPairPending) {
            return stored;
        }
        String migrated = NATIVE_LIGHT;
        if (legacyPairPending && "dark".equals(preferences.getString("theme_mode", null))) {
            migrated = NATIVE_DARK;
        } else if (stored != null && LEGACY_DARK_IDS.contains(stored)) {
            migrated = NATIVE_DARK;
        }
        preferences.edit()
                .putString(PREF_THEME_ID, migrated)
                .remove("theme_brand")
                .remove("theme_mode")
                .apply();
        return migrated;
    }

    private static String validId(String id) {
        for (PhoneDeckTheme candidate : all()) {
            if (candidate.id.equals(id)) {
                return id;
            }
        }
        return NATIVE_LIGHT;
    }

    int contentBackground() {
        return background;
    }

    View wrapContent(Context context, View content) {
        return content;
    }

    void applyWindow(Activity activity) {
        activity.setTheme(light ? R.style.AppTheme : R.style.AppThemeDark);
        // XML 主题固定是 Material.Light；深色主题下提前把窗口背景刷成主题底色，
        // 避免 onCreate 到 setContentView 之间闪一下白底。
        activity.getWindow().setBackgroundDrawable(
                new android.graphics.drawable.ColorDrawable(background));
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

    /// 快捷卡底：统一键帽底色，分类语义只进编辑器，不打扰主界面。
    int shortcutColor(String color) {
        return key;
    }

    /// 分类色板：供快捷键编辑/选择器展示分类语义；主界面不使用。
    int shortcutAccent(String color) {
        switch (color) {
            case "purple":
                return light ? Color.rgb(116, 75, 216) : Color.rgb(154, 128, 246);
            case "green":
                return light ? Color.rgb(25, 158, 112) : Color.rgb(98, 183, 123);
            case "orange":
                return light ? Color.rgb(224, 104, 35) : Color.rgb(233, 162, 102);
            case "red":
                return light ? Color.rgb(209, 66, 105) : Color.rgb(232, 122, 110);
            case "slate":
                return muted;
            default:
                return primary;
        }
    }

    int shortcutPressedColor(String color) {
        return mix(key, primary, light ? 0.13f : 0.25f);
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

    /// 原生涟漪反馈：内容仍为纯色 shape，按下时半透明 pressed 色扩散，
    /// 比整块变色更接近系统应用的手感（minSdk 26，RippleDrawable 可用）。
    android.graphics.drawable.Drawable pressable(
            Context context, int normalColor, int pressedColor, int radiusDp) {
        return pressable(context, normalColor, pressedColor, radiusDp, 0, Color.TRANSPARENT);
    }

    android.graphics.drawable.Drawable pressable(
            Context context, int normalColor, int pressedColor, int radiusDp,
            int strokeWidthDp, int strokeColor) {
        GradientDrawable content = shape(context, normalColor, radiusDp, strokeWidthDp, strokeColor);
        return new android.graphics.drawable.RippleDrawable(
                android.content.res.ColorStateList.valueOf(withAlpha(pressedColor, 170)),
                content,
                null);
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

    private static int withAlpha(int color, int alpha) {
        return Color.argb(alpha, Color.red(color), Color.green(color), Color.blue(color));
    }

    private static PhoneDeckTheme nativeTheme(boolean light) {
        return new PhoneDeckTheme(
                light ? NATIVE_LIGHT : NATIVE_DARK,
                light ? "墨水屏 · 暖白" : "墨水屏 · 深色",
                light ? "暖白纸面，墨黑线条" : "柔和墨底，暖白文字",
                light,
                Color.parseColor(light ? "#F5F3EE" : "#1F1E1B"),
                Color.parseColor(light ? "#FAF9F6" : "#292824"),
                Color.parseColor(light ? "#E9E6DF" : "#34322D"),
                Color.parseColor(light ? "#FAF9F6" : "#292824"),
                Color.parseColor(light ? "#FAF9F6" : "#292824"),
                Color.parseColor(light ? "#D1CCC2" : "#4C4941"),
                Color.parseColor(light ? "#242320" : "#F4F1EA"),
                Color.parseColor(light ? "#69665F" : "#B6B1A6"),
                Color.parseColor(light ? "#242320" : "#F1EDE4"),
                Color.parseColor(light ? "#42403A" : "#D3CEC3"),
                Color.parseColor(light ? "#F9F7F2" : "#242320"),
                Color.parseColor(light ? "#242320" : "#F1EDE4"),
                Color.parseColor(light ? "#E7E3DA" : "#38362F"),
                Color.parseColor(light ? "#242320" : "#F2EEE7"),
                Color.parseColor(light ? "#242320" : "#F1EDE4"),
                Color.parseColor(light ? "#F9F7F2" : "#242320"),
                Color.parseColor(light ? "#474640" : "#C5C3B9"),
                Color.parseColor(light ? "#EAE8E1" : "#35342F"),
                Color.parseColor(light ? "#625E56" : "#D3C9B6"),
                Color.parseColor(light ? "#EDE9E1" : "#36332C"),
                Color.parseColor(light ? "#8E3535" : "#E7A49E"),
                Color.parseColor(light ? "#F0E5E0" : "#3B2C28"));
    }
}
