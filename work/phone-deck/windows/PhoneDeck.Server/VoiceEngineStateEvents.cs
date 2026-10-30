using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

/// <summary>
/// Core Audio 采集会话事件：任一采集会话新建、状态变化或断开时立即通知诊断线程刷新，
/// 电脑端在输入法里停止听写后无需等下一次轮询就能发出 phoneStopV1 凭据。
/// 只是“提前唤醒”：判断仍由 <see cref="VoiceEngineStateProbe"/> 完成，事件注册失败时
/// 诊断线程的定时轮询照常工作。启动时的采集设备列表之后新增的设备同样靠轮询兜底。
/// </summary>
internal sealed class VoiceEngineStateEvents : IDisposable
{
    // A busy audio graph can churn sessions. Keep both active registrations and
    // deferred COM cleanup bounded; the periodic probe remains the fallback for
    // sessions deliberately not subscribed here.
    private const int MaxTrackedSessions = 256;
    private readonly Action changed;
    private readonly object sync = new();
    private readonly List<MMDevice> devices = new();
    private readonly List<WatchedSession> sessions = new();
    private readonly Queue<WatchedSession> cleanupQueue = new();
    private MMDeviceEnumerator? enumerator;
    private int trackedSessions;
    private bool cleanupWorkerScheduled;
    private bool started;
    private bool disposed;

    internal VoiceEngineStateEvents(Action changed)
    {
        this.changed = changed;
    }

    internal int WatchedSessions { get { lock (sync) return sessions.Count; } }

