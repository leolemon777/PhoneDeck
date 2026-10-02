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

/// 六套外观共用语义令牌，原墨水屏主题 ID 保持兼容。
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
    static final String FOREST_LIGHT = "forest_light";
    static final String VIOLET_LIGHT = "violet_light";
    static final String TERRACOTTA_LIGHT = "terracotta_light";
    static final String OCEAN_LIGHT = "ocean_light";

    /// 历史深色主题 ID：迁移时落深色，其余历史 ID 一律落浅色。
    private static final Set<String> LEGACY_DARK_IDS = new HashSet<>(Arrays.asList(
            "espresso", "cocoa", "mono", "ocean", "oled", "inkdark",
            "goldamber", "goldforest", "gptdark", "claudedark", "grokdark"));

    private static final PhoneDeckTheme[] THEMES = {
            nativeTheme(true), nativeTheme(false), forestTheme(), violetTheme(),
            terracottaTheme(), oceanTheme()};

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

    // A single high-contrast device panel anchors the mobile workspace in every theme.
    int workspacePanel() {
        return light ? text : surfaceRaised;
    }

    int workspaceInk() {
        return light ? surface : text;
    }

    int workspaceMuted() {
        return mix(workspaceInk(), workspacePanel(), 0.27f);
    }

    int workspaceRaised() {
        return mix(workspacePanel(), workspaceInk(), 0.10f);
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
            case NATIVE_DARK:
                return R.style.AppThemeDark;
            case FOREST_LIGHT:
                return R.style.AppThemeForest;
            case VIOLET_LIGHT:
                return R.style.AppThemeViolet;
            case TERRACOTTA_LIGHT:
                return R.style.AppThemeTerracotta;
            case OCEAN_LIGHT:
                return R.style.AppThemeOcean;
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

    private static PhoneDeckTheme forestTheme() {
        return new PhoneDeckTheme(
                FOREST_LIGHT, "森林 · 松绿", "浅绿纸面，沉静松绿", true,
                Color.parseColor("#F2F5EE"), Color.parseColor("#FBFCF8"),
                Color.parseColor("#E4ECDC"), Color.parseColor("#FBFCF8"),
                Color.parseColor("#FBFCF8"), Color.parseColor("#C9D4C3"),
                Color.parseColor("#26392D"), Color.parseColor("#586959"),
                Color.parseColor("#2F6346"), Color.parseColor("#244E38"),
                Color.parseColor("#FFFDF8"), Color.parseColor("#2F6346"),
                Color.parseColor("#DEEBDF"), Color.parseColor("#244A32"),
                Color.parseColor("#2F6346"), Color.parseColor("#FFFDF8"),
                Color.parseColor("#2F6346"), Color.parseColor("#E0ECDD"),
                Color.parseColor("#705521"), Color.parseColor("#F1E7D0"),
                Color.parseColor("#963F3A"), Color.parseColor("#F7E5DE"));
    }

    private static PhoneDeckTheme violetTheme() {
        return new PhoneDeckTheme(
                VIOLET_LIGHT, "瓷白 · 淡紫", "柔白底色，低饱和紫", true,
                Color.parseColor("#F4F1FA"), Color.parseColor("#FDFBFF"),
                Color.parseColor("#E9E2F1"), Color.parseColor("#FDFBFF"),
                Color.parseColor("#FDFBFF"), Color.parseColor("#D5CCE1"),
                Color.parseColor("#302A3D"), Color.parseColor("#6C617A"),
                Color.parseColor("#66508C"), Color.parseColor("#4C3C6C"),
                Color.parseColor("#FFFCFF"), Color.parseColor("#66508C"),
                Color.parseColor("#E7DEF3"), Color.parseColor("#46355F"),
                Color.parseColor("#66508C"), Color.parseColor("#FFFCFF"),
                Color.parseColor("#3E6450"), Color.parseColor("#E2EDE6"),
                Color.parseColor("#765723"), Color.parseColor("#F4EAD7"),
                Color.parseColor("#943C50"), Color.parseColor("#F5E3E9"));
    }

    private static PhoneDeckTheme terracottaTheme() {
        return new PhoneDeckTheme(
                TERRACOTTA_LIGHT, "燕麦 · 陶土", "燕麦暖底，陶土红棕", true,
                Color.parseColor("#F6F0E8"), Color.parseColor("#FFFAF4"),
                Color.parseColor("#EEE2D5"), Color.parseColor("#FFFAF4"),
                Color.parseColor("#FFFAF4"), Color.parseColor("#DBCBBC"),
                Color.parseColor("#3D3028"), Color.parseColor("#706151"),
                Color.parseColor("#915038"), Color.parseColor("#733F2D"),
                Color.parseColor("#FFFBF6"), Color.parseColor("#915038"),
                Color.parseColor("#F0DDD0"), Color.parseColor("#713D2A"),
                Color.parseColor("#915038"), Color.parseColor("#FFFBF6"),
                Color.parseColor("#426247"), Color.parseColor("#E6ECDC"),
                Color.parseColor("#785620"), Color.parseColor("#F2E6CC"),
                Color.parseColor("#963C3C"), Color.parseColor("#F4E0DB"));
    }

    private static PhoneDeckTheme oceanTheme() {
        return new PhoneDeckTheme(
                OCEAN_LIGHT, "天空 · 雾蓝", "清浅蓝底，柔和雾蓝", true,
                Color.parseColor("#EFF4F8"), Color.parseColor("#F9FCFE"),
                Color.parseColor("#DFE9F1"), Color.parseColor("#F9FCFE"),
                Color.parseColor("#F9FCFE"), Color.parseColor("#C6D4DF"),
                Color.parseColor("#243746"), Color.parseColor("#586875"),
                Color.parseColor("#365F7E"), Color.parseColor("#294A63"),
                Color.parseColor("#FAFDFF"), Color.parseColor("#365F7E"),
                Color.parseColor("#DEEAF3"), Color.parseColor("#284A63"),
                Color.parseColor("#365F7E"), Color.parseColor("#FAFDFF"),
                Color.parseColor("#37654F"), Color.parseColor("#DFEDE5"),
                Color.parseColor("#775820"), Color.parseColor("#F3EBD6"),
                Color.parseColor("#963F49"), Color.parseColor("#F6E4E6"));
    }
}
