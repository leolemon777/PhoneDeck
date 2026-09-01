package com.codex.phonedeck;

import android.os.Handler;
import android.os.Looper;
import android.util.Log;

import org.json.JSONObject;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * 负责从当前连接的目标电脑拉取并增量同步 Agent 快捷指令配置（/plan, /goal, /compact, /clear 等）。
 */
final class AgentSyncManager {
    interface Listener {
        void onAgentShortcutsSynced();
    }

    private static final String LOG_TAG = "PhoneDeckSync";
    private final ShortcutConfigRepository repository;
    private final Listener listener;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final ExecutorService syncExecutor = Executors.newSingleThreadExecutor();

    AgentSyncManager(ShortcutConfigRepository repository, Listener listener) {
        this.repository = repository;
        this.listener = listener;
    }

    void sync(PhoneDeckEndpoint endpoint) {
        if (endpoint == null) {
            return;
        }
        syncExecutor.execute(() -> {
            try {
                JSONObject remote = PhoneDeckHttp.getJson(
                        endpoint, "/api/config/agent-shortcuts", 700, 1000);
                if (remote != null && repository.applyAgentOverrides(remote)) {
                    Log.i(LOG_TAG, "Agent 指令已从电脑成功同步");
                    mainHandler.post(() -> {
                        if (listener != null) {
                            listener.onAgentShortcutsSynced();
                        }
                    });
                }
            } catch (Exception exception) {
                // 旧版接收端未提供该接口或网络异常时，静默保留手机本地配置。
                Log.d(LOG_TAG, "Agent 指令同步跳过：" + exception.getMessage());
            }
        });
    }

    void shutdown() {
        syncExecutor.shutdownNow();
    }
}
