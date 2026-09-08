using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace PhoneDeck.ControlCenter;

internal sealed class ControlCenterForm : Form
{
    private const string AutoStartValue = "PhoneDeck Control Center";
    // Warm, paper-like neutrals keep the console calm and product-oriented.
    // Saturated color is reserved for connection state and primary actions.
    private static readonly Color Canvas = Color.FromArgb(246, 246, 248);
    private static readonly Color Sidebar = Color.FromArgb(251, 251, 253);
    private static readonly Color Card = Color.FromArgb(255, 255, 255);
    private static readonly Color CardRaised = Color.FromArgb(245, 245, 247);
    private static readonly Color Ink = Color.FromArgb(29, 29, 31);
    private static readonly Color Muted = Color.FromArgb(110, 110, 115);
    private static readonly Color Primary = Color.FromArgb(0, 113, 227);
    private static readonly Color Success = Color.FromArgb(45, 164, 78);
    private static readonly Color Warning = Color.FromArgb(191, 126, 0);
    private static readonly Color Danger = Color.FromArgb(210, 42, 47);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 2500 };
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly Label receiverValue = ValueLabel();
    private readonly Label receiverDetail = DetailLabel();
    private readonly Label wifiValue = ValueLabel();
    private readonly Label wifiDetail = DetailLabel();
    private readonly Label audioValue = ValueLabel();
    private readonly Label audioDetail = DetailLabel();
    private readonly Label usbValue = ValueLabel();
    private readonly Label usbDetail = DetailLabel();
    private readonly Label overallBadge = new();
    private readonly Label lastCheckedLabel = new();
    private readonly CheckBox lanDiscoveryCheck = SettingCheck("Wi-Fi 自动发现", "手机与电脑同一 Wi-Fi 时自动出现");
    private readonly CheckBox usbWatchdogCheck = SettingCheck("USB 自动恢复", "插线时自动重建语音通道");
    private readonly CheckBox autoStartCheck = SettingCheck("登录后打开控制台", "Windows 登录后自动启动接收端与本界面");
    private readonly TextBox adbPathBox = new();
    private readonly RichTextBox activityLog = new();
    private readonly Button startButton;
    private readonly Button restartButton;
    private readonly Button stopButton;
    private readonly Button saveButton;
    private readonly Button agentSettingsButton;
    private bool loadingSettings;
    private readonly NotifyIcon trayIcon = new();
    private bool isExiting;

    private static string AppDirectory => AppContext.BaseDirectory;
    private static string ServerPath => Path.Combine(AppDirectory, "PhoneDeck.Server.exe");
    private static string DataDirectory => Path.Combine(AppDirectory, "data");
    private static string SettingsPath => Path.Combine(DataDirectory, "server-settings.json");
    private static string BundledAdbPath => Path.Combine(AppDirectory, "platform-tools", "adb.exe");

    internal ControlCenterForm()
    {
        Text = "PhoneDeck 电脑控制台";
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Canvas;
        ForeColor = Ink;
        MinimumSize = new Size(1080, 760);
        Size = new Size(1180, 840);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        startButton = ActionButton("启动接收端", Primary);
        restartButton = ActionButton("重新启动", CardRaised, secondary: true);
        stopButton = ActionButton("停止", CardRaised, secondary: true);
        saveButton = ActionButton("保存并应用", Primary);
        agentSettingsButton = ActionButton("配置 Agent 操作", CardRaised, secondary: true);
        agentSettingsButton.Size = new Size(170, 36);

        Controls.Add(BuildLayout());
        WireEvents();
        LoadSettings();
        InitTrayIcon();
    }

    private void InitTrayIcon()
    {
        try
        {
            trayIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch
        {
            trayIcon.Icon = SystemIcons.Application;
        }
        trayIcon.Text = "PhoneDeck 电脑控制台";
        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("打开控制台", null, (_, _) => RestoreFromTray())
        {
            Font = new Font(menu.Font, FontStyle.Bold)
        };
        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("启动接收端", null, async (_, _) =>
        {
            await EnsureServerAsync();
            await RefreshStatusAsync();
        }));
        menu.Items.Add(new ToolStripMenuItem("重新启动", null, async (_, _) =>
        {
            await RestartServerAsync();
            await RefreshStatusAsync();
        }));
        menu.Items.Add(new ToolStripMenuItem("停止接收端", null, async (_, _) =>
        {
            await StopServersAsync();
            await RefreshStatusAsync();
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApplication()));
        trayIcon.ContextMenuStrip = menu;
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        trayIcon.Visible = true;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private void ExitApplication()
    {
        isExiting = true;
        trayIcon.Visible = false;
        Close();
        Application.Exit();
    }

    private Control BuildLayout()
    {
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 22, 28, 24),
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Canvas
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 156));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        content.Controls.Add(BuildHeader(), 0, 0);
        content.Controls.Add(BuildStatusCards(), 0, 1);
        content.Controls.Add(BuildMainArea(), 0, 2);
        content.Controls.Add(BuildFooter(), 0, 3);

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Canvas,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 188));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.Controls.Add(BuildSidebar(), 0, 0);
        shell.Controls.Add(content, 1, 0);
        return shell;
    }

    private Control BuildSidebar()
    {
        var sidebar = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Sidebar,
            Padding = new Padding(16, 20, 14, 16),
            Margin = Padding.Empty
        };

        var brand = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Sidebar };
        brand.Controls.Add(new Label
        {
            Text = "PhoneDeck",
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
            ForeColor = Ink,
            AutoSize = true,
            Location = new Point(4, 4)
        });
        brand.Controls.Add(new Label
        {
            Text = "电脑控制中心",
            Font = new Font(Font.FontFamily, 7.5F, FontStyle.Bold),
            AutoSize = true,
            ForeColor = Muted,
            Location = new Point(5, 34)
        });
        sidebar.Controls.Add(brand);

        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 270,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Sidebar,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        nav.Controls.Add(NavButton("概览", "⌂", active: true));
        nav.Controls.Add(NavButton("连接", "↔"));
        nav.Controls.Add(NavButton("设备", "▣"));
        nav.Controls.Add(NavButton("日志", "≡"));
        nav.Controls.Add(NavButton("设置", "⚙"));
        sidebar.Controls.Add(nav);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 74, BackColor = Sidebar };
        footer.Controls.Add(new Label
        {
            Text = "PhoneDeck 1.6",
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
            ForeColor = Ink,
            AutoSize = true,
            Location = new Point(4, 4)
        });
        footer.Controls.Add(new Label
        {
            Text = "本地优先 · Wi‑Fi / USB",
            Font = new Font(Font.FontFamily, 8F),
            ForeColor = Muted,
            AutoSize = true,
            Location = new Point(4, 30)
        });
        sidebar.Controls.Add(footer);
        return sidebar;
    }

    private static Button NavButton(string text, string glyph, bool active = false)
    {
        var button = new Button
        {
            Text = $"  {glyph}   {text}",
            AutoSize = false,
            Width = 174,
            Height = 42,
            FlatStyle = FlatStyle.Flat,
            BackColor = active ? Color.FromArgb(237, 243, 252) : Sidebar,
            ForeColor = active ? Ink : Muted,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 0, 6),
            Font = new Font("Microsoft YaHei UI", 9.5F, active ? FontStyle.Bold : FontStyle.Regular),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(245, 247, 250);
        return button;
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            BackColor = Canvas,
            Margin = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));

        var titles = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
        titles.Controls.Add(new Label
        {
            Text = "PHONEDECK  ·  控制中心",
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
            ForeColor = Muted,
            AutoSize = true,
            Location = new Point(0, 4)
        });
        titles.Controls.Add(new Label
        {
            Text = "电脑控制台",
            Font = new Font(Font.FontFamily, 22F, FontStyle.Bold),
            ForeColor = Ink,
            AutoSize = true,
            Location = new Point(-2, 28)
        });
        titles.Controls.Add(new Label
        {
            Text = "管理接收端、连接与设备状态",
            Font = new Font(Font.FontFamily, 10F),
            ForeColor = Muted,
            AutoSize = true,
            Location = new Point(0, 90)
        });

        overallBadge.Text = "正在检查…";
        overallBadge.AutoSize = false;
        overallBadge.Dock = DockStyle.Right;
        overallBadge.Size = new Size(176, 36);
        overallBadge.TextAlign = ContentAlignment.MiddleCenter;
        overallBadge.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
        overallBadge.ForeColor = Muted;
        overallBadge.BackColor = Color.FromArgb(245, 245, 247);
        overallBadge.Margin = new Padding(0, 24, 0, 30);

        header.Controls.Add(titles, 0, 0);
        header.Controls.Add(overallBadge, 1, 0);
        return header;
    }

    private Control BuildStatusCards()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            Padding = new Padding(0, 4, 0, 18),
            BackColor = Canvas,
            Margin = Padding.Empty
        };
        for (var index = 0; index < 4; index++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }
        row.Controls.Add(StatusCard("接收端", receiverValue, receiverDetail), 0, 0);
        row.Controls.Add(StatusCard("Wi-Fi", wifiValue, wifiDetail), 1, 0);
        row.Controls.Add(StatusCard("手机音频", audioValue, audioDetail), 2, 0);
        row.Controls.Add(StatusCard("USB 兜底", usbValue, usbDetail), 3, 0);
        return row;
    }

    private Control BuildMainArea()
    {
        var area = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            BackColor = Canvas,
            Margin = Padding.Empty
        };
        area.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        area.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        area.Controls.Add(BuildOperationsCard(), 0, 0);
        area.Controls.Add(BuildSettingsCard(), 1, 0);
        return area;
    }

    private Control BuildOperationsCard()
    {
        var card = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0) };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 20, 24, 20),
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Color.Transparent
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(SectionTitle("连接控制"), 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent
        };
        buttons.Controls.Add(startButton);
        buttons.Controls.Add(restartButton);
        buttons.Controls.Add(stopButton);
        layout.Controls.Add(buttons, 0, 1);

        var hint = new Label
        {
            Text = "推荐：电脑端保持运行，手机连上相同 Wi-Fi 后点击顶部连接卡片即可。",
            ForeColor = Muted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(hint, 0, 2);
        layout.Controls.Add(new Label
        {
            Text = "运行记录",
            ForeColor = Ink,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 3);

        activityLog.Dock = DockStyle.Fill;
        activityLog.ReadOnly = true;
        activityLog.BorderStyle = BorderStyle.None;
        activityLog.BackColor = Color.FromArgb(247, 249, 252);
        activityLog.ForeColor = Ink;
        activityLog.Font = new Font("Cascadia Mono", 9F);
        activityLog.DetectUrls = false;
        layout.Controls.Add(activityLog, 0, 4);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildSettingsCard()
    {
        var card = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(10, 0, 0, 0) };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 20, 24, 20),
            ColumnCount = 1,
            RowCount = 9,
            BackColor = Color.Transparent
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(SectionTitle("连接设置"), 0, 0);
        layout.Controls.Add(lanDiscoveryCheck, 0, 1);
        layout.Controls.Add(usbWatchdogCheck, 0, 2);
        layout.Controls.Add(autoStartCheck, 0, 3);
        layout.Controls.Add(agentSettingsButton, 0, 4);
        layout.Controls.Add(new Label
        {
            Text = "ADB 路径（USB 兜底）",
            ForeColor = Muted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 5);

        adbPathBox.Dock = DockStyle.Fill;
        adbPathBox.BorderStyle = BorderStyle.FixedSingle;
        adbPathBox.BackColor = Color.White;
        adbPathBox.ForeColor = Ink;
        adbPathBox.BorderStyle = BorderStyle.FixedSingle;
        adbPathBox.Margin = new Padding(0, 4, 0, 4);
        layout.Controls.Add(adbPathBox, 0, 6);
        layout.Controls.Add(saveButton, 0, 7);

        var dataHint = new Label
        {
            Text = "配置与配对数据保存在：\n" + DataDirectory,
            ForeColor = Muted,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Padding = new Padding(0, 8, 0, 0)
        };
        layout.Controls.Add(dataHint, 0, 8);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            BackColor = Canvas,
            Padding = new Padding(0, 10, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        footer.Controls.Add(new Label
        {
            Text = "PhoneDeck 1.6 · 本地优先，不经过云端转发",
            ForeColor = Muted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        lastCheckedLabel.ForeColor = Muted;
        lastCheckedLabel.Dock = DockStyle.Fill;
        lastCheckedLabel.TextAlign = ContentAlignment.MiddleRight;
        footer.Controls.Add(lastCheckedLabel, 1, 0);
        return footer;
    }

    private void WireEvents()
    {
        Shown += async (_, _) =>
        {
            Log("控制台已打开，正在检查接收端…");
            await EnsureServerAsync();
            await RefreshStatusAsync();
            refreshTimer.Start();
        };
        FormClosing += (_, e) =>
        {
            if (!isExiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                trayIcon.ShowBalloonTip(
                    1500,
                    "PhoneDeck 已最小化到托盘",
                    "接收端与 USB 看门狗在后台继续运行。双击托盘图标可重新打开控制台。",
                    ToolTipIcon.Info);
            }
        };
        FormClosed += (_, _) =>
        {
            refreshTimer.Stop();
            http.Dispose();
            refreshGate.Dispose();
            trayIcon.Dispose();
        };
        refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        startButton.Click += async (_, _) =>
        {
            await EnsureServerAsync();
            await RefreshStatusAsync();
        };
        restartButton.Click += async (_, _) =>
        {
            await RestartServerAsync();
            await RefreshStatusAsync();
        };
        stopButton.Click += async (_, _) =>
        {
            await StopServersAsync();
            await RefreshStatusAsync();
        };
        saveButton.Click += async (_, _) => await SaveSettingsAsync();
        agentSettingsButton.Click += (_, _) =>
        {
            using var editor = new AgentShortcutEditorForm(DataDirectory);
            if (editor.ShowDialog(this) == DialogResult.OK)
            {
                Log("Agent 操作已更新，手机将在下一次连接检查时自动同步。", Success);
            }
        };
    }

    private void LoadSettings()
    {
        loadingSettings = true;
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<ServerSettings>(
                    File.ReadAllText(SettingsPath), JsonOptions) ?? new ServerSettings()
                : new ServerSettings();
            lanDiscoveryCheck.Checked = settings.LanDiscovery;
            usbWatchdogCheck.Checked = settings.UsbWatchdog;
            adbPathBox.Text = string.IsNullOrWhiteSpace(settings.AdbPath)
                ? BundledAdbPath : settings.AdbPath;
            autoStartCheck.Checked = IsAutoStartEnabled();
        }
        catch (Exception exception)
        {
            Log("读取设置失败：" + exception.Message, Danger);
        }
        finally
        {
            loadingSettings = false;
        }
    }

    private async Task SaveSettingsAsync()
    {
        if (loadingSettings)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var settings = new ServerSettings
            {
                LanDiscovery = lanDiscoveryCheck.Checked,
                UsbWatchdog = usbWatchdogCheck.Checked,
                AdbPath = adbPathBox.Text.Trim()
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            SetAutoStart(autoStartCheck.Checked);
            Log("设置已保存，正在重新启动接收端…", Primary);
            await RestartServerAsync();
            await RefreshStatusAsync();
        }
        catch (Exception exception)
        {
            Log("保存失败：" + exception.Message, Danger);
            MessageBox.Show(this, exception.Message, "PhoneDeck 设置保存失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task EnsureServerAsync()
    {
        if (await IsHealthyAsync())
        {
            return;
        }
        await StopServersAsync(logResult: false);
        if (!File.Exists(ServerPath))
        {
            Log("找不到 PhoneDeck.Server.exe", Danger);
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
        Log("接收端已启动。", Success);
        for (var attempt = 0; attempt < 15 && !await IsHealthyAsync(); attempt++)
        {
            await Task.Delay(250);
        }
    }

    private async Task RestartServerAsync()
    {
        await StopServersAsync(logResult: false);
        await Task.Delay(400);
        await EnsureServerAsync();
        Log("接收端已重新启动。", Success);
    }

    private static Task StopServersAsync(bool logResult = true)
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
                    // 已退出或没有权限时由后续健康检查反馈。
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
            var network = FindWifiNetwork();
            wifiValue.Text = network.Address ?? "未连接";
            wifiDetail.Text = network.Name is null
                ? "请连接 Wi-Fi" : network.Name + " · 手机需连接同一网络";
            wifiValue.ForeColor = network.Address is null ? Danger : Success;

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
                receiverValue.Text = "未运行";
                receiverDetail.Text = "点击“启动接收端”";
                receiverValue.ForeColor = Danger;
                audioValue.Text = "不可用";
                audioDetail.Text = "接收端未运行";
                audioValue.ForeColor = Muted;
                overallBadge.Text = "需要启动";
                overallBadge.ForeColor = Danger;
                overallBadge.BackColor = Color.FromArgb(252, 235, 236);
            }
            else
            {
                using (health)
                {
                    var root = health.RootElement;
                    receiverValue.Text = "运行中";
                    receiverValue.ForeColor = Success;
                    receiverDetail.Text = root.GetProperty("displayName").GetString() +
                        " · " + root.GetProperty("version").GetString();

                    var audio = root.GetProperty("audio");
                    var available = audio.GetProperty("available").GetBoolean();
                    var streaming = audio.GetProperty("streaming").GetBoolean();
                    audioValue.Text = streaming ? "传输中" : available ? "已就绪" : "未配置";
                    audioValue.ForeColor = available ? Success : Warning;
                    audioDetail.Text = available
                        ? "VB-CABLE · 语音引擎" : "请安装或检查 VB-CABLE";

                    overallBadge.Text = network.Address is null ? "等待 Wi-Fi" : "可以连接手机";
                    overallBadge.ForeColor = network.Address is null ? Warning : Success;
                    overallBadge.BackColor = network.Address is null
                        ? Color.FromArgb(255, 246, 226) : Color.FromArgb(229, 246, 238);
                }
            }

            var usb = await ReadUsbStatusAsync();
            usbValue.Text = usb.Connected ? "已连接" : "未连接";
            usbValue.ForeColor = usb.Connected ? Success : Muted;
            usbDetail.Text = usb.Detail;
            lastCheckedLabel.Text = "最后检查 " + DateTime.Now.ToString("HH:mm:ss");
        }
        finally
        {
            refreshGate.Release();
        }
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
                .FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(item));
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
            return line is null
                ? (false, "Wi-Fi 模式无需插线")
                : (true, line.Split('\t')[0].Trim());
        }
        catch
        {
            return (false, "ADB 检查超时");
        }
    }

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
        return key?.GetValue(AutoStartValue) is string value &&
            value.Contains(Environment.ProcessPath ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (enabled)
        {
            key.SetValue(AutoStartValue, '"' + (Environment.ProcessPath ?? string.Empty) + '"');
        }
        else
        {
            key.DeleteValue(AutoStartValue, throwOnMissingValue: false);
        }
    }

    private void Log(string message, Color? color = null)
    {
        if (activityLog.InvokeRequired)
        {
            activityLog.BeginInvoke(() => Log(message, color));
            return;
        }
        activityLog.SelectionStart = activityLog.TextLength;
        activityLog.SelectionColor = color ?? Muted;
        activityLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        activityLog.ScrollToCaret();
    }

    private static Control StatusCard(string title, Label value, Label detail)
    {
        var card = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(4, 0, 4, 0) };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 13, 16, 11),
            RowCount = 3,
            BackColor = Color.Transparent
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.Controls.Add(new Label
        {
            Text = title,
            ForeColor = Muted,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Dock = DockStyle.Fill
        }, 0, 0);
        layout.Controls.Add(value, 0, 1);
        layout.Controls.Add(detail, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private static Label ValueLabel() => new()
    {
        Text = "检查中",
        ForeColor = Muted,
        Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static Label DetailLabel() => new()
    {
        Text = "—",
        ForeColor = Muted,
        Font = new Font("Microsoft YaHei UI", 8.5F),
        Dock = DockStyle.Fill,
        AutoEllipsis = true
    };

    private static Label SectionTitle(string text) => new()
    {
        Text = text,
        ForeColor = Ink,
        Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold),
        Dock = DockStyle.Fill
    };

    private static CheckBox SettingCheck(string title, string description)
    {
        return new CheckBox
        {
            Text = title + "  ·  " + description,
            ThreeState = false,
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = Ink,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(2, 4, 0, 4),
            Font = new Font("Microsoft YaHei UI", 8.8F)
        };
    }

    private static Button ActionButton(string text, Color color, bool secondary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Size = new Size(126, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = color,
            ForeColor = secondary ? Ink : Color.White,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 4, 8, 4),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
        };
        button.FlatAppearance.BorderSize = secondary ? 1 : 0;
        button.FlatAppearance.BorderColor = secondary ? Color.FromArgb(210, 210, 215) : color;
        button.FlatAppearance.MouseOverBackColor = secondary
            ? Color.FromArgb(237, 237, 240)
            : Color.FromArgb(0, 102, 204);
        return button;
    }

    private sealed class ServerSettings
    {
        public bool UsbWatchdog { get; set; } = true;
        public bool LanDiscovery { get; set; } = true;
        public string? AdbPath { get; set; }
    }

    private sealed class RoundedPanel : Panel
    {
        internal RoundedPanel()
        {
            DoubleBuffered = true;
            BackColor = Card;
            Padding = new Padding(1);
        }

        protected override void OnResize(EventArgs eventArgs)
        {
            base.OnResize(eventArgs);
            using var path = RoundedRectangle(ClientRectangle, 14);
            Region = new Region(path);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = ClientRectangle;
            bounds.Width -= 1;
            bounds.Height -= 1;
            using var path = RoundedRectangle(bounds, 14);
            using var pen = new Pen(Color.FromArgb(225, 225, 230), 1F);
            eventArgs.Graphics.DrawPath(pen, path);
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            var diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
