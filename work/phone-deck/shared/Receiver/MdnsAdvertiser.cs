using System.Net;
using Makaretu.Dns;

/// <summary>
/// mDNS 广播（M1-A A5 / NET-01）：发布 _phonedeck._tcp，TXT 字段与 UDP 应答同一集合——
/// 仅 computerId/displayName/platform/port/capabilities，绝不携带配对令牌或证书指纹；
/// 候选地址必须经 HTTPS 8766 的钉扎+令牌校验才可触发输入/采音（与 UDP 路径同一不变量）。
/// 多播不可用的网络上优雅降级（Running=false，不抛出）；UDP 8767 应答保持回退。
/// </summary>
internal sealed class MdnsAdvertiser : IDisposable
{
    internal const string ServiceName = "_phonedeck";
    internal const string ServiceProtocol = "_tcp";

    private static readonly string[] Capabilities =
    {
        "fixedAction", "keyChord", "text", "macro", "phoneAudio",
        "managedDictation", "secureLan",
    };

    private MulticastService? multicast;
    private ServiceDiscovery? discovery;
    private volatile bool running;

    internal bool Running => running;

    /// <summary>TXT 字段构造（纯函数，供单测与无泄漏断言）。</summary>
    internal static Dictionary<string, string> BuildTxtFields(
        string computerId,
        string displayName,
        string platform,
        int httpsPort,
        string[]? capabilities = null)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["computerId"] = computerId,
            ["displayName"] = displayName,
            ["platform"] = string.IsNullOrWhiteSpace(platform) ? "unknown" : platform,
            ["port"] = httpsPort.ToString(),
            ["capabilities"] = string.Join(",",
                capabilities is { Length: > 0 } ? capabilities : Capabilities),
        };
    }

    /// <summary>DNS 实例名：仅保留字母数字与连字符（computerId 为 GUID 形态，天然合法）。</summary>
    internal static string SanitizeInstanceName(string computerId)
    {
        var cleaned = new string(computerId
            .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')
            .ToArray());
        return cleaned.Length > 63 ? cleaned[..63] : cleaned;
    }

    internal void Start(ReceiverIdentity identity, int httpsPort, string[]? capabilities = null)
    {
        Stop();
        try
        {
            var fields = BuildTxtFields(
                identity.ComputerId, identity.DisplayName, identity.Platform, httpsPort, capabilities);
            var profile = new ServiceProfile(
                SanitizeInstanceName(identity.ComputerId),
                ServiceName,
                (ushort)httpsPort);
            profile.Resources.Add(new TXTRecord
            {
                Name = profile.FullyQualifiedName,
                Strings = fields.Select(pair => $"{pair.Key}={pair.Value}").ToList(),
            });
            multicast = new MulticastService();
            discovery = new ServiceDiscovery(multicast);
            multicast.Start();
            discovery.Advertise(profile);
            running = true;
            Console.WriteLine($"局域网发现在线：mDNS {ServiceName}.{ServiceProtocol}（TXT 无令牌/指纹）");
        }
        catch (Exception exception)
        {
            // 多播被网络/防火墙禁用是合法环境：降级到 UDP 回退，不影响服务。
            Console.WriteLine($"mDNS 广播未启用（降级到 UDP 回退）：{exception.Message}");
            Stop();
        }
    }

    internal void Stop()
    {
        discovery?.Dispose();
        discovery = null;
        multicast?.Dispose();
        multicast = null;
        running = false;
    }

    public void Dispose() => Stop();
}
