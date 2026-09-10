using System.Diagnostics;

/// <summary>受管语音听写会话状态机：面向 IVoiceEngineController 编程，
/// 对 toggle 引擎（按一下切换）与 hold 引擎（按住说话）一视同仁——
/// 触发方式的差异由控制器内部消化，本类只负责校验、音频时序与复位。</summary>
internal sealed class DictationSessionManager : IDisposable
{
    // WASAPI cold start on the second receiver occasionally exceeds 1.2s even
    // though the phone audio request is already connected. These are maximum
    // failure budgets only; successful starts still return immediately.
    private const int AudioSessionReadyTimeoutMilliseconds = 3_000;
    private const int EngineStateTimeoutMilliseconds = 2_000;

    // 手机端已经并行发起 PCM POST。给 VB-CABLE/WASAPI 一个很短的预热窗口，
    // 避免语音引擎在二号电脑上先打开尚未唤醒的 CABLE Output，触发约 2–3 秒
    // 的 Core Audio 冷启动。这里只等 250ms；超时仍立即发送触发键，不会退回
    // 旧版“音频完全建立后才唤醒引擎”的串行长等待。
    private const int AudioWarmupGraceMilliseconds = 250;

    /// <summary>停止语音引擎前等待音频流（含 pre-roll 尾部排空）结束的上限。</summary>
    private const int AudioDrainTimeoutMilliseconds = PhoneAudioBridge.StopWaitMs;

    private readonly object syncRoot = new();
    private readonly IPhoneAudioSessionController audioBridge;
    private readonly IVoiceEngineController engine;

    /// <summary>volatile：/api/health 等读端不得被启动/停止的慢路径阻塞。</summary>
    private volatile string? activeDictationSessionId;
    private string? activeMode;

    internal DictationSessionManager(PhoneAudioBridge audioBridge)
        : this(audioBridge, new WindowsVoiceEngineController())
    {
    }

    internal DictationSessionManager(
        IPhoneAudioSessionController audioBridge,
        IVoiceEngineController engine)
    {
        this.audioBridge = audioBridge;
        this.engine = engine;
    }

    internal bool IsActive => activeDictationSessionId is not null;

    internal string? ActiveSessionId => activeDictationSessionId;

    internal bool Start(string? sessionId, string? requestId, string? mode)
    {
        var normalizedSessionId = ValidateSessionId(sessionId);
        var normalizedRequestId = ValidateRequestId(requestId);
        var normalizedMode = NormalizeMode(mode);
        if (!engine.IsModeConfigured(normalizedMode))
        {
            throw new InvalidOperationException(
                $"{engine.EngineDisplayName} 未配置该模式的快捷键，无法启动");
        }
        // 文件读盘与 Core Audio 枚举都放在锁外：它们是只读前置检查，
        // 进入锁后仍会复核会话唯一性。无可读配置的引擎跳过麦克风校验。
        if (engine.CanVerifyVirtualCable == true && !engine.UsesVirtualCable)
        {
            throw new InvalidOperationException(
                $"{engine.EngineDisplayName} 麦克风未选择 CABLE Output，已拒绝启动");
        }
        var capturingBeforeStart = engine.IsCapturing();
        if (capturingBeforeStart is null)
        {
            throw new InvalidOperationException(
                $"无法读取 {engine.EngineDisplayName} 录音状态，已拒绝启动");
        }
        if (capturingBeforeStart is true)
        {
            throw new InvalidOperationException(
                $"{engine.EngineDisplayName} 已在听写，请先在电脑端停止后重试");
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
                throw new InvalidOperationException("另一个语音听写会话仍在运行");
            }
            var startTimestamp = Stopwatch.GetTimestamp();
            activeMode = normalizedMode;

            var audioWarmed = audioBridge.WaitForSessionActive(
                normalizedSessionId, AudioWarmupGraceMilliseconds);
            Console.WriteLine(
                $"[dictation:{normalizedSessionId}] audioWarmup=" +
                $"{(audioWarmed ? "ready" : "timeout")} " +
                $"+{PhoneAudioBridge.ElapsedMs(startTimestamp)}ms");

            var duplicate = engine.BeginOnce(normalizedRequestId, normalizedMode);
            Console.WriteLine(
                $"[dictation:{normalizedSessionId}] engineStartRequested=" +
                $"+{PhoneAudioBridge.ElapsedMs(startTimestamp)}ms");
            activeDictationSessionId = normalizedSessionId;
            bool? started;
            try
            {
                started = engine.WaitForCapturing(
                    expected: true, EngineStateTimeoutMilliseconds);
            }
            catch (Exception exception)
            {
                ResetFailedStart(normalizedSessionId);
                throw new InvalidOperationException(
                    $"读取 {engine.EngineDisplayName} 启动状态失败", exception);
            }
            if (started is not true)
            {
                ResetFailedStart(normalizedSessionId);
                throw new InvalidOperationException(started is null
                    ? $"无法确认 {engine.EngineDisplayName} 是否开始听写"
                    : $"{engine.EngineDisplayName} 未确认开始听写");
            }
            Console.WriteLine(
                $"[dictation:{normalizedSessionId}] engineCapturing=" +
                $"+{PhoneAudioBridge.ElapsedMs(startTimestamp)}ms");
            // 手机点击后会并行启动录音、音频 POST 和本 start 请求。
            // 短预热后已立即唤醒引擎；这里再用完整失败预算复核对应
            // 音频会话仍然有效。若音频连接失败，ResetFailedStart 会把
            // 已唤醒的引擎自动复位，不留下孤立录音会话。
            if (!audioBridge.WaitForSessionActive(
                    normalizedSessionId, AudioSessionReadyTimeoutMilliseconds))
            {
                ResetFailedStart(normalizedSessionId);
                throw new InvalidOperationException("音频会话不存在或已断开");
            }
            // 引擎已真实采集：放行 pre-roll，把点击后最先到达的音频
            // 按原顺序送入 CABLE，保证第一音节不丢。
            audioBridge.BeginPlayback(normalizedSessionId);
            Console.WriteLine($"语音听写会话已启动：{normalizedSessionId}");
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
        bool audioDrained;
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
                throw new InvalidOperationException("请求的会话不是当前听写会话");
            }

