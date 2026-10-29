package com.codex.phonedeck;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.view.View;

/** Small original line icons; no fonts, bitmaps or third-party asset dependency. */
@android.annotation.SuppressLint("ViewConstructor") // Programmatic-only icon, never inflated from XML.
final class DeckIconView extends View {
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final android.graphics.Path bubble = new android.graphics.Path();
    private final String kind;

    DeckIconView(Context context, String kind, int color) {
        super(context);
        this.kind = kind;
        paint.setColor(color);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(1.75f);
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
            case "devices":
                canvas.drawRoundRect(3, 4.5f, 21, 16, 2.2f, 2.2f, paint);
                canvas.drawLine(12, 16, 12, 19.5f, paint);
                canvas.drawLine(8, 19.5f, 16, 19.5f, paint);
                break;
            case "laptop":
                canvas.drawRoundRect(4.5f, 5, 19.5f, 15.5f, 2f, 2f, paint);
                canvas.drawLine(2.5f, 18.5f, 21.5f, 18.5f, paint);
                break;
            case "translate":
                canvas.drawLine(4, 5, 12, 5, paint);
                canvas.drawLine(8, 3, 8, 5, paint);
                canvas.drawArc(1, -1, 9, 11, 0, 75, false, paint);
                canvas.drawLine(6, 7, 11, 11.3f, paint);
                canvas.drawLine(13, 21, 17, 12, paint);
                canvas.drawLine(17, 12, 21, 21, paint);
                canvas.drawLine(14.5f, 18, 19.5f, 18, paint);
                break;
            case "ask":
                bubble.reset();
                bubble.moveTo(4, 5); bubble.lineTo(20, 5); bubble.lineTo(20, 16); bubble.lineTo(10, 16);
                bubble.lineTo(5, 20); bubble.lineTo(5, 16); bubble.lineTo(4, 16); bubble.close();
                canvas.drawPath(bubble, paint);
                canvas.drawArc(10, 7.5f, 14, 11.5f, 180, 230, false, paint);
                canvas.drawLine(12, 12, 12, 13, paint);
                break;
            case "slash":
                canvas.drawLine(15, 4, 9, 20, paint);
                break;
            case "plus":
                canvas.drawLine(12, 5, 12, 19, paint);
                canvas.drawLine(5, 12, 19, 12, paint);
                break;
            case "appearance":
                canvas.drawCircle(12, 12, 7.5f, paint);
                canvas.drawArc(7.5f, 7.5f, 16.5f, 16.5f, -90, 180, false, paint);
                break;
            case "voice":
                canvas.drawRoundRect(9, 3.5f, 15, 13.5f, 3, 3, paint);
                canvas.drawArc(6, 8, 18, 18.5f, 0, 180, false, paint);
                canvas.drawLine(12, 18.5f, 12, 21.5f, paint);
                canvas.drawLine(9, 21.5f, 15, 21.5f, paint);
                break;
            case "keys":
                canvas.drawRoundRect(3, 5.5f, 21, 18.5f, 3, 3, paint);
                for (int x = 7; x <= 17; x += 5) {
                    canvas.drawLine(x - 0.8f, 9.5f, x + 0.8f, 9.5f, paint);
                    canvas.drawLine(x - 0.8f, 12.5f, x + 0.8f, 12.5f, paint);
                }
                canvas.drawLine(8, 15.5f, 16, 15.5f, paint);
                break;
            case "connection":
                canvas.drawArc(2.5f, 4, 21.5f, 23, 225, 90, false, paint);
                canvas.drawArc(6.5f, 8.5f, 17.5f, 19.5f, 225, 90, false, paint);
                canvas.drawCircle(12, 17, 0.9f, paint);
                break;
            case "sync":
                canvas.drawArc(4.5f, 4.5f, 19.5f, 19.5f, 200, 130, false, paint);
                canvas.drawLine(17.5f, 5.5f, 19.5f, 8.5f, paint);
                canvas.drawLine(19.5f, 8.5f, 16f, 9f, paint);
                canvas.drawArc(4.5f, 4.5f, 19.5f, 19.5f, 20, 130, false, paint);
                canvas.drawLine(6.5f, 18.5f, 4.5f, 15.5f, paint);
                canvas.drawLine(4.5f, 15.5f, 8f, 15f, paint);
                break;
            case "update":
            case "↻":
            default:
                canvas.drawArc(4.5f, 4.5f, 19.5f, 19.5f, -55, 285, false, paint);
                canvas.drawLine(16, 4.5f, 19.5f, 5.5f, paint);
                canvas.drawLine(19.5f, 5.5f, 18.2f, 9, paint);
                break;
        }
        canvas.restore();
    }
}
