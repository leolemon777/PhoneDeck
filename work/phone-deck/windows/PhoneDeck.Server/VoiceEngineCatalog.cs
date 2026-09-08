/// <summary>内置与用户扩展的语音引擎档案集合，以及当前激活引擎的选择。
/// 启动时加载一次；修改 voice-engines 目录或 voice-engine-settings.json 后需重启。</summary>
internal sealed class VoiceEngineCatalog
{
    internal const string DefaultEngineId = "typeless";

    // 内置档案。Typeless 是唯一经过真机验证的引擎；豆包/微信输入法的
    // 快捷键与进程名来自公开资料，未经实装核实，均标记 experimental，
    // 用户可用 data\voice-engines\*.json 按 id 覆盖修正。
    private const string TypelessProfileJson = """
        {
          // Typeless（typeless.com）：快捷键与麦克风从其配置文件自动读取。
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
        """;

    private const string DoubaoProfileJson = """
        {
          // 豆包客户端全局语音输入（Ctrl+D 唤醒）。豆包 Windows 版仍在内测，
          // 新版本可能移除或修改该快捷键；失效时请用 voice-engine-settings.json
          // 覆盖，或在本档案同目录放置修正后的 doubao.json。
          "id": "doubao",
          "displayName": "豆包",
          "experimental": true,
          "processNames": ["Doubao"],
          "modes": [
            { "id": "dictation", "label": "语音输入", "keys": "Ctrl+D", "trigger": "toggle" }
          ]
        }
        """;

    private const string WetypeProfileJson = """
        {
          // 微信输入法（WeType）Windows 2.1.3+ 支持语音输入，默认 Fn 键无法
          // 程序注入，且其自定义快捷键必须以 Ctrl/Alt/Shift 开头。请先在微信
          // 输入法设置中把语音快捷键改为如 Ctrl+Shift+V，再填入
          // voice-engine-settings.json 的 shortcutOverrides。
          "id": "wetype",
          "displayName": "微信输入法",
          "experimental": true,
          "processNames": ["WeType", "微信输入法"],
          "modes": [
            { "id": "dictation", "label": "语音输入", "keys": null, "trigger": "hold" }
          ]
        }
        """;

    /// <summary>内置档案（id, JSON）列表，供启动加载与单元测试使用。</summary>
    internal static readonly (string Id, string Json)[] BuiltinProfiles =
    {
        (DefaultEngineId, TypelessProfileJson),
        ("doubao", DoubaoProfileJson),
        ("wetype", WetypeProfileJson)
    };

    private readonly Dictionary<string, VoiceEngineProfile> profiles;

    private VoiceEngineCatalog(
        Dictionary<string, VoiceEngineProfile> profiles,
        VoiceEngineProfile active,
        VoiceEngineSettings settings)
    {
        this.profiles = profiles;
        Active = active;
        Settings = settings;
    }

    /// <summary>当前激活的引擎档案（来自 voice-engine-settings.json）。</summary>
    internal VoiceEngineProfile Active { get; }

    internal VoiceEngineSettings Settings { get; }

    internal IReadOnlyCollection<VoiceEngineProfile> Profiles =>
        profiles.Values.OrderBy(profile => profile.Id).ToArray();

    internal VoiceEngineProfile? Find(string engineId) => profiles.TryGetValue(
        engineId.Trim().ToLowerInvariant(), out var profile) ? profile : null;

