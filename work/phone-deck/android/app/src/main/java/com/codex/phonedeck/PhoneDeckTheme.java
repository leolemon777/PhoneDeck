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

/// 八套外观（来自言渡 Android UI 设计稿）共用语义令牌；墨白沿用原 native_light / native_dark ID。
/// 槽位分四组：
/// - 底面：background / surface / surfaceRaised（凹陷与按压）/ key（键帽）/ voiceDock / outline；
/// - 内容：text / muted；
/// - 品牌：primary（交互填充）/ primaryPressed / onPrimary / accent（仅文字与焦点）/
///   primaryContainer / onPrimaryContainer（选中容器）；
/// - 状态：live / onLive（"正在采音"的墨色，与错误 danger 分离）及
///   success / warning / danger 的容器色。
/// 当前外观及验证记录见 docs/design/UI_REFRESH.md。
final class PhoneDeckTheme {
    static final String PREFS_NAME = "PhoneDeckSettings";
    static final String PREF_THEME_ID = "theme_id";
    static final String NATIVE_LIGHT = "native_light";
    static final String PAPER = "paper";
    static final String FOREST = "forest";
    static final String VIOLET = "violet";
    static final String CLAY = "clay";
    static final String MIST = "mist";
    static final String NATIVE_DARK = "native_dark";
    static final String NIGHT = "night";

    /// 上一代彩色主题 ID → 新配色中最接近的一套（2026-10 换成设计稿配色时删除旧四套）。
    private static final java.util.Map<String, String> RETIRED_IDS = new java.util.HashMap<>();
    static {
        RETIRED_IDS.put("forest_light", FOREST);
        RETIRED_IDS.put("violet_light", VIOLET);
        RETIRED_IDS.put("terracotta_light", CLAY);
        RETIRED_IDS.put("ocean_light", MIST);
    }

    /// 历史深色主题 ID：迁移时落深色，其余历史 ID 一律落浅色。
    private static final Set<String> LEGACY_DARK_IDS = new HashSet<>(Arrays.asList(
            "espresso", "cocoa", "mono", "ocean", "oled", "inkdark",
            "goldamber", "goldforest", "gptdark", "claudedark", "grokdark"));

    /// 墨白：bg, sf, sf2, line, text, muted, ink, onInk, ok, okBg, warn, warnBg, rec, recBg。
    private static final String[] PALETTE_LIGHT = {
            "#FFFFFF", "#F5F5F3", "#EBEAE6", "#E2E1DC", "#151515", "#63625C", "#151515",
            "#FFFFFF", "#1E7A4C", "#E5F2EA", "#8F5400", "#FAEFD9", "#BF3324", "#FBE8E4"};

    /// 暖纸：bg, sf, sf2, line, text, muted, ink, onInk, ok, okBg, warn, warnBg, rec, recBg。
    private static final String[] PALETTE_PAPER = {
            "#F7F3EA", "#EEE8DA", "#E4DCCB", "#E0D7C5", "#22201B", "#665F52", "#22201B",
            "#F7F3EA", "#3B7546", "#E3EDDD", "#8A5300", "#F5E8CF", "#B23526", "#F6E1DA"};

    /// 松绿：bg, sf, sf2, line, text, muted, ink, onInk, ok, okBg, warn, warnBg, rec, recBg。
    private static final String[] PALETTE_FOREST = {
            "#F5F8F6", "#E7EFEA", "#D9E5DD", "#D3E0D8", "#12251B", "#4C6155", "#1D5A3C",
            "#FFFFFF", "#1E7A4C", "#DDEFE4", "#8A5300", "#F6EBD4", "#B7352A", "#F8E4E0"};

    /// 淡紫：bg, sf, sf2, line, text, muted, ink, onInk, ok, okBg, warn, warnBg, rec, recBg。
    private static final String[] PALETTE_VIOLET = {
            "#FAF9FD", "#EFEDF8", "#E3E0F2", "#DEDAEF", "#1D1A2C", "#5B5573", "#4A3E8E",
            "#FFFFFF", "#1F7A55", "#E2F1EA", "#8A5300", "#F6EBD4", "#B8342A", "#F8E4E1"};

    /// 陶土：bg, sf, sf2, line, text, muted, ink, onInk, ok, okBg, warn, warnBg, rec, recBg。
    private static final String[] PALETTE_CLAY = {
            "#FAF6F1", "#F1E8DE", "#E7DACB", "#E3D5C5", "#2A1F18", "#6A5A4D", "#9C4D2A",
            "#FFFFFF", "#3B7546", "#E4EDDD", "#7D5A00", "#F3EACB", "#A3203A", "#F6DDE2"};

    /// 雾蓝：bg, sf, sf2, line, text, muted, ink, onInk, ok, okBg, warn, warnBg, rec, recBg。
    private static final String[] PALETTE_MIST = {
            "#F6F9FC", "#E8F0F7", "#DAE6F1", "#D3E0EC", "#132232", "#4C5F73", "#1E5C8B",
            "#FFFFFF", "#1E7A55", "#DDF0E7", "#8A5300", "#F6EBD4", "#B8342A", "#F8E4E1"};

