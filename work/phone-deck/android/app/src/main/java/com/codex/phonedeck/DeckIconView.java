package com.codex.phonedeck;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.view.View;

/** Small original line icons; no fonts, bitmaps or third-party asset dependency. */
@android.annotation.SuppressLint("ViewConstructor") // Programmatic-only icon, never inflated from XML.
final class DeckIconView extends View {
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final String kind;

    DeckIconView(Context context, String kind, int color) {
        super(context);
        this.kind = kind;
        paint.setColor(color);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(1.6f);
        paint.setStrokeCap(Paint.Cap.ROUND);
        paint.setStrokeJoin(Paint.Join.ROUND);
        setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_NO);
    }

    @Override protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        canvas.save();
        float scale = Math.min(getWidth(), getHeight()) / 24f;
        canvas.translate((getWidth() - 24 * scale) / 2, (getHeight() - 24 * scale) / 2);
        canvas.scale(scale, scale);
        switch (kind) {
            case "appearance":
                canvas.drawCircle(12, 12, 8, paint);
                canvas.drawLine(12, 4, 12, 20, paint);
                canvas.drawArc(6, 6, 18, 18, -90, 180, false, paint);
                break;
            case "voice":
                canvas.drawRoundRect(9, 3, 15, 14, 3, 3, paint);
                canvas.drawArc(6, 8, 18, 19, 0, 180, false, paint);
                canvas.drawLine(12, 19, 12, 22, paint);
                canvas.drawLine(9, 22, 15, 22, paint);
                break;
            case "keys":
                canvas.drawRoundRect(3, 5, 21, 19, 3, 3, paint);
                for (int x = 7; x <= 17; x += 5) {
                    canvas.drawPoint(x, 9, paint); canvas.drawPoint(x, 12, paint);
                }
                canvas.drawLine(8, 16, 16, 16, paint);
                break;
            case "connection":
                canvas.drawArc(2, 3, 22, 23, 225, 90, false, paint);
                canvas.drawArc(6, 8, 18, 20, 225, 90, false, paint);
                canvas.drawCircle(12, 17, 1, paint);
                break;
            default:
                canvas.drawArc(4, 4, 20, 20, -60, 280, false, paint);
                canvas.drawLine(4, 5, 4, 10, paint);
                canvas.drawLine(4, 10, 9, 10, paint);
                canvas.drawLine(12, 8, 12, 13, paint);
                canvas.drawLine(12, 13, 15, 15, paint);
        }
        canvas.restore();
    }
}
