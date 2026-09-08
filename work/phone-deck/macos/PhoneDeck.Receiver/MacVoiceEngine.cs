using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace PhoneDeck.MacReceiver;

/// <summary>引擎触发方式常量（与 Windows 端档案格式保持一致）。</summary>
internal static class MacEngineTriggers
{
    internal const string Toggle = "toggle";
    internal const string Hold = "hold";
}

/// <summary>语音引擎的一种工作模式。keys 为 Windows 默认绑定（本端忽略），
/// macKeys 为 macOS 默认绑定；null 表示无默认值，必须手动配置。</summary>
internal sealed record MacEngineMode(
    string Id,
    string? Label,
    string? Keys,
    string? MacKeys,
    string Trigger);

internal sealed record MacVoiceEngineProfile(
    string Id,
    string DisplayName,
    bool Experimental,
    string[] ProcessNames,
    string? SettingsReader,
    bool RequiresVirtualCable,
    MacEngineMode[] Modes)
{
    internal bool VerifiesMicrophone => SettingsReader is not null;

    internal MacEngineMode? FindMode(string modeId) => Modes.FirstOrDefault(
        mode => string.Equals(mode.Id, modeId, StringComparison.OrdinalIgnoreCase));

    internal string PrimaryModeId => Modes[0].Id;

    internal IReadOnlyList<string> ModeIds => Modes.Select(mode => mode.Id).ToArray();
}

/// <summary>引擎档案 JSON 解析与校验，格式与 Windows 端共用（见 docs/VOICE_ENGINES.md）。</summary>
internal static class MacVoiceEngineProfileJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    internal static MacVoiceEngineProfile Parse(string json, string sourceDescription)
    {
        MacVoiceEngineProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<MacVoiceEngineProfile>(json, Options);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                $"{sourceDescription} 不是合法的语音引擎档案：{exception.Message}", exception);
        }
        if (profile is null)
        {
            throw new ArgumentException($"{sourceDescription} 引擎档案内容为空");
        }
        return NormalizeAndValidate(profile, sourceDescription);
    }

    private static MacVoiceEngineProfile NormalizeAndValidate(
        MacVoiceEngineProfile profile, string sourceDescription)
    {
        [DoesNotReturn]
        void Fail(string reason) => throw new ArgumentException(
            $"{sourceDescription} 引擎档案无效：{reason}");

        var id = profile.Id?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(id))
        {
            Fail("缺少 id");
        }
        var displayName = profile.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            Fail("缺少 displayName");
        }
        var processNames = (profile.ProcessNames ?? Array.Empty<string>())
            .Select(name => name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToArray();
        if (processNames.Length == 0)
        {
            Fail("processNames 至少需要一个进程名（用于录音状态探测）");
        }
        if (profile.Modes is null || profile.Modes.Length is < 1 or > 6)
        {
            Fail("modes 必须包含 1–6 个模式");
        }
        var modes = new List<MacEngineMode>(profile.Modes.Length);
        var seenModeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mode in profile.Modes)
        {
            var modeId = mode?.Id?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(modeId))
            {
                Fail("mode 缺少 id");
            }
            if (!seenModeIds.Add(modeId!))
            {
                Fail($"mode id 重复：{modeId}");
            }
            var trigger = (mode!.Trigger ?? MacEngineTriggers.Toggle).Trim().ToLowerInvariant();
            if (trigger is not (MacEngineTriggers.Toggle or MacEngineTriggers.Hold))
            {
                Fail($"mode「{modeId}」的 trigger 必须是 toggle 或 hold");
            }
            modes.Add(new MacEngineMode(
                modeId!,
                NullIfWhiteSpace(mode.Label),
                NullIfWhiteSpace(mode.Keys),
                NullIfWhiteSpace(mode.MacKeys),
                trigger));
        }
        return new MacVoiceEngineProfile(
            id!,
            displayName!,
            profile.Experimental,
            processNames,
            NullIfWhiteSpace(profile.SettingsReader),
            profile.RequiresVirtualCable,
            modes.ToArray());
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>语音引擎选择与手动快捷键覆盖（voice-engine-settings.json），
/// 文件格式与 Windows 端一致；改动后需重启接收端。</summary>
internal sealed class MacVoiceEngineSettings
{
    public string ActiveEngine { get; set; } = MacVoiceEngineCatalog.DefaultEngineId;

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

