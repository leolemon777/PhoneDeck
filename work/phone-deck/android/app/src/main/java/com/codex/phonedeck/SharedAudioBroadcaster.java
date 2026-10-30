package com.codex.phonedeck;

import android.Manifest;
import android.content.Context;
import android.content.pm.PackageManager;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.os.SystemClock;

import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.util.ArrayDeque;
import java.util.Arrays;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import java.util.Objects;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.Semaphore;

/** One AudioRecord source fanned out to independent, bounded receiver queues. */
final class SharedAudioBroadcaster implements AutoCloseable {
    interface Listener {
        void onLevel(String sessionId, int percent);
        void onConnectionsChanged(String sessionId);
        void onStopped(String sessionId, String reason);
    }

    static final class Target {
        final String computerId;
        final String displayName;
        final PhoneDeckEndpoint endpoint;

        Target(String computerId, String displayName, PhoneDeckEndpoint endpoint) {
            this.computerId = computerId;
            this.displayName = displayName;
            this.endpoint = endpoint;
        }

        boolean sameEndpoint(Target other) {
            if (other == null) {
                return false;
            }
            // 地址相同不等于连接身份相同：配对凭据、证书或逐手机 clientId
            // 轮换后必须拆掉旧 sink，不能让它继续持有旧授权。
            boolean sameComputer = computerId == null
                    ? other.computerId == null
                    : other.computerId != null && computerId.equalsIgnoreCase(other.computerId);
            return sameComputer
                    && Objects.equals(endpoint.baseUrl, other.endpoint.baseUrl)
                    && Objects.equals(endpoint.accessToken, other.endpoint.accessToken)
                    && Objects.equals(endpoint.clientId, other.endpoint.clientId)
                    && Objects.equals(endpoint.certificateSha256, other.endpoint.certificateSha256);
        }
    }

    private static final int SAMPLE_RATE = 48_000;
    private static final int CHUNK_BYTES = SAMPLE_RATE * 2 * 20 / 1_000;
    // 240 ms：吸收常见 Wi-Fi 抖动；满了优先丢静音（见 FrameQueue），接收端再跳过积压静音。
    private static final int QUEUE_FRAMES = 12;
    /// A platform socket can ignore both interrupt and disconnect for a while.
    /// Keep process-wide worker growth bounded across service sessions, and
    /// reserve only one live worker for each normalized computer ID.
    private static final int MAX_SHARED_WORKERS = TargetDeviceManager.MAX_DEVICES * 2;
    private static final Semaphore WORKER_PERMITS =
            new Semaphore(MAX_SHARED_WORKERS, true);
    private static final ConcurrentHashMap<String, WorkerLease> WORKER_LEASES =
            new ConcurrentHashMap<>();
    /// 共享模式长时间（含锁屏）供音：两帧 20 ms 合并成一个约 40 ms 的网络包，
    /// TLS 记录与无线唤醒次数减半，代价是最多多出 20 ms 延迟。
    static final long BATCH_WAIT_MS = 25;

    private static final class WorkerLease {
        final String computerKey;

        WorkerLease(String computerKey) {
            this.computerKey = computerKey;
        }
    }

    private static String normalizeWorkerKey(String computerId) {
        if (computerId == null) {
            return "";
        }
        return computerId.trim().toLowerCase(Locale.ROOT);
    }

    private static WorkerLease claimWorker(String computerId) {
        if (!WORKER_PERMITS.tryAcquire()) {
            return null;
        }
        WorkerLease lease = new WorkerLease(normalizeWorkerKey(computerId));
        WorkerLease previous = WORKER_LEASES.putIfAbsent(lease.computerKey, lease);
        if (previous != null) {
            WORKER_PERMITS.release();
            return null;
        }
        return lease;
    }

    private static void releaseWorker(WorkerLease lease) {
        if (lease != null && WORKER_LEASES.remove(lease.computerKey, lease)) {
            WORKER_PERMITS.release();
        }
    }

    private final Context context;
    private final Listener listener;
    private final Object stateLock = new Object();
    private final ConcurrentHashMap<String, TargetSink> sinks = new ConcurrentHashMap<>();
    private volatile boolean running;
    private volatile AudioRecord recorder;
    private volatile String sessionId;
    private Thread captureThread;

