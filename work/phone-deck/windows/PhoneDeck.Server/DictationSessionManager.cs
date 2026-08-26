internal sealed class DictationSessionManager : IDisposable
{
    private const int TypelessMinimumToggleGapMilliseconds = 400;
    private const int TypelessStateTimeoutMilliseconds = 1_200;
    private readonly object syncRoot = new();
    private readonly PhoneAudioBridge audioBridge;
    private string? activeDictationSessionId;
    private long lastTypelessToggleMilliseconds;

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
            if (!KeyboardInput.TypelessUsesVirtualCable)
            {
                throw new InvalidOperationException(
                    "Typeless 麦克风未选择 CABLE Output，已拒绝启动");
            }
            if (TypelessStateProbe.IsCapturing() is true)
            {
                throw new InvalidOperationException(
                    "Typeless 已在听写，请先在电脑端停止后重试");
            }

            var duplicate = ToggleTypelessOnce(normalizedRequestId);
            activeDictationSessionId = normalizedSessionId;
            if (!duplicate
                && TypelessStateProbe.WaitForCapturing(
                    expected: true, TypelessStateTimeoutMilliseconds) is false)
            {
                if (TryStopTypeless(requestId: null, out _))
                {
                    activeDictationSessionId = null;
                }
                throw new InvalidOperationException("Typeless 未确认开始听写");
            }
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

            if (!TryStopTypeless(normalizedRequestId, out duplicate))
            {
                throw new InvalidOperationException("Typeless 仍在听写，停止指令未被确认");
            }
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

            try
            {
                if (TryStopTypeless(requestId: null, out _))
                {
                    activeDictationSessionId = null;
                    Console.WriteLine($"音频断流，已自动复位 Typeless：{sessionId}");
                }
                else
                {
                    Console.Error.WriteLine(
                        $"音频断流，但 Typeless 仍在听写：{sessionId}");
                }
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

    private bool TryStopTypeless(string? requestId, out bool duplicate)
    {
        var capturing = TypelessStateProbe.IsCapturing();
        if (capturing is false)
        {
            duplicate = true;
            return true;
        }

        duplicate = requestId is null
            ? ToggleTypeless()
            : ToggleTypelessOnce(requestId);
        var stopped = TypelessStateProbe.WaitForCapturing(
            expected: false, TypelessStateTimeoutMilliseconds);
        if (stopped is not false)
        {
            return true;
        }

        // SendInput 成功只代表 Windows 接收了按键，不代表 Electron
        // 应用已处理。仅当音频会话仍明确为 Active 时重试一次，
        // 避免已经停止后又被双击切换回开启。
        ToggleTypeless();
        stopped = TypelessStateProbe.WaitForCapturing(
            expected: false, TypelessStateTimeoutMilliseconds);
        return stopped is not false;
    }

    private bool ToggleTypelessOnce(string requestId)
    {
        WaitForTypelessToggleGap();
        var duplicate = KeyboardInput.ExecuteOnce("typeless", null, requestId);
        if (!duplicate)
        {
            lastTypelessToggleMilliseconds = Environment.TickCount64;
        }
        return duplicate;
    }

    private bool ToggleTypeless()
    {
        WaitForTypelessToggleGap();
        KeyboardInput.Execute("typeless", null);
        lastTypelessToggleMilliseconds = Environment.TickCount64;
        return false;
    }

    private void WaitForTypelessToggleGap()
    {
        if (lastTypelessToggleMilliseconds <= 0)
        {
            return;
        }
        var remaining = TypelessMinimumToggleGapMilliseconds
            - (Environment.TickCount64 - lastTypelessToggleMilliseconds);
        if (remaining > 0)
        {
            Thread.Sleep((int)remaining);
        }
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
                    TryStopTypeless(requestId: null, out _);
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
