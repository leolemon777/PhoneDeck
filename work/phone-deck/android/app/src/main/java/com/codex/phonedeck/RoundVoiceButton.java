package com.codex.phonedeck;

import android.annotation.SuppressLint;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.drawable.Drawable;
import android.widget.Button;

/** Keeps the glyph centered while the separate caption can wrap at large font sizes. */
@SuppressLint("ViewConstructor") // Programmatic control; its glyph is shared with the voice state owner.
final class RoundVoiceButton extends Button {
    private final MicrophoneGlyphDrawable glyph;

    RoundVoiceButton(Context context, MicrophoneGlyphDrawable glyph) {
        super(context);
        this.glyph = glyph;
        glyph.setCallback(this);
        setText("");
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        int width = Math.min(glyph.getIntrinsicWidth(), getWidth());
        int height = Math.min(glyph.getIntrinsicHeight(), getHeight());
        int left = (getWidth() - width) / 2;
        int top = (getHeight() - height) / 2;
        glyph.setBounds(left, top, left + width, top + height);
        glyph.draw(canvas);
    }

    @Override
    protected boolean verifyDrawable(Drawable who) {
        return who == glyph || super.verifyDrawable(who);
    }
}
