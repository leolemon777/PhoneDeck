package com.codex.phonedeck;

import android.content.Context;
import android.os.SystemClock;
import org.json.JSONArray;
import org.json.JSONObject;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

/** Explicitly enabled, bounded in-memory final-text relay. Never types or submits remote text. */
final class TranscriptRelay {
    private static final String PREFS = "PhoneDeckTranscriptSync";
    private static final long RETENTION_MS = 30 * 60 * 1000L;
    private static final ScheduledExecutorService WORKER = Executors.newSingleThreadScheduledExecutor(r -> {
        Thread t = new Thread(r, "PhoneDeck-TranscriptRelay"); t.setDaemon(true); return t;
    });
    private static Context app;
    private static int users, generation;
    private static boolean scheduled;
    private static volatile String status = "同步未开启";
    private static final Map<String, Long> cursors = new HashMap<>();
    private static final Map<String, CachedEndpoint> endpoints = new HashMap<>();
    private static final LinkedHashMap<String, Pending> pending = new LinkedHashMap<>();
    private static final List<JSONObject> history = new ArrayList<>();
    private static final class CachedEndpoint {
        final PhoneDeckEndpoint endpoint; final long checked; final String token;
        CachedEndpoint(PhoneDeckEndpoint e, String token) { endpoint = e; this.token = token; checked = SystemClock.elapsedRealtime(); }
    }
    private static final class Pending {
        final JSONObject result; final long received = SystemClock.elapsedRealtime(); final Set<String> targets;
        Pending(JSONObject r, Set<String> targets) { result = r; this.targets = targets; }
    }
    private TranscriptRelay() { }
    static synchronized void acquire(Context context) {
        app = context.getApplicationContext(); users++;
        if (!scheduled) { scheduled = true; WORKER.scheduleWithFixedDelay(TranscriptRelay::tick, 0, 2, TimeUnit.SECONDS); }
    }
    static synchronized void release() { if (users > 0) users--; }
    static boolean enabled(Context c) { return c.getApplicationContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean("enabled", false); }
    static synchronized void setEnabled(Context c, boolean value) {
        c.getApplicationContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean("enabled", value).apply();
        generation++; cursors.clear(); endpoints.clear(); pending.clear(); history.clear(); status = value ? "等待共享组电脑连接" : "同步未开启";
    }
    static String status() { return status; }
    static synchronized List<String> texts() {
        List<String> result = new ArrayList<>();
        for (JSONObject item : history) result.add(item.optString("text"));
        return result;
    }
    static synchronized void clearHistory() { history.clear(); }
    private static void tick() {
        Context context; int ticket;
        synchronized (TranscriptRelay.class) { if (users == 0 || app == null || !enabled(app)) return; context = app; ticket = generation; }
        try { synchronize(context, ticket); }
        catch (Exception e) { if (valid(context, ticket)) status = "同步暂未完成，将自动重试"; } // Never log recognized text or credentials.
    }
    private static synchronized boolean valid(Context c, int ticket) { return users > 0 && ticket == generation && enabled(c); }
    private static void synchronize(Context context, int ticket) throws Exception {
        List<TargetDeviceManager.Device> group = new ArrayList<>();
        for (TargetDeviceManager.Device device : TargetDeviceManager.get(context).list())
            if (device.sharedGroup && device.hasLanPairing() && device.hasClientCredential()) group.add(device);
        Map<String, PhoneDeckEndpoint> online = new HashMap<>(); Set<String> selected = new HashSet<>(); int unsupported = 0;
        for (TargetDeviceManager.Device device : group) {
            if (!valid(context, ticket)) return;
            selected.add(device.computerId);
            PhoneDeckEndpoint endpoint = resolve(device, ticket);
            if (endpoint == null) { unsupported++; continue; }
            online.put(device.computerId, endpoint);
            long after;
            synchronized (TranscriptRelay.class) { after = cursors.getOrDefault(device.computerId, 0L); }
            try {
                JSONObject reply = PhoneDeckHttp.getJson(endpoint, "/api/transcripts?after=" + after + "&targetComputerId=" + device.computerId, 800, 1600);
                if (!device.computerId.equals(reply.optString("computerId"))) continue;
                long cursor = reply.optLong("cursor", -1); if (cursor < 0) continue;
                JSONArray results = reply.optJSONArray("results"); if (results == null || results.length() > 100) continue;
                synchronized (TranscriptRelay.class) {
                    if (!valid(context, ticket)) return;
                    // The receiver restarted: its memory cursor starts at zero again.
                    if (cursor < after) { cursors.put(device.computerId, 0L); continue; }
                    for (int i = 0; i < results.length(); i++) {
                        JSONObject result = results.optJSONObject(i);
                        if (!validResult(result, device.computerId)) continue;
                        String id = result.getString("resultId"); String key = device.computerId + ":" + id;
                        if (pending.containsKey(key)) continue;
                        Set<String> destinations = new HashSet<>(selected);
                        // Include all selected devices, including ones probed later in this loop.
                        for (TargetDeviceManager.Device d : group) destinations.add(d.computerId);
                        destinations.remove(device.computerId);
                        pending.put(key, new Pending(new JSONObject(result.toString()), destinations));
                        history.add(0, new JSONObject(result.toString())); if (history.size() > 100) history.remove(history.size() - 1);
                        while (pending.size() > 100) pending.remove(pending.keySet().iterator().next());
                    }
                    cursors.put(device.computerId, cursor);
                }
            } catch (Exception e) { synchronized (TranscriptRelay.class) { endpoints.remove(device.computerId); } }
        }
        List<Pending> deliveries;
        synchronized (TranscriptRelay.class) {
            if (!valid(context, ticket)) return;
            pending.values().removeIf(p -> SystemClock.elapsedRealtime() - p.received > RETENTION_MS);
            // Phone history follows the same 30-minute memory retention as desktop history.
            Set<String> retained = new HashSet<>(); for (Pending p : pending.values()) retained.add(p.result.optString("resultId"));
            history.removeIf(h -> !retained.contains(h.optString("resultId")));
            deliveries = new ArrayList<>(pending.values());
        }
        for (Pending item : deliveries) {
            Set<String> targets;
            synchronized (TranscriptRelay.class) { item.targets.retainAll(selected); targets = new HashSet<>(item.targets); }
            for (String target : targets) {
                if (!valid(context, ticket)) return; PhoneDeckEndpoint endpoint = online.get(target); if (endpoint == null) continue;
                try {
                    JSONObject body = new JSONObject().put("targetComputerId", target).put("result", item.result);
                    JSONObject ack = PhoneDeckHttp.postJson(endpoint, "/api/transcripts/receive", body, 1800);
                    if (item.result.optString("resultId").equals(ack.optString("resultId")))
                        synchronized (TranscriptRelay.class) { if (valid(context, ticket)) item.targets.remove(target); }
                } catch (Exception e) { /* Retry the same resultId; receivers deduplicate. */ }
            }
        }
        synchronized (TranscriptRelay.class) {
            if (!valid(context, ticket)) return;
            int waiting = 0; for (Pending p : pending.values()) waiting += p.targets.size();
            status = "已连接 " + online.size() + "/" + group.size() + " 台 · 待同步 " + waiting + " 份"
                    + (unsupported > 0 ? "\n离线或旧接收端暂不支持文字同步，请使用2.0跨平台预览包。" : "");
        }
    }
    private static PhoneDeckEndpoint resolve(TargetDeviceManager.Device device, int ticket) {
        synchronized (TranscriptRelay.class) {
            CachedEndpoint cached = endpoints.get(device.computerId);
            if (cached != null && cached.token.equals(device.lanToken) && SystemClock.elapsedRealtime() - cached.checked < 30000) return cached.endpoint;
        }
        PhoneDeckLanClient.ProbeResult probe = PhoneDeckLanClient.probe(device); if (probe == null) return null;
        JSONArray capabilities = probe.health.optJSONArray("capabilities"); boolean supported = false;
        for (int i = 0; capabilities != null && i < capabilities.length(); i++)
            if ("transcriptSyncV1".equals(capabilities.optString(i))) supported = true;
        if (!supported) return null;
        synchronized (TranscriptRelay.class) { if (ticket != generation) return null; endpoints.put(device.computerId, new CachedEndpoint(probe.endpoint, device.lanToken)); }
        return probe.endpoint;
    }
    static boolean validResult(JSONObject result, String source) {
        try {
            if (result == null || !source.equals(result.getString("sourceComputerId"))) return false;
            UUID.fromString(result.getString("resultId")); UUID.fromString(result.getString("sessionId")); UUID.fromString(source);
            String text = result.getString("text"); return !text.isBlank() && text.length() <= 4096 && text.indexOf('\0') < 0;
        } catch (Exception e) { return false; }
    }
}
