using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

internal sealed class PhoneAudioBridge : IPhoneAudioSessionController, IDisposable
{
    private const int SampleRate = 48_000;

    /// <summary>Typeless 确认采集前，手机 PCM 最多在服务端暂存的时长；
    /// 超过该水位自动放行，避免 Typeless 未启动时无界等待。</summary>
    internal const int PreRollHoldMs = 1_500;

    /// <summary>流结束后等待 BufferedWaveProvider 排空的上限。</summary>
    internal const int DrainMaxMs = 3_000;

    // Provider 为空仅表示 PCM 已被取走；重采样器、WASAPI 和虚拟声卡
    // 仍可能持有最后几帧。继续播放静音，给输出/采集链路完成尾帧的时间。
    internal static readonly int OutputTailMs = PcmLatency.OutputTailMs;
    internal static readonly int StopWaitMs = DrainMaxMs + OutputTailMs + 1_500;

    private static readonly int PreRollHoldBytes = SampleRate * 2 * PreRollHoldMs / 1_000;

    private readonly SemaphoreSlim streamGate = new(1, 1);
    private readonly object sessionSync = new();
    private readonly Func<IWaveProvider, IPhoneAudioPlayback> createPlayback;
    private readonly bool keepOutputWarm;
    private readonly object warmSync = new();
    private SwitchingWaveProvider? warmSource;
    private IPhoneAudioPlayback? warmPlayback;
    private int warmReady;
    private long prewarmRetryAt;
    /// <summary>手机最近一次请求后保持预热这么久；之后关闭常驻输出，空闲时不占 CPU 与音频引擎。</summary>
    internal const int WarmIdleMs = 90_000;
    private const int PrewarmRetryDelayMs = 2_000;
    private long lastPhoneActivity = Environment.TickCount64;
    private int prewarmQueued;
    private readonly Timer? idleReaper;
    private int disposed;
    private CancellationTokenSource? activeCancellation;
    private ActiveStream? activeStream;
    private string? activeSessionId;
    private AudioStreamMode? activeMode;
    private string? completedSessionId;
    private bool completedDrainSucceeded;

    /// <summary>同一共享会话重连时等待旧连接让出的上限。</summary>
    internal const int TakeoverWaitMs = 2_000;

    /// <summary>手机供音期间至少每 100 ms 写一次（暂停时写静音保活）。超过此时长无数据，
    /// 视为半开连接（手机休眠、断网或进程被杀而 FIN 未到达），释放接收端，避免幽灵会话
    /// 永久占用并让后续听写全部 409。</summary>
    internal const int DefaultStallTimeoutMs = 5_000;
    private readonly int stallTimeoutMs;

    internal PhoneAudioBridge()
        : this(source => new WasapiPhoneAudioPlayback(source),
            keepOutputWarm: Environment.GetEnvironmentVariable("PHONEDECK_WARM_AUDIO") != "0")
    {
    }

