using System.Text.Json;

/// <summary>语音引擎选择与手动快捷键覆盖，持久化到 data\voice-engine-settings.json。
/// 改动后需重启 PhoneDeck.Server.exe 生效；控制台界面会直接写入本文件。</summary>
internal sealed class VoiceEngineSettings
{
    /// <summary>激活引擎 id（如 typeless、doubao、wetype 或扩展档案的 id）。</summary>
    public string ActiveEngine { get; set; } = VoiceEngineCatalog.DefaultEngineId;

    /// <summary>手动快捷键覆盖：engineId → modeId → 绑定串（如 "Ctrl+Shift+V"）。
    /// 优先级高于引擎配置文件与档案默认值；无配置可读的引擎（如微信输入法）
    /// 必须在这里填写快捷键后受管听写才可用。</summary>
    public Dictionary<string, Dictionary<string, string>>? ShortcutOverrides { get; set; }

    internal string? ShortcutOverrideFor(string engineId, string modeId)
    {
        if (ShortcutOverrides is null)
        {
            return null;
        }
        foreach (var (storedEngineId, engineOverrides) in ShortcutOverrides)
        {
            if (!string.Equals(storedEngineId, engineId, StringComparison.OrdinalIgnoreCase)
                || engineOverrides is null)
            {
                continue;
            }
            foreach (var (storedModeId, binding) in engineOverrides)
            {
                if (string.Equals(storedModeId, modeId, StringComparison.OrdinalIgnoreCase))
                {
                    return binding;
                }
            }
        }
        return null;
    }

    internal static string SettingsPath =>
        Path.Combine(PhoneDeckDataDirectory.Get(), "voice-engine-settings.json");

    internal static VoiceEngineSettings LoadOrCreate()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                var loaded = JsonSerializer.Deserialize<VoiceEngineSettings>(
                    File.ReadAllText(SettingsPath), options);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"读取 voice-engine-settings.json 失败，使用默认设置：{exception.Message}");
        }
        try
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(SettingsPath)!);
            if (!File.Exists(SettingsPath))
            {
                File.WriteAllText(SettingsPath,
                    "{\n"
                    + "  // 语音引擎设置。改动后重启 PhoneDeck.Server.exe 生效。\n"
                    + "  // activeEngine：当前语音转文字引擎，可选内置 typeless/doubao/wetype，\n"
                    + "  // 或 data\\voice-engines\\ 目录中扩展档案的 id。详见 docs/VOICE_ENGINES.md。\n"
                    + "  \"activeEngine\": \"typeless\",\n"
                    + "  // shortcutOverrides：手动指定某引擎某模式的快捷键，优先级最高。\n"
                    + "  // 无配置可读的引擎（如微信输入法）必须在这里填写才能使用，例如：\n"
                    + "  // \"shortcutOverrides\": { \"wetype\": { \"dictation\": \"Ctrl+Shift+V\" } }\n"
                    + "  \"shortcutOverrides\": {}\n"
                    + "}\n");
            }
        }
        catch (Exception)
        {
            // 设置模板写入失败不影响启动。
        }
        return new VoiceEngineSettings();
    }

    internal static void Save(VoiceEngineSettings settings)
    {
        try
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            Console.WriteLine($"写入 voice-engine-settings.json 失败：{exception.Message}");
        }
    }
}
