package com.codex.phonedeck;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Typeface;
import android.os.Bundle;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.GridLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

public final class KeyPickerActivity extends Activity {
    static final String EXTRA_KEYS = "keys";

    private PhoneDeckTheme theme;
    private CheckBox ctrl;
    private CheckBox shift;
    private CheckBox alt;
    private CheckBox win;
    private String selectedBaseKey = "";
    private TextView preview;
    private LinearLayout baseKeyContainer;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        theme = PhoneDeckTheme.load(this);
        theme.applyWindow(this);
        setContentView(createInterface());
        loadInitialKeys();
        updatePreview();
    }

    private View createInterface() {
        ScrollView scroll = new ScrollView(this);
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(20), dp(20), dp(20), dp(28));
        page.setBackgroundColor(theme.contentBackground());
        scroll.addView(page);

        LinearLayout header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);
        Button back = button("←");
        back.setOnClickListener(view -> finish());
        header.addView(back, new LinearLayout.LayoutParams(dp(48), dp(44)));
        TextView title = text("选择按键", 22, theme.text, Typeface.BOLD);
        LinearLayout.LayoutParams titleParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT,
                LinearLayout.LayoutParams.WRAP_CONTENT);
        titleParams.leftMargin = dp(12);
        header.addView(title, titleParams);
        page.addView(header);

        preview = text("", 17, theme.primary, Typeface.BOLD);
        preview.setGravity(Gravity.CENTER);
        preview.setPadding(dp(14), dp(14), dp(14), dp(14));
        preview.setBackground(theme.shape(this, theme.surfaceRaised, 16, 1, theme.outline));
        page.addView(preview, topMargin(dp(20)));

        TextView modifierTitle = text("修饰键", 16, theme.text, Typeface.BOLD);
        page.addView(modifierTitle, topMargin(dp(22)));

        GridLayout modifiers = new GridLayout(this);
        modifiers.setColumnCount(2);
        modifiers.setPadding(dp(12), dp(8), dp(12), dp(8));
        modifiers.setBackground(theme.shape(this, theme.surface, 16, 1, theme.outline));
        ctrl = modifier("Ctrl", modifiers);
        shift = modifier("Shift", modifiers);
        alt = modifier("Alt", modifiers);
        win = modifier("Win", modifiers);
        page.addView(modifiers, topMargin(dp(8)));

        TextView baseTitle = text("基础按键", 16, theme.text, Typeface.BOLD);
        page.addView(baseTitle, topMargin(dp(22)));

        baseKeyContainer = new LinearLayout(this);
        baseKeyContainer.setOrientation(LinearLayout.VERTICAL);
        page.addView(baseKeyContainer, topMargin(dp(8)));

        buildBaseKeySection("字母与数字", getLettersAndNumbers());
        buildBaseKeySection("常用与控制", getControls());
        buildBaseKeySection("功能键", getFunctions());
        buildBaseKeySection("媒体", getMediaKeys());

        Button use = button("使用这个组合键");
        use.setTextColor(theme.onPrimary);
        use.setBackground(theme.pressable(this, theme.primary, theme.primaryPressed, 16));
        use.setOnClickListener(view -> finishWithSelection());
        page.addView(use, topMargin(dp(28)));
        return theme.wrapContent(this, scroll);
    }

    private void buildBaseKeySection(String titleStr, List<String> keys) {
        TextView groupTitle = text(titleStr, 14, theme.muted, Typeface.BOLD);
        baseKeyContainer.addView(groupTitle, topMargin(dp(12)));

        GridLayout grid = new GridLayout(this);
        grid.setColumnCount(4);
        grid.setUseDefaultMargins(false);
        for (String key : keys) {
            Button btn = new Button(this);
            btn.setText(KeyCatalog.displayKey(key));
            btn.setTextSize(13);
            btn.setAllCaps(false);
            btn.setTextColor(key.equals(selectedBaseKey) ? theme.onPrimary : theme.text);
            btn.setBackground(theme.pressable(this, key.equals(selectedBaseKey) ? theme.primary : theme.surface,
                    theme.primaryPressed, 12));
            btn.setPadding(dp(4), 0, dp(4), 0);
            btn.setOnClickListener(v -> {
                selectedBaseKey = key;
                updateBaseKeySelection();
                updatePreview();
            });
            btn.setTag(key);
            GridLayout.LayoutParams params = new GridLayout.LayoutParams();
            params.width = 0;
            params.height = dp(48);
            params.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
            params.setMargins(dp(3), dp(3), dp(3), dp(3));
            grid.addView(btn, params);
        }
        baseKeyContainer.addView(grid, topMargin(dp(6)));
    }

    private void updateBaseKeySelection() {
        for (int i = 0; i < baseKeyContainer.getChildCount(); i++) {
            View child = baseKeyContainer.getChildAt(i);
            if (child instanceof GridLayout) {
                GridLayout grid = (GridLayout) child;
                for (int j = 0; j < grid.getChildCount(); j++) {
                    Button btn = (Button) grid.getChildAt(j);
                    String key = (String) btn.getTag();
                    boolean isSelected = key.equals(selectedBaseKey);
                    btn.setTextColor(isSelected ? theme.onPrimary : theme.text);
                    btn.setBackground(theme.pressable(this, isSelected ? theme.primary : theme.surface,
                            theme.primaryPressed, 12));
                }
            }
        }
    }

    private List<String> getLettersAndNumbers() {
        List<String> keys = new ArrayList<>();
        for (char value = 'A'; value <= 'Z'; value++) keys.add(String.valueOf(value));
        for (char value = '0'; value <= '9'; value++) keys.add(String.valueOf(value));
        return keys;
    }

    private List<String> getControls() {
        return Arrays.asList("ENTER", "ESC", "TAB", "SPACE", "BACKSPACE", "DELETE", "INSERT",
                "HOME", "END", "PAGEUP", "PAGEDOWN", "UP", "DOWN", "LEFT", "RIGHT", "PRINTSCREEN");
    }

    private List<String> getFunctions() {
        List<String> keys = new ArrayList<>();
        for (int number = 1; number <= 24; number++) keys.add("F" + number);
        return keys;
    }

    private List<String> getMediaKeys() {
        return Arrays.asList("VOLUMEUP", "VOLUMEDOWN", "VOLUMEMUTE",
                "MEDIAPLAYPAUSE", "MEDIAPREVIOUS", "MEDIANEXT");
    }

    private CheckBox modifier(String label, GridLayout parent) {
        CheckBox checkBox = new CheckBox(this);
        checkBox.setText(label);
        checkBox.setTextColor(theme.text);
        checkBox.setTextSize(16);
        checkBox.setPadding(dp(8), dp(5), dp(8), dp(5));
        checkBox.setOnCheckedChangeListener((button, checked) -> {
            if (checked && selectedModifierCount() > 3) {
                button.setChecked(false);
                preview.setText("最多选择 3 个修饰键，再搭配 1 个基础按键");
                preview.setTextColor(theme.danger);
                return;
            }
            updatePreview();
        });
        GridLayout.LayoutParams params = new GridLayout.LayoutParams();
        params.width = 0;
        params.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
        parent.addView(checkBox, params);
        return checkBox;
    }

    private int selectedModifierCount() {
        int count = 0;
        if (ctrl != null && ctrl.isChecked()) count++;
        if (shift != null && shift.isChecked()) count++;
        if (alt != null && alt.isChecked()) count++;
        if (win != null && win.isChecked()) count++;
        return count;
    }

    private void loadInitialKeys() {
        ArrayList<String> initial = getIntent().getStringArrayListExtra(EXTRA_KEYS);
        if (initial == null || initial.isEmpty()) {
            selectedBaseKey = "";
            return;
        }
        ctrl.setChecked(initial.contains("CTRL"));
        shift.setChecked(initial.contains("SHIFT"));
        alt.setChecked(initial.contains("ALT"));
        win.setChecked(initial.contains("WIN"));
        for (String key : initial) {
            if (KeyCatalog.BASE_KEYS.contains(key)) {
                selectedBaseKey = key;
                updateBaseKeySelection();
                break;
            }
        }
    }

    private ArrayList<String> selectedKeys() {
        ArrayList<String> keys = new ArrayList<>();
        if (ctrl.isChecked()) keys.add("CTRL");
        if (shift.isChecked()) keys.add("SHIFT");
        if (alt.isChecked()) keys.add("ALT");
        if (win.isChecked()) keys.add("WIN");
        if (!selectedBaseKey.isEmpty()) {
            keys.add(selectedBaseKey);
        }
        return KeyCatalog.normalizeChord(keys);
    }

    private void updatePreview() {
        if (preview == null) {
            return;
        }
        try {
            preview.setText("预览：" + KeyCatalog.displayChord(selectedKeys()));
            preview.setTextColor(theme.primary);
        } catch (Exception exception) {
            preview.setText(exception.getMessage());
            preview.setTextColor(theme.danger);
        }
    }

    private void finishWithSelection() {
        try {
            Intent result = new Intent();
            result.putStringArrayListExtra(EXTRA_KEYS, selectedKeys());
            setResult(RESULT_OK, result);
            finish();
        } catch (Exception exception) {
            preview.setText(exception.getMessage());
            preview.setTextColor(theme.danger);
        }
    }

    private Button button(String label) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextColor(theme.text);
        button.setTextSize(15);
        button.setAllCaps(false);
        button.setBackground(theme.pressable(this, theme.surfaceRaised,
                PhoneDeckTheme.blend(theme.surfaceRaised, theme.primary, 0.14f), 14));
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

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
