internal sealed class DictationSessionManager : IDisposable
{
    private const int AudioSessionReadyTimeoutMilliseconds = 1_200;
    private const int TypelessStateTimeoutMilliseconds = 1_200;
    private readonly object syncRoot = new();
    private readonly IPhoneAudioSessionController audioBridge;
    private readonly ITypelessController typeless;
    private string? activeDictationSessionId;

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
            // Android 在取得音频 POST 的输出流后会立即并发发送 start。
            // Kestrel/WASAPI 可能还需要几十毫秒才登记 activeSessionId，
            // 因此等待真实会话就绪，而不是把正常竞态误报成 USB 断线。
            if (!audioBridge.WaitForSessionActive(
                    normalizedSessionId, AudioSessionReadyTimeoutMilliseconds))
            {
                throw new InvalidOperationException("音频会话不存在或已断开");
            }
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

            var duplicate = typeless.ToggleOnce(normalizedRequestId);
            activeDictationSessionId = normalizedSessionId;
            bool? started;
            try
            {
                started = typeless.WaitForCapturing(
                    expected: true, TypelessStateTimeoutMilliseconds);
            }
            catch (Exception exception)
            {
                ResetFailedStart(normalizedSessionId);
                throw new InvalidOperationException(
                    "读取 Typeless 启动状态失败", exception);
            }
            if (started is not true)
            {
                ResetFailedStart(normalizedSessionId);
                throw new InvalidOperationException(started is null
                    ? "无法确认 Typeless 是否开始听写"
                    : "Typeless 未确认开始听写");
            }
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
        Console.WriteLine($"Typeless 会话已停止：{normalizedSessionId}");
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
            : typeless.ToggleOnce(requestId);
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
        typeless.Toggle();
        stopped = typeless.WaitForCapturing(
            expected: false, TypelessStateTimeoutMilliseconds);
        return stopped is true;
    }

    private bool ToggleWithoutRequestId()
    {
        typeless.Toggle();
        return false;
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
