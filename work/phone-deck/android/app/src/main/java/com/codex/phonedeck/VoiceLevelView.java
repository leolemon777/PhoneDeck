package com.codex.phonedeck;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.view.View;

/// 麦克风电平条：唯一的数据可视化元素。扁平细条，
/// 颜色只表达状态（空闲=描边灰、活动=点缀色、暂停/错误=语义色），无辉光。
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
        int activeColor = state == PAUSED
                ? theme.warning
                : state == ERROR ? theme.danger : theme.primary;

        for (int index = 0; index < BAR_COUNT; index++) {
            float wave = 0.28f + 0.72f * (float) Math.abs(
                    Math.sin((index + 1) * 0.78));
            float height = dp(6) + wave * dp(state == ACTIVE ? 16 : 8);
            if (state == IDLE || state == PAUSED || state == ERROR) {
                height = dp(5);
            }
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
