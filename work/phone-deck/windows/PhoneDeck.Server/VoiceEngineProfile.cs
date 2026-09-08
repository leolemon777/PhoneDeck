using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

/// <summary>引擎触发方式常量。</summary>
internal static class EngineTriggers
{
    /// <summary>按一下快捷键开始，再按一下结束（Typeless 风格）。</summary>
    internal const string Toggle = "toggle";

    /// <summary>按住快捷键说话，松开结束（微信输入法等“按住说话”风格）；
    /// 受管会话开始时按下并保持，结束时释放。</summary>
    internal const string Hold = "hold";
}

/// <summary>语音引擎的一种工作模式（如 Typeless 的听写/翻译/问答）。</summary>
internal sealed record VoiceEngineMode
{
    /// <summary>模式 id（小写，如 dictation），对应手机端 /api/dictation 的 mode 参数。</summary>
    public required string Id { get; init; }

    /// <summary>模式的默认显示名（中文）；null 时手机端显示 id。</summary>
    public string? Label { get; init; }

    /// <summary>Windows 默认快捷键绑定串（如 "Ctrl+D"、"RightAlt"）；
    /// null 表示没有可靠默认值，必须先在电脑端手动配置。</summary>
    public string? Keys { get; init; }

    /// <summary>macOS 默认快捷键绑定串；null 表示 macOS 端无默认值。</summary>
    public string? MacKeys { get; init; }

    /// <summary>触发方式：toggle 或 hold，见 EngineTriggers。</summary>
    public string Trigger { get; init; } = EngineTriggers.Toggle;
}

/// <summary>语音引擎档案：描述一个第三方语音转文字软件如何被触发与探测。
/// 内置档案见 VoiceEngineCatalog；用户可在 data\voice-engines\*.json 中
/// 按 id 覆盖内置档案或新增引擎，无需修改代码。</summary>
internal sealed record VoiceEngineProfile
{
    /// <summary>引擎 id（小写），如 typeless、doubao、wetype。</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>实验性档案：快捷键/进程名未在真机上核实，可能随第三方版本变动。</summary>
    public bool Experimental { get; init; }

    /// <summary>录音状态探测时匹配的进程名（忽略大小写；相等或包含即命中）。
    /// 语音功能在子进程中的引擎（如输入法）可只写主进程名。</summary>
    public required string[] ProcessNames { get; init; }

    /// <summary>配置读取器 id；目前仅 "typeless"（读
    /// %APPDATA%\Typeless.exe\app-settings.json 自动获取快捷键与麦克风）。
    /// null 表示该引擎无可读配置，快捷键来自档案默认值或手动覆盖。</summary>
    public string? SettingsReader { get; init; }

    /// <summary>受管听写要求该引擎的输入设备选择虚拟声卡（CABLE Output）。</summary>
    public bool RequiresVirtualCable { get; init; } = true;

    public required VoiceEngineMode[] Modes { get; init; }

    internal bool VerifiesMicrophone => SettingsReader is not null;

    internal VoiceEngineMode? FindMode(string modeId) => Modes.FirstOrDefault(
        mode => string.Equals(mode.Id, modeId, StringComparison.OrdinalIgnoreCase));

    internal string PrimaryModeId => Modes[0].Id;

    internal IReadOnlyList<string> ModeIds => Modes.Select(mode => mode.Id).ToArray();
}

/// <summary>引擎档案 JSON 解析与校验。档案格式见 docs/VOICE_ENGINES.md。</summary>
internal static class VoiceEngineProfileJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    internal static VoiceEngineProfile Parse(string json, string sourceDescription)
    {
        VoiceEngineProfile profile;
        try
        {
            profile = JsonSerializer.Deserialize<VoiceEngineProfile>(json, Options)
                ?? throw new JsonException("档案内容为空");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                $"{sourceDescription} 不是合法的语音引擎档案：{exception.Message}", exception);
        }
        return NormalizeAndValidate(profile, sourceDescription);
    }

    private static VoiceEngineProfile NormalizeAndValidate(
        VoiceEngineProfile profile, string sourceDescription)
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
        var modes = new List<VoiceEngineMode>(profile.Modes.Length);
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
            var trigger = (mode!.Trigger ?? EngineTriggers.Toggle).Trim().ToLowerInvariant();
            if (trigger is not (EngineTriggers.Toggle or EngineTriggers.Hold))
            {
                Fail($"mode「{modeId}」的 trigger 必须是 toggle 或 hold");
            }
            modes.Add(mode with
            {
                Id = modeId!,
                Label = NullIfWhiteSpace(mode.Label),
                Keys = NullIfWhiteSpace(mode.Keys),
                MacKeys = NullIfWhiteSpace(mode.MacKeys),
                Trigger = trigger
            });
        }
        return profile with
        {
            Id = id!,
            DisplayName = displayName!,
            ProcessNames = processNames,
            SettingsReader = NullIfWhiteSpace(profile.SettingsReader),
            Modes = modes.ToArray()
        };
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
