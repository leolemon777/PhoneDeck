using System.Collections.Concurrent;
using System.Text.Json.Nodes;

/// <summary>
/// 最近来过请求的手机（内存态，不落盘），供本机状态页与 Windows 托盘显示“已连接几台手机、走 USB 还是 Wi-Fi”。
/// 只记录手机 App 的请求（带 X-PhoneDeck-Foreground 头或 Android HTTP 栈的 User-Agent），
/// 本机状态页、托盘自身轮询 8765 不算手机。10 秒内有请求视为在线。
/// </summary>
internal sealed class PhonePresence
{
    internal const long ActiveWindowMs = 10_000;
    private const int MaxEntries = 32;

    private sealed record Entry(string? ClientId, string Transport, string? Device, long LastSeen);

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    /// <summary>Kestrel 中间件里调用：localPort 8765 = USB 回环，8766 = Wi-Fi。</summary>
    internal void Observe(int localPort, string? clientId, string? userAgent, bool hasPhoneHeader, string? device)
    {
        if (!hasPhoneHeader && (userAgent is null || !userAgent.StartsWith("Dalvik", StringComparison.Ordinal)))
        {
            return;
        }
        var transport = localPort == 8766 ? "wifi" : "usb";
        var key = transport + ":" + (clientId ?? "");
        var cleaned = device is { Length: > 0 and <= 64 }
            ? new string(device.Where(c => !char.IsControl(c)).ToArray())
            : null;
        entries[key] = new Entry(clientId, transport, cleaned, Environment.TickCount64);
        if (entries.Count > MaxEntries)
        {
            var oldest = entries.OrderBy(pair => pair.Value.LastSeen).First().Key;
            entries.TryRemove(oldest, out _);
        }
    }

    /// <summary>最近的手机，新到旧；label 由调用方按凭据记录解析（逐手机凭据显示配对时的手机名）。</summary>
    internal JsonArray Snapshot(Func<string?, string?> labelFor)
    {
        var now = Environment.TickCount64;
        var array = new JsonArray();
        foreach (var entry in entries.Values.OrderByDescending(value => value.LastSeen))
        {
            var ago = now - entry.LastSeen;
            array.Add((JsonNode)new JsonObject
            {
                ["clientId"] = entry.ClientId,
                ["label"] = labelFor(entry.ClientId) ?? entry.Device,
                ["device"] = entry.Device,
                ["transport"] = entry.Transport,
                ["secondsAgo"] = (int)(ago / 1000),
                ["active"] = ago <= ActiveWindowMs,
            });
        }
        return array;
    }
}