    SharedAudioBroadcaster(Context context, Listener listener) {
        this.context = context.getApplicationContext();
        this.listener = listener;
    }

    boolean isRunning() {
        return running;
    }

    boolean isFinishing() {
        synchronized (stateLock) {
            return !running && captureThread != null;
        }
    }

    String getSessionId() {
        return sessionId;
    }

    boolean start(String newSessionId) {
        synchronized (stateLock) {
            if (captureThread != null && captureThread.isAlive()) {
                return false;
            }
            if (context.checkSelfPermission(Manifest.permission.RECORD_AUDIO)
                    != PackageManager.PERMISSION_GRANTED) {
                throw new SecurityException("没有手机麦克风权限");
            }
            sessionId = newSessionId;
            running = true;
            captureThread = new Thread(() -> captureLoop(newSessionId), "PhoneDeck-SharedCapture");
            captureThread.start();
            return true;
        }
    }

    void updateTargets(Map<String, Target> requested) {
        java.util.List<TargetSink> stopped;
        synchronized (stateLock) {
            stopped = updateTargetsLocked(requested);
        }
        stopSinks(stopped);
    }

    /**
     * Applies a probe result only while the expected audio session is still
     * current.  The operation intentionally runs on the probe executor: an
     * old sink may need {@link HttpURLConnection#disconnect()} and must never
     * block the service main thread during stop or network handover.
     */
    boolean updateTargetsIfCurrent(Map<String, Target> requested,
                                   String expectedSessionId,
                                   java.util.function.BooleanSupplier guard) {
        java.util.List<TargetSink> stopped;
        synchronized (stateLock) {
            if (!running || expectedSessionId == null
                    || !expectedSessionId.equals(sessionId)
                    || guard != null && !guard.getAsBoolean()) {
                return false;
            }
            stopped = updateTargetsLocked(requested);
        }
        // Disconnecting a stale sink can touch a blocked socket.  Keep it
        // outside stateLock; this method is called on the probe executor.
        stopSinks(stopped);
        return true;
    }

    private java.util.List<TargetSink> updateTargetsLocked(Map<String, Target> requested) {
        java.util.List<TargetSink> stopped = new java.util.ArrayList<>();
        if (!running) {
            return stopped;
        }
        Map<String, Target> desired = new HashMap<>(requested);
        for (Map.Entry<String, TargetSink> entry : sinks.entrySet()) {
            Target next = desired.remove(entry.getKey());
            TargetSink existing = entry.getValue();
            if (next == null || !existing.target.sameEndpoint(next) || !existing.isAlive()) {
                if (sinks.remove(entry.getKey(), existing)) {
                    stopped.add(existing);
                }
                if (next != null) {
                    addSink(next);
                }
            }
        }
        for (Target target : desired.values()) {
            addSink(target);
        }
        notifyConnections(sessionId);
        return stopped;
    }

    private static void stopSinks(java.util.List<TargetSink> stopped) {
        for (TargetSink sink : stopped) {
            sink.stop();
        }
    }

    /** Returns only the in-memory sink connection state; never performs I/O. */
    Map<String, Boolean> connectionSnapshot() {
        synchronized (stateLock) {
            Map<String, Boolean> snapshot = new HashMap<>();
            for (Map.Entry<String, TargetSink> entry : sinks.entrySet()) {
                snapshot.put(entry.getKey(), entry.getValue().connected);
            }
            return snapshot;
        }
    }

    private void addSink(Target target) {
        TargetSink sink = new TargetSink(target, sessionId);
        TargetSink previous = sinks.putIfAbsent(target.computerId, sink);
        if (previous == null) {
            sink.start();
        }
    }

