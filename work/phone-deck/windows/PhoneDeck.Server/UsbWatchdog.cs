using System.Diagnostics;
using System.Text.RegularExpressions;

internal sealed class UsbWatchdog : IDisposable
{
    private const int CheckIntervalMilliseconds = 2_000;
    private const string MutexName = @"Local\PhoneDeckUsbReconnect";
    private static readonly Regex ReverseEntryPattern = new(
        @"tcp:8765\s+tcp:8765", RegexOptions.Compiled);

    private readonly CancellationTokenSource cancellation = new();
    private readonly Mutex mutex;
    private readonly bool ownsMutex;
    private Thread? worker;
    private volatile bool enabled = true;
    internal void SetEnabled(bool value) { enabled = value; if (value) Start(); }

    internal UsbWatchdog(string? configuredAdbPath)
    {
        mutex = new Mutex(true, MutexName, out bool createdNew);
        ownsMutex = createdNew;
        AdbPath = ResolveAdbPath(configuredAdbPath);
    }

    internal string? AdbPath { get; }
    internal string? LastRestoredAt { get; private set; }
    internal int RestoreCount { get; private set; }

    internal bool Running => enabled && worker?.IsAlive == true;

    internal void Start()
    {
        if (worker is not null)
        {
            return;
        }
        if (AdbPath is null)
        {
            Console.WriteLine(
                "USB 看门狗未启用：未找到 adb.exe。"
                + "可在 PhoneDeck 数据目录的 server-settings.json 配置 adbPath，"
                + "或把 platform-tools 放到程序目录旁。");
            return;
        }
        if (!ownsMutex)
        {
            Console.WriteLine("USB 看门狗未启用：已有另一个看门狗在运行。");
            return;
        }
        worker = new Thread(() => Run(cancellation.Token))
        {
            IsBackground = true,
            Name = "PhoneDeckUsbWatchdog"
        };
        worker.Start();
        Console.WriteLine($"USB 看门狗已启动（adb：{AdbPath}）");
    }

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (enabled) CheckAndRestore();
            }
            catch (Exception exception)
            {
                // 看门狗自身不能崩溃；单轮失败静默等下一轮。
                _ = exception;
            }
            try
            {
                Task.Delay(CheckIntervalMilliseconds, token).Wait(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void CheckAndRestore()
    {
        if (RunAdb("get-state", out var stateOutput, out _)?.Trim() != "device")
        {
            return;
        }
        var reverseList = RunAdb("reverse --list", out var listOutput, out _) ?? string.Empty;
        if (ReverseEntryPattern.IsMatch(listOutput))
        {
            return;
        }
        if (RunAdb("reverse tcp:8765 tcp:8765", out _, out _) is null)
        {
            return;
        }
        RunAdb("shell am start -n com.codex.phonedeck/.MainActivity", out _, out _);
        RestoreCount++;
        LastRestoredAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Console.WriteLine($"USB 看门狗已恢复 adb reverse（第 {RestoreCount} 次，{LastRestoredAt}）");
    }

    private string? RunAdb(string arguments, out string output, out string errors)
    {
        if (AdbPath is null)
        {
            output = string.Empty;
            errors = string.Empty;
            return null;
        }
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = AdbPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            process.Start();
            var finished = process.WaitForExit(4_000);
            if (!finished)
            {
                try
                {
                    process.Kill(true);
                }
                catch (Exception)
                {
                    // 进程可能刚好已退出。
                }
                output = string.Empty;
                errors = "adb 超时";
                return null;
            }
            output = process.StandardOutput.ReadToEnd();
            errors = process.StandardError.ReadToEnd();
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception exception)
        {
            output = string.Empty;
            errors = exception.Message;
            return null;
        }
    }

    private static string? ResolveAdbPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath))
            {
                return configuredPath;
            }
            Console.WriteLine(
                $"server-settings.json 配置的 adbPath 不存在：{configuredPath}，改为自动查找。");
        }
        var bundled = Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "adb.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // PATH 中的非法目录直接跳过。
            }
        }
        return null;
    }

    public void Dispose()
    {
        try
        {
            cancellation.Cancel();
            worker?.Join(3_000);
        }
        finally
        {
            cancellation.Dispose();
            if (ownsMutex)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (Exception)
                {
                    // 同步块可能已由系统释放。
                }
            }
            mutex.Dispose();
        }
    }
}
