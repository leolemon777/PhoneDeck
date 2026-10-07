package com.codex.phonedeck;

import android.annotation.SuppressLint;
import android.content.Context;
import android.view.MotionEvent;
import android.view.VelocityTracker;
import android.view.ViewConfiguration;
import android.widget.HorizontalScrollView;

/**
 * 电脑卡片轮播的翻页容器：像相册一样一次只翻一页。
 * 松手时按拖动距离与速度决定目标页，自己滚过去；不做惯性甩动，
 * 所以不会先甩过头再被拉回来。卡片自身的点按、长按照常由卡片处理。
 */
@SuppressLint("ViewConstructor") // Programmatic control owned by MainActivity.
final class CardPager extends HorizontalScrollView {
    interface Listener {
        /// 松手后定下的页（可能与原来相同），或代码主动翻到的页。
        void onPageChosen(int page);
    }

    private final Listener listener;
    private final int touchSlop;
    private final int flingVelocity;
    private VelocityTracker velocityTracker;
    private float downX;
    private int downPage;
    private boolean moved;
    private boolean dragging;
    private boolean locked;
    private int pageWidth = 1;
    private int pageCount = 1;
    /// 滑动动画结束后才通知选中：否则切换确认会在动画中途重建卡片，把滑动截断成一跳。
    private static final long SETTLE_DELAY_MS = 280;
    private Runnable pendingChoice;
    private int restorePage = -1;

    CardPager(Context context, Listener listener) {
        super(context);
        this.listener = listener;
        ViewConfiguration configuration = ViewConfiguration.get(context);
        touchSlop = configuration.getScaledTouchSlop();
        flingVelocity = Math.round(context.getResources().getDisplayMetrics().density * 350);
        setHorizontalScrollBarEnabled(false);
        setOverScrollMode(OVER_SCROLL_NEVER);
    }

    void configure(int pageWidth, int pageCount) {
        this.pageWidth = Math.max(1, pageWidth);
        this.pageCount = Math.max(1, pageCount);
    }

    /// 说话中锁住：不能滑走，避免看到的电脑和正在说话的电脑不一致。
    void setLocked(boolean locked) {
        this.locked = locked;
    }

    boolean isDragging() {
        return dragging;
    }

    int currentPage() {
        return clamp(Math.round(getScrollX() / (float) pageWidth));
    }

    /// 翻到指定页。notify 为 true 时按用户选择处理（会触发切换确认）。
    void showPage(int page, boolean animate, boolean notify) {
        int chosen = clamp(page);
        int target = chosen * pageWidth;
        cancelPendingChoice();
        if (animate) smoothScrollTo(target, 0);
        else scrollTo(target, 0);
        if (!notify) return;
        if (!animate || getScrollX() == target) {
            listener.onPageChosen(chosen);
            return;
        }
        pendingChoice = () -> {
            pendingChoice = null;
            listener.onPageChosen(chosen);
        };
        postDelayed(pendingChoice, SETTLE_DELAY_MS);
    }

    /// 卡片重建后停在这一页：在下一次布局里、绘制前定位，不会先闪到第一张。
    void holdPage(int page) {
        restorePage = page;
        requestLayout();
    }

    @Override
    protected void onLayout(boolean changed, int left, int top, int right, int bottom) {
        super.onLayout(changed, left, top, right, bottom);
        if (restorePage >= 0 && !dragging && pendingChoice == null) {
            scrollTo(clamp(restorePage) * pageWidth, 0);
        }
        restorePage = -1;
    }

    /// 焦点落到别的卡片（重建时常落到第一张）不自动滚动：翻页只由手势和代码决定。
    @Override
    protected int computeScrollDeltaToGetChildRectOnScreen(android.graphics.Rect rect) {
        return 0;
    }

    /// 还没通知出去的选中（动画中）：新手势开始或页面重建时取消。
    boolean hasPendingChoice() {
        return pendingChoice != null;
    }

    private void cancelPendingChoice() {
        if (pendingChoice != null) {
            removeCallbacks(pendingChoice);
            pendingChoice = null;
        }
    }

    @Override
    public boolean dispatchTouchEvent(MotionEvent event) {
        if (locked) {
            // 仍允许点按、长按卡片；拦截与滚动在下面关闭。
            return super.dispatchTouchEvent(event);
        }
        int action = event.getActionMasked();
        if (action == MotionEvent.ACTION_DOWN) {
            if (velocityTracker == null) velocityTracker = VelocityTracker.obtain();
            velocityTracker.clear();
            cancelPendingChoice();
            downX = event.getX();
            downPage = currentPage();
            moved = false;
            dragging = true;
        }
        if (velocityTracker != null) velocityTracker.addMovement(event);
        if (action == MotionEvent.ACTION_MOVE && Math.abs(event.getX() - downX) > touchSlop) {
            moved = true;
        }
        boolean handled = super.dispatchTouchEvent(event);
        if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_CANCEL) {
            dragging = false;
            if (moved) settle(action == MotionEvent.ACTION_UP);
            if (velocityTracker != null) {
                velocityTracker.recycle();
                velocityTracker = null;
            }
        }
        return handled;
    }

    private void settle(boolean released) {
        int target = downPage;
        if (released && velocityTracker != null) {
            velocityTracker.computeCurrentVelocity(1000);
            float velocity = velocityTracker.getXVelocity();
            int distance = getScrollX() - downPage * pageWidth;
            if (Math.abs(velocity) > flingVelocity) {
                target = downPage + (velocity < 0 ? 1 : -1);
            } else if (Math.abs(distance) > pageWidth / 4) {
                target = downPage + (distance > 0 ? 1 : -1);
            }
        }
        showPage(target, true, true);
    }

    @Override
    public void fling(int velocityX) {
        // 翻页由 settle 决定；系统惯性会让卡片甩过头。
    }

    @Override
    public boolean onInterceptTouchEvent(MotionEvent event) {
        return !locked && super.onInterceptTouchEvent(event);
    }

    @SuppressLint("ClickableViewAccessibility") // Scrolling only; cards keep their own click handling.
    @Override
    public boolean onTouchEvent(MotionEvent event) {
        return !locked && super.onTouchEvent(event);
    }

    private int clamp(int page) {
        return Math.max(0, Math.min(pageCount - 1, page));
    }
}
