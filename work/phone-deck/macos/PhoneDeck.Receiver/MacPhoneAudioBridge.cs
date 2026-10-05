namespace PhoneDeck.MacReceiver;

internal interface IMacPhoneAudioSessionController
{
    bool WaitForSessionActive(string sessionId, int timeoutMilliseconds);
    bool WaitForSessionEnd(string sessionId, int timeoutMilliseconds);
    void BeginPlayback(string sessionId);
    bool StopSession(string sessionId);

    /// <summary>BlackHole 常驻输出已运行，引擎可与音频建连并行启动。</summary>
    bool OutputWarm => false;
}

internal sealed class MacPhoneAudioBridge : IMacPhoneAudioSessionController, IDisposable
{
    /// <summary>未放行前暂存的手机 PCM（mono）：最多 1 秒。</summary>
    private const int PreRollCapacityBytes = 48_000 * 2;
    internal const int ManagedTargetMs = 150;
    internal const int SharedTargetMs = 120;
    internal const int TakeoverWaitMs = 2_000;
    internal const int DrainMaxMs = 3_000;
    internal static readonly int OutputTailMs = PcmLatency.OutputTailMs;
    internal static readonly int StopWaitMs = DrainMaxMs + OutputTailMs + 1_500;
    private readonly object syncRoot = new();
    private readonly IMacAudioOutputFactory outputFactory;
    private readonly bool keepOutputWarm;
    private readonly object warmSync = new();
    private IMacAudioOutput? warmOutput;
    private ActiveSession? active;
    private string? completedSessionId;
    private bool completedDrainSucceeded;

    internal MacPhoneAudioBridge(IMacAudioOutputFactory outputFactory, bool keepOutputWarm = false)
    {
        this.outputFactory = outputFactory;
        this.keepOutputWarm = keepOutputWarm;
    }

    /// <summary>BlackHole 常驻输出正在运行（空闲时渲染静音），会话开始无需重建 AUHAL。</summary>
    public bool OutputWarm { get { lock (warmSync) return warmOutput is not null; } }

    /// <summary>启动时预热常驻输出；失败（例如未安装 BlackHole）时保持按会话创建。</summary>
    internal void Prewarm()
    {
        if (!keepOutputWarm)
        {
            return;
        }
        lock (warmSync)
        {
            if (warmOutput is not null)
            {
                return;
            }
            try
            {
                warmOutput = outputFactory.Create();
                Console.WriteLine("[audio] 常驻 BlackHole 输出已启动");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[audio] 常驻输出不可用，改为按会话创建：{exception.Message}");
            }
        }
    }

    private IMacAudioOutput AcquireOutput()
    {
        lock (warmSync)
        {
            if (warmOutput is not null)
            {
                return warmOutput;
            }
            var created = outputFactory.Create();
            if (keepOutputWarm)
            {
                warmOutput = created;
            }
            return created;
        }
    }

