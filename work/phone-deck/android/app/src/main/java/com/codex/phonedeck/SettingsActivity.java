package com.codex.phonedeck;

import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.os.Bundle;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.HapticFeedbackConstants;
import android.view.View;
import android.view.WindowManager;
import android.view.animation.AccelerateInterpolator;
import android.view.animation.DecelerateInterpolator;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

/**
 * 设置模块：根列表页（入口 + 当前值摘要 + 内联保活开关）与三个子页
 * （外观主题 / 语音输入 / 快捷键与布局）之间用 FrameLayout 页面栈切换，
 * 返回键先退回根页再退出 Activity。
 */
public final class SettingsActivity extends Activity {
    private static final String PREFS_NAME = "PhoneDeckSettings";
    private static final String PREF_VOICE_WORK_MODE = "voice_work_mode";
    private static final String PREF_VOICE_MODE = "voice_mode";
    private static final String WORK_MANAGED = "managed";
    private static final String WORK_SHARED = "shared";
    private static final String MODE_TAP = "tap";
    private static final String MODE_HOLD = "hold";
    private static final int REQUEST_EXPORT_CONFIG = 4101;
    private static final int REQUEST_IMPORT_CONFIG = 4102;
    private static final long PAGE_SLIDE_MS = 230L;

    private SharedPreferences preferences;
    private PhoneDeckTheme theme;
    private ShortcutConfigRepository repository;

    private FrameLayout pageHost;
    private View rootPage;
    private View themePage;
    private View voicePage;
    private View shortcutsPage;
    private View activeSubPage;
    private boolean pageAnimating;

    private TextView themeSummary;
    private TextView voiceSummary;
    private TextView shortcutSummary;