    internal static MacVoiceEngineSettings LoadOrCreate()
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
                var loaded = JsonSerializer.Deserialize<MacVoiceEngineSettings>(
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
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            if (!File.Exists(SettingsPath))
            {
                File.WriteAllText(SettingsPath,
                    "{\n"
                    + "  // 语音引擎设置。改动后重启 PhoneDeck.Receiver 生效。\n"
                    + "  // activeEngine：typeless / doubao / wetype 或 voice-engines 目录扩展档案的 id。\n"
                    + "  \"activeEngine\": \"typeless\",\n"
                    + "  // shortcutOverrides：手动指定快捷键，优先级最高。例如：\n"
                    + "  // \"shortcutOverrides\": { \"doubao\": { \"dictation\": \"Control+D\" } }\n"
                    + "  \"shortcutOverrides\": {}\n"
                    + "}\n");
            }
        }
        catch (Exception)
        {
            // 设置模板写入失败不影响启动。
        }
        return new MacVoiceEngineSettings();
    }
}

/// <summary>内置与扩展（~/…/PhoneDeck/voice-engines/*.json）引擎档案集合。</summary>
internal sealed class MacVoiceEngineCatalog
{
    internal const string DefaultEngineId = "typeless";

    internal static readonly (string Id, string Json)[] BuiltinProfiles =
    {
        (DefaultEngineId, """
            {
              // Typeless：快捷键与麦克风从其 app-settings.json 自动读取
              // （候选目录见 MacTypelessConfiguration.ResolvePath）。
              "id": "typeless",
              "displayName": "Typeless",
              "processNames": ["Typeless"],
              "settingsReader": "typeless",
              "modes": [
                { "id": "dictation", "label": "听写", "trigger": "toggle" },
                { "id": "translation", "label": "翻译", "trigger": "toggle" },
                { "id": "ask", "label": "问答", "trigger": "toggle" }
              ]
            }
            """),
        ("doubao", """
            {
              // 豆包 macOS 版语音输入默认按住 Fn；Fn 无法程序注入，
              // 需在豆包设置中改为可注入的组合键后填入 shortcutOverrides。
              "id": "doubao",
              "displayName": "豆包",
              "experimental": true,
              "processNames": ["Doubao"],
              "modes": [
                { "id": "dictation", "label": "语音输入", "trigger": "hold" }
              ]
            }
            """),
        ("wetype", """
            {
              // 微信输入法（WeType）macOS 版语音输入默认 Fn，无法程序注入；
              // 需在其设置中改为可注入的组合键后填入 shortcutOverrides。
              "id": "wetype",
              "displayName": "微信输入法",
              "experimental": true,
              "processNames": ["WeType", "微信输入法"],
              "modes": [
                { "id": "dictation", "label": "语音输入", "trigger": "hold" }
              ]
            }
            """)
    };

    private readonly Dictionary<string, MacVoiceEngineProfile> profiles;

    private MacVoiceEngineCatalog(
        Dictionary<string, MacVoiceEngineProfile> profiles,
        MacVoiceEngineProfile active,
        MacVoiceEngineSettings settings,
        MacReceiverSettings receiverSettings)
    {
        this.profiles = profiles;
        Active = active;
        Settings = settings;
        ReceiverSettings = receiverSettings;
    }

    internal MacVoiceEngineProfile Active { get; }

    internal MacVoiceEngineSettings Settings { get; }

    /// <summary>server-settings.json（含旧版 typelessSettingsPath/typelessShortcuts 覆盖）。</summary>
    internal MacReceiverSettings ReceiverSettings { get; }

    internal IReadOnlyCollection<MacVoiceEngineProfile> Profiles =>
        profiles.Values.OrderBy(profile => profile.Id).ToArray();

    internal MacVoiceEngineProfile? Find(string engineId) => profiles.TryGetValue(
        engineId.Trim().ToLowerInvariant(), out var profile) ? profile : null;

