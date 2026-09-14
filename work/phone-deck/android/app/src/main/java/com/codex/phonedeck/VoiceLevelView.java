package com.codex.phonedeck;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.LinearGradient;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.Shader;
import android.view.View;

final class VoiceLevelView extends View {
    static final int IDLE = 0;
    static final int CONNECTING = 1;
    static final int ACTIVE = 2;
    static final int PAUSED = 3;
    static final int ERROR = 4;

    private static final int BAR_COUNT = 19;
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final PhoneDeckTheme theme;
    private int level;
    private int state = IDLE;

    VoiceLevelView(Context context, PhoneDeckTheme theme) {
        super(context);
        this.theme = theme;
        setContentDescription("手机麦克风音量");
        if (theme.isFrost()) {
            setLayerType(LAYER_TYPE_SOFTWARE, null);
        }
    }

    void setLevel(int percent) {
        level = Math.max(0, Math.min(100, percent));
        invalidate();
    }

    void setVoiceState(int state) {
        this.state = state;
        if (state != ACTIVE) {
            level = 0;
        }
        invalidate();
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        if (theme.isFrost()) {
            drawFrostWave(canvas);
            return;
        }
        float gap = dp(3);
        float available = getWidth() - gap * (BAR_COUNT - 1);
        float width = Math.max(dp(3), available / BAR_COUNT);
        float center = getHeight() / 2f;
        int activeBars = state == ACTIVE
                ? Math.max(1, Math.round(level / 100f * BAR_COUNT))
                : state == CONNECTING ? 4 : 0;
        int activeColor = state == PAUSED
                ? theme.warning
                : state == ERROR ? theme.danger : theme.success;

        for (int index = 0; index < BAR_COUNT; index++) {
            float wave = 0.28f + 0.72f * (float) Math.abs(
                    Math.sin((index + 1) * 0.78));
            float height = dp(7) + wave * dp(state == ACTIVE ? 18 : 10);
            if (state == IDLE || state == PAUSED || state == ERROR) {
                height = dp(6);
            }
            height = Math.min(height, Math.max(0, getHeight() - dp(2)));
            float left = index * (width + gap);
            paint.setColor(index < activeBars ? activeColor : theme.outline);
            canvas.drawRoundRect(
                    left,
                    center - height / 2f,
                    left + width,
                    center + height / 2f,
                    width / 2f,
                    width / 2f,
                    paint);
        }
    }

    private void drawFrostWave(Canvas canvas) {
        float width = getWidth();
        float center = getHeight() / 2f;
        int color = state == ERROR ? theme.danger
                : state == PAUSED ? theme.warning
                : state == ACTIVE ? theme.primary : theme.outline;

        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeCap(Paint.Cap.ROUND);
        paint.setStrokeWidth(dp(state == ACTIVE ? 2 : 1));
        paint.setShader(new LinearGradient(
                0, 0, width, 0,
                new int[]{Color.TRANSPARENT, color, color, Color.TRANSPARENT},
                new float[]{0f, 0.20f, 0.80f, 1f}, Shader.TileMode.CLAMP));
        if (state == ACTIVE) {
            paint.setShadowLayer(dp(5), 0, 0, Color.argb(125,
                    Color.red(theme.primary), Color.green(theme.primary), Color.blue(theme.primary)));
        } else {
            paint.clearShadowLayer();
        }

        Path path = new Path();
        float strength = state == ACTIVE ? 0.35f + level / 100f * 0.65f
                : state == CONNECTING ? 0.32f : 0.10f;
        for (int x = 0; x <= Math.round(width); x += Math.max(1, Math.round(dp(2)))) {
            float normalized = width == 0 ? 0 : x / width;
            float envelope = (float) Math.sin(Math.PI * normalized);
            float wave = (float) (Math.sin(normalized * Math.PI * 18)
                    + 0.45f * Math.sin(normalized * Math.PI * 33 + 0.7f));
            float y = center + wave * envelope * dp(8) * strength;
            if (x == 0) {
                path.moveTo(x, y);
            } else {
                path.lineTo(x, y);
            }
        }
        canvas.drawPath(path, paint);
        paint.clearShadowLayer();
        paint.setShader(null);
        paint.setStyle(Paint.Style.FILL);
    }

    private float dp(int value) {
        return PhoneDeckTheme.dp(getContext(), value);
    }
}