    private LinearLayout managedOption;
    private LinearLayout sharedOption;
    private TextView managedCheck;
    private TextView sharedCheck;
    private LinearLayout tapOption;
    private LinearLayout holdOption;
    private TextView tapCheck;
    private TextView holdCheck;
    private LinearLayout tapHoldSection;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        theme = PhoneDeckTheme.load(this);
        theme.applyWindow(this);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        preferences = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
        repository = new ShortcutConfigRepository(this);
        setContentView(createInterface());
        refreshSelection();
        refreshRootSummaries();
    }

    @Override
    protected void onResume() {
        super.onResume();
        refreshRootSummaries();
    }

    @Override
    public void onBackPressed() {
        if (activeSubPage != null) {
            popPage();
        } else {
            super.onBackPressed();
        }
    }

    private View createInterface() {
        pageHost = new FrameLayout(this);
        rootPage = buildRootPage();
        themePage = buildThemePage();
        voicePage = buildVoicePage();
        shortcutsPage = buildShortcutsPage();
        pageHost.addView(rootPage, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT));
        return theme.wrapContent(this, pageHost);
    }

    private View buildRootPage() {
        LinearLayout page = pageShell();
        addHeader(page, "设置", view -> finish());

        themeSummary = summaryText();
        page.addView(navRow("🎨", "外观主题", themeSummary,
                () -> pushPage(themePage, true)), fullWidthMargins(dp(16)));

        voiceSummary = summaryText();
        page.addView(navRow("🎤", "语音输入", voiceSummary,
                () -> pushPage(voicePage, true)), fullWidthMargins(dp(10)));

        shortcutSummary = summaryText();
        page.addView(navRow("⌨️", "快捷键与布局", shortcutSummary,
                () -> pushPage(shortcutsPage, true)), fullWidthMargins(dp(10)));

        page.addView(keepAliveRow(), fullWidthMargins(dp(10)));
        return wrapInScroll(page);
    }

    private View buildThemePage() {
        LinearLayout page = pageShell();
        addHeader(page, "外观主题", view -> popPage());

        TextView hint = text(
                "冰川玻璃与纸卡系列，另有瑞士黑白/克莱因蓝/工业沙橙/极简单色四款设计稿风格。",
                13, theme.muted, Typeface.NORMAL);
        hint.setLineSpacing(0, 1.18f);
        page.addView(hint, topMargin(dp(14)));

        for (PhoneDeckTheme candidate : PhoneDeckTheme.all()) {
            page.addView(themeOption(candidate), fullWidthMargins(dp(10)));
        }
        return wrapInScroll(page);
    }

    private View buildVoicePage() {
        LinearLayout page = pageShell();
        addHeader(page, "语音输入", view -> popPage());

        managedOption = option("手机控制听写",
                "手机按钮控制当前电脑的语音输入软件（在电脑端设置中选择引擎），保留点击/按住操作", true);
        managedCheck = (TextView) managedOption.getChildAt(1);
        managedOption.setOnClickListener(view -> selectWorkMode(WORK_MANAGED));
        page.addView(managedOption, fullWidthMargins(dp(14)));

        sharedOption = option("共享麦克风",
                "手机持续向所有在线电脑供音；在每台电脑上用自己的快捷键触发语音软件", false);
        sharedCheck = (TextView) sharedOption.getChildAt(1);
        sharedOption.setOnClickListener(view -> selectWorkMode(WORK_SHARED));
        page.addView(sharedOption, fullWidthMargins(dp(12)));

        tapHoldSection = new LinearLayout(this);
        tapHoldSection.setOrientation(LinearLayout.VERTICAL);

        TextView controlHeading = text("手机控制听写方式", 14, theme.muted, Typeface.BOLD);
        tapHoldSection.addView(controlHeading, topMargin(dp(10)));

        tapOption = option("点击说话", "点击主按钮开始，再点同一按钮停止", true);
        tapCheck = (TextView) tapOption.getChildAt(1);
        tapOption.setOnClickListener(view -> selectMode(MODE_TAP));
        tapHoldSection.addView(tapOption, fullWidthMargins(dp(18)));

        holdOption = option("按住说话", "按下立即开始，松开后自动停止并输入文字", false);
        holdCheck = (TextView) holdOption.getChildAt(1);
        holdOption.setOnClickListener(view -> selectMode(MODE_HOLD));
        tapHoldSection.addView(holdOption, fullWidthMargins(dp(12)));

        TextView tip = text("提示：点击模式适合长内容，使用同一个主按钮开始和停止；长按模式更适合短句。",
                13, theme.muted, Typeface.NORMAL);
        tip.setLineSpacing(0, 1.18f);
        tip.setPadding(dp(14), dp(13), dp(14), dp(13));
        tip.setBackground(roundRect(theme.surface, 14, 1, theme.outline));
        tapHoldSection.addView(tip, fullWidthMargins(dp(20)));

        page.addView(tapHoldSection);
        return wrapInScroll(page);
    }

    private View buildShortcutsPage() {
        LinearLayout page = pageShell();
        addHeader(page, "快捷键与布局", view -> popPage());

        TextView hint = text("编辑手机上的按钮、按键、颜色和顺序；配置可导出为 JSON，在其他设备导入。",
                13, theme.muted, Typeface.NORMAL);
        hint.setLineSpacing(0, 1.18f);
        page.addView(hint, topMargin(dp(14)));

        Button shortcuts = new Button(this);
        shortcuts.setText("编辑按钮、按键、颜色和顺序  →");
        shortcuts.setTextSize(15);
        shortcuts.setTextColor(theme.text);
        shortcuts.setAllCaps(false);
        shortcuts.setGravity(Gravity.CENTER_VERTICAL);
        shortcuts.setPadding(dp(18), 0, dp(18), 0);
        shortcuts.setBackground(roundRect(
                theme.feedbackSurface(theme.primary), 16, 1, theme.primary));
        shortcuts.setOnClickListener(view ->
                startActivity(new Intent(this, ShortcutSettingsActivity.class)));
        LinearLayout.LayoutParams shortcutParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(60));
        shortcutParams.topMargin = dp(16);
        page.addView(shortcuts, shortcutParams);

        LinearLayout transferRow = new LinearLayout(this);
        transferRow.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams exportParams = new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.MATCH_PARENT, 1f);
        Button exportButton = button("导出配置");
        exportButton.setOnClickListener(view -> {
            Intent intent = new Intent(Intent.ACTION_CREATE_DOCUMENT);
            intent.addCategory(Intent.CATEGORY_OPENABLE);
            intent.setType("application/json");
            intent.putExtra(Intent.EXTRA_TITLE, "phonedeck-shortcuts.json");
            startActivityForResult(intent, REQUEST_EXPORT_CONFIG);
        });
        transferRow.addView(exportButton, exportParams);
        Button importButton = button("导入配置");
        LinearLayout.LayoutParams importParams = new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.MATCH_PARENT, 1f);
        importParams.leftMargin = dp(10);
        importButton.setOnClickListener(view -> {
            Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT);
            intent.addCategory(Intent.CATEGORY_OPENABLE);
            intent.setType("*/*");
            intent.putExtra(Intent.EXTRA_MIME_TYPES, new String[]{"application/json", "text/*"});
            startActivityForResult(intent, REQUEST_IMPORT_CONFIG);
        });
        transferRow.addView(importButton, importParams);
        page.addView(transferRow, fullWidthMargins(dp(10)));
        return wrapInScroll(page);
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (resultCode != RESULT_OK || data == null || data.getData() == null) {
            return;
        }
        android.net.Uri uri = data.getData();
        if (requestCode == REQUEST_EXPORT_CONFIG) {
            String json = repository.exportJson();
            if (json == null) {
                Toast.makeText(this, "导出失败：本地还没有配置文件", Toast.LENGTH_LONG).show();
                return;
            }
            try (java.io.OutputStream output = getContentResolver().openOutputStream(uri)) {
                if (output == null) {
                    throw new IllegalStateException("无法打开目标文件");
                }
                output.write(json.getBytes(java.nio.charset.StandardCharsets.UTF_8));
                Toast.makeText(this, "配置已导出", Toast.LENGTH_SHORT).show();
            } catch (Exception exception) {
                Toast.makeText(this, "导出失败：" + exception.getMessage(), Toast.LENGTH_LONG).show();
            }
        } else if (requestCode == REQUEST_IMPORT_CONFIG) {
            try (java.io.InputStream input = getContentResolver().openInputStream(uri);
                 java.io.ByteArrayOutputStream buffer = new java.io.ByteArrayOutputStream()) {
                byte[] chunk = new byte[8192];
                int read;
                while ((read = input.read(chunk)) > 0) {
                    buffer.write(chunk, 0, read);
                }
                String json = buffer.toString("UTF-8");
                if (repository.importJson(json)) {
                    Toast.makeText(this, "配置已导入并生效", Toast.LENGTH_SHORT).show();
                    refreshRootSummaries();
                } else {
                    Toast.makeText(this, "导入失败：文件无效，原配置未改动", Toast.LENGTH_LONG).show();
                }
            } catch (Exception exception) {
                Toast.makeText(this, "导入失败：" + exception.getMessage(), Toast.LENGTH_LONG).show();
            }
        }
    }

    private void pushPage(View page, boolean animated) {
        if (activeSubPage != null || pageAnimating) {
            return;
        }
        activeSubPage = page;
        FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT);
        if (!animated) {
            pageHost.addView(page, params);
            page.bringToFront();
            return;
        }
        pageAnimating = true;
        float slide = slideDistance();
        page.setTranslationX(slide);
        pageHost.addView(page, params);
        page.bringToFront();
        page.animate().translationX(0f).setDuration(PAGE_SLIDE_MS)
                .setInterpolator(new DecelerateInterpolator())
                .withEndAction(() -> pageAnimating = false);
        rootPage.animate().translationX(-slide * 0.22f).alpha(0.35f)
                .setDuration(PAGE_SLIDE_MS)
                .setInterpolator(new DecelerateInterpolator());
    }

    private void popPage() {
        if (activeSubPage == null || pageAnimating) {
            return;
        }
        View page = activeSubPage;
        activeSubPage = null;
        pageAnimating = true;
        float slide = slideDistance();
        page.animate().translationX(slide).setDuration(PAGE_SLIDE_MS)
                .setInterpolator(new AccelerateInterpolator())
                .withEndAction(() -> {
                    pageHost.removeView(page);
                    pageAnimating = false;
                });
        rootPage.animate().translationX(0f).alpha(1f).setDuration(PAGE_SLIDE_MS)
                .setInterpolator(new AccelerateInterpolator());
        refreshRootSummaries();
    }

    private float slideDistance() {
        int width = pageHost.getWidth();
        return width > 0 ? width : getResources().getDisplayMetrics().widthPixels;
    }

    private void addHeader(LinearLayout page, String title, View.OnClickListener onBack) {
        LinearLayout header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);

        Button back = new Button(this);
        back.setText("←");
        back.setTextSize(22);
        back.setTextColor(theme.text);
        back.setBackground(roundRect(theme.surface, 14, 1, theme.outline));
        back.setOnClickListener(onBack);
        header.addView(back, new LinearLayout.LayoutParams(dp(48), dp(44)));

        TextView titleView = text(title, 22, theme.text, Typeface.BOLD);
        LinearLayout.LayoutParams titleParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        titleParams.leftMargin = dp(13);
        header.addView(titleView, titleParams);

        page.addView(header, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(48)));
    }

    private LinearLayout navRow(String glyph, String title, TextView summary, Runnable open) {
        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(dp(14), dp(13), dp(14), dp(13));
        row.setBackground(roundRect(theme.surface, 18, 1, theme.outline));
        row.addView(iconTile(glyph), new LinearLayout.LayoutParams(dp(42), dp(42)));

        LinearLayout copy = new LinearLayout(this);
        copy.setOrientation(LinearLayout.VERTICAL);
        LinearLayout.LayoutParams copyParams = new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        copyParams.leftMargin = dp(13);
        copy.addView(text(title, 17, theme.text, Typeface.BOLD));
        if (summary != null) {
            copy.addView(summary, topMargin(dp(3)));
        }
        row.addView(copy, copyParams);

        TextView chevron = text("›", 22, theme.muted, Typeface.BOLD);
        chevron.setGravity(Gravity.CENTER);
        row.addView(chevron, new LinearLayout.LayoutParams(dp(24), dp(28)));

        installPressFeedback(row);
        row.setOnClickListener(view -> {
            confirmHaptic(row);
            open.run();
        });
        return row;
    }

    private LinearLayout iconTile(String glyph) {
        LinearLayout tile = new LinearLayout(this);
        tile.setGravity(Gravity.CENTER);
        tile.setBackground(roundRect(
                theme.feedbackSurface(theme.primary), 13, 1, theme.primary));
        TextView glyphText = text(glyph, 17, theme.text, Typeface.NORMAL);
        tile.addView(glyphText, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT,
                LinearLayout.LayoutParams.WRAP_CONTENT));
        return tile;
    }

    private LinearLayout keepAliveRow() {
        boolean enabled = preferences.getBoolean("keep_connection_alive", true);

        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(dp(14), dp(13), dp(14), dp(13));
        row.setBackground(roundRect(
                enabled ? theme.feedbackSurface(theme.primary) : theme.surface,
                18, enabled ? 2 : 1,
                enabled ? theme.primary : theme.outline));
        row.addView(iconTile("📡"), new LinearLayout.LayoutParams(dp(42), dp(42)));

        LinearLayout copy = new LinearLayout(this);
        copy.setOrientation(LinearLayout.VERTICAL);
        LinearLayout.LayoutParams copyParams = new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        copyParams.leftMargin = dp(13);
        row.addView(copy, copyParams);

        copy.addView(text("Wi-Fi 保活", 17, theme.text, Typeface.BOLD));
        TextView detail = text(
                "App 运行期间保持 Wi-Fi 高性能模式，降低灭屏或省电导致的无线断流；"
                        + "USB 断连由电脑端看门狗自动恢复。",
                12, theme.muted, Typeface.NORMAL);
        detail.setLineSpacing(0, 1.12f);
        copy.addView(detail, topMargin(dp(4)));

        Switch switchView = new Switch(this);
        switchView.setChecked(enabled);
        switchView.setContentDescription("Wi-Fi 保活开关");
        switchView.setOnCheckedChangeListener((view, checked) -> {
            preferences.edit().putBoolean("keep_connection_alive", checked).apply();
            row.setBackground(roundRect(
                    checked ? theme.feedbackSurface(theme.primary) : theme.surface,
                    18, checked ? 2 : 1,
                    checked ? theme.primary : theme.outline));
            row.announceForAccessibility(checked
                    ? "已开启 Wi-Fi 保活" : "已关闭 Wi-Fi 保活");
        });
        row.addView(switchView, new LinearLayout.LayoutParams(
                android.view.ViewGroup.LayoutParams.WRAP_CONTENT,
                android.view.ViewGroup.LayoutParams.WRAP_CONTENT));
        return row;
    }

    private void refreshRootSummaries() {
        if (themeSummary == null) {
            return;
        }
        themeSummary.setText(theme.name);
        boolean sharedSelected = WORK_SHARED.equals(
                preferences.getString(PREF_VOICE_WORK_MODE, WORK_MANAGED));
        boolean holdSelected = MODE_HOLD.equals(
                preferences.getString(PREF_VOICE_MODE, MODE_TAP));
        voiceSummary.setText(sharedSelected ? "共享麦克风"
                : "手机控制 · " + (holdSelected ? "按住说话" : "点击说话"));
        shortcutSummary.setText(repository.load().size() + " 个按钮");
    }

    private TextView summaryText() {
        TextView view = text("", 13, theme.muted, Typeface.NORMAL);
        view.setMaxLines(1);
        view.setEllipsize(TextUtils.TruncateAt.END);
        return view;
    }

    private LinearLayout pageShell() {
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(20), dp(20), dp(20), dp(24));
        page.setBackgroundColor(theme.contentBackground());
        return page;
    }

    private View wrapInScroll(LinearLayout page) {
        ScrollView scroll = new ScrollView(this);
        scroll.addView(page);
        return scroll;
    }

    private void confirmHaptic(View view) {
        view.performHapticFeedback(Build.VERSION.SDK_INT >= Build.VERSION_CODES.R
                ? HapticFeedbackConstants.CONFIRM
                : HapticFeedbackConstants.VIRTUAL_KEY);
    }

    private void selectMode(String mode) {
        preferences.edit().putString(PREF_VOICE_MODE, mode).apply();
        refreshSelection();
        refreshRootSummaries();
        View selected = MODE_HOLD.equals(mode) ? holdOption : tapOption;
        confirmHaptic(selected);
        selected.announceForAccessibility(MODE_HOLD.equals(mode)
                ? "已选择按住说话模式" : "已选择点击说话模式");
    }

    private void selectWorkMode(String mode) {
        String selectedMode = WORK_SHARED.equals(mode) ? WORK_SHARED : WORK_MANAGED;
        preferences.edit().putString(PREF_VOICE_WORK_MODE, selectedMode).apply();
        if (WORK_MANAGED.equals(selectedMode)) {
            stopSharedMicrophone();
        }
        refreshSelection();
        refreshRootSummaries();
        View selected = WORK_SHARED.equals(selectedMode) ? sharedOption : managedOption;
        confirmHaptic(selected);
        selected.announceForAccessibility(WORK_SHARED.equals(selectedMode)
                ? "已选择共享麦克风模式，需要返回主界面手动开启"
                : "已选择手机控制听写模式");
    }

    private void stopSharedMicrophone() {
        Intent stop = new Intent(this, PhoneAudioService.class);
        stop.setAction(PhoneAudioService.ACTION_STOP);
        startService(stop);
    }

    private void refreshSelection() {
        boolean sharedSelected = WORK_SHARED.equals(
                preferences.getString(PREF_VOICE_WORK_MODE, WORK_MANAGED));
        managedOption.setBackground(roundRect(
                sharedSelected ? theme.surface : theme.feedbackSurface(theme.primary),
                18, sharedSelected ? 1 : 2,
                sharedSelected ? theme.outline : theme.primary));
        sharedOption.setBackground(roundRect(
                sharedSelected ? theme.feedbackSurface(theme.warning) : theme.surface,
                18, sharedSelected ? 2 : 1,
                sharedSelected ? theme.warning : theme.outline));
        managedCheck.setText(sharedSelected ? "○" : "✓");
        managedCheck.setTextColor(sharedSelected ? theme.muted : theme.primary);
        sharedCheck.setText(sharedSelected ? "✓" : "○");
        sharedCheck.setTextColor(sharedSelected ? theme.warning : theme.muted);

        boolean holdSelected = MODE_HOLD.equals(
                preferences.getString(PREF_VOICE_MODE, MODE_TAP));
        tapOption.setBackground(roundRect(
                holdSelected ? theme.surface : theme.feedbackSurface(theme.primary),
                18, holdSelected ? 1 : 2,
                holdSelected ? theme.outline : theme.primary));
        holdOption.setBackground(roundRect(
                holdSelected ? theme.feedbackSurface(theme.warning) : theme.surface,
                18, holdSelected ? 2 : 1,
                holdSelected ? theme.warning : theme.outline));
        tapCheck.setText(holdSelected ? "○" : "✓");
        tapCheck.setTextColor(holdSelected ? theme.muted : theme.primary);
        holdCheck.setText(holdSelected ? "✓" : "○");
        holdCheck.setTextColor(holdSelected ? theme.warning : theme.muted);
        tapOption.setEnabled(!sharedSelected);
        holdOption.setEnabled(!sharedSelected);
        tapOption.setAlpha(sharedSelected ? 0.48f : 1f);
        holdOption.setAlpha(sharedSelected ? 0.48f : 1f);
        tapHoldSection.setVisibility(sharedSelected ? View.GONE : View.VISIBLE);
    }

    private View themeOption(PhoneDeckTheme candidate) {
        boolean selected = candidate.id.equals(theme.id);
        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(dp(12), dp(12), dp(12), dp(12));
        row.setBackground(roundRect(
                selected ? theme.feedbackSurface(theme.primary) : theme.surface,
                17, selected ? 2 : 1, selected ? theme.primary : theme.outline));
        row.setContentDescription(candidate.name + "，" + candidate.description
                + (selected ? "，当前主题" : ""));

        LinearLayout preview = new LinearLayout(this);
        preview.setOrientation(LinearLayout.VERTICAL);
        preview.setPadding(dp(7), dp(7), dp(7), dp(7));
        preview.setBackground(candidate.shape(this, candidate.background, 12, 1,
                candidate.outline));
        View primary = new View(this);
        primary.setBackground(candidate.shape(this, candidate.primary, 6));
        preview.addView(primary, new LinearLayout.LayoutParams(dp(46), dp(13)));
        LinearLayout keys = new LinearLayout(this);
        LinearLayout.LayoutParams keysParams = new LinearLayout.LayoutParams(dp(46), dp(17));
        keysParams.topMargin = dp(5);
        preview.addView(keys, keysParams);
        for (int index = 0; index < 3; index++) {
            View key = new View(this);
            key.setBackground(candidate.shape(this,
                    index == 1 ? candidate.shortcutColor("green") : candidate.key, 5));
            LinearLayout.LayoutParams keyParams = new LinearLayout.LayoutParams(0, dp(17), 1f);
            if (index > 0) {
                keyParams.leftMargin = dp(3);
            }
            keys.addView(key, keyParams);
        }
        row.addView(preview, new LinearLayout.LayoutParams(dp(60), dp(56)));

        LinearLayout copy = new LinearLayout(this);
        copy.setOrientation(LinearLayout.VERTICAL);
        copy.addView(text(candidate.name, 15, theme.text, Typeface.BOLD));
        TextView detail = text(candidate.description, 12, theme.muted, Typeface.NORMAL);
        detail.setMaxLines(2);
        copy.addView(detail, topMargin(dp(3)));
        LinearLayout.LayoutParams copyParams = new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        copyParams.leftMargin = dp(12);
        row.addView(copy, copyParams);

        TextView check = text(selected ? "✓" : "○", 20,
                selected ? theme.primary : theme.muted, Typeface.BOLD);
        check.setGravity(Gravity.CENTER);
        row.addView(check, new LinearLayout.LayoutParams(dp(34), dp(34)));
        installPressFeedback(row);
        row.setOnClickListener(view -> selectTheme(candidate.id, row));
        return row;
    }

    private void selectTheme(String themeId, View selected) {
        if (theme.id.equals(themeId)) {
            return;
        }
        PhoneDeckTheme.save(this, themeId);
        confirmHaptic(selected);
        selected.announceForAccessibility("主题已切换");
        theme = PhoneDeckTheme.load(this);
        theme.applyWindow(this);
        activeSubPage = null;
        pageAnimating = false;
        setContentView(createInterface());
        refreshSelection();
        refreshRootSummaries();
        pushPage(themePage, false);
    }

    private void installPressFeedback(View row) {
        row.setHapticFeedbackEnabled(true);
        row.setOnTouchListener((view, event) -> {
            if (event.getActionMasked() == android.view.MotionEvent.ACTION_DOWN
                    && view.isEnabled()) {
                view.animate().scaleX(0.985f).scaleY(0.985f).setDuration(55).start();
            } else if (event.getActionMasked() == android.view.MotionEvent.ACTION_UP
                    || event.getActionMasked() == android.view.MotionEvent.ACTION_CANCEL) {
                view.animate().scaleX(1f).scaleY(1f).setDuration(90).start();
            }
            return false;
        });
    }

    private Button button(String label) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextSize(15);
        button.setTextColor(theme.text);
        button.setAllCaps(false);
        button.setGravity(Gravity.CENTER);
        button.setBackground(roundRect(theme.surfaceRaised, 16, 1, theme.outline));
        return button;
    }

    private LinearLayout option(String title, String detail, boolean tap) {
        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(dp(18), dp(17), dp(16), dp(17));

        LinearLayout copy = new LinearLayout(this);
        copy.setOrientation(LinearLayout.VERTICAL);
        row.addView(copy, new LinearLayout.LayoutParams(0,
                LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        copy.addView(text(title, 18, theme.text, Typeface.BOLD));
        TextView detailText = text(detail, 13, theme.muted, Typeface.NORMAL);
        detailText.setLineSpacing(0, 1.12f);
        copy.addView(detailText, topMargin(dp(5)));

        TextView check = text(tap ? "✓" : "○", 22, theme.primary, Typeface.BOLD);
        check.setGravity(Gravity.CENTER);
        row.addView(check, new LinearLayout.LayoutParams(dp(38), dp(38)));
        return row;
    }

    private TextView text(String value, int size, int color, int style) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(size);
        view.setTextColor(color);
        view.setTypeface(Typeface.DEFAULT, style);
        return view;
    }

    private GradientDrawable roundRect(int fill, int radius, int strokeWidth, int strokeColor) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(fill);
        drawable.setCornerRadius(dp(radius));
        drawable.setStroke(dp(strokeWidth), strokeColor);
        return drawable;
    }

    private LinearLayout.LayoutParams topMargin(int top) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        params.topMargin = top;
        return params;
    }

    private LinearLayout.LayoutParams fullWidthMargins(int top) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        params.topMargin = top;
        return params;
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
