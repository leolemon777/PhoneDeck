using Microsoft.Win32;
using System.Drawing.Drawing2D;

namespace PhoneDeck.ControlCenter;

// Native controls and a few flat fills. No effects, animation loop, or web renderer.
internal sealed class ReceiverStatusWindow : Form
{
    private readonly Palette palette;
    private readonly Label heading, explanation, indicator, machine, engine, audio, version;
    private readonly Button reconnect;
    private readonly List<Font> fonts = new();
    private readonly Func<Task> connect;
    private readonly Image? logo;
    private bool busy;

    internal ReceiverStatusWindow(Icon? icon, Func<Task> connect, bool? dark = null)
    {
        SuspendLayout();
        this.connect = connect;
        palette = new Palette(dark ?? DarkSystemTheme());
        Text = "Luma · 电脑接收器";
        Font = OwnFont(10);
        BackColor = palette.Background; ForeColor = palette.Text;
        Icon = icon; StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(448, 566); MinimumSize = new Size(430, 570);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; } };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24) };
        var content = Stack(); content.Dock = DockStyle.Top; content.Padding = Padding.Empty;

        var brand = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3, Margin = new Padding(0, 0, 0, 24) };
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // The cream Luma ring disappears on a white surface, so the mark sits on an ink tile in both themes.
        var mark = new SurfacePanel(palette.BrandTile, palette.BrandTile, 12) { Size = new Size(44, 44), Margin = Padding.Empty };
        using (var stream = typeof(ReceiverStatusWindow).Assembly.GetManifestResourceStream("PhoneDeck.ControlCenter.Assets.Luma.png"))
        {
            if (stream != null) { using var decoded = Image.FromStream(stream); logo = new Bitmap(decoded); }
        }
        mark.Padding = new Padding(4);
        mark.Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = logo, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent });
        brand.Controls.Add(mark, 0, 0);
        var brandCopy = Stack(); brandCopy.Margin = new Padding(2, 0, 0, 0);
        brandCopy.Controls.Add(Copy("Luma", 19, palette.Text, true));
        brandCopy.Controls.Add(Copy("电脑接收器", 9, palette.Muted)); brand.Controls.Add(brandCopy, 1, 0);
        var quiet = Copy("PHONEDECK", 8, palette.Muted, true); quiet.Margin = new Padding(0, 13, 0, 0); brand.Controls.Add(quiet, 2, 0);
        content.Controls.Add(brand);

        var stateCard = new SurfacePanel(palette.Surface, palette.Outline, 18) { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(22), Margin = new Padding(0, 0, 0, 16) };
        var state = Stack();
        indicator = Copy("●  正在检查", 10, palette.Muted, true); indicator.Margin = new Padding(0, 0, 0, 12);
        heading = Copy("准备连接", 25, palette.Text, true); heading.Margin = new Padding(0, 0, 0, 9);
        explanation = Copy("正在读取接收器状态…", 10, palette.Muted); explanation.MaximumSize = new Size(310, 0);
        state.Controls.Add(indicator); state.Controls.Add(heading); state.Controls.Add(explanation); stateCard.Controls.Add(state); content.Controls.Add(stateCard);

        var details = new SurfacePanel(palette.Surface, palette.Outline, 16) { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(18, 12, 18, 12), Margin = new Padding(0, 0, 0, 20) };
        var rows = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88)); rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        machine = Detail(rows, "这台电脑", Environment.MachineName, 0);
        engine = Detail(rows, "语音输入法", "连接后显示", 1);
        audio = Detail(rows, "音频通道", "正在检查", 2);
        version = Detail(rows, "接收器版本", "—", 3);
        details.Controls.Add(rows); content.Controls.Add(details);

        reconnect = new Button { Text = "重新连接", AutoSize = true, Dock = DockStyle.Top, MinimumSize = new Size(0, 48),
            Font = OwnFont(11, true), FlatStyle = FlatStyle.Flat, BackColor = palette.Primary, ForeColor = palette.OnPrimary,
            Cursor = Cursors.Hand, Margin = new Padding(0, 0, 0, 18), Padding = new Padding(12, 8, 12, 8), UseVisualStyleBackColor = false };
        reconnect.FlatAppearance.BorderSize = 0; reconnect.FlatAppearance.MouseOverBackColor = palette.PrimaryHover;
        reconnect.FlatAppearance.MouseDownBackColor = palette.PrimaryHover;
        reconnect.Resize += (_, _) =>
        {
            using var path = RoundedPath(new RectangleF(0, 0, reconnect.Width, reconnect.Height), 12 * reconnect.DeviceDpi / 96f);
            var previous = reconnect.Region; reconnect.Region = new Region(path); previous?.Dispose();
        };
        reconnect.AccessibleDescription = "检查并恢复本机接收器，不中断正在进行的语音";
        reconnect.Click += async (_, _) => { if (busy) return; try { await this.connect(); } catch (Exception e) { ShowNotice("连接未完成", e.Message); } };
        content.Controls.Add(reconnect); AcceptButton = reconnect;
        var guide = new SurfacePanel(palette.Subtle, palette.Subtle, 12) { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(18), Margin = Padding.Empty };
        var guideCopy = Stack();
        guideCopy.Controls.Add(Copy("设置在手机，连接留在这里", 10, palette.Text, true));
        var path = Copy("手机 App → 设置 → 电脑与输入法\n关闭窗口后，接收器继续在托盘运行。", 9, palette.Hint);
        path.Margin = new Padding(0, 6, 0, 0); guideCopy.Controls.Add(path);
        guide.Controls.Add(guideCopy); content.Controls.Add(guide);
        scroll.Controls.Add(content); Controls.Add(scroll);
        if (palette.Dark && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            HandleCreated += (_, _) => { var enabled = 1; DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)); };
        // Set the design DPI after constructing the tree. Scaling earlier (e.g.
        // when assigning Font) leaves later pixel sizes unscaled at 150%.
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(true);
    }

    internal void SetBusy(bool busy)
    {
        if (IsDisposed) return;
        this.busy = busy;
        reconnect.Text = busy ? "正在重新连接…" : "重新连接";
        // Restyle instead of Enabled=false: WinForms renders flat disabled buttons
        // with system gray, which flashes on the dark theme.
        reconnect.BackColor = busy ? palette.PrimaryDisabled : palette.Primary;
        reconnect.ForeColor = busy ? palette.OnPrimaryDisabled : palette.OnPrimary;
        reconnect.FlatAppearance.MouseOverBackColor = busy ? palette.PrimaryDisabled : palette.PrimaryHover;
        reconnect.FlatAppearance.MouseDownBackColor = busy ? palette.PrimaryDisabled : palette.PrimaryHover;
        reconnect.Cursor = busy ? Cursors.WaitCursor : Cursors.Hand;
    }
    internal void UpdateStatus(string name, string engineName, bool streaming, bool audioAvailable, string build)
    {
        indicator.Text = streaming ? "●  音频传输中" : "●  接收器在线";
        indicator.ForeColor = palette.Success;
        heading.Text = streaming ? "正在接收声音" : "设备已就绪";
        explanation.Text = streaming ? "手机正在向这台电脑供音。" : "可以从手机连接，开始输入或共享麦克风。";
        machine.Text = name; engine.Text = engineName;
        audio.Text = audioAvailable ? "已就绪" : "请检查虚拟声卡";
        audio.ForeColor = audioAvailable ? palette.Success : palette.Warning;
        version.Text = build;
    }
    internal void ShowNotice(string title, string message)
    {
        if (IsDisposed) return;
        indicator.Text = "●  等待连接"; indicator.ForeColor = palette.Warning;
        heading.Text = title; explanation.Text = message;
        engine.Text = "连接后显示"; audio.Text = "等待检查"; audio.ForeColor = palette.Muted; version.Text = "—";
    }
    private Label Detail(TableLayoutPanel rows, string title, string value, int row)
    {
        var label = Copy(title, 9, palette.Muted); label.Margin = new Padding(0, 7, 6, 7);
        var result = Copy(value, 10, palette.Text); result.Margin = new Padding(0, 7, 0, 7); result.MaximumSize = new Size(255, 0);
        rows.Controls.Add(label, 0, row); rows.Controls.Add(result, 1, row); return result;
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
    private static bool DarkSystemTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { logo?.Dispose(); foreach (var font in fonts) font.Dispose(); fonts.Clear(); }
    }

    private static GraphicsPath RoundedPath(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (d <= 0) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90); path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90); path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90); path.CloseFigure();
        return path;
    }

    private sealed class Palette
    {
        internal readonly bool Dark;
        internal readonly Color Background, Surface, Text, Muted, Outline, Primary, PrimaryHover, OnPrimary, PrimaryDisabled, OnPrimaryDisabled,
            Success, Warning, Hint, Subtle, BrandTile;
        internal Palette(bool dark)
        {
            Dark = dark;
            Color C(string light, string night) => ColorTranslator.FromHtml(dark ? night : light);
            Background = C("#EFF1F6", "#0E1015"); Surface = C("#FFFFFF", "#1B2029"); Text = C("#171E2C", "#EDF1F8");
            Muted = C("#5C6678", "#A6AFC2"); Outline = C("#E3E7EF", "#313B4B");
            Primary = C("#2E62E6", "#3A69E8"); PrimaryHover = C("#2450C6", "#3157D0"); OnPrimary = C("#FFFFFF", "#FFFFFF");
            PrimaryDisabled = C("#C9D3EA", "#272F45"); OnPrimaryDisabled = C("#41506E", "#7E88A0");
            Success = C("#0C7D45", "#5FD6A0"); Warning = C("#8A5B0A", "#E8C476");
            Hint = C("#3E4859", "#C5CDDE"); Subtle = C("#EEF1FA", "#222A38"); BrandTile = C("#141B2C", "#273044");
        }
    }

    private sealed class SurfacePanel : Panel
    {
        private readonly Color fill, border;
        private readonly int radius;
        internal SurfacePanel(Color fill, Color border, int radius)
        {
            this.fill = fill; this.border = border; this.radius = radius;
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
            using var brush = new SolidBrush(fill); using var pen = new Pen(border);
            e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); base.OnPaint(e);
        }
    }
}
