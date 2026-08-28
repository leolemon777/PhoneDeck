using System.Text.Json;

internal sealed class ServerSettings
{
    public bool UsbWatchdog { get; init; } = true;

    /// <summary>局域网 UDP 发现应答（端口 8767，仅 LocalSubnet 应答，不含令牌）。</summary>
    public bool LanDiscovery { get; init; } = true;

    /// <summary>可选：手动指定 adb.exe 完整路径；留空则依次查找
    /// 程序目录 platform-tools 与 PATH。</summary>
    public string? AdbPath { get; init; }

    internal static ServerSettings LoadOrCreate()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhoneDeck", "server-settings.json");
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
                var loaded = JsonSerializer.Deserialize<ServerSettings>(
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
                    + "  // PhoneDeck 电脑端设置。改动后重启 PhoneDeck.Server.exe 生效。\n"
                    + "  // usbWatchdog：USB 看门狗，自动恢复 adb reverse 隧道，保持手机 USB 通道常连。\n"
                    + "  \"usbWatchdog\": true,\n"
                    + "  // lanDiscovery：UDP 8767 局域网发现应答；只回电脑 ID/名称/端口，不包含令牌。\n"
                    + "  \"lanDiscovery\": true,\n"
                    + "  // adbPath：adb.exe 完整路径；留空则自动查找程序旁 platform-tools 和 PATH。\n"
                    + "  \"adbPath\": null\n"
                    + "}\n");
            }
        }
        catch (Exception)
        {
            // 设置模板写入失败不影响启动。
        }
        return new ServerSettings();
    }
}
