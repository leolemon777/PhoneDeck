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

    private static readonly int PreRollHoldBytes = SampleRate * 2 * PreRollHoldMs / 1_000;

    private readonly SemaphoreSlim streamGate = new(1, 1);
    private readonly object sessionSync = new();
    private CancellationTokenSource? activeCancellation;
    private ActiveStream? activeStream;
    private string? activeSessionId;

    private sealed class ActiveStream
    {
        public required PreRollBuffer Preroll { get; init; }
        public required BufferedWaveProvider Provider { get; init; }
        public required object PlaybackGate { get; init; }
        public required ManualResetEventSlim Ended { get; init; }
        public long StartedAt { get; } = Stopwatch.GetTimestamp();
        public bool PlaybackReleased;
        public long ReleasedAtMs;
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
            return SelectDevice(devices)?.FriendlyName;
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
        cancellation?.Cancel();
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
            var preRollAudio = stream.Preroll.TakeAll();
            if (preRollAudio.Length > 0)
            {
                stream.Provider.AddSamples(preRollAudio, 0, preRollAudio.Length);
            }
            stream.PlaybackReleased = true;
            stream.ReleasedAtMs = ElapsedMs(stream.StartedAt);
            Console.WriteLine(
                $"[audio:{sessionId}] playbackReleased=+{stream.ReleasedAtMs}ms " +
                $"firstAddSamples=+{stream.ReleasedAtMs}ms preRollBytes={preRollAudio.Length}");
        }
    }

    public bool WaitForSessionEnd(string sessionId, int timeoutMilliseconds)
    {
        ActiveStream? stream;
        lock (sessionSync)
        {
            if (!string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
            {
                return true;
            }
            stream = activeStream;
        }
        if (stream is null)
        {
            return true;
        }
        return stream.Ended.Wait(timeoutMilliseconds);
    }

    internal async Task<long> StreamAsync(
        Stream input,
        string sessionId,
        Action<string> sessionEnded,
        CancellationToken cancellationToken)
    {
        if (!await streamGate.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("已有手机麦克风正在传输");
        }

        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        MMDevice? selected = null;
        List<MMDevice>? devices = null;
        WasapiOut? output = null;
        long totalBytes = 0;
        var stream = new ActiveStream
        {
            Preroll = new PreRollBuffer(PreRollHoldBytes),
            PlaybackGate = new object(),
            Ended = new ManualResetEventSlim(false),
            Provider = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, 1))
            {
                // 700ms 实时余量 + pre-roll 突发灌入 + 排空期间的到达数据。
                BufferDuration = TimeSpan.FromMilliseconds(700 + PreRollHoldMs + 500),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            }
        };
        try
        {
            // 分段计时：定位 WASAPI 启动慢在哪个阶段（枚举/选设备/初始化/启动）。
            var enumerateAt = Environment.TickCount64;
            using var enumerator = new MMDeviceEnumerator();
            devices = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .ToList();
            var selectAt = Environment.TickCount64;
            selected = SelectDevice(devices);
            var initAt = Environment.TickCount64;
            if (selected is null)
            {
                throw new InvalidOperationException("未找到 VB-Audio Virtual Cable 播放端");
            }

            output = new WasapiOut(
                selected,
                AudioClientShareMode.Shared,
                useEventSync: true,
                latency: 30);
            output.Init(stream.Provider);
            var playAt = Environment.TickCount64;
            output.Play();
            Console.WriteLine(
                $"[audio:{sessionId}] wasapiStarted=+{ElapsedMs(stream.StartedAt)}ms " +
                $"enumerate={selectAt - enumerateAt}ms select={initAt - selectAt}ms " +
                $"init={playAt - initAt}ms play={Environment.TickCount64 - playAt}ms");

            // 只有虚拟音频设备已找到且 WASAPI 真正启动后，
            // 才允许听写管理器唤醒 Typeless。
            lock (sessionSync)
            {
                activeSessionId = sessionId;
                activeCancellation = sessionCancellation;
                activeStream = stream;
            }

            var bytes = new byte[16 * 1024];
            var carry = 0;
            var firstBytesLogged = false;
            while (true)
            {
                var read = await input.ReadAsync(
                    bytes.AsMemory(carry, bytes.Length - carry),
                    sessionCancellation.Token);
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
            return totalBytes;
        }
        finally
        {
            // 尾部排空必须在 finally 且先于 Ended.Set/sessionEnded：
            // 手机断流（RST/异常）也不能跳过，否则 WASAPI 启动慢造成的
            // 积压音频永远不会播进 CABLE，Typeless 停止键一响，
            // 落在积压里的最后几秒语音整体丢失。
            try
            {
                var drainStartedAt = Environment.TickCount64;
                var drainDeadline = drainStartedAt + DrainMaxMs;
                while (stream.Provider.BufferedBytes > 0
                    && Environment.TickCount64 < drainDeadline)
                {
                    await Task.Delay(40, CancellationToken.None);
                }
                if (stream.Provider.BufferedBytes > 0)
                {
                    Console.Error.WriteLine(
                        $"[audio:{sessionId}] drainTimeoutBytes={stream.Provider.BufferedBytes}");
                }
                else
                {
                    Console.WriteLine(
                        $"[audio:{sessionId}] drained=+{ElapsedMs(stream.StartedAt)}ms " +
                        $"bufferedWait={Environment.TickCount64 - drainStartedAt}ms");
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"[audio:{sessionId}] drainFailed: {exception.Message}");
            }
            try
            {
                output?.Stop();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"[audio:{sessionId}] outputStopFailed: {exception.Message}");
            }
            output?.Dispose();
            Console.WriteLine(
                $"[audio:{sessionId}] sessionStopped=+{ElapsedMs(stream.StartedAt)}ms " +
                $"bytes={totalBytes}");
            lock (sessionSync)
            {
                if (ReferenceEquals(activeCancellation, sessionCancellation))
                {
                    activeCancellation = null;
                    activeSessionId = null;
                    activeStream = null;
                }
            }
            // Ended 事件不 Dispose：WaitForSessionEnd 的等待方可能仍持有引用。
            stream.Ended.Set();
            try
            {
                if (devices is not null)
                {
                    foreach (var device in devices)
                    {
                        device.Dispose();
                    }
                }
            }
            finally
            {
                // 先允许下一条音频流进入，再通知听写管理器。
                // 否则快速“停止→重新开始”时，旧流的 AudioEnded
                // 可能等待管理器锁，而新流又在等待旧流释放闸门。
                streamGate.Release();
                sessionEnded(sessionId);
            }
        }
    }

    private static void ReleasePreRoll(ActiveStream stream, string sessionId, string reason)
    {
        var preRollAudio = stream.Preroll.TakeAll();
        if (preRollAudio.Length > 0)
        {
            stream.Provider.AddSamples(preRollAudio, 0, preRollAudio.Length);
        }
        stream.PlaybackReleased = true;
        stream.ReleasedAtMs = ElapsedMs(stream.StartedAt);
        Console.WriteLine(
            $"[audio:{sessionId}] playbackReleased=+{stream.ReleasedAtMs}ms " +
            $"reason={reason} preRollBytes={preRollAudio.Length}");
    }

    /// <summary>
    /// 缓存 CABLE 播放端的设备 ID。FriendlyName 是 COM 属性读取，
    /// 慢/僵尸设备（蓝牙 A2DP 等）单个调用可阻塞 1-2 秒，是 WASAPI
    /// 启动慢与音频积压的元凶；ID 比较不触发属性读取。
    /// </summary>
    private static string? cachedCableDeviceId;

    private static MMDevice? SelectDevice(IEnumerable<MMDevice> devices)
    {
        var cached = cachedCableDeviceId;
        if (cached is not null)
        {
            var byId = devices.FirstOrDefault(device => device.ID == cached);
            if (byId is not null)
            {
                return byId;
            }
            // 缓存的设备已移除：清空后回退到名字匹配。
            cachedCableDeviceId = null;
        }
        MMDevice? selected = null;
        bool prefer16 = false;
        foreach (var device in devices)
        {
            string name;
            try
            {
                name = device.FriendlyName;
            }
            catch
            {
                // 僵尸设备：读取友好名失败不影响其他候选。
                continue;
            }
            if (!name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var is16 = name.Contains("16", StringComparison.OrdinalIgnoreCase);
            if (selected is null || (is16 && !prefer16))
            {
                selected = device;
                prefer16 = is16;
            }
        }
        if (selected is not null)
        {
            cachedCableDeviceId = selected.ID;
        }
        return selected;
    }

    /// <summary>启动时后台预热设备选择缓存，避免首条音频流付 1-2 秒设备名查询。</summary>
    public void Prewarm()
    {
        Task.Run(() =>
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var devices = enumerator
                    .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .ToList();
                var startedAt = Environment.TickCount64;
                var selected = SelectDevice(devices);
                Console.WriteLine(
                    $"[prewarm] devices={devices.Count} " +
                    $"cable={(selected is not null ? "cached" : "not-found")} " +
                    $"selectMs={Environment.TickCount64 - startedAt}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[prewarm] failed: {exception.Message}");
            }
        });
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (sessionSync)
        {
            cancellation = activeCancellation;
        }
        cancellation?.Cancel();
    }
}
