package com.codex.phonedeck;

import android.animation.ValueAnimator;
import android.annotation.SuppressLint;
import android.os.Build;
import android.os.SystemClock;
import android.view.HapticFeedbackConstants;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewConfiguration;
import android.view.ViewParent;
import android.view.animation.DecelerateInterpolator;
import android.view.animation.OvershootInterpolator;

/** Short, interruptible feedback; haptics always follow the phone's system setting. */
final class TouchFeedback {
    private TouchFeedback() { }

    static void install(View control, Runnable onRelease) {
        install(control, onRelease, true);
    }

    /**
     * 按住类手势完全自管（供快捷键网格按钮）。系统长按一遇手指微动就取消、手势退化为滚动，
     * 所以这里自己计时：按满时长即回调 onHeld（浮起拖动/连发/全删），已按住再滑出回调
     * onHeldThenMoved（连发转拖动）；未按住就松手仍走 performClick。水平为主的移动预先
     * 阻止父容器拦截，保证“按下即横扫”也能到点转拖动；竖向移动保持父容器正常滚动。
     */
    @SuppressLint("ClickableViewAccessibility") // 手势自管；UP 时显式 performClick。
    static void installHoldable(View control, Runnable onRelease,
            Runnable onHeld, Runnable onHeldThenMoved) {
        control.setSoundEffectsEnabled(true);
        control.setHapticFeedbackEnabled(true);
        float slop = ViewConfiguration.get(control.getContext()).getScaledTouchSlop();
        long holdTimeout = ViewConfiguration.getLongPressTimeout();
        float[] down = new float[2];
        long[] downAt = new long[1];
        boolean[] held = new boolean[1];
        boolean[] movedWhileHeld = new boolean[1];
        boolean[] finished = new boolean[1];
        Runnable[] timer = new Runnable[1];
        control.setOnTouchListener((view, event) -> {
            int action = event.getActionMasked();
            if (action == MotionEvent.ACTION_DOWN && view.isEnabled()) {
                press(view, 0.95f, true);
                down[0] = event.getX();
                down[1] = event.getY();
                downAt[0] = SystemClock.uptimeMillis();
                held[0] = false;
                movedWhileHeld[0] = false;
                finished[0] = false;
                Runnable tick = () -> {
                    if (finished[0] || held[0]) {
                        return;
                    }
                    held[0] = true;
                    disallowIntercept(view);
                    if (onHeld != null) onHeld.run();
                };
                timer[0] = tick;
                view.postDelayed(tick, holdTimeout);
            } else if (action == MotionEvent.ACTION_MOVE) {
                float dx = event.getX() - down[0];
                float dy = event.getY() - down[1];
                if (Math.abs(dx) <= slop && Math.abs(dy) <= slop) {
                    return true;
                }
                if (!held[0]) {
                    if (Math.abs(dx) > Math.abs(dy)) {
                        // 行内扫动先占住事件流，等按住计时到点转拖动。
                        disallowIntercept(view);
                    }
                    return true;
                }
                if (!movedWhileHeld[0]) {
                    movedWhileHeld[0] = true;
                    disallowIntercept(view);
                    if (onHeldThenMoved != null) onHeldThenMoved.run();
                }
            } else if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_CANCEL) {
                finished[0] = true;
                if (timer[0] != null) {
                    view.removeCallbacks(timer[0]);
                }
                float dx = event.getX() - down[0];
                float dy = event.getY() - down[1];
                boolean wasHeld = held[0];
                release(view);
                if (onRelease != null) onRelease.run();
                if (action == MotionEvent.ACTION_UP && !wasHeld && view.isEnabled()
                        && Math.abs(dx) <= slop && Math.abs(dy) <= slop) {
                    view.performClick();
                }
            }
            return true;
        });
    }

    private static void disallowIntercept(View view) {
        ViewParent parent = view.getParent();
        while (parent != null) {
            parent.requestDisallowInterceptTouchEvent(true);
            parent = parent.getParent();
        }
    }

    /**
     * 拖动把手（如列表行的 ☰）：按下即锁定事件流（把手区域不参与父容器滚动），
     * 手指滑出 slop 或按满时长任一先到即回调 onDragStart；未拖动就松手仍走 performClick。
     */
    @SuppressLint("ClickableViewAccessibility") // 手势自管；UP 时显式 performClick。
    static void installDragHandle(View control, Runnable onDragStart) {
        control.setSoundEffectsEnabled(true);
        control.setHapticFeedbackEnabled(true);
        float slop = ViewConfiguration.get(control.getContext()).getScaledTouchSlop();
        long holdTimeout = ViewConfiguration.getLongPressTimeout();
        float[] down = new float[2];
        boolean[] started = new boolean[1];
        boolean[] finished = new boolean[1];
        Runnable[] timer = new Runnable[1];
        control.setOnTouchListener((view, event) -> {
            int action = event.getActionMasked();
            if (action == MotionEvent.ACTION_DOWN && view.isEnabled()) {
                press(view, 0.95f, true);
                down[0] = event.getX();
                down[1] = event.getY();
                started[0] = false;
                finished[0] = false;
                disallowIntercept(view);
                Runnable tick = () -> {
                    if (finished[0] || started[0]) {
                        return;
                    }
                    started[0] = true;
                    view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
                    if (onDragStart != null) onDragStart.run();
                };
                timer[0] = tick;
                view.postDelayed(tick, holdTimeout);
            } else if (action == MotionEvent.ACTION_MOVE
                    && !started[0]
                    && (Math.abs(event.getX() - down[0]) > slop
                    || Math.abs(event.getY() - down[1]) > slop)) {
                started[0] = true;
                view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
                if (onDragStart != null) onDragStart.run();
            } else if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_CANCEL) {
                finished[0] = true;
                if (timer[0] != null) {
                    view.removeCallbacks(timer[0]);
                }
                float dx = event.getX() - down[0];
                float dy = event.getY() - down[1];
                boolean dragged = started[0];
                release(view);
                if (action == MotionEvent.ACTION_UP && !dragged && view.isEnabled()
                        && Math.abs(dx) <= slop && Math.abs(dy) <= slop) {
                    view.performClick();
                }
            }
            return true;
        });
    }

    @SuppressLint("ClickableViewAccessibility") // Always returns false; native onTouchEvent owns performClick.
    static void install(View control, Runnable onRelease, boolean hapticOnPress) {
        control.setSoundEffectsEnabled(true);
        control.setHapticFeedbackEnabled(true);
        control.setOnTouchListener((view, event) -> {
            int action = event.getActionMasked();
            if (action == MotionEvent.ACTION_DOWN && view.isEnabled()) {
                press(view, 0.95f, hapticOnPress);
            } else if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_CANCEL) {
                release(view);
                if (onRelease != null) onRelease.run();
            }
            // Native click/long-click, ripple, and accessibility still own the action.
            return false;
        });
    }

    static void press(View view, float scale, boolean haptic) {
        view.animate().cancel();
        view.animate().withEndAction(null);
        view.setTranslationX(0);
        view.setAlpha(view.isEnabled() ? 1f : 0.45f);
        if (ValueAnimator.areAnimatorsEnabled()) {
            view.animate().scaleX(scale).scaleY(scale).setDuration(85)
                    .setInterpolator(new DecelerateInterpolator()).start();
        } else {
            view.setScaleX(1f);
            view.setScaleY(1f);
        }
        if (haptic) view.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY);
    }

    static void release(View view) {
        view.animate().cancel();
        view.animate().withEndAction(null);
        view.setTranslationX(0);
        view.setAlpha(view.isEnabled() ? 1f : 0.45f);
        if (!ValueAnimator.areAnimatorsEnabled()) {
            view.setScaleX(1f);
            view.setScaleY(1f);
            return;
        }
        view.animate().scaleX(1f).scaleY(1f).setDuration(210)
                .setInterpolator(new OvershootInterpolator(1.1f)).start();
    }

    static void selection(View view) {
        view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK);
    }

    static void resultHaptic(View view, boolean success) {
        view.performHapticFeedback(Build.VERSION.SDK_INT >= Build.VERSION_CODES.R
                ? success ? HapticFeedbackConstants.CONFIRM : HapticFeedbackConstants.REJECT
                : success ? HapticFeedbackConstants.VIRTUAL_KEY : HapticFeedbackConstants.LONG_PRESS);
    }

    static void result(View view, boolean success) {
        view.animate().cancel();
        view.animate().withEndAction(null);
        view.setScaleX(1f);
        view.setScaleY(1f);
        view.setTranslationX(0);
        float restingAlpha = view.isEnabled() ? 1f : 0.45f;
        view.setAlpha(restingAlpha);
        if (!ValueAnimator.areAnimatorsEnabled()) return;
        if (success) {
            view.setAlpha(0.7f);
            // 结束时按当时的可用状态复位，避免动画期间按钮已恢复却被拉回半透明。
            view.animate().alpha(restingAlpha).setDuration(180)
                    .setInterpolator(new DecelerateInterpolator())
                    .withEndAction(() -> view.setAlpha(view.isEnabled() ? 1f : 0.45f)).start();
        } else {
            float distance = PhoneDeckTheme.dp(view.getContext(), 4);
            view.setTranslationX(-distance);
            view.animate().translationX(distance).setDuration(65)
                    .setInterpolator(new DecelerateInterpolator()).withEndAction(() ->
                    view.animate().translationX(0).setDuration(140)
                            .setInterpolator(new OvershootInterpolator(1.2f)).start()).start();
        }
    }
}
