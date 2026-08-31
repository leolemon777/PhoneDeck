using System.Text.Json;

namespace PhoneDeck.MacReceiver;

internal sealed class MacReceiverSettings
{
    public bool UsbWatchdog { get; init; } = true;
    public bool LanDiscovery { get; init; } = true;
    public string? AdbPath { get; init; }

    internal static MacReceiverSettings LoadOrCreate()
    {
        var path = Path.Combine(PhoneDeckDataDirectory.Get(), "server-settings.json");
        try
        {
            if (File.Exists(path))
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                var loaded = JsonSerializer.Deserialize<MacReceiverSettings>(
                    File.ReadAllText(path), options);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"读取 server-settings.json 失败，使用默认设置：{exception.Message}");
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                File.WriteAllText(path,
                    "{\n"
                    + "  // usbWatchdog：检测 Android 后自动恢复 adb reverse。\n"
                    + "  \"usbWatchdog\": true,\n"
                    + "  // lanDiscovery：在 UDP 8767 回应已配对手机的发现请求。\n"
                    + "  \"lanDiscovery\": true,\n"
                    + "  // adbPath：可选的 adb 完整路径。\n"
                    + "  \"adbPath\": null\n"
                    + "}\n");
            }
        }
        catch (Exception)
        {
            // 设置模板写入失败不影响接收端启动。
        }
        return new MacReceiverSettings();
    }
}
