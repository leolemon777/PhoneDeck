namespace PhoneDeck.MacReceiver;

/// <summary>手机共享麦克风音频 → BlackHole。只支持共享模式（2026-11-05 删除受管听写与启动缓存）。</summary>
internal sealed class MacPhoneAudioBridge : IDisposable
{
    /// <summary>
    /// 共享模式静音追赶目标。f29f03d 曾降到 60 ms，Wi-Fi 抖动超过 60 ms 就播空断续；恢复 120 ms。
    /// </summary>
    internal const int SharedTargetMs = 120;
    /// <summary>
    /// 共享模式的播放延迟硬上限，与 Windows PhonePcmBuffer.SharedQueueMs 一致。静音追赶依赖识别静音，
    /// 手机底噪高时永远不触发；没有上限时每次 Wi-Fi 卡顿都会把延迟永久累加，实测会涨到缓冲上限 2 秒。
    /// </summary>
    internal const int SharedQueueMs = 240;
    private const int MonoBytesPerMs = 48_000 * 2 / 1_000;
    private const int StereoBytesPerMs = 48_000 * 4 / 1_000;
    internal const int TakeoverWaitMs = 2_000;
    internal const int DrainMaxMs = 3_000;
    internal static readonly int OutputTailMs = PcmLatency.OutputTailMs;
    internal static readonly int StopWaitMs = DrainMaxMs + OutputTailMs + 1_500;
    /// <summary>手机供音期间至少每 100 ms 写一次（暂停时写静音保活）。超过此时长无数据，
    /// 视为半开连接（手机休眠、断网或进程被杀而 FIN 未到达），释放接收端，避免幽灵会话
    /// 永久占用并让后续听写全部 409。</summary>
    internal const int DefaultStallTimeoutMs = 5_000;
    private readonly int stallTimeoutMs;
    private readonly object syncRoot = new();
    private readonly IMacAudioOutputFactory outputFactory;
    private readonly bool keepOutputWarm;
    private readonly object warmSync = new();
    private IMacAudioOutput? warmOutput;
    /// <summary>手机最近一次请求后保持预热这么久；之后关闭常驻输出，空闲时不占 CPU 与 coreaudiod。</summary>
    internal const int WarmIdleMs = 90_000;
    private long lastPhoneActivity = Environment.TickCount64;
    private int warmUsers;
    private int prewarmQueued;
    private readonly Timer? idleReaper;
    private (bool Available, string? DeviceName, string? DeviceUid, string? Error) probeCache;
    private bool probeCached;
    private long probeCachedAt;
    private readonly object probeSync = new();
    private ActiveSession? active;
    private string? completedSessionId;
    private bool completedDrainSucceeded;
    private MacAudioQuality? lastQuality;

    internal MacPhoneAudioBridge(IMacAudioOutputFactory outputFactory, bool keepOutputWarm = false,
        int stallTimeoutMs = DefaultStallTimeoutMs)
    {
        this.outputFactory = outputFactory;
        this.keepOutputWarm = keepOutputWarm;
        this.stallTimeoutMs = stallTimeoutMs;
        if (keepOutputWarm)
        {
            idleReaper = new Timer(_ => ReleaseIdleOutput(), null, 30_000, 30_000);
        }
    }

