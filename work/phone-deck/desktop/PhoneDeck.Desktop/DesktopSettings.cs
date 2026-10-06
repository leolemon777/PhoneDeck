using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhoneDeck.Desktop;

internal sealed record DesktopSettings(
    [property: JsonRequired] string DisplayName,
    [property: JsonRequired] string Language,
    [property: JsonRequired] bool AutoInsert,
    [property: JsonRequired] string TapShortcut,
    [property: JsonRequired] string HoldShortcut);
internal sealed record SettingsRequest(string? TargetComputerId, string? Revision, DesktopSettings? Settings);
internal sealed record SettingsOption(string Id, string Label);

// These are identifiers from a fixed table, never commands or arbitrary native key codes.
internal static class VoiceShortcutOptions
{
    internal static readonly SettingsOption[] All = [
        new("Ctrl+Alt+Space", "Ctrl + Alt + Space"), new("Ctrl+Alt+V", "Ctrl + Alt + V"),
        new("Ctrl+Alt+B", "Ctrl + Alt + B"), new("Ctrl+Alt+N", "Ctrl + Alt + N"),
        new("Ctrl+Alt+F8", "Ctrl + Alt + F8"), new("Ctrl+Alt+F9", "Ctrl + Alt + F9"),
        new("Ctrl+Alt+F10", "Ctrl + Alt + F10"), new("Ctrl+Alt+F11", "Ctrl + Alt + F11")];
    internal static bool Contains(string? value) => All.Any(x => x.Id == value);
    internal static uint WindowsKey(string value) => value switch
    {
        "Ctrl+Alt+Space" => 0x20, "Ctrl+Alt+V" => 0x56, "Ctrl+Alt+B" => 0x42, "Ctrl+Alt+N" => 0x4E,
        "Ctrl+Alt+F8" => 0x77, "Ctrl+Alt+F9" => 0x78, "Ctrl+Alt+F10" => 0x79, "Ctrl+Alt+F11" => 0x7A,
        _ => throw new ArgumentException("快捷键不在候选列表中")
    };
    internal static nuint X11Keysym(string value) => value switch
    {
        "Ctrl+Alt+Space" => 0x20, "Ctrl+Alt+V" => 0x76, "Ctrl+Alt+B" => 0x62, "Ctrl+Alt+N" => 0x6E,
        "Ctrl+Alt+F8" => 0xFFC5, "Ctrl+Alt+F9" => 0xFFC6, "Ctrl+Alt+F10" => 0xFFC7, "Ctrl+Alt+F11" => 0xFFC8,
        _ => throw new ArgumentException("快捷键不在候选列表中")
    };
}

internal sealed class DesktopSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly SettingsOption[] Languages = [new("auto", "自动识别"), new("zh", "中文"), new("en", "英语"),
        new("ja", "日语"), new("ko", "韩语"), new("fr", "法语"), new("de", "德语"), new("es", "西班牙语")];
    private readonly object gate = new();
    private readonly string computerId, path;
    private DesktopSettings current;
    internal DesktopSettings Current => Volatile.Read(ref current);
    internal string Revision => RevisionFor(Current);
    internal DesktopSettingsStore(string directory, string computerId, string defaultName)
    {
        this.computerId = computerId; path = Path.Combine(directory, "desktop-settings.json");
        current = new(defaultName, "auto", true, "Ctrl+Alt+Space", "Ctrl+Alt+V");
        if (!File.Exists(path)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(path), Json);
            if (saved is null || saved.SchemaVersion != 1 || saved.ComputerId != computerId || saved.Settings is null)
                throw new InvalidDataException("电脑配置身份或版本无效，请恢复备份");
            current = Validate(saved.Settings);
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        { throw new InvalidDataException("电脑配置损坏，请恢复备份；不会自动覆盖", e); }
    }
    internal object Snapshot(bool busy, string hotkeysStatus)
    {
        var value = Current;
        return new { ok = true, schemaVersion = 1, computerId, revision = RevisionFor(value), busy, settings = value,
            shortcutOptions = VoiceShortcutOptions.All, languageOptions = Languages, hotkeysStatus,
            shortcutHint = "Mac 上 Ctrl / Alt 对应 Control / Option；Wayland 请在系统中绑定 --toggle、--start / --stop。" };
    }
    internal void RequireTarget(string? target)
    {
        if (target != computerId) throw new ArgumentException("配置目标电脑不匹配，请重新读取");
    }
    // apply returns a rollback action for changes made to runtime registration.
    internal void Save(SettingsRequest request, Func<DesktopSettings, Action> apply)
    {
        RequireTarget(request.TargetComputerId);
        var value = Validate(request.Settings);
        lock (gate)
        {
            if (request.Revision != RevisionFor(current)) throw new InvalidOperationException("设置已在其他地方修改，请刷新后重新编辑");
            var rollback = apply(value);
            try { WriteAtomic(value); Volatile.Write(ref current, value); }
            catch { rollback(); throw; }
        }
    }
    private string RevisionFor(DesktopSettings value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new SavedSettings(1, computerId, value), Json)))).ToLowerInvariant();
    internal static DesktopSettings Validate(DesktopSettings? value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.DisplayName) || value.DisplayName.Length > 64 || value.DisplayName.Any(char.IsControl))
            throw new ArgumentException("电脑名称需要 1–64 个可见字符");
        if (!Languages.Any(x => x.Id == value.Language)) throw new ArgumentException("识别语言不在候选列表中");
        if (!VoiceShortcutOptions.Contains(value.TapShortcut) || !VoiceShortcutOptions.Contains(value.HoldShortcut))
            throw new ArgumentException("快捷键不在候选列表中");
        if (value.TapShortcut == value.HoldShortcut) throw new ArgumentException("开始/结束和按住说话需要使用不同快捷键");
        return value with { DisplayName = value.DisplayName.Trim() };
    }
    private void WriteAtomic(DesktopSettings value)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory); PrivateFiles.RestrictDirectory(directory);
        var temporary = Path.Combine(directory, ".desktop-settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, new SavedSettings(1, computerId, value), Json); file.Flush(true); }
            PrivateFiles.RestrictFile(temporary);
            File.Move(temporary, path, true);
        }
        catch (UnauthorizedAccessException e) { throw new IOException("无法保存电脑配置，请检查数据目录权限", e); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed record SavedSettings(int SchemaVersion, string ComputerId, DesktopSettings Settings);
}
