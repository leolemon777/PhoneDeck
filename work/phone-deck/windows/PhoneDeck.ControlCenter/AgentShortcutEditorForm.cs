using System.Text.Json;

namespace PhoneDeck.ControlCenter;

internal sealed class AgentShortcutEditorForm : Form
{
    private static readonly (string Id, string Label, string Text)[] Defaults =
    {
        ("agentPlan", "规划", "/plan"),
        ("agentGoal", "目标", "/goal"),
        ("agentCompact", "压缩上下文", "/compact"),
        ("agentClear", "新会话", "/clear")
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string settingsPath;
    private readonly List<EditorRow> rows = new();

    internal AgentShortcutEditorForm(string dataDirectory)
    {
        settingsPath = Path.Combine(dataDirectory, "agent-shortcuts.json");
        Text = "配置 Agent 操作";
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.FromArgb(240, 246, 255);
        ForeColor = Color.FromArgb(16, 39, 72);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 520);
        Size = new Size(820, 570);
        AutoScaleMode = AutoScaleMode.Dpi;
        Controls.Add(BuildLayout());
        LoadValues();
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 22, 28, 24),
            ColumnCount = 1,
            RowCount = 5,
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.Controls.Add(new Label
        {
            Text = "Agent 快捷操作",
            Dock = DockStyle.Fill,
            ForeColor = ForeColor,
            Font = new Font(Font.FontFamily, 21F, FontStyle.Bold)
        }, 0, 0);
        root.Controls.Add(new Label
        {
            Text = "替换手机按钮的名称和斜杠指令；保存后通过 Wi-Fi 或 USB 自动同步。",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(91, 115, 151)
        }, 0, 1);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = Defaults.Length + 1,
            BackColor = Color.White,
            Padding = new Padding(14, 12, 14, 12),
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
        foreach (var heading in new[] { "按钮名称", "发送内容", "自动回车", "显示" })
        {
            grid.Controls.Add(new Label
            {
                Text = heading,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(91, 115, 151),
                Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
                Padding = new Padding(6, 0, 0, 0)
            });
        }
        for (var index = 0; index < Defaults.Length; index++)
        {
            var label = EditorTextBox();
            var text = EditorTextBox();
            var submit = EditorCheckBox();
            var visible = EditorCheckBox();
            rows.Add(new EditorRow(Defaults[index].Id, label, text, submit, visible));
            grid.Controls.Add(label, 0, index + 1);
            grid.Controls.Add(text, 1, index + 1);
            grid.Controls.Add(submit, 2, index + 1);
            grid.Controls.Add(visible, 3, index + 1);
        }
        root.Controls.Add(grid, 0, 2);
        root.Controls.Add(new Label
        {
            Text = "示例：把“规划”改成“先调研”，发送内容改成 /plan before coding。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(91, 115, 151)
        }, 0, 3);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = BackColor
        };
        var save = DialogButton("保存并同步", Color.FromArgb(42, 103, 224));
        var cancel = DialogButton("取消", Color.FromArgb(112, 132, 160));
        save.Click += (_, _) => SaveValues();
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 4);
        AcceptButton = save;
        CancelButton = cancel;
        return root;
    }

    private void LoadValues()
    {
        ShortcutFile? file = null;
        try
        {
            if (File.Exists(settingsPath))
            {
                file = JsonSerializer.Deserialize<ShortcutFile>(
                    File.ReadAllText(settingsPath), JsonOptions);
            }
        }
        catch
        {
            // 损坏配置由保存时的新配置替换。
        }
        foreach (var row in rows)
        {
            var value = file?.Buttons.FirstOrDefault(button => button.Id == row.Id);
            var fallback = Defaults.First(item => item.Id == row.Id);
            row.Label.Text = value?.Label ?? fallback.Label;
            row.Text.Text = value?.Text ?? fallback.Text;
            row.Submit.Checked = value?.Submit ?? true;
            row.Visible.Checked = value?.Visible ?? true;
        }
    }

    private void SaveValues()
    {
        var values = new List<ShortcutValue>();
        foreach (var row in rows)
        {
            var label = row.Label.Text.Trim();
            var text = row.Text.Text.Trim();
            if (label.Length is < 1 or > 24)
            {
                MessageBox.Show(this, "按钮名称必须为 1–24 个字符。", "无法保存",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                row.Label.Focus();
                return;
            }
            if (text.Length is < 1 or > 512 || text.Contains('\r') || text.Contains('\n'))
            {
                MessageBox.Show(this, "发送内容必须为 1–512 个字符的单行文本。", "无法保存",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                row.Text.Focus();
                return;
            }
            values.Add(new ShortcutValue(row.Id, label, text,
                row.Submit.Checked, row.Visible.Checked));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var file = new ShortcutFile(1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), values);
        var temporaryPath = settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(file, JsonOptions));
        File.Move(temporaryPath, settingsPath, overwrite: true);
        DialogResult = DialogResult.OK;
    }

    private static TextBox EditorTextBox() => new()
    {
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(6),
        ForeColor = Color.FromArgb(16, 39, 72),
        BackColor = Color.White
    };

    private static CheckBox EditorCheckBox() => new()
    {
        Dock = DockStyle.Fill,
        CheckAlign = ContentAlignment.MiddleCenter,
        FlatStyle = FlatStyle.Flat
    };

    private static Button DialogButton(string text, Color color)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(128, 42),
            FlatStyle = FlatStyle.Flat,
            BackColor = color,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
            Margin = new Padding(10, 4, 0, 4)
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private sealed record EditorRow(
        string Id, TextBox Label, TextBox Text, CheckBox Submit, CheckBox Visible);
    private sealed record ShortcutFile(
        int SchemaVersionValue, long UpdatedAt, List<ShortcutValue> Buttons);
    private sealed record ShortcutValue(
        string Id, string Label, string Text, bool Submit, bool Visible);
}
