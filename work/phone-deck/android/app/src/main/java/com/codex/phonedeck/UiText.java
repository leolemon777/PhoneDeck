package com.codex.phonedeck;

/** Presentation-only normalization; never changes device identity or protocol data. */
final class UiText {
    private UiText() { }

    static String optional(String value) {
        if (value == null || value.isBlank() || "null".equalsIgnoreCase(value.trim())) return "";
        return value.trim();
    }

    static String connectionDetail(String deviceName, String message) {
        String prefix = optional(deviceName) + " · ";
        return message.startsWith(prefix) ? message.substring(prefix.length()) : message;
    }
}