    private void ReleaseOutput(IMacAudioOutput output, bool healthy)
    {
        lock (warmSync)
        {
            if (ReferenceEquals(output, warmOutput))
            {
                if (healthy)
                {
                    return;
                }
                // 排空失败可能是设备已移除：丢弃常驻输出，下一段重新创建。
                warmOutput = null;
            }
        }
        output.Dispose();
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
        CancellationToken cancellationToken,
        string? ownerId = null)
    {
        lock (syncRoot)
        {
            if (active is not null && !TrySupersedeLocked(sessionId, mode, ownerId))
            {
                throw new AudioStreamConflictException(
                    $"已有 {active.Mode.ToWireValue()} 音频会话正在使用接收端");
            }
            // 同一共享会话断线重连：旧连接已被要求让出，等待它退出。
            var takeoverDeadline = Environment.TickCount64 + TakeoverWaitMs;
            while (active is not null)
            {
                var remaining = takeoverDeadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    throw new AudioStreamConflictException(
                        $"已有 {active.Mode.ToWireValue()} 音频会话正在使用接收端");
                }
                Monitor.Wait(syncRoot, (int)remaining);
            }
        }
        IMacAudioOutput output;
        try
        {
            output = AcquireOutput();
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
                ReleaseOutput(output, healthy: true);
                throw new AudioStreamConflictException(
                    $"已有 {active.Mode.ToWireValue()} 音频会话正在使用接收端");
            }
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            session = new ActiveSession(sessionId, mode, output, cancellation)
            {
                OwnerId = ownerId,
                PlaybackReleased = mode == AudioStreamMode.Shared,
                CatchUp = new PcmSilenceCatchUp(
                    mode == AudioStreamMode.Shared ? SharedTargetMs : ManagedTargetMs)
            };
            active = session;
            Monitor.PulseAll(syncRoot);
        }

        var buffer = new byte[16 * 1024];
        var drained = true;
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
                lock (syncRoot)
                {
                    if (!ReferenceEquals(active, session))
                    {
                        break;
                    }
                    if (session.PlaybackReleased)
                    {
                        session.WriteLive(buffer.AsSpan(0, count));
                    }
                    else
                    {
                        session.PreRoll.Enqueue(buffer.AsSpan(0, count).ToArray());
                        session.PreRollBytes += count;
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
            bool released;
            lock (syncRoot) { session.Ending = true; released = session.PlaybackReleased; }
            try
            {
                // Cancellation stops intake immediately, not playback of PCM that
                // was already delivered. Unreleased startup pre-roll is discarded.
                // 被同会话新连接接管时由新连接继续供音，旧连接直接让出。
                if (released && !session.Superseded)
                    drained = await session.Output.DrainAsync(DrainMaxMs, OutputTailMs)
                        .WaitAsync(TimeSpan.FromMilliseconds(DrainMaxMs + OutputTailMs + 500));
            }
            catch (Exception exception)
            {
                drained = false;
                Console.Error.WriteLine($"[audio:{sessionId}] 尾音排空失败：{exception.Message}");
            }
            finally
            {
                try { ReleaseOutput(session.Output, drained); }
                finally
                {
                    lock (syncRoot)
                    {
                        if (ReferenceEquals(active, session))
                        {
                            completedSessionId = sessionId;
                            completedDrainSucceeded = drained;
                            active = null;
                            Monitor.PulseAll(syncRoot);
                        }
                    }
                    session.Cancellation.Dispose();
                    // Signal before the callback: Stop may hold the dictation lock
                    // while waiting here, and the callback needs that same lock.
                    session.Ended.TrySetResult(drained);
                    ended(sessionId, mode);
                }
            }
        }
        if (!drained) throw new IOException("手机尾音未能在限定时间内播放完成");
    }

    public void BeginPlayback(string sessionId)
    {
        lock (syncRoot)
        {
            var session = RequireSession(sessionId);
            if (session.Ending) throw new InvalidOperationException("音频会话正在结束");
            if (session.Mode != AudioStreamMode.Managed || session.PlaybackReleased)
            {
                return;
            }
            var preRoll = new byte[session.PreRollBytes];
            var offset = 0;
            while (session.PreRoll.TryDequeue(out var audio))
            {
                audio.CopyTo(preRoll, offset);
                offset += audio.Length;
            }
            session.PreRollBytes = 0;
            session.PlaybackReleased = true;
            // 开口前的静音只会拉长整段延迟：放行时只保留首个语音块前 LeadInMs。
            var trimmed = PcmLatency.LeadingSilenceTrimOffset(
                preRoll, PcmLatency.MsToBytes(PcmLatency.LeadInMs));
            session.WriteLive(preRoll.AsSpan(trimmed));
            Console.WriteLine(
                $"[audio:{sessionId}] playbackReleased preRollBytes={preRoll.Length} " +
                $"leadingSilenceTrimmedBytes={trimmed}");
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
        Task<bool> ended;
        lock (syncRoot)
        {
            var current = active;
            if (current is null || !string.Equals(current.SessionId, sessionId, StringComparison.Ordinal))
                return !string.Equals(completedSessionId, sessionId, StringComparison.Ordinal)
                    || completedDrainSucceeded;
            ended = current.Ended.Task;
        }
        return ended.Wait(timeoutMilliseconds) && ended.Result;
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
        lock (warmSync)
        {
            if (warmOutput is not null)
            {
                warmOutput.Dispose();
                warmOutput = null;
            }
        }
    }

    /// <summary>当前音频流是否属于该手机（USB 与旧共享令牌同为 legacy 身份）。</summary>
    internal bool IsStreamOwnedBy(string? clientId)
    {
        lock (syncRoot)
        {
            return active is not null && string.Equals(
                PhoneStopReceipts.NormalizeOwner(active.OwnerId),
                PhoneStopReceipts.NormalizeOwner(clientId), StringComparison.Ordinal);
        }
    }

    private bool TrySupersedeLocked(string sessionId, AudioStreamMode mode, string? ownerId)
    {
        var current = active;
        if (current is null || mode != AudioStreamMode.Shared
            || current.Mode != AudioStreamMode.Shared
            || !string.Equals(current.SessionId, sessionId, StringComparison.Ordinal)
            || !string.Equals(current.OwnerId, ownerId, StringComparison.Ordinal))
        {
            return false;
        }
        current.Superseded = true;
        current.Cancellation.Cancel();
        return true;
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
        internal bool Ending { get; set; }
        internal volatile bool Superseded;
        internal string? OwnerId { get; init; }
        internal required PcmSilenceCatchUp CatchUp { get; init; }
        private readonly Pcm16MonoToStereoConverter converter = new();
        private byte[] filtered = new byte[16 * 1024];

        /// <summary>跳过积压中的静音后转成双声道写入输出；调用方持有桥接锁。</summary>
        internal void WriteLive(ReadOnlySpan<byte> mono)
        {
            if (filtered.Length < mono.Length)
            {
                filtered = new byte[mono.Length];
            }
            var count = CatchUp.Filter(mono, filtered, Output.BufferedBytes / 2);
            var stereo = converter.Convert(filtered.AsSpan(0, count));
            if (stereo.Length > 0)
            {
                Output.Write(stereo);
            }
        }
        internal TaskCompletionSource<bool> Ended { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
