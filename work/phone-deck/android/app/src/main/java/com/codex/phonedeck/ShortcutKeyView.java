package com.codex.phonedeck;

import android.content.Context;
import android.graphics.Typeface;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.View;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;

/// 中性卡面 + 发丝线描边；分类色只出现在标题前的小圆点上，
/// 层级靠卡面灰阶与描边表达，不使用阴影或渐变。
final class ShortcutKeyView extends FrameLayout {
    private final PhoneDeckTheme theme;
    private final TextView stateBadge;
    private final LinearLayout content;
    private final Runnable clearState = this::clearFeedback;

    ShortcutKeyView(Context context, PhoneDeckTheme theme, ShortcutButtonConfig config) {
        super(context);
        this.theme = theme;
        setClickable(true);
        setFocusable(true);
        setSoundEffectsEnabled(true);
        setHapticFeedbackEnabled(true);
        setForegroundGravity(Gravity.CENTER);
        setPadding(dp(10), dp(8), dp(10), dp(8));
        setBackground(theme.pressable(
                context,
                theme.surface,
                theme.surfaceRaised,
                14));

        content = new LinearLayout(context);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setGravity(Gravity.CENTER);
        addView(content, new FrameLayout.LayoutParams(
                LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT));

        LinearLayout titleRow = new LinearLayout(context);
        titleRow.setGravity(Gravity.CENTER_VERTICAL);
        View dot = new View(context);
        dot.setBackground(categoryDot(config.color));
        dot.setContentDescription(config.label + "，分类色：" + config.color);
        titleRow.addView(dot, new LinearLayout.LayoutParams(dp(7), dp(7)));

        TextView title = label(config.label, 14,
                theme.text, Typeface.BOLD);
        title.setGravity(Gravity.CENTER_VERTICAL);
        title.setMaxLines(1);
        title.setEllipsize(TextUtils.TruncateAt.END);
        title.setMaxWidth(dp(110));
        LinearLayout.LayoutParams titleParams = new LinearLayout.LayoutParams(
                LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT);
        titleParams.leftMargin = dp(6);
        titleRow.addView(title, titleParams);
        content.addView(titleRow, new LinearLayout.LayoutParams(
                LayoutParams.WRAP_CONTENT, dp(25)));

        TextView chord = label(config.subtitle(), config.isTextAction() ? 11 : 10,
                config.isTextAction() ? theme.shortcutAccent(config.color) : theme.muted,
                Typeface.BOLD);
        chord.setGravity(Gravity.CENTER);
        chord.setMaxLines(1);
        chord.setEllipsize(TextUtils.TruncateAt.END);
        chord.setPadding(dp(6), 0, dp(6), 0);
        chord.setBackground(theme.shape(context, theme.surfaceRaised, 9));
        LinearLayout.LayoutParams chordParams = new LinearLayout.LayoutParams(
                LayoutParams.WRAP_CONTENT, dp(19));
        chordParams.topMargin = dp(5);
        content.addView(chord, chordParams);

        stateBadge = label("", 12, theme.onPrimary, Typeface.BOLD);
        stateBadge.setGravity(Gravity.CENTER);
        stateBadge.setVisibility(View.GONE);
        FrameLayout.LayoutParams stateParams = new FrameLayout.LayoutParams(dp(25), dp(25),
                Gravity.TOP | Gravity.END);
        stateParams.setMargins(0, dp(-3), dp(-3), 0);
        addView(stateBadge, stateParams);
    }

    private android.graphics.drawable.GradientDrawable categoryDot(String color) {
        android.graphics.drawable.GradientDrawable dot = new android.graphics.drawable.GradientDrawable();
        dot.setColor(theme.shortcutAccent(color));
        dot.setShape(android.graphics.drawable.GradientDrawable.OVAL);
        dot.setSize(dp(7), dp(7));
        return dot;
    }

    void showSending() {
        removeCallbacks(clearState);
        content.animate().alpha(0.72f).setDuration(90).start();
        showBadge("…", theme.warning);
    }

    void showSuccess() {
        removeCallbacks(clearState);
        content.animate().alpha(1f).setDuration(100).start();
        showBadge("✓", theme.success);
        animate().cancel();
        setScaleX(0.97f);
        setScaleY(0.97f);
        animate().scaleX(1f).scaleY(1f).setDuration(180).start();
        postDelayed(clearState, 900);
    }

    void showFailure() {
        removeCallbacks(clearState);
        content.animate().alpha(1f).setDuration(100).start();
        showBadge("!", theme.danger);
        animate().cancel();
        setTranslationX(-dp(5));
        animate().translationX(dp(5)).setDuration(70).withEndAction(() ->
                animate().translationX(0).setDuration(90).start()).start();
        postDelayed(clearState, 1200);
    }

    private void showBadge(String text, int color) {
        stateBadge.setText(text);
        stateBadge.setTextColor(theme.light ? theme.onPrimary : theme.text);
        stateBadge.setBackground(theme.shape(getContext(), color, 13));
        stateBadge.setVisibility(View.VISIBLE);
    }

    private void clearFeedback() {
        stateBadge.setVisibility(View.GONE);
        content.animate().alpha(1f).setDuration(120).start();
        animate().translationX(0).start();
    }

    private TextView label(String value, int size, int color, int style) {
        TextView view = new TextView(getContext());
        view.setText(value);
        view.setTextSize(size);
        view.setTextColor(color);
        view.setTypeface(Typeface.DEFAULT, style);
        return view;
    }

    private int dp(int value) {
        return PhoneDeckTheme.dp(getContext(), value);
    }
}
