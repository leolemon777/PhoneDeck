using System.Text.Json;

namespace PhoneDeck.MacReceiver;

internal sealed record MacTypelessConfig(
    string? SettingsPath,
    string? Microphone,
    bool UsesBlackHole,
    string? DictationBinding,
    string? TranslationBinding,
    string? AskBinding,
    string? Error)
{
    internal string? BindingFor(string mode) => mode switch
    {
        "translation" => TranslationBinding,
        "ask" => AskBinding,
        _ => DictationBinding
    };
}

internal static class MacTypelessConfiguration
{
    private sealed class CacheEntry
    {
        public MacTypelessConfig? Config;
        public string? Path;
        public DateTime WriteTime;
        public long Length;
        public long CheckedAt = long.MinValue;
    }

    /// <summary>按设置对象各存一份（引擎目录与 Program 持有不同的设置实例），设置被替换后旧条目随之回收。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MacReceiverSettings, CacheEntry> Cache = new();

    /// <summary>
    /// 健康检查每次会读十几次快捷键/麦克风字段：同一设置对象 1 秒内直接复用，
    /// 超过 1 秒只看文件修改时间与大小，文件变了才重新解析。
    /// </summary>
    internal static MacTypelessConfig Load(MacReceiverSettings settings)
    {
        var entry = Cache.GetValue(settings, _ => new CacheEntry());
        var now = Environment.TickCount64;
        lock (entry)
        {
            if (entry.Config is not null && now - entry.CheckedAt < 1_000)
            {
                return entry.Config;
            }
            var path = ResolvePath(settings.TypelessSettingsPath);
            DateTime writeTime = default;
            long length = -1;
            if (path is not null)
            {
                try
                {
                    var info = new FileInfo(path);
                    writeTime = info.LastWriteTimeUtc;
                    length = info.Length;
                }
                catch (IOException)
                {
                    // 交给 LoadUncached 报错。
                }
            }
            if (entry.Config is null || entry.Path != path
                || entry.WriteTime != writeTime || entry.Length != length)
            {
                entry.Config = LoadUncached(settings, path);
                entry.Path = path;
                entry.WriteTime = writeTime;
                entry.Length = length;
            }
            entry.CheckedAt = now;
            return entry.Config;
        }
    }

    private static MacTypelessConfig LoadUncached(MacReceiverSettings settings, string? path)
    {
        if (path is null)
        {
            return ApplyOverrides(
                new MacTypelessConfig(null, null, false, null, null, null,
                    "未找到 Typeless app-settings.json"),
                settings.TypelessShortcuts);
        }
        try
        {
            return Parse(File.ReadAllText(path), path, settings.TypelessShortcuts);
        }
        catch (Exception exception)
        {
            return ApplyOverrides(
                new MacTypelessConfig(path, null, false, null, null, null,
                    "读取 Typeless 配置失败：" + exception.Message),
                settings.TypelessShortcuts);
        }
    }

    internal static string? ResolvePath(string? configuredPath, string? applicationData = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expanded = configuredPath.StartsWith("~/", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    configuredPath[2..])
                : configuredPath;
            if (Directory.Exists(expanded))
            {
                expanded = Path.Combine(expanded, "app-settings.json");
            }
            return File.Exists(expanded) ? Path.GetFullPath(expanded) : null;
        }
        var root = applicationData
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] candidates =
        [
            Path.Combine(root, "Typeless", "app-settings.json"),
            Path.Combine(root, "Typeless.exe", "app-settings.json"),
            Path.Combine(root, "typeless", "app-settings.json"),
            Path.Combine(root, "com.typeless.app", "app-settings.json")
        ];
        var direct = candidates.FirstOrDefault(File.Exists);
        if (direct is not null || !Directory.Exists(root))
        {
            return direct;
        }
        try
        {
            return Directory.EnumerateDirectories(root)
                .Where(directory => Path.GetFileName(directory)
                    .Contains("Typeless", StringComparison.OrdinalIgnoreCase))
                .Select(directory => Path.Combine(directory, "app-settings.json"))
                .FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }

    internal static MacTypelessConfig Parse(
        string json,
        string path,
        TypelessShortcutOverrides? overrides = null)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        var root = document.RootElement;
        string? Binding(string propertyName)
        {
            if (!root.TryGetProperty("featureShortcutBindings", out var bindings)
                || !bindings.TryGetProperty(propertyName, out var mode))
            {
                return null;
            }
            if (mode.ValueKind == JsonValueKind.Array && mode.GetArrayLength() > 0)
            {
                return Normalize(mode[0].GetString());
            }
            return mode.ValueKind == JsonValueKind.String
                ? Normalize(mode.GetString()) : null;
        }

        string? microphone = null;
        if (root.TryGetProperty("selectedMicrophoneDevice", out var selected))
        {
            if (selected.ValueKind == JsonValueKind.String)
            {
                microphone = Normalize(selected.GetString());
            }
            else if (selected.ValueKind == JsonValueKind.Object)
            {
                var label = selected.TryGetProperty("label", out var labelValue)
                    ? Normalize(labelValue.GetString()) : null;
                var description = selected.TryGetProperty("description", out var descriptionValue)
                    ? Normalize(descriptionValue.GetString()) : null;
                microphone = label is not null && description is not null
                    ? $"{label} / {description}" : label ?? description;
            }
        }
        var config = new MacTypelessConfig(
            path,
            microphone,
            microphone?.Contains("BlackHole 2ch", StringComparison.OrdinalIgnoreCase) == true,
            Binding("dictationMode"),
            Binding("translationMode"),
            Binding("askAnythingMode"),
            null);
        return ApplyOverrides(config, overrides);
    }

    private static MacTypelessConfig ApplyOverrides(
        MacTypelessConfig config,
        TypelessShortcutOverrides? overrides)
    {
        if (overrides is null)
        {
            return config;
        }
        return config with
        {
            DictationBinding = Normalize(overrides.Dictation) ?? config.DictationBinding,
            TranslationBinding = Normalize(overrides.Translation) ?? config.TranslationBinding,
            AskBinding = Normalize(overrides.Ask) ?? config.AskBinding
        };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
