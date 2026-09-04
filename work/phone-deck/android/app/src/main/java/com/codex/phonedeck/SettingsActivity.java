package com.codex.phonedeck;

import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.os.Bundle;
import android.view.Gravity;
import android.view.HapticFeedbackConstants;
import android.view.View;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

public final class SettingsActivity extends Activity {
    private static final String PREFS_NAME = "PhoneDeckSettings";
    private static final String PREF_VOICE_WORK_MODE = "voice_work_mode";
    private static final String PREF_VOICE_MODE = "voice_mode";
    private static final String WORK_MANAGED = "managed";
    private static final String WORK_SHARED = "shared";
    private static final String MODE_TAP = "tap";
    private static final String MODE_HOLD = "hold";
    private SharedPreferences preferences;
    private PhoneDeckTheme theme;
    private LinearLayout managedOption;
    private LinearLayout sharedOption;
    private TextView managedCheck;
    private TextView sharedCheck;
    private LinearLayout tapOption;
    private LinearLayout holdOption;
    private TextView tapCheck;
    private TextView holdCheck;
    private ShortcutConfigRepository repository;
    private static final int REQUEST_EXPORT_CONFIG = 4101;
    private static final int REQUEST_IMPORT_CONFIG = 4102;

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
    }

    private View createInterface() {
        ScrollView scroll = new ScrollView(this);
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(20), dp(20), dp(20), dp(24));
        page.setBackgroundColor(theme.contentBackground());
        scroll.addView(page);

        LinearLayout header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);
        page.addView(header, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(48)));

        Button back = new Button(this);
        back.setText("←");
        back.setTextSize(22);
        back.setTextColor(theme.text);
        back.setBackground(roundRect(theme.surface, 14, 1, theme.outline));
        back.setOnClickListener(view -> finish());
        header.addView(back, new LinearLayout.LayoutParams(dp(48), dp(44)));

        TextView headerTitle = text("设置", 22, theme.text, Typeface.BOLD);
        LinearLayout.LayoutParams titleParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        titleParams.leftMargin = dp(13);
        header.addView(headerTitle, titleParams);

        TextView intro = text("两套语音工作方式互斥切换；快捷键和布局可单独编辑。",
                14, theme.muted, Typeface.NORMAL);
        page.addView(intro, topMargin(dp(22)));

        TextView appearanceHeading = text("外观主题", 17, theme.text, Typeface.BOLD);
        page.addView(appearanceHeading, topMargin(dp(24)));
        TextView appearanceHint = text("统一使用冰川玻璃，应用到主界面、语音区和所有编辑页面。",
                12, theme.muted, Typeface.NORMAL);
        page.addView(appearanceHint, topMargin(dp(4)));
        for (PhoneDeckTheme candidate : PhoneDeckTheme.all()) {
            page.addView(themeOption(candidate), fullWidthMargins(dp(10)));
        }

        TextView voiceHeading = text("语音输入", 17, theme.text, Typeface.BOLD);
        page.addView(voiceHeading, topMargin(dp(28)));

        managedOption = option("手机控制听写",
                "手机按钮控制当前电脑的 Typeless，保留听写、翻译、问答和点击/按住操作", true);
        managedCheck = (TextView) managedOption.getChildAt(1);
        managedOption.setOnClickListener(view -> selectWorkMode(WORK_MANAGED));
        page.addView(managedOption, fullWidthMargins(dp(18)));

        sharedOption = option("共享麦克风",
                "手机持续向所有在线电脑供音；在每台电脑上用自己的快捷键触发 Typeless", false);
        sharedCheck = (TextView) sharedOption.getChildAt(1);
        sharedOption.setOnClickListener(view -> selectWorkMode(WORK_SHARED));
        page.addView(sharedOption, fullWidthMargins(dp(12)));

        TextView controlHeading = text("手机控制听写方式", 14, theme.muted, Typeface.BOLD);
        page.addView(controlHeading, topMargin(dp(24)));

        tapOption = option("点击说话", "点击主按钮开始，再点同一按钮停止", true);
        tapCheck = (TextView) tapOption.getChildAt(1);
        tapOption.setOnClickListener(view -> selectMode(MODE_TAP));
        page.addView(tapOption, fullWidthMargins(dp(18)));

        holdOption = option("按住说话", "按下立即开始，松开后自动停止并输入文字", false);
        holdCheck = (TextView) holdOption.getChildAt(1);
        holdOption.setOnClickListener(view -> selectMode(MODE_HOLD));
        page.addView(holdOption, fullWidthMargins(dp(12)));

        TextView tip = text("提示：点击模式适合长内容，使用同一个主按钮开始和停止；长按模式更适合短句。",
                13, theme.muted, Typeface.NORMAL);
        tip.setLineSpacing(0, 1.18f);
        tip.setPadding(dp(14), dp(13), dp(14), dp(13));
        tip.setBackground(roundRect(theme.surface, 14, 1, theme.outline));
        page.addView(tip, fullWidthMargins(dp(22)));

        TextView connectionHeading = text("连接保持", 17, theme.text, Typeface.BOLD);
        page.addView(connectionHeading, topMargin(dp(28)));
        page.addView(keepAliveOption(), fullWidthMargins(dp(18)));

        TextView shortcutHeading = text("快捷键与布局", 17, theme.text, Typeface.BOLD);
        page.addView(shortcutHeading, topMargin(dp(28)));

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
        page.addView(shortcuts, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(60)));

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
        page.addView(transferRow, topMargin(dp(10)));
        return theme.wrapContent(this, scroll);
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
                } else {
                    Toast.makeText(this, "导入失败：文件无效，原配置未改动", Toast.LENGTH_LONG).show();
                }
            } catch (Exception exception) {
                Toast.makeText(this, "导入失败：" + exception.getMessage(), Toast.LENGTH_LONG).show();
            }
        }
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

    private LinearLayout keepAliveOption() {
        boolean enabled = preferences.getBoolean("keep_connection_alive", true);

        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(dp(18), dp(17), dp(16), dp(17));
        row.setBackground(roundRect(
                enabled ? theme.feedbackSurface(theme.primary) : theme.surface,
                18, enabled ? 2 : 1,
                enabled ? theme.primary : theme.outline));

        LinearLayout copy = new LinearLayout(this);
        copy.setOrientation(LinearLayout.VERTICAL);
        row.addView(copy, new LinearLayout.LayoutParams(0,
                LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        copy.addView(text("Wi-Fi 保活", 18, theme.text, Typeface.BOLD));
        TextView detail = text(
                "App 运行期间保持 Wi-Fi 高性能模式，降低灭屏或省电导致的无线断流；"
                        + "USB 断连由电脑端看门狗自动恢复。",
                13, theme.muted, Typeface.NORMAL);
        detail.setLineSpacing(0, 1.12f);
        copy.addView(detail, topMargin(dp(5)));

        android.widget.Switch switchView = new android.widget.Switch(this);
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

    private void selectMode(String mode) {
        preferences.edit().putString(PREF_VOICE_MODE, mode).apply();
        refreshSelection();
        View selected = MODE_HOLD.equals(mode) ? holdOption : tapOption;
        selected.performHapticFeedback(Build.VERSION.SDK_INT >= Build.VERSION_CODES.R
                ? HapticFeedbackConstants.CONFIRM
                : HapticFeedbackConstants.VIRTUAL_KEY);
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
        View selected = WORK_SHARED.equals(selectedMode) ? sharedOption : managedOption;
        selected.performHapticFeedback(Build.VERSION.SDK_INT >= Build.VERSION_CODES.R
                ? HapticFeedbackConstants.CONFIRM
                : HapticFeedbackConstants.VIRTUAL_KEY);
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
        row.setOnClickListener(view -> selectTheme(candidate.id, row));
        return row;
    }

    private void selectTheme(String themeId, View selected) {
        if (theme.id.equals(themeId)) {
            return;
        }
        PhoneDeckTheme.save(this, themeId);
        selected.performHapticFeedback(Build.VERSION.SDK_INT >= Build.VERSION_CODES.R
                ? HapticFeedbackConstants.CONFIRM
                : HapticFeedbackConstants.VIRTUAL_KEY);
        selected.announceForAccessibility("主题已切换");
        recreate();
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
