package com.codex.phonedeck;

import android.Manifest;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothServerSocket;
import android.bluetooth.BluetoothSocket;
import android.content.Context;
import android.content.pm.PackageManager;
import android.os.Build;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

final class BluetoothTransport implements AutoCloseable {
    static final UUID SERVICE_UUID = UUID.fromString("7d2ea28a-f7bd-485a-bd9d-92ad6ecfe93e");

    interface Listener {
        void onStateChanged(boolean connected, String detail);
    }

    private final Context context;
    private final Listener listener;
    private final Object writeLock = new Object();
    private final ConcurrentHashMap<String, PendingAck> pendingAcks =
            new ConcurrentHashMap<>();
    private final BluetoothAdapter adapter;

    private volatile boolean closed;
    private volatile BluetoothServerSocket serverSocket;
    private volatile BluetoothSocket socket;
    private volatile OutputStream output;
    private Thread worker;

    BluetoothTransport(Context context, Listener listener) {
        this.context = context.getApplicationContext();
        this.listener = listener;
        this.adapter = BluetoothAdapter.getDefaultAdapter();
    }

    boolean isSupported() {
        return adapter != null;
    }

    boolean isEnabled() {
        return adapter != null && hasPermission() && adapter.isEnabled();
    }

    boolean isConnected() {
        BluetoothSocket current = socket;
        return current != null && current.isConnected() && output != null;
    }

    synchronized void start() {
        if (closed || worker != null && worker.isAlive()) {
            return;
        }
        if (adapter == null) {
            listener.onStateChanged(false, "本机不支持蓝牙");
            return;
        }
        if (!hasPermission()) {
            listener.onStateChanged(false, "需要蓝牙权限");
            return;
        }
        if (!adapter.isEnabled()) {
            listener.onStateChanged(false, "手机蓝牙未开启");
            return;
        }

        worker = new Thread(this::acceptLoop, "PhoneDeck-Bluetooth");
        worker.start();
    }

    boolean sendAndWaitForAck(JSONObject message, int timeoutMilliseconds) {
        String requestId = message.optString("requestId", "");
        if (requestId.isEmpty()) {
            return false;
        }

        byte[] bytes = (message.toString() + "\n").getBytes(StandardCharsets.UTF_8);
        PendingAck pendingAck = new PendingAck();
        pendingAcks.put(requestId, pendingAck);
        synchronized (writeLock) {
            OutputStream current = output;
            if (current == null) {
                pendingAcks.remove(requestId);
                return false;
            }
            try {
                current.write(bytes);
                current.flush();
            } catch (IOException exception) {
                pendingAcks.remove(requestId);
                disconnect();
                start();
                return false;
            }
        }

        try {
            return pendingAck.completed.await(timeoutMilliseconds, TimeUnit.MILLISECONDS)
                    && pendingAck.accepted;
        } catch (InterruptedException exception) {
            Thread.currentThread().interrupt();
            return false;
        } finally {
            pendingAcks.remove(requestId);
        }
    }

    private void acceptLoop() {
        while (!closed && adapter != null && adapter.isEnabled()) {
            try {
                listener.onStateChanged(false, "等待电脑蓝牙连接");
                serverSocket = adapter.listenUsingRfcommWithServiceRecord("PhoneDeck", SERVICE_UUID);
                BluetoothSocket accepted = serverSocket.accept();
                closeServerSocket();

                socket = accepted;
                output = accepted.getOutputStream();
                String deviceName = accepted.getRemoteDevice().getName();
                listener.onStateChanged(true,
                        deviceName == null ? "蓝牙已连接" : "蓝牙已连接 · " + deviceName);

                monitorConnection(accepted.getInputStream());
            } catch (SecurityException exception) {
                listener.onStateChanged(false, "需要蓝牙权限");
                break;
            } catch (IOException exception) {
                if (!closed) {
                    listener.onStateChanged(false, "蓝牙正在重新连接");
                }
            } finally {
                disconnect();
            }

            if (!closed) {
                try {
                    Thread.sleep(600);
                } catch (InterruptedException ignored) {
                    Thread.currentThread().interrupt();
                    break;
                }
            }
        }
        worker = null;
    }

    private void monitorConnection(InputStream input) throws IOException {
        byte[] received = new byte[512];
        ByteArrayOutputStream pending = new ByteArrayOutputStream(512);
        int count;
        while (!closed && (count = input.read(received)) >= 0) {
            for (int index = 0; index < count; index++) {
                int value = received[index] & 0xff;
                if (value == '\n') {
                    handleIncomingLine(pending.toByteArray());
                    pending.reset();
                } else if (pending.size() < 32 * 1024) {
                    pending.write(value);
                } else {
                    pending.reset();
                }
            }
        }
    }

    private void handleIncomingLine(byte[] bytes) {
        if (bytes.length == 0) {
            return;
        }
        try {
            JSONObject message = new JSONObject(new String(bytes, StandardCharsets.UTF_8));
            if (!"ack".equals(message.optString("type"))) {
                return;
            }
            String requestId = message.optString("requestId", "");
            PendingAck pendingAck = pendingAcks.get(requestId);
            if (pendingAck != null) {
                pendingAck.accepted = message.optBoolean("ok", false);
                pendingAck.completed.countDown();
            }
        } catch (Exception ignored) {
            // 忽略心跳之外的损坏消息，保持蓝牙会话继续运行。
        }
    }

    private boolean hasPermission() {
        return Build.VERSION.SDK_INT < Build.VERSION_CODES.S
                || context.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT)
                == PackageManager.PERMISSION_GRANTED;
    }

    private void disconnect() {
        output = null;
        for (PendingAck pendingAck : pendingAcks.values()) {
            pendingAck.accepted = false;
            pendingAck.completed.countDown();
        }
        pendingAcks.clear();
        BluetoothSocket current = socket;
        socket = null;
        if (current != null) {
            try {
                current.close();
            } catch (IOException ignored) {
            }
        }
        closeServerSocket();
    }

    private void closeServerSocket() {
        BluetoothServerSocket current = serverSocket;
        serverSocket = null;
        if (current != null) {
            try {
                current.close();
            } catch (IOException ignored) {
            }
        }
    }

    @Override
    public synchronized void close() {
        closed = true;
        disconnect();
        if (worker != null) {
            worker.interrupt();
        }
    }

    private static final class PendingAck {
        private final CountDownLatch completed = new CountDownLatch(1);
        private volatile boolean accepted;
    }
}
