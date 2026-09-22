using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PhoneDeck.ControlCenter;

public partial class AgentShortcutEditorWindow : Window
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
    private readonly List<EditorRow> rows;

    public AgentShortcutEditorWindow(string dataDirectory)
    {
        InitializeComponent();
        settingsPath = Path.Combine(dataDirectory, "agent-shortcuts.json");
        rows =
        [
            new("agentPlan", PlanLabel, PlanText, PlanSubmit, PlanVisible),
            new("agentGoal", GoalLabel, GoalText, GoalSubmit, GoalVisible),
            new("agentCompact", CompactLabel, CompactText, CompactSubmit, CompactVisible),
            new("agentClear", ClearLabel, ClearText, ClearSubmit, ClearVisible)
        ];
        LoadValues();
        SourceInitialized += (_, _) => NativeWindowAppearance.Initialize(this);
        StateChanged += (_, _) => UpdateWindowCorners();
        UpdateWindowCorners();
    }

    private void UpdateWindowCorners() => NativeWindowAppearance.UpdateBorder(this, WindowRoot);

    private void LoadValues()
    {
        ShortcutFile? file = null;
        try
        {
            if (File.Exists(settingsPath))
            {
                file = JsonSerializer.Deserialize<ShortcutFile>(File.ReadAllText(settingsPath), JsonOptions);
            }
        }
        catch
        {
            // 损坏配置会在下一次保存时由新配置替换。
        }

        foreach (var row in rows)
        {
            var value = file?.Buttons.FirstOrDefault(button => button.Id == row.Id);
            var fallback = Defaults.First(item => item.Id == row.Id);
            row.Label.Text = value?.Label ?? fallback.Label;
            row.Text.Text = value?.Text ?? fallback.Text;
            row.Submit.IsChecked = value?.Submit ?? true;
            row.Visible.IsChecked = value?.Visible ?? true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var values = new List<ShortcutValue>();
        foreach (var row in rows)
        {
            var label = row.Label.Text.Trim();
            var text = row.Text.Text.Trim();
            if (label.Length is < 1 or > 24)
            {
                MessageBox.Show(this, "按钮名称必须为 1–24 个字符。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                row.Label.Focus();
                return;
            }
            if (text.Length is < 1 or > 512 || text.Contains('\r') || text.Contains('\n'))
            {
                MessageBox.Show(this, "发送内容必须为 1–512 个字符的单行文本。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                row.Text.Focus();
                return;
            }
            values.Add(new ShortcutValue(row.Id, label, text, row.Submit.IsChecked == true, row.Visible.IsChecked == true));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var file = new ShortcutFile(1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), values);
        var temporaryPath = settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(file, JsonOptions));
        File.Move(temporaryPath, settingsPath, overwrite: true);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private sealed record EditorRow(string Id, TextBox Label, TextBox Text, CheckBox Submit, CheckBox Visible);
    private sealed record ShortcutFile(int SchemaVersionValue, long UpdatedAt, List<ShortcutValue> Buttons);
    private sealed record ShortcutValue(string Id, string Label, string Text, bool Submit, bool Visible);
}
