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

/// 主题仓库：简洁浅/深主题 + 9 套历史配色；已有用户偏好保留。
/// - 冰川玻璃：唯一玻璃拟态，极光背景 + 半透明玻璃层次；
/// - 纸卡系列 4 套：奶油/云白浅色、暖黑/暖灰深色；
/// - 风格皮肤 4 套（按用户设计稿）：瑞士黑白（直角细边）、
///   克莱因蓝（蓝渐变主按钮）、工业沙橙（包豪斯暖沙底 + 橙点缀）、
///   极简单色（纯黑终端，分类色走灰阶）。
/// 每套主题只有一个点缀色（primary），success/warning/danger 只用于状态语义。
final class PhoneDeckTheme {
    static final String PREFS_NAME = "PhoneDeckSettings";
    static final String PREF_THEME_ID = "theme_id";
    static final String NATIVE_LIGHT = "native_light";
    static final String NATIVE_DARK = "native_dark";
    static final String FROST = "frost";
    static final String IVORY = "ivory";
    static final String PEARL = "pearl";
    static final String ESPRESSO = "espresso";
    static final String COCOA = "cocoa";
    static final String SWISS = "swiss";
    static final String KLEIN = "klein";
    static final String BAUHAUS = "bauhaus";
    static final String MONO = "mono";

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
    /// 风格皮肤参数：圆角上限（超出则压到该值）与卡面细边宽度（0=不加）。
    private final int radiusCapDp;
    private final int cardStrokeDp;

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
        this(id, name, description, light, background, surface, surfaceRaised, key,
                primary, primaryPressed, onPrimary, text, muted, success, warning,
                danger, outline, voiceDock, 99, 0);
    }

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
            int voiceDock,
            int radiusCapDp,
            int cardStrokeDp) {
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
        this.radiusCapDp = radiusCapDp;
        this.cardStrokeDp = cardStrokeDp;
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

    /** 简洁浅/深优先展示，后接九套兼容主题。 */
    static PhoneDeckTheme[] all() {
        return new PhoneDeckTheme[]{
                nativeTheme(true), nativeTheme(false),
                frost(),
                ivory(), pearl(), espresso(), cocoa(),
                swiss(), klein(), bauhaus(), mono()};
    }

    static PhoneDeckTheme byId(String id) {
        switch (validId(id)) {
            case NATIVE_LIGHT: return nativeTheme(true);
            case NATIVE_DARK: return nativeTheme(false);
            case IVORY: return ivory();
            case PEARL: return pearl();
            case ESPRESSO: return espresso();
            case COCOA: return cocoa();
            case SWISS: return swiss();
            case KLEIN: return klein();
            case BAUHAUS: return bauhaus();
            case MONO: return mono();
            default: return frost();
        }
    }

    /**
     * 读取并一次性迁移历史存储：
     * - dev.10 的品牌族（theme_brand + theme_mode）两级存储；
     * - dev.8/9 及更早的单级 theme_id。
     * 已下线主题按明暗就近映射：浅色 → 瑞士黑白，深色 → 极简单色，
     * 其余回退冰川玻璃。
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
        String migrated = null;
        if (legacyPairPending) {
            String brand = preferences.getString("theme_brand", null);
            String mode = preferences.getString("theme_mode", null);
            if ("glass".equals(brand)) {
                migrated = FROST;
            } else if (brand != null) {
                // gpt / claude / grok 族：按明暗就近落到新皮肤。
                migrated = "dark".equals(mode) ? MONO : SWISS;
            }
        }
        if (migrated == null && stored != null) {
            switch (stored) {
                case FROST: case IVORY: case PEARL:
                case ESPRESSO: case COCOA:
                    migrated = stored;
                    break;
                case "soft":
                    migrated = IVORY;
                    break;
                case "paper": case "inklight": case "goldblue":
                case "gptlight": case "claudelight": case "groklight":
                    migrated = SWISS;
                    break;
                case "ocean": case "oled": case "inkdark":
                case "goldamber": case "goldforest":
                case "gptdark": case "claudedark": case "grokdark":
                    migrated = MONO;
                    break;
                default:
                    migrated = FROST;
                    break;
            }
        }
        if (migrated == null) {
            migrated = NATIVE_LIGHT;
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
        return FROST;
    }

    /// 冰川玻璃实例：主界面据此挂载极光磨砂背景。
    boolean isFrost() {
        return FROST.equals(id);
    }

    boolean isNative() {
        return NATIVE_LIGHT.equals(id) || NATIVE_DARK.equals(id);
    }

    private static PhoneDeckTheme nativeTheme(boolean light) {
        return new PhoneDeckTheme(
                light ? NATIVE_LIGHT : NATIVE_DARK,
                light ? "简洁 · 浅色" : "简洁 · 深色",
                light ? "清晰留白，蓝色强调" : "柔和深灰，舒适低光",
                light,
                Color.parseColor(light ? "#F5F6F8" : "#111317"),
                Color.parseColor(light ? "#FFFFFF" : "#1B1E24"),
                Color.parseColor(light ? "#EDF0F5" : "#2A2F39"),
                Color.parseColor(light ? "#FFFFFF" : "#22262E"),
                Color.parseColor(light ? "#285ED4" : "#A9C7FF"),
                Color.parseColor(light ? "#204CAA" : "#8BB2F2"),
                Color.parseColor(light ? "#FFFFFF" : "#142746"),
                Color.parseColor(light ? "#18202D" : "#F0F2F6"),
                Color.parseColor(light ? "#626D7C" : "#A7B0C0"),
                Color.parseColor(light ? "#157347" : "#79D2A3"),
                Color.parseColor(light ? "#8A5B0A" : "#EAC078"),
                Color.parseColor(light ? "#BA3044" : "#FFA0AA"),
                Color.parseColor(light ? "#E1E5EB" : "#343B48"),
                Color.parseColor(light ? "#FFFFFF" : "#1B1E24"), 18, 1);
    }

    /// 纸卡系列：卡片回归纸面，预设色只留给小面积点缀。
    boolean isSoft() {
        return IVORY.equals(id) || PEARL.equals(id)
                || ESPRESSO.equals(id) || COCOA.equals(id);
    }

    /// 极简单色皮肤：分类色统一映射为灰阶，卡片只有深浅差异。
    boolean isMonochrome() {
        return MONO.equals(id);
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

    /// 快捷卡底色：分类点缀色以低比例混入按键底，保持卡片安静。
    int shortcutColor(String color) {
        if (isNative()) return key;
        if (isSoft()) {
            // 纸卡风格：卡片回归纸面，预设色只留给 chord 点缀。
            return surface;
        }
        if (isFlatCard()) {
            // 设计稿风格：素色键帽，分类色只出现在小圆点。
            return key;
        }
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
        if (isSoft()) {
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
                return light ? Color.rgb(116, 75, 216) : Color.rgb(130, 91, 190);
            case "green":
                return light ? Color.rgb(25, 158, 112) : Color.rgb(50, 147, 111);
            case "orange":
                return light ? Color.rgb(224, 104, 35) : Color.rgb(190, 120, 51);
            case "red":
                return light ? Color.rgb(209, 66, 105) : Color.rgb(183, 75, 96);
            case "slate":
                return isFrost() ? Color.rgb(79, 105, 141) : muted;
            default:
                return primary;
        }
    }

    int shortcutPressedColor(String color) {
        if (isSoft()) {
            return mix(surface, key, light ? 0.55f : 0.8f);
        }
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
        } else if (isKlein() && (fill == primary || fill == primaryPressed)) {
            // 克莱因蓝主按钮：深邃蓝渐变胶囊。
            drawable.setOrientation(GradientDrawable.Orientation.TL_BR);
            drawable.setColors(fill == primary
                    ? new int[]{Color.rgb(30, 58, 138), Color.rgb(30, 64, 175),
                    Color.rgb(37, 99, 235)}
                    : new int[]{Color.rgb(23, 46, 110), Color.rgb(23, 50, 138)});
            if (strokeWidthDp == 0) {
                strokeWidthDp = 1;
                strokeColor = Color.argb(70, 255, 255, 255);
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
            // 风格皮肤的卡面：压圆角并补细边（调用处显式传边框时不覆盖）。
            radiusDp = Math.min(radiusDp, radiusCapDp);
            if (cardStrokeDp > 0 && strokeWidthDp == 0 && isCardSurface(fill)) {
                strokeWidthDp = cardStrokeDp;
                strokeColor = outline;
            }
        }
        drawable.setCornerRadius(dp(context, radiusDp));
        if (strokeWidthDp > 0) {
            drawable.setStroke(dp(context, strokeWidthDp), strokeColor);
        }
        return drawable;
    }

    private boolean isKlein() {
        return KLEIN.equals(id);
    }

    /// 设计稿风格皮肤：素色键帽 + 圆润卡片，与纸卡共享同一形态语言。
    private boolean isFlatCard() {
        return SWISS.equals(id) || KLEIN.equals(id) || BAUHAUS.equals(id);
    }

    /// 卡面判定：风格皮肤的细边只加在表面/键帽/语音坞上。
    private boolean isCardSurface(int fill) {
        return fill == surface || fill == surfaceRaised || fill == key || fill == voiceDock;
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

    /** 冰川玻璃（原版默认）：柔光玻璃渐变、极光背景与玻璃语音坞。 */
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

    /** 软色纸卡 · 奶油：暖奶油底、纯白卡片、蓝色主操作。 */
    private static PhoneDeckTheme ivory() {
        return new PhoneDeckTheme(
                IVORY,
                "纸卡 · 奶油",
                "参考图浅色：暖奶油底 · 白卡片 · 柔投影",
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

    /** 软色纸卡 · 云白：清亮白底、白卡片、蓝点缀。 */
    private static PhoneDeckTheme pearl() {
        return new PhoneDeckTheme(
                PEARL,
                "纸卡 · 云白",
                "参考图浅色：清亮白底 · 白卡片 · 蓝点缀",
                true,
                Color.rgb(245, 244, 241),
                Color.rgb(255, 255, 255),
                Color.rgb(240, 239, 236),
                Color.rgb(234, 233, 229),
                Color.rgb(61, 107, 243),
                Color.rgb(46, 86, 208),
                Color.rgb(255, 255, 255),
                Color.rgb(26, 25, 24),
                Color.rgb(138, 136, 131),
                Color.rgb(62, 155, 95),
                Color.rgb(224, 138, 69),
                Color.rgb(217, 84, 72),
                Color.rgb(231, 230, 226),
                Color.rgb(255, 255, 255));
    }

    /** 软色纸卡 · 暖黑：暖黑底、深咖卡片、白色主按钮。 */
    private static PhoneDeckTheme espresso() {
        return new PhoneDeckTheme(
                ESPRESSO,
                "纸卡 · 暖黑",
                "参考图深色：暖黑底 · 深咖卡片 · 白主按钮",
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

    /** 软色纸卡 · 暖灰：暖深灰底、灰咖卡片。 */
    private static PhoneDeckTheme cocoa() {
        return new PhoneDeckTheme(
                COCOA,
                "纸卡 · 暖灰",
                "参考图深色：暖深灰底 · 灰咖卡片 · 白主按钮",
                false,
                Color.rgb(30, 27, 24),
                Color.rgb(45, 42, 37),
                Color.rgb(53, 49, 44),
                Color.rgb(39, 36, 32),
                Color.rgb(241, 238, 230),
                Color.rgb(214, 210, 200),
                Color.rgb(25, 23, 19),
                Color.rgb(241, 238, 230),
                Color.rgb(158, 152, 142),
                Color.rgb(98, 183, 123),
                Color.rgb(233, 162, 102),
                Color.rgb(232, 122, 110),
                Color.rgb(58, 54, 48),
                Color.rgb(45, 42, 37));
    }

    /** 瑞士黑白（设计稿 1）：白纸底、近黑主钮、圆润卡片。 */
    private static PhoneDeckTheme swiss() {
        return new PhoneDeckTheme(
                SWISS,
                "瑞士黑白",
                "设计稿 1 · 白底黑字 · 黑主钮 · 克制排版",
                true,
                Color.rgb(255, 255, 255),
                Color.rgb(250, 250, 250),
                Color.rgb(245, 245, 245),
                Color.rgb(255, 255, 255),
                Color.rgb(10, 10, 10),
                Color.rgb(38, 38, 38),
                Color.WHITE,
                Color.rgb(10, 10, 10),
                Color.rgb(115, 115, 115),
                Color.rgb(21, 128, 61),
                Color.rgb(180, 83, 9),
                Color.rgb(185, 28, 28),
                Color.rgb(212, 212, 212),
                Color.rgb(250, 250, 250),
                16, 0);
    }

    /** 克莱因蓝（设计稿 3）：蓝白科技底、蓝渐变主按钮、柔和圆角。 */
    private static PhoneDeckTheme klein() {
        return new PhoneDeckTheme(
                KLEIN,
                "克莱因蓝",
                "设计稿 3 · 蓝白科技 · 深蓝渐变主钮 · 柔光卡片",
                true,
                Color.rgb(244, 246, 251),
                Color.rgb(255, 255, 255),
                Color.rgb(239, 243, 251),
                Color.rgb(248, 250, 255),
                Color.rgb(37, 99, 235),
                Color.rgb(29, 78, 216),
                Color.WHITE,
                Color.rgb(15, 23, 42),
                Color.rgb(100, 116, 139),
                Color.rgb(5, 150, 105),
                Color.rgb(217, 119, 6),
                Color.rgb(220, 38, 38),
                Color.rgb(203, 213, 225),
                Color.rgb(255, 255, 255),
                16, 0);
    }

    /** 工业沙橙（设计稿 4）：包豪斯暖沙底、橙色点缀、圆润卡片。 */
    private static PhoneDeckTheme bauhaus() {
        return new PhoneDeckTheme(
                BAUHAUS,
                "工业沙橙",
                "设计稿 4 · 暖沙底 · 工业橙点缀 · 柔和卡片",
                true,
                Color.rgb(242, 239, 233),
                Color.rgb(250, 248, 244),
                Color.rgb(240, 237, 229),
                Color.rgb(247, 245, 240),
                Color.rgb(234, 88, 12),
                Color.rgb(194, 65, 12),
                Color.WHITE,
                Color.rgb(30, 29, 27),
                Color.rgb(110, 106, 96),
                Color.rgb(77, 124, 15),
                Color.rgb(217, 119, 6),
                Color.rgb(220, 38, 38),
                Color.rgb(207, 200, 187),
                Color.rgb(232, 228, 220),
                16, 0);
    }

    /** 极简单色（设计稿 2）：纯黑终端、白主钮、发丝边卡片、分类色灰阶。 */
    private static PhoneDeckTheme mono() {
        return new PhoneDeckTheme(
                MONO,
                "极简单色",
                "设计稿 2 · 纯黑终端 · 白主钮 · 分类色灰阶 · 圆润",
                false,
                Color.rgb(0, 0, 0),
                Color.rgb(13, 13, 13),
                Color.rgb(20, 20, 20),
                Color.rgb(15, 15, 15),
                Color.rgb(255, 255, 255),
                Color.rgb(229, 229, 229),
                Color.rgb(0, 0, 0),
                Color.rgb(229, 229, 229),
                Color.rgb(115, 115, 115),
                Color.rgb(52, 211, 153),
                Color.rgb(251, 191, 36),
                Color.rgb(248, 113, 113),
                Color.rgb(41, 41, 41),
                Color.rgb(12, 12, 12),
                16, 1);
    }
}
