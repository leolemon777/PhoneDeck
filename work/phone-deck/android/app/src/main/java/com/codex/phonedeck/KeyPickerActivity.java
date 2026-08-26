package com.codex.phonedeck;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.Typeface;
import android.os.Bundle;
import android.view.Gravity;
import android.view.View;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;

import java.util.ArrayList;

public final class KeyPickerActivity extends Activity {
    static final String EXTRA_KEYS = "keys";
    private static final int BACKGROUND = Color.rgb(11, 16, 32);
    private static final int PANEL = Color.rgb(22, 29, 50);
    private static final int PRIMARY = Color.rgb(121, 168, 255);
    private static final int TEXT = Color.rgb(247, 249, 255);
    private static final int MUTED = Color.rgb(159, 172, 202);
    private static final int DANGER = Color.rgb(255, 112, 132);

    private CheckBox ctrl;
    private CheckBox shift;
    private CheckBox alt;
    private CheckBox win;
    private Spinner baseKey;
    private TextView preview;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().setStatusBarColor(BACKGROUND);
        getWindow().setNavigationBarColor(BACKGROUND);
        setContentView(createInterface());
        loadInitialKeys();
        updatePreview();
    }

    private View createInterface() {
        ScrollView scroll = new ScrollView(this);
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(20), dp(20), dp(20), dp(28));
        page.setBackgroundColor(BACKGROUND);
        scroll.addView(page);

        LinearLayout header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);
        Button back = button("←");
        back.setOnClickListener(view -> finish());
        header.addView(back, new LinearLayout.LayoutParams(dp(48), dp(44)));
        TextView title = text("选择按键", 22, TEXT, Typeface.BOLD);
        LinearLayout.LayoutParams titleParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT,
                LinearLayout.LayoutParams.WRAP_CONTENT);
        titleParams.leftMargin = dp(12);
        header.addView(title, titleParams);
        page.addView(header);

        TextView hint = text("最多 4 个键，并且必须选择一个普通键。", 14, MUTED,
                Typeface.NORMAL);
        page.addView(hint, topMargin(dp(20)));

        TextView modifierTitle = text("修饰键", 16, TEXT, Typeface.BOLD);
        page.addView(modifierTitle, topMargin(dp(22)));
        LinearLayout modifiers = new LinearLayout(this);
        modifiers.setOrientation(LinearLayout.VERTICAL);
        modifiers.setPadding(dp(12), dp(8), dp(12), dp(8));
        modifiers.setBackgroundColor(PANEL);
        ctrl = modifier("Ctrl", modifiers);
        shift = modifier("Shift", modifiers);
        alt = modifier("Alt", modifiers);
        win = modifier("Win", modifiers);
        page.addView(modifiers, topMargin(dp(8)));

        TextView baseTitle = text("普通键", 16, TEXT, Typeface.BOLD);
        page.addView(baseTitle, topMargin(dp(22)));
        ArrayList<String> labels = new ArrayList<>();
        for (String key : KeyCatalog.BASE_KEYS) {
            labels.add(KeyCatalog.displayKey(key));
        }
        baseKey = new Spinner(this);
        ArrayAdapter<String> adapter = new ArrayAdapter<>(this,
                android.R.layout.simple_spinner_item, labels);
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        baseKey.setAdapter(adapter);
        baseKey.setBackgroundColor(PANEL);
        baseKey.setPadding(dp(12), 0, dp(12), 0);
        baseKey.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(AdapterView<?> parent, View view,
                                                  int position, long id) {
                updatePreview();
            }
            @Override public void onNothingSelected(AdapterView<?> parent) {
            }
        });
        page.addView(baseKey, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(54)));

        preview = text("", 17, PRIMARY, Typeface.BOLD);
        preview.setGravity(Gravity.CENTER);
        preview.setPadding(dp(14), dp(14), dp(14), dp(14));
        preview.setBackgroundColor(PANEL);
        page.addView(preview, topMargin(dp(20)));

        Button use = button("使用这个组合键");
        use.setTextColor(BACKGROUND);
        use.setBackgroundColor(PRIMARY);
        use.setOnClickListener(view -> finishWithSelection());
        page.addView(use, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(58)));
        return scroll;
    }

    private CheckBox modifier(String label, LinearLayout parent) {
        CheckBox checkBox = new CheckBox(this);
        checkBox.setText(label);
        checkBox.setTextColor(TEXT);
        checkBox.setTextSize(16);
        checkBox.setPadding(dp(8), dp(5), dp(8), dp(5));
        checkBox.setOnCheckedChangeListener((button, checked) -> updatePreview());
        parent.addView(checkBox);
        return checkBox;
    }

    private void loadInitialKeys() {
        ArrayList<String> initial = getIntent().getStringArrayListExtra(EXTRA_KEYS);
        if (initial == null || initial.isEmpty()) {
            return;
        }
        ctrl.setChecked(initial.contains("CTRL"));
        shift.setChecked(initial.contains("SHIFT"));
        alt.setChecked(initial.contains("ALT"));
        win.setChecked(initial.contains("WIN"));
        for (String key : initial) {
            int position = KeyCatalog.BASE_KEYS.indexOf(key);
            if (position >= 0) {
                baseKey.setSelection(position);
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
        keys.add(KeyCatalog.BASE_KEYS.get(baseKey.getSelectedItemPosition()));
        return KeyCatalog.normalizeChord(keys);
    }

    private void updatePreview() {
        if (preview == null || baseKey == null) {
            return;
        }
        try {
            preview.setText("预览：" + KeyCatalog.displayChord(selectedKeys()));
            preview.setTextColor(PRIMARY);
        } catch (Exception exception) {
            preview.setText("组合键最多 4 个键");
            preview.setTextColor(DANGER);
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
            preview.setTextColor(DANGER);
        }
    }

    private Button button(String label) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextColor(TEXT);
        button.setTextSize(15);
        button.setAllCaps(false);
        button.setBackgroundColor(PANEL);
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
