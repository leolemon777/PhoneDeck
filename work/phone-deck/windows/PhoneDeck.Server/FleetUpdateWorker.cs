using System.Diagnostics;
using System.Text.Json;

internal static class FleetUpdateWorker
{
    internal static async Task<int> Run(string[] args)
    {
        if (args.Length != 5) return 2;
        var install = Path.GetFullPath(args[1]);
        var data = Path.GetFullPath(args[2]);
        var root = Path.Combine(data, "updates");
        using var updaterLock = new Mutex(true, "PhoneDeck.FleetUpdate.Worker", out var ownsLock);
        if (!ownsLock) return 3;
        var stage = Path.Combine(root, "verified");
        var backup = Path.Combine(root, "backup");
        var transaction = new UpdateFileTransaction(install, stage, backup);
        var consoleWasRunning = false;
        Process? replacement = null;
        void Status(string state, string detail) => FleetUpdates.WriteState(root, state, detail);
        try
        {
            var manifest = UpdatePackage.Verify(Path.Combine(root, "bundle.zip"), stage);
            // A PID alone may have been reused while the helper was starting.
            try
            {
                using var parent = Process.GetProcessById(int.Parse(args[3]));
                if (parent.StartTime.ToUniversalTime().Ticks != long.Parse(args[4])) throw new IOException("原进程身份变化");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await parent.WaitForExitAsync(deadline.Token);
            }
            catch (ArgumentException) { /* Parent already exited. */ }
            Directory.CreateDirectory(backup);
            foreach (var process in Process.GetProcessesByName("PhoneDeck.ControlCenter"))
            {
                using (process)
                {
                    if (!string.Equals(process.MainModule?.FileName, Path.Combine(install, "PhoneDeck.ControlCenter.exe"), StringComparison.OrdinalIgnoreCase)) continue;
                    consoleWasRunning = true;
                    process.Kill();
                    await process.WaitForExitAsync();
                }
            }
            transaction.Apply();
            replacement = Start(Path.Combine(install, "PhoneDeck.Server.exe"), install, data);
            if (!await Healthy(manifest, File.ReadAllText(Path.Combine(data, "computer-id.txt")).Trim()))
                throw new IOException("新接收端未通过版本与设备身份检查");
            File.WriteAllText(Path.Combine(root, "installed-sequence.txt"), manifest.Sequence.ToString());
            Status("completed", "更新成功，配置与配对资料已保留");
            if (consoleWasRunning) Start(Path.Combine(install, "PhoneDeck.ControlCenter.exe"), install, data)?.Dispose();
            return 0;
        }
        catch (Exception exception)
        {
            try
            {
                if (replacement is { HasExited: false }) { replacement.Kill(); await replacement.WaitForExitAsync(); }
                transaction.Rollback();
                Status("failed", "更新失败，已恢复原文件：" + exception.Message);
                Start(Path.Combine(install, "PhoneDeck.Server.exe"), install, data)?.Dispose();
                if (consoleWasRunning) Start(Path.Combine(install, "PhoneDeck.ControlCenter.exe"), install, data)?.Dispose();
            }
            catch (Exception rollback) { Status("recovery-required", "需要修复：" + rollback.Message); }
            return 1;
        }
        finally { replacement?.Dispose(); File.Delete(Path.Combine(root, "installing")); }
    }

    private static Process? Start(string path, string install, string data)
    {
        var info = new ProcessStartInfo(path) { WorkingDirectory = install, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        info.Environment[PhoneDeckDataDirectory.EnvironmentVariable] = data;
        return Process.Start(info);
    }

    private static async Task<bool> Healthy(UpdateManifest manifest, string id)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var health = JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:8765/api/health"));
                if (health.RootElement.GetProperty("version").GetString() == manifest.WindowsVersion
                    && health.RootElement.GetProperty("updates").GetProperty("sequence").GetInt64() == manifest.Sequence
                    && health.RootElement.GetProperty("computerId").GetString() == id) return true;
            }
            catch (Exception) { }
            await Task.Delay(1000);
        }
        return false;
    }
}
