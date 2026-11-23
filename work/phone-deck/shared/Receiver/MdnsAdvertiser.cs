using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
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

    private readonly object syncRoot = new();
    private MulticastService? multicast;
    private ServiceDiscovery? discovery;
    private string? activeComputerId;
    private string? activeDisplayName;
    private string? activePlatform;
    private int activeHttpsPort;
    private string[]? activeCapabilities;
    private NetworkAddressChangedEventHandler? addressChangedHandler;
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

    /// <summary>DNS 实例名：仅保留 ASCII 字母数字与连字符（computerId 为 GUID 形态，天然合法）。</summary>
    internal static string SanitizeInstanceName(string computerId)
    {
        var cleaned = new string((computerId ?? "")
            .Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-')
            .ToArray());
        if (string.IsNullOrEmpty(cleaned))
        {
            cleaned = "phonedeck";
        }
        return cleaned.Length > 63 ? cleaned[..63] : cleaned;
    }

    /// <summary>
    /// Makaretu.Dns 的 WireWriter/WireReader 要求 TXT 字符串只含 ASCII（≤0x7F），
    /// 中文电脑名（如“往里走的COMPUTE”）会触发 Only ASCII characters are allowed。
    /// 纯 ASCII 原样保持；含非 ASCII 时对非 ASCII 字节做 UTF-8 百分号编码，手机端解码还原。
    /// </summary>
    internal static string EncodeWireTxtValue(string value)
    {
        if (string.IsNullOrEmpty(value) || value.All(c => c is >= ' ' and <= '~'))
        {
            return value ?? "";
        }
        var builder = new StringBuilder(value.Length * 2);
        foreach (var ch in value)
        {
            if (ch is >= ' ' and <= '~' && ch != '%')
            {
                builder.Append(ch);
            }
            else
            {
                foreach (var b in Encoding.UTF8.GetBytes(new[] { ch }))
                {
                    builder.Append('%').Append(b.ToString("X2"));
                }
            }
        }
        return builder.ToString();
    }

    internal static ServiceProfile CreateProfile(
        string computerId,
        string displayName,
        string platform,
        int httpsPort,
        string[]? capabilities = null,
        IEnumerable<IPAddress>? addresses = null)
    {
        var fields = BuildTxtFields(
            computerId, displayName, platform, httpsPort, capabilities);
        var resolvedAddresses = (addresses ?? GetAdvertisableAddresses()).ToList();
        var profile = new ServiceProfile(
            SanitizeInstanceName(computerId),
            ServiceName,
            (ushort)httpsPort,
            resolvedAddresses.Count > 0 ? resolvedAddresses : null);
        profile.Resources.RemoveAll(record => record is TXTRecord);
        profile.Resources.Add(new TXTRecord
        {
            Name = profile.FullyQualifiedName,
            Strings = fields
                .Select(pair => $"{pair.Key}={EncodeWireTxtValue(pair.Value)}")
                .ToList(),
        });
        return profile;
    }

    private static IEnumerable<NetworkInterface> FilterNetworkInterfaces(
        IEnumerable<NetworkInterface> interfaces) =>
        interfaces.Where(network =>
            network.OperationalStatus == OperationalStatus.Up
            && network.NetworkInterfaceType is not NetworkInterfaceType.Loopback
            && network.NetworkInterfaceType is not NetworkInterfaceType.Tunnel
            && network.GetIPProperties().UnicastAddresses.Any(
                unicast => IsAdvertisableAddress(unicast.Address)));

    private static IEnumerable<IPAddress> GetAdvertisableAddresses()
    {
        try
        {
            return FilterNetworkInterfaces(NetworkInterface.GetAllNetworkInterfaces())
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(IsAdvertisableAddress)
                .Distinct()
                .ToArray();
        }
        catch
        {
            return Array.Empty<IPAddress>();
        }
    }

    internal static bool IsAdvertisableAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }
        return bytes[0] != 127
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19));
    }

    /// <summary>身份字段以基本类型传入：Windows、macOS 与 Desktop 的身份类型各自定义。</summary>
    internal void Start(string computerId, string displayName, string platform,
        int httpsPort, string[]? capabilities = null)
    {
        lock (syncRoot)
        {
            StopCore();
            activeComputerId = computerId;
            activeDisplayName = displayName;
            activePlatform = platform;
            activeHttpsPort = httpsPort;
            activeCapabilities = capabilities;
            addressChangedHandler = (_, _) => RefreshAdvertisement();
            NetworkChange.NetworkAddressChanged += addressChangedHandler;
            StartCore(logStarted: true);
        }
    }

    private void RefreshAdvertisement()
    {
        lock (syncRoot)
        {
            if (activeComputerId is null || activeDisplayName is null || activePlatform is null)
            {
                return;
            }
            StopInstanceOnly();
            StartCore(logStarted: false);
        }
    }

    private void StartCore(bool logStarted)
    {
        try
        {
            var profile = CreateProfile(
                activeComputerId!, activeDisplayName!, activePlatform!, activeHttpsPort, activeCapabilities);
            multicast = new MulticastService(FilterNetworkInterfaces);
            discovery = new ServiceDiscovery(multicast);
            multicast.Start();
            discovery.Advertise(profile);
            running = true;
            if (logStarted)
            {
                Console.WriteLine($"局域网发现在线：mDNS {ServiceName}.{ServiceProtocol}（TXT 无令牌/指纹）");
            }
        }
        catch (Exception exception)
        {
            // 多播被网络/防火墙禁用是合法环境：降级到 UDP 回退，不影响服务。
            if (logStarted)
            {
                Console.WriteLine($"mDNS 广播未启用（降级到 UDP 回退）：{exception.Message}");
            }
            StopInstanceOnly();
        }
    }

    internal void Stop()
    {
        lock (syncRoot)
        {
            StopCore();
        }
    }

    private void StopCore()
    {
        if (addressChangedHandler is not null)
        {
            NetworkChange.NetworkAddressChanged -= addressChangedHandler;
            addressChangedHandler = null;
        }
        activeComputerId = null;
        activeDisplayName = null;
        activePlatform = null;
        StopInstanceOnly();
    }

    private void StopInstanceOnly()
    {
        discovery?.Dispose();
        discovery = null;
        multicast?.Dispose();
        multicast = null;
        running = false;
    }

    public void Dispose() => Stop();
}
