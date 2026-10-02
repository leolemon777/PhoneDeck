package com.codex.phonedeck;

import org.json.JSONArray;
import org.json.JSONObject;

final class RemoteVoiceState {
    final Boolean dictationActive;
    final String sessionId;
    final Boolean capturing;
    final boolean stale;
    final long probeStartedAt;
    final long sampleAgeMs;
    final String stopRequestedSessionId;

    private RemoteVoiceState(Boolean active, String sessionId, Boolean capturing,
                             boolean stale, long probeStartedAt, long sampleAgeMs,
                             String stopRequestedSessionId) {
        this.dictationActive = active;
        this.sessionId = sessionId;
        this.capturing = capturing;
        this.stale = stale;
        this.probeStartedAt = probeStartedAt;
        this.sampleAgeMs = sampleAgeMs;
        this.stopRequestedSessionId = stopRequestedSessionId;
    }

    static RemoteVoiceState fromHealth(JSONObject health, long probeStartedAt) {
        JSONObject dictation = health == null ? null : health.optJSONObject("dictation");
        JSONObject probe = health == null ? null : health.optJSONObject("typeless");
        Object age = probe == null ? null : probe.opt("ageMs");
        long ageMs = age instanceof Number ? ((Number) age).longValue() : age == null ? 0 : -1;
        boolean stale = probe == null || (probe.has("stale")
                && !Boolean.FALSE.equals(RemoteStopPolicy.knownBoolean(probe.opt("stale"))));
        return new RemoteVoiceState(
                dictation == null ? null : RemoteStopPolicy.knownBoolean(dictation.opt("active")),
                nullableString(dictation, "sessionId"),
                probe == null ? null : RemoteStopPolicy.knownBoolean(probe.opt("capturing")),
                stale, probeStartedAt, ageMs, stopRequest(health));
    }

    static String stopRequest(JSONObject health) {
        JSONArray capabilities = health == null ? null : health.optJSONArray("capabilities");
        if (capabilities != null) {
            for (int i = 0; i < capabilities.length(); i++) {
                if ("phoneStopV1".equals(capabilities.optString(i))) {
                    return nullableString(health.optJSONObject("audio"), "stopRequestedSessionId");
                }
            }
        }
        return null;
    }

    private static String nullableString(JSONObject node, String key) {
        Object value = node == null ? null : node.opt(key);
        return value instanceof String && !((String) value).isBlank() ? (String) value : null;
    }
}
