using System.Net;
using System.Security.Cryptography;
using System.Text;

internal readonly record struct LanAuthResult(bool Authorized, string? ClientId)
{
    public static readonly LanAuthResult Denied = new(false, null);
}

internal static class LanRequestAuthenticator
{
    internal static bool IsAuthorized(int localPort, string? suppliedToken, string expectedToken)
    {
        if (localPort != 8766)
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(suppliedToken))
        {
            return false;
        }
        var supplied = Encoding.UTF8.GetBytes(suppliedToken);
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        return supplied.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    /// <summary>
    /// M1-A A1 鉴权解析（D02 设计 §5.1）：8766 端口接受两种凭据——
    /// 新式 Authorization: Bearer + 逐手机令牌（返回其 clientId），
    /// 或旧式 X-PhoneDeck-Token 共享令牌（映射隐式 legacy-shared，迁移窗口内共存）。
    /// Bearer 出现但无效时直接拒绝，不回退旧头（防混淆降级）。非 8766 端口（回环）放行、无 clientId。
    /// </summary>
    internal static LanAuthResult Resolve(
        int localPort,
        string? legacyTokenHeader,
        string? authorizationHeader,
        string expectedSharedToken,
        ClientCredentialsStore credentials)
    {
        if (localPort != 8766)
        {
            return new LanAuthResult(true, null);
        }
        var bearer = ParseBearer(authorizationHeader);
        if (bearer is not null)
        {
            var record = credentials.Authenticate(bearer);
            return record is null
                ? LanAuthResult.Denied
                : new LanAuthResult(true, record.ClientId);
        }
        if (legacyTokenHeader is not null
            && IsAuthorized(localPort, legacyTokenHeader, expectedSharedToken))
        {
            return new LanAuthResult(true, ClientCredentialsStore.LegacySharedClientId);
        }
        return LanAuthResult.Denied;
    }

    private static string? ParseBearer(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }
        var prefix = "Bearer ";
        return authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && authorizationHeader.Length > prefix.Length
            ? authorizationHeader[prefix.Length..].Trim()
            : null;
    }

    internal static bool IsUsbPairingRequest(int localPort, IPAddress? remoteAddress) =>
        localPort == 8765
        && remoteAddress is not null
        && IPAddress.IsLoopback(remoteAddress);
}
