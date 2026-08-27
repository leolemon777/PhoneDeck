package com.codex.phonedeck;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.Typeface;
import android.os.Bundle;
import android.text.Editable;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.View;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.Switch;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class ShortcutEditActivity extends Activity {
    static final String EXTRA_BUTTON_ID = "buttonId";
    private static final int REQUEST_KEYS = 2001;
    private static final int BACKGROUND = Color.rgb(11, 16, 32);
    private static final int PANEL = Color.rgb(22, 29, 50);
    private static final int PRIMARY = Color.rgb(121, 168, 255);
    private static final int TEXT = Color.rgb(247, 249, 255);
    private static final int MUTED = Color.rgb(159, 172, 202);
    private static final int SUCCESS = Color.rgb(79, 220, 156);
    private static final int PENDING = Color.rgb(255, 195, 92);
    private static final int DANGER = Color.rgb(255, 112, 132);

    private final ExecutorService executor = Executors.newSingleThreadExecutor();
    private ShortcutConfigRepository repository;
    private ShortcutButtonConfig draft;
    private EditText labelInput;
    private EditText iconInput;
    private Spinner colorSpinner;
    private Switch visibleSwitch;
    private TextView keySummary;
    private TextView feedback;
    private Button testButton;
    private boolean dirty;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().setStatusBarColor(BACKGROUND);
        getWindow().setNavigationBarColor(BACKGROUND);
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
        page.setBackgroundColor(BACKGROUND);
        scroll.addView(page);

        LinearLayout header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);
        Button back = button("←", PANEL, TEXT);
        back.setOnClickListener(view -> requestClose());
        header.addView(back, new LinearLayout.LayoutParams(dp(48), dp(44)));
        TextView title = text(draft.builtIn ? "编辑按钮" : "新增按钮", 22, TEXT,
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
            Button preset = button(icon, PANEL, TEXT);
            preset.setOnClickListener(view -> iconInput.setText(icon));
            presets.addView(preset, new LinearLayout.LayoutParams(0, dp(44), 1f));
        }
        page.addView(presets, topMargin(dp(7)));

        page.addView(label("按钮颜色"), topMargin(dp(18)));
        colorSpinner = new Spinner(this);
        ArrayAdapter<String> colors = new ArrayAdapter<>(this,
                android.R.layout.simple_spinner_item,
                Arrays.asList("蓝色", "紫色", "绿色", "橙色", "红色", "深灰"));
        colors.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        colorSpinner.setAdapter(colors);
        colorSpinner.setBackgroundColor(PANEL);
        page.addView(colorSpinner, fieldParams());

        visibleSwitch = new Switch(this);
        visibleSwitch.setText("在主界面显示这个按钮");
        visibleSwitch.setTextColor(TEXT);
        visibleSwitch.setTextSize(15);
        visibleSwitch.setPadding(dp(12), dp(8), dp(12), dp(8));
        page.addView(visibleSwitch, topMargin(dp(18)));

        page.addView(label("按键动作"), topMargin(dp(20)));
        keySummary = text("", 17, PRIMARY, Typeface.BOLD);
        keySummary.setGravity(Gravity.CENTER);
        keySummary.setPadding(dp(14), dp(15), dp(14), dp(15));
        keySummary.setBackgroundColor(PANEL);
        page.addView(keySummary, topMargin(dp(7)));

        Button choose = button("选择单键或组合键", PANEL, TEXT);
        choose.setOnClickListener(view -> {
            Intent intent = new Intent(this, KeyPickerActivity.class);
            intent.putStringArrayListExtra(KeyPickerActivity.EXTRA_KEYS,
                    new ArrayList<>(draft.keys));
            startActivityForResult(intent, REQUEST_KEYS);
        });
        page.addView(choose, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(54)));

        testButton = button("发送测试（不会保存）", Color.rgb(50, 65, 104), TEXT);
        testButton.setOnClickListener(view -> testAction());
        page.addView(testButton, topMargin(dp(18)));

        feedback = text("先测试，再保存。电脑端必须为 1.5.0。", 13, MUTED,
                Typeface.NORMAL);
        feedback.setPadding(dp(12), dp(10), dp(12), dp(10));
        page.addView(feedback, topMargin(dp(7)));

        Button save = button("保存并返回主界面", PRIMARY, BACKGROUND);
        save.setOnClickListener(view -> saveAndFinish());
        page.addView(save, topMargin(dp(18)));

        Button restoreOrDelete = button(
                draft.builtIn ? "恢复这个内置按钮" : "删除这个自定义按钮",
                Color.rgb(78, 39, 51), DANGER);
        restoreOrDelete.setOnClickListener(view -> confirmRestoreOrDelete());
        page.addView(restoreOrDelete, topMargin(dp(12)));
        return scroll;
    }

    private void bindDraft() {
        labelInput.setText(draft.label);
        iconInput.setText(draft.icon);
        int colorIndex = KeyCatalog.COLORS.indexOf(draft.color);
        colorSpinner.setSelection(Math.max(0, colorIndex));
        visibleSwitch.setChecked(draft.visible);
        updateKeySummary();
        dirty = false;
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
        colorSpinner.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(AdapterView<?> parent, View view,
                                                  int position, long id) {
                dirty = true;
            }
            @Override public void onNothingSelected(AdapterView<?> parent) { }
        });
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
                KeyCatalog.COLORS.get(colorSpinner.getSelectedItemPosition()),
                visibleSwitch.isChecked(), draft.sortIndex, draft.builtIn,
                draft.keys, draft.holdMs, System.currentTimeMillis());
    }

    private void testAction() {
        ShortcutButtonConfig candidate;
        try {
            candidate = collectDraft();
        } catch (Exception exception) {
            showFeedback(exception.getMessage(), DANGER);
            return;
        }
        testButton.setEnabled(false);
        showFeedback("正在发送测试：" + candidate.subtitle(), PENDING);
        executor.execute(() -> {
            try {
                String message = PhoneDeckUsbClient.sendKeyChord(
                        candidate.keys,
                        candidate.holdMs,
                        BluetoothTransport.current());
                runOnUiThread(() -> {
                    showFeedback("✓  " + message, SUCCESS);
                    testButton.setEnabled(true);
                });
            } catch (Exception exception) {
                runOnUiThread(() -> {
                    showFeedback("✕  " + exception.getMessage(), DANGER);
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
            showFeedback("✕  " + exception.getMessage(), DANGER);
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
                    showFeedback("✓  已恢复这个按钮", SUCCESS);
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
            showFeedback("✕  " + exception.getMessage(), DANGER);
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
        feedback.announceForAccessibility(feedback.getText());
    }

    private TextView label(String value) {
        return text(value, 15, TEXT, Typeface.BOLD);
    }

    private EditText input(String hint) {
        EditText input = new EditText(this);
        input.setHint(hint);
        input.setHintTextColor(MUTED);
        input.setTextColor(TEXT);
        input.setTextSize(16);
        input.setSingleLine(true);
        input.setPadding(dp(12), 0, dp(12), 0);
        input.setBackgroundColor(PANEL);
        return input;
    }

    private Button button(String label, int background, int foreground) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextColor(foreground);
        button.setTextSize(15);
        button.setAllCaps(false);
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
