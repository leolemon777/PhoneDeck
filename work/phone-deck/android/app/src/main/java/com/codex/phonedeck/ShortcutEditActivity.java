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
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class ShortcutEditActivity extends Activity {
    static final String EXTRA_BUTTON_ID = "buttonId";
    private static final int REQUEST_KEYS = 2001;
    private static final int REQUEST_MACRO_STEP_KEYS = 2002;

    private final ExecutorService executor = Executors.newSingleThreadExecutor();
    private PhoneDeckTheme theme;
    private ShortcutConfigRepository repository;
    private ShortcutButtonConfig draft;
    private EditText labelInput;
    private EditText textInput;
    private Switch submitSwitch;
    private LinearLayout colorContainer;
    private String selectedColor;
    private Switch visibleSwitch;
    private TextView keySummary;
    private TextView feedback;
    private Button testButton;
    private boolean dirty;
    private LinearLayout actionContainer;
    private LinearLayout typeSelectorRow;
    private LinearLayout macroStepContainer;
    private Button macroAddStepButton;
    private int editingStepIndex = -1;

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

    @Override
    public void onConfigurationChanged(android.content.res.Configuration newConfig) {
        super.onConfigurationChanged(newConfig);
        // 旋转不重建 Activity：先把界面上的未保存修改收进草稿，再重建视图并还原。
        harvestDraft();
        boolean wasDirty = dirty;
        setContentView(createInterface());
        bindDraft();
        installDirtyTracking();
        dirty = wasDirty;
    }

    private void harvestDraft() {
        if (labelInput != null) {
            String value = labelInput.getText().toString().trim();
            if (!value.isEmpty()) {
                draft.label = value;
            }
        }
        if (textInput != null) {
            draft.text = textInput.getText().toString();
        }
        if (submitSwitch != null) {
            draft.submitText = submitSwitch.isChecked();
        }
        if (visibleSwitch != null) {
            draft.visible = visibleSwitch.isChecked();
        }
        if (selectedColor != null) {
            draft.color = selectedColor;
        }
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

        page.addView(label("动作类型"), topMargin(dp(20)));
        page.addView(buildTypeSelector(), topMargin(dp(8)));

        actionContainer = new LinearLayout(this);
        actionContainer.setOrientation(LinearLayout.VERTICAL);
        page.addView(actionContainer, topMargin(dp(4)));
        populateActionSection();

        testButton = button("发送测试（不会保存）", theme.surfaceRaised, theme.text);
        testButton.setOnClickListener(view -> testAction());
        page.addView(testButton, topMargin(dp(18)));

        feedback = text("先测试，再保存。文本指令会输入到当前光标位置。", 13, theme.muted,
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

    private LinearLayout buildTypeSelector() {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        String[][] types = {
                {ShortcutButtonConfig.ACTION_KEY_CHORD, "键盘动作"},
                {ShortcutButtonConfig.ACTION_TEXT, "文本指令"},
                {ShortcutButtonConfig.ACTION_MACRO, "多步宏"}};
        for (String[] entry : types) {
            Button option = button(entry[1], theme.surface, theme.text);
            option.setTextSize(14);
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                    0, dp(44), 1f);
            if (entry[0].equals(ShortcutButtonConfig.ACTION_TEXT)
                    || entry[0].equals(ShortcutButtonConfig.ACTION_MACRO)) {
                params.leftMargin = dp(8);
            }
            option.setOnClickListener(view -> switchActionType(entry[0]));
            row.addView(option, params);
        }
        refreshTypeSelector(row);
        typeSelectorRow = row;
        return row;
    }

    private void refreshTypeSelector(LinearLayout row) {
        for (int index = 0; index < row.getChildCount(); index++) {
            Button option = (Button) row.getChildAt(index);
            boolean selected = selectedTypeForIndex(index).equals(draft.actionType);
            option.setBackground(theme.shape(
                    this,
                    selected ? theme.feedbackSurface(theme.primary) : theme.surface,
                    14, selected ? 2 : 1,
                    selected ? theme.primary : theme.outline));
            option.setTextColor(selected ? theme.primary : theme.muted);
        }
    }

    private static String selectedTypeForIndex(int index) {
        switch (index) {
            case 1: return ShortcutButtonConfig.ACTION_TEXT;
            case 2: return ShortcutButtonConfig.ACTION_MACRO;
            default: return ShortcutButtonConfig.ACTION_KEY_CHORD;
        }
    }

    private void switchActionType(String newType) {
        if (newType.equals(draft.actionType)) {
            return;
        }
        draft.actionType = newType;
        if (ShortcutButtonConfig.ACTION_TEXT.equals(newType)
                && (draft.text == null || draft.text.isBlank())) {
            // 新建 Agent 指令以“一键执行”为默认体验；已有指令的开关选择保持不变。
            draft.submitText = true;
        }
        if (ShortcutButtonConfig.ACTION_KEY_CHORD.equals(newType)
                && (draft.keys == null || draft.keys.isEmpty())) {
            draft.keys = new ArrayList<>();
            draft.keys.add("F1");
        }
        if (ShortcutButtonConfig.ACTION_MACRO.equals(newType)
                && (draft.steps == null || draft.steps.isEmpty())) {
            draft.steps = new ArrayList<>();
            draft.steps.add(ShortcutButtonConfig.MacroStep.text("", true, 0));
        }
        dirty = true;
        populateActionSection();
        if (typeSelectorRow != null) {
            refreshTypeSelector(typeSelectorRow);
        }
    }

    private void populateActionSection() {
        textInput = null;
        submitSwitch = null;
        keySummary = null;
        macroStepContainer = null;
        macroAddStepButton = null;
        actionContainer.removeAllViews();
        if (draft.isTextAction()) {
            actionContainer.addView(label("AI Agent 文本指令"), topMargin(dp(16)));
            textInput = input("例如：/plan");
            textInput.setText(draft.text == null ? "" : draft.text);
            actionContainer.addView(textInput, fieldParams());
            submitSwitch = new Switch(this);
            submitSwitch.setText("输入后自动回车执行");
            submitSwitch.setTextColor(theme.text);
            submitSwitch.setTextSize(15);
            submitSwitch.setPadding(dp(12), dp(8), dp(12), dp(8));
            submitSwitch.setChecked(draft.submitText);
            actionContainer.addView(submitSwitch, topMargin(dp(8)));
        } else if (draft.isMacroAction()) {
            actionContainer.addView(label("宏步骤（最多 8 步，从上到下依次执行）"),
                    topMargin(dp(16)));
            macroStepContainer = new LinearLayout(this);
            macroStepContainer.setOrientation(LinearLayout.VERTICAL);
            actionContainer.addView(macroStepContainer, topMargin(dp(6)));
            rebuildMacroSteps();
            macroAddStepButton = button("＋ 添加步骤", theme.surface, theme.text);
            macroAddStepButton.setOnClickListener(view -> {
                if (draft.steps.size() >= 8) {
                    showFeedback("宏最多 8 个步骤", theme.warning);
                    return;
                }
                draft.steps.add(ShortcutButtonConfig.MacroStep.text("", true, 0));
                dirty = true;
                rebuildMacroSteps();
            });
            actionContainer.addView(macroAddStepButton, topMargin(dp(10)));
            actionContainer.addView(macroHintCard());
        } else {
            actionContainer.addView(label("按键动作"), topMargin(dp(16)));
            keySummary = text("", 17, theme.primary, Typeface.BOLD);
            keySummary.setGravity(Gravity.CENTER);
            keySummary.setPadding(dp(14), dp(15), dp(14), dp(15));
            keySummary.setBackground(theme.shape(
                    this, theme.surfaceRaised, 16, 1, theme.outline));
            actionContainer.addView(keySummary, topMargin(dp(7)));

            Button choose = button("选择单键或组合键", theme.surface, theme.text);
            choose.setOnClickListener(view -> {
                Intent intent = new Intent(this, KeyPickerActivity.class);
                intent.putStringArrayListExtra(KeyPickerActivity.EXTRA_KEYS,
                        new ArrayList<>(draft.keys));
                startActivityForResult(intent, REQUEST_KEYS);
            });
            actionContainer.addView(choose, new LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT, dp(54)));
            updateKeySummary();
        }
    }

    private TextView macroHintCard() {
        TextView hint = text("示例：先按 Ctrl + ` 唤出终端，延迟 300ms，再输入 /compact 并回车。",
                13, theme.muted, Typeface.NORMAL);
        hint.setLineSpacing(0, 1.15f);
        hint.setPadding(dp(12), dp(10), dp(12), dp(10));
        hint.setBackground(theme.shape(this, theme.surface, 12, 1, theme.outline));
        return hint;
    }

    private void rebuildMacroSteps() {
        macroStepContainer.removeAllViews();
        for (int index = 0; index < draft.steps.size(); index++) {
            macroStepContainer.addView(
                    buildMacroStepCard(index), topMargin(index == 0 ? 0 : dp(10)));
        }
    }

    private LinearLayout buildMacroStepCard(final int index) {
        final ShortcutButtonConfig.MacroStep step = draft.steps.get(index);
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(14), dp(12), dp(14), dp(12));
        card.setBackground(theme.shape(this, theme.surfaceRaised, 16, 1, theme.outline));

        LinearLayout header = new LinearLayout(this);
        header.setOrientation(LinearLayout.HORIZONTAL);
        header.setGravity(Gravity.CENTER_VERTICAL);
        TextView stepTitle = text("第 " + (index + 1) + " 步 · "
                + (step.isText() ? "文本" : "按键"), 15, theme.text, Typeface.BOLD);
        header.addView(stepTitle, new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        Button typeToggle = button(step.isText() ? "改为按键" : "改为文本",
                theme.surface, theme.text);
        typeToggle.setTextSize(12);
        typeToggle.setOnClickListener(view -> {
            if (step.isText()) {
                ShortcutButtonConfig.MacroStep replacement =
                        ShortcutButtonConfig.MacroStep.keyChord(
                                java.util.Collections.singletonList("F1"), 45,
                                step.delayBeforeMs);
                draft.steps.set(index, replacement);
            } else {
                ShortcutButtonConfig.MacroStep replacement =
                        ShortcutButtonConfig.MacroStep.text("", true, step.delayBeforeMs);
                draft.steps.set(index, replacement);
            }
            dirty = true;
            rebuildMacroSteps();
        });
        header.addView(typeToggle, new LinearLayout.LayoutParams(
                dp(96), dp(38)));
        card.addView(header);

        LinearLayout delayRow = new LinearLayout(this);
        delayRow.setOrientation(LinearLayout.HORIZONTAL);
        delayRow.setGravity(Gravity.CENTER_VERTICAL);
        delayRow.addView(text("前置延迟 ms", 13, theme.muted, Typeface.NORMAL),
                new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        EditText delayInput = input(String.valueOf(step.delayBeforeMs));
        delayInput.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);
        delayInput.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int a, int b, int c) { }
            @Override public void onTextChanged(CharSequence s, int a, int b, int c) { }
            @Override public void afterTextChanged(Editable s) {
                try {
                    step.delayBeforeMs = Math.max(0, Math.min(2000,
                            Integer.parseInt(s.toString().trim())));
                } catch (Exception exception) {
                    step.delayBeforeMs = 0;
                }
                dirty = true;
            }
        });
        delayRow.addView(delayInput, new LinearLayout.LayoutParams(dp(110),
                LinearLayout.LayoutParams.WRAP_CONTENT));
        card.addView(delayRow, topMargin(dp(8)));

        if (step.isText()) {
            EditText content = input(step.text);
            content.addTextChangedListener(new TextWatcher() {
                @Override public void beforeTextChanged(CharSequence s, int a, int b, int c) { }
                @Override public void onTextChanged(CharSequence s, int a, int b, int c) { }
                @Override public void afterTextChanged(Editable s) {
                    step.text = s.toString();
                    dirty = true;
                }
            });
            card.addView(content, topMargin(dp(6)));
            Switch enter = new Switch(this);
            enter.setText("这一步输入后自动回车");
            enter.setTextColor(theme.text);
            enter.setTextSize(14);
            enter.setChecked(step.submit);
            enter.setOnCheckedChangeListener((button, checked) -> {
                step.submit = checked;
                dirty = true;
            });
            card.addView(enter, topMargin(dp(4)));
        } else {
            TextView summary = text(step.description(), 15, theme.primary, Typeface.BOLD);
            summary.setGravity(Gravity.CENTER);
            summary.setPadding(dp(10), dp(10), dp(10), dp(10));
            summary.setBackground(theme.shape(this, theme.surface, 12, 1, theme.outline));
            card.addView(summary, topMargin(dp(6)));

            Button pick = button("选择按键", theme.surface, theme.text);
            pick.setOnClickListener(view -> {
                editingStepIndex = index;
                Intent intent = new Intent(this, KeyPickerActivity.class);
                intent.putStringArrayListExtra(KeyPickerActivity.EXTRA_KEYS,
                        new ArrayList<>(step.keys));
                startActivityForResult(intent, REQUEST_MACRO_STEP_KEYS);
            });
            card.addView(pick, new LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT, dp(46)));
        }

        LinearLayout ops = new LinearLayout(this);
        ops.setOrientation(LinearLayout.HORIZONTAL);
        ops.setGravity(Gravity.END);
        Button up = button("↑", theme.surface, theme.text);
        up.setEnabled(index > 0);
        up.setAlpha(index > 0 ? 1f : 0.45f);
        up.setOnClickListener(view -> {
            if (index > 0) {
                java.util.Collections.swap(draft.steps, index, index - 1);
                dirty = true;
                rebuildMacroSteps();
            }
        });
        Button down = button("↓", theme.surface, theme.text);
        boolean canDown = index < draft.steps.size() - 1;
        down.setEnabled(canDown);
        down.setAlpha(canDown ? 1f : 0.45f);
        down.setOnClickListener(view -> {
            if (canDown) {
                java.util.Collections.swap(draft.steps, index, index + 1);
                dirty = true;
                rebuildMacroSteps();
            }
        });
        Button remove = button("删除", theme.feedbackSurface(theme.danger), theme.danger);
        remove.setOnClickListener(view -> {
            draft.steps.remove(index);
            dirty = true;
            rebuildMacroSteps();
        });
        LinearLayout.LayoutParams opParams = new LinearLayout.LayoutParams(
                dp(64), dp(40));
        opParams.leftMargin = dp(8);
        ops.addView(up, opParams);
        ops.addView(down, opParams);
        ops.addView(remove, opParams);
        card.addView(ops, topMargin(dp(8)));
        return card;
    }

    private void bindDraft() {
        labelInput.setText(draft.label);
        if (textInput != null) {
            textInput.setText(draft.text);
        }
        if (submitSwitch != null) {
            submitSwitch.setChecked(draft.submitText);
        }
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
            swatch.setBackground(theme.shape(this, theme.shortcutAccent(c), size / 2, isSelected ? 3 : 0, theme.text));
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
        if (textInput != null) {
            textInput.addTextChangedListener(watcher);
        }
        if (submitSwitch != null) {
            submitSwitch.setOnCheckedChangeListener((button, checked) -> dirty = true);
        }
        visibleSwitch.setOnCheckedChangeListener((button, checked) -> dirty = true);
    }

    private ShortcutButtonConfig collectDraft() {
        String label = labelInput.getText().toString().trim();
        if (label.isEmpty() || label.length() > 24) {
            throw new IllegalArgumentException("按钮名称必须为 1–24 个字符");
        }
        String color = selectedColor == null ? "blue" : selectedColor;
        if (draft.isMacroAction()) {
            if (draft.steps.isEmpty() || draft.steps.size() > 8) {
                throw new IllegalArgumentException("宏必须包含 1–8 个步骤");
            }
            for (ShortcutButtonConfig.MacroStep step : draft.steps) {
                if (step.isText() && (step.text == null || step.text.trim().isEmpty())) {
                    throw new IllegalArgumentException("第 " + (draft.steps.indexOf(step) + 1)
                            + " 步的文本内容不能为空");
                }
            }
            return ShortcutButtonConfig.macroAction(
                    draft.id, label, color, visibleSwitch.isChecked(),
                    draft.sortIndex, draft.builtIn, draft.steps,
                    System.currentTimeMillis());
        }
        if (draft.isTextAction()) {
            String command = textInput == null ? "" : textInput.getText().toString().trim();
            if (command.isEmpty() || command.length() > 512
                    || command.contains("\r") || command.contains("\n")) {
                throw new IllegalArgumentException("文本指令必须为 1–512 个字符的单行文本");
            }
            return ShortcutButtonConfig.textAction(
                    draft.id, label, color, visibleSwitch.isChecked(),
                    draft.sortIndex, draft.builtIn, command,
                    submitSwitch != null && submitSwitch.isChecked(),
                    System.currentTimeMillis());
        }
        return new ShortcutButtonConfig(
                draft.id, label, "", color,
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
                String message;
                if (candidate.isTextAction()) {
                    message = PhoneDeckUsbClient.sendText(
                            this,
                            candidate.textForSend(),
                            BluetoothTransport.current());
                } else if (candidate.isMacroAction()) {
                    message = PhoneDeckUsbClient.sendMacro(
                            this,
                            candidate.steps,
                            BluetoothTransport.current());
                } else {
                    message = PhoneDeckUsbClient.sendKeyChord(
                            this,
                            candidate.keys,
                            candidate.holdMs,
                            BluetoothTransport.current());
                }
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
                ? "只恢复这个按钮的名称、颜色和动作吗？"
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
        if (resultCode != RESULT_OK || data == null) {
            return;
        }
        ArrayList<String> keys = data.getStringArrayListExtra(KeyPickerActivity.EXTRA_KEYS);
        if (keys == null) {
            return;
        }
        if (requestCode == REQUEST_KEYS) {
            draft.keys = KeyCatalog.normalizeChord(keys);
            dirty = true;
            updateKeySummary();
        } else if (requestCode == REQUEST_MACRO_STEP_KEYS
                && editingStepIndex >= 0 && editingStepIndex < draft.steps.size()) {
            ShortcutButtonConfig.MacroStep step = draft.steps.get(editingStepIndex);
            if (!step.isText()) {
                step.keys = KeyCatalog.normalizeChord(keys);
                dirty = true;
                rebuildMacroSteps();
            }
            editingStepIndex = -1;
        }
    }

    @Override
    protected void onDestroy() {
        executor.shutdownNow();
        super.onDestroy();
    }

    private void updateKeySummary() {
        if (keySummary != null) {
            keySummary.setText(draft.subtitle());
        }
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
                theme.mix(background, theme.primary, 0.16f), 14));
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
