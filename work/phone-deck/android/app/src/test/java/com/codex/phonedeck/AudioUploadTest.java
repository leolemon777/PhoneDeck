package com.codex.phonedeck;

import static org.junit.Assert.*;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import org.junit.Test;

public final class AudioUploadTest {
    @Test
    public void queuedTailIsClosedAndAcknowledgedBeforeDisconnect() throws Exception {
        FakeConnection connection = new FakeConnection();
        connection.body.write(new byte[] { 11, 12, 13, 14 });

        AudioUpload.finish(connection, connection.body);

        assertArrayEquals(new byte[] { 11, 12, 13, 14 }, connection.body.toByteArray());
        assertTrue(connection.eof);
        assertTrue(connection.acknowledged);
        assertFalse(connection.disconnected);
    }

    @Test
    public void failedReceiverResponseCannotReportSuccessfulTail() throws Exception {
        FakeConnection connection = new FakeConnection();
        connection.status = 500;
        assertThrows(IOException.class, () -> AudioUpload.finish(connection, connection.body));
        assertTrue(connection.eof);
    }

    @Test
    public void blockedFinishIsAbortedWithinBoundedTime() throws Exception {
        CountDownLatch release = new CountDownLatch(1);
        CountDownLatch aborted = new CountDownLatch(1);
        Thread worker = new Thread(() -> {
            try { release.await(); }
            catch (InterruptedException error) { Thread.currentThread().interrupt(); }
        });
        worker.start();
        try {
            AudioUpload.abortAfter(worker, 50, () -> {
                aborted.countDown();
                release.countDown();
            });
            assertTrue(aborted.await(2, TimeUnit.SECONDS));
        } finally {
            release.countDown();
            worker.join(2_000);
        }
    }

    private static final class FakeConnection extends HttpURLConnection {
        boolean eof;
        boolean acknowledged;
        boolean disconnected;
        int status = 200;
        final ByteArrayOutputStream body = new ByteArrayOutputStream() {
            @Override public void close() { eof = true; }
        };

        FakeConnection() throws Exception { super(new URL("http://127.0.0.1/audio")); }
        @Override public int getResponseCode() {
            assertTrue("Response must be read after the terminating HTTP chunk", eof);
            assertFalse(disconnected);
            acknowledged = true;
            return status;
        }
        @Override public void disconnect() { disconnected = true; }
        @Override public boolean usingProxy() { return false; }
        @Override public void connect() {}
    }
}