            // 先等音频流（含 pre-roll 尾部排空）真正结束，再停引擎，
            // 否则突发灌入的尾部音频会被引擎提前停止而丢失。
            audioDrained = audioBridge.WaitForSessionEnd(
                normalizedSessionId, AudioDrainTimeoutMilliseconds);

            try
            {
                stopConfirmed = TryStopEngine(normalizedRequestId, out duplicate);
            }
            catch (Exception exception)
            {
                stopConfirmed = false;
                stopFailure = exception;
            }
            // 无论引擎是否确认停止，都释放服务内部所有权和音频会话。
            // 如果真实录音仍在进行，下一次 Start 的状态检查会拒绝误启动，
            // 但不会因为一个陈旧 sessionId 永久锁死服务。
            activeDictationSessionId = null;
        }
        audioBridge.StopSession(normalizedSessionId);
        if (stopFailure is not null)
        {
            throw new InvalidOperationException(
                $"停止 {engine.EngineDisplayName} 时发生错误", stopFailure);
        }
        if (!stopConfirmed)
        {
            throw new InvalidOperationException(
                $"{engine.EngineDisplayName} 仍在听写，停止指令未被确认");
        }
        if (!audioDrained)
        {
            throw new InvalidOperationException(
                "手机尾音传输未完成，已停止会话；请检查连接后重试");
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
                stopped = TryStopEngine(requestId: null, out _);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"音频断流后复位 {engine.EngineDisplayName} 失败：{exception.Message}");
            }
            finally
            {
                // 自动清理失败也不能让陈旧会话永久阻塞之后的请求。
                activeDictationSessionId = null;
            }
            if (stopped)
            {
                Console.WriteLine($"音频断流，已自动复位 {engine.EngineDisplayName}：{sessionId}");
            }
            else
            {
                Console.Error.WriteLine(
                    $"音频断流，但 {engine.EngineDisplayName} 停止状态未被确认：{sessionId}");
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

    private bool TryStopEngine(string? requestId, out bool duplicate)
    {
        var capturing = engine.IsCapturing();
        if (capturing is false)
        {
            duplicate = true;
            return true;
        }

        var mode = activeMode ?? engine.Modes.First();
        duplicate = requestId is null
            ? EndWithoutRequestId(mode)
            : engine.End(mode, requestId);
        var stopped = engine.WaitForCapturing(
            expected: false, EngineStateTimeoutMilliseconds);
        if (stopped is true)
        {
            return true;
        }
        if (stopped is null)
        {
            // 已尽力发送一次停止键，但状态探针不可用时不能向手机谎报成功。
            return false;
        }

        // SendInput 成功只代表 Windows 接收了按键，不代表目标应用
        // 已处理。重试前立即再次读取真实状态，避免引擎刚刚停止后被
        // 第二个切换键重新打开。
        capturing = engine.IsCapturing();
        if (capturing is false)
        {
            return true;
        }
        if (capturing is not true)
        {
            return false;
        }

        // 仅当音频会话仍明确为 Active 时重试一次，
        // 避免已经停止后又被双击切换回开启（hold 引擎的 End 是
        // 幂等释放，重试安全）。
        engine.End(mode, null);
        stopped = engine.WaitForCapturing(
            expected: false, EngineStateTimeoutMilliseconds);
        return stopped is true;
    }

    private bool EndWithoutRequestId(string mode)
    {
        engine.End(mode, null);
        return false;
    }

    private string NormalizeMode(string? mode)
    {
        var normalized = string.IsNullOrWhiteSpace(mode)
            ? engine.Modes.First()
            : mode.Trim().ToLowerInvariant();
        if (!engine.Modes.Contains(normalized))
        {
            throw new ArgumentException($"未知的语音引擎模式：{mode}");
        }
        return normalized;
    }

    private void ResetFailedStart(string sessionId)
    {
        try
        {
            if (!TryStopEngine(requestId: null, out _))
            {
                Console.Error.WriteLine(
                    $"引擎启动失败，停止状态未被确认：{sessionId}");
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"引擎启动失败后的复位也失败：{exception.Message}");
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
                    if (!TryStopEngine(requestId: null, out _))
                    {
                        Console.Error.WriteLine("退出时引擎停止状态未被确认");
                    }
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"退出时复位引擎失败：{exception.Message}");
                }
            }
        }
        if (sessionId is not null)
        {
            audioBridge.StopSession(sessionId);
        }
        engine.Dispose();
    }
}