    /// <summary>手机请求（健康检查、音频）到达：记下时间，若常驻输出已关闭则在后台重新预热。</summary>
    internal void NotePhoneActivity()
    {
        Volatile.Write(ref lastPhoneActivity, Environment.TickCount64);
        if (keepOutputWarm && !OutputWarm && Interlocked.Exchange(ref prewarmQueued, 1) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Prewarm();
                }
                finally
                {
                    Volatile.Write(ref prewarmQueued, 0);
                }
            });
        }
    }

    private void ReleaseIdleOutput()
    {
        IMacAudioOutput? idle = null;
        lock (warmSync)
        {
            if (warmOutput is not null && warmUsers == 0 && !IsStreaming
                && Environment.TickCount64 - Volatile.Read(ref lastPhoneActivity) > WarmIdleMs)
            {
                idle = warmOutput;
                warmOutput = null;
            }
        }
        if (idle is not null)
        {
            idle.Dispose();
            Console.WriteLine("[audio] 手机已空闲，关闭常驻 BlackHole 输出");
        }
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
            Volatile.Write(ref lastPhoneActivity, Environment.TickCount64);
            if (warmOutput is not null)
            {
                warmUsers++;
                return warmOutput;
            }
            var created = outputFactory.Create();
            if (keepOutputWarm)
            {
                warmOutput = created;
                warmUsers++;
            }
            return created;
        }
    }

    private void ReleaseOutput(IMacAudioOutput output, bool healthy)
    {
        lock (warmSync)
        {
            Volatile.Write(ref lastPhoneActivity, Environment.TickCount64);
            if (ReferenceEquals(output, warmOutput))
            {
                warmUsers = Math.Max(0, warmUsers - 1);
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

    /// <summary>设备探测要枚举全部 Core Audio 设备：3 秒内复用结果（健康检查每 2 秒一次，会话期更频繁）。</summary>
    internal (bool Available, string? DeviceName, string? DeviceUid, string? Error) Probe()
    {
        lock (probeSync)
        {
            var now = Environment.TickCount64;
            // 不用 long.MinValue 作“从未探测”：now - MinValue 会溢出成负数，缓存永远显得新鲜。
            if (probeCached && now - probeCachedAt < 3_000)
            {
                return probeCache;
            }
            probeCache = outputFactory.Probe();
            probeCachedAt = now;
            probeCached = true;
            return probeCache;
        }
    }

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
            // 常驻输出跨会话复用：每个会话开始时重新设定上限。
            output.SetLatencyCap(SharedQueueMs * StereoBytesPerMs);
            session = new ActiveSession(sessionId, mode, output, cancellation)
            {
                StartCounters = output.Counters,
                OwnerId = ownerId,
                CatchUp = new PcmSilenceCatchUp(SharedTargetMs)
            };
            active = session;
            Monitor.PulseAll(syncRoot);
        }

        var buffer = new byte[16 * 1024];
        var carry = 0;
        var drained = true;
        var stalled = false;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(session.Cancellation.Token);
        try
        {
            while (true)
            {
                // 每次读取重新计时：只有持续 stallTimeoutMs 收不到任何字节才判定断流。
                stall.CancelAfter(stallTimeoutMs);
                var count = await source.ReadAsync(
                    buffer.AsMemory(carry, buffer.Length - carry), stall.Token);
                if (count == 0)
                {
                    break;
                }
                var available = carry + count;
                var completeBytes = available & ~1;
                if (completeBytes > 0)
                {
                    lock (syncRoot)
                    {
                        if (!ReferenceEquals(active, session))
                        {
                            break;
                        }
                        var aligned = buffer.AsSpan(0, completeBytes);
                        session.CountReceived(aligned);
                        session.WriteLive(aligned);
                    }
                }
                carry = available - completeBytes;
                if (carry == 1)
                {
                    buffer[0] = buffer[completeBytes];
                }
            }
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (stall.IsCancellationRequested)
        {
            stalled = true;
            Console.Error.WriteLine(
                $"[audio:{sessionId}] {stallTimeoutMs}ms 未收到手机音频，判定连接已断开，释放接收端");
        }
        finally
        {
            lock (syncRoot) { session.Ending = true; }
            try
            {
                // Cancellation stops intake immediately, not playback of PCM that
                // was already delivered.
                // 被同会话新连接接管或断流超时时不再排空尾音，旧连接直接让出。
                if (!session.Superseded && !stalled)
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
                    LogQuality(session, drained, stalled);
                    // Signal before the callback: Stop may hold the dictation lock
                    // while waiting here, and the callback needs that same lock.
                    session.Ended.TrySetResult(drained);
                    ended(sessionId, mode);
                }
            }
        }
        if (stalled) throw new AudioStreamStalledException("手机音频流长时间无数据，连接已释放");
        if (!drained) throw new IOException("手机尾音未能在限定时间内播放完成");
    }

    /// <summary>当前会话（若有）与上一个已结束会话的播放质量。</summary>
    internal (MacAudioQuality? Current, MacAudioQuality? Last) Quality
    {
        get
        {
            lock (syncRoot)
            {
                return (active?.Quality(), lastQuality);
            }
        }
    }

    private void LogQuality(ActiveSession session, bool drained, bool stalled)
    {
        var quality = session.Quality();
        lock (syncRoot)
        {
            lastQuality = quality;
        }
        Console.WriteLine(
            $"[audio:{session.SessionId}] 结束 mode={quality.Mode} 时长={quality.DurationMs}ms " +
            $"收到={quality.ReceivedMs}ms 断续={quality.GapEvents}次/{quality.GapMs}ms " +
            $"溢出丢弃={quality.DroppedMs}ms 静音追赶={quality.SilenceSkippedMs}ms " +
            $"最大积压={quality.MaxBufferedMs}ms 全零={quality.DigitalSilenceMs}ms " +
            $"排空={(drained ? "完成" : "未完成")}{(stalled ? " 断流" : "")}");
        if (quality.ReceivedMs >= 2_000 && quality.DigitalSilenceMs * 10 >= quality.ReceivedMs * 9)
        {
            Console.Error.WriteLine(
                $"[audio:{session.SessionId}] 手机送来的几乎全是数字静音（全零），" +
                "手机麦克风可能被其他应用占用或被系统静音");
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
        var eofGraceMs = Math.Min(timeoutMilliseconds, 800);
        if (ended.Wait(eofGraceMs))
        {
            return ended.Result;
        }
        StopSession(sessionId);
        var remainingMs = Math.Max(1, timeoutMilliseconds - eofGraceMs);
        return ended.Wait(remainingMs) && ended.Result;
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
        idleReaper?.Dispose();
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
            return IsStreamOwnedByLocked(clientId);
        }
    }

    private bool IsStreamOwnedByLocked(string? clientId) =>
        active is not null && string.Equals(
            PhoneStopReceipts.NormalizeOwner(active.OwnerId),
            PhoneStopReceipts.NormalizeOwner(clientId), StringComparison.Ordinal);

    internal bool? HasReceivedPcm(string? requesterClientId)
    {
        lock (syncRoot)
        {
            if (active is null || !IsStreamOwnedByLocked(requesterClientId))
            {
                return null;
            }
            return active.HasReceivedPcm;
        }
    }

    internal long? LastPcmAgeMs(string? requesterClientId)
    {
        lock (syncRoot)
        {
            if (active is null || !IsStreamOwnedByLocked(requesterClientId) || active.LastPcmTick < 0)
            {
                return null;
            }
            return Math.Max(0, Environment.TickCount64 - active.LastPcmTick);
        }
    }

    private bool TrySupersedeLocked(string sessionId, AudioStreamMode mode, string? ownerId)
    {
        var current = active;
        if (current is null || mode != AudioStreamMode.Shared
            || current.Mode != AudioStreamMode.Shared)
        {
            return false;
        }
        var sameSession = string.Equals(current.SessionId, sessionId, StringComparison.Ordinal)
            && (string.Equals(current.OwnerId, ownerId, StringComparison.Ordinal)
                || current.OwnerId is null
                || ownerId is null);
        var sameAuthenticatedPhone = !string.IsNullOrEmpty(ownerId)
            && string.Equals(current.OwnerId, ownerId, StringComparison.Ordinal);
        if (!sameSession && !sameAuthenticatedPhone)
        {
            return false;
        }
        current.Superseded = true;
        current.Cancellation.Cancel();
        return true;
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
        internal long StartedAt { get; } = Environment.TickCount64;
        internal MacAudioOutputCounters StartCounters { get; init; }
        private long receivedBytes;
        private long digitalSilenceBytes;
        private int maxBufferedBytes;
        internal long LastPcmTick { get; private set; } = -1;
        internal bool HasReceivedPcm => receivedBytes > 0;

        /// <summary>统计收到的单声道 PCM16：总量与整块全零（Android 静默采集）的部分；调用方持有桥接锁。</summary>
        internal void CountReceived(ReadOnlySpan<byte> mono)
        {
            receivedBytes += mono.Length;
            LastPcmTick = Environment.TickCount64;
            for (var offset = 0; offset + PcmLatency.BlockBytes <= mono.Length; offset += PcmLatency.BlockBytes)
            {
                if (!mono.Slice(offset, PcmLatency.BlockBytes).ContainsAnyExcept((byte)0))
                {
                    digitalSilenceBytes += PcmLatency.BlockBytes;
                }
            }
        }

        internal MacAudioQuality Quality()
        {
            var counters = Output.Counters - StartCounters;
            return new MacAudioQuality(
                SessionId,
                Mode.ToWireValue(),
                Environment.TickCount64 - StartedAt,
                receivedBytes / MonoBytesPerMs,
                counters.GapEvents,
                counters.GapBytes / StereoBytesPerMs,
                counters.DroppedBytes / StereoBytesPerMs,
                CatchUp.SkippedBytes / MonoBytesPerMs,
                maxBufferedBytes / StereoBytesPerMs,
                Output.BufferedBytes / StereoBytesPerMs,
                digitalSilenceBytes / MonoBytesPerMs);
        }
        internal CancellationTokenSource Cancellation { get; } = cancellation;
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
            var stereo = converter.ConvertSpan(filtered.AsSpan(0, count));
            if (stereo.Length > 0)
            {
                Output.Write(stereo);
                maxBufferedBytes = Math.Max(maxBufferedBytes, Output.BufferedBytes);
            }
        }
        internal TaskCompletionSource<bool> Ended { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// 单个音频会话的播放质量（毫秒）。Gap 为播放中途缺数据又恢复的断续；DigitalSilence 为整块全零的输入，
/// 持续全零通常表示 Android 麦克风被其他应用占用或被系统静音。
/// </summary>
internal sealed record MacAudioQuality(
    string SessionId,
    string Mode,
    long DurationMs,
    long ReceivedMs,
    long GapEvents,
    long GapMs,
    long DroppedMs,
    long SilenceSkippedMs,
    long MaxBufferedMs,
    long BufferedMs,
    long DigitalSilenceMs);