    @android.annotation.SuppressLint("MissingPermission")
    private void captureLoop(String captureSessionId) {
        android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO);
        AudioRecord localRecorder = null;
        String failure = null;
        try {
            if (context.checkSelfPermission(Manifest.permission.RECORD_AUDIO)
                    != PackageManager.PERMISSION_GRANTED) {
                throw new SecurityException("手机麦克风权限已被撤销");
            }
            int minimum = AudioRecord.getMinBufferSize(
                    SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT);
            if (minimum <= 0) {
                throw new IllegalStateException("手机不支持 48 kHz 麦克风采集");
            }
            localRecorder = new AudioRecord(
                    MediaRecorder.AudioSource.VOICE_RECOGNITION,
                    SAMPLE_RATE,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT,
                    Math.max(minimum * 2, 8192));
            if (localRecorder.getState() != AudioRecord.STATE_INITIALIZED) {
                throw new IllegalStateException("无法初始化手机麦克风");
            }
            synchronized (stateLock) {
                if (!running) {
                    return;
                }
                recorder = localRecorder;
                localRecorder.startRecording();
            }
            byte[] chunk = new byte[CHUNK_BYTES];
            long lastLevelAt = 0;
            while (running) {
                int count = localRecorder.read(chunk, 0, chunk.length, AudioRecord.READ_BLOCKING);
                if (count <= 0) {
                    if (running) {
                        throw new IllegalStateException("手机麦克风读取中断：" + count);
                    }
                    break;
                }
                byte[] frame = Arrays.copyOf(chunk, count);
                for (TargetSink sink : sinks.values()) {
                    sink.offer(frame);
                }
                long now = SystemClock.elapsedRealtime();
                if (now - lastLevelAt >= 100) {
                    listener.onLevel(captureSessionId, calculateLevel(chunk, count));
                    lastLevelAt = now;
                }
            }
        } catch (Exception exception) {
            if (running) {
                failure = exception.getMessage();
            }
        } finally {
            synchronized (stateLock) {
                running = false;
                recorder = null;
            }
            for (TargetSink sink : sinks.values()) {
                sink.finish();
            }
            if (localRecorder != null) {
                try {
                    if (localRecorder.getRecordingState() == AudioRecord.RECORDSTATE_RECORDING) {
                        localRecorder.stop();
                    }
                } catch (IllegalStateException ignored) {
                }
                localRecorder.release();
            }
            // All sinks drain in parallel; one slow receiver gets a bounded budget.
            long deadline = SystemClock.elapsedRealtime()
                    + SharedAudioPolicies.SHARED_FINISH_TIMEOUT_MS;
            for (TargetSink sink : sinks.values()) {
                try {
                    long remaining = deadline - SystemClock.elapsedRealtime();
                    if (remaining > 0 && sink.worker != null) {
                        sink.worker.join(remaining);
                    }
                } catch (InterruptedException exception) {
                    Thread.currentThread().interrupt();
                }
                if (sink.worker != null && sink.worker.isAlive()) {
                    sink.stop();
                }
            }
            sinks.clear();
            synchronized (stateLock) {
                captureThread = null;
            }
            listener.onConnectionsChanged(captureSessionId);
            listener.onStopped(captureSessionId, failure);
        }
    }

    void stop() {
        synchronized (stateLock) {
            running = false;
            AudioRecord current = recorder;
            if (current != null) {
                try {
                    current.stop();
                } catch (IllegalStateException ignored) {
                }
            }
        }
        // captureLoop seals queues after offering its last successful read.
    }

    private void notifyConnections(String eventSessionId) {
        // Consumers read the current connection snapshot when handling this
        // notification, so a queued callback cannot replay obsolete flags.
        listener.onConnectionsChanged(eventSessionId);
    }

    private final class TargetSink {
        final Target target;
        final String ownerSessionId;
        final SharedAudioPolicies.FrameQueue queue =
                new SharedAudioPolicies.FrameQueue(QUEUE_FRAMES);
        private final Object attemptLock = new Object();
        private final Object connectionStateLock = new Object();
        private final Object disconnectLock = new Object();
        private final Object leaseLock = new Object();
        private final ArrayDeque<ConnectionAttempt> disconnectQueue = new ArrayDeque<>();
        volatile boolean shouldRun = true;
        volatile boolean finishing;
        volatile boolean connected;
        private ConnectionAttempt activeAttempt;
        private long nextAttemptGeneration;
        private Thread disconnectWorker;
        private boolean disconnectWorkerClosing;
        private WorkerLease workerLease;
        private boolean workerExited;
        /// true until a disconnect cleaner is actually started; sinks that
        /// never enqueue cleanup can release their worker lease immediately.
        private boolean cleanerExited = true;
        private boolean leaseReleased;
        Thread worker;

        TargetSink(Target target, String ownerSessionId) {
            this.target = target;
            this.ownerSessionId = ownerSessionId;
        }

        boolean start() {
            WorkerLease lease = claimWorker(target.computerId);
            if (lease == null) {
                // Keep the sink object in the desired-target map. The next
                // probe will retry after the old same-computer worker really
                // releases its lease; never create an overlapping worker.
                return false;
            }
            synchronized (leaseLock) {
                workerLease = lease;
            }
            Thread created = new Thread(() -> runWorker(lease),
                    "PhoneDeck-Shared-" + target.computerId.substring(
                            0, Math.min(8, target.computerId.length())));
            created.setDaemon(true);
            worker = created;
            try {
                created.start();
                return true;
            } catch (RuntimeException | Error exception) {
                worker = null;
                synchronized (leaseLock) {
                    workerLease = null;
                    workerExited = true;
                    leaseReleased = true;
                }
                releaseWorker(lease);
                throw exception;
            }
        }

        boolean isAlive() {
            return shouldRun && worker != null && worker.isAlive();
        }

        void offer(byte[] frame) {
            if (!shouldRun) {
                return;
            }
            queue.offerLatest(frame);
        }

        /**
         * A single immutable identity for one HTTP attempt.  The watchdog only
         * ever disconnects this exact connection object after checking that it
         * is still the sink's active attempt; a late timeout cannot tear down a
         * replacement connection or a later broadcaster session.
         */
        private final class ConnectionAttempt {
            final long generation;
            final String sessionId;
            final HttpURLConnection connection;
            final long openedAt;
            volatile long outputOpenedAt = Long.MIN_VALUE;
            volatile long firstWriteAt = Long.MIN_VALUE;
            volatile long lastProgressAt = Long.MIN_VALUE;
            volatile long finishingAt = Long.MIN_VALUE;
            volatile boolean abortRequested;
            /// Guarded by attemptLock. Set before enqueueing cleanup so a
            /// slow disconnect can never cause duplicate cleaners.
            boolean disconnectScheduled;

            ConnectionAttempt(long generation, String sessionId,
                              HttpURLConnection connection, long openedAt) {
                this.generation = generation;
                this.sessionId = sessionId;
                this.connection = connection;
                this.openedAt = openedAt;
            }

            void outputOpened(long nowElapsed) {
                synchronized (attemptLock) {
                    outputOpenedAt = nowElapsed;
                }
            }

            void frameFlushed(long nowElapsed) {
                synchronized (attemptLock) {
                    if (firstWriteAt == Long.MIN_VALUE) {
                        firstWriteAt = nowElapsed;
                    }
                    lastProgressAt = nowElapsed;
                }
            }

            void beginFinishing(long nowElapsed) {
                synchronized (attemptLock) {
                    finishingAt = nowElapsed;
                }
            }

            /// Called while attemptLock is held by the watchdog.
            long deadline() {
                if (finishingAt != Long.MIN_VALUE) {
                    return finishingAt + SharedAudioPolicies.SHARED_FINISH_TIMEOUT_MS;
                }
                if (outputOpenedAt == Long.MIN_VALUE) {
                    return openedAt + SharedAudioPolicies.OUTPUT_OPEN_TIMEOUT_MS;
                }
                if (firstWriteAt == Long.MIN_VALUE) {
                    return outputOpenedAt + SharedAudioPolicies.FIRST_WRITE_TIMEOUT_MS;
                }
                long last = lastProgressAt;
                // Defensive against observing the two volatile progress
                // fields between writes: never turn an uninitialized value
                // into a wrapped deadline.
                return last == Long.MIN_VALUE
                        ? outputOpenedAt + SharedAudioPolicies.FIRST_WRITE_TIMEOUT_MS
                        : last + SharedAudioPolicies.WRITE_PROGRESS_TIMEOUT_MS;
            }
        }

        private final class ConnectionWatchdog implements AutoCloseable {
            private final ConnectionAttempt attempt;
            private final Thread thread;
            private volatile boolean closed;

            ConnectionWatchdog(ConnectionAttempt attempt) {
                this.attempt = attempt;
                this.thread = new Thread(this::run,
                        "PhoneDeck-SharedWatchdog-" + target.computerId.substring(
                                0, Math.min(8, target.computerId.length()))
                                + "-" + attempt.generation);
                // A misbehaving platform socket must never keep the Android
                // process alive after the service has gone away.
                this.thread.setDaemon(true);
            }

            void start() {
                thread.start();
            }

            private void run() {
                while (!closed) {
                    long sleepMs;
                    synchronized (attemptLock) {
                        if (!isCurrentAttemptLocked(attempt)) {
                            return;
                        }
                        long now = SystemClock.elapsedRealtime();
                        long deadline = attempt.deadline();
                        if (SharedAudioPolicies.watchdogExpired(now, deadline)) {
                            // Re-read the progress fields while holding the
                            // same lock as frameFlushed(): a flush that won a
                            // race with the timer must move the deadline first.
                            sleepMs = 0;
                        } else {
                            sleepMs = SharedAudioPolicies.watchdogSleepMs(now, deadline);
                        }
                    }
                    if (sleepMs <= 0) {
                        if (abortTimedOutAttempt(attempt)) {
                            return;
                        }
                        continue;
                    }
                    try {
                        Thread.sleep(sleepMs);
                    } catch (InterruptedException exception) {
                        if (closed) {
                            return;
                        }
                    }
                }
            }

            @Override
            public void close() {
                closed = true;
                thread.interrupt();
                if (Thread.currentThread() == thread) {
                    return;
                }
                try {
                    thread.join(SharedAudioPolicies.WATCHDOG_JOIN_TIMEOUT_MS);
                } catch (InterruptedException exception) {
                    Thread.currentThread().interrupt();
                }
            }
        }

        private ConnectionAttempt beginAttempt(HttpURLConnection local, long nowElapsed) {
            synchronized (attemptLock) {
                ConnectionAttempt attempt = new ConnectionAttempt(
                        ++nextAttemptGeneration, ownerSessionId, local, nowElapsed);
                activeAttempt = attempt;
                return attempt;
            }
        }

        private void clearAttempt(ConnectionAttempt attempt) {
            if (attempt == null) {
                return;
            }
            synchronized (attemptLock) {
                if (activeAttempt == attempt) {
                    activeAttempt = null;
                }
            }
        }

        private boolean isCurrentAttemptLocked(ConnectionAttempt attempt) {
            return activeAttempt == attempt
                    && attempt.generation == nextAttemptGeneration
                    && ownerSessionId.equals(attempt.sessionId);
        }

        /**
         * Close only the attempt that timed out.  Never use the sink's mutable
         * connection reference here: a late watchdog would otherwise
         * disconnect a newly established connection after reconnect or target
         * replacement.
         */
        private void abortAttempt(ConnectionAttempt attempt) {
            boolean scheduleDisconnect = false;
            boolean connectedChanged;
            synchronized (attemptLock) {
                if (!isCurrentAttemptLocked(attempt)
                        || attempt.abortRequested) {
                    return;
                }
                attempt.abortRequested = true;
                if (!attempt.disconnectScheduled) {
                    attempt.disconnectScheduled = true;
                    scheduleDisconnect = true;
                }
                // Capture the worker identity and interrupt it while the
                // attempt identity is still validated. No delayed cleaner is
                // ever allowed to interrupt a mutable worker after retry.
                Thread currentWorker = worker;
                if (currentWorker != null && currentWorker != Thread.currentThread()) {
                    currentWorker.interrupt();
                }
                connectedChanged = setConnectedLocked(false);
            }
            notifyConnectedChanged(connectedChanged);
            if (scheduleDisconnect) {
                enqueueDisconnect(attempt);
            }
        }

        private boolean abortTimedOutAttempt(ConnectionAttempt attempt) {
            boolean scheduleDisconnect = false;
            boolean connectedChanged;
            synchronized (attemptLock) {
                if (!isCurrentAttemptLocked(attempt)) {
                    return true;
                }
                // The sleep deadline was only a hint. Read it again under
                // attemptLock immediately before marking this attempt stale.
                long now = SystemClock.elapsedRealtime();
                if (attempt.abortRequested) {
                    return true;
                }
                if (!SharedAudioPolicies.watchdogExpired(now, attempt.deadline())) {
                    return false;
                }
                attempt.abortRequested = true;
                if (!attempt.disconnectScheduled) {
                    attempt.disconnectScheduled = true;
                    scheduleDisconnect = true;
                }
                Thread currentWorker = worker;
                if (currentWorker != null && currentWorker != Thread.currentThread()) {
                    currentWorker.interrupt();
                }
                connectedChanged = setConnectedLocked(false);
            }
            notifyConnectedChanged(connectedChanged);
            if (scheduleDisconnect) {
                enqueueDisconnect(attempt);
            }
            return true;
        }

        private void scheduleDisconnect(ConnectionAttempt attempt) {
            boolean enqueue = false;
            synchronized (attemptLock) {
                if (!attempt.disconnectScheduled) {
                    attempt.disconnectScheduled = true;
                    enqueue = true;
                }
            }
            if (enqueue) {
                enqueueDisconnect(attempt);
            }
        }

        /**
         * Disconnect is delegated to one daemon cleaner per sink. The audio
         * worker, capture teardown, and watchdog never call the platform
         * disconnect method directly. A blocked disconnect can therefore hold
         * at most this sink's daemon and a bounded queue, never one thread per
         * watchdog tick/retry.
         */
        private void enqueueDisconnect(ConnectionAttempt attempt) {
            synchronized (disconnectLock) {
                if (disconnectWorkerClosing
                        || disconnectQueue.size() >= SharedAudioPolicies.DISCONNECT_QUEUE_CAPACITY) {
                    // The attempt is already marked scheduled. Its worker has
                    // released all strong local references; letting this
                    // cleanup be dropped is safer than unbounded teardown.
                    return;
                }
                disconnectQueue.addLast(attempt);
                if (disconnectWorker == null) {
                    synchronized (leaseLock) {
                        cleanerExited = false;
                    }
                    disconnectWorker = new Thread(this::disconnectLoop,
                            "PhoneDeck-SharedDisconnect-" + target.computerId.substring(
                                    0, Math.min(8, target.computerId.length())));
                    disconnectWorker.setDaemon(true);
                    try {
                        disconnectWorker.start();
                    } catch (RuntimeException | Error exception) {
                        disconnectWorker = null;
                        disconnectQueue.clear();
                        synchronized (leaseLock) {
                            cleanerExited = true;
                        }
                        throw exception;
                    }
                }
                disconnectLock.notifyAll();
            }
        }

        private void disconnectLoop() {
            try {
                while (true) {
                    ConnectionAttempt attempt;
                    synchronized (disconnectLock) {
                        while (disconnectQueue.isEmpty() && !disconnectWorkerClosing) {
                            try {
                                disconnectLock.wait();
                            } catch (InterruptedException exception) {
                                Thread.currentThread().interrupt();
                                return;
                            }
                        }
                        if (disconnectQueue.isEmpty() && disconnectWorkerClosing) {
                            return;
                        }
                        attempt = disconnectQueue.removeFirst();
                    }
                    try {
                        // Only this task ever touches this attempt's socket;
                        // it cannot accidentally close a replacement object.
                        attempt.connection.disconnect();
                    } catch (Exception ignored) {
                    }
                }
            } finally {
                WorkerLease release = null;
                synchronized (disconnectLock) {
                    if (Thread.currentThread() == disconnectWorker) {
                        disconnectWorker = null;
                        // Update cleanerExited before releasing
                        // disconnectLock, so a replacement cleaner cannot
                        // race this old thread's final callback.
                        release = markCleanerExitedLocked();
                    }
                }
                if (release != null) {
                    releaseWorker(release);
                }
            }
        }

        private void closeDisconnectCleaner() {
            synchronized (disconnectLock) {
                disconnectWorkerClosing = true;
                disconnectLock.notifyAll();
            }
        }

        private void runWorker(WorkerLease lease) {
            try {
                writeLoop();
            } finally {
                // The lease is process-wide and survives broadcaster/session
                // teardown until the old worker has truly returned. This is
                // the hard boundary that prevents cross-session leaks.
                closeDisconnectCleaner();
                markWorkerExited(lease);
            }
        }

        private void markWorkerExited(WorkerLease lease) {
            WorkerLease release = null;
            synchronized (leaseLock) {
                workerExited = true;
                if (cleanerExited && !leaseReleased) {
                    leaseReleased = true;
                    release = lease;
                }
            }
            if (release != null) {
                releaseWorker(release);
            }
        }

        /// Must be called while disconnectLock is held.
        private WorkerLease markCleanerExitedLocked() {
            WorkerLease release = null;
            synchronized (leaseLock) {
                cleanerExited = true;
                if (workerExited && !leaseReleased) {
                    leaseReleased = true;
                    release = workerLease;
                }
            }
            return release;
        }

        private boolean setConnectedLocked(boolean value) {
            synchronized (connectionStateLock) {
                boolean changed = connected != value;
                connected = value;
                return changed;
            }
        }

        private void notifyConnectedChanged(boolean changed) {
            if (changed) {
                notifyConnections(ownerSessionId);
            }
        }

        private void publishConnectedForAttempt(ConnectionAttempt attempt) {
            boolean changed;
            synchronized (attemptLock) {
                if (!isCurrentAttemptLocked(attempt)
                        || !shouldRun || attempt.abortRequested) {
                    return;
                }
                // Identity validation, connected=true, and the abort flag are
                // one critical section. An old attempt cannot publish false
                // after a replacement has already become connected.
                changed = setConnectedLocked(true);
            }
            notifyConnectedChanged(changed);
        }

        private void clearConnectedForAttempt(ConnectionAttempt attempt) {
            boolean changed;
            synchronized (attemptLock) {
                if (attempt != null && !isCurrentAttemptLocked(attempt)) {
                    return;
                }
                changed = setConnectedLocked(false);
            }
            notifyConnectedChanged(changed);
        }

        void writeLoop() {
            android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_AUDIO);
            SharedAudioPolicies.ReconnectBackoff backoff =
                    new SharedAudioPolicies.ReconnectBackoff();
            while (shouldRun) {
                // finish() lets an already-open connection drain, but must
                // not create a fresh connection after the capture loop has
                // sealed this sink's queue.
                if (finishing) {
                    break;
                }
                HttpURLConnection local = null;
                ConnectionAttempt attempt = null;
                ConnectionWatchdog watchdog = null;
                try {
                    local = PhoneDeckHttp.open(
                            target.endpoint, "/api/audio/stream", 1800, 5000);
                    long openedAt = SystemClock.elapsedRealtime();
                    attempt = beginAttempt(local, openedAt);
                    backoff.connectionOpened(openedAt);
                    watchdog = new ConnectionWatchdog(attempt);
                    watchdog.start();
                    if (!shouldRun) {
                        abortAttempt(attempt);
                        continue;
                    }
                    local.setRequestMethod("POST");
                    local.setRequestProperty("Content-Type", "audio/L16; rate=48000; channels=1");
                    local.setRequestProperty("X-PhoneDeck-Audio", "pcm-s16le");
                    local.setRequestProperty("X-PhoneDeck-Audio-Mode", "shared");
                    local.setRequestProperty("X-PhoneDeck-Protocol", "2");
                    local.setRequestProperty("X-PhoneDeck-Session", ownerSessionId);
                    local.setRequestProperty("X-PhoneDeck-Computer-Id", target.computerId);
                    local.setChunkedStreamingMode(CHUNK_BYTES);
                    local.setDoOutput(true);
                    try (OutputStream output = local.getOutputStream()) {
                        attempt.outputOpened(SystemClock.elapsedRealtime());
                        while (shouldRun && !attempt.abortRequested) {
                            byte[] frame = queue.take();
                            if (frame == null) {
                                break;
                            }
                            output.write(frame);
                            byte[] next = queue.poll(BATCH_WAIT_MS);
                            if (next != null) {
                                output.write(next);
                            }
                            output.flush();
                            long flushedAt = SystemClock.elapsedRealtime();
                            attempt.frameFlushed(flushedAt);
                            if (!attempt.abortRequested) {
                                backoff.frameWritten(flushedAt);
                            }
                            // getOutputStream() only proves local setup. The
                            // first complete write+flush is the earliest point
                            // at which this target can be shown as connected;
                            // this is still not a remote ACK.
                            publishConnectedForAttempt(attempt);
                        }
                        if (shouldRun && finishing) {
                            attempt.beginFinishing(SystemClock.elapsedRealtime());
                            AudioUpload.finish(local, output);
                        }
                    }
                } catch (InterruptedException exception) {
                    if (!shouldRun || finishing) {
                        Thread.currentThread().interrupt();
                        break;
                    }
                    // A connection watchdog interrupted a blocked queue read;
                    // retry this target with its current backoff.
                } catch (Exception ignored) {
                    // Retry only this computer; capture and other sinks continue.
                } finally {
                    if (watchdog != null) {
                        watchdog.close();
                    }
                    if (attempt != null) {
                        scheduleDisconnect(attempt);
                        clearConnectedForAttempt(attempt);
                    }
                    clearAttempt(attempt);
                    backoff.connectionClosed();
                    // An interrupt used to break a timed-out write must not
                    // poison the next retry's Thread.sleep when the sink is
                    // still active. Explicit stop retains the interrupted
                    // status and exits below.
                    if (shouldRun && !finishing) {
                        Thread.interrupted();
                    }
                }
                if (finishing) {
                    break;
                }
                if (shouldRun && !finishing) {
                    try {
                        Thread.sleep(backoff.nextDelayMs());
                    } catch (InterruptedException exception) {
                        Thread.currentThread().interrupt();
                        break;
                    }
                }
            }
            shouldRun = false;
            notifyConnections(ownerSessionId);
        }

        void stop() {
            shouldRun = false;
            queue.finish();
            ConnectionAttempt attempt;
            boolean scheduleDisconnect = false;
            boolean connectedChanged;
            synchronized (attemptLock) {
                attempt = activeAttempt;
                if (attempt != null && !attempt.abortRequested) {
                    attempt.abortRequested = true;
                    if (!attempt.disconnectScheduled) {
                        attempt.disconnectScheduled = true;
                        scheduleDisconnect = true;
                    }
                }
                // Capture and interrupt the current worker only while the
                // sink/attempt identity is locked. A finish timeout must not
                // later interrupt a worker that has already moved on.
                Thread currentWorker = worker;
                if (currentWorker != null && currentWorker != Thread.currentThread()) {
                    currentWorker.interrupt();
                }
                connectedChanged = setConnectedLocked(false);
            }
            notifyConnectedChanged(connectedChanged);
            if (scheduleDisconnect && attempt != null) {
                enqueueDisconnect(attempt);
            }
        }

        void finish() {
            finishing = true;
            queue.finish();
            // Shared mode has no remote EOF/ACK contract. The capture-loop
            // deadline owns the final bounded join; it calls stop() under the
            // attempt identity lock if this worker is still in a socket or
            // backoff sleep. Avoid a delayed callback that could later race a
            // replacement worker identity.
        }
    }

    private static int calculateLevel(byte[] pcm, int count) {
        int sampleCount = count / 2;
        if (sampleCount == 0) {
            return 0;
        }
        double sumSquares = 0;
        for (int index = 0; index + 1 < count; index += 2) {
            short sample = (short) ((pcm[index] & 0xff) | (pcm[index + 1] << 8));
            double normalized = sample / 32768.0;
            sumSquares += normalized * normalized;
        }
        return (int) Math.min(100,
                Math.round(Math.sqrt(sumSquares / sampleCount) * 420));
    }

    @Override
    public void close() {
        stop();
    }
}
