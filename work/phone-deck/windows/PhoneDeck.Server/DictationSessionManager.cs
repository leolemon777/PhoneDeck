internal sealed class DictationSessionManager : IDisposable
{
    private readonly object syncRoot = new();
    private readonly PhoneAudioBridge audioBridge;
    private string? activeDictationSessionId;

    internal DictationSessionManager(PhoneAudioBridge audioBridge)
    {
        this.audioBridge = audioBridge;
    }

    internal bool IsActive
    {
        get
        {
            lock (syncRoot)
            {
                return activeDictationSessionId is not null;
            }
        }
    }

    internal string? ActiveSessionId
    {
        get
        {
            lock (syncRoot)
            {
                return activeDictationSessionId;
            }
        }
    }

    internal bool Start(string? sessionId, string? requestId)
    {
        var normalizedSessionId = ValidateSessionId(sessionId);
        var normalizedRequestId = ValidateRequestId(requestId);
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
            if (!audioBridge.IsSessionActive(normalizedSessionId))
            {
                throw new InvalidOperationException("音频会话不存在或已断开");
            }

            var duplicate = KeyboardInput.ExecuteOnce("typeless", null, normalizedRequestId);
            activeDictationSessionId = normalizedSessionId;
            Console.WriteLine($"Typeless 会话已启动：{normalizedSessionId}");
            return duplicate;
        }
    }

    internal bool Stop(string? sessionId, string? requestId)
    {
        var normalizedSessionId = ValidateSessionId(sessionId);
        var normalizedRequestId = ValidateRequestId(requestId);
        bool duplicate;
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

            duplicate = KeyboardInput.ExecuteOnce("typeless", null, normalizedRequestId);
            activeDictationSessionId = null;
            Console.WriteLine($"Typeless 会话已停止：{normalizedSessionId}");
        }
        audioBridge.StopSession(normalizedSessionId);
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

            activeDictationSessionId = null;
            try
            {
                KeyboardInput.Execute("typeless", null);
                Console.WriteLine($"音频断流，已自动复位 Typeless：{sessionId}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"音频断流后复位 Typeless 失败：{exception.Message}");
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
                    KeyboardInput.Execute("typeless", null);
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