    /// 墨白 · 深色：bg, sf, sf2, line, text, muted, ink, onInk, ok, okBg, warn, warnBg, rec, recBg。
    private static final String[] PALETTE_DARK = {
            "#141413", "#1F1F1D", "#2B2B28", "#34342F", "#F1F0EB", "#A6A59E", "#F1F0EB",
            "#141413", "#62C793", "#1B3226", "#E8AE52", "#3A2C14", "#F2806F", "#3E211C"};

    /// 极夜：bg, sf, sf2, line, text, muted, ink, onInk, ok, okBg, warn, warnBg, rec, recBg。
    private static final String[] PALETTE_NIGHT = {
            "#0B0B0B", "#171717", "#242424", "#2C2C2C", "#FFFFFF", "#A8A8A8", "#FFFFFF",
            "#0B0B0B", "#5FD39A", "#14301F", "#F0B44E", "#3A2B12", "#FF7A66", "#3F1C17"};

    private static final PhoneDeckTheme[] THEMES = {
            palette(NATIVE_LIGHT, "墨白", "纯白底，墨色话筒", true, PALETTE_LIGHT),
            palette(PAPER, "暖纸", "米色纸面，深墨文字", true, PALETTE_PAPER),
            palette(FOREST, "松绿", "浅绿底色，深松绿主色", true, PALETTE_FOREST),
            palette(VIOLET, "淡紫", "瓷白底色，沉静紫", true, PALETTE_VIOLET),
            palette(CLAY, "陶土", "燕麦暖底，陶土红棕", true, PALETTE_CLAY),
            palette(MIST, "雾蓝", "清浅蓝底，深雾蓝", true, PALETTE_MIST),
            palette(NATIVE_DARK, "墨白 · 深色", "暖黑底，米白话筒", false, PALETTE_DARK),
            palette(NIGHT, "极夜", "纯黑底，适合 OLED 夜间", false, PALETTE_NIGHT)};

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

    /** 返回稳定顺序的主题目录；调用方不会改动内部目录。 */
    static PhoneDeckTheme[] all() {
        return THEMES.clone();
    }

    static PhoneDeckTheme byId(String id) {
        for (PhoneDeckTheme candidate : THEMES) {
            if (candidate.id.equals(id)) {
                return candidate;
            }
        }
        return THEMES[0];
    }

    /**
     * 读取并一次性迁移历史存储（dev.10 品牌族两级存储、dev.8/9 及更早的单级 theme_id）。
     * 旧 ID 按明暗迁移；当前六套主题的选择优先于残留的历史品牌字段。
     */
    private static String ensureThemeId(Context context) {
        SharedPreferences preferences =
                context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        String stored = preferences.getString(PREF_THEME_ID, null);
        boolean legacyPairPending = preferences.contains("theme_brand")
                || preferences.contains("theme_mode");
        if (stored != null && stored.equals(validId(stored))) {
            if (legacyPairPending) {
                preferences.edit().remove("theme_brand").remove("theme_mode").apply();
            }
            return stored;
        }
        String migrated = NATIVE_LIGHT;
        if (stored != null && RETIRED_IDS.containsKey(stored)) {
            migrated = RETIRED_IDS.get(stored);
        } else if (legacyPairPending && "dark".equals(preferences.getString("theme_mode", null))) {
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
        for (PhoneDeckTheme candidate : THEMES) {
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
        activity.setTheme(nativeStyle());
        // 原生对话框、输入框和开关使用同一套 XML 配色；窗口提前着色避免白底闪烁。
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

    private int nativeStyle() {
        switch (id) {
            case PAPER:
                return R.style.AppThemePaper;
            case FOREST:
                return R.style.AppThemeForest;
            case VIOLET:
                return R.style.AppThemeViolet;
            case CLAY:
                return R.style.AppThemeClay;
            case MIST:
                return R.style.AppThemeMist;
            case NATIVE_DARK:
                return R.style.AppThemeDark;
            case NIGHT:
                return R.style.AppThemeNight;
            default:
                return R.style.AppTheme;
        }
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

    /// 设计稿令牌 → 语义槽：卡片与键帽用底色，凹陷/分段用 sf，选中容器用 sf2；
    /// 话筒、当前电脑卡与“正在采音”都用墨色 ink（采音状态靠光环与状态胶囊区分）。
    private static PhoneDeckTheme palette(String id, String name, String description, boolean light,
                                          String[] p) {
        int bg = Color.parseColor(p[0]);
        int ink = Color.parseColor(p[6]);
        int onInk = Color.parseColor(p[7]);
        return new PhoneDeckTheme(id, name, description, light,
                bg, bg, Color.parseColor(p[1]), bg, bg, Color.parseColor(p[3]),
                Color.parseColor(p[4]), Color.parseColor(p[5]),
                ink, blend(ink, bg, 0.18f), onInk, ink,
                Color.parseColor(p[2]), Color.parseColor(p[4]),
                ink, onInk,
                Color.parseColor(p[8]), Color.parseColor(p[9]),
                Color.parseColor(p[10]), Color.parseColor(p[11]),
                Color.parseColor(p[12]), Color.parseColor(p[13]));
    }
}
