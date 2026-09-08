using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

/// <summary>语音引擎真实录音状态探针：枚举所有采集端点的音频会话，
/// 按引擎档案的进程名列表匹配（忽略大小写，相等或包含即命中），
/// 判断引擎进程是否真的在录音。带 250ms 熔断，避免卡死时拖垮健康检查。</summary>
internal static class VoiceEngineStateProbe
{
    private const int MaxProbeDurationMs = 250;

    internal static bool? IsCapturing(VoiceEngineProfile profile)
    {
        var startTick = Environment.TickCount64;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .ToList();
            try
            {
                foreach (var device in devices)
                {
                    if (Environment.TickCount64 - startTick > MaxProbeDurationMs)
                    {
                        break;
                    }
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    for (var index = 0; index < manager.Sessions.Count; index++)
                    {
                        using var session = manager.Sessions[index];
                        var processId = session.GetProcessID;
                        if (processId == 0)
                        {
                            continue;
                        }
                        try
                        {
                            using var process = Process.GetProcessById((int)processId);
                            if (MatchesProcess(profile, process.ProcessName)
                                && session.State == AudioSessionState.AudioSessionStateActive)
                            {
                                return true;
                            }
                        }
                        catch (ArgumentException)
                        {
                            // 音频会话枚举后进程可能已退出。
                        }
                        catch (InvalidOperationException)
                        {
                            // 进程在读取名称时可能已退出。
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
        catch (Exception exception)
        {
            Console.Error.WriteLine($"语音引擎录音状态检测失败：{exception.Message}");
            return null;
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
