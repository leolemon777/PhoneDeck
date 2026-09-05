using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace PhoneDeck.ControlCenter;

public partial class MainWindow : Window
{
    private const string AutoStartValue = "PhoneDeck Control Center";
    private static readonly Uri LightIconUri = new("pack://application:,,,/Assets/PhoneDeck.Light.ico", UriKind.Absolute);
    private static readonly Uri DarkIconUri = new("pack://application:,,,/Assets/PhoneDeck.Dark.ico", UriKind.Absolute);
    private static readonly Uri LightBrandUri = new("pack://application:,,,/Assets/PhoneDeck.Light.png", UriKind.Absolute);
    private static readonly Uri DarkBrandUri = new("pack://application:,,,/Assets/PhoneDeck.Dark.png", UriKind.Absolute);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(2500) };
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly Forms.NotifyIcon trayIcon = new();
    private bool loadingSettings;
    private bool isExiting;
    private string currentComputerId = string.Empty;
    private bool lastSharedRequested;
    private bool applyingSharedLink;
    private HwndSource? sourceHandle;

    private const int WmHotKey = 0x0312;
    private const int SharedHotKeyId = 0x504D;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkM = 0x4D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static string AppDirectory => AppContext.BaseDirectory;
    private static string ServerPath => Path.Combine(AppDirectory, "PhoneDeck.Server.exe");
    private static string DataDirectory =>
        Environment.GetEnvironmentVariable("PHONEDECK_DATA_DIR") is { } configured
            && !string.IsNullOrWhiteSpace(configured)
            ? Environment.ExpandEnvironmentVariables(configured.Trim())
            : Path.Combine(AppDirectory, "data");
    private static string SettingsPath => Path.Combine(DataDirectory, "server-settings.json");
    private static string BundledAdbPath => Path.Combine(AppDirectory, "platform-tools", "adb.exe");

    public MainWindow()
    {
        InitializeComponent();
        ActivityLog.Document = new FlowDocument();
        ActivityLogFull.Document = new FlowDocument();
        DataDirectoryText.Text = "配置与配对数据：" + DataDirectory;
        LoadCachedIdentity();
        LoadSettings();
        InitTrayIcon();
        ApplyThemeAwareIcons();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        refreshTimer.Tick += RefreshTimer_Tick;
        SystemEvents.UserPreferenceChanged += SystemTheme_UserPreferenceChanged;
        SizeChanged += (_, _) => UpdateWindowCorners();
        StateChanged += (_, _) => UpdateWindowCorners();
        UpdateWindowCorners();
    }

    private void UpdateWindowCorners()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowRoot.CornerRadius = new CornerRadius(0);
            WindowRoot.BorderThickness = new Thickness(0);
            WindowRoot.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
            return;
        }

        const double radius = 20;
        WindowRoot.CornerRadius = new CornerRadius(radius);
        WindowRoot.BorderThickness = new Thickness(1);
        WindowRoot.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), radius, radius);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        sourceHandle = HwndSource.FromHwnd(handle);
        sourceHandle?.AddHook(SharedHotkeyHook);
        if (!RegisterHotKey(handle, SharedHotKeyId, ModControl | ModAlt | ModNoRepeat, VkM))
        {
            Log("注册 Ctrl+Alt+M 热键失败：可能被其他程序占用，仍可用界面开关。", ResourceBrush("BrushWarning"));
        }
        FitWindowToWorkArea();
    }

    // 设计尺寸按 ≥900px 高的屏幕绘制；小屏（如 1280x800）上窗口比屏幕高时，
    // 自定义标题栏（含关闭按钮）会被居中定位到屏幕外，导致无法关闭。这里在
    // 建立窗口句柄后把窗口和最小尺寸一起钳制进主屏工作区，并重新居中。
    private void FitWindowToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        const double Margin = 12;
        var maxWidth = Math.Max(480, work.Width - Margin * 2);
        var maxHeight = Math.Max(360, work.Height - Margin * 2);
        if (MinWidth > maxWidth) MinWidth = maxWidth;
        if (MinHeight > maxHeight) MinHeight = maxHeight;
        if (Width > maxWidth) Width = maxWidth;
        if (Height > maxHeight) Height = maxHeight;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }

    private IntPtr SharedHotkeyHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == SharedHotKeyId)
        {
            _ = Dispatcher.InvokeAsync(() => _ = ToggleSharedLinkAsync());
            handled = true;
        }
        return IntPtr.Zero;
    }

    private async Task ToggleSharedLinkAsync() => await SetSharedLinkAsync(!lastSharedRequested, fromHotkey: true);

    private async Task SetSharedLinkAsync(bool requested, bool fromHotkey)
    {
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { requested }), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(
                "http://127.0.0.1:8765/api/shared/request", content);
            if (!response.IsSuccessStatusCode)
            {
                Log("切换共享麦克风联动失败：HTTP " + (int)response.StatusCode,
                    ResourceBrush("BrushDanger"));
                await RefreshStatusAsync();
                return;
            }
            lastSharedRequested = requested;
            Log((requested ? "已请求手机开启共享麦克风" : "已关闭共享麦克风联动")
                    + (fromHotkey ? "（Ctrl+Alt+M）" : string.Empty) + "。",
                requested ? ResourceBrush("BrushSuccess") : ResourceBrush("BrushMist"));
        }
        catch (Exception)
        {
            Log("接收端未运行，无法切换共享麦克风联动。", ResourceBrush("BrushDanger"));
        }
        await RefreshStatusAsync();
    }

    private async void SharedMicLink_Changed(object sender, RoutedEventArgs e)
    {
        if (applyingSharedLink)
        {
            return;
        }
        await SetSharedLinkAsync(SharedMicLinkCheck.IsChecked == true, fromHotkey: false);
    }

    private Brush ResourceBrush(string key) => (Brush)FindResource(key);

    private static SolidColorBrush SoftBrush(byte alpha, byte red, byte green, byte blue) =>
        new(Color.FromArgb(alpha, red, green, blue));

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Log("控制台已打开，正在检查接收端…");
        await EnsureServerAsync();
        await RefreshStatusAsync();
        refreshTimer.Start();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (isExiting)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        trayIcon.ShowBalloonTip(
            1500,
            "PhoneDeck 已最小化到托盘",
            "接收端与 USB 看门狗在后台继续运行。双击托盘图标可重新打开控制台。",
            Forms.ToolTipIcon.Info);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemTheme_UserPreferenceChanged;
        if (sourceHandle != null)
        {
            UnregisterHotKey(new WindowInteropHelper(this).Handle, SharedHotKeyId);
            sourceHandle.RemoveHook(SharedHotkeyHook);
            sourceHandle = null;
        }
        refreshTimer.Stop();
        http.Dispose();
        refreshGate.Dispose();
        trayIcon.Dispose();
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e) => await RefreshStatusAsync();

    private void InitTrayIcon()
    {
        try
        {
            trayIcon.Icon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty)
                ?? Drawing.SystemIcons.Application;
        }
        catch
        {
            trayIcon.Icon = Drawing.SystemIcons.Application;
        }

        trayIcon.Text = "PhoneDeck 电脑控制台";
        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("打开控制台", null, (_, _) => Dispatcher.Invoke(RestoreFromTray))
        {
            Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold)
        };
        menu.Items.Add(openItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("启动接收端", null, async (_, _) =>
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                await EnsureServerAsync();
                await RefreshStatusAsync();
            });
        }));
        menu.Items.Add(new Forms.ToolStripMenuItem("重新启动", null, async (_, _) =>
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                await RestartServerAsync();
                await RefreshStatusAsync();
            });
        }));
        menu.Items.Add(new Forms.ToolStripMenuItem("停止接收端", null, async (_, _) =>
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                await StopServersAsync();
                await RefreshStatusAsync();
            });
        }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication)));
        trayIcon.ContextMenuStrip = menu;
        trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        trayIcon.Visible = true;
    }

    private void SystemTheme_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (isExiting || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(ApplyThemeAwareIcons);
    }

    private void ApplyThemeAwareIcons()
    {
        var useDarkIcon = IsSystemDarkMode();
        var windowIconUri = useDarkIcon ? DarkIconUri : LightIconUri;
        var brandIconUri = useDarkIcon ? DarkBrandUri : LightBrandUri;

        try
        {
            Icon = BitmapFrame.Create(windowIconUri);

            var brandImage = new BitmapImage();
            brandImage.BeginInit();
            brandImage.UriSource = brandIconUri;
            brandImage.CacheOption = BitmapCacheOption.OnLoad;
            brandImage.EndInit();
            brandImage.Freeze();
            BrandIcon.Source = brandImage;

            var resource = System.Windows.Application.GetResourceStream(windowIconUri);
            if (resource is not null)
            {
                using var sourceIcon = new Drawing.Icon(resource.Stream);
                var replacement = (Drawing.Icon)sourceIcon.Clone();
                var previous = trayIcon.Icon;
                trayIcon.Icon = replacement;
                previous?.Dispose();
            }
        }
        catch
        {
            // Keep the executable icon selected by InitTrayIcon if a resource cannot be loaded.
        }
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void ExitApplication()
    {
        isExiting = true;
        trayIcon.Visible = false;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        var page = sender == NavConnection ? "connection"
            : sender == NavDevices ? "devices"
            : sender == NavLogs ? "logs"
            : sender == NavSettings ? "settings"
            : "overview";
        ShowPage(page);
    }

    private void ShowPage(string page)
    {
        PageOverview.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        PageConnection.Visibility = page == "connection" ? Visibility.Visible : Visibility.Collapsed;
        PageDevices.Visibility = page == "devices" ? Visibility.Visible : Visibility.Collapsed;
        PageLogs.Visibility = page == "logs" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;

        NavOverview.Tag = page == "overview" ? "Active" : null;
        NavConnection.Tag = page == "connection" ? "Active" : null;
        NavDevices.Tag = page == "devices" ? "Active" : null;
        NavLogs.Tag = page == "logs" ? "Active" : null;
        NavSettings.Tag = page == "settings" ? "Active" : null;

        if (page == "settings")
        {
            CopyOverviewSettingsToPage();
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        await EnsureServerAsync();
        await RefreshStatusAsync();
    }

    private async void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        await RestartServerAsync();
        await RefreshStatusAsync();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopServersAsync();
        Log("接收端已停止。", ResourceBrush("BrushWarning"));
        await RefreshStatusAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    private async void SaveSettings_Click(object sender, RoutedEventArgs e) => await SaveSettingsAsync(fromSettingsPage: false);

    private async void SaveSettingsPage_Click(object sender, RoutedEventArgs e) => await SaveSettingsAsync(fromSettingsPage: true);

    private void AgentSettings_Click(object sender, RoutedEventArgs e)
    {
        var editor = new AgentShortcutEditorWindow(DataDirectory) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            Log("Agent 操作已更新，手机将在下一次连接检查时自动同步。", ResourceBrush("BrushSuccess"));
        }
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(currentComputerId))
        {
            Log("电脑 ID 尚未就绪。", ResourceBrush("BrushWarning"));
            return;
        }

        try
        {
            Clipboard.SetText(currentComputerId);
            Log("电脑 ID 已复制。", ResourceBrush("BrushSystemBlue"));
        }
        catch (Exception exception)
        {
            Log("复制电脑 ID 失败：" + exception.Message, ResourceBrush("BrushDanger"));
        }
    }

    private void LoadCachedIdentity()
    {
        try
        {
            var path = Path.Combine(DataDirectory, "computer-id.txt");
            if (File.Exists(path))
            {
                var value = File.ReadAllText(path).Trim();
                if (Guid.TryParse(value, out _))
                {
                    currentComputerId = value;
                    UpdateIdentity(Environment.MachineName, value);
                }
            }
        }
        catch
        {
            // 服务健康检查成功后会刷新身份。
        }
    }

    private void UpdateIdentity(string computerName, string computerId)
    {
        currentComputerId = computerId;
        TopologyComputerName.Text = computerName;
        CurrentComputerText.Text = "当前电脑 · " + computerName;
        ComputerIdText.Text = computerId;
        DeviceComputerName.Text = computerName;
        DeviceComputerId.Text = computerId;
    }

    private void LoadSettings()
    {
        loadingSettings = true;
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new ServerSettings()
                : new ServerSettings();
            LanDiscoveryCheck.IsChecked = settings.LanDiscovery;
            UsbWatchdogCheck.IsChecked = settings.UsbWatchdog;
            AdbPathBox.Text = string.IsNullOrWhiteSpace(settings.AdbPath) ? BundledAdbPath : settings.AdbPath;
            AutoStartCheck.IsChecked = IsAutoStartEnabled();
            CopyOverviewSettingsToPage();
        }
        catch (Exception exception)
        {
            Log("读取设置失败：" + exception.Message, ResourceBrush("BrushDanger"));
        }
        finally
        {
            loadingSettings = false;
        }
    }

    private void CopyOverviewSettingsToPage()
    {
        LanDiscoveryCheckPage.IsChecked = LanDiscoveryCheck.IsChecked;
        UsbWatchdogCheckPage.IsChecked = UsbWatchdogCheck.IsChecked;
        AutoStartCheckPage.IsChecked = AutoStartCheck.IsChecked;
        AdbPathBoxPage.Text = AdbPathBox.Text;
    }

    private void CopyPageSettingsToOverview()
    {
        LanDiscoveryCheck.IsChecked = LanDiscoveryCheckPage.IsChecked;
        UsbWatchdogCheck.IsChecked = UsbWatchdogCheckPage.IsChecked;
        AutoStartCheck.IsChecked = AutoStartCheckPage.IsChecked;
        AdbPathBox.Text = AdbPathBoxPage.Text;
    }

    private async Task SaveSettingsAsync(bool fromSettingsPage)
    {
        if (loadingSettings)
        {
            return;
        }

        if (fromSettingsPage)
        {
            CopyPageSettingsToOverview();
        }
        else
        {
            CopyOverviewSettingsToPage();
        }

        try
        {
            Directory.CreateDirectory(DataDirectory);
            var settings = new ServerSettings
            {
                LanDiscovery = LanDiscoveryCheck.IsChecked == true,
                UsbWatchdog = UsbWatchdogCheck.IsChecked == true,
                SharedRequested = lastSharedRequested,
                AdbPath = AdbPathBox.Text.Trim()
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            SetAutoStart(AutoStartCheck.IsChecked == true);
            Log("设置已保存，正在重新启动接收端…", ResourceBrush("BrushSystemBlue"));
            await RestartServerAsync();
            await RefreshStatusAsync();
        }
        catch (Exception exception)
        {
            Log("保存失败：" + exception.Message, ResourceBrush("BrushDanger"));
            MessageBox.Show(this, exception.Message, "PhoneDeck 设置保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task EnsureServerAsync()
    {
        if (await IsHealthyAsync())
        {
            return;
        }

        await StopServersAsync();
        if (!File.Exists(ServerPath))
        {
            Log("找不到 PhoneDeck.Server.exe", ResourceBrush("BrushDanger"));
            return;
        }

        var startInfo = new ProcessStartInfo(ServerPath)
        {
            WorkingDirectory = AppDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.Environment["PHONEDECK_DATA_DIR"] = DataDirectory;
        Process.Start(startInfo);
        Log("接收端已启动。", ResourceBrush("BrushSuccess"));
        for (var attempt = 0; attempt < 15 && !await IsHealthyAsync(); attempt++)
        {
            await Task.Delay(250);
        }
    }

    private async Task RestartServerAsync()
    {
        await StopServersAsync();
        await Task.Delay(400);
        await EnsureServerAsync();
        Log("接收端已重新启动。", ResourceBrush("BrushSuccess"));
    }

    private static Task StopServersAsync()
    {
        return Task.Run(() =>
        {
            foreach (var process in Process.GetProcessesByName("PhoneDeck.Server"))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2500);
                }
                catch
                {
                    // 已退出或无权限时，由下一次健康检查反馈。
                }
            }
        });
    }

    private async Task<bool> IsHealthyAsync()
    {
        try
        {
            using var response = await http.GetAsync("http://127.0.0.1:8765/api/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (!await refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var success = ResourceBrush("BrushSuccess");
            var warning = ResourceBrush("BrushWarning");
            var danger = ResourceBrush("BrushDanger");
            var muted = ResourceBrush("BrushMist");

            var network = FindWifiNetwork();
            SetStatus(WifiValue, WifiDot,
                network.Address ?? "未连接",
                network.Name is null ? "请连接 Wi-Fi" : network.Name + " · 手机需连接同一网络",
                network.Address is null ? danger : success,
                WifiDetail);
            ConnectionWifiStatus.Text = network.Address is null
                ? "当前未检测到可用 Wi-Fi"
                : $"{network.Name} · {network.Address}";

            JsonDocument? health = null;
            try
            {
                var json = await http.GetStringAsync("http://127.0.0.1:8765/api/health");
                health = JsonDocument.Parse(json);
            }
            catch
            {
                // 下面统一显示离线状态。
            }

            if (health is null)
            {
                SetStatus(ReceiverValue, ReceiverDot, "未运行", "点击“启动接收端”", danger, ReceiverDetail);
                SetStatus(AudioValue, AudioDot, "不可用", "接收端未运行", muted, AudioDetail);
                SharedMicDetail.Text = "接收端未运行";
                OverallText.Text = "需要启动";
                OverallText.Foreground = danger;
                OverallDot.Fill = danger;
                OverallBadge.Background = SoftBrush(18, 210, 42, 47);
                OverallBadge.BorderBrush = SoftBrush(48, 210, 42, 47);
                TopologyStatus.Text = "接收端离线";
                TopologyStatus.Foreground = danger;
                ReceiverNode.BorderBrush = SoftBrush(90, 210, 42, 47);
            }
            else
            {
                using (health)
                {
                    var root = health.RootElement;
                    var displayName = root.GetProperty("displayName").GetString() ?? Environment.MachineName;
                    var computerId = root.GetProperty("computerId").GetString() ?? currentComputerId;
                    UpdateIdentity(displayName, computerId);

                    SetStatus(ReceiverValue, ReceiverDot, "运行中",
                        displayName + " · " + root.GetProperty("version").GetString(), success, ReceiverDetail);

                    var audio = root.GetProperty("audio");
                    var available = audio.GetProperty("available").GetBoolean();
                    var streaming = audio.GetProperty("streaming").GetBoolean();
                    var mode = audio.TryGetProperty("mode", out var modeValue)
                        ? modeValue.GetString()
                        : null;
                    var modeLabel = mode == "shared" ? "共享麦克风" : "手机控制听写";
                    SetStatus(AudioValue, AudioDot,
                        streaming ? modeLabel : available ? "已就绪" : "未配置",
                        available
                            ? streaming ? $"VB-CABLE · {modeLabel}" : "VB-CABLE · Typeless"
                            : "请安装或检查 VB-CABLE",
                        available ? success : warning,
                        AudioDetail);

                    var sharedRequested = root.TryGetProperty("shared", out var sharedNode)
                        && sharedNode.TryGetProperty("requested", out var requestedNode)
                        && requestedNode.GetBoolean();
                    lastSharedRequested = sharedRequested;
                    applyingSharedLink = true;
                    SharedMicLinkCheck.IsChecked = sharedRequested;
                    applyingSharedLink = false;
                    SharedMicDetail.Text = !sharedRequested
                        ? "关闭 · 开关或 Ctrl+Alt+M 让手机开麦"
                        : streaming && mode == "shared"
                            ? "手机供音中 · Ctrl+Alt+M 关闭"
                            : "已请求 · 等待手机开始供音";

                    OverallText.Text = network.Address is null ? "等待 Wi-Fi" : "可以连接手机";
                    OverallText.Foreground = network.Address is null ? warning : success;
                    OverallDot.Fill = network.Address is null ? warning : success;
                    OverallBadge.Background = network.Address is null
                        ? SoftBrush(18, 191, 126, 0)
                        : SoftBrush(18, 45, 164, 78);
                    OverallBadge.BorderBrush = network.Address is null
                        ? SoftBrush(48, 191, 126, 0)
                        : SoftBrush(48, 45, 164, 78);
                    TopologyStatus.Text = network.Address is null ? "等待网络" : "通道正常";
                    TopologyStatus.Foreground = network.Address is null ? warning : success;
                    ReceiverNode.BorderBrush = network.Address is null
                        ? SoftBrush(70, 191, 126, 0)
                        : SoftBrush(70, 45, 164, 78);
                }
            }

            var usb = await ReadUsbStatusAsync();
            SetStatus(UsbValue, UsbDot,
                usb.Connected ? "已连接" : "未连接",
                usb.Detail,
                usb.Connected ? success : muted,
                UsbDetail);
            ConnectionUsbStatus.Text = usb.Connected ? "ADB 设备已连接 · " + usb.Detail : usb.Detail;
            LastCheckedText.Text = "最后检查 " + DateTime.Now.ToString("HH:mm:ss");
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private static void SetStatus(TextBlock value, Ellipse dot, string text, string detail, Brush color, TextBlock detailBlock)
    {
        value.Text = text;
        value.Foreground = color;
        dot.Fill = color;
        detailBlock.Text = detail;
    }

    private static (string? Name, string? Address) FindWifiNetwork()
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(value => value.OperationalStatus == OperationalStatus.Up &&
                         value.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
        {
            var properties = network.GetIPProperties();
            var address = properties.UnicastAddresses
                .Select(item => item.Address)
                .FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item));
            if (address is not null)
            {
                return (network.Name, address.ToString());
            }
        }
        return (null, null);
    }

    private static async Task<(bool Connected, string Detail)> ReadUsbStatusAsync()
    {
        if (!File.Exists(BundledAdbPath))
        {
            return (false, "未找到 ADB（Wi-Fi 不受影响）");
        }

        try
        {
            var startInfo = new ProcessStartInfo(BundledAdbPath, "devices")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(startInfo)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(value => value.TrimEnd().EndsWith("\tdevice", StringComparison.Ordinal));
            return line is null ? (false, "Wi-Fi 模式无需插线") : (true, line.Split('\t')[0].Trim());
        }
        catch
        {
            return (false, "ADB 检查超时");
        }
    }

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
        return key?.GetValue(AutoStartValue) is string value &&
            value.Contains(Environment.ProcessPath ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (enabled)
        {
            key.SetValue(AutoStartValue, '"' + (Environment.ProcessPath ?? string.Empty) + '"');
        }
        else
        {
            key.DeleteValue(AutoStartValue, throwOnMissingValue: false);
        }
    }

    private void Log(string message, Brush? color = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Log(message, color));
            return;
        }

        var timestamp = $"[{DateTime.Now:HH:mm:ss}] ";
        AppendLog(ActivityLog, timestamp, message, color);
        AppendLog(ActivityLogFull, timestamp, message, color);
    }

    private void AppendLog(RichTextBox target, string timestamp, string message, Brush? color)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
        paragraph.Inlines.Add(new Run(timestamp) { Foreground = ResourceBrush("BrushMist") });
        paragraph.Inlines.Add(new Run(message) { Foreground = color ?? ResourceBrush("BrushSlate") });
        target.Document.Blocks.Add(paragraph);
        while (target.Document.Blocks.Count > 180)
        {
            target.Document.Blocks.Remove(target.Document.Blocks.FirstBlock);
        }
        target.ScrollToEnd();
    }

    private sealed class ServerSettings
    {
        public bool UsbWatchdog { get; set; } = true;
        public bool LanDiscovery { get; set; } = true;
        public bool SharedRequested { get; set; }
        public string? AdbPath { get; set; }
    }
}
