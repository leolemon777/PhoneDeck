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

    private static readonly object CacheLock = new();
    private static TypelessConfigState? cachedState;
    private static DateTime cachedWriteUtc;
    private static long cachedLength = -1;
    private static long cachedAtTick;
    private const int CacheTtlMs = 30_000;

    /// <summary>一次读取聚合状态；文件未变化且虚拟声卡已就绪时直接复用缓存，减少周期磁盘 IO 与 JSON 解析。</summary>
    internal static TypelessConfigState Read()
    {
        try
        {
            var path = SettingsPath;
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new TypelessConfigState(null, false, null, null, null);
            }
            var now = Environment.TickCount64;
            var writeUtc = info.LastWriteTimeUtc;
            var length = info.Length;
            lock (CacheLock)
            {
                if (cachedState is { UsesVirtualCable: true } cached
                    && writeUtc == cachedWriteUtc
                    && length == cachedLength
                    && now - cachedAtTick < CacheTtlMs)
                {
                    return cached;
                }
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
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
            var isSystemDefault = false;
            if (root.TryGetProperty("selectedMicrophoneDevice", out var selected))
            {
                var deviceId = selected.TryGetProperty("deviceId", out var deviceIdValue)
                    ? deviceIdValue.GetString()
                    : null;
                var label = selected.TryGetProperty("label", out var labelValue)
                    ? labelValue.GetString()
                    : null;
                var description = selected.TryGetProperty("description", out var descriptionValue)
                    ? descriptionValue.GetString()
                    : null;
                isSystemDefault = string.Equals(deviceId, "default", StringComparison.OrdinalIgnoreCase)
                    || (description?.Contains("system default", StringComparison.OrdinalIgnoreCase) == true)
                    || (label?.StartsWith("Auto-detect", StringComparison.OrdinalIgnoreCase) == true);
                if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(description))
                {
                    microphone = $"{label} / {description}";
                }
                else
                {
                    microphone = string.IsNullOrWhiteSpace(label) ? description : label;
                }
            }

            var usesVirtualCable = IsVirtualCableName(microphone);
            if (!usesVirtualCable && isSystemDefault)
            {
                var defaultCaptureName = TryReadSystemDefaultCaptureDeviceName();
                if (IsVirtualCableName(defaultCaptureName))
                {
                    usesVirtualCable = true;
                    microphone = $"Auto-detect → {defaultCaptureName}";
                }
            }

            var state = new TypelessConfigState(
                microphone,
                usesVirtualCable,
                ReadBinding("dictationMode"),
                ReadBinding("translationMode"),
                ReadBinding("askAnythingMode"));
            lock (CacheLock)
            {
                cachedState = state;
                cachedWriteUtc = writeUtc;
                cachedLength = length;
                cachedAtTick = now;
            }
            return state;
        }
        catch (Exception)
        {
            return new TypelessConfigState(null, false, null, null, null);
        }
    }

    private static bool IsVirtualCableName(string? name) =>
        name is not null
        && (name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase));

    private static string? TryReadSystemDefaultCaptureDeviceName()
    {
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(
                NAudio.CoreAudioApi.DataFlow.Capture,
                NAudio.CoreAudioApi.Role.Console);
            var currentName = AudioDeviceNames.Read(device);
            if (IsVirtualCableName(currentName))
            {
                return currentName;
            }

            foreach (var candidate in enumerator.EnumerateAudioEndPoints(
                NAudio.CoreAudioApi.DataFlow.Capture,
                NAudio.CoreAudioApi.DeviceState.Active))
            {
                using (candidate)
                {
                    var candidateName = AudioDeviceNames.Read(candidate);
                    if (IsVirtualCableName(candidateName) && TrySetDefaultCaptureEndpoint(candidate.ID))
                    {
                        return candidateName;
                    }
                }
            }

            return currentName;
        }
        catch
        {
            return null;
        }
    }

    private static bool TrySetDefaultCaptureEndpoint(string deviceId)
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"));
            if (type is null || Activator.CreateInstance(type) is not IPolicyConfig policy)
            {
                return false;
            }
            policy.SetDefaultEndpoint(deviceId, 0);
            policy.SetDefaultEndpoint(deviceId, 1);
            policy.SetDefaultEndpoint(deviceId, 2);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        int GetMixFormat(string pszDeviceName, nint ppFormat);
        int GetDeviceFormat(string pszDeviceName, int bDefault, nint ppFormat);
        int ResetDeviceFormat(string pszDeviceName);
        int SetDeviceFormat(string pszDeviceName, nint pEndpointFormat, nint mixFormat);
        int GetProcessingPeriod(string pszDeviceName, int bDefault, nint pmftDefaultPeriod, nint pmftMinimumPeriod);
        int SetProcessingPeriod(string pszDeviceName, nint pmftPeriod);
        int GetShareMode(string pszDeviceName, nint pMode);
        int SetShareMode(string pszDeviceName, nint mode);
        int GetPropertyValue(string pszDeviceName, nint bFxStore, nint key, nint pv);
        int SetPropertyValue(string pszDeviceName, nint bFxStore, nint key, nint pv);
        int SetDefaultEndpoint([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszDeviceName, int role);
        int SetEndpointVisibility(string pszDeviceName, int bVisible);
    }
}
