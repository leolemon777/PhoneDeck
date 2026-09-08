namespace PhoneDeck.MacReceiver;

/// <summary>受管语音听写会话状态机：面向 IMacVoiceEngineController 编程，
/// toggle（按一下切换）与 hold（按住说话）引擎的差异由控制器内部消化。</summary>
internal sealed class MacDictationSessionManager(
    IMacPhoneAudioSessionController audio,
    IMacVoiceEngineController engine) : IDisposable
{
    private readonly object syncRoot = new();
    private volatile string? activeSessionId;
    private string? activeMode;

    internal bool IsActive => activeSessionId is not null;
    internal string? ActiveSessionId => activeSessionId;

    internal bool Start(string? sessionId, string? requestId, string? mode)
    {
        var normalizedSession = ValidateId(sessionId, "sessionId");
        var normalizedRequest = ValidateId(requestId, "requestId");
        var normalizedMode = NormalizeMode(mode);
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
            if (!audio.WaitForSessionActive(normalizedSession, 3_000))
            {
                throw new InvalidOperationException("对应的手机音频会话不存在");
            }
            activeSessionId = normalizedSession;
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
                audio.BeginPlayback(normalizedSession);
                return duplicate;
            }
            catch
            {
                activeSessionId = null;
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

    internal bool Stop(string? sessionId, string? requestId)
    {
        var normalizedSession = ValidateId(sessionId, "sessionId");
        var normalizedRequest = ValidateId(requestId, "requestId");
        lock (syncRoot)
        {
            if (activeSessionId is null)
            {
                audio.StopSession(normalizedSession);
                return true;
            }
            if (!string.Equals(activeSessionId, normalizedSession, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("请求的会话不是当前听写会话");
            }
            audio.WaitForSessionEnd(normalizedSession, 2_000);
            var duplicate = false;
            bool? stopped = null;
            Exception? failure = null;
            try
            {
                duplicate = engine.End(activeMode ?? engine.Modes.First(), normalizedRequest);
                stopped = engine.WaitForCapturing(false, 2_000);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                activeSessionId = null;
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