    internal static VoiceEngineCatalog Load()
    {
        var profiles = new Dictionary<string, VoiceEngineProfile>(StringComparer.Ordinal);
        foreach (var builtin in BuiltinProfiles)
        {
            var profile = VoiceEngineProfileJson.Parse(
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
                    var profile = VoiceEngineProfileJson.Parse(
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

        var settings = VoiceEngineSettings.LoadOrCreate();
        var activeId = settings.ActiveEngine?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(activeId)
            || !profiles.ContainsKey(activeId))
        {
            if (!string.IsNullOrWhiteSpace(activeId))
            {
                Console.Error.WriteLine(
                    $"voice-engine-settings.json 中的引擎 {activeId} 不存在，回退到 {DefaultEngineId}。");
                activeId = profiles.ContainsKey(DefaultEngineId)
                    ? DefaultEngineId
                    : profiles.Keys.First();
            }
            else
            {
                activeId = profiles.ContainsKey(DefaultEngineId)
                    ? DefaultEngineId
                    : profiles.Keys.First();
            }
        }
        return new VoiceEngineCatalog(profiles, profiles[activeId], settings);
    }
}

/// <summary>进程内共享的引擎档案入口：懒加载一次，读取端全部走这里。</summary>
internal static class VoiceEngines
{
    private static readonly Lazy<VoiceEngineCatalog> LazyCatalog =
        new(VoiceEngineCatalog.Load);

    internal static VoiceEngineCatalog Catalog => LazyCatalog.Value;

    internal static VoiceEngineProfile Active => Catalog.Active;

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
        Active.FindMode(modeId)?.Trigger ?? EngineTriggers.Toggle;

    /// <summary>解析某模式的快捷键绑定串；优先级：手动覆盖 > 引擎配置文件 > 档案默认值。
    /// 返回 null 表示该模式未配置（受管听写会拒绝启动）。</summary>
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
            var fromSettings = TypelessSettingsReader.Read().BindingFor(modeId);
            if (!string.IsNullOrWhiteSpace(fromSettings))
            {
                return fromSettings;
            }
            // Typeless 听写保留官方 Windows 默认键 RightAlt，其余模式没有可靠默认值。
            return modeId == "dictation" ? "RightAlt" : null;
        }
        return mode.Keys;
    }

    internal static bool IsModeConfigured(string modeId) =>
        ResolveBinding(modeId) is not null;

    /// <summary>把某模式的绑定串解析为修饰键在前的虚拟键码序列。</summary>
    internal static ushort[] ResolveKeysOrThrow(string modeId)
    {
        var binding = ResolveBinding(modeId)
            ?? throw new ArgumentException(
                $"{ActiveDisplayName} 未配置「{LabelOf(modeId)}」模式的快捷键，请先在电脑端配置");
        var keys = KeyboardInput.ParseBindingKeys(binding);
        if (keys is null || keys.Length == 0)
        {
            throw new ArgumentException(
                $"{ActiveDisplayName}「{LabelOf(modeId)}」模式快捷键无法识别：{binding}");
        }
        return KeyboardInput.OrderModifiersFirst(keys);
    }

    /// <summary>某模式快捷键的显示名数组（如 ["CTRL","SHIFT","S"]）；未配置返回 null。</summary>
    internal static string[]? ModeKeyNames(string modeId) =>
        KeyboardInput.BindingKeyNames(ResolveBinding(modeId));

    /// <summary>引擎所选麦克风描述；无可读配置的引擎返回 null。</summary>
    internal static string? MicrophoneDescription =>
        string.Equals(Active.SettingsReader, "typeless", StringComparison.Ordinal)
            ? TypelessSettingsReader.Read().Microphone
            : null;

    /// <summary>引擎输入设备是否已选虚拟声卡；null 表示无法校验（不阻断，仅提示）。</summary>
    internal static bool? UsesVirtualCable =>
        string.Equals(Active.SettingsReader, "typeless", StringComparison.Ordinal)
            ? TypelessSettingsReader.Read().UsesVirtualCable
            : null;

    internal static VoiceEngineSnapshot BuildSnapshot(bool? capturing)
    {
        var engine = Active;
        var modes = engine.Modes
            .Select(mode => new EngineModeSnapshot(
                mode.Id,
                mode.Label ?? mode.Id,
                TriggerFor(mode.Id),
                IsModeConfigured(mode.Id),
                ModeKeyNames(mode.Id)))
            .ToArray();
        return new VoiceEngineSnapshot(
            engine.Id,
            engine.DisplayName,
            engine.Experimental,
            capturing,
            MicrophoneDescription,
            UsesVirtualCable,
            modes);
    }
}

/// <summary>一次诊断快照中的引擎状态。</summary>
internal sealed record VoiceEngineSnapshot(
    string Id,
    string DisplayName,
    bool Experimental,
    bool? Capturing,
    string? Microphone,
    bool? UsesVirtualCable,
    EngineModeSnapshot[] Modes);

internal sealed record EngineModeSnapshot(
    string Id,
    string Label,
    string Trigger,
    bool Configured,
    string[]? Keys);