    internal static MacVoiceEngineCatalog Load()
    {
        var profiles = new Dictionary<string, MacVoiceEngineProfile>(StringComparer.Ordinal);
        foreach (var builtin in BuiltinProfiles)
        {
            var profile = MacVoiceEngineProfileJson.Parse(
                builtin.Json, $"内置引擎档案 {builtin.Id}");
            profiles[profile.Id] = profile;
        }

        var extensionDirectory = Path.Combine(
            PhoneDeckDataDirectory.Get(), "voice-engines");
        if (Directory.Exists(extensionDirectory))
        {
            foreach (var file in Directory.GetFiles(extensionDirectory, "*.json")
                         .OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var profile = MacVoiceEngineProfileJson.Parse(
                        File.ReadAllText(file), $"扩展引擎档案 {Path.GetFileName(file)}");
                    var replaced = profiles.ContainsKey(profile.Id);
                    profiles[profile.Id] = profile;
                    Console.WriteLine(
                        $"语音引擎档案 {(replaced ? "已覆盖" : "已新增")}：{profile.Id}（{Path.GetFileName(file)}）");
                }
                catch (ArgumentException exception)
                {
                    Console.Error.WriteLine($"跳过无效的扩展引擎档案：{exception.Message}");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"读取扩展引擎档案 {Path.GetFileName(file)} 失败：{exception.Message}");
                }
            }
        }

        var receiverSettings = MacReceiverSettings.LoadOrCreate();
        var settings = MacVoiceEngineSettings.LoadOrCreate();
        var activeId = settings.ActiveEngine?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(activeId) || !profiles.ContainsKey(activeId))
        {
            if (!string.IsNullOrWhiteSpace(activeId))
            {
                Console.Error.WriteLine(
                    $"voice-engine-settings.json 中的引擎 {activeId} 不存在，回退到 {DefaultEngineId}。");
            }
            activeId = profiles.ContainsKey(DefaultEngineId)
                ? DefaultEngineId
                : profiles.Keys.First();
        }
        return new MacVoiceEngineCatalog(profiles, profiles[activeId], settings, receiverSettings);
    }
}

/// <summary>进程内共享的引擎档案入口：懒加载一次，读取端全部走这里。</summary>
internal static class MacVoiceEngines
{
    private static readonly Lazy<MacVoiceEngineCatalog> LazyCatalog =
        new(MacVoiceEngineCatalog.Load);

    internal static MacVoiceEngineCatalog Catalog => LazyCatalog.Value;

    internal static MacVoiceEngineProfile Active => Catalog.Active;

    internal static string ActiveDisplayName => Active.DisplayName;

    /// <summary>规范化并校验 mode 参数；空值回退到引擎的第一个模式。</summary>
    internal static string NormalizeMode(string? mode)
    {
        var normalized = string.IsNullOrWhiteSpace(mode)
            ? Active.PrimaryModeId
            : mode.Trim().ToLowerInvariant();
        if (Active.FindMode(normalized) is null)
        {
            throw new ArgumentException($"未知的语音引擎模式：{mode}");
        }
        return normalized;
    }

    internal static string LabelOf(string modeId) =>
        Active.FindMode(modeId)?.Label ?? modeId;

    internal static string TriggerFor(string modeId) =>
        Active.FindMode(modeId)?.Trigger ?? MacEngineTriggers.Toggle;

    /// <summary>解析某模式的快捷键绑定串；优先级：手动覆盖 > 引擎配置文件 > 档案默认值。</summary>
    internal static string? ResolveBinding(string modeId)
    {
        var mode = Active.FindMode(modeId)
            ?? throw new ArgumentException($"未知的语音引擎模式：{modeId}");
        var overrideBinding = Catalog.Settings.ShortcutOverrideFor(Active.Id, modeId);
        if (!string.IsNullOrWhiteSpace(overrideBinding))
        {
            return overrideBinding;
        }
        if (string.Equals(Active.SettingsReader, "typeless", StringComparison.Ordinal))
        {
            var fromSettings = MacTypelessConfiguration.Load(Catalog.ReceiverSettings)
                .BindingFor(modeId);
            if (!string.IsNullOrWhiteSpace(fromSettings))
            {
                return fromSettings;
            }
            // Typeless macOS 听写保留旧版默认键 Fn，其余模式没有可靠默认值。
            return modeId == "dictation" ? "Fn" : null;
        }
        return mode.MacKeys;
    }

    internal static bool IsModeConfigured(string modeId) =>
        ResolveBinding(modeId) is not null;

    internal static string[]? ModeKeyNames(string modeId) =>
        SplitBinding(ResolveBinding(modeId));

    /// <summary>引擎所选麦克风描述；无可读配置的引擎返回 null。</summary>
    internal static string? MicrophoneDescription =>
        string.Equals(Active.SettingsReader, "typeless", StringComparison.Ordinal)
            ? MacTypelessConfiguration.Load(Catalog.ReceiverSettings).Microphone
            : null;

    /// <summary>引擎输入设备是否已选 BlackHole；null 表示无法校验（不阻断，仅提示）。</summary>
    internal static bool? UsesVirtualCable =>
        string.Equals(Active.SettingsReader, "typeless", StringComparison.Ordinal)
            ? MacTypelessConfiguration.Load(Catalog.ReceiverSettings).UsesBlackHole
            : null;

    internal static string[]? SplitBinding(string? binding) =>
        string.IsNullOrWhiteSpace(binding)
            ? null
            : binding.Split('+', StringSplitOptions.TrimEntries
                | StringSplitOptions.RemoveEmptyEntries);
}
