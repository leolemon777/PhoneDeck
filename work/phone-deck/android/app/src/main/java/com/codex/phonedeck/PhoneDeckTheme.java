package com.codex.phonedeck;

import android.app.Activity;
import android.content.Context;
import android.content.SharedPreferences;
import android.content.res.Configuration;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;

/// 主题：品牌族 × 深浅模式两级选择（应用户要求收拢为 4 族）：
/// 冰川玻璃（默认，唯一玻璃拟态，仅浅色）、ChatGPT、Claude、Grok；
/// 后三族各带浅/深两套调色板，深浅可跟随系统昼夜或手动锁定。
/// 每套主题只有一个点缀色（primary），success/warning/danger 只用于状态语义。
final class PhoneDeckTheme {
    static final String PREFS_NAME = "PhoneDeckSettings";
    static final String PREF_THEME_ID = "theme_id";
    static final String PREF_THEME_BRAND = "theme_brand";
    static final String PREF_THEME_MODE = "theme_mode";
    /// 品牌族：一族一个选项，深浅由 mode 决定。
    static final String GLASS = "glass";
    static final String GPT = "gpt";
    static final String CLAUDE = "claude";
    static final String GROK = "grok";
    static final String[] BRANDS = {GLASS, GPT, CLAUDE, GROK};
    /// 深浅模式：跟随系统 / 浅色 / 深色。
    static final String MODE_AUTO = "auto";
    static final String MODE_LIGHT = "light";
    static final String MODE_DARK = "dark";
    // 具体实例 id（brand + mode 解析结果）。
    static final String FROST = "frost";
    static final String GPT_LIGHT = "gptlight";
    static final String GPT_DARK = "gptdark";
    static final String CLAUDE_LIGHT = "claudelight";
    static final String CLAUDE_DARK = "claudedark";
    static final String GROK_LIGHT = "groklight";
    static final String GROK_DARK = "grokdark";

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
        String[] selection = ensureSelection(context);
        return brandTheme(selection[0], isDarkMode(context, selection[1]));
    }

    /// 当前存储的品牌族；设置页选中态判断用。
    static String storedBrand(Context context) {
        return ensureSelection(context)[0];
    }

    /// 当前存储的深浅模式（auto/light/dark）。
    static String storedMode(Context context) {
        return ensureSelection(context)[1];
    }

    static void saveSelection(Context context, String brand, String mode) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
                .edit()
                .putString(PREF_THEME_BRAND, validBrand(brand))
                .putString(PREF_THEME_MODE, validMode(mode))
                .apply();
    }

    /// 品牌族显示名。
    static String brandName(String brand) {
        switch (validBrand(brand)) {
            case GPT:
                return "ChatGPT";
            case CLAUDE:
                return "Claude";
            case GROK:
                return "Grok";
            default:
                return "冰川玻璃";
        }
    }

    /// 品牌 + 深浅 → 具体主题实例；设置页预览也用它取两侧配色。
    /// 冰川玻璃只有浅色一态，深色模式下仍返回浅色玻璃。
    static PhoneDeckTheme brandTheme(String brand, boolean dark) {
        switch (validBrand(brand)) {
            case GPT:
                return dark ? gptDark() : gptLight();
            case CLAUDE:
                return dark ? claudeDark() : claudeLight();
            case GROK:
                return dark ? grokDark() : grokLight();
            default:
                return frost();
        }
    }

    private static boolean isDarkMode(Context context, String mode) {
        if (MODE_DARK.equals(mode)) {
            return true;
        }
        if (MODE_LIGHT.equals(mode)) {
            return false;
        }
        int night = context.getResources().getConfiguration().uiMode
                & Configuration.UI_MODE_NIGHT_MASK;
        return night == Configuration.UI_MODE_NIGHT_YES;
    }

    /// 读取品牌 + 模式；首次遇到旧版 theme_id 时一次性迁移成两级存储。
    /// 已下线主题（纸卡 4 套、历史配色 9 套、Gemini/Hermes/豆包）统一回退冰川玻璃，
    /// 尽量保留用户原先的深浅语义。
    private static String[] ensureSelection(Context context) {
        SharedPreferences preferences =
                context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        String brand = preferences.getString(PREF_THEME_BRAND, null);
        String mode = preferences.getString(PREF_THEME_MODE, null);
        if (brand != null && mode != null) {
            return new String[]{validBrand(brand), validMode(mode)};
        }
        String legacy = preferences.getString(PREF_THEME_ID, null);
        String migratedBrand = GLASS;
        String migratedMode = MODE_AUTO;
        if (legacy != null) {
            switch (legacy) {
                // 品牌族时代的实例 id：回到对应族。
                case GPT_LIGHT:
                case CLAUDE_LIGHT:
                case GROK_LIGHT:
                    migratedBrand = legacy.replace("light", "");
                    migratedMode = MODE_LIGHT;
                    break;
                case GPT_DARK:
                case CLAUDE_DARK:
                case GROK_DARK:
                    migratedBrand = legacy.replace("dark", "");
                    migratedMode = MODE_DARK;
                    break;
                case GEMINI_LIGHT_LEGACY:
                case GEMINI_DARK_LEGACY:
                case HERMES_LIGHT_LEGACY:
                case HERMES_DARK_LEGACY:
                case DOUBAO_LIGHT_LEGACY:
                case DOUBAO_DARK_LEGACY:
                    migratedBrand = GLASS;
                    migratedMode = legacy.endsWith("dark") ? MODE_DARK : MODE_LIGHT;
                    break;
                // dev.8/9 单级 id：纸卡与历史配色全部回退冰川玻璃。
                case "frost":
                    migratedBrand = GLASS;
                    migratedMode = MODE_LIGHT;
                    break;
                case "soft":
                    migratedBrand = GLASS;
                    migratedMode = MODE_AUTO;
                    break;
                case "ivory": case "pearl": case "paper":
                case "inklight": case "goldblue":
                    migratedBrand = GLASS;
                    migratedMode = MODE_LIGHT;
                    break;
                case "espresso": case "cocoa": case "ocean": case "oled":
                case "inkdark": case "goldamber": case "goldforest":
                    migratedBrand = GLASS;
                    migratedMode = MODE_DARK;
                    break;
                case MODE_AUTO:
                    migratedBrand = GPT;
                    migratedMode = MODE_AUTO;
                    break;
                default:
                    // 更早的已下线 ID：回退冰川玻璃浅色。
                    migratedBrand = GLASS;
                    migratedMode = MODE_LIGHT;
                    break;
            }
        }
        preferences.edit()
                .putString(PREF_THEME_BRAND, migratedBrand)
                .putString(PREF_THEME_MODE, migratedMode)
                .remove(PREF_THEME_ID)
                .apply();
        return new String[]{migratedBrand, migratedMode};
    }

    // 仅供迁移 switch 引用的已下线实例 id。
    private static final String GEMINI_LIGHT_LEGACY = "geminilight";
    private static final String GEMINI_DARK_LEGACY = "geminidark";
    private static final String HERMES_LIGHT_LEGACY = "hermeslight";
    private static final String HERMES_DARK_LEGACY = "hermesdark";
    private static final String DOUBAO_LIGHT_LEGACY = "doubaolight";
    private static final String DOUBAO_DARK_LEGACY = "doubaodark";

    private static String validBrand(String brand) {
        for (String candidate : BRANDS) {
            if (candidate.equals(brand)) {
                return candidate;
            }
        }
        return GLASS;
    }

    private static String validMode(String mode) {
        return MODE_LIGHT.equals(mode) || MODE_DARK.equals(mode) ? mode : MODE_AUTO;
    }

    static PhoneDeckTheme byId(String id) {
        // 兼容入口：按实例 id 查找；未知 id 回退冰川玻璃。
        if (id != null) {
            for (String brand : BRANDS) {
                for (boolean dark : new boolean[]{false, true}) {
                    PhoneDeckTheme candidate = brandTheme(brand, dark);
                    if (candidate.id.equals(id)) {
                        return candidate;
                    }
                }
            }
        }
        return frost();
    }

    /// 冰川玻璃实例：主界面据此挂载极光磨砂背景。
    boolean isFrost() {
        return FROST.equals(id);
    }

    /// Grok 单色皮肤：分类色统一映射为灰阶，卡片只有深浅差异。
    boolean isMonochrome() {
        return GROK_LIGHT.equals(id) || GROK_DARK.equals(id);
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

    /// 快捷卡底色：分类点缀色以低比例混入按键底，保持卡片安静。
    int shortcutColor(String color) {
        int accent = shortcutAccent(color);
        if ("slate".equals(color)) {
            return key;
        }
        float blendAmount = isFrost() ? 0.10f : isMonochrome() ? 0.32f : light ? 0.13f : 0.30f;
        int result = mix(key, accent, blendAmount);
        return isFrost() ? withAlpha(result, 214) : result;
    }

    /// 快捷卡的分类色点：只在小面积（圆点、文本动作副标题）出现，承载分类语义。
    int shortcutAccent(String color) {
        if (isMonochrome()) {
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
                return light ? Color.rgb(108, 92, 186) : Color.rgb(140, 120, 212);
            case "green":
                return light ? Color.rgb(28, 140, 96) : Color.rgb(74, 182, 130);
            case "orange":
                return light ? Color.rgb(198, 108, 28) : Color.rgb(226, 150, 72);
            case "red":
                return light ? Color.rgb(200, 64, 72) : Color.rgb(230, 110, 116);
            case "slate":
                return isFrost() ? Color.rgb(79, 105, 141) : muted;
            default:
                return primary;
        }
    }

    int shortcutPressedColor(String color) {
        return mix(shortcutColor(color), primary, light ? 0.13f : 0.25f);
    }

    int feedbackSurface(int semanticColor) {
        return mix(surface, semanticColor, light ? 0.07f : 0.14f);
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
            // 玻璃表面：斜向三段渐变 + 白描边。
            int glassAlpha = Color.alpha(fill);
            int endAlpha = fill == voiceDock ? Math.max(246, glassAlpha - 2)
                    : Math.max(165, glassAlpha - 16);
            drawable.setOrientation(GradientDrawable.Orientation.TL_BR);
            drawable.setColors(new int[]{
                    withAlpha(Color.WHITE, Math.max(190, glassAlpha)),
                    fill,
                    withAlpha(Color.rgb(225, 238, 255), endAlpha)});
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

    /// 原生涟漪反馈：内容仍为纯色 shape，按下时半透明 pressed 色扩散，
    /// 比整块变色更接近系统应用的手感（minSdk 26，RippleDrawable 可用）。
    android.graphics.drawable.Drawable pressable(
            Context context, int normalColor, int pressedColor, int radiusDp) {
        GradientDrawable content = shape(context, normalColor, radiusDp);
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

    private boolean isGlassSurface(int fill) {
        return fill == surface || fill == surfaceRaised || fill == key || fill == voiceDock
                || (Color.alpha(fill) < 250 && fill != Color.TRANSPARENT);
    }

    private static int withAlpha(int color, int alpha) {
        return Color.argb(alpha, Color.red(color), Color.green(color), Color.blue(color));
    }

    /// 冰川玻璃（原版默认）：柔光玻璃渐变、极光背景与玻璃语音坞。
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

    /// ChatGPT 浅色：OpenAI App 风格 · 纯白底、近黑主钮、极简灰阶。
    private static PhoneDeckTheme gptLight() {
        return new PhoneDeckTheme(
                GPT_LIGHT,
                "ChatGPT",
                "OpenAI 风格 · 黑白灰阶，主钮深浅反转",
                true,
                Color.rgb(255, 255, 255),
                Color.rgb(249, 249, 249),
                Color.rgb(240, 240, 240),
                Color.rgb(244, 244, 244),
                Color.rgb(13, 13, 13),
                Color.rgb(47, 47, 47),
                Color.WHITE,
                Color.rgb(13, 13, 13),
                Color.rgb(143, 143, 143),
                Color.rgb(26, 152, 108),
                Color.rgb(187, 121, 26),
                Color.rgb(202, 60, 60),
                Color.rgb(229, 229, 229),
                Color.rgb(255, 255, 255));
    }

    /// ChatGPT 深色：炭灰底 + 白主钮（深色模式主按钮反转）。
    private static PhoneDeckTheme gptDark() {
        return new PhoneDeckTheme(
                GPT_DARK,
                "ChatGPT",
                "OpenAI 风格 · 黑白灰阶，主钮深浅反转",
                false,
                Color.rgb(33, 33, 33),
                Color.rgb(47, 47, 47),
                Color.rgb(56, 56, 56),
                Color.rgb(42, 42, 42),
                Color.rgb(255, 255, 255),
                Color.rgb(217, 217, 217),
                Color.rgb(13, 13, 13),
                Color.rgb(236, 236, 236),
                Color.rgb(166, 166, 166),
                Color.rgb(62, 206, 152),
                Color.rgb(224, 178, 92),
                Color.rgb(230, 110, 116),
                Color.rgb(66, 66, 66),
                Color.rgb(38, 38, 38));
    }

    /// Claude 浅色：Anthropic App 风格 · 米白纸感底 + 陶土橙点缀。
    private static PhoneDeckTheme claudeLight() {
        return new PhoneDeckTheme(
                CLAUDE_LIGHT,
                "Claude",
                "Anthropic 风格 · 米白/暖炭底 + 陶土橙",
                true,
                Color.rgb(250, 249, 245),
                Color.rgb(255, 255, 255),
                Color.rgb(240, 238, 230),
                Color.rgb(240, 238, 230),
                Color.rgb(217, 119, 87),
                Color.rgb(196, 99, 60),
                Color.WHITE,
                Color.rgb(38, 38, 36),
                Color.rgb(138, 133, 124),
                Color.rgb(44, 140, 96),
                Color.rgb(180, 128, 44),
                Color.rgb(196, 84, 76),
                Color.rgb(222, 217, 207),
                Color.rgb(250, 249, 245));
    }

    /// Claude 深色：暖炭底 + 陶土橙（深色下橙色略提亮）。
    private static PhoneDeckTheme claudeDark() {
        return new PhoneDeckTheme(
                CLAUDE_DARK,
                "Claude",
                "Anthropic 风格 · 米白/暖炭底 + 陶土橙",
                false,
                Color.rgb(38, 38, 36),
                Color.rgb(48, 48, 46),
                Color.rgb(58, 58, 56),
                Color.rgb(46, 46, 44),
                Color.rgb(217, 119, 87),
                Color.rgb(224, 139, 109),
                Color.WHITE,
                Color.rgb(243, 242, 236),
                Color.rgb(163, 162, 156),
                Color.rgb(110, 180, 120),
                Color.rgb(222, 178, 92),
                Color.rgb(230, 110, 104),
                Color.rgb(69, 68, 65),
                Color.rgb(42, 42, 40));
    }

    /// Grok 浅色：xAI App 风格 · 纯白黑字，分类色走灰阶。
    private static PhoneDeckTheme grokLight() {
        return new PhoneDeckTheme(
                GROK_LIGHT,
                "Grok",
                "xAI 风格 · 纯黑纯白，分类色灰阶",
                true,
                Color.rgb(255, 255, 255),
                Color.rgb(247, 247, 247),
                Color.rgb(239, 239, 239),
                Color.rgb(245, 245, 245),
                Color.rgb(0, 0, 0),
                Color.rgb(38, 38, 43),
                Color.WHITE,
                Color.rgb(15, 15, 15),
                Color.rgb(107, 107, 112),
                Color.rgb(44, 138, 88),
                Color.rgb(176, 128, 40),
                Color.rgb(198, 58, 58),
                Color.rgb(227, 227, 227),
                Color.rgb(255, 255, 255));
    }

    /// Grok 深色：纯黑画布 + 白主钮。
    private static PhoneDeckTheme grokDark() {
        return new PhoneDeckTheme(
                GROK_DARK,
                "Grok",
                "xAI 风格 · 纯黑纯白，分类色灰阶",
                false,
                Color.rgb(0, 0, 0),
                Color.rgb(18, 18, 19),
                Color.rgb(28, 28, 30),
                Color.rgb(22, 22, 23),
                Color.rgb(255, 255, 255),
                Color.rgb(212, 212, 216),
                Color.rgb(0, 0, 0),
                Color.rgb(237, 237, 239),
                Color.rgb(143, 143, 148),
                Color.rgb(98, 190, 138),
                Color.rgb(222, 178, 92),
                Color.rgb(232, 106, 106),
                Color.rgb(38, 38, 43),
                Color.rgb(10, 10, 11));
    }
}
