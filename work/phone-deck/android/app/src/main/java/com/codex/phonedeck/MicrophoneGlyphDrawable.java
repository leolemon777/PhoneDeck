package com.codex.phonedeck;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.ColorFilter;
import android.graphics.Paint;
import android.graphics.PixelFormat;
import android.graphics.Rect;
import android.graphics.RectF;
import android.graphics.drawable.Drawable;

final class MicrophoneGlyphDrawable extends Drawable {
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final int width;
    private final int height;
    private int color;

    MicrophoneGlyphDrawable(Context context, int color) {
        this.color = color;
        width = PhoneDeckTheme.dp(context, 28);
        height = PhoneDeckTheme.dp(context, 34);
        paint.setStrokeCap(Paint.Cap.ROUND);
        paint.setStrokeJoin(Paint.Join.ROUND);
    }

    void setColor(int color) {
        this.color = color;
        invalidateSelf();
    }

    @Override
    public void draw(Canvas canvas) {
        Rect bounds = getBounds();
        float w = bounds.width();
        float h = bounds.height();
        float left = bounds.left;
        float top = bounds.top;
        paint.setColor(color);

        paint.setStyle(Paint.Style.FILL);
        canvas.drawRoundRect(new RectF(
                left + w * 0.34f, top + h * 0.05f,
                left + w * 0.66f, top + h * 0.62f),
                w * 0.16f, w * 0.16f, paint);

        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(Math.max(2f, w * 0.09f));
        canvas.drawArc(new RectF(
                left + w * 0.20f, top + h * 0.29f,
                left + w * 0.80f, top + h * 0.79f),
                0f, 180f, false, paint);
        canvas.drawLine(left + w * 0.50f, top + h * 0.79f,
                left + w * 0.50f, top + h * 0.92f, paint);
        canvas.drawLine(left + w * 0.34f, top + h * 0.92f,
                left + w * 0.66f, top + h * 0.92f, paint);
    }

    @Override
    public void setAlpha(int alpha) {
        paint.setAlpha(alpha);
    }

    @Override
    public void setColorFilter(ColorFilter colorFilter) {
        paint.setColorFilter(colorFilter);
    }

    @Override
    public int getOpacity() {
        return PixelFormat.TRANSLUCENT;
    }

    @Override
    public int getIntrinsicWidth() {
        return width;
    }

    @Override
    public int getIntrinsicHeight() {
        return height;
    }
}
