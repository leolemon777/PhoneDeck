using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

/// <summary>语音引擎真实录音状态探针：枚举所有采集端点的音频会话，
/// 按引擎档案的进程名列表匹配（忽略大小写，相等或包含即命中），
/// 判断引擎进程是否真的在录音。带 250ms 协作预算；同步 COM 调用无法被
/// 强制取消，超时完成后会标记为未知，不会把未知误报为空闲。</summary>
internal static class VoiceEngineStateProbe
{
    private const int MaxProbeDurationMs = 250;

    internal static bool? IsCapturing(VoiceEngineProfile profile)
    {
        return RunWithBudget(
            startTick => ScanCaptureSessions(profile, startTick,
                () => Environment.TickCount64),
            () => Environment.TickCount64,
            MaxProbeDurationMs,
            exception => Console.Error.WriteLine(
                $"语音引擎录音状态检测失败：{exception.Message}"));
    }

    /// <summary>
    /// Runs one probe under a monotonic time budget. A result produced after the
    /// budget is unknown: a slow Core Audio call cannot prove that the engine is
    /// idle. The delegates are injectable so the timeout classification is
    /// testable without requiring a real audio endpoint.
    /// </summary>
    internal static bool? RunWithBudget(
        Func<long, bool?> scan,
        Func<long> clock,
        int budgetMilliseconds,
        Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(clock);
        if (budgetMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budgetMilliseconds));
        }

        var startTick = clock();
        try
        {
            var result = scan(startTick);
            return clock() - startTick > budgetMilliseconds ? null : result;
        }
        catch (Exception exception)
        {
            try
            {
                onError?.Invoke(exception);
            }
            catch
            {
                // Logging must not turn an inconclusive probe into a health
                // check failure. The probe result remains unknown.
            }
            return null;
        }
    }

    private static bool? ScanCaptureSessions(
        VoiceEngineProfile profile,
        long startTick,
        Func<long> clock)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .ToList();
        try
        {
            foreach (var device in devices)
            {
                // The budget is a circuit breaker, not a positive "not
                // recording" result. COM endpoint/session enumeration can
                // exceed the budget while the engine is still recording.
                if (clock() - startTick > MaxProbeDurationMs)
                {
                    return null;
                }
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                for (var index = 0; index < manager.Sessions.Count; index++)
                {
                    if (clock() - startTick > MaxProbeDurationMs)
                    {
                        return null;
                    }
                    using var session = manager.Sessions[index];
                    var processId = session.GetProcessID;
                    if (processId == 0)
                    {
                        continue;
                    }
                    try
                    {
                        using var process = Process.GetProcessById((int)processId);
                        if (clock() - startTick > MaxProbeDurationMs)
                        {
                            return null;
                        }
                        string processName;
                        try
                        {
                            processName = process.ProcessName;
                        }
                        catch (InvalidOperationException)
                        {
                            // The process can exit between PID lookup and
                            // reading its name; skip this stale session.
                            continue;
                        }
                        var isActive = session.State == AudioSessionState.AudioSessionStateActive;
                        if (clock() - startTick > MaxProbeDurationMs)
                        {
                            return null;
                        }
                        if (MatchesProcess(profile, processName) && isActive)
                        {
                            return true;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // 音频会话枚举后进程可能已退出。
                    }
                }
            }
            return false;
        }
        finally
        {
            foreach (var device in devices)
            {
                device.Dispose();
            }
        }
    }

    private static bool MatchesProcess(VoiceEngineProfile profile, string processName) =>
        profile.ProcessNames.Any(name =>
            processName.Equals(name, StringComparison.OrdinalIgnoreCase)
            || processName.Contains(name, StringComparison.OrdinalIgnoreCase));

    internal static bool? WaitForCapturing(
        VoiceEngineProfile profile, bool expected, int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        var observed = false;
        do
        {
            var capturing = IsCapturing(profile);
            if (capturing.HasValue)
            {
                observed = true;
                if (capturing.Value == expected)
                {
                    return true;
                }
            }
            Thread.Sleep(50);
        }
        while (Environment.TickCount64 < deadline);
        return observed ? false : null;
    }
}
