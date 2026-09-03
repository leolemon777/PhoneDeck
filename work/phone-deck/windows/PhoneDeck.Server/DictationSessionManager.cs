using System.Diagnostics;

internal sealed class DictationSessionManager : IDisposable
{
    // WASAPI cold start on the second receiver occasionally exceeds 1.2s even
    // though the phone audio request is already connected. These are maximum
    // failure budgets only; successful starts still return immediately.
    private const int AudioSessionReadyTimeoutMilliseconds = 3_000;
    private const int TypelessStateTimeoutMilliseconds = 2_000;

    // 手机端已经并行发起 PCM POST。给 VB-CABLE/WASAPI 一个很短的预热窗口，
    // 避免 Typeless 在二号电脑上先打开尚未唤醒的 CABLE Output，触发约 2–3 秒
    // 的 Core Audio 冷启动。这里只等 250ms；超时仍立即发送快捷键，不会退回
    // 旧版“音频完全建立后才唤醒 Typeless”的串行长等待。
    private const int AudioWarmupGraceMilliseconds = 250;

    /// <summary>停止 Typeless 前等待音频流（含 pre-roll 尾部排空）结束的上限。</summary>
    private const int AudioDrainTimeoutMilliseconds = 2_000;

    private static readonly string[] ValidModes = { "dictation", "translation", "ask" };
    private readonly object syncRoot = new();
    private readonly IPhoneAudioSessionController audioBridge;
    private readonly ITypelessController typeless;

    /// <summary>volatile：/api/health 等读端不得被启动/停止的慢路径阻塞。</summary>
    private volatile string? activeDictationSessionId;
    private string activeMode = "dictation";

    internal DictationSessionManager(PhoneAudioBridge audioBridge)
        : this(audioBridge, new WindowsTypelessController())
    {
    }

    internal DictationSessionManager(
        IPhoneAudioSessionController audioBridge,
        ITypelessController typeless)
    {
        this.audioBridge = audioBridge;
        this.typeless = typeless;
    }

    internal bool IsActive => activeDictationSessionId is not null;

    internal string? ActiveSessionId => activeDictationSessionId;

    internal bool Start(string? sessionId, string? requestId, string? mode)
    {
        var normalizedSessionId = ValidateSessionId(sessionId);
        var normalizedRequestId = ValidateRequestId(requestId);
        var normalizedMode = NormalizeMode(mode);
        if (!typeless.IsModeConfigured(normalizedMode))
        {
            throw new InvalidOperationException(
                "Typeless 未配置该模式的快捷键，无法启动");
        }
        // 文件读盘与 Core Audio 枚举都放在锁外：它们是只读前置检查，
        // 进入锁后仍会复核会话唯一性。
        if (!typeless.UsesVirtualCable)
        {
            throw new InvalidOperationException(
                "Typeless 麦克风未选择 CABLE Output，已拒绝启动");
        }
        var capturingBeforeStart = typeless.IsCapturing();
        if (capturingBeforeStart is null)
        {
            throw new InvalidOperationException(
                "无法读取 Typeless 录音状态，已拒绝启动");
        }
        if (capturingBeforeStart is true)
        {
            throw new InvalidOperationException(
                "Typeless 已在听写，请先在电脑端停止后重试");
        }

        lock (syncRoot)
        {
            if (string.Equals(activeDictationSessionId, normalizedSessionId,
                    StringComparison.Ordinal))
            {
                return true;
            }
            if (activeDictationSessionId is not null)
            {
                throw new InvalidOperationException("另一个 Typeless 听写会话仍在运行");
            }
            var startTimestamp = Stopwatch.GetTimestamp();
            activeMode = normalizedMode;

            var audioWarmed = audioBridge.WaitForSessionActive(
                normalizedSessionId, AudioWarmupGraceMilliseconds);
            Console.WriteLine(
                $"[dictation:{normalizedSessionId}] audioWarmup=" +
                $"{(audioWarmed ? "ready" : "timeout")} " +
                $"+{PhoneAudioBridge.ElapsedMs(startTimestamp)}ms");

            var duplicate = typeless.ToggleOnce(normalizedRequestId, normalizedMode);
            Console.WriteLine(
                $"[dictation:{normalizedSessionId}] typelessStartRequested=" +
                $"+{PhoneAudioBridge.ElapsedMs(startTimestamp)}ms");
            activeDictationSessionId = normalizedSessionId;

            // 手机点击后并行启动录音、音频 POST 和本 start 请求。
            // 立即放行 pre-roll，把点击后最先到达的音频按原顺序送入 CABLE。
            audioBridge.BeginPlayback(normalizedSessionId);

            // 快速探针仅作诊断日志记录，不阻断已触发的会话
            bool? started = null;
            try
            {
                started = typeless.WaitForCapturing(expected: true, 150);
            }
            catch { }

            Console.WriteLine(
                $"[dictation:{normalizedSessionId}] typelessCapturing={(started == true ? "ready" : "stream_active")} " +
                $"+{PhoneAudioBridge.ElapsedMs(startTimestamp)}ms");

            Console.WriteLine($"Typeless 会话已启动：{normalizedSessionId}");
            return duplicate;
        }
    }

