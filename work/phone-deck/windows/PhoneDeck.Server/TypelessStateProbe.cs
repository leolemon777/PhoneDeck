using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

internal static class TypelessStateProbe
{
    internal static bool? IsCapturing()
    {
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
                            if (process.ProcessName.Equals(
                                    "Typeless", StringComparison.OrdinalIgnoreCase)
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
            Console.Error.WriteLine($"Typeless 录音状态检测失败：{exception.Message}");
            return null;
        }
    }

    internal static bool? WaitForCapturing(bool expected, int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        var observed = false;
        do
        {
            var capturing = IsCapturing();
            if (capturing.HasValue)
            {
                observed = true;
                if (capturing.Value == expected)
                {
                    return true;
                }
            }
            Thread.Sleep(60);
        }
        while (Environment.TickCount64 < deadline);
        return observed ? false : null;
    }
}
