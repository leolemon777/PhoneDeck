package com.codex.phonedeck;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.app.Dialog;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.Typeface;
import android.view.Gravity;
import android.view.View;
import android.view.Window;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.util.function.Consumer;

final class HomeStylePicker {
    private HomeStylePicker() { }

    static Dialog show(Activity activity, PhoneDeckTheme theme, HomeStyle selected,
                       Consumer<HomeStyle> onSelect) {
        Dialog dialog = new Dialog(activity);
        dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);
        LinearLayout panel = new LinearLayout(activity);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(activity, 20), dp(activity, 20), dp(activity, 20), dp(activity, 12));
        panel.addView(label(activity, "首页 UI", 22, theme.text, true));
        TextView hint = label(activity, "选一种布局，配色和语音方式继续沿用你的选择。", 13, theme.muted, false);
        hint.setPadding(0, dp(activity, 8), 0, dp(activity, 16));
        panel.addView(hint);
        ScrollView scroll = new ScrollView(activity);
        LinearLayout options = new LinearLayout(activity);
        options.setOrientation(LinearLayout.VERTICAL);
        scroll.addView(options);
        panel.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f));
        for (HomeStyle style : HomeStyle.values()) {
            boolean current = style == selected;
            LinearLayout row = new LinearLayout(activity);
            row.setGravity(Gravity.CENTER_VERTICAL);
            row.setPadding(dp(activity, 12), dp(activity, 12), dp(activity, 12), dp(activity, 12));
            row.setBackground(theme.pressable(activity,
                    current ? theme.primaryContainer : theme.surface, theme.surfaceRaised,
                    20, current ? 2 : 1, current ? theme.primary : theme.outline));
            row.setFocusable(true);
            row.setSelected(current);
            row.setContentDescription(style.title + "，" + style.detail + (current ? "，当前款式" : ""));
            row.addView(new Preview(activity, theme, style),
                    new LinearLayout.LayoutParams(dp(activity, 66), dp(activity, 100)));
            LinearLayout copy = new LinearLayout(activity);
            copy.setOrientation(LinearLayout.VERTICAL);
            copy.setPadding(dp(activity, 14), 0, dp(activity, 4), 0);
            copy.addView(label(activity, style.title, 17, theme.text, true));
            TextView detail = label(activity, style.detail, 12, theme.muted, false);
            detail.setPadding(0, dp(activity, 5), 0, 0);
            copy.addView(detail);
            row.addView(copy, new LinearLayout.LayoutParams(0, -2, 1f));
            row.addView(label(activity, current ? "✓" : "", 20, theme.primary, true),
                    new LinearLayout.LayoutParams(dp(activity, 24), -2));
            TouchFeedback.install(row, null, false);
            row.setOnClickListener(view -> {
                TouchFeedback.selection(view);
                dialog.dismiss();
                onSelect.accept(style);
            });
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
            params.bottomMargin = dp(activity, 10);
            options.addView(row, params);
        }
        Button close = new Button(activity);
        close.setText("返回语音");
        close.setTextColor(theme.text);
        close.setAllCaps(false);
        close.setBackground(theme.pressable(activity, theme.surfaceRaised, theme.primaryContainer, 16));
        close.setStateListAnimator(null);
        TouchFeedback.install(close, null);
        close.setOnClickListener(view -> dialog.dismiss());
        panel.addView(close, new LinearLayout.LayoutParams(-1, dp(activity, 48)));
        dialog.setContentView(panel);
        dialog.setCanceledOnTouchOutside(true);
        dialog.show();
        Window window = dialog.getWindow();
        if (window != null) {
            window.setGravity(Gravity.BOTTOM);
            window.setBackgroundDrawable(theme.shape(activity, theme.background, 28));
            window.setLayout(-1, Math.min(dp(activity, 560),
                    activity.getResources().getDisplayMetrics().heightPixels * 85 / 100));
        }
        return dialog;
    }

    private static TextView label(Context context, String text, int size, int color, boolean bold) {
        TextView view = new TextView(context);
        view.setText(text);
        view.setTextSize(size);
        view.setTextColor(color);
        view.setTypeface(Typeface.DEFAULT, bold ? Typeface.BOLD : Typeface.NORMAL);
        return view;
    }

    private static int dp(Context context, int value) { return PhoneDeckTheme.dp(context, value); }

    /** Native thumbnail of placement only; it has no fake computer or recording state. */
    @SuppressLint("ViewConstructor")
    private static final class Preview extends View {
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final PhoneDeckTheme theme;
        private final HomeStyle style;

        Preview(Context context, PhoneDeckTheme theme, HomeStyle style) {
            super(context);
            this.theme = theme;
            this.style = style;
            setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_NO);
        }

        @Override protected void onDraw(Canvas canvas) {
            super.onDraw(canvas);
            canvas.save();
            canvas.scale(getWidth() / 66f, getHeight() / 100f);
            rect(canvas, 0, 0, 66, 100, 8, theme.background);
            rect(canvas, 7, 8, 24, 11, 1.5f, theme.text);
            rect(canvas, 7, 17, 59, 23, 3, theme.surfaceRaised);
            if (style == HomeStyle.CENTER) {
                mic(canvas, 33, 48, 14);
                rect(canvas, 19, 67, 47, 69, 1, theme.muted);
                mode(canvas, 80);
                keys(canvas, 89);
            } else if (style == HomeStyle.DOCK) {
                rect(canvas, 8, 40, 45, 43, 1, theme.text);
                rect(canvas, 8, 47, 32, 49, 1, theme.muted);
                mode(canvas, 57);
                mic(canvas, 33, 76, 11);
                keys(canvas, 91);
            } else {
                rect(canvas, 5, 43, 61, 85, 6, theme.surfaceRaised);
                rect(canvas, 10, 49, 44, 52, 1, theme.text);
                mic(canvas, 22, 69, 11);
                for (int i = 0; i < 3; i++) rect(canvas, 38, 58 + i * 8, 55, 64 + i * 8, 2, theme.surface);
                mode(canvas, 90);
            }
            canvas.restore();
        }

        private void mic(Canvas canvas, float x, float y, float radius) {
            paint.setColor(theme.primary);
            canvas.drawCircle(x, y, radius, paint);
            rect(canvas, x - 2, y - 5, x + 2, y + 3, 2, theme.onPrimary);
            rect(canvas, x - 3, y + 5, x + 3, y + 6, 1, theme.onPrimary);
        }

        private void mode(Canvas canvas, float top) {
            rect(canvas, 7, top, 59, top + 5, 2.5f, theme.primaryContainer);
            rect(canvas, 8, top, 24, top + 5, 2.5f, theme.primary);
        }

        private void keys(Canvas canvas, float top) {
            for (int i = 0; i < 3; i++) rect(canvas, 7 + i * 18, top, 23 + i * 18, top + 6, 2, theme.surfaceRaised);
        }

        private void rect(Canvas canvas, float left, float top, float right, float bottom,
                          float radius, int color) {
            paint.setColor(color);
            canvas.drawRoundRect(left, top, right, bottom, radius, radius, paint);
        }
    }
}
