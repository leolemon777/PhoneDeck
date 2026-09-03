package com.codex.phonedeck;

import android.app.Activity;
import android.content.Context;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;

/// 设计基准（依据 Material Design 3 色彩角色与 Apple HIG 语义色）：
/// 表面层级用中性灰阶的色调步进表达，不依赖发光、玻璃或渐变；
/// 每套主题只有一个点缀色（primary），蓝色/主题色=可交互，
/// success/warning/danger 只用于状态语义，红色只表示录音与停止。
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
    static final String GEMINI = "gemini";
    static final String HERMES = "hermes";
    static final String DOUBAO = "doubao";
    static final String[] BRANDS = {
            GLASS, GPT, CLAUDE, GROK, GEMINI, HERMES, DOUBAO};
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
    static final String GEMINI_LIGHT = "geminilight";
    static final String GEMINI_DARK = "geminidark";
    static final String HERMES_LIGHT = "hermeslight";
    static final String HERMES_DARK = "hermesdark";
    static final String DOUBAO_LIGHT = "doubaolight";
    static final String DOUBAO_DARK = "doubaodark";

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
            case GEMINI:
                return "Gemini";
            case HERMES:
                return "Hermes";
            case DOUBAO:
                return "豆包";
            default:
                return "冰川玻璃";
        }
    }

    /// 品牌 + 深浅 → 具体主题实例；设置页预览也用它取两侧配色。
    static PhoneDeckTheme brandTheme(String brand, boolean dark) {
        switch (validBrand(brand)) {
            case GPT:
                return dark ? gptDark() : gptLight();
            case CLAUDE:
                return dark ? claudeDark() : claudeLight();
            case GROK:
                return dark ? grokDark() : grokLight();
            case GEMINI:
                return dark ? geminiDark() : geminiLight();
            case HERMES:
                return dark ? hermesDark() : hermesLight();
            case DOUBAO:
                return dark ? doubaoDark() : doubaoLight();
            default:
                return glass();
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
                & android.content.res.Configuration.UI_MODE_NIGHT_MASK;
        return night == android.content.res.Configuration.UI_MODE_NIGHT_YES;
    }

    /// 读取品牌 + 模式；首次遇到旧版 theme_id 时一次性迁移成两级存储。
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
        String migratedMode = MODE_LIGHT;
        if (legacy != null) {
            switch (legacy) {
                case GPT_LIGHT: case CLAUDE_LIGHT: case GROK_LIGHT:
                case GEMINI_LIGHT:
                    migratedBrand = legacy.replace("light", "");
                    migratedMode = MODE_LIGHT;
                    break;
                case GPT_DARK: case CLAUDE_DARK: case GROK_DARK:
                case GEMINI_DARK:
                    migratedBrand = legacy.replace("dark", "");
                    migratedMode = MODE_DARK;
                    break;
                case MODE_AUTO:
                    migratedBrand = GPT;
                    migratedMode = MODE_AUTO;
                    break;
                case GLASS:
                    migratedBrand = GLASS;
                    migratedMode = MODE_LIGHT;
                    break;
                default:
                    // frost 与更早的已下线 ID：回退冰川玻璃浅色。
                    break;
            }
        }
        preferences.edit()
                .putString(PREF_THEME_BRAND, migratedBrand)
                .putString(PREF_THEME_MODE, migratedMode)
                .apply();
        return new String[]{migratedBrand, migratedMode};
    }

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
        // 兼容入口：按实例 id 查找；未知 id 回退 frost（代码级默认）。
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

    boolean isFrost() {
        return FROST.equals(id);
    }

    /// 原版冰川玻璃：唯一的玻璃拟态主题，保留柔光背景与半透明表面。
    boolean isGlass() {
        return GLASS.equals(id);
    }

    /// Grok 单色皮肤：分类色统一映射为灰阶，卡片只有深浅差异。
    boolean isMonochrome() {
        return GROK_LIGHT.equals(id) || GROK_DARK.equals(id);
    }

    int contentBackground() {
        return isGlass() ? Color.TRANSPARENT : background;
    }

    View wrapContent(Context context, View content) {
        if (!isGlass()) {
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
                return muted;
            default:
                return primary;
        }
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
        if (isGlass() && isGlassSurface(fill)) {
            // 原版玻璃表面：斜向三段渐变 + 白描边。
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
        } else if (isGlass() && (fill == primary || fill == primaryPressed)) {
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

    /// 经典浅色：中性冷灰 + 蓝色点缀（默认主题）。
    private static PhoneDeckTheme frost() {
        return new PhoneDeckTheme(
                FROST,
                "经典浅色",
                "中性灰阶 · 蓝色点缀，克制耐看",
                true,
                Color.rgb(246, 247, 249),
                Color.rgb(255, 255, 255),
                Color.rgb(240, 242, 245),
                Color.rgb(243, 244, 247),
                Color.rgb(0, 92, 190),
                Color.rgb(0, 72, 156),
                Color.WHITE,
                Color.rgb(28, 30, 34),
                Color.rgb(122, 127, 135),
                Color.rgb(22, 138, 88),
                Color.rgb(178, 122, 20),
                Color.rgb(202, 52, 56),
                Color.rgb(224, 226, 230),
                Color.rgb(252, 252, 254));
    }

    /// 原版冰川玻璃：柔光背景 + 半透明表面 + 蓝渐变主按钮（项目所有者指定恢复）。
    private static PhoneDeckTheme glass() {
        return new PhoneDeckTheme(
                GLASS,
                "冰川玻璃",
                "原版 · 柔光玻璃层次与光晕背景",
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

    /// Gemini 浅色：Google App 风格 · 浅蓝灰面板 + 蓝点缀。
    private static PhoneDeckTheme geminiLight() {
        return new PhoneDeckTheme(
                GEMINI_LIGHT,
                "Gemini",
                "Google 风格 · 蓝点缀 + 浅蓝灰面板",
                true,
                Color.rgb(255, 255, 255),
                Color.rgb(240, 244, 249),
                Color.rgb(233, 238, 246),
                Color.rgb(240, 244, 249),
                Color.rgb(11, 87, 208),
                Color.rgb(8, 66, 160),
                Color.WHITE,
                Color.rgb(31, 31, 31),
                Color.rgb(95, 99, 104),
                Color.rgb(30, 142, 92),
                Color.rgb(180, 128, 40),
                Color.rgb(199, 54, 54),
                Color.rgb(214, 219, 226),
                Color.rgb(248, 250, 253));
    }

    /// Gemini 深色：石墨底 + 淡蓝主钮（深色下 Google 蓝提亮为 A8C7FA）。
    private static PhoneDeckTheme geminiDark() {
        return new PhoneDeckTheme(
                GEMINI_DARK,
                "Gemini",
                "Google 风格 · 蓝点缀 + 浅蓝灰面板",
                false,
                Color.rgb(19, 19, 20),
                Color.rgb(30, 31, 32),
                Color.rgb(40, 41, 43),
                Color.rgb(30, 31, 32),
                Color.rgb(168, 199, 250),
                Color.rgb(194, 217, 251),
                Color.rgb(6, 46, 111),
                Color.rgb(227, 227, 227),
                Color.rgb(154, 160, 166),
                Color.rgb(98, 190, 138),
                Color.rgb(222, 178, 92),
                Color.rgb(232, 106, 106),
                Color.rgb(51, 53, 55),
                Color.rgb(27, 28, 29));
    }

    /// Hermes 浅色：Nous Hermes Agent "daylight" 风 · 纸感米白底 + 冷蓝点缀。
    private static PhoneDeckTheme hermesLight() {
        return new PhoneDeckTheme(
                HERMES_LIGHT,
                "Hermes",
                "Nous 风格 · 纸感底冷蓝，石墨底暖橙",
                true,
                Color.rgb(250, 249, 245),
                Color.rgb(255, 255, 255),
                Color.rgb(232, 230, 220),
                Color.rgb(238, 236, 226),
                Color.rgb(63, 110, 158),
                Color.rgb(52, 92, 134),
                Color.WHITE,
                Color.rgb(20, 20, 19),
                Color.rgb(124, 122, 112),
                Color.rgb(95, 133, 80),
                Color.rgb(176, 140, 60),
                Color.rgb(188, 88, 74),
                Color.rgb(218, 215, 203),
                Color.rgb(252, 251, 247));
    }

    /// Hermes 深色：Nous Hermes Agent "slate" 风 · 石墨蓝底 + 签名暖橙。
    private static PhoneDeckTheme hermesDark() {
        return new PhoneDeckTheme(
                HERMES_DARK,
                "Hermes",
                "Nous 风格 · 纸感底冷蓝，石墨底暖橙",
                false,
                Color.rgb(24, 27, 31),
                Color.rgb(33, 37, 42),
                Color.rgb(43, 48, 54),
                Color.rgb(37, 41, 46),
                Color.rgb(217, 119, 87),
                Color.rgb(228, 140, 110),
                Color.WHITE,
                Color.rgb(236, 235, 229),
                Color.rgb(150, 152, 158),
                Color.rgb(134, 168, 110),
                Color.rgb(222, 178, 92),
                Color.rgb(232, 112, 100),
                Color.rgb(55, 60, 66),
                Color.rgb(29, 33, 38));
    }

    /// 豆包浅色：字节豆包 App 风 · 白底 + 靛蓝点缀 + 蓝灰面板。
    private static PhoneDeckTheme doubaoLight() {
        return new PhoneDeckTheme(
                DOUBAO_LIGHT,
                "豆包",
                "字节风格 · 靛蓝点缀，清爽蓝灰",
                true,
                Color.rgb(255, 255, 255),
                Color.rgb(245, 246, 251),
                Color.rgb(236, 238, 247),
                Color.rgb(243, 244, 250),
                Color.rgb(59, 91, 235),
                Color.rgb(47, 76, 208),
                Color.WHITE,
                Color.rgb(28, 30, 38),
                Color.rgb(110, 114, 130),
                Color.rgb(36, 150, 102),
                Color.rgb(188, 132, 32),
                Color.rgb(206, 62, 78),
                Color.rgb(222, 224, 236),
                Color.rgb(250, 251, 254));
    }

    /// 豆包深色：深灰蓝底（非纯黑）+ 亮靛蓝主钮。
    private static PhoneDeckTheme doubaoDark() {
        return new PhoneDeckTheme(
                DOUBAO_DARK,
                "豆包",
                "字节风格 · 靛蓝点缀，清爽蓝灰",
                false,
                Color.rgb(21, 21, 27),
                Color.rgb(31, 31, 40),
                Color.rgb(41, 42, 53),
                Color.rgb(35, 35, 45),
                Color.rgb(112, 134, 255),
                Color.rgb(138, 158, 255),
                Color.rgb(12, 16, 44),
                Color.rgb(233, 234, 240),
                Color.rgb(152, 155, 170),
                Color.rgb(96, 196, 140),
                Color.rgb(224, 180, 96),
                Color.rgb(234, 108, 120),
                Color.rgb(53, 54, 68),
                Color.rgb(26, 26, 34));
    }
}
