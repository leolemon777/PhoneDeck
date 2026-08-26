package com.codex.phonedeck;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ClipData;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.Typeface;
import android.os.Bundle;
import android.view.DragEvent;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.util.ArrayList;

public final class ShortcutSettingsActivity extends Activity {
    private static final int BACKGROUND = Color.rgb(11, 16, 32);
    private static final int PANEL = Color.rgb(22, 29, 50);
    private static final int PRIMARY = Color.rgb(121, 168, 255);
    private static final int TEXT = Color.rgb(247, 249, 255);
    private static final int MUTED = Color.rgb(159, 172, 202);
    private static final int DANGER = Color.rgb(255, 112, 132);

    private ShortcutConfigRepository repository;
    private LinearLayout list;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().setStatusBarColor(BACKGROUND);
        getWindow().setNavigationBarColor(BACKGROUND);
        repository = new ShortcutConfigRepository(this);
        setContentView(createInterface());
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (list != null) {
            refreshList();
        }
    }

    private View createInterface() {
        ScrollView scroll = new ScrollView(this);
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(16), dp(20), dp(16), dp(32));
        page.setBackgroundColor(BACKGROUND);
        scroll.addView(page);

        LinearLayout header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);
        Button back = button("←", PANEL, TEXT);
        back.setOnClickListener(view -> finish());
        header.addView(back, new LinearLayout.LayoutParams(dp(48), dp(44)));
        TextView title = text("快捷键与布局", 22, TEXT, Typeface.BOLD);
        LinearLayout.LayoutParams titleParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT,
                LinearLayout.LayoutParams.WRAP_CONTENT);
        titleParams.leftMargin = dp(12);
        header.addView(title, titleParams);
        page.addView(header);

        TextView hint = text("点击编辑；长按拖动排序，也可用上下箭头。内置按钮可隐藏，自定义按钮可删除。",
                13, MUTED, Typeface.NORMAL);
        hint.setLineSpacing(0, 1.15f);
        page.addView(hint, topMargin(dp(18)));

        LinearLayout actions = new LinearLayout(this);
        Button add = button("＋ 新增按钮", Color.rgb(39, 66, 112), TEXT);
        add.setOnClickListener(view ->
                startActivity(new Intent(this, ShortcutEditActivity.class)));
        actions.addView(add, new LinearLayout.LayoutParams(0, dp(52), 1f));
        Button restore = button("恢复全部", Color.rgb(78, 39, 51), DANGER);
        restore.setOnClickListener(view -> confirmResetAll());
        LinearLayout.LayoutParams restoreParams = new LinearLayout.LayoutParams(0, dp(52), 1f);
        restoreParams.leftMargin = dp(8);
        actions.addView(restore, restoreParams);
        page.addView(actions, topMargin(dp(16)));

        list = new LinearLayout(this);
        list.setOrientation(LinearLayout.VERTICAL);
        page.addView(list, topMargin(dp(12)));
        refreshList();
        return scroll;
    }

    private void refreshList() {
        list.removeAllViews();
        try {
            ArrayList<ShortcutButtonConfig> buttons = repository.load();
            for (int index = 0; index < buttons.size(); index++) {
                list.addView(createRow(buttons.get(index), index, buttons.size()),
                        rowParams(index == 0 ? 0 : dp(8)));
            }
        } catch (Exception exception) {
            showError("无法读取快捷键配置");
        }
    }

    private View createRow(ShortcutButtonConfig config, int index, int total) {
        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(dp(10), dp(9), dp(8), dp(9));
        row.setBackgroundColor(PANEL);
        row.setTag(config.id);
        row.setContentDescription(config.label + "，" + config.subtitle()
                + (config.visible ? "，已显示" : "，已隐藏"));

        TextView drag = text("☰", 20, MUTED, Typeface.BOLD);
        drag.setGravity(Gravity.CENTER);
        row.addView(drag, new LinearLayout.LayoutParams(dp(32), dp(48)));

        LinearLayout copy = new LinearLayout(this);
        copy.setOrientation(LinearLayout.VERTICAL);
        TextView title = text((config.icon.isEmpty() ? "" : config.icon + "  ") + config.label,
                15, config.visible ? TEXT : MUTED, Typeface.BOLD);
        copy.addView(title);
        copy.addView(text(config.subtitle() + (config.visible ? "" : "  ·  已隐藏"),
                12, MUTED, Typeface.NORMAL));
        copy.setOnClickListener(view -> openEditor(config.id));
        row.addView(copy, new LinearLayout.LayoutParams(0,
                LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        Button visible = miniButton(config.visible ? "隐藏" : "显示");
        visible.setContentDescription((config.visible ? "隐藏" : "显示") + config.label);
        visible.setOnClickListener(view -> toggleVisible(config.id));
        row.addView(visible, new LinearLayout.LayoutParams(dp(52), dp(42)));

        Button up = miniButton("↑");
        up.setEnabled(index > 0);
        up.setContentDescription("上移 " + config.label);
        up.setOnClickListener(view -> moveBy(config.id, -1));
        row.addView(up, new LinearLayout.LayoutParams(dp(42), dp(42)));

        Button down = miniButton("↓");
        down.setEnabled(index + 1 < total);
        down.setContentDescription("下移 " + config.label);
        down.setOnClickListener(view -> moveBy(config.id, 1));
        row.addView(down, new LinearLayout.LayoutParams(dp(42), dp(42)));

        Button edit = miniButton("编辑");
        edit.setOnClickListener(view -> openEditor(config.id));
        row.addView(edit, new LinearLayout.LayoutParams(dp(54), dp(42)));

        row.setOnLongClickListener(view -> {
            ClipData data = ClipData.newPlainText("PhoneDeckButtonId", config.id);
            view.startDragAndDrop(data, new View.DragShadowBuilder(view), null, 0);
            return true;
        });
        row.setOnDragListener((view, event) -> handleDrag(view, event));
        return row;
    }

    private boolean handleDrag(View target, DragEvent event) {
        if (event.getAction() == DragEvent.ACTION_DRAG_STARTED) {
            return event.getClipDescription() != null;
        }
        if (event.getAction() == DragEvent.ACTION_DRAG_ENTERED) {
            target.setAlpha(0.62f);
            return true;
        }
        if (event.getAction() == DragEvent.ACTION_DRAG_EXITED
                || event.getAction() == DragEvent.ACTION_DRAG_ENDED) {
            target.setAlpha(1f);
            return true;
        }
        if (event.getAction() == DragEvent.ACTION_DROP) {
            target.setAlpha(1f);
            if (event.getClipData() == null || event.getClipData().getItemCount() == 0) {
                return false;
            }
            String sourceId = event.getClipData().getItemAt(0).getText().toString();
            String targetId = String.valueOf(target.getTag());
            moveBefore(sourceId, targetId);
            return true;
        }
        return true;
    }

    private void openEditor(String id) {
        Intent intent = new Intent(this, ShortcutEditActivity.class);
        intent.putExtra(ShortcutEditActivity.EXTRA_BUTTON_ID, id);
        startActivity(intent);
    }

    private void toggleVisible(String id) {
        try {
            ArrayList<ShortcutButtonConfig> buttons = repository.load();
            for (ShortcutButtonConfig value : buttons) {
                if (value.id.equals(id)) {
                    value.visible = !value.visible;
                    value.updatedAt = System.currentTimeMillis();
                    repository.save(buttons);
                    refreshList();
                    return;
                }
            }
        } catch (Exception exception) {
            showError("保存显示状态失败");
        }
    }

    private void moveBy(String id, int delta) {
        try {
            ArrayList<ShortcutButtonConfig> buttons = repository.load();
            for (int index = 0; index < buttons.size(); index++) {
                if (!buttons.get(index).id.equals(id)) {
                    continue;
                }
                int destination = index + delta;
                if (destination < 0 || destination >= buttons.size()) {
                    return;
                }
                ShortcutButtonConfig moving = buttons.remove(index);
                buttons.add(destination, moving);
                repository.save(buttons);
                refreshList();
                return;
            }
        } catch (Exception exception) {
            showError("保存按钮顺序失败");
        }
    }

    private void moveBefore(String sourceId, String targetId) {
        if (sourceId.equals(targetId)) {
            return;
        }
        try {
            ArrayList<ShortcutButtonConfig> buttons = repository.load();
            ShortcutButtonConfig moving = null;
            for (ShortcutButtonConfig value : buttons) {
                if (value.id.equals(sourceId)) {
                    moving = value;
                    break;
                }
            }
            if (moving == null) {
                return;
            }
            buttons.remove(moving);
            int targetIndex = 0;
            while (targetIndex < buttons.size()
                    && !buttons.get(targetIndex).id.equals(targetId)) {
                targetIndex++;
            }
            buttons.add(Math.min(targetIndex, buttons.size()), moving);
            repository.save(buttons);
            refreshList();
        } catch (Exception exception) {
            showError("保存拖动顺序失败");
        }
    }

    private void confirmResetAll() {
        new AlertDialog.Builder(this)
                .setTitle("恢复全部默认布局")
                .setMessage("这会删除自定义按钮并恢复所有内置按钮。语音模式不会改变。")
                .setNegativeButton("取消", null)
                .setPositiveButton("恢复全部", (dialog, which) -> {
                    try {
                        repository.resetAll();
                        refreshList();
                    } catch (Exception exception) {
                        showError("恢复默认布局失败");
                    }
                })
                .show();
    }

    private void showError(String message) {
        Toast.makeText(this, message, Toast.LENGTH_LONG).show();
    }

    private Button miniButton(String label) {
        return button(label, Color.rgb(31, 41, 68), TEXT);
    }

    private Button button(String label, int background, int foreground) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextColor(foreground);
        button.setTextSize(12);
        button.setAllCaps(false);
        button.setPadding(dp(2), 0, dp(2), 0);
        button.setBackgroundColor(background);
        return button;
    }

    private TextView text(String value, int size, int color, int style) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(size);
        view.setTextColor(color);
        view.setTypeface(Typeface.DEFAULT, style);
        return view;
    }

    private LinearLayout.LayoutParams topMargin(int top) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT);
        params.topMargin = top;
        return params;
    }

    private LinearLayout.LayoutParams rowParams(int top) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT);
        params.topMargin = top;
        return params;
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
