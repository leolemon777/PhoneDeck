namespace PhoneDeck.MacReceiver;

internal sealed class MacDictationSessionManager(
    IMacPhoneAudioSessionController audio,
    IMacTypelessController typeless) : IDisposable
{
    private readonly object syncRoot = new();
    private volatile string? activeSessionId;
    private string activeMode = "dictation";

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
                throw new InvalidOperationException("另一个 Typeless 会话仍在运行");
            }
        }
        var configuration = typeless.Configuration;
        if (!configuration.UsesBlackHole)
        {
            throw new InvalidOperationException(
                "Typeless 麦克风未选择 BlackHole，已拒绝手机控制模式");
        }
        if (configuration.BindingFor(normalizedMode) is null)
        {
            throw new InvalidOperationException("Typeless 未配置该模式的快捷键");
        }
        var before = typeless.IsCapturing();
        if (before is null)
        {
            throw new InvalidOperationException(
                "Core Audio 进程状态探针不可用，已拒绝手机控制模式");
        }
        if (before is true)
        {
            throw new InvalidOperationException("Typeless 已在采集，请先在电脑端停止");
        }

        lock (syncRoot)
        {
            if (activeSessionId is not null)
            {
                throw new InvalidOperationException("另一个 Typeless 会话仍在运行");
            }
            if (!audio.WaitForSessionActive(normalizedSession, 3_000))
            {
                throw new InvalidOperationException("对应的手机音频会话不存在");
            }
            activeSessionId = normalizedSession;
            activeMode = normalizedMode;
            var toggleSent = false;
            try
            {
                var duplicate = typeless.ToggleOnce(normalizedRequest, normalizedMode);
                toggleSent = true;
                if (typeless.WaitForCapturing(true, 2_000) is not true)
                {
                    throw new InvalidOperationException("Typeless 未确认开始采集");
                }
                audio.BeginPlayback(normalizedSession);
                return duplicate;
            }
            catch
            {
                activeSessionId = null;
                try
                {
                    if (toggleSent && typeless.IsCapturing() is not false)
                    {
                        typeless.Toggle(normalizedMode);
                        typeless.WaitForCapturing(false, 2_000);
                    }
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("启动失败后复位 Typeless 失败：" + exception.Message);
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
                throw new InvalidOperationException("请求的会话不是当前 Typeless 会话");
            }
            audio.WaitForSessionEnd(normalizedSession, 2_000);
            var duplicate = false;
            bool? stopped = null;
            Exception? failure = null;
            try
            {
                duplicate = typeless.ToggleOnce(normalizedRequest, activeMode);
                stopped = typeless.WaitForCapturing(false, 2_000);
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
                throw new InvalidOperationException("停止 Typeless 时发生错误", failure);
            }
            if (stopped is not true)
            {
                throw new InvalidOperationException(stopped is null
                    ? "无法确认 Typeless 是否停止" : "Typeless 仍在采集");
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
                if (typeless.IsCapturing() is true)
                {
                    typeless.Toggle(activeMode);
                    typeless.WaitForCapturing(false, 2_000);
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("断流后复位 Typeless 失败：" + exception.Message);
            }
            finally
            {
                activeSessionId = null;
            }
        }
    }

    public void Dispose()
    {
        var session = activeSessionId;
        if (session is not null)
        {
            audio.StopSession(session);
        }
        activeSessionId = null;
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

    private static string NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        null or "" or "dictation" => "dictation",
        "translation" => "translation",
        "ask" => "ask",
        _ => throw new ArgumentException("未知的 Typeless 模式")
    };
}
