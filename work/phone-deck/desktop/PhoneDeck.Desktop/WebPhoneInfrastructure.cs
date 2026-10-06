using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PhoneDeck.Desktop;

internal sealed record WebPhoneIdentity(string ComputerId, string DisplayName);
internal static class WebPhoneNetwork
{
    internal static string[] Addresses() => NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up)
        .SelectMany(x => x.GetIPProperties().UnicastAddresses).Select(x => x.Address)
        .Where(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x) && !x.ToString().StartsWith("169.254.", StringComparison.Ordinal)
            && !x.ToString().StartsWith("198.18.", StringComparison.Ordinal) && !x.ToString().StartsWith("198.19.", StringComparison.Ordinal))
        .Select(x => x.ToString()).Distinct().ToArray();
}

internal static class PrivateFiles
{
    internal static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    internal static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

internal sealed class RequestDeduplicator
{
    private readonly Dictionary<string, (long At, string Hash, bool Complete)> recent = new();
    internal bool Duplicate(string owner, string id, string payload)
    {
        foreach (var key in recent.Where(x => Environment.TickCount64 - x.Value.At > 30000).Select(x => x.Key).ToArray()) recent.Remove(key);
        if (!recent.TryGetValue(owner + ":" + id, out var record)) return false;
        if (record.Hash != Hash(payload)) throw new ArgumentException("相同请求编号对应不同内容");
        if (!record.Complete) throw new InvalidOperationException("该请求已尝试但未确认完成，请检查电脑结果；不会自动重复执行");
        return true;
    }
    internal void Begin(string owner, string id, string payload) => Save(owner, id, payload, false);
    internal void Confirm(string owner, string id, string payload)
        => Save(owner, id, payload, true);
    private void Save(string owner, string id, string payload, bool complete)
    {
        if (recent.Count >= 4096) recent.Remove(recent.MinBy(x => x.Value.At).Key);
        recent[owner + ":" + id] = (Environment.TickCount64, Hash(payload), complete);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal sealed record PairRequest(string? PairingId, string? OneTimeMaterial, string? ClientId, string? ClientLabel);
internal sealed record AdminRequest(string? PairingId, string? ClientId);
