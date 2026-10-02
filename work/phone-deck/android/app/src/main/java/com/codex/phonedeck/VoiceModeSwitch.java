package com.codex.phonedeck;

import android.animation.ValueAnimator;
import android.annotation.SuppressLint;
import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.view.Gravity;
import android.view.View;
import android.view.accessibility.AccessibilityNodeInfo;
import android.view.animation.DecelerateInterpolator;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.LinearLayout;

/** One compact, accessible switch for the existing three phone interaction choices. */
@SuppressLint("ViewConstructor")
final class VoiceModeSwitch extends FrameLayout {
    interface Listener { void onSelect(String mode, View source); }

    private final PhoneDeckTheme theme;
    private final View indicator;
    private final LinearLayout choices;
    private final Button[] buttons = new Button[3];
    private final String[] modes = {"tap", "hold", "shared"};
    private int selectedIndex;
    private String selectedMode;

    VoiceModeSwitch(Context context, PhoneDeckTheme theme, Listener listener) {
        super(context);
        this.theme = theme;
        setBackground(theme.shape(context, theme.primaryContainer, 28));
        setPadding(dp(4), dp(4), dp(4), dp(4));
        indicator = new View(context);
        indicator.setBackground(theme.shape(context, theme.primary, 24));
        indicator.setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_NO);
        addView(indicator, new FrameLayout.LayoutParams(0, dp(48)));
        choices = new LinearLayout(context);
        addView(choices, new FrameLayout.LayoutParams(LayoutParams.MATCH_PARENT, dp(48)));
        String[] labels = {"点击", "按住", "共享"};
        String[] descriptions = {"点击说话，再次点击停止", "按住说话，松开停止", "共享麦克风，由电脑控制听写"};
        for (int i = 0; i < buttons.length; i++) {
            final String mode = modes[i];
            Button button = new Button(context);
            buttons[i] = button;
            button.setText(labels[i]);
            button.setTextSize(13);
            button.setAllCaps(false);
            button.setSingleLine(true);
            button.setPadding(dp(2), 0, dp(2), 0);
            button.setGravity(Gravity.CENTER);
            button.setStateListAnimator(null);
            button.setBackground(theme.pressable(context, Color.TRANSPARENT,
                    theme.surfaceRaised, 24));
            button.setContentDescription(descriptions[i]);
            button.setAccessibilityDelegate(new View.AccessibilityDelegate() {
                @Override public void onInitializeAccessibilityNodeInfo(View host, AccessibilityNodeInfo info) {
                    super.onInitializeAccessibilityNodeInfo(host, info);
                    info.setClassName("android.widget.RadioButton");
                    info.setCheckable(true);
                    info.setChecked(host.isSelected());
                }
            });
            TouchFeedback.install(button, null, false);
            button.setOnClickListener(view -> listener.onSelect(mode, view));
            choices.addView(button, new LinearLayout.LayoutParams(0, dp(48), 1f));
        }
        setMode("tap", false);
    }

    void setMode(String mode, boolean animate) {
        if (mode.equals(selectedMode)) return;
        selectedMode = mode;
        for (int i = 0; i < buttons.length; i++) {
            boolean selected = modes[i].equals(mode);
            buttons[i].setSelected(selected);
            buttons[i].setTextColor(selected ? theme.onPrimary : theme.onPrimaryContainer);
            buttons[i].setTypeface(Typeface.DEFAULT, selected ? Typeface.BOLD : Typeface.NORMAL);
            if (selected) selectedIndex = i;
        }
        moveIndicator(animate);
    }

    @Override protected void onLayout(boolean changed, int left, int top, int right, int bottom) {
        super.onLayout(changed, left, top, right, bottom);
        if (changed || indicator.getWidth() == 0) moveIndicator(false);
    }

    private void moveIndicator(boolean animate) {
        Button selected = buttons[selectedIndex];
        if (selected == null || selected.getWidth() == 0) return;
        LayoutParams params = (LayoutParams) indicator.getLayoutParams();
        if (params.width != selected.getWidth()) {
            params.width = selected.getWidth();
            indicator.setLayoutParams(params);
        }
        float destination = choices.getLeft() + selected.getLeft() - indicator.getLeft();
        indicator.animate().cancel();
        if (animate && ValueAnimator.areAnimatorsEnabled()) {
            indicator.animate().translationX(destination).setDuration(190)
                    .setInterpolator(new DecelerateInterpolator()).start();
        } else {
            indicator.setTranslationX(destination);
        }
    }

    private int dp(int value) { return PhoneDeckTheme.dp(getContext(), value); }
}
