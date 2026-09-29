package com.codex.phonedeck;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.view.View;

/// 电平条：19 根圆头短棒。ACTIVE 用 live（采音红），CONNECTING/PAUSED 用 warning，
/// ERROR 用 danger；空闲时是安静的低棒轮廓。
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
        float gap = dp(3);
        float available = getWidth() - gap * (BAR_COUNT - 1);
        float width = Math.max(dp(3), available / BAR_COUNT);
        float center = getHeight() / 2f;
        int activeBars = state == ACTIVE
                ? Math.max(1, Math.round(level / 100f * BAR_COUNT))
                : state == CONNECTING ? 4 : 0;
        int activeColor = state == ACTIVE
                ? theme.live
                : state == PAUSED || state == CONNECTING
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

    private float dp(int value) {
        return PhoneDeckTheme.dp(getContext(), value);
    }
}
