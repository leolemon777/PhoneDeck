using Microsoft.Win32;
using System.Drawing.Drawing2D;

namespace PhoneDeck.ControlCenter;

/// <summary>托盘状态窗口的数据（每次刷新由 ReceiverTray 从本机回环接口组装）。</summary>
internal sealed record ReceiverStatus(
    string Name, string Version, bool Streaming, bool AudioOk, string? AudioDevice,
    string EngineName, bool MicOk, string? Microphone, bool? LanDiscovery, bool? AutoStart,
    IReadOnlyList<string> ActivePhones, IReadOnlyList<PhoneEntry> Phones);

/// <summary>已允许的手机；Legacy = 旧版共享令牌条目。</summary>
internal sealed record PhoneEntry(string ClientId, string Label, string Detail, bool Legacy);

// 墨白风格状态窗口：连接状态大卡、这台电脑的检查清单、已允许的手机。原生控件加少量纯色填充，无动画与网页渲染。
internal sealed class ReceiverStatusWindow : Form
{
    private readonly Palette palette;
    private readonly Label subtitle, heroState, heroTitle, heroLine;
    private readonly SurfacePanel hero;
    private readonly TableLayoutPanel checks, phones;
    private readonly Button reconnect;
    private readonly List<Font> fonts = new();
    private readonly Func<Task> connect;
    private readonly Func<PhoneEntry, Task> revoke;
    private string? lastSignature;

