package com.codex.phonedeck;

import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Color;
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

public final class SettingsActivity extends Activity {
    private static final String PREFS_NAME = "PhoneDeckSettings";
    private static final String PREF_VOICE_MODE = "voice_mode";
    private static final String MODE_TAP = "tap";
    private static final String MODE_HOLD = "hold";
    private static final int BACKGROUND = Color.rgb(11, 16, 32);
    private static final int PANEL = Color.rgb(22, 29, 50);
    private static final int PRIMARY = Color.rgb(121, 168, 255);
    private static final int TEXT = Color.rgb(247, 249, 255);
    private static final int MUTED = Color.rgb(159, 172, 202);
    private static final int PENDING = Color.rgb(255, 195, 92);

    private SharedPreferences preferences;
    private LinearLayout tapOption;
    private LinearLayout holdOption;
    private TextView tapCheck;
    private TextView holdCheck;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().setStatusBarColor(BACKGROUND);
        getWindow().setNavigationBarColor(BACKGROUND);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        preferences = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
        setContentView(createInterface());
        refreshSelection();
    }

    private View createInterface() {
        ScrollView scroll = new ScrollView(this);
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(20), dp(20), dp(20), dp(24));
        page.setBackgroundColor(BACKGROUND);
        scroll.addView(page);

        LinearLayout header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);
        page.addView(header, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(48)));

        Button back = new Button(this);
        back.setText("←");
        back.setTextSize(22);
        back.setTextColor(TEXT);
        back.setBackground(roundRect(PANEL, 14, 1, PANEL));
        back.setOnClickListener(view -> finish());
        header.addView(back, new LinearLayout.LayoutParams(dp(48), dp(44)));

        TextView headerTitle = text("设置", 22, TEXT, Typeface.BOLD);
        LinearLayout.LayoutParams titleParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        titleParams.leftMargin = dp(13);
        header.addView(headerTitle, titleParams);

        TextView intro = text("语音模式会立即保存；快捷键和布局可单独编辑。",
                14, MUTED, Typeface.NORMAL);
        page.addView(intro, topMargin(dp(22)));

        TextView voiceHeading = text("语音输入", 17, TEXT, Typeface.BOLD);
        page.addView(voiceHeading, topMargin(dp(22)));

        tapOption = option("点击说话", "点击主按钮开始，再点同一按钮停止；可暂停或继续", true);
        tapCheck = (TextView) tapOption.getChildAt(1);
        tapOption.setOnClickListener(view -> selectMode(MODE_TAP));
        page.addView(tapOption, fullWidthMargins(dp(18)));

        holdOption = option("按住说话", "按下立即开始，松开后自动停止并输入文字", false);
        holdCheck = (TextView) holdOption.getChildAt(1);
        holdOption.setOnClickListener(view -> selectMode(MODE_HOLD));
        page.addView(holdOption, fullWidthMargins(dp(12)));

        TextView tip = text("提示：点击模式适合长内容，暂停时会停止采集麦克风；长按模式更适合短句。",
                13, MUTED, Typeface.NORMAL);
        tip.setLineSpacing(0, 1.18f);
        tip.setPadding(dp(14), dp(13), dp(14), dp(13));
        tip.setBackground(roundRect(PANEL, 14, 1, PANEL));
        page.addView(tip, fullWidthMargins(dp(22)));

        TextView shortcutHeading = text("快捷键与布局", 17, TEXT, Typeface.BOLD);
        page.addView(shortcutHeading, topMargin(dp(28)));

        Button shortcuts = new Button(this);
        shortcuts.setText("编辑按钮、按键、颜色和顺序  →");
        shortcuts.setTextSize(15);
        shortcuts.setTextColor(TEXT);
        shortcuts.setAllCaps(false);
        shortcuts.setGravity(Gravity.CENTER_VERTICAL);
        shortcuts.setPadding(dp(18), 0, dp(18), 0);
        shortcuts.setBackground(roundRect(Color.rgb(27, 45, 76), 16, 1, PRIMARY));
        shortcuts.setOnClickListener(view ->
                startActivity(new Intent(this, ShortcutSettingsActivity.class)));
        page.addView(shortcuts, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(60)));
        return scroll;
    }

    private LinearLayout option(String title, String detail, boolean tap) {
        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(dp(18), dp(17), dp(16), dp(17));

        LinearLayout copy = new LinearLayout(this);
        copy.setOrientation(LinearLayout.VERTICAL);
        row.addView(copy, new LinearLayout.LayoutParams(0,
                LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        copy.addView(text(title, 18, TEXT, Typeface.BOLD));
        TextView detailText = text(detail, 13, MUTED, Typeface.NORMAL);
        detailText.setLineSpacing(0, 1.12f);
        copy.addView(detailText, topMargin(dp(5)));

        TextView check = text(tap ? "✓" : "○", 22, PRIMARY, Typeface.BOLD);
        check.setGravity(Gravity.CENTER);
        row.addView(check, new LinearLayout.LayoutParams(dp(38), dp(38)));
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

    private void refreshSelection() {
        boolean holdSelected = MODE_HOLD.equals(
                preferences.getString(PREF_VOICE_MODE, MODE_TAP));
        tapOption.setBackground(roundRect(holdSelected ? PANEL : Color.rgb(27, 45, 76),
                18, holdSelected ? 1 : 2, holdSelected ? PANEL : PRIMARY));
        holdOption.setBackground(roundRect(holdSelected ? Color.rgb(62, 48, 28) : PANEL,
                18, holdSelected ? 2 : 1, holdSelected ? PENDING : PANEL));
        tapCheck.setText(holdSelected ? "○" : "✓");
        tapCheck.setTextColor(holdSelected ? MUTED : PRIMARY);
        holdCheck.setText(holdSelected ? "✓" : "○");
        holdCheck.setTextColor(holdSelected ? PENDING : MUTED);
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
