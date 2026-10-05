using System.Security.Cryptography;
using System.Text.Json;

// Receiver.Core：phoneManagedSettingsV1 的平台无关部分（并发闸门、修订号、目标校验、原子写入与请求格式）。
// 各平台在自己的 partial 中实现输入法快捷键校验与登录启动。

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

internal static partial class DesktopConfiguration
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

    internal static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
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

