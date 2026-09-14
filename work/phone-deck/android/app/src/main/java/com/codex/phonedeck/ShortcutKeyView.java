package com.codex.phonedeck;

import android.content.Context;
import android.graphics.Typeface;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.View;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;

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
        setPadding(dp(12), dp(12), dp(12), dp(12));
        setBackground(theme.pressable(
                context,
                theme.shortcutColor(config.color),
                theme.shortcutPressedColor(config.color),
                12));
        setElevation(theme.isNative() ? 0 : dp(2));

        content = new LinearLayout(context);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setGravity(Gravity.CENTER_VERTICAL | Gravity.START);
        addView(content, new FrameLayout.LayoutParams(
                LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT));

        TextView title = label(config.label, 13,
                theme.text, Typeface.BOLD);
        title.setGravity(Gravity.START);
        title.setMaxLines(2);
        title.setEllipsize(TextUtils.TruncateAt.END);
        content.addView(title, new LinearLayout.LayoutParams(
                LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT));

        TextView chord = label(config.subtitle(), 11,
                theme.isNative() ? theme.muted : theme.shortcutAccent(config.color),
                Typeface.NORMAL);
        chord.setGravity(Gravity.START);
        chord.setMaxLines(1);
        chord.setEllipsize(TextUtils.TruncateAt.END);
        LinearLayout.LayoutParams chordParams = new LinearLayout.LayoutParams(
                LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT);
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
        stateBadge.setTextColor(theme.light ? android.graphics.Color.WHITE : theme.background);
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
