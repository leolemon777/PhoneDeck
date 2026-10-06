using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PhoneDeck.ControlCenter;

// 常驻的只有托盘图标和一个 2 秒的回环检查；状态窗口、连接确认窗口按需创建、关闭即释放。
internal sealed class ReceiverTray : ApplicationContext
{
    private const string Local = "http://127.0.0.1:8765/";
    private readonly Control dispatcher = new();
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem header, autoStart;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2500 };
    // 同一 Wi-Fi 的手机点「连接」时弹出本机确认；只读回环状态，不常驻窗口。
    private readonly System.Windows.Forms.Timer nearbyTimer = new() { Interval = 2000 };
    private readonly RegisteredWaitHandle activation;
    private ReceiverStatusWindow? window;
    private ConnectRequestForm? request;
    private string? answeredPairingId;
    private bool refreshing, reconnecting, closing, checkingNearby;
    private static string ServerPath => Path.Combine(AppContext.BaseDirectory, "PhoneDeck.Server.exe");
    private static string DataDirectory => Environment.GetEnvironmentVariable("PHONEDECK_DATA_DIR") is string path
        && !string.IsNullOrWhiteSpace(path) ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()))
        : Path.Combine(AppContext.BaseDirectory, "data");

    internal ReceiverTray(bool hidden, EventWaitHandle activate)
    {
        _ = dispatcher.Handle;
        using var resource = typeof(ReceiverTray).Assembly.GetManifestResourceStream("PhoneDeck.ControlCenter.Assets.PhoneDeck.Light.ico");
        tray = new NotifyIcon { Icon = resource is null ? SystemIcons.Application : new Icon(resource),
            Text = "言渡 · 电脑接收端", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        var menu = tray.ContextMenuStrip;
        header = new ToolStripMenuItem("言渡 · 正在检查…") { Enabled = false };
        autoStart = new ToolStripMenuItem("开机启动") { CheckOnClick = false };
        autoStart.Click += async (_, _) => await ToggleAutoStart();
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开状态窗口", null, (_, _) => ShowWindow());
        menu.Items.Add(autoStart);
        menu.Items.Add("重新连接", null, async (_, _) => await Reconnect());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出并停止接收", null, (_, _) => StopAndExit());
        // 菜单打开时才读一次状态，平时不为菜单额外轮询。
        menu.Opening += async (_, _) => await RefreshMenu();
        tray.DoubleClick += (_, _) => ShowWindow();
        timer.Tick += async (_, _) => await Refresh();
        nearbyTimer.Tick += async (_, _) => await CheckNearbyRequest();
        nearbyTimer.Start();
        activation = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) =>
        {
            try { if (!closing) dispatcher.BeginInvoke((Action)ShowWindow); }
            catch (InvalidOperationException) when (closing) { }
        }, null, Timeout.Infinite, false);
        dispatcher.BeginInvoke((Action)(async () => { if (!hidden) ShowWindow(); await Reconnect(); }));
    }

    private async Task<JsonDocument?> GetJson(string path)
    {
        try
        {
            using var response = await http.GetAsync(Local + path);
            if (!response.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or ObjectDisposedException)
        {
            return null;
        }
    }

    private async Task<bool> PostJson(string path, string json)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(Local + path, content);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task CheckNearbyRequest()
    {
        if (closing || checkingNearby) return;
        checkingNearby = true;
        try
        {
            using var doc = await GetJson("api/admin/pairing/status");
            var root = doc?.RootElement;
            string? pairingId = null, label = null, code = null;
            var seconds = 60;
            if (root is { } r && r.TryGetProperty("nearby", out var nearby) && nearby.ValueKind == JsonValueKind.True
                && r.TryGetProperty("pending", out var pending) && pending.ValueKind == JsonValueKind.Object)
            {
                pairingId = r.GetProperty("pairingId").GetString();
                label = pending.GetProperty("clientLabel").GetString();
                code = r.GetProperty("checkCode").GetString();
                if (r.TryGetProperty("remainingSeconds", out var left) && left.TryGetInt32(out var value)) seconds = value;
            }
            // 请求已在状态页确认、超时或被别的手机替换：关掉旧窗口。
            if (request is { } open && open.PairingId != pairingId) { request = null; open.Close(); }
            if (pairingId is null || code is null || request != null || pairingId == answeredPairingId) return;
            var form = new ConnectRequestForm(tray.Icon, pairingId, string.IsNullOrWhiteSpace(label) ? "一台手机" : label!, code, seconds);
            form.Decided += async allow =>
            {
                answeredPairingId = form.PairingId;
                await PostJson("api/admin/pairing/" + (allow ? "confirm" : "deny"),
                    JsonSerializer.Serialize(new Dictionary<string, string> { ["pairingId"] = form.PairingId }));
                if (window != null) await Refresh();
            };
            // 关窗（✕ 或倒计时）= 不处理，交给服务端超时拒绝，同一请求不再弹出。
            form.FormClosed += (_, _) => { answeredPairingId = form.PairingId; if (ReferenceEquals(request, form)) request = null; form.Dispose(); };
            request = form;
            form.Show();
            form.Activate();
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException) { }
        finally { checkingNearby = false; }
    }

    private void ShowWindow()
    {
        if (closing) return;
        if (window != null) { window.WindowState = FormWindowState.Normal; window.Show(); window.Activate(); return; }
        window = new ReceiverStatusWindow(tray.Icon, Reconnect, Revoke);
        window.Resize += (_, _) => { if (window?.WindowState == FormWindowState.Minimized) timer.Stop(); else timer.Start(); };
        window.FormClosed += (_, _) => { timer.Stop(); window = null; };
        window.Show(); timer.Start(); _ = Refresh();
    }

    private async Task Revoke(PhoneEntry phone)
    {
        var ok = phone.Legacy
            ? await PostJson("api/admin/legacy/revoke", "{}")
            : await PostJson("api/admin/clients/revoke",
                JsonSerializer.Serialize(new Dictionary<string, string> { ["clientId"] = phone.ClientId }));
        if (!ok) window?.ShowNotice("没能撤销", "接收端没有响应，请稍后重试。");
        await Refresh();
    }

    private async Task<bool> Refresh()
    {
        if (refreshing || closing) return false;
        refreshing = true;
        try
        {
            using var health = await GetJson("api/health");
            if (health is null || closing)
            {
                SetStatus("接收端暂未响应\n点击重新连接，或从手机检查连接。");
                return false;
            }
            using var presence = await GetJson("api/admin/presence");
            using var clients = await GetJson("api/admin/clients");
            using var config = await GetJson("api/config/desktop");
            var status = BuildStatus(health.RootElement, presence?.RootElement, clients?.RootElement, config?.RootElement);
            window?.UpdateStatus(status);
            tray.Text = status.Streaming ? "言渡 · 正在接收声音" : status.ActivePhones.Count > 0 ? "言渡 · 接收中" : "言渡 · 等待手机";
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or JsonException)
        { SetStatus("接收端返回的数据无法读取，请重新连接。"); return false; }
        finally { refreshing = false; }
    }

    private static ReceiverStatus BuildStatus(JsonElement health, JsonElement? presence, JsonElement? clients, JsonElement? config)
    {
        static string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        static bool? Bool(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

        var audio = health.GetProperty("audio");
        var engine = health.TryGetProperty("voiceEngine", out var ve) && ve.ValueKind == JsonValueKind.Object ? ve : default;
        var active = new List<string>();
        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        if (presence is { } p && p.TryGetProperty("phones", out var list))
        {
            foreach (var phone in list.EnumerateArray())
            {
                if (Bool(phone, "active") != true) continue;
                var name = Str(phone, "label") ?? Str(phone, "device") ?? "手机";
                active.Add(name + " · " + (Str(phone, "transport") == "wifi" ? "Wi-Fi" : "USB"));
                if (Str(phone, "clientId") is { } id) activeIds.Add(id);
            }
        }
        var phones = new List<PhoneEntry>();
        if (clients is { } c && c.TryGetProperty("clients", out var records))
        {
            foreach (var record in records.EnumerateArray())
            {
                if (Str(record, "revokedAtUtc") is not null) continue;
                var id = Str(record, "clientId") ?? "";
                if (Bool(record, "legacy") == true)
                {
                    phones.Add(new PhoneEntry(id, "旧版共享令牌", "老版本手机或 USB 首次配对在用；全部升级后可关闭", true));
                    continue;
                }
                var issued = (Str(record, "issuedAtUtc") ?? "").Replace('T', ' ');
                phones.Add(new PhoneEntry(id, Str(record, "label") ?? id,
                    (activeIds.Contains(id) ? "正在连接 · " : "") + "允许于 " + (issued.Length > 16 ? issued[..16] : issued), false));
            }
        }
        var connection = config is { } cfg && cfg.TryGetProperty("connection", out var conn) ? conn : default;
        return new ReceiverStatus(
            Str(health, "displayName") ?? Environment.MachineName,
            Str(health, "version") ?? "—",
            Bool(audio, "streaming") == true,
            Bool(audio, "available") == true,
            Str(audio, "device"),
            engine.ValueKind == JsonValueKind.Object ? Str(engine, "displayName") ?? "语音输入法" : "语音输入法",
            engine.ValueKind != JsonValueKind.Object || Bool(engine, "virtualCableSelected") != false,
            engine.ValueKind == JsonValueKind.Object ? Str(engine, "microphone") : null,
            connection.ValueKind == JsonValueKind.Object ? Bool(connection, "lanDiscovery") : null,
            connection.ValueKind == JsonValueKind.Object ? Bool(connection, "autoStart") : null,
            active, phones);
    }

    private async Task RefreshMenu()
    {
        using var health = await GetJson("api/health");
        if (health is null) { header.Text = "言渡 · 接收端未响应"; autoStart.Enabled = false; return; }
        using var presence = await GetJson("api/admin/presence");
        using var config = await GetJson("api/config/desktop");
        var status = BuildStatus(health.RootElement, presence?.RootElement, null, config?.RootElement);
        header.Text = status.ActivePhones.Count > 0
            ? "言渡 · " + (status.Streaming ? "正在接收声音" : "接收中") + " · " + status.ActivePhones[0]
            : "言渡 · 等待手机连接";
        autoStart.Enabled = status.AutoStart is not null;
        autoStart.Checked = status.AutoStart == true;
    }

    /// <summary>托盘直接开关开机启动：读取当前修订号后提交（与手机「多电脑配置」同一接口）。</summary>
    private async Task ToggleAutoStart()
    {
        using var config = await GetJson("api/config/desktop");
        if (config is null) return;
        var root = config.RootElement;
        var target = root.GetProperty("computerId").GetString() ?? "";
        var revision = root.GetProperty("connectionRevision").GetString() ?? "";
        var current = root.GetProperty("connection").GetProperty("autoStart").GetBoolean();
        var body = $"{{\"targetComputerId\":{JsonSerializer.Serialize(target)},\"revision\":{JsonSerializer.Serialize(revision)},\"autoStart\":{(!current ? "true" : "false")}}}";
        if (!await PostJson("api/config/desktop/connection", body))
            MessageBox.Show("没能修改开机启动。正在输入或供音时不能保存，请稍后再试。", "言渡", MessageBoxButtons.OK, MessageBoxIcon.Information);
        if (window != null) await Refresh();
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
                if (!File.Exists(ServerPath)) { SetStatus("未找到同目录的 PhoneDeck.Server.exe，请重新安装接收端。"); return; }
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
        if (MessageBox.Show("退出后手机将无法向这台电脑输入，正在进行的语音也会停止。", "退出言渡", MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question) != DialogResult.OK) return;
        foreach (var process in ServerProcesses())
        { using (process) { try { process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } } }
        ExitThread();
    }

    protected override void ExitThreadCore() { closing = true; tray.Visible = false; base.ExitThreadCore(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing = true; activation.Unregister(null); timer.Dispose(); nearbyTimer.Dispose();
            request?.Dispose(); window?.Dispose(); tray.Icon?.Dispose(); tray.Dispose(); http.Dispose(); dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
