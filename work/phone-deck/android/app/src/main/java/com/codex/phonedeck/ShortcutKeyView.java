package com.codex.phonedeck;

import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.HapticFeedbackConstants;
import android.view.View;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;

final class ShortcutKeyView extends FrameLayout {
    private final PhoneDeckTheme theme;
    private final TextView stateBadge;
    private final TextView deleteBadge;
    private final LinearLayout content;
    private final int baseSurface;
    private final int pressedSurface;
    private final int cornerRadius;
    private boolean editMode;
    private final Runnable clearState = this::clearFeedback;

    ShortcutKeyView(Context context, PhoneDeckTheme theme, ShortcutButtonConfig config) {
        this(context, theme, config, false);
    }

    private final int strokeColor;

    ShortcutKeyView(Context context, PhoneDeckTheme theme, ShortcutButtonConfig config, boolean compact) {
        super(context);
        this.theme = theme;
        this.cornerRadius = compact ? 16 : 18;
        setClickable(true);
        setFocusable(true);
        setSoundEffectsEnabled(true);
        setHapticFeedbackEnabled(true);
        setClipChildren(false);
        setClipToPadding(false);
        setForegroundGravity(Gravity.CENTER);
        setPadding(dp(compact ? 8 : 12), dp(compact ? 8 : 12), dp(compact ? 8 : 10), dp(compact ? 8 : 12));
        boolean command = config.isTextAction();
        int accent = theme.shortcutAccent(config.color);
        boolean hasCustomColor = config.color != null && !config.color.isEmpty()
                && !"slate".equals(config.color) && !"blue".equals(config.color);
        baseSurface = compact
                ? (command
                        ? theme.mix(theme.surface, theme.primaryContainer, 0.55f)
                        : theme.surface)
                : (command
                        ? theme.primaryContainer
                        : hasCustomColor
                        ? theme.mix(theme.surface, accent, theme.light ? 0.05f : 0.10f)
                        : theme.surface);
        strokeColor = compact
                ? Color.TRANSPARENT
                : (hasCustomColor
                        ? theme.mix(theme.outline, accent, theme.light ? 0.28f : 0.38f)
                        : theme.outline);
        pressedSurface = theme.shortcutPressedColor(config.color);
        setBackground(theme.pressable(context, baseSurface, pressedSurface, cornerRadius,
                compact ? 0 : 1, strokeColor));
        setElevation(0);

        content = new LinearLayout(context);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setGravity(compact ? Gravity.CENTER : Gravity.START);
        addView(content, new FrameLayout.LayoutParams(
                LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT));

        TextView title = label(config.label, compact ? 15 : 15,
                command && !compact ? theme.onPrimaryContainer : theme.text, Typeface.BOLD);
        title.setGravity(compact ? Gravity.CENTER : Gravity.START);
        title.setMaxLines(compact ? 1 : 2);
        title.setEllipsize(TextUtils.TruncateAt.END);
        content.addView(title, new LinearLayout.LayoutParams(
                LayoutParams.MATCH_PARENT, compact ? LayoutParams.WRAP_CONTENT : 0, compact ? 0f : 1f));

        String sub = compact
                ? config.subtitle().replace(" + ", "+").replace("Backspace", "⌫")
                : config.subtitle();
        int subColor = command
                ? theme.primary
                : (hasCustomColor && !compact
                        ? theme.mix(theme.body, accent, theme.light ? 0.72f : 0.82f)
                        : theme.muted);
        TextView chord = label(sub, compact ? 11 : 11, subColor, Typeface.NORMAL);
        chord.setGravity(Gravity.CENTER);
        chord.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        chord.setMaxLines(1);
        chord.setEllipsize(TextUtils.TruncateAt.END);
        LinearLayout.LayoutParams chordParams = new LinearLayout.LayoutParams(
                compact ? LayoutParams.WRAP_CONTENT : LayoutParams.MATCH_PARENT,
                LayoutParams.WRAP_CONTENT);
        chordParams.topMargin = dp(compact ? 3 : 6);
        content.addView(chord, chordParams);

        stateBadge = label("", 12, theme.onPrimary, Typeface.BOLD);
        stateBadge.setGravity(Gravity.CENTER);
        stateBadge.setVisibility(View.GONE);
        FrameLayout.LayoutParams stateParams = new FrameLayout.LayoutParams(dp(22), dp(22),
                Gravity.TOP | Gravity.END);
        stateParams.setMargins(0, dp(-3), dp(-3), 0);
        addView(stateBadge, stateParams);

        deleteBadge = label("×", 13, Color.WHITE, Typeface.BOLD);
        deleteBadge.setGravity(Gravity.CENTER);
        deleteBadge.setBackground(theme.shape(context, theme.danger, 12));
        deleteBadge.setContentDescription("移除 " + config.label);
        deleteBadge.setVisibility(View.GONE);
        FrameLayout.LayoutParams deleteParams = new FrameLayout.LayoutParams(dp(22), dp(22),
                Gravity.TOP | Gravity.END);
        deleteParams.setMargins(0, dp(-4), dp(-4), 0);
        addView(deleteBadge, deleteParams);
    }

    void setEditMode(boolean enabled, Runnable onDelete) {
        this.editMode = enabled;
        if (enabled) {
            stateBadge.setVisibility(View.GONE);
            deleteBadge.setVisibility(View.VISIBLE);
            deleteBadge.setOnClickListener(view -> {
                view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
                if (onDelete != null) onDelete.run();
            });
            setBackground(theme.pressable(getContext(), baseSurface, pressedSurface,
                    cornerRadius, 1, theme.primary));
        } else {
            deleteBadge.setVisibility(View.GONE);
            deleteBadge.setOnClickListener(null);
            setBackground(theme.pressable(getContext(), baseSurface, pressedSurface,
                    cornerRadius, strokeColor == Color.TRANSPARENT ? 0 : 1, strokeColor));
        }
    }

    void showSending() {
        if (editMode) return;
        removeCallbacks(clearState);
        content.animate().alpha(0.72f).setDuration(90).start();
        showBadge("…", theme.warning);
    }

    void showSuccess() {
        if (editMode) return;
        removeCallbacks(clearState);
        content.animate().alpha(1f).setDuration(100).start();
        showBadge("✓", theme.success);
        TouchFeedback.result(this, true);
        postDelayed(clearState, 900);
    }

    void showFailure() {
        if (editMode) return;
        removeCallbacks(clearState);
        content.animate().alpha(1f).setDuration(100).start();
        showBadge("!", theme.danger);
        TouchFeedback.result(this, false);
        postDelayed(clearState, 1200);
    }

    private void showBadge(String text, int color) {
        stateBadge.setText(text);
        stateBadge.setTextColor(theme.light ? Color.WHITE : theme.background);
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
