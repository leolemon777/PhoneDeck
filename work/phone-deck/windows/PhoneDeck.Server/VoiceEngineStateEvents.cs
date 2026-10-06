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
    private readonly Action changed;
    private readonly object sync = new();
    private readonly List<MMDevice> devices = new();
    private readonly List<AudioSessionControl> sessions = new();
    private readonly Handler handler;
    private MMDeviceEnumerator? enumerator;
    private bool disposed;

    internal VoiceEngineStateEvents(Action changed)
    {
        this.changed = changed;
        handler = new Handler(Signal);
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
                if (!disposed)
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
        session.RegisterEventClient(handler);
        sessions.Add(session);
    }

    private void Signal()
    {
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
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            foreach (var session in sessions)
            {
                try
                {
                    session.UnRegisterEventClient(handler);
                    session.Dispose();
                }
                catch
                {
                    // 进程退出时的尽力清理。
                }
            }
            sessions.Clear();
            foreach (var device in devices)
            {
                try
                {
                    device.AudioSessionManager.OnSessionCreated -= OnSessionCreated;
                }
                catch
                {
                }
                device.Dispose();
            }
            devices.Clear();
            enumerator?.Dispose();
        }
    }

    private sealed class Handler(Action signal) : IAudioSessionEventsHandler
    {
        public void OnStateChanged(AudioSessionState state) => signal();

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) => signal();

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