    internal bool Stop(string? sessionId, string? requestId)
    {
        var normalizedSessionId = ValidateSessionId(sessionId);
        var normalizedRequestId = ValidateRequestId(requestId);
        bool duplicate = false;
        bool stopConfirmed = true;
        Exception? stopFailure = null;
        lock (syncRoot)
        {
            if (activeDictationSessionId is null)
            {
                audioBridge.StopSession(normalizedSessionId);
                return true;
            }
            if (!string.Equals(activeDictationSessionId, normalizedSessionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("请求的会话不是当前 Typeless 会话");
            }

            // 先等音频流（含 pre-roll 尾部排空）真正结束，再停 Typeless，
            // 否则突发灌入的尾部音频会被 Typeless 提前停止而丢失。
            audioBridge.WaitForSessionEnd(
                normalizedSessionId, AudioDrainTimeoutMilliseconds);

            try
            {
                stopConfirmed = TryStopTypeless(normalizedRequestId, out duplicate);
            }
            catch (Exception exception)
            {
                stopConfirmed = false;
                stopFailure = exception;
            }
            // 无论 Typeless 是否确认，都释放服务内部所有权和音频会话。
            // 如果真实录音仍在进行，下一次 Start 的状态检查会拒绝误启动，
            // 但不会因为一个陈旧 sessionId 永久锁死服务。
            activeDictationSessionId = null;
        }
        audioBridge.StopSession(normalizedSessionId);
        if (stopFailure is not null)
        {
            throw new InvalidOperationException("停止 Typeless 时发生错误", stopFailure);
        }
        if (!stopConfirmed)
        {
            throw new InvalidOperationException("Typeless 仍在听写，停止指令未被确认");
        }
        Console.WriteLine(
            $"[dictation:{normalizedSessionId}] sessionStopped 已停止");
        return duplicate;
    }

    internal void AudioEnded(string sessionId)
    {
        lock (syncRoot)
        {
            if (!string.Equals(activeDictationSessionId, sessionId,
                    StringComparison.Ordinal))
            {
                return;
            }

            var stopped = false;
            try
            {
                stopped = TryStopTypeless(requestId: null, out _);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"音频断流后复位 Typeless 失败：{exception.Message}");
            }
            finally
            {
                // 自动清理失败也不能让陈旧会话永久阻塞之后的请求。
                activeDictationSessionId = null;
            }
            if (stopped)
            {
                Console.WriteLine($"音频断流，已自动复位 Typeless：{sessionId}");
            }
            else
            {
                Console.Error.WriteLine(
                    $"音频断流，但 Typeless 停止状态未被确认：{sessionId}");
            }
        }
    }

    private static string ValidateSessionId(string? sessionId)
    {
        var normalized = sessionId?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 128
            || !Guid.TryParse(normalized, out _))
        {
            throw new ArgumentException("无效的 sessionId");
        }
        return normalized;
    }

    private static string ValidateRequestId(string? requestId)
    {
        var normalized = requestId?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 128)
        {
            throw new ArgumentException("无效的 requestId");
        }
        return normalized;
    }

    private bool TryStopTypeless(string? requestId, out bool duplicate)
    {
        var capturing = typeless.IsCapturing();
        if (capturing is false)
        {
            duplicate = true;
            return true;
        }

        duplicate = requestId is null
            ? ToggleWithoutRequestId()
            : typeless.ToggleOnce(requestId, activeMode);
        var stopped = typeless.WaitForCapturing(
            expected: false, TypelessStateTimeoutMilliseconds);
        if (stopped is true)
        {
            return true;
        }
        if (stopped is null)
        {
            // 已尽力发送一次停止键，但状态探针不可用时不能向手机谎报成功。
            return false;
        }

        // SendInput 成功只代表 Windows 接收了按键，不代表 Electron
        // 应用已处理。重试前立即再次读取真实状态，避免 Typeless
        // 刚刚停止后被第二个切换键重新打开。
        capturing = typeless.IsCapturing();
        if (capturing is false)
        {
            return true;
        }
        if (capturing is not true)
        {
            return false;
        }

        // 仅当音频会话仍明确为 Active 时重试一次，
        // 避免已经停止后又被双击切换回开启。
        typeless.Toggle(activeMode);
        stopped = typeless.WaitForCapturing(
            expected: false, TypelessStateTimeoutMilliseconds);
        return stopped is true;
    }

    private bool ToggleWithoutRequestId()
    {
        typeless.Toggle(activeMode);
        return false;
    }

    private static string NormalizeMode(string? mode)
    {
        var normalized = string.IsNullOrWhiteSpace(mode)
            ? "dictation"
            : mode.Trim().ToLowerInvariant();
        if (!ValidModes.Contains(normalized))
        {
            throw new ArgumentException($"未知的 Typeless 模式：{mode}");
        }
        return normalized;
    }

    private void ResetFailedStart(string sessionId)
    {
        try
        {
            if (!TryStopTypeless(requestId: null, out _))
            {
                Console.Error.WriteLine(
                    $"Typeless 启动失败，停止状态未被确认：{sessionId}");
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Typeless 启动失败后的复位也失败：{exception.Message}");
        }
        activeDictationSessionId = null;
    }

    public void Dispose()
    {
        string? sessionId;
        lock (syncRoot)
        {
            sessionId = activeDictationSessionId;
            activeDictationSessionId = null;
            if (sessionId is not null)
            {
                try
                {
                    if (!TryStopTypeless(requestId: null, out _))
                    {
                        Console.Error.WriteLine("退出时 Typeless 停止状态未被确认");
                    }
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"退出时复位 Typeless 失败：{exception.Message}");
                }
            }
        }
        if (sessionId is not null)
        {
            audioBridge.StopSession(sessionId);
        }
    }
}
