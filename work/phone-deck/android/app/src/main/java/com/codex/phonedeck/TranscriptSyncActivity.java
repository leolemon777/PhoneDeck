package com.codex.phonedeck;

import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

public final class TranscriptSyncActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private TextView status;
    private LinearLayout history;
    private PhoneDeckTheme theme;
    private String rendered = "";
    private final Runnable refresh = new Runnable() { public void run() { render(); handler.postDelayed(this, 2000); } };
    @Override public void onCreate(Bundle state) {
        super.onCreate(state); theme = PhoneDeckTheme.load(this); theme.applyWindow(this);
        LinearLayout page = new LinearLayout(this); page.setOrientation(LinearLayout.VERTICAL); page.setPadding(32, 24, 32, 32);
        Button back = new Button(this); back.setText("返回设置"); back.setOnClickListener(v -> finish()); page.addView(back);
        TextView title = new TextView(this); title.setText("同步文字"); title.setTextSize(24); title.setTextColor(theme.text); page.addView(title);
        TextView hint = new TextView(this); hint.setText("同一手机配对的电脑可通过手机转发最终文字。只发给下面勾选的共享组电脑；接收后保留记录，不自动输入或回车。手机页面打开或共享麦克风运行时同步。\n\n需要2.0跨平台预览接收端；旧版Typeless接收端仅共享声音。文字仅在内存保留30分钟，断线重试也不写磁盘。"); hint.setTextColor(theme.muted); page.addView(hint);
        Switch enabled = new Switch(this); enabled.setText("同步最终文字"); enabled.setTextColor(theme.text); enabled.setChecked(TranscriptRelay.enabled(this));
        enabled.setOnCheckedChangeListener((v, on) -> { TranscriptRelay.setEnabled(this, on); render(); }); page.addView(enabled);
        TargetDeviceManager devices = TargetDeviceManager.get(this);
        for (TargetDeviceManager.Device d : devices.list()) {
            CheckBox check = new CheckBox(this); check.setText(d.displayName + " · " + d.platform); check.setTextColor(theme.text); check.setChecked(d.sharedGroup);
            check.setEnabled(d.hasClientCredential()); check.setOnCheckedChangeListener((v, on) -> { devices.setSharedGroup(d.computerId, on); render(); }); page.addView(check);
        }
        TextView groupHint = new TextView(this); groupHint.setText("此选择也用于共享麦克风。改变组成员后，重开共享麦克风让供音目标生效。"); groupHint.setTextColor(theme.muted); page.addView(groupHint);
        status = new TextView(this); status.setTextColor(theme.muted); page.addView(status);
        Button clear = new Button(this); clear.setText("清空手机记录"); clear.setOnClickListener(v -> { TranscriptRelay.clearHistory(); rendered = ""; render(); }); page.addView(clear);
        history = new LinearLayout(this); history.setOrientation(LinearLayout.VERTICAL); page.addView(history);
        ScrollView scroll = new ScrollView(this); scroll.addView(page); setContentView(theme.wrapContent(this, scroll));
        TranscriptRelay.acquire(this); handler.post(refresh);
    }
    private void render() {
        if (status == null) return; status.setText(TranscriptRelay.status());
        java.util.List<String> texts = TranscriptRelay.texts(); String key = texts.toString(); if (key.equals(rendered)) return; rendered = key;
        history.removeAllViews();
        for (String text : texts) {
            TextView value = new TextView(this); value.setText(text); value.setTextSize(16); value.setTextColor(theme.text); value.setTextIsSelectable(true); history.addView(value);
            Button copy = new Button(this); copy.setText("复制文字"); copy.setOnClickListener(v -> {
                ((ClipboardManager)getSystemService(CLIPBOARD_SERVICE)).setPrimaryClip(ClipData.newPlainText(getString(R.string.brand_name), text));
                Toast.makeText(this, "已复制", Toast.LENGTH_SHORT).show();
            }); history.addView(copy);
        }
    }
    @Override public void onDestroy() { handler.removeCallbacks(refresh); TranscriptRelay.release(); super.onDestroy(); }
}
