namespace PhoneDeck.ControlCenter;

/// <summary>
/// 同一 Wi-Fi 的手机请求连接时弹出：手机名、四位校验码（与手机上显示的应相同）、允许 / 拒绝、倒计时。
/// 结果由 ReceiverTray 经本机回环接口提交；请求在别处收束（状态页确认、超时）时由托盘关闭本窗口。
/// </summary>
internal sealed class ConnectRequestForm : Form
{
    private readonly List<Font> fonts = new();
    private readonly System.Windows.Forms.Timer countdown = new() { Interval = 1000 };
    private readonly Label remaining;
    private int secondsLeft;

    internal string PairingId { get; }

    /// <summary>true = 允许，false = 拒绝；关窗不算拒绝（服务端到时自动拒绝）。</summary>
    internal event Action<bool>? Decided;

    internal ConnectRequestForm(Icon? icon, string pairingId, string phoneLabel, string checkCode, int seconds)
    {
        PairingId = pairingId;
        secondsLeft = Math.Max(1, seconds);
        var palette = new ReceiverStatusWindow.Palette(ReceiverStatusWindow.DarkSystemTheme());
        SuspendLayout();
        Text = "言渡 · 新手机连接";
        Icon = icon; StartPosition = FormStartPosition.CenterScreen; TopMost = true;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        BackColor = palette.Background; ForeColor = palette.Text;
        Font = OwnFont(10);
        ClientSize = new Size(400, 500);

        var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(28, 22, 28, 22) };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Label Center(string text, float size, Color color, bool bold = false, int bottom = 0)
        {
            var label = new Label { Text = text, AutoSize = false, Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleCenter,
                Font = OwnFont(size, bold), ForeColor = color, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, bottom) };
            label.Height = TextRenderer.MeasureText(text, label.Font, new Size(340, 0), TextFormatFlags.WordBreak).Height + 4;
            return label;
        }

        var phone = new ReceiverStatusWindow.SurfacePanel(palette.Surface, palette.Surface, 28)
            { Size = new Size(56, 56), Anchor = AnchorStyles.Top, Margin = new Padding(0, 0, 0, 12) };
        phone.Controls.Add(new Label { Text = "▢", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Font = OwnFont(16, true), ForeColor = palette.Text, BackColor = Color.Transparent });
        stack.Controls.Add(phone);
        stack.Controls.Add(Center(phoneLabel, 16, palette.Text, true, 2));
        stack.Controls.Add(Center("想连接这台电脑，用它说话和发快捷键", 10, palette.Muted, false, 16));
        stack.Controls.Add(Center("校验码", 9, palette.Muted, false, 6));

        var digits = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Top, WrapContents = false, Margin = new Padding(0, 0, 0, 14) };
        foreach (var digit in checkCode)
        {
            var cell = new ReceiverStatusWindow.SurfacePanel(palette.Surface, palette.Surface, 14) { Size = new Size(56, 68), Margin = new Padding(5, 0, 5, 0) };
            cell.Controls.Add(new Label { Text = digit.ToString(), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Font = Track(new Font("Consolas", 26, FontStyle.Bold)), ForeColor = palette.Text, BackColor = Color.Transparent });
            digits.Controls.Add(cell);
        }
        stack.Controls.Add(digits);
        stack.Controls.Add(Center($"先看一眼手机：显示的也是 {checkCode}，再点允许。数字不一样就拒绝。", 9, palette.Muted, false, 18));

        var buttons = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, Height = 52, Margin = new Padding(0, 0, 0, 10) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var deny = BigButton("拒绝", palette.Background, palette.Text, palette.Outline, palette.Surface);
        var allow = BigButton("允许", palette.Ink, palette.OnInk, palette.Ink, palette.Ink);
        deny.Click += (_, _) => Finish(false);
        allow.Click += (_, _) => Finish(true);
        buttons.Controls.Add(deny, 0, 0); buttons.Controls.Add(allow, 1, 0);
        stack.Controls.Add(buttons);
        remaining = Center($"{secondsLeft} 秒后没有操作会自动拒绝", 9, palette.Muted);
        stack.Controls.Add(remaining);
        Controls.Add(stack);
        CancelButton = deny;

        countdown.Tick += (_, _) =>
        {
            secondsLeft--;
            if (secondsLeft <= 0) { Close(); return; }
            remaining.Text = $"{secondsLeft} 秒后没有操作会自动拒绝";
        };
        countdown.Start();
        if (palette.Dark && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            HandleCreated += (_, _) => { var enabled = 1; ReceiverStatusWindow.DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)); };
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(true);
    }

    private void Finish(bool allow)
    {
        countdown.Stop();
        Decided?.Invoke(allow);
        Close();
    }

    private Button BigButton(string text, Color fill, Color ink, Color border, Color hover)
    {
        var button = new Button { Text = text, Dock = DockStyle.Fill, Font = OwnFont(11, true), FlatStyle = FlatStyle.Flat,
            BackColor = fill, ForeColor = ink, Cursor = Cursors.Hand, Margin = new Padding(4, 0, 4, 0), UseVisualStyleBackColor = false };
        button.FlatAppearance.BorderColor = border;
        button.FlatAppearance.MouseOverBackColor = hover;
        button.FlatAppearance.MouseDownBackColor = hover;
        return button;
    }

    private Font Track(Font font) { fonts.Add(font); return font; }

    private Font OwnFont(float size, bool bold = false)
    {
        var font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular); fonts.Add(font); return font;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { countdown.Dispose(); foreach (var font in fonts) font.Dispose(); fonts.Clear(); }
    }
}
