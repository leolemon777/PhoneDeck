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

    /// 历史彩色及深色主题 ID：统一精简迁移至苹果生态浅色/深色。
    private static final java.util.Map<String, String> RETIRED_IDS = new java.util.HashMap<>();
    static {
        RETIRED_IDS.put("forest_light", NATIVE_LIGHT);
        RETIRED_IDS.put("violet_light", NATIVE_LIGHT);
        RETIRED_IDS.put("terracotta_light", NATIVE_LIGHT);
        RETIRED_IDS.put("ocean_light", NATIVE_LIGHT);
        RETIRED_IDS.put(FOREST, NATIVE_LIGHT);
        RETIRED_IDS.put(VIOLET, NATIVE_LIGHT);
        RETIRED_IDS.put(CLAY, NATIVE_LIGHT);
        RETIRED_IDS.put(MIST, NATIVE_LIGHT);
    }

    /// 历史深色主题 ID：迁移时落深色，其余历史 ID 一律落浅色。
    private static final Set<String> LEGACY_DARK_IDS = new HashSet<>(Arrays.asList(
            "espresso", "cocoa", "mono", "ocean", "oled", "inkdark",
            "goldamber", "goldforest", "gptdark", "claudedark", "grokdark"));

    /// 苹果浅色（系统灰底 + 纯白卡片）：bg, sf, sf2, line, text, body, muted, primary, onPrimary, pContainer, onPContainer, ok, okBg, warn, warnBg, rec, recBg。
    private static final String[] PALETTE_IOS_LIGHT = {
            "#F2F2F7", "#FFFFFF", "#E5E5EA", "#E5E5EA",
            "#000000", "#3A3A3C", "#8E8E93",
            "#000000", "#FFFFFF", "#E5E5EA", "#000000",
            "#34C759", "#EBF9EE", "#FF9500", "#FFF5E5", "#FF3B30", "#FFECEB"};

    /// 苹果纯白（纯白底 + 系统灰卡片）：bg, sf, sf2, line, text, body, muted, primary, onPrimary, pContainer, onPContainer, ok, okBg, warn, warnBg, rec, recBg。
    private static final String[] PALETTE_IOS_WHITE = {
            "#FFFFFF", "#F2F2F7", "#E5E5EA", "#E5E5EA",
            "#000000", "#3A3A3C", "#8E8E93",
            "#000000", "#FFFFFF", "#F2F2F7", "#000000",
            "#34C759", "#EBF9EE", "#FF9500", "#FFF5E5", "#FF3B30", "#FFECEB"};

    /// 苹果深色（纯黑底 + 深灰卡片）：bg, sf, sf2, line, text, body, muted, primary, onPrimary, pContainer, onPContainer, ok, okBg, warn, warnBg, rec, recBg。
    private static final String[] PALETTE_IOS_DARK = {
            "#000000", "#1C1C1E", "#2C2C2E", "#38383A",
            "#FFFFFF", "#EBEBF5", "#8E8E93",
            "#FFFFFF", "#000000", "#2C2C2E", "#FFFFFF",
            "#30D158", "#14301F", "#FF9F0A", "#3A2B12", "#FF453A", "#3F1C17"};

    /// 苹果深空灰（石墨暗底 + 柔和层次）：bg, sf, sf2, line, text, body, muted, primary, onPrimary, pContainer, onPContainer, ok, okBg, warn, warnBg, rec, recBg。
    private static final String[] PALETTE_IOS_SPACE = {
            "#1C1C1E", "#2C2C2E", "#3A3A3C", "#48484A",
            "#FFFFFF", "#EBEBF5", "#8E8E93",
            "#FFFFFF", "#000000", "#3A3A3C", "#FFFFFF",
            "#30D158", "#163522", "#FF9F0A", "#3D2E14", "#FF453A", "#421E19"};

    private static final PhoneDeckTheme[] THEMES = {
            palette(NATIVE_LIGHT, "苹果浅色 · 通透灰", "系统灰底 (#F2F2F7) 配纯白卡片，层次分明", true, PALETTE_IOS_LIGHT),
            palette(PAPER, "苹果浅色 · 纯白", "纯白底 (#FFFFFF) 配浅灰卡片，极致纯净", true, PALETTE_IOS_WHITE),
            palette(NATIVE_DARK, "苹果深色 · 纯黑", "OLED 纯黑底 (#000000) 配深灰卡片", false, PALETTE_IOS_DARK),
            palette(NIGHT, "苹果深色 · 深空灰", "深空灰底 (#1C1C1E)，柔和夜间暗色", false, PALETTE_IOS_SPACE)};

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
    final int body;
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
            int body,
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
        this.body = body;
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

    /// 浮层毛玻璃效果（Apple Glassmorphism）：半透明表层 + Android 12+ 原生窗口背景模糊。
    void applySheetGlass(Context context, android.view.Window window, int radiusDp) {
        if (window == null) {
            return;
        }
        int glassFill = withAlpha(surface, light ? 240 : 235);
        window.setBackgroundDrawable(shape(context, glassFill, radiusDp, 1, outline));
        window.setDimAmount(light ? 0.26f : 0.48f);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            try {
                window.addFlags(android.view.WindowManager.LayoutParams.FLAG_BLUR_BEHIND);
                window.getAttributes().setBlurBehindRadius(dp(context, 22));
                window.setBackgroundBlurRadius(dp(context, 28));
            } catch ( Throwable ignored) {
                // 部分 ROM 不支持窗口级模糊时回退为高通透半透明背景。
            }
        }
    }

    /// 苹果生态语义令牌映射：
    /// 底面 bg / 卡片与键帽 sf / 次级表面 sf2 / 分割线 line；
    /// 文字三层：标题 text (#000000) / 正文 body (#3A3A3C) / 辅助说明 muted (#8E8E93)；
    /// 唯一主色 primary (#000000 纯黑控件 / 深色模式 #FFFFFF) + 适中饱和度状态色 success (#34C759) / warning (#FF9500) / danger (#FF3B30)。
    private static PhoneDeckTheme palette(String id, String name, String description, boolean light,
                                          String[] p) {
        int bg = Color.parseColor(p[0]);
        int sf = Color.parseColor(p[1]);
        int sf2 = Color.parseColor(p[2]);
        int line = Color.parseColor(p[3]);
        int text = Color.parseColor(p[4]);
        int body = Color.parseColor(p[5]);
        int muted = Color.parseColor(p[6]);
        int primary = Color.parseColor(p[7]);
        int onPrimary = Color.parseColor(p[8]);
        int pContainer = Color.parseColor(p[9]);
        int onPContainer = Color.parseColor(p[10]);
        return new PhoneDeckTheme(id, name, description, light,
                bg, sf, sf2, sf, bg, line,
                text, body, muted,
                primary, blend(primary, onPrimary, 0.20f), onPrimary, primary,
                pContainer, onPContainer,
                primary, onPrimary,
                Color.parseColor(p[11]), Color.parseColor(p[12]),
                Color.parseColor(p[13]), Color.parseColor(p[14]),
                Color.parseColor(p[15]), Color.parseColor(p[16]));
    }
}
