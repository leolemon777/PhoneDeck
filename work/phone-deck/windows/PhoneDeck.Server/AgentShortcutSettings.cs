using System.Text.Json;

internal sealed class AgentShortcutSettings
{
    internal const int SchemaVersion = 1;
    private static readonly string[] SupportedIds =
    {
        "agentPlan", "agentGoal", "agentCompact", "agentClear"
    };

    public int SchemaVersionValue { get; init; } = SchemaVersion;
    public long UpdatedAt { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public List<AgentShortcutDefinition> Buttons { get; init; } = Defaults();

    internal static string PathForCurrentDataDirectory() =>
        Path.Combine(PhoneDeckDataDirectory.Get(), "agent-shortcuts.json");

    internal static AgentShortcutSettings LoadOrCreate()
    {
        var path = PathForCurrentDataDirectory();
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AgentShortcutSettings>(
                    File.ReadAllText(path), JsonOptions());
                if (loaded is not null && TryValidate(loaded, out _))
                {
                    return loaded;
                }
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"读取 agent-shortcuts.json 失败，使用默认指令：{exception.Message}");
        }

        var defaults = new AgentShortcutSettings();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions()));
        return defaults;
    }

    internal static bool TryValidate(AgentShortcutSettings value, out string error)
    {
        if (value.SchemaVersionValue != SchemaVersion)
        {
            error = "不支持的 Agent 指令配置版本";
            return false;
        }
        if (value.Buttons.Count != SupportedIds.Length)
        {
            error = "Agent 指令数量不正确";
            return false;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var button in value.Buttons)
        {
            if (!SupportedIds.Contains(button.Id, StringComparer.Ordinal) || !ids.Add(button.Id))
            {
                error = "Agent 指令包含未知或重复 ID";
                return false;
            }
            if (string.IsNullOrWhiteSpace(button.Label) || button.Label.Trim().Length > 24)
            {
                error = "Agent 按钮名称必须为 1–24 个字符";
                return false;
            }
            if (string.IsNullOrWhiteSpace(button.Text) || button.Text.Trim().Length > 512 ||
                button.Text.Contains('\r') || button.Text.Contains('\n'))
            {
                error = "Agent 指令必须为 1–512 个字符的单行文本";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    internal static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static List<AgentShortcutDefinition> Defaults() => new()
    {
        new("agentPlan", "规划", "/plan", true, true),
        new("agentGoal", "目标", "/goal", true, true),
        new("agentCompact", "压缩上下文", "/compact", true, true),
        new("agentClear", "新会话", "/clear", true, true)
    };
}

internal sealed record AgentShortcutDefinition(
    string Id,
    string Label,
    string Text,
    bool Submit,
    bool Visible);
