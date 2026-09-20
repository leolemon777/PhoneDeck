package com.codex.phonedeck;

import java.io.IOException;
import java.io.OutputStream;
import java.net.HttpURLConnection;

/** Finish PCM as an HTTP request, rather than aborting its socket at stop. */
final class AudioUpload {
    static final int FINISH_TIMEOUT_MS = 8_000;

    private AudioUpload() {}

    static void finish(HttpURLConnection connection, OutputStream output) throws IOException {
        output.flush();
        // Sends the terminating chunk. flush() alone does not signal EOF.
        output.close();
        int status = connection.getResponseCode();
        if (status < 200 || status >= 300) {
            throw new IOException("电脑未确认音频收尾（HTTP " + status + "）");
        }
    }

    static void abortAfter(Thread worker, int timeoutMs, Runnable abort) {
        if (worker == null) {
            return;
        }
        Thread watchdog = new Thread(() -> {
            try {
                worker.join(timeoutMs);
                if (worker.isAlive()) {
                    abort.run();
                }
            } catch (InterruptedException exception) {
                Thread.currentThread().interrupt();
            }
        }, "PhoneDeck-AudioFinishTimeout");
        watchdog.setDaemon(true);
        watchdog.start();
    }
}
