using System.Diagnostics;
using System.Text.RegularExpressions;

internal sealed class UsbWatchdog : IDisposable
{
    /// <summary>没连上或刚恢复时每 2 秒检查；adb reverse 已在位时放宽到 10 秒（每次只启动一个 adb 进程）。</summary>
    private const int CheckIntervalMilliseconds = 2_000;
    private const int HealthyIntervalMilliseconds = 10_000;
    private volatile bool lastCheckHealthy;
    private const string MutexName = @"Local\PhoneDeckUsbReconnect";
    private static readonly Regex ReverseEntryPattern = new(
        @"tcp:8765\s+tcp:8765", RegexOptions.Compiled);

    private readonly CancellationTokenSource cancellation = new();
    private readonly Mutex mutex;
    private readonly bool ownsMutex;
    private readonly object lifecycleGate = new();
    private readonly AutoResetEvent wakeSignal = new(false);
    private Thread? worker;
    private volatile bool enabled = true;
    private bool disposed;

    internal void SetEnabled(bool value)
    {
        lock (lifecycleGate)
        {
            if (disposed)
            {
                return;
            }
            enabled = value;
            if (!value)
            {
                // Do not let a previous healthy sample delay a later re-enable
                // by the full 10-second healthy interval.
                lastCheckHealthy = false;
            }
        }
        Wake();
        if (value) Start();
    }

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
        lock (lifecycleGate)
        {
            if (disposed || !enabled || worker?.IsAlive == true)
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
            var nextWorker = new Thread(() => Run(cancellation.Token))
            {
                IsBackground = true,
                Name = "PhoneDeckUsbWatchdog"
            };
            worker = nextWorker;
            nextWorker.Start();
            Console.WriteLine($"USB 看门狗已启动（adb：{AdbPath}）");
        }
    }

    private void Run(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (enabled) CheckAndRestore(token);
                }
                catch (Exception exception)
                {
                    // 看门狗自身不能崩溃；单轮失败等下一轮，并保留短检查间隔。
                    lastCheckHealthy = false;
                    Console.WriteLine($"USB 看门狗本轮失败：{exception.Message}");
                }
                try
                {
                    using var cancellationRegistration = token.Register(
                        static state => ((UsbWatchdog)state!).Wake(), this);
                    if (wakeSignal.WaitOne(
                            enabled && lastCheckHealthy
                                ? HealthyIntervalMilliseconds : CheckIntervalMilliseconds))
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            // A failure outside one check (for example a runtime wait failure)
            // must not permanently strand the watchdog as a dead Thread.
            lastCheckHealthy = false;
            Console.WriteLine($"USB 看门狗循环异常：{exception.Message}");
        }
        finally
        {
            bool restart;
            lock (lifecycleGate)
            {
                if (ReferenceEquals(worker, Thread.CurrentThread))
                {
                    worker = null;
                }
                restart = !disposed && enabled && !token.IsCancellationRequested;
            }
            if (restart)
            {
                // Restart only after the current worker has detached. Start's
                // gate prevents a concurrent settings request from creating a
                // second loop.
                Thread.Sleep(250);
                Start();
            }
        }
    }

    private void CheckAndRestore(CancellationToken token)
    {
        lastCheckHealthy = false;
        // 没有设备时 reverse --list 直接失败（返回 null），不必再单独跑 get-state。
        if (RunAdb("reverse --list", token, out var listOutput, out _) is null)
        {
            return;
        }
        if (ReverseEntryPattern.IsMatch(listOutput))
        {
            // Keep the healthy path to one adb reverse query. Process launch
            // is considered only after a missing reverse has been repaired, so
            // the normal 10-second polling path does not add foreground work.
            lastCheckHealthy = true;
            return;
        }
        if (RunAdb("reverse tcp:8765 tcp:8765", token, out _, out _) is null)
        {
            return;
        }
        if (!EnsurePhoneDeckProcess(token))
        {
            Console.WriteLine("USB 看门狗已恢复 reverse，但 PhoneDeck 进程未能启动。");
            return;
        }
        RestoreCount++;
        LastRestoredAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Console.WriteLine($"USB 看门狗已恢复 adb reverse（第 {RestoreCount} 次，{LastRestoredAt}）");
    }

    private bool EnsurePhoneDeckProcess(CancellationToken token)
    {
        var pidResult = RunAdb("shell pidof com.codex.phonedeck", token,
            out var pidOutput, out _);
        if (pidResult is null)
        {
            return RunAdb(
                "shell am start -n com.codex.phonedeck/.MainActivity",
                token, out _, out _) is not null;
        }
        if (!string.IsNullOrWhiteSpace(pidOutput))
        {
            // The process is already running. Do not use am start here: that
            // would repeatedly bring the user's manually opened Activity to
            // the foreground during healthy polling.
            return true;
        }
        return RunAdb(
            "shell am start -n com.codex.phonedeck/.MainActivity",
            token, out _, out _) is not null;
    }

    private string? RunAdb(
        string arguments, CancellationToken token, out string output, out string errors)
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
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(4_000);
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                StopProcess(process);
                output = string.Empty;
                errors = token.IsCancellationRequested ? "adb 已取消" : "adb 超时";
                return null;
            }
            // Read both pipes concurrently. ADB normally writes very little,
            // but sequential reads can deadlock if a diagnostic grows large.
            var pipes = Task.WhenAll(outputTask, errorTask);
            if (!pipes.Wait(1_000))
            {
                StopProcess(process);
                output = string.Empty;
                errors = "adb 输出读取超时";
                return null;
            }
            output = outputTask.GetAwaiter().GetResult();
            errors = errorTask.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception exception)
        {
            output = string.Empty;
            errors = exception.Message;
            return null;
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // The process may have exited between HasExited and Kill.
        }
        try
        {
            process.WaitForExit(1_000);
        }
        catch (Exception)
        {
            // Cleanup is best effort; the caller still returns to the next
            // bounded watchdog round instead of waiting indefinitely.
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
        Thread? thread;
        lock (lifecycleGate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            enabled = false;
            lastCheckHealthy = false;
            thread = worker;
        }
        try
        {
            cancellation.Cancel();
            Wake();
            // RunAdb has a 4-second command budget plus a bounded process
            // cleanup wait. Give the worker enough time to observe cancellation
            // before disposing its token/mutex.
            thread?.Join(6_000);
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
            wakeSignal.Dispose();
        }
    }

    private void Wake()
    {
        try
        {
            wakeSignal.Set();
        }
        catch (ObjectDisposedException)
        {
            // A settings request can race process shutdown; disposal already
            // made the worker ineligible for further work.
        }
    }
}
