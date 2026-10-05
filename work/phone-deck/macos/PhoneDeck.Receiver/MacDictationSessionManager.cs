namespace PhoneDeck.MacReceiver;

/// <summary>受管语音听写会话状态机：面向 IMacVoiceEngineController 编程，
/// toggle（按一下切换）与 hold（按住说话）引擎的差异由控制器内部消化。</summary>
internal sealed class MacDictationSessionManager(
    IMacPhoneAudioSessionController audio,
    IMacVoiceEngineController engine) : IDisposable
{
    private readonly object syncRoot = new();
    private volatile string? activeSessionId;
    private volatile string? activeOwnerClientId;
    private string? activeMode;

    /// <summary>与 Windows 共用的代次/墓碑登记：停止后迟到的 start 不得复活同一会话。</summary>
    internal SessionLeaseRegistry Leases { get; } = new();

    internal bool IsActive => activeSessionId is not null;
    internal string? ActiveSessionId => activeSessionId;

    /// <summary>当前会话是否属于该手机（USB 与旧共享令牌同为 legacy 身份）。</summary>
    internal bool IsOwnedBy(string? clientId) =>
        activeSessionId is not null
        && string.Equals(PhoneStopReceipts.NormalizeOwner(activeOwnerClientId),
            PhoneStopReceipts.NormalizeOwner(clientId), StringComparison.Ordinal);

    /// <summary>phoneStopV1：电脑本机结束会话时写入，手机据此立即关麦。</summary>
    internal PhoneStopReceipts Receipts { get; } = new();

    /// <summary>电脑本机主动结束（控制页按钮）：先发凭据让手机停止供音，再收尾。</summary>
    internal void StopFromDesktop()
    {
        var session = activeSessionId;
        if (session is null)
        {
            return;
        }
        Receipts.Record(session, activeOwnerClientId);
        Stop(session, Guid.NewGuid().ToString());
    }

    internal bool Start(string? sessionId, string? requestId, string? mode, string? clientId = null)
    {
        var normalizedSession = ValidateId(sessionId, "sessionId");
        var normalizedRequest = ValidateId(requestId, "requestId");
        var normalizedMode = NormalizeMode(mode);
        switch (Leases.BeginStart(normalizedSession, PhoneStopReceipts.NormalizeOwner(clientId)))
        {
            case SessionLeaseRegistry.StartOutcome.Idempotent:
                if (string.Equals(activeSessionId, normalizedSession, StringComparison.Ordinal))
                {
                    return true;
                }
                throw new InvalidOperationException("该会话尚未确认开始，请重试");
            case SessionLeaseRegistry.StartOutcome.RejectedTombstoned:
                throw new InvalidOperationException("该会话已终止，迟到的启动请求已被拒绝；请开启新会话");
            case SessionLeaseRegistry.StartOutcome.RejectedOwned:
                throw new InvalidOperationException("另一个语音听写会话仍在运行");
        }
        try
        {
            return StartReserved(normalizedSession, normalizedRequest, normalizedMode, clientId);
        }
        catch
        {
            // 启动失败不是终止：同 sessionId 重试是合法新尝试，只移除登记、不写墓碑。
            Leases.Abandon(normalizedSession);
            throw;
        }
    }

    private bool StartReserved(
        string normalizedSession, string normalizedRequest, string normalizedMode, string? clientId)
    {
        lock (syncRoot)
        {
            if (string.Equals(activeSessionId, normalizedSession, StringComparison.Ordinal))
            {
                return true;
            }
            if (activeSessionId is not null)
            {
                throw new InvalidOperationException("另一个语音听写会话仍在运行");
            }
        }
        if (!engine.IsModeConfigured(normalizedMode))
        {
            throw new InvalidOperationException(
                $"{engine.EngineDisplayName} 未配置该模式的快捷键");
        }
        // 引擎无可读配置时（CanVerifyVirtualCable 为 null）跳过麦克风校验，
        // 由 Core Audio 状态探针确认真实采集。
        if (engine.CanVerifyVirtualCable == true && !engine.UsesVirtualCable)
        {
            throw new InvalidOperationException(
                $"{engine.EngineDisplayName} 麦克风未选择 BlackHole，已拒绝手机控制模式");
        }
        var before = engine.IsCapturing();
        if (before is null)
        {
            throw new InvalidOperationException(
                "Core Audio 进程状态探针不可用，已拒绝手机控制模式");
        }
        if (before is true)
        {
            throw new InvalidOperationException(
                $"{engine.EngineDisplayName} 已在采集，请先在电脑端停止");
        }

        lock (syncRoot)
        {
            if (activeSessionId is not null)
            {
                throw new InvalidOperationException("另一个语音听写会话仍在运行");
            }
            // 常驻输出已让 BlackHole 处于运行状态时，引擎与手机音频建连并行：
            // 先触发引擎，再等待音频会话；首段语音由手机与接收端 pre-roll 保留。
            // 输出未常驻时保持原顺序，避免引擎先打开尚未运行的虚拟声卡。
            var parallelStart = audio.OutputWarm;
            if (!parallelStart && !audio.WaitForSessionActive(normalizedSession, 3_000))
            {
                throw new InvalidOperationException("对应的手机音频会话不存在");
            }
            activeSessionId = normalizedSession;
            activeOwnerClientId = clientId;
            activeMode = normalizedMode;
            var beginSent = false;
            try
            {
                var duplicate = engine.BeginOnce(normalizedRequest, normalizedMode);
                beginSent = true;
                if (engine.WaitForCapturing(true, 2_000) is not true)
                {
                    throw new InvalidOperationException(
                        $"{engine.EngineDisplayName} 未确认开始采集");
                }
                if (parallelStart && !audio.WaitForSessionActive(normalizedSession, 3_000))
                {
                    throw new InvalidOperationException("对应的手机音频会话不存在");
                }
                audio.BeginPlayback(normalizedSession);
                return duplicate;
            }
            catch
            {
                activeSessionId = null;
                activeOwnerClientId = null;
                try
                {
                    if (beginSent)
                    {
                        ResetEngine();
                    }
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"启动失败后复位 {engine.EngineDisplayName} 失败：{exception.Message}");
                }
                audio.StopSession(normalizedSession);
                throw;
            }
        }
    }

    internal bool Stop(string? sessionId, string? requestId,
        string? requesterClientId = null, bool checkOwner = false)
    {
        var normalizedSession = ValidateId(sessionId, "sessionId");
        var normalizedRequest = ValidateId(requestId, "requestId");
        // 多手机：另一台手机不能结束别人的听写；在写墓碑前拒绝。
        if (checkOwner
            && string.Equals(activeSessionId, normalizedSession, StringComparison.Ordinal)
            && !IsOwnedBy(requesterClientId))
        {
            throw new InvalidOperationException("该语音会话属于另一台手机，不能由本机结束");
        }
        // 停止优先：先写墓碑，此后同会话迟到的 start 一律拒绝。
        var stopOwnsSession = Leases.BeginStop(normalizedSession);
        lock (syncRoot)
        {
            if (activeSessionId is null)
            {
                audio.StopSession(normalizedSession);
                return true;
            }
            if (!string.Equals(activeSessionId, normalizedSession, StringComparison.Ordinal))
            {
                if (stopOwnsSession)
                {
                    Leases.Bury(normalizedSession);
                }
                throw new InvalidOperationException("请求的会话不是当前听写会话");
            }
            var audioDrained = audio.WaitForSessionEnd(normalizedSession, MacPhoneAudioBridge.StopWaitMs);
            var duplicate = false;
            bool? stopped = null;
            Exception? failure = null;
            try
            {
                if (engine.IsCapturing() is false)
                {
                    // The user may already have stopped in Typeless. A second toggle
                    // would start a new recording while the phone is shutting down.
                    engine.ReleaseHeldKeys();
                    stopped = true;
                }
                else
                {
                    duplicate = engine.End(activeMode ?? engine.Modes.First(), normalizedRequest);
                    stopped = engine.WaitForCapturing(false, 2_000);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                activeSessionId = null;
                activeOwnerClientId = null;
                engine.ReleaseHeldKeys();
                audio.StopSession(normalizedSession);
            }
            if (failure is not null)
            {
                throw new InvalidOperationException(
                    $"停止 {engine.EngineDisplayName} 时发生错误", failure);
            }
            if (stopped is not true)
            {
                throw new InvalidOperationException(stopped is null
                    ? $"无法确认 {engine.EngineDisplayName} 是否停止"
                    : $"{engine.EngineDisplayName} 仍在采集");
            }
            if (!audioDrained)
                throw new InvalidOperationException("手机尾音传输未完成，已停止会话；请检查连接后重试");
            return duplicate;
        }
    }

    internal void AudioEnded(string sessionId)
    {
        lock (syncRoot)
        {
            if (!string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }
            // 断流终止会话：写墓碑，迟到的 start 不能复活。
            Leases.Bury(sessionId);
            try
            {
                if (engine.IsCapturing() is true)
                {
                    ResetEngine();
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"断流后复位 {engine.EngineDisplayName} 失败：{exception.Message}");
            }
            finally
            {
                activeSessionId = null;
                activeOwnerClientId = null;
                engine.ReleaseHeldKeys();
            }
        }
    }

    /// <summary>内部复位：hold 引擎释放按键，toggle 引擎补发一次切换；
    /// 结束后等待确认停止。</summary>
    private void ResetEngine()
    {
        engine.End(activeMode ?? engine.Modes.First(), null);
        engine.WaitForCapturing(false, 2_000);
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            var session = activeSessionId;
            if (session is not null)
            {
                audio.StopSession(session);
            }
            activeSessionId = null;
                activeOwnerClientId = null;
        }
        engine.Dispose();
    }

    private static string ValidateId(string? value, string name)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 128
            || !Guid.TryParse(normalized, out _))
        {
            throw new ArgumentException($"无效的 {name}");
        }
        return normalized;
    }

    private string NormalizeMode(string? mode)
    {
        var normalized = string.IsNullOrWhiteSpace(mode)
            ? engine.Modes.First()
            : mode.Trim().ToLowerInvariant();
        if (!engine.Modes.Contains(normalized))
        {
            throw new ArgumentException("未知的语音引擎模式");
        }
        return normalized;
    }
}