    internal void Start()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            lock (sync)
            {
                if (disposed || started)
                {
                    return;
                }
                started = true;
                enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(
                             DataFlow.Capture, DeviceState.Active))
                {
                    devices.Add(device);
                    var manager = device.AudioSessionManager;
                    manager.OnSessionCreated += OnSessionCreated;
                    manager.RefreshSessions();
                    for (var index = 0; index < manager.Sessions.Count; index++)
                    {
                        WatchLocked(manager.Sessions[index]);
                    }
                }
            }
            Console.WriteLine($"[engine] 已订阅 {WatchedSessions} 个采集会话的状态事件");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[engine] 采集会话事件不可用，继续使用轮询：{exception.Message}");
        }
    }

    private void OnSessionCreated(object sender, IAudioSessionControl newSession)
    {
        try
        {
            lock (sync)
            {
                if (!disposed && CanWatchLocked())
                {
                    WatchLocked(new AudioSessionControl(newSession));
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[engine] 订阅新采集会话失败：{exception.Message}");
        }
        Signal();
    }

    private void WatchLocked(AudioSessionControl session)
    {
        if (!CanWatchLocked())
        {
            // This wrapper was created by the initial session enumeration; it
            // was never registered, so disposing it here cannot deadlock the
            // session callback path.
            try
            {
                session.Dispose();
            }
            catch
            {
            }
            return;
        }
        trackedSessions++;
        WatchedSession? watched = null;
        var registration = new WatchedSession(session, Signal,
            () => QueueDisconnected(watched!));
        watched = registration;
        bool scheduleCleanup;
        try
        {
            session.RegisterEventClient(registration.Handler);
            registration.Registered = true;
            registration.RegistrationComplete = true;
            if (registration.CleanupPending || registration.CleanupQueued)
            {
                scheduleCleanup = EnqueueCleanupLocked(registration);
            }
            else
            {
                sessions.Add(registration);
                scheduleCleanup = false;
            }
        }
        catch
        {
            // Registration can fail after COM has partially installed the
            // callback. Defer unregister and Dispose to the worker instead of
            // touching the session on the event/initialization thread.
            registration.RegistrationComplete = true;
            if (!registration.CleanupQueued)
            {
                registration.CleanupQueued = true;
            }
            scheduleCleanup = EnqueueCleanupLocked(registration);
            if (scheduleCleanup)
            {
                ScheduleCleanupWorker();
            }
            throw;
        }
        if (scheduleCleanup)
        {
            ScheduleCleanupWorker();
        }
    }

    private bool CanWatchLocked() => trackedSessions < MaxTrackedSessions;

    private void QueueDisconnected(WatchedSession watched)
    {
        bool scheduleCleanup;
        lock (sync)
        {
            if (watched.CleanupQueued)
            {
                return;
            }
            watched.CleanupQueued = true;
            sessions.Remove(watched);
            if (!watched.RegistrationComplete)
            {
                watched.CleanupPending = true;
                scheduleCleanup = false;
            }
            else
            {
                scheduleCleanup = EnqueueCleanupLocked(watched);
            }
        }
        if (scheduleCleanup)
        {
            ScheduleCleanupWorker();
        }
        // The Core Audio callback must return without UnRegisterEventClient:
        // some Windows versions synchronously wait for the callback to leave.
        Signal();
    }

    private bool EnsureCleanupWorkerLocked()
    {
        if (cleanupWorkerScheduled)
        {
            return false;
        }
        cleanupWorkerScheduled = true;
        return true;
    }

    private bool EnqueueCleanupLocked(WatchedSession watched)
    {
        if (watched.CleanupEnqueued)
        {
            return false;
        }
        watched.CleanupEnqueued = true;
        cleanupQueue.Enqueue(watched);
        return EnsureCleanupWorkerLocked();
    }

    private void ScheduleCleanupWorker()
    {
        if (ThreadPool.QueueUserWorkItem(
                static state => ((VoiceEngineStateEvents)state!).CleanupLoop(), this))
        {
            return;
        }

        lock (sync)
        {
            cleanupWorkerScheduled = false;
        }
        Console.Error.WriteLine("[engine] 音频会话清理任务无法排队");
    }

    private void Signal()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
        }
        try
        {
            changed();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[engine] 状态事件回调失败：{exception.Message}");
        }
    }

    public void Dispose()
    {
        List<MMDevice> devicesToDispose;
        MMDeviceEnumerator? enumeratorToDispose;
        bool scheduleCleanup;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            scheduleCleanup = false;
            foreach (var session in sessions.ToArray())
            {
                if (!session.CleanupQueued)
                {
                    session.CleanupQueued = true;
                }
                scheduleCleanup |= EnqueueCleanupLocked(session);
            }
            sessions.Clear();
            // Also recover a queue left behind if a prior ThreadPool enqueue
            // failed while no active session remained to retry it.
            scheduleCleanup |= cleanupQueue.Count > 0 && EnsureCleanupWorkerLocked();
            devicesToDispose = devices.ToList();
            devices.Clear();
            enumeratorToDispose = enumerator;
            enumerator = null;
        }

        // Do not unregister device callbacks while holding sync: a callback
        // already in flight must be able to observe disposed and return.
        foreach (var device in devicesToDispose)
        {
            try
            {
                device.AudioSessionManager.OnSessionCreated -= OnSessionCreated;
            }
            catch
            {
            }
        }
        if (scheduleCleanup)
        {
            ScheduleCleanupWorker();
        }
        foreach (var device in devicesToDispose)
        {
            try
            {
                device.Dispose();
            }
            catch
            {
            }
        }
        try
        {
            enumeratorToDispose?.Dispose();
        }
        catch
        {
        }
    }

    private void CleanupLoop()
    {
        while (true)
        {
            WatchedSession? watched;
            lock (sync)
            {
                if (cleanupQueue.Count == 0)
                {
                    // Producers take the same lock before enqueuing, so a
                    // producer either sees this worker active or schedules a
                    // new one after this worker marks itself idle.
                    cleanupWorkerScheduled = false;
                    return;
                }
                watched = cleanupQueue.Dequeue();
            }

            var released = false;
            try
            {
                if (watched.Registered)
                {
                    watched.Session.UnRegisterEventClient(watched.Handler);
                }
            }
            catch
            {
                // The session may already be gone; Dispose below still
                // releases the managed/native wrapper as far as possible.
            }
            finally
            {
                try
                {
                    watched.Session.Dispose();
                    released = true;
                }
                catch
                {
                    // Keep its admission slot occupied if the native wrapper
                    // could not be released. Repeated driver failures must not
                    // allow unbounded registrations beyond the original cap.
                }
            }
            lock (sync)
            {
                // Keep the slot occupied until the COM wrapper is actually
                // handed to Dispose, including while UnRegister is blocked.
                if (released)
                {
                    trackedSessions--;
                }
            }
        }
    }

    private sealed class WatchedSession
    {
        internal WatchedSession(
            AudioSessionControl session,
            Action signal,
            Action disconnected)
        {
            Session = session;
            Handler = new Handler(signal, disconnected);
        }

        internal AudioSessionControl Session { get; }
        internal Handler Handler { get; }
        internal bool Registered { get; set; }
        internal bool RegistrationComplete { get; set; }
        internal bool CleanupPending { get; set; }
        internal bool CleanupQueued { get; set; }
        internal bool CleanupEnqueued { get; set; }
    }

    private sealed class Handler(Action signal, Action disconnected) : IAudioSessionEventsHandler
    {
        public void OnStateChanged(AudioSessionState state)
        {
            if (state == AudioSessionState.AudioSessionStateExpired)
            {
                disconnected();
                return;
            }
            signal();
        }

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) => disconnected();

        public void OnVolumeChanged(float volume, bool isMuted)
        {
        }

        public void OnDisplayNameChanged(string displayName)
        {
        }

        public void OnIconPathChanged(string iconPath)
        {
        }

        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex)
        {
        }

        public void OnGroupingParamChanged(ref Guid groupingId)
        {
        }
    }
}