    internal PhoneAudioBridge(
        Func<IWaveProvider, IPhoneAudioPlayback> createPlayback,
        bool keepOutputWarm = false,
        int stallTimeoutMs = DefaultStallTimeoutMs)
    {
        this.createPlayback = createPlayback;
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
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        Volatile.Write(ref lastPhoneActivity, Environment.TickCount64);
        var warmOutputReady = IsWarmOutputReadyFast();
        // Health/event polling must not take warmSync: EnsureWarmLocked can
        // synchronously enter COM/WASAPI.  The ready/queued/retry flags are
        // deliberately atomic so a 50ms poll only schedules one bounded task.
        if (keepOutputWarm
            && !warmOutputReady
            && Environment.TickCount64 >= Volatile.Read(ref prewarmRetryAt)
            && Interlocked.CompareExchange(ref prewarmQueued, 1, 0) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (Volatile.Read(ref disposed) == 0)
                    {
                        Prewarm();
                    }
                }
                finally
                {
                    Volatile.Write(ref prewarmQueued, 0);
                }
            });
        }
    }

    private bool IsWarmOutputReadyFast()
    {
        if (Volatile.Read(ref warmReady) == 0)
        {
            return false;
        }
        var playback = Volatile.Read(ref warmPlayback);
        if (playback is { IsFaulted: false } && Volatile.Read(ref disposed) == 0)
        {
            return true;
        }
        Volatile.Write(ref warmReady, 0);
        return false;
    }

    private void ReleaseIdleOutput()
    {
        IPhoneAudioPlayback? playback;
        lock (warmSync)
        {
            // 会话全程持有 streamGate：计数为 1 说明没有音频流在用常驻输出。
            if (Volatile.Read(ref disposed) != 0 || warmPlayback is null || streamGate.CurrentCount == 0
                || Environment.TickCount64 - Volatile.Read(ref lastPhoneActivity) <= WarmIdleMs)
            {
                return;
            }
            playback = warmPlayback;
            warmPlayback = null;
            warmSource = null;
            Volatile.Write(ref warmReady, 0);
        }
        TryDisposePlayback(playback, "idle");
        Console.WriteLine("[audio] 手机已空闲，关闭常驻虚拟声卡输出");
    }

    /// <summary>常驻输出正在运行：会话开始不再初始化 WASAPI，引擎也无需等待声卡预热。</summary>
    public bool OutputWarm
    {
        get
        {
            if (Volatile.Read(ref disposed) != 0
                || Volatile.Read(ref warmReady) == 0)
            {
                return false;
            }
            lock (warmSync)
            {
                var ready = warmPlayback is { IsFaulted: false };
                if (!ready)
                {
                    Volatile.Write(ref warmReady, 0);
                }
                return ready;
            }
        }
    }

    /// <summary>启动时预热常驻输出；失败（例如未安装 VB-CABLE）时保持按会话创建。</summary>
    internal void Prewarm()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        if (streamGate.CurrentCount == 0)
        {
            Volatile.Write(
                ref prewarmRetryAt,
                Environment.TickCount64 + PrewarmRetryDelayMs);
            return;
        }
        lock (warmSync)
        {
            // A stream may acquire the gate while this callback is waiting
            // for warmSync; re-check under the lock before replacing an old
            // playback object that an active stream may still own.
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }
            if (streamGate.CurrentCount == 0)
            {
                Volatile.Write(
                    ref prewarmRetryAt,
                    Environment.TickCount64 + PrewarmRetryDelayMs);
                return;
            }
            EnsureWarmLocked();
        }
    }

    private SwitchingWaveProvider? EnsureWarmLocked()
    {
        if (!keepOutputWarm || Volatile.Read(ref disposed) != 0)
        {
            Volatile.Write(ref warmReady, 0);
            return null;
        }
        if (warmPlayback is { IsFaulted: false } && warmSource is not null)
        {
            Volatile.Write(ref warmReady, 1);
            return warmSource;
        }
        var stalePlayback = warmPlayback;
        warmPlayback = null;
        warmSource = null;
        Volatile.Write(ref warmReady, 0);
        TryDisposePlayback(stalePlayback, "warm-replace");
        try
        {
            var source = new SwitchingWaveProvider();
            var playback = createPlayback(source);
            try
            {
                playback.Play();
            }
            catch
            {
                playback.Dispose();
                throw;
            }
            warmSource = source;
            warmPlayback = playback;
            Volatile.Write(ref warmReady, 1);
            Console.WriteLine("[audio] 常驻虚拟声卡输出已启动");
            return source;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref warmReady, 0);
            Volatile.Write(
                ref prewarmRetryAt,
                Environment.TickCount64 + PrewarmRetryDelayMs);
            Console.Error.WriteLine($"[audio] 常驻输出不可用，改为按会话创建：{exception.Message}");
            return null;
        }
    }

    private static void TryDisposePlayback(IPhoneAudioPlayback? playback, string reason)
    {
        if (playback is null)
        {
            return;
        }
        try
        {
            playback.Dispose();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[audio] playbackDisposeFailed={reason}:{exception.GetType().Name}");
        }
    }

    private sealed class ActiveStream
    {
        public required PreRollBuffer Preroll { get; init; }
        public required PhonePcmBuffer Provider { get; init; }
        public required object PlaybackGate { get; init; }
        public required ManualResetEventSlim Ended { get; init; }
        public required AudioStreamMode Mode { get; init; }
        public string? OwnerId { get; init; }
        public long StartedAt { get; } = Stopwatch.GetTimestamp();
        /// <summary>同一共享会话的新连接已接管：旧连接不再排空或补尾音。</summary>
        public volatile bool Superseded;
        public bool PlaybackReleased;
        /// <summary>所属 WASAPI 输出在上传期间意外停止；该流必须立即让出。</summary>
        public volatile bool OutputFaulted;
        public long ReleasedAtMs = -1;
        public bool DrainSucceeded;
        public long ReceivedPcmBytes;
        public long LastPcmTick = -1;
        public string? RuntimeError;
    }

    internal bool IsStreaming
    {
        get
        {
            lock (sessionSync)
            {
                return activeSessionId is not null;
            }
        }
    }

    internal string? ActiveSessionId
    {
        get
        {
            lock (sessionSync)
            {
                return activeSessionId;
            }
        }
    }

    internal string? ActiveOwnerId
    {
        get
        {
            lock (sessionSync)
            {
                return activeStream?.OwnerId;
            }
        }
    }

    /// <summary>当前音频流是否属于该手机（USB 与旧共享令牌同为 legacy 身份）。</summary>
    internal bool IsStreamOwnedBy(string? clientId)
    {
        lock (sessionSync)
        {
            return activeStream is not null && string.Equals(
                PhoneStopReceipts.NormalizeOwner(activeStream.OwnerId),
                PhoneStopReceipts.NormalizeOwner(clientId), StringComparison.Ordinal);
        }
    }

    internal string? ActiveMode
    {
        get
        {
            lock (sessionSync)
            {
                return activeMode?.ToWireValue();
            }
        }
    }

    internal PhoneAudioRuntimeSnapshot GetRuntimeSnapshot(string? requesterClientId)
    {
        lock (sessionSync)
        {
            var checkedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var stream = activeStream;
            if (stream is null || activeSessionId is null)
            {
                return new PhoneAudioRuntimeSnapshot(
                    Streaming: false,
                    SessionId: null,
                    Mode: null,
                    ReceivedPcmBytes: null,
                    HasReceivedPcm: null,
                    LastPcmAgeMs: null,
                    LastError: null,
                    CheckedAtMs: checkedAtMs);
            }

            var isOwner = string.Equals(
                PhoneStopReceipts.NormalizeOwner(stream.OwnerId),
                PhoneStopReceipts.NormalizeOwner(requesterClientId),
                StringComparison.Ordinal);
            long? lastPcmAgeMs = null;
            if (isOwner && stream.LastPcmTick >= 0)
            {
                lastPcmAgeMs = Math.Max(0, Environment.TickCount64 - stream.LastPcmTick);
            }
            return new PhoneAudioRuntimeSnapshot(
                Streaming: true,
                SessionId: isOwner ? activeSessionId : null,
                Mode: activeMode?.ToWireValue(),
                ReceivedPcmBytes: isOwner ? stream.ReceivedPcmBytes : null,
                HasReceivedPcm: isOwner ? stream.ReceivedPcmBytes > 0 : null,
                LastPcmAgeMs: lastPcmAgeMs,
                LastError: isOwner ? stream.RuntimeError : null,
                CheckedAtMs: checkedAtMs);
        }
    }

    internal static long ElapsedMs(long startTimestamp) =>
        (Stopwatch.GetTimestamp() - startTimestamp) * 1000 / Stopwatch.Frequency;

    internal string? FindVirtualCable()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .ToList();
        try
        {
            _ = SelectDevice(devices, out var name);
            return name;
        }
        finally
        {
            foreach (var device in devices)
            {
                device.Dispose();
            }
        }
    }

    public bool IsSessionActive(string sessionId)
    {
        lock (sessionSync)
        {
            return string.Equals(activeSessionId, sessionId, StringComparison.Ordinal);
        }
    }

    public bool WaitForSessionActive(string sessionId, int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        do
        {
            if (IsSessionActive(sessionId))
            {
                return true;
            }
            Thread.Sleep(20);
        }
        while (Environment.TickCount64 < deadline);
        return IsSessionActive(sessionId);
    }

    public bool StopSession(string sessionId)
    {
        CancellationTokenSource? cancellation;
        lock (sessionSync)
        {
            if (!string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
            {
                return false;
            }
            cancellation = activeCancellation;
        }
        TryCancel(cancellation, $"stop:{sessionId}");
        return true;
    }

    public void BeginPlayback(string sessionId)
    {
        ActiveStream? stream;
        lock (sessionSync)
        {
            if (!string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }
            stream = activeStream;
        }
        if (stream is null)
        {
            return;
        }
        lock (stream.PlaybackGate)
        {
            if (stream.PlaybackReleased)
            {
                return;
            }
            ReleasePreRoll(stream, sessionId, "engine");
        }
    }

    public bool WaitForSessionEnd(string sessionId, int timeoutMilliseconds)
    {
        ActiveStream? stream;
        lock (sessionSync)
        {
            if (!string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
            {
                return !string.Equals(completedSessionId, sessionId, StringComparison.Ordinal)
                    || completedDrainSucceeded;
            }
            stream = activeStream;
        }
        if (stream is null)
        {
            return true;
        }
        return stream.Ended.Wait(timeoutMilliseconds) && stream.DrainSucceeded;
    }

    internal async Task<long> StreamAsync(
        Stream input,
        string sessionId,
        AudioStreamMode mode,
        Action<string, AudioStreamMode> sessionEnded,
        CancellationToken cancellationToken,
        string? ownerId = null)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        if (!await streamGate.WaitAsync(0, cancellationToken))
        {
            // Wi-Fi 抖动后手机会用同一共享 sessionId 重连，而服务端可能还没发现旧连接
            // 已断开。只允许同一所有者的同一共享会话接管，其他情况仍是冲突。
            if (!TrySupersede(sessionId, mode, ownerId)
                || !await streamGate.WaitAsync(TakeoverWaitMs, cancellationToken))
            {
                throw new AudioStreamConflictException("已有手机麦克风正在传输");
            }
            Console.WriteLine($"[audio:{sessionId}] sharedReconnectTakeover");
        }

        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var stream = new ActiveStream
        {
            Preroll = new PreRollBuffer(PreRollHoldBytes),
            PlaybackGate = new object(),
            Ended = new ManualResetEventSlim(false),
            Mode = mode,
            OwnerId = ownerId,
            Provider = new PhonePcmBuffer(mode)
        };
        SwitchingWaveProvider? warm = null;
        IPhoneAudioPlayback? monitoredPlayback = null;
        IPhoneAudioPlayback? output = null;
        CancellationTokenSource? outputFaultMonitorCancellation = null;
        Task? outputFaultMonitor = null;
        try
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref disposed) != 0,
                this);
            Volatile.Write(ref lastPhoneActivity, Environment.TickCount64);
            lock (warmSync)
            {
                warm = EnsureWarmLocked();
                warm?.Attach(stream.Provider);
                // Capture the playback paired with this exact source while the
                // same lock still protects the warm source/playback pair.
                if (warm is not null)
                {
                    monitoredPlayback = warmPlayback;
                }
            }
            if (warm is null)
            {
                output = createPlayback(stream.Provider);
                output.Play();
                monitoredPlayback = output;
            }
            Console.WriteLine(
                $"[audio:{sessionId}] wasapiStarted=+{ElapsedMs(stream.StartedAt)}ms " +
                $"warm={warm is not null}");

            // 只有虚拟音频设备已找到且 WASAPI 真正启动后，
            // 才允许听写管理器唤醒 Typeless。
            lock (sessionSync)
            {
                activeSessionId = sessionId;
                activeCancellation = sessionCancellation;
                activeStream = stream;
                activeMode = mode;
            }
            if (monitoredPlayback is not null)
            {
                // Keep watching through client-cancelled drain/tail handling;
                // only stream finally owns cancellation of this monitor.
                outputFaultMonitorCancellation = new CancellationTokenSource();
                outputFaultMonitor = MonitorPlaybackFaultAsync(
                    stream,
                    sessionId,
                    monitoredPlayback,
                    outputFaultMonitorCancellation.Token,
                    sessionCancellation);
            }
            if (mode == AudioStreamMode.Shared)
            {
                // 共享麦克风长期向虚拟声卡供音，不等待或控制 Typeless。
                // 每台电脑自己的 Typeless 快捷键决定何时开始采集。
                ReleasePreRoll(stream, sessionId, "shared");
            }

            var bytes = new byte[16 * 1024];
            var carry = 0;
            long totalBytes = 0;
            var firstBytesLogged = false;
            var stalled = false;
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(
                sessionCancellation.Token);
            try
            {
                while (true)
                {
                    // 每次读取重新计时：只有持续 stallTimeoutMs 收不到任何字节才判定断流。
                    stall.CancelAfter(stallTimeoutMs);
                    var read = await input.ReadAsync(
                        bytes.AsMemory(carry, bytes.Length - carry),
                        stall.Token);
                    if (read == 0)
                    {
                        break;
                    }
                    if (!firstBytesLogged)
                    {
                        Console.WriteLine(
                            $"[audio:{sessionId}] serverFirstBytes=+{ElapsedMs(stream.StartedAt)}ms");
                        firstBytesLogged = true;
                    }

                    var available = carry + read;
                    var completeBytes = available & ~1;
                    if (completeBytes > 0)
                    {
                        lock (stream.PlaybackGate)
                        {
                            if (!stream.PlaybackReleased
                                && stream.Preroll.StoredBytes + completeBytes
                                    >= stream.Preroll.CapacityBytes)
                            {
                                // 水位超限：Typeless 迟迟未确认采集，自动按序放行，
                                // 不让已到达的语音无界积压。
                                ReleasePreRoll(stream, sessionId, "watermark");
                            }
                            if (stream.PlaybackReleased)
                            {
                                stream.Provider.AddSamples(bytes, 0, completeBytes);
                            }
                            else
                            {
                                stream.Preroll.Write(bytes, 0, completeBytes);
                            }
                        }
                        totalBytes += completeBytes;
                    }
                    carry = available - completeBytes;
                    if (carry == 1)
                    {
                        bytes[0] = bytes[completeBytes];
                    }
                    if (completeBytes > 0)
                    {
                        lock (sessionSync)
                        {
                            // A superseded stream may still finish one read
                            // while the replacement is being admitted.  Do
                            // not let that stale read update the new stream's
                            // health counters.
                            if (ReferenceEquals(activeStream, stream))
                            {
                                stream.ReceivedPcmBytes += completeBytes;
                                stream.LastPcmTick = Environment.TickCount64;
                            }
                        }
                    }
                }

            }
            catch (OperationCanceledException)
                when (stall.IsCancellationRequested && !sessionCancellation.IsCancellationRequested)
            {
                stalled = true;
                Console.Error.WriteLine(
                    $"[audio:{sessionId}] {stallTimeoutMs}ms 未收到手机音频，判定连接已断开；排空已收到的 PCM 后释放接收端");
            }
            catch (OperationCanceledException) when (stream.OutputFaulted)
            {
                Console.Error.WriteLine(
                    $"[audio:{sessionId}] 虚拟声卡输出意外停止，立即终止手机音频上传");
            }
            catch (OperationCanceledException)
            {
                // 旧手机客户端 disconnect/显式取消仍须播放已收到的尾音。
                Console.WriteLine($"[audio:{sessionId}] uploadCancelled; draining received PCM");
            }
            catch (IOException exception)
            {
                Console.WriteLine(
                    $"[audio:{sessionId}] uploadDisconnected={exception.GetType().Name}; draining received PCM");
            }

            if (stream.OutputFaulted)
            {
                lock (stream.PlaybackGate)
                {
                    var discarded = stream.Preroll.TakeAll().Length;
                    stream.PlaybackReleased = true;
                    if (discarded > 0)
                    {
                        Console.WriteLine(
                            $"[audio:{sessionId}] outputFaultPreRollDiscardedBytes={discarded}");
                    }
                }
                throw new InvalidOperationException("虚拟声卡输出故障，音频上传已终止");
            }

            lock (stream.PlaybackGate)
            {
                if (!stream.PlaybackReleased)
                {
                    // 会话结束前 Typeless 从未确认：按序丢弃暂存音频，
                    // 不把孤立语音播进 CABLE。
                    var discarded = stream.Preroll.TakeAll().Length;
                    stream.PlaybackReleased = true;
                    if (discarded > 0)
                    {
                        Console.WriteLine(
                            $"[audio:{sessionId}] preRollDiscardedBytes={discarded}");
                    }
                }
            }

            // 尾部排空：把已放行的音频完整送入 CABLE 后再停 WASAPI，
            // 否则 pre-roll 突发灌入的尾部会被提前截断。被同会话新连接接管时
            // 由新连接继续供音，旧连接直接让出。
            var drainDeadline = Environment.TickCount64 + DrainMaxMs;
            while (!stream.Superseded && !stream.OutputFaulted && stream.Provider.BufferedBytes > 0
                && Environment.TickCount64 < drainDeadline)
            {
                await Task.Delay(40, CancellationToken.None);
            }
            if (stream.OutputFaulted)
            {
                throw new InvalidOperationException("虚拟声卡输出故障，音频上传已终止");
            }
            if (!stream.Superseded && stream.Provider.BufferedBytes > 0)
            {
                Console.Error.WriteLine(
                    $"[audio:{sessionId}] drainTimeoutBytes={stream.Provider.BufferedBytes}");
                throw new TimeoutException("手机尾音未能在限定时间内播放完成");
            }
            else
            {
                Console.WriteLine(
                    $"[audio:{sessionId}] drained=+{ElapsedMs(stream.StartedAt)}ms");
            }
            if (!stream.Superseded && totalBytes > 0 && stream.ReleasedAtMs >= 0)
            {
                // ReadFully 在输入排空后继续生成静音，不额外打开手机麦克风。
                await Task.Delay(OutputTailMs, CancellationToken.None);
                if (stream.OutputFaulted)
                {
                    throw new InvalidOperationException("虚拟声卡输出故障，音频上传已终止");
                }
            }
            output?.Stop();
            if (stream.OutputFaulted)
            {
                throw new InvalidOperationException("虚拟声卡输出故障，音频上传已终止");
            }
            stream.DrainSucceeded = true;
            Console.WriteLine(
                $"[audio:{sessionId}] sessionStopped=+{ElapsedMs(stream.StartedAt)}ms " +
                $"bytes={totalBytes} droppedStaleBytes={stream.Provider.DroppedBytes} " +
                $"skippedSilenceBytes={stream.Provider.SkippedSilenceBytes}");
            if (stalled)
            {
                lock (sessionSync)
                {
                    if (ReferenceEquals(activeStream, stream))
                    {
                        stream.RuntimeError = "stalled";
                    }
                }
                throw new AudioStreamStalledException("手机音频流长时间无数据，连接已释放");
            }
            return totalBytes;
        }
        finally
        {
            var outputFaulted = false;
            try
            {
                TryAudioCleanup(sessionId, "monitorCancel", () =>
                    outputFaultMonitorCancellation?.Cancel());

                if (outputFaultMonitor is not null)
                {
                    await TryAwaitAudioCleanup(
                        sessionId, "monitorWait", outputFaultMonitor).ConfigureAwait(false);
                }

                TryAudioCleanup(sessionId, "monitorDispose", () =>
                    outputFaultMonitorCancellation?.Dispose());

                outputFaulted = stream.OutputFaulted;
                if (outputFaulted)
                {
                    stream.DrainSucceeded = false;
                }

                TryAudioCleanup(sessionId, "playbackDispose", () => output?.Dispose());
                TryAudioCleanup(sessionId, "warmDetach", () => warm?.Detach(stream.Provider));
            }
            finally
            {
                TryAudioCleanup(sessionId, "sessionState", () =>
                {
                    lock (sessionSync)
                    {
                        if (ReferenceEquals(activeCancellation, sessionCancellation))
                        {
                            completedSessionId = sessionId;
                            completedDrainSucceeded = stream.DrainSucceeded;
                            activeCancellation = null;
                            activeSessionId = null;
                            activeStream = null;
                            activeMode = null;
                        }
                    }
                });
                // Ended 事件不 Dispose：WaitForSessionEnd 的等待方可能仍持有引用。
                TryAudioCleanup(sessionId, "endedSignal", () => stream.Ended.Set());
                // 先释放闸门，再回调可能等待听写管理器锁的收尾。
                TryAudioCleanup(sessionId, "streamGate", () => streamGate.Release());
                TryAudioCleanup(sessionId, "sessionEnded", () => sessionEnded(sessionId, mode));
            }

            if (outputFaulted)
            {
                throw new InvalidOperationException("虚拟声卡输出故障，音频上传已终止");
            }
        }
    }

    private static void LogAudioCleanupFailure(
        string sessionId,
        string stage,
        Exception exception) =>
        Console.Error.WriteLine(
            $"[audio:{sessionId}] cleanupFailed={stage}:{exception.GetType().Name}");

    private static void TryAudioCleanup(string sessionId, string stage, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            LogAudioCleanupFailure(sessionId, stage, exception);
        }
    }

    private static async Task TryAwaitAudioCleanup(
        string sessionId,
        string stage,
        Task cleanup)
    {
        try
        {
            await cleanup.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogAudioCleanupFailure(sessionId, stage, exception);
        }
    }

    private async Task MonitorPlaybackFaultAsync(
        ActiveStream stream,
        string sessionId,
        IPhoneAudioPlayback playback,
        CancellationToken monitorCancellation,
        CancellationTokenSource sessionCancellation)
    {
        try
        {
            while (true)
            {
                if (monitorCancellation.IsCancellationRequested)
                {
                    return;
                }
                if (playback.IsFaulted)
                {
                    if (TryMarkPlaybackFault(stream, sessionId, monitorCancellation))
                    {
                        sessionCancellation.Cancel();
                    }
                    return;
                }

                // Keep the monitor inexpensive while still detecting a fault
                // that appears after the initial synchronous check.
                await Task.Delay(100, monitorCancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
        {
            // Stream cleanup or client cancellation stopped this monitor.
        }
        catch (ObjectDisposedException) when (monitorCancellation.IsCancellationRequested)
        {
            // Cleanup raced disposal after the monitor was cancelled.
        }
        catch (ObjectDisposedException) when (!monitorCancellation.IsCancellationRequested)
        {
            // A warm output can be rebuilt while this stream is unwinding.  If
            // it was still the active stream, its source no longer has a
            // playback owner and must be cancelled rather than left ghosting.
            if (TryMarkPlaybackFault(stream, sessionId, monitorCancellation))
            {
                sessionCancellation.Cancel();
            }
        }
        catch (Exception exception) when (!monitorCancellation.IsCancellationRequested)
        {
            if (TryMarkPlaybackFault(stream, sessionId, monitorCancellation))
            {
                Console.Error.WriteLine(
                    $"[audio:{sessionId}] playbackFaultMonitorFailed={exception.GetType().Name}");
                sessionCancellation.Cancel();
            }
        }
        catch (Exception) when (monitorCancellation.IsCancellationRequested)
        {
            // Never let a cancelled old monitor mask stream cleanup.
        }
    }

    private bool TryMarkPlaybackFault(
        ActiveStream stream,
        string sessionId,
        CancellationToken monitorCancellation)
    {
        lock (sessionSync)
        {
            // A warm playback can be rebuilt while an old stream is
            // unwinding.  Only the still-current stream may be cancelled.
            if (monitorCancellation.IsCancellationRequested
                || !ReferenceEquals(activeStream, stream))
            {
                return false;
            }
            stream.OutputFaulted = true;
            stream.RuntimeError = "outputFault";
        }
        Console.Error.WriteLine(
            $"[audio:{sessionId}] playbackFaultObserved; cancelling upload");
        return true;
    }

    private bool TrySupersede(string sessionId, AudioStreamMode mode, string? ownerId)
    {
        CancellationTokenSource? cancellation;
        lock (sessionSync)
        {
            var current = activeStream;
            if (mode != AudioStreamMode.Shared || current is null
                || current.Mode != AudioStreamMode.Shared
                || !string.Equals(activeSessionId, sessionId, StringComparison.Ordinal)
                || !string.Equals(current.OwnerId, ownerId, StringComparison.Ordinal))
            {
                return false;
            }
            current.Superseded = true;
            cancellation = activeCancellation;
        }
        TryCancel(cancellation, $"supersede:{sessionId}");
        return true;
    }

    private static void ReleasePreRoll(ActiveStream stream, string sessionId, string reason)
    {
        var preRollAudio = stream.Preroll.TakeAll();
        // 开口前的静音只会拉长整段延迟：放行时只保留首个语音块前 LeadInMs。
        var trimmed = PcmLatency.LeadingSilenceTrimOffset(
            preRollAudio, PcmLatency.MsToBytes(PcmLatency.LeadInMs));
        if (preRollAudio.Length > trimmed)
        {
            stream.Provider.AddSamples(preRollAudio, trimmed, preRollAudio.Length - trimmed);
        }
        stream.PlaybackReleased = true;
        stream.ReleasedAtMs = ElapsedMs(stream.StartedAt);
        Console.WriteLine(
            $"[audio:{sessionId}] playbackReleased=+{stream.ReleasedAtMs}ms " +
            $"reason={reason} preRollBytes={preRollAudio.Length} leadingSilenceTrimmedBytes={trimmed}");
    }

    internal static MMDevice? SelectDevice(IEnumerable<MMDevice> devices) =>
        SelectDevice(devices, out _);

    private static MMDevice? SelectDevice(IEnumerable<MMDevice> devices, out string? name)
    {
        MMDevice? fallback = null;
        name = null;
        foreach (var device in devices)
        {
            var candidateName = AudioDeviceNames.Read(device);
            if (!candidateName.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // Preserve the existing stable preference: first non-16 cable,
            // otherwise the first 16-channel cable. No sort or second COM read.
            if (!candidateName.Contains("16", StringComparison.OrdinalIgnoreCase))
            {
                name = candidateName;
                return device;
            }
            if (fallback is null)
            {
                fallback = device;
                name = candidateName;
            }
        }
        return fallback;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        idleReaper?.Dispose();
        CancellationTokenSource? cancellation;
        lock (sessionSync)
        {
            cancellation = activeCancellation;
        }
        TryCancel(cancellation, "bridge-dispose");
        IPhoneAudioPlayback? playback;
        lock (warmSync)
        {
            playback = warmPlayback;
            warmPlayback = null;
            warmSource = null;
            Volatile.Write(ref warmReady, 0);
        }
        TryDisposePlayback(playback, "bridge-dispose");
    }

    private static void TryCancel(CancellationTokenSource? cancellation, string reason)
    {
        if (cancellation is null)
        {
            return;
        }
        try
        {
            cancellation.Cancel();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[audio] cancellationFailed={reason}:{exception.GetType().Name}");
        }
    }
}
