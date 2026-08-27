package com.codex.phonedeck;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.LinearGradient;
import android.graphics.Paint;
import android.graphics.RadialGradient;
import android.graphics.Shader;
import android.view.View;

final class FrostedBackdropView extends View {
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);

    FrostedBackdropView(Context context) {
        super(context);
        setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_NO);
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        float width = getWidth();
        float height = getHeight();

        paint.setShader(new LinearGradient(
                0, 0, width, height,
                new int[]{Color.rgb(247, 251, 255), Color.rgb(231, 241, 255),
                        Color.rgb(247, 250, 255)},
                new float[]{0f, 0.55f, 1f}, Shader.TileMode.CLAMP));
        canvas.drawRect(0, 0, width, height, paint);

        drawGlow(canvas, width * 0.90f, height * 0.10f, width * 0.58f,
                Color.argb(105, 119, 177, 255));
        drawGlow(canvas, width * 0.08f, height * 0.42f, width * 0.48f,
                Color.argb(75, 95, 191, 255));
        drawGlow(canvas, width * 0.85f, height * 0.72f, width * 0.66f,
                Color.argb(62, 107, 165, 255));
        drawGlow(canvas, width * 0.34f, height * 0.95f, width * 0.56f,
                Color.argb(72, 181, 218, 255));

        paint.setShader(new LinearGradient(
                0, 0, width, 0,
                new int[]{Color.argb(0, 255, 255, 255), Color.argb(82, 255, 255, 255),
                        Color.argb(0, 255, 255, 255)},
                null, Shader.TileMode.CLAMP));
        for (int index = 0; index < 4; index++) {
            float top = height * (0.18f + index * 0.23f);
            canvas.save();
            canvas.rotate(-9f, width / 2f, top);
            canvas.drawRect(-width * 0.2f, top, width * 1.2f, top + dp(54), paint);
            canvas.restore();
        }
        paint.setShader(null);
    }

    private void drawGlow(Canvas canvas, float x, float y, float radius, int centerColor) {
        paint.setShader(new RadialGradient(
                x, y, radius,
                new int[]{centerColor, Color.argb(0, Color.red(centerColor),
                        Color.green(centerColor), Color.blue(centerColor))},
                null, Shader.TileMode.CLAMP));
        canvas.drawCircle(x, y, radius, paint);
    }

    private float dp(int value) {
        return PhoneDeckTheme.dp(getContext(), value);
    }
}
