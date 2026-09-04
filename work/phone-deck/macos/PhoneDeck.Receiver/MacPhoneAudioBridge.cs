namespace PhoneDeck.MacReceiver;

internal interface IMacPhoneAudioSessionController
{
    bool WaitForSessionActive(string sessionId, int timeoutMilliseconds);
    bool WaitForSessionEnd(string sessionId, int timeoutMilliseconds);
    void BeginPlayback(string sessionId);
    bool StopSession(string sessionId);
}

internal sealed class MacPhoneAudioBridge : IMacPhoneAudioSessionController, IDisposable
{
    private const int PreRollCapacityBytes = 48_000 * 4;
    private readonly object syncRoot = new();
    private readonly IMacAudioOutputFactory outputFactory;
    private ActiveSession? active;

    internal MacPhoneAudioBridge(IMacAudioOutputFactory outputFactory)
    {
        this.outputFactory = outputFactory;
    }

    internal bool IsStreaming { get { lock (syncRoot) return active is not null; } }
    internal string? ActiveSessionId { get { lock (syncRoot) return active?.SessionId; } }
    internal AudioStreamMode? ActiveMode { get { lock (syncRoot) return active?.Mode; } }

    internal (bool Available, string? DeviceName, string? DeviceUid, string? Error) Probe() =>
        outputFactory.Probe();

    internal async Task StreamAsync(
        Stream source,
        string sessionId,
        AudioStreamMode mode,
        Action<string, AudioStreamMode> ended,
        CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            if (active is not null)
            {
                throw new AudioStreamConflictException(
                    $"已有 {active.Mode.ToWireValue()} 音频会话正在使用接收端");
            }
        }
        IMacAudioOutput output;
        try
        {
            output = outputFactory.Create();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "BlackHole 音频输出不可用：" + exception.Message, exception);
        }

        ActiveSession session;
        lock (syncRoot)
        {
            if (active is not null)
            {
                output.Dispose();
                throw new AudioStreamConflictException(
                    $"已有 {active.Mode.ToWireValue()} 音频会话正在使用接收端");
            }
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            session = new ActiveSession(sessionId, mode, output, cancellation)
            {
                PlaybackReleased = mode == AudioStreamMode.Shared
            };
            active = session;
            Monitor.PulseAll(syncRoot);
        }

        var converter = new Pcm16MonoToStereoConverter();
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(
                    buffer.AsMemory(), session.Cancellation.Token);
                if (count == 0)
                {
                    break;
                }
                var stereo = converter.Convert(buffer.AsSpan(0, count));
                if (stereo.Length == 0)
                {
                    continue;
                }
                lock (syncRoot)
                {
                    if (!ReferenceEquals(active, session))
                    {
                        break;
                    }
                    if (session.PlaybackReleased)
                    {
                        session.Output.Write(stereo);
                    }
                    else
                    {
                        session.PreRoll.Enqueue(stereo);
                        session.PreRollBytes += stereo.Length;
                        while (session.PreRollBytes > PreRollCapacityBytes
                               && session.PreRoll.TryDequeue(out var dropped))
                        {
                            session.PreRollBytes -= dropped.Length;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            lock (syncRoot)
            {
                if (ReferenceEquals(active, session))
                {
                    active = null;
                    Monitor.PulseAll(syncRoot);
                }
            }
            session.Output.Dispose();
            session.Cancellation.Dispose();
            session.Ended.TrySetResult();
            ended(sessionId, mode);
        }
    }

    public void BeginPlayback(string sessionId)
    {
        lock (syncRoot)
        {
            var session = RequireSession(sessionId);
            if (session.Mode != AudioStreamMode.Managed || session.PlaybackReleased)
            {
                return;
            }
            while (session.PreRoll.TryDequeue(out var audio))
            {
                session.Output.Write(audio);
            }
            session.PreRollBytes = 0;
            session.PlaybackReleased = true;
        }
    }

    public bool WaitForSessionActive(string sessionId, int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        lock (syncRoot)
        {
            while (true)
            {
                if (string.Equals(active?.SessionId, sessionId, StringComparison.Ordinal))
                {
                    return true;
                }
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    return false;
                }
                Monitor.Wait(syncRoot, (int)Math.Min(int.MaxValue, remaining));
            }
        }
    }

    public bool WaitForSessionEnd(string sessionId, int timeoutMilliseconds)
    {
        Task? ended;
        lock (syncRoot)
        {
            var current = active;
            ended = current is not null
                && string.Equals(current.SessionId, sessionId, StringComparison.Ordinal)
                ? current.Ended.Task : null;
        }
        return ended is null || ended.Wait(timeoutMilliseconds);
    }

    public bool StopSession(string sessionId)
    {
        lock (syncRoot)
        {
            var current = active;
            if (current is null
                || !string.Equals(current.SessionId, sessionId, StringComparison.Ordinal))
            {
                return false;
            }
            current.Cancellation.Cancel();
            return true;
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            active?.Cancellation.Cancel();
        }
    }

    private ActiveSession RequireSession(string sessionId)
    {
        var current = active;
        return current is not null
            && string.Equals(current.SessionId, sessionId, StringComparison.Ordinal)
            ? current
            : throw new InvalidOperationException("音频会话不存在或已断开");
    }

    private sealed class ActiveSession(
        string sessionId,
        AudioStreamMode mode,
        IMacAudioOutput output,
        CancellationTokenSource cancellation)
    {
        internal string SessionId { get; } = sessionId;
        internal AudioStreamMode Mode { get; } = mode;
        internal IMacAudioOutput Output { get; } = output;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal Queue<byte[]> PreRoll { get; } = new();
        internal int PreRollBytes { get; set; }
        internal bool PlaybackReleased { get; set; }
        internal TaskCompletionSource Ended { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
