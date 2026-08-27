package com.codex.phonedeck;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.graphics.Typeface;
import android.os.Bundle;
import android.text.Editable;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.HorizontalScrollView;
import android.widget.Switch;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class ShortcutEditActivity extends Activity {
    static final String EXTRA_BUTTON_ID = "buttonId";
    private static final int REQUEST_KEYS = 2001;

    private final ExecutorService executor = Executors.newSingleThreadExecutor();
    private PhoneDeckTheme theme;
    private ShortcutConfigRepository repository;
    private ShortcutButtonConfig draft;
    private EditText labelInput;
    private EditText iconInput;
    private LinearLayout colorContainer;
    private String selectedColor;
    private Switch visibleSwitch;
    private TextView keySummary;
    private TextView feedback;
    private Button testButton;
    private boolean dirty;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        theme = PhoneDeckTheme.load(this);
        theme.applyWindow(this);
        repository = new ShortcutConfigRepository(this);
        String id = getIntent().getStringExtra(EXTRA_BUTTON_ID);
        draft = id == null ? repository.newCustomButton() : findButton(id);
        if (draft == null) {
            finish();
            return;
        }
        setContentView(createInterface());
        bindDraft();
        installDirtyTracking();
    }

    private ShortcutButtonConfig findButton(String id) {
        for (ShortcutButtonConfig value : repository.load()) {
            if (value.id.equals(id)) {
                return value.copy();
            }
        }
        return null;
    }

    private View createInterface() {
        ScrollView scroll = new ScrollView(this);
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(20), dp(20), dp(20), dp(34));
        page.setBackgroundColor(theme.contentBackground());
        scroll.addView(page);

        LinearLayout header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);
        Button back = button("←", theme.surfaceRaised, theme.text);
        back.setOnClickListener(view -> requestClose());
        header.addView(back, new LinearLayout.LayoutParams(dp(48), dp(44)));
        TextView title = text(draft.builtIn ? "编辑按钮" : "新增按钮", 22, theme.text,
                Typeface.BOLD);
        LinearLayout.LayoutParams titleParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT,
                LinearLayout.LayoutParams.WRAP_CONTENT);
        titleParams.leftMargin = dp(12);
        header.addView(title, titleParams);
        page.addView(header);

        page.addView(label("按钮名称"), topMargin(dp(22)));
        labelInput = input("例如：帮助");
        page.addView(labelInput, fieldParams());

        page.addView(label("图标或 Emoji"), topMargin(dp(18)));
        iconInput = input("例如：✨");
        page.addView(iconInput, fieldParams());
        LinearLayout presets = new LinearLayout(this);
        for (String icon : Arrays.asList("✨", "📋", "💾", "🎙", "▶", "⚙")) {
            Button preset = button(icon, theme.surface, theme.text);
            preset.setOnClickListener(view -> iconInput.setText(icon));
            presets.addView(preset, new LinearLayout.LayoutParams(0, dp(44), 1f));
        }
        page.addView(presets, topMargin(dp(7)));

        page.addView(label("按钮颜色"), topMargin(dp(18)));
        colorContainer = new LinearLayout(this);
        colorContainer.setOrientation(LinearLayout.HORIZONTAL);
        HorizontalScrollView colorScroll = new HorizontalScrollView(this);
        colorScroll.addView(colorContainer, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        colorScroll.setHorizontalScrollBarEnabled(false);
        page.addView(colorScroll, topMargin(dp(8)));

        visibleSwitch = new Switch(this);
        visibleSwitch.setText("在主界面显示这个按钮");
        visibleSwitch.setTextColor(theme.text);
        visibleSwitch.setTextSize(15);
        visibleSwitch.setPadding(dp(12), dp(8), dp(12), dp(8));
        page.addView(visibleSwitch, topMargin(dp(18)));

        page.addView(label("按键动作"), topMargin(dp(20)));
        keySummary = text("", 17, theme.primary, Typeface.BOLD);
        keySummary.setGravity(Gravity.CENTER);
        keySummary.setPadding(dp(14), dp(15), dp(14), dp(15));
        keySummary.setBackground(theme.shape(this, theme.surfaceRaised, 16, 1, theme.outline));
        page.addView(keySummary, topMargin(dp(7)));

        Button choose = button("选择单键或组合键", theme.surface, theme.text);
        choose.setOnClickListener(view -> {
            Intent intent = new Intent(this, KeyPickerActivity.class);
            intent.putStringArrayListExtra(KeyPickerActivity.EXTRA_KEYS,
                    new ArrayList<>(draft.keys));
            startActivityForResult(intent, REQUEST_KEYS);
        });
        page.addView(choose, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(54)));

        testButton = button("发送测试（不会保存）", theme.surfaceRaised, theme.text);
        testButton.setOnClickListener(view -> testAction());
        page.addView(testButton, topMargin(dp(18)));

        feedback = text("先测试，再保存。电脑端必须为 1.5.0。", 13, theme.muted,
                Typeface.NORMAL);
        feedback.setPadding(dp(12), dp(10), dp(12), dp(10));
        feedback.setBackground(theme.shape(this, theme.surface, 12, 1, theme.outline));
        page.addView(feedback, topMargin(dp(7)));

        Button save = button("保存并返回主界面", theme.primary, theme.onPrimary);
        save.setOnClickListener(view -> saveAndFinish());
        page.addView(save, topMargin(dp(18)));

        Button restoreOrDelete = button(
                draft.builtIn ? "恢复这个内置按钮" : "删除这个自定义按钮",
                theme.feedbackSurface(theme.danger), theme.danger);
        restoreOrDelete.setOnClickListener(view -> confirmRestoreOrDelete());
        page.addView(restoreOrDelete, topMargin(dp(12)));
        return theme.wrapContent(this, scroll);
    }

    private void bindDraft() {
        labelInput.setText(draft.label);
        iconInput.setText(draft.icon);
        selectColor(draft.color);
        visibleSwitch.setChecked(draft.visible);
        updateKeySummary();
        dirty = false;
    }

    private void selectColor(String color) {
        selectedColor = color;
        colorContainer.removeAllViews();
        for (String c : KeyCatalog.COLORS) {
            View swatch = new View(this);
            int size = dp(48);
            boolean isSelected = c.equals(color);
            swatch.setBackground(theme.shape(this, theme.shortcutColor(c), size / 2, isSelected ? 3 : 0, theme.text));
            swatch.setContentDescription("选择" + colorName(c) + "按钮颜色"
                    + (isSelected ? "，当前已选择" : ""));
            swatch.setOnClickListener(v -> {
                selectColor(c);
                dirty = true;
            });
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(size, size);
            params.rightMargin = dp(12);
            colorContainer.addView(swatch, params);
        }
    }

    private String colorName(String color) {
        switch (color) {
            case "purple": return "紫色";
            case "green": return "绿色";
            case "orange": return "橙色";
            case "red": return "红色";
            case "slate": return "深灰色";
            default: return "蓝色";
        }
    }

    private void installDirtyTracking() {
        TextWatcher watcher = new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence text, int start, int count, int after) { }
            @Override public void onTextChanged(CharSequence text, int start, int before, int count) {
                dirty = true;
            }
            @Override public void afterTextChanged(Editable text) { }
        };
        labelInput.addTextChangedListener(watcher);
        iconInput.addTextChangedListener(watcher);
        visibleSwitch.setOnCheckedChangeListener((button, checked) -> dirty = true);
    }

    private ShortcutButtonConfig collectDraft() {
        String label = labelInput.getText().toString().trim();
        String icon = iconInput.getText().toString().trim();
        if (label.isEmpty() || label.length() > 24) {
            throw new IllegalArgumentException("按钮名称必须为 1–24 个字符");
        }
        if (icon.length() > 8) {
            throw new IllegalArgumentException("图标或 Emoji 不能超过 8 个字符");
        }
        return new ShortcutButtonConfig(
                draft.id, label, icon,
                selectedColor == null ? "blue" : selectedColor,
                visibleSwitch.isChecked(), draft.sortIndex, draft.builtIn,
                draft.keys, draft.holdMs, System.currentTimeMillis());
    }

    private void testAction() {
        ShortcutButtonConfig candidate;
        try {
            candidate = collectDraft();
        } catch (Exception exception) {
            showFeedback(exception.getMessage(), theme.danger);
            return;
        }
        testButton.setEnabled(false);
        showFeedback("正在发送测试：" + candidate.subtitle(), theme.warning);
        executor.execute(() -> {
            try {
                String message = PhoneDeckUsbClient.sendKeyChord(
                        this,
                        candidate.keys,
                        candidate.holdMs,
                        BluetoothTransport.current());
                runOnUiThread(() -> {
                    showFeedback("✓  " + message, theme.success);
                    testButton.setEnabled(true);
                });
            } catch (Exception exception) {
                runOnUiThread(() -> {
                    showFeedback("✕  " + exception.getMessage(), theme.danger);
                    testButton.setEnabled(true);
                });
            }
        });
    }

    private void saveAndFinish() {
        try {
            ShortcutButtonConfig candidate = collectDraft();
            ArrayList<ShortcutButtonConfig> buttons = repository.load();
            boolean replaced = false;
            for (int index = 0; index < buttons.size(); index++) {
                if (buttons.get(index).id.equals(candidate.id)) {
                    buttons.set(index, candidate);
                    replaced = true;
                    break;
                }
            }
            if (!replaced) {
                buttons.add(candidate);
            }
            repository.save(buttons);
            dirty = false;
            finish();
        } catch (Exception exception) {
            showFeedback("✕  " + exception.getMessage(), theme.danger);
        }
    }

    private void confirmRestoreOrDelete() {
        String message = draft.builtIn
                ? "只恢复这个按钮的名称、颜色、图标和按键吗？"
                : "确定删除这个自定义按钮吗？";
        new AlertDialog.Builder(this)
                .setTitle(draft.builtIn ? "恢复按钮" : "删除按钮")
                .setMessage(message)
                .setNegativeButton("取消", null)
                .setPositiveButton("确定", (dialog, which) -> restoreOrDelete())
                .show();
    }

    private void restoreOrDelete() {
        try {
            ArrayList<ShortcutButtonConfig> buttons = repository.load();
            boolean found = false;
            for (int index = 0; index < buttons.size(); index++) {
                if (!buttons.get(index).id.equals(draft.id)) {
                    continue;
                }
                found = true;
                if (draft.builtIn) {
                    ShortcutButtonConfig restored = repository.defaultForId(draft.id);
                    restored.sortIndex = buttons.get(index).sortIndex;
                    buttons.set(index, restored);
                    repository.save(buttons);
                    draft = restored.copy();
                    bindDraft();
                    showFeedback("✓  已恢复这个按钮", theme.success);
                } else {
                    buttons.remove(index);
                    repository.save(buttons);
                    dirty = false;
                    finish();
                }
                return;
            }
            if (!found && !draft.builtIn) {
                dirty = false;
                finish();
            }
        } catch (Exception exception) {
            showFeedback("✕  " + exception.getMessage(), theme.danger);
        }
    }

    private void requestClose() {
        if (!dirty) {
            finish();
            return;
        }
        new AlertDialog.Builder(this)
                .setTitle("尚未保存")
                .setMessage("保存修改后返回吗？")
                .setPositiveButton("保存", (dialog, which) -> saveAndFinish())
                .setNeutralButton("放弃", (dialog, which) -> {
                    dirty = false;
                    finish();
                })
                .setNegativeButton("继续编辑", null)
                .show();
    }

    @Override
    public void onBackPressed() {
        requestClose();
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQUEST_KEYS && resultCode == RESULT_OK && data != null) {
            ArrayList<String> keys = data.getStringArrayListExtra(KeyPickerActivity.EXTRA_KEYS);
            if (keys != null) {
                draft.keys = KeyCatalog.normalizeChord(keys);
                dirty = true;
                updateKeySummary();
            }
        }
    }

    @Override
    protected void onDestroy() {
        executor.shutdownNow();
        super.onDestroy();
    }

    private void updateKeySummary() {
        keySummary.setText(draft.subtitle());
    }

    private void showFeedback(String message, int color) {
        feedback.setText(message == null ? "操作失败" : message);
        feedback.setTextColor(color);
        feedback.setBackground(theme.shape(this, theme.feedbackSurface(color), 12, 1, color));
        feedback.announceForAccessibility(feedback.getText());
    }

    private TextView label(String value) {
        return text(value, 15, theme.text, Typeface.BOLD);
    }

    private EditText input(String hint) {
        EditText input = new EditText(this);
        input.setHint(hint);
        input.setHintTextColor(theme.muted);
        input.setTextColor(theme.text);
        input.setTextSize(16);
        input.setSingleLine(true);
        input.setPadding(dp(12), 0, dp(12), 0);
        input.setBackground(theme.shape(this, theme.surface, 14, 1, theme.outline));
        return input;
    }

    private Button button(String label, int background, int foreground) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextColor(foreground);
        button.setTextSize(15);
        button.setAllCaps(false);
        button.setBackground(theme.pressable(this, background,
                PhoneDeckTheme.blend(background, theme.primary, 0.16f), 14));
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

    private LinearLayout.LayoutParams fieldParams() {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(52));
        params.topMargin = dp(7);
        return params;
    }

    private LinearLayout.LayoutParams topMargin(int top) {
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
