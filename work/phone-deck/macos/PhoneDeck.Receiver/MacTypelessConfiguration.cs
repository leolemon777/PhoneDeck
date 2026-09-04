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
    internal static MacTypelessConfig Load(MacReceiverSettings settings)
    {
        var path = ResolvePath(settings.TypelessSettingsPath);
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
