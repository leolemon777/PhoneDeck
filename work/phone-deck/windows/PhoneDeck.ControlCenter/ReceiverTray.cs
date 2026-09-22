using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace PhoneDeck.ControlCenter;

// No settings pages, graphics engine, or hidden window tree stays resident.
internal sealed class ReceiverTray : ApplicationContext
{
    private readonly Control dispatcher = new();
    private readonly NotifyIcon tray;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2500 };
    private readonly RegisteredWaitHandle activation;
    private ReceiverStatusWindow? window;
    private bool refreshing, reconnecting, closing;
    private static string ServerPath => Path.Combine(AppContext.BaseDirectory, "PhoneDeck.Server.exe");
    private static string DataDirectory => Environment.GetEnvironmentVariable("PHONEDECK_DATA_DIR") is string path
        && !string.IsNullOrWhiteSpace(path) ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()))
        : Path.Combine(AppContext.BaseDirectory, "data");

    internal ReceiverTray(bool hidden, EventWaitHandle activate)
    {
        _ = dispatcher.Handle;
        using var resource = typeof(ReceiverTray).Assembly.GetManifestResourceStream("PhoneDeck.ControlCenter.Assets.PhoneDeck.Light.ico");
        tray = new NotifyIcon { Icon = resource is null ? SystemIcons.Application : new Icon(resource),
            Text = "PhoneDeck · 在手机 App 中设置", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        tray.ContextMenuStrip.Items.Add("查看连接状态", null, (_, _) => ShowWindow());
        tray.ContextMenuStrip.Items.Add("重新连接", null, async (_, _) => await Reconnect());
        tray.ContextMenuStrip.Items.Add("退出并停止接收", null, (_, _) => StopAndExit());
        tray.DoubleClick += (_, _) => ShowWindow();
        timer.Tick += async (_, _) => await Refresh();
        activation = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) =>
        {
            try { if (!closing) dispatcher.BeginInvoke((Action)ShowWindow); }
            catch (InvalidOperationException) when (closing) { }
        }, null, Timeout.Infinite, false);
        dispatcher.BeginInvoke((Action)(async () => { if (!hidden) ShowWindow(); await Reconnect(); }));
    }

    private void ShowWindow()
    {
        if (closing) return;
        if (window != null) { window.WindowState = FormWindowState.Normal; window.Show(); window.Activate(); return; }
        window = new ReceiverStatusWindow(tray.Icon, Reconnect);
        window.Resize += (_, _) => { if (window?.WindowState == FormWindowState.Minimized) timer.Stop(); else timer.Start(); };
        window.FormClosed += (_, _) => { timer.Stop(); window = null; };
        window.Show(); timer.Start(); _ = Refresh();
    }

    private async Task<bool> Refresh()
    {
        if (refreshing || closing) return false;
        refreshing = true;
        try
        {
            using var response = await http.GetAsync("http://127.0.0.1:8765/api/health");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (closing) return false;
            var root = doc.RootElement;
            var audio = root.GetProperty("audio"); var engine = root.GetProperty("voiceEngine");
            var name = root.GetProperty("displayName").GetString();
            var streaming = audio.GetProperty("streaming").GetBoolean();
            window?.UpdateStatus(name ?? Environment.MachineName,
                engine.GetProperty("displayName").GetString() ?? "正在读取", streaming,
                audio.GetProperty("available").GetBoolean(), root.GetProperty("version").GetString() ?? "—");
            tray.Text = streaming ? "PhoneDeck · 正在接收音频" : "PhoneDeck · 接收器就绪";
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or ObjectDisposedException)
        { SetStatus("接收器暂未响应\n点击重新连接，或从手机检查连接。"); return false; }
        finally { refreshing = false; }
    }
    private void SetStatus(string text) { window?.ShowNotice("连接待恢复", text); }
    private async Task Reconnect()
    {
        if (closing || reconnecting) return;
        if (File.Exists(Path.Combine(DataDirectory, "updates", "installing")))
        { SetStatus("正在安装更新，请稍候…"); return; }
        reconnecting = true; window?.SetBusy(true);
        try
        {
            // Do not kill a healthy or temporarily busy receiver on a timeout.
            var existing = ServerProcesses();
            var running = existing.Count > 0;
            foreach (var process in existing) process.Dispose();
            if (!running)
            {
                if (!File.Exists(ServerPath)) { SetStatus("未找到同目录的 PhoneDeck.Server.exe，请重新安装接收器。"); return; }
                var start = new ProcessStartInfo(ServerPath) { UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory };
                start.Environment["PHONEDECK_DATA_DIR"] = DataDirectory;
                using var process = Process.Start(start);
                await Task.Delay(700);
            }
            await Refresh();
        }
        catch (Exception e) { SetStatus("重新连接失败：" + e.Message); }
        finally { reconnecting = false; window?.SetBusy(false); }
    }
    private static List<Process> ServerProcesses()
    {
        var result = new List<Process>();
        foreach (var process in Process.GetProcessesByName("PhoneDeck.Server"))
        {
            try { if (string.Equals(process.MainModule?.FileName, ServerPath, StringComparison.OrdinalIgnoreCase)) { result.Add(process); continue; } }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
        return result;
    }
    private void StopAndExit()
    {
        if (MessageBox.Show("退出后手机将无法向这台电脑输入，正在进行的语音也会停止。", "退出 PhoneDeck", MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question) != DialogResult.OK) return;
        foreach (var process in ServerProcesses())
        { using (process) { try { process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } } }
        ExitThread();
    }
    protected override void ExitThreadCore() { closing = true; tray.Visible = false; base.ExitThreadCore(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { closing = true; activation.Unregister(null); timer.Dispose(); window?.Dispose(); tray.Icon?.Dispose(); tray.Dispose(); http.Dispose(); dispatcher.Dispose(); }
        base.Dispose(disposing);
    }
}
