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
        setPadding(dp(10), dp(8), dp(10), dp(8));
        setBackground(theme.pressable(
                context,
                theme.shortcutColor(config.color),
                theme.shortcutPressedColor(config.color),
                17));
        setElevation(dp(theme.isFrost() ? 5 : theme.light ? 1 : 2));

        content = new LinearLayout(context);
        content.setOrientation(theme.isFrost() ? LinearLayout.HORIZONTAL : LinearLayout.VERTICAL);
        content.setGravity(Gravity.CENTER);
        addView(content, new FrameLayout.LayoutParams(
                LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT));

        String iconValue = config.icon.isEmpty() ? "·" : config.icon;
        TextView icon = label(iconValue,
                config.icon.isEmpty() ? 12
                        : theme.isFrost() && iconValue.length() > 2 ? 12
                        : theme.isFrost() ? 21 : 19,
                config.icon.isEmpty() ? theme.muted
                        : theme.isFrost() ? theme.shortcutAccent(config.color) : theme.text,
                Typeface.BOLD);
        icon.setGravity(Gravity.CENTER);
        icon.setMaxLines(1);
        if (theme.isFrost()) {
            icon.setBackground(theme.shape(context,
                    theme.feedbackSurface(theme.shortcutAccent(config.color)), 13,
                    1, theme.outline));
            LinearLayout.LayoutParams iconParams = new LinearLayout.LayoutParams(dp(34), dp(46));
            iconParams.rightMargin = dp(6);
            content.addView(icon, iconParams);
        } else {
            content.addView(icon, new LinearLayout.LayoutParams(
                    LayoutParams.MATCH_PARENT, dp(25)));
        }

        LinearLayout copy = theme.isFrost() ? new LinearLayout(context) : content;
        if (theme.isFrost()) {
            copy.setOrientation(LinearLayout.VERTICAL);
            copy.setGravity(Gravity.CENTER_VERTICAL);
            content.addView(copy, new LinearLayout.LayoutParams(
                    0, LayoutParams.MATCH_PARENT, 1f));
        }

        TextView title = label(config.label, theme.isFrost() ? 12 : 13,
                theme.text, Typeface.BOLD);
        title.setGravity(theme.isFrost() ? Gravity.START | Gravity.CENTER_VERTICAL : Gravity.CENTER);
        title.setMaxLines(1);
        title.setEllipsize(TextUtils.TruncateAt.END);
        copy.addView(title, new LinearLayout.LayoutParams(
                LayoutParams.MATCH_PARENT, dp(22)));

        TextView chord = label(config.subtitle(), 10, theme.muted, Typeface.BOLD);
        chord.setGravity(Gravity.CENTER);
        chord.setMaxLines(1);
        chord.setEllipsize(TextUtils.TruncateAt.END);
        chord.setPadding(dp(6), 0, dp(6), 0);
        chord.setBackground(theme.shape(context, theme.surface, 9));
        LinearLayout.LayoutParams chordParams = new LinearLayout.LayoutParams(
                LayoutParams.WRAP_CONTENT, dp(19));
        chordParams.topMargin = dp(3);
        copy.addView(chord, chordParams);

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
