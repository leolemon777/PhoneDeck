package com.codex.phonedeck;

import android.app.Activity;
import android.content.Context;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.StateListDrawable;
import android.os.Build;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;

final class PhoneDeckTheme {
    static final String PREFS_NAME = "PhoneDeckSettings";
    static final String PREF_THEME_ID = "theme_id";
    static final String FROST = "frost";

    // 仅供文件内历史调色板工厂保持源码兼容；all()/byId()/load() 均不会返回这些主题。
    private static final String OCEAN = "ocean";
    private static final String OLED = "oled";
    private static final String PAPER = "paper";
    private static final String INK_LIGHT = "inklight";
    private static final String INK_DARK = "inkdark";
    private static final String GOLD_BLUE = "goldblue";
    private static final String GOLD_AMBER = "goldamber";
    private static final String GOLD_FOREST = "goldforest";

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
        SharedPreferences preferences = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        String storedId = preferences.getString(PREF_THEME_ID, FROST);
        if (!FROST.equals(storedId)) {
            preferences.edit().putString(PREF_THEME_ID, FROST).apply();
        }
        return frost();
    }

    static void save(Context context, String themeId) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
                .edit()
                .putString(PREF_THEME_ID, FROST)
                .apply();
    }

    static PhoneDeckTheme[] all() {
        return new PhoneDeckTheme[]{frost()};
    }

    static PhoneDeckTheme byId(String id) {
        // 旧版本可能仍保存 ocean/oled/paper/ink/gold 等主题 ID。
        // 单主题版统一回退到冰川玻璃，并在下次保存时持久化 frost。
        return frost();
    }

    boolean isFrost() {
        return FROST.equals(id);
    }

    boolean isMonochrome() {
        return INK_LIGHT.equals(id) || INK_DARK.equals(id);
    }

    int contentBackground() {
        return isFrost() ? Color.TRANSPARENT : background;
    }

    View wrapContent(Context context, View content) {
        if (!isFrost()) {
            return content;
        }
        FrameLayout root = new FrameLayout(context);
        root.setBackgroundColor(background);
        root.addView(new FrostedBackdropView(context), new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        root.addView(content, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        return root;
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

    int shortcutColor(String color) {
        int accent = shortcutAccent(color);
        if ("slate".equals(color)) {
            return key;
        }
        float blendAmount = isFrost() ? 0.10f : isMonochrome() ? 0.32f : light ? 0.13f : 0.30f;
        int result = mix(key, accent, blendAmount);
        return isFrost() ? withAlpha(result, 214) : result;
    }

    int shortcutAccent(String color) {
        if (isMonochrome()) {
            // Grok 式单色皮肤：六个预设色统一映射为柔和灰阶，
            // 让卡片只有深浅差异，不出现彩色。
            if (light) {
                switch (color) {
                    case "purple":
                        return Color.rgb(71, 71, 76);
                    case "green":
                        return Color.rgb(94, 94, 100);
                    case "orange":
                        return Color.rgb(117, 117, 123);
                    case "red":
                        return Color.rgb(140, 140, 146);
                    case "slate":
                        return key;
                    default:
                        return Color.rgb(48, 48, 52);
                }
            }
            switch (color) {
                case "purple":
                    return Color.rgb(135, 135, 143);
                case "green":
                    return Color.rgb(155, 155, 163);
                case "orange":
                    return Color.rgb(175, 175, 183);
                case "red":
                    return Color.rgb(195, 195, 203);
                case "slate":
                    return key;
                default:
                    return Color.rgb(115, 115, 123);
            }
        }
        switch (color) {
            case "purple":
                return light ? Color.rgb(116, 75, 216) : Color.rgb(130, 91, 190);
            case "green":
                return light ? Color.rgb(25, 158, 112) : Color.rgb(50, 147, 111);
            case "orange":
                return light ? Color.rgb(224, 104, 35) : Color.rgb(190, 120, 51);
            case "red":
                return light ? Color.rgb(209, 66, 105) : Color.rgb(183, 75, 96);
            case "slate":
                return isFrost() ? Color.rgb(79, 105, 141) : key;
            default:
                return primary;
        }
    }

    int shortcutPressedColor(String color) {
        return mix(shortcutColor(color), primary, light ? 0.13f : 0.25f);
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
        if (isFrost() && isGlassSurface(fill)) {
            int glassAlpha = Color.alpha(fill);
            int endAlpha = fill == voiceDock ? Math.max(246, glassAlpha - 2)
                    : Math.max(165, glassAlpha - 16);
            drawable.setOrientation(GradientDrawable.Orientation.TL_BR);
            drawable.setColors(new int[]{
                    withAlpha(Color.WHITE, Math.max(190, glassAlpha)),
                    fill,
                    withAlpha(Color.rgb(225, 238, 255), endAlpha)
            });
            if (strokeWidthDp == 0) {
                strokeWidthDp = 1;
                strokeColor = Color.argb(205, 255, 255, 255);
            }
        } else if (isFrost() && (fill == primary || fill == primaryPressed)) {
            drawable.setOrientation(GradientDrawable.Orientation.TL_BR);
            drawable.setColors(fill == primary
                    ? new int[]{Color.rgb(103, 164, 255), Color.rgb(43, 105, 224),
                    Color.rgb(38, 91, 205)}
                    : new int[]{Color.rgb(77, 137, 235), Color.rgb(31, 84, 194)});
            if (strokeWidthDp == 0) {
                strokeWidthDp = 1;
                strokeColor = Color.argb(220, 255, 255, 255);
            }
        } else {
            drawable.setColor(fill);
        }
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

    private boolean isGlassSurface(int fill) {
        return fill == surface || fill == surfaceRaised || fill == key || fill == voiceDock
                || (Color.alpha(fill) < 250 && fill != Color.TRANSPARENT);
    }

    private static int withAlpha(int color, int alpha) {
        return Color.argb(alpha, Color.red(color), Color.green(color), Color.blue(color));
    }

    private static PhoneDeckTheme frost() {
        return new PhoneDeckTheme(
                FROST,
                "冰川玻璃",
                "清透、明亮，带柔光玻璃层次",
                true,
                Color.rgb(237, 245, 255),
                Color.argb(218, 255, 255, 255),
                Color.argb(235, 255, 255, 255),
                Color.argb(205, 244, 249, 255),
                Color.rgb(48, 105, 215),
                Color.rgb(31, 80, 179),
                Color.WHITE,
                Color.rgb(17, 35, 62),
                Color.rgb(94, 119, 154),
                Color.rgb(29, 157, 100),
                Color.rgb(202, 119, 25),
                Color.rgb(210, 63, 91),
                Color.argb(195, 196, 216, 241),
                Color.rgb(250, 253, 255));
    }

    private static PhoneDeckTheme ocean() {
        return new PhoneDeckTheme(
                OCEAN,
                "深海蓝",
                "沉稳、清晰，适合日常效率操作",
                false,
                Color.rgb(8, 14, 28),
                Color.rgb(20, 29, 49),
                Color.rgb(26, 39, 65),
                Color.rgb(29, 43, 71),
                Color.rgb(119, 169, 255),
                Color.rgb(161, 197, 255),
                Color.rgb(5, 14, 29),
                Color.rgb(247, 249, 255),
                Color.rgb(157, 173, 204),
                Color.rgb(78, 220, 156),
                Color.rgb(255, 194, 91),
                Color.rgb(255, 108, 132),
                Color.rgb(52, 68, 99),
                Color.rgb(15, 23, 42));
    }

    private static PhoneDeckTheme oled() {
        return new PhoneDeckTheme(
                OLED,
                "OLED 黑",
                "纯黑省电，高对比度，适合长期亮屏",
                false,
                Color.BLACK,
                Color.rgb(10, 11, 15),
                Color.rgb(18, 20, 27),
                Color.rgb(21, 24, 33),
                Color.rgb(139, 183, 255),
                Color.rgb(181, 211, 255),
                Color.rgb(0, 7, 18),
                Color.rgb(250, 251, 255),
                Color.rgb(166, 174, 193),
                Color.rgb(77, 224, 159),
                Color.rgb(255, 198, 91),
                Color.rgb(255, 110, 135),
                Color.rgb(42, 46, 58),
                Color.rgb(7, 8, 11));
    }

    private static PhoneDeckTheme paper() {
        return new PhoneDeckTheme(
                PAPER,
                "柔和浅色",
                "温暖、低刺激，适合白天和明亮环境",
                true,
                Color.rgb(246, 243, 236),
                Color.rgb(255, 255, 255),
                Color.rgb(237, 234, 226),
                Color.rgb(232, 237, 246),
                Color.rgb(49, 94, 168),
                Color.rgb(38, 78, 143),
                Color.WHITE,
                Color.rgb(29, 36, 48),
                Color.rgb(101, 111, 130),
                Color.rgb(31, 132, 89),
                Color.rgb(173, 106, 21),
                Color.rgb(184, 59, 82),
                Color.rgb(214, 210, 201),
                Color.rgb(252, 250, 246));
    }

    private static PhoneDeckTheme inkLight() {
        return new PhoneDeckTheme(
                INK_LIGHT,
                "极简墨白",
                "Grok 风格 · 柔和灰阶层叠，安静克制",
                true,
                Color.rgb(248, 248, 247),
                Color.WHITE,
                Color.rgb(241, 241, 239),
                Color.WHITE,
                Color.rgb(16, 16, 18),
                Color.rgb(46, 46, 48),
                Color.WHITE,
                Color.rgb(20, 20, 22),
                Color.rgb(113, 113, 122),
                Color.rgb(58, 58, 63),
                Color.rgb(142, 142, 147),
                Color.rgb(23, 23, 26),
                Color.rgb(233, 233, 230),
                Color.rgb(255, 255, 255));
    }

    private static PhoneDeckTheme inkDark() {
        return new PhoneDeckTheme(
                INK_DARK,
                "极简纯黑",
                "Grok 风格 · 近黑层叠，低刺激深色",
                false,
                Color.rgb(10, 10, 11),
                Color.rgb(23, 23, 26),
                Color.rgb(32, 32, 36),
                Color.rgb(26, 26, 30),
                Color.rgb(242, 242, 243),
                Color.rgb(255, 255, 255),
                Color.rgb(11, 11, 12),
                Color.rgb(244, 244, 245),
                Color.rgb(139, 139, 146),
                Color.rgb(201, 201, 206),
                Color.rgb(126, 126, 132),
                Color.rgb(255, 255, 255),
                Color.rgb(44, 44, 49),
                Color.rgb(16, 16, 19));
    }

    private static PhoneDeckTheme goldBlue() {
        return new PhoneDeckTheme(
                GOLD_BLUE,
                "黄金靛蓝",
                "基准色相 215° · 伙伴色按黄金角 137.5° 取绯红",
                true,
                Color.rgb(241, 244, 249),
                Color.rgb(255, 255, 255),
                Color.rgb(226, 233, 242),
                Color.rgb(224, 232, 243),
                Color.rgb(36, 86, 174),
                Color.rgb(27, 69, 144),
                Color.WHITE,
                Color.rgb(22, 35, 58),
                Color.rgb(92, 112, 137),
                Color.rgb(46, 125, 91),
                Color.rgb(168, 114, 31),
                Color.rgb(178, 58, 71),
                Color.rgb(203, 216, 232),
                Color.rgb(250, 251, 253));
    }

    private static PhoneDeckTheme goldAmber() {
        return new PhoneDeckTheme(
                GOLD_AMBER,
                "黄金琥珀",
                "基准色相 32° · 青绿与品红按黄金角配对",
                false,
                Color.rgb(22, 18, 16),
                Color.rgb(33, 27, 21),
                Color.rgb(44, 36, 27),
                Color.rgb(45, 37, 28),
                Color.rgb(227, 154, 69),
                Color.rgb(239, 172, 95),
                Color.rgb(32, 21, 5),
                Color.rgb(243, 236, 226),
                Color.rgb(166, 152, 138),
                Color.rgb(63, 174, 150),
                Color.rgb(224, 195, 104),
                Color.rgb(194, 94, 126),
                Color.rgb(58, 48, 38),
                Color.rgb(26, 21, 17));
    }

    private static PhoneDeckTheme goldForest() {
        return new PhoneDeckTheme(
                GOLD_FOREST,
                "黄金松绿",
                "基准色相 152° · 橙与紫红按黄金角配对",
                false,
                Color.rgb(11, 19, 16),
                Color.rgb(18, 32, 26),
                Color.rgb(26, 44, 36),
                Color.rgb(27, 45, 37),
                Color.rgb(78, 200, 148),
                Color.rgb(99, 214, 166),
                Color.rgb(6, 19, 12),
                Color.rgb(234, 244, 238),
                Color.rgb(134, 160, 147),
                Color.rgb(143, 224, 182),
                Color.rgb(206, 155, 78),
                Color.rgb(196, 88, 107),
                Color.rgb(34, 53, 41),
                Color.rgb(14, 24, 19));
    }
}
