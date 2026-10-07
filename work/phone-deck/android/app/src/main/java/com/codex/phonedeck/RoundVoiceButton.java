package com.codex.phonedeck;

import android.annotation.SuppressLint;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.drawable.Drawable;
import android.widget.Button;

/**
 * Keeps the glyph centered while the separate caption can wrap at large font sizes.
 * 设置了条形文字后变成底部长条（像微信「按住 说话」）：话筒图形与文字一起居中。
 */
@SuppressLint("ViewConstructor") // Programmatic control; its glyph is shared with the voice state owner.
final class RoundVoiceButton extends Button {
    private final MicrophoneGlyphDrawable glyph;
    private float glyphScale = 0.42f;
    private String barLabel;

    RoundVoiceButton(Context context, MicrophoneGlyphDrawable glyph) {
        super(context);
        this.glyph = glyph;
        glyph.setCallback(this);
        setText("");
    }

    void setGlyphScale(float scale) {
        glyphScale = scale;
        invalidate();
    }

    void setBarLabel(String label) {
        if (label == null ? barLabel == null : label.equals(barLabel)) return;
        barLabel = label;
        invalidate();
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        if (barLabel != null) {
            drawBar(canvas);
            return;
        }
        int height = Math.round(Math.min(getWidth(), getHeight()) * glyphScale);
        int width = Math.round(height * (float) glyph.getIntrinsicWidth()
                / glyph.getIntrinsicHeight());
        int left = (getWidth() - width) / 2;
        int top = (getHeight() - height) / 2;
        glyph.setBounds(left, top, left + width, top + height);
        glyph.draw(canvas);
    }

    private void drawBar(Canvas canvas) {
        Paint paint = getPaint();
        paint.setColor(getCurrentTextColor());
        paint.setFakeBoldText(true);
        float textWidth = paint.measureText(barLabel);
        int glyphHeight = Math.round(getHeight() * 0.36f);
        int glyphWidth = Math.round(glyphHeight * (float) glyph.getIntrinsicWidth()
                / glyph.getIntrinsicHeight());
        int gap = Math.round(getResources().getDisplayMetrics().density * 10);
        float left = (getWidth() - glyphWidth - gap - textWidth) / 2f;
        int top = (getHeight() - glyphHeight) / 2;
        glyph.setBounds(Math.round(left), top, Math.round(left) + glyphWidth, top + glyphHeight);
        glyph.draw(canvas);
        Paint.FontMetrics metrics = paint.getFontMetrics();
        float baseline = getHeight() / 2f - (metrics.ascent + metrics.descent) / 2f;
        canvas.drawText(barLabel, left + glyphWidth + gap, baseline, paint);
    }

    @Override
    protected boolean verifyDrawable(Drawable who) {
        return who == glyph || super.verifyDrawable(who);
    }
}
