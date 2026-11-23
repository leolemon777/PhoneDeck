using System.Text.Json;

/// <summary>语音引擎选择与手动快捷键覆盖，持久化到 data\voice-engine-settings.json。
/// 手机通过带目标与版本校验的配置 API 保存并热应用；手工改文件仍需重启。</summary>
internal sealed class VoiceEngineSettings
{
    /// <summary>激活引擎 id；只支持 typeless，保留字段以兼容旧配置文件与手机设置接口。</summary>
    public string ActiveEngine { get; set; } = VoiceEngineCatalog.DefaultEngineId;

    /// <summary>手动快捷键覆盖：engineId → modeId → 绑定串（如 "Ctrl+Shift+V"）。
    /// 优先级高于 Typeless 自己的配置；读不到 Typeless 配置时可在这里填写。</summary>
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
                    + "  // 只支持 Typeless；快捷键默认从 Typeless 自己的设置读取。\n"
                    + "  \"activeEngine\": \"typeless\",\n"
                    + "  // shortcutOverrides：读不到 Typeless 设置时手动指定快捷键，例如：\n"
                    + "  // \"shortcutOverrides\": { \"typeless\": { \"dictation\": \"RightAlt\" } }\n"
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
}
