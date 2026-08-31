using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PhoneDeck.MacReceiver;

internal sealed class UsbWatchdog : IDisposable
{
    private const int CheckIntervalMilliseconds = 2_000;
    private static readonly Regex ReverseEntryPattern = new(
        @"tcp:8765\s+tcp:8765", RegexOptions.Compiled);

    private readonly CancellationTokenSource cancellation = new();
    private Thread? worker;

    internal UsbWatchdog(string? configuredAdbPath)
    {
        AdbPath = ResolveAdbPath(configuredAdbPath);
    }

    internal string? AdbPath { get; }
    internal string? LastRestoredAt { get; private set; }
    internal int RestoreCount { get; private set; }
    internal bool Running => worker?.IsAlive == true;

    internal void Start()
    {
        if (worker is not null)
        {
            return;
        }
        if (AdbPath is null)
        {
            Console.WriteLine(
                "USB 看门狗未启用：未找到 adb。可安装 Android Platform Tools，"
                + "或在 server-settings.json 中配置 adbPath。");
            return;
        }
        worker = new Thread(() => Run(cancellation.Token))
        {
            IsBackground = true,
            Name = "PhoneDeckMacUsbWatchdog"
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
                CheckAndRestore();
            }
            catch (Exception)
            {
                // 单轮失败不终止看门狗。
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
        if (RunAdb(["get-state"], out _)?.Trim() != "device")
        {
            return;
        }
        var reverseList = RunAdb(["reverse", "--list"], out _) ?? string.Empty;
        if (ReverseEntryPattern.IsMatch(reverseList))
        {
            return;
        }
        if (RunAdb(["reverse", "tcp:8765", "tcp:8765"], out _) is null)
        {
            return;
        }
        _ = RunAdb(
            ["shell", "am", "start", "-n", "com.codex.phonedeck/.MainActivity"],
            out _);
        RestoreCount++;
        LastRestoredAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Console.WriteLine($"USB 看门狗已恢复 adb reverse（第 {RestoreCount} 次）");
    }

    private string? RunAdb(IReadOnlyList<string> arguments, out string errors)
    {
        errors = string.Empty;
        if (AdbPath is null)
        {
            return null;
        }
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = AdbPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(4_000))
            {
                try
                {
                    process.Kill(true);
                }
                catch (Exception)
                {
                    // 进程可能刚好退出。
                }
                errors = "adb 超时";
                return null;
            }
            Task.WaitAll([outputTask, errorTask], 1_000);
            errors = errorTask.IsCompletedSuccessfully ? errorTask.Result : string.Empty;
            return process.ExitCode == 0 && outputTask.IsCompletedSuccessfully
                ? outputTask.Result
                : null;
        }
        catch (Exception exception)
        {
            errors = exception.Message;
            return null;
        }
    }

    internal static string? ResolveAdbPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath))
            {
                return Path.GetFullPath(configuredPath);
            }
            Console.WriteLine($"配置的 adbPath 不存在：{configuredPath}，改为自动查找。");
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb");
        if (File.Exists(bundled))
        {
            return bundled;
        }
        var commonPaths = new[]
        {
            "/opt/homebrew/bin/adb",
            "/usr/local/bin/adb"
        };
        foreach (var candidate in commonPaths)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(
                     Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "adb");
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
        cancellation.Cancel();
        worker?.Join(3_000);
        cancellation.Dispose();
    }
}
