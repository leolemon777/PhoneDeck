using System.Text.Json;

/// <summary>Typeless（typeless.com）配置读取器：解析
/// %APPDATA%\Typeless.exe\app-settings.json 中的快捷键绑定与所选麦克风。
/// Typeless 未安装、配置被占用或格式变化时按未配置处理。</summary>
internal static class TypelessSettingsReader
{
    /// <summary>Typeless app-settings.json 一次读取得到的配置状态。</summary>
    internal sealed record TypelessConfigState(
        string? Microphone,
        bool UsesVirtualCable,
        string? DictationBinding,
        string? TranslationBinding,
        string? AskBinding)
    {
        internal string? BindingFor(string modeId) => modeId switch
        {
            "translation" => TranslationBinding,
            "ask" => AskBinding,
            _ => DictationBinding
        };
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Typeless.exe", "app-settings.json");

    /// <summary>一次读取聚合状态；读取失败返回全空状态（听写绑定由调用方回退默认键）。</summary>
    internal static TypelessConfigState Read()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var root = document.RootElement;

            string? ReadBinding(string propertyName)
            {
                if (root.TryGetProperty("featureShortcutBindings", out var bindings)
                    && bindings.TryGetProperty(propertyName, out var mode)
                    && mode.ValueKind == JsonValueKind.Array
                    && mode.GetArrayLength() > 0)
                {
                    var binding = mode[0].GetString();
                    return string.IsNullOrWhiteSpace(binding) ? null : binding;
                }
                return null;
            }

            string? microphone = null;
            if (root.TryGetProperty("selectedMicrophoneDevice", out var selected))
            {
                var label = selected.TryGetProperty("label", out var labelValue)
                    ? labelValue.GetString()
                    : null;
                var description = selected.TryGetProperty("description", out var descriptionValue)
                    ? descriptionValue.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(description))
                {
                    microphone = $"{label} / {description}";
                }
                else
                {
                    microphone = string.IsNullOrWhiteSpace(label) ? description : label;
                }
            }

            var usesVirtualCable = microphone is not null
                && (microphone.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)
                    || microphone.Contains(
                        "VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase));

            return new TypelessConfigState(
                microphone,
                usesVirtualCable,
                ReadBinding("dictationMode"),
                ReadBinding("translationMode"),
                ReadBinding("askAnythingMode"));
        }
        catch (Exception)
        {
            return new TypelessConfigState(null, false, null, null, null);
        }
    }
}
