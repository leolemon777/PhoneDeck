using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

// A write excludes new input/audio requests; an existing stream holds a use
// lease until its tail drains. No thread-affine lock spans an HTTP await.
internal sealed class ConfigurationGate
{
    private readonly object sync = new();
    private int users;
    private bool writing;
    internal bool EnterUse() { lock (sync) { if (writing) return false; users++; return true; } }
    internal void ExitUse() { lock (sync) users--; }
    internal bool Apply(Func<bool> busy, Action write)
    {
        lock (sync)
        {
            if (writing || users != 0 || busy()) return false;
            writing = true;
        }
        try { write(); return true; }
        finally { lock (sync) writing = false; }
    }
}

internal static class DesktopConfiguration
{
    internal static string Revision<T>(T settings) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(settings)));

    internal static void CheckTarget(string? target, string actual)
    {
        if (!string.Equals(target, actual, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请求目标不是当前电脑，请重新连接");
    }

    internal static void CheckRevision(string? expected, string current)
    {
        if (!string.Equals(expected, current, StringComparison.Ordinal))
            throw new InvalidOperationException("设置已变化，请重新读取后再保存");
    }

    internal static VoiceEngineSettings ValidateVoice(VoiceEngineCatalog catalog, DesktopVoiceRequest request)
    {
        var id = request.ActiveEngine?.Trim().ToLowerInvariant();
        if (id is null || catalog.Find(id) is null) throw new ArgumentException("请选择这台电脑支持的输入法");
        var overrides = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (request.ShortcutOverrides?.Count > catalog.Profiles.Count)
            throw new ArgumentException("输入法配置数量超出限制");
        foreach (var (engineId, bindings) in request.ShortcutOverrides ?? new())
        {
            var profile = catalog.Find(engineId) ?? throw new ArgumentException("未知输入法配置");
            if (bindings is null || bindings.Count > profile.Modes.Length) throw new ArgumentException("无效的输入法模式");
            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (mode, binding) in bindings)
            {
                if (profile.FindMode(mode) is null) throw new ArgumentException("未知的语音模式");
                var value = binding?.Trim() ?? "";
                if (value.Length > 100 || (value.Length > 0 && KeyboardInput.ParseBindingKeys(value) is null))
                    throw new ArgumentException("快捷键无法识别，请使用如 Ctrl+Shift+V 的组合");
                copy.Add(mode, value);
            }
            overrides.Add(profile.Id, copy);
        }
        return new VoiceEngineSettings { ActiveEngine = id, ShortcutOverrides = overrides };
    }

    internal static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true });
                file.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed record DesktopVoiceRequest(string? TargetComputerId, string? Revision,
    string? ActiveEngine, Dictionary<string, Dictionary<string, string>>? ShortcutOverrides);
internal sealed record DesktopConnectionRequest(string? TargetComputerId, string? Revision,
    bool? UsbWatchdog, bool? LanDiscovery, bool? AutoStart);
internal sealed record DesktopTargetRequest(string? TargetComputerId);

internal static class DesktopAutoStart
{
    private const string ValueName = "PhoneDeck Control Center";
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "PhoneDeck.ControlCenter.exe");
    internal static bool Enabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue(ValueName) is string value
                && value.Contains('"' + Executable + '"', StringComparison.OrdinalIgnoreCase);
        }
    }
    internal static void Set(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (enabled && !File.Exists(Executable)) throw new IOException("未找到同目录的桌面接收器，无法启用登录启动");
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (enabled) key.SetValue(ValueName, '"' + Executable + "\" --tray");
        else key.DeleteValue(ValueName, false);
    }
}
