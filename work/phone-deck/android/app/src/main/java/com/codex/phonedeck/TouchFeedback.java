package com.codex.phonedeck;

import android.animation.ValueAnimator;
import android.annotation.SuppressLint;
import android.os.Build;
import android.view.HapticFeedbackConstants;
import android.view.MotionEvent;
import android.view.View;
import android.view.animation.DecelerateInterpolator;
import android.view.animation.OvershootInterpolator;

/** Short, interruptible feedback; haptics always follow the phone's system setting. */
final class TouchFeedback {
    private TouchFeedback() { }

    static void install(View control, Runnable onRelease) {
        install(control, onRelease, true);
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
            view.animate().alpha(restingAlpha).setDuration(180)
                    .setInterpolator(new DecelerateInterpolator()).start();
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