    internal ReceiverStatusWindow(Icon? icon, Func<Task> connect, Func<PhoneEntry, Task> revoke, bool? dark = null)
    {
        SuspendLayout();
        this.connect = connect;
        this.revoke = revoke;
        palette = new Palette(dark ?? DarkSystemTheme());
        Text = "言渡 · 电脑接收端";
        Font = OwnFont(10);
        BackColor = palette.Background; ForeColor = palette.Text;
        Icon = icon; StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 700); MinimumSize = new Size(440, 560);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; } };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20, 16, 20, 20) };
        var content = Stack(); content.Dock = DockStyle.Top;

        var brand = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3, Margin = new Padding(0, 0, 0, 14) };
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var mark = new SurfacePanel(palette.Ink, palette.Ink, 12) { Size = new Size(40, 40), Margin = new Padding(0, 2, 0, 0) };
        mark.Controls.Add(new Label { Text = "言", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Font = OwnFont(14, true), ForeColor = palette.OnInk, BackColor = Color.Transparent });
        brand.Controls.Add(mark, 0, 0);
        var brandCopy = Stack();
        brandCopy.Controls.Add(Copy("言渡", 15, palette.Text, true));
        subtitle = Copy("电脑接收端 · " + Environment.MachineName, 9, palette.Muted);
        brandCopy.Controls.Add(subtitle); brand.Controls.Add(brandCopy, 1, 0);
        reconnect = OutlineButton("重新连接");
        reconnect.AccessibleDescription = "检查并恢复本机接收端，不中断正在进行的语音";
        reconnect.Click += async (_, _) => { try { await this.connect(); } catch (Exception e) { ShowNotice("连接未完成", e.Message); } };
        brand.Controls.Add(reconnect, 2, 0);
        content.Controls.Add(brand);

        hero = new SurfacePanel(palette.Ink, palette.Ink, 20) { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(18), Margin = new Padding(0, 0, 0, 16) };
        var heroStack = Stack();
        heroState = Copy("●  正在检查", 9, palette.OnInk); heroState.Margin = new Padding(0, 0, 0, 6);
        heroTitle = Copy("准备连接", 20, palette.OnInk, true); heroTitle.Margin = new Padding(0, 0, 0, 4);
        heroLine = Copy("正在读取接收端状态…", 10, palette.OnInk); heroLine.MaximumSize = new Size(360, 0);
        heroStack.Controls.Add(heroState); heroStack.Controls.Add(heroTitle); heroStack.Controls.Add(heroLine);
        hero.Controls.Add(heroStack); content.Controls.Add(hero);

        content.Controls.Add(Section("这台电脑"));
        checks = ListPanel(content);
        content.Controls.Add(Section("已允许的手机"));
        phones = ListPanel(content);

        var foot = Copy("设置在手机：言渡 → ⋯ → 设置 → 多电脑配置。\n关掉窗口后，接收端继续在托盘运行。", 9, palette.Muted);
        foot.Margin = new Padding(0, 4, 0, 0); content.Controls.Add(foot);
        scroll.Controls.Add(content); Controls.Add(scroll);
        if (palette.Dark && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            HandleCreated += (_, _) => { var enabled = 1; DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)); };
        // 先建控件树再设定设计 DPI，否则后续像素尺寸在 150% 缩放下不放大。
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(true);
    }

    internal void SetBusy(bool busy)
    {
        if (IsDisposed) return;
        reconnect.Enabled = !busy; reconnect.Text = busy ? "正在重新连接…" : "重新连接";
    }

    internal void UpdateStatus(ReceiverStatus status)
    {
        if (IsDisposed) return;
        subtitle.Text = "电脑接收端 · " + status.Name + " · " + status.Version;
        var problems = !status.AudioOk || !status.MicOk;
        SetHero(problems);
        if (problems)
        {
            heroState.Text = "●  还差一步";
            heroTitle.Text = status.ActivePhones.Count > 0 ? "手机连上了，但还不能说话" : "还不能用手机说话";
            heroLine.Text = "把下面标出的项目弄好，手机的声音才能进到语音输入法。";
        }
        else if (status.ActivePhones.Count > 0)
        {
            heroState.Text = status.Streaming ? "●  正在接收声音" : "●  接收中 · 等待说话";
            heroTitle.Text = $"已连接 {status.ActivePhones.Count} 台手机";
            heroLine.Text = string.Join("，", status.ActivePhones);
        }
        else
        {
            heroState.Text = "●  设备已就绪";
            heroTitle.Text = "等待手机连接";
            heroLine.Text = "手机打开言渡后会显示在这里。";
        }

        // 列表内容没变就不重建，避免每 2.5 秒闪烁与按钮焦点丢失。
        var signature = string.Join("|", status.AudioOk, status.AudioDevice, status.EngineName, status.MicOk,
            status.Microphone, status.LanDiscovery, status.AutoStart,
            string.Join(",", status.Phones.Select(phone => phone.ClientId + phone.Detail)));
        if (signature == lastSignature) return;
        lastSignature = signature;
        checks.SuspendLayout(); checks.Controls.Clear();
        AddRow(checks, status.AudioOk, status.AudioOk ? "虚拟声卡" : "没找到虚拟声卡",
            status.AudioOk ? (status.AudioDevice ?? "CABLE Input") + " · 48 kHz" : "需要安装 VB-CABLE，装完重启电脑",
            status.AudioOk ? null : ("怎么安装", () => OpenLink("https://vb-audio.com/Cable/")));
        AddRow(checks, status.MicOk, status.MicOk ? "语音输入法" : status.EngineName + " 的麦克风不是 CABLE Output",
            status.MicOk ? status.EngineName + (status.Microphone is { Length: > 0 } mic ? " · 麦克风：" + mic : "")
                : "在 " + status.EngineName + " 设置里把麦克风改成 CABLE Output", null);
        if (status.LanDiscovery is { } lan)
            AddRow(checks, lan, "局域网发现", lan ? "已开启 · 同一 Wi-Fi 的手机会看到这台电脑" : "已关闭 · 可在手机「多电脑配置」里打开", null);
        if (status.AutoStart is { } auto)
            AddRow(checks, auto, "开机启动", auto ? "已开启" : "未开启 · 托盘菜单或手机里可以打开", null, neutral: !auto);
        checks.ResumeLayout(true);

        phones.SuspendLayout(); phones.Controls.Clear();
        foreach (var phone in status.Phones)
        {
            var entry = phone;
            AddRow(phones, true, entry.Label, entry.Detail, (entry.Legacy ? "关闭" : "撤销", () => ConfirmRevoke(entry)), phoneRow: true);
        }
        if (status.Phones.Count == 0)
        {
            var empty = Copy("还没有允许任何手机。手机连同一 Wi-Fi 后在首页点这台电脑即可。", 9, palette.Muted);
            empty.Margin = new Padding(14, 12, 14, 12); empty.MaximumSize = new Size(380, 0); phones.Controls.Add(empty);
        }
        phones.ResumeLayout(true);
    }

    internal void ShowNotice(string title, string message)
    {
        if (IsDisposed) return;
        SetHero(true);
        heroState.Text = "●  等待连接"; heroTitle.Text = title; heroLine.Text = message;
    }

    private void SetHero(bool warn)
    {
        hero.Fill = warn ? palette.WarnFill : palette.Ink;
        var ink = warn ? palette.Text : palette.OnInk;
        heroTitle.ForeColor = ink; heroLine.ForeColor = ink;
        heroState.ForeColor = warn ? palette.Warning : palette.OnInk;
        hero.Invalidate();
    }

    private void ConfirmRevoke(PhoneEntry entry)
    {
        var message = entry.Legacy
            ? "关闭后，仍用旧版共享令牌的手机要重新连接这台电脑。确定关闭？"
            : $"撤销后「{entry.Label}」要重新连接这台电脑。确定撤销？";
        if (MessageBox.Show(this, message, "言渡", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
            _ = revoke(entry);
    }

    private void AddRow(TableLayoutPanel list, bool ok, string title, string detail, (string Text, Action Click)? action,
        bool phoneRow = false, bool neutral = false)
    {
        var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3, Margin = Padding.Empty,
            Padding = new Padding(12, 10, 12, 10), BackColor = Color.Transparent };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var fill = phoneRow || neutral ? palette.Surface : ok ? palette.OkFill : palette.WarnFill;
        var glyph = phoneRow ? "▢" : neutral ? "–" : ok ? "✓" : "!";
        var badge = new SurfacePanel(fill, fill, 14) { Size = new Size(28, 28), Margin = new Padding(0, 2, 0, 0) };
        badge.Controls.Add(new Label { Text = glyph, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent,
            Font = OwnFont(9, true), ForeColor = phoneRow || neutral ? palette.Text : ok ? palette.Success : palette.Warning });
        row.Controls.Add(badge, 0, 0);
        var copy = Stack();
        copy.Controls.Add(Copy(title, 10, palette.Text, true));
        var sub = Copy(detail, 9, palette.Muted); sub.MaximumSize = new Size(260, 0); copy.Controls.Add(sub);
        row.Controls.Add(copy, 1, 0);
        if (action is { } act)
        {
            var button = OutlineButton(act.Text);
            button.Click += (_, _) => act.Click();
            row.Controls.Add(button, 2, 0);
        }
        if (list.Controls.Count > 0)
            list.Controls.Add(new Panel { Height = 1, Dock = DockStyle.Top, BackColor = palette.Outline, Margin = Padding.Empty });
        list.Controls.Add(row);
    }

    private TableLayoutPanel ListPanel(TableLayoutPanel content)
    {
        var frame = new SurfacePanel(palette.Background, palette.Outline, 16) { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(1), Margin = new Padding(0, 0, 0, 16) };
        var list = Stack();
        frame.Controls.Add(list); content.Controls.Add(frame);
        return list;
    }

    private Label Section(string text)
    {
        var label = Copy(text, 9, palette.Muted); label.Margin = new Padding(2, 0, 0, 8); return label;
    }

    private Button OutlineButton(string text)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, 34), Font = OwnFont(9),
            FlatStyle = FlatStyle.Flat, BackColor = palette.Background, ForeColor = palette.Text, Cursor = Cursors.Hand,
            Padding = new Padding(10, 2, 10, 2), Margin = new Padding(8, 2, 0, 0), UseVisualStyleBackColor = false };
        button.FlatAppearance.BorderColor = palette.Outline;
        button.FlatAppearance.MouseOverBackColor = palette.Surface;
        button.FlatAppearance.MouseDownBackColor = palette.Surface;
        return button;
    }

    private static void OpenLink(string url)
    {
        try { using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private TableLayoutPanel Stack()
    {
        var panel = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Top, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return panel;
    }
    private Font OwnFont(float size, bool bold = false) { var font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular); fonts.Add(font); return font; }
    private Label Copy(string text, float size, Color color, bool bold = false) => new() {
        Text = text, AutoSize = true, Dock = DockStyle.Top, Font = OwnFont(size, bold), ForeColor = color, BackColor = Color.Transparent, Margin = Padding.Empty };
    internal static bool DarkSystemTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { foreach (var font in fonts) font.Dispose(); fonts.Clear(); }
    }

    /// <summary>墨白令牌（与手机 App、本机状态页同一套）。</summary>
    internal sealed class Palette
    {
        internal readonly bool Dark;
        internal readonly Color Background, Surface, Text, Muted, Outline, Ink, OnInk, Success, OkFill, Warning, WarnFill;
        internal Palette(bool dark)
        {
            Dark = dark;
            Color C(string light, string night) => ColorTranslator.FromHtml(dark ? night : light);
            Background = C("#FFFFFF", "#141413"); Surface = C("#F5F5F3", "#1F1F1D"); Text = C("#151515", "#F1F0EB");
            Muted = C("#63625C", "#A6A59E"); Outline = C("#E2E1DC", "#34342F"); Ink = C("#151515", "#F1F0EB");
            OnInk = C("#FFFFFF", "#141413"); Success = C("#1E7A4C", "#62C793"); OkFill = C("#E5F2EA", "#1B3226");
            Warning = C("#8F5400", "#E8AE52"); WarnFill = C("#FAEFD9", "#3A2C14");
        }
    }

    internal sealed class SurfacePanel : Panel
    {
        private readonly Color border;
        private readonly int radius;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal Color Fill { get; set; }
        internal SurfacePanel(Color fill, Color border, int radius)
        {
            Fill = fill; this.border = border; this.radius = radius;
            BackColor = Color.Transparent;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(.5f, .5f, Width - 1, Height - 1);
            var d = Math.Min(radius * 2f * DeviceDpi / 96, Math.Min(bounds.Width, bounds.Height));
            if (d <= 0) return;
            using var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90); path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90); path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90); path.CloseFigure();
            using var brush = new SolidBrush(Fill); using var pen = new Pen(border);
            e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); base.OnPaint(e);
        }
    }
}
