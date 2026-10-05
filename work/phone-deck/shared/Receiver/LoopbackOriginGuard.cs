/// <summary>
/// M0/R1 修复（SEC-02/V13/KNOWN_ISSUES #3）：8765 回环入口的 Host/Origin 校验，
/// 防本机恶意网页经简单表单/跨站 fetch 触发有副作用的管理端点。
/// 规则（对 8765 上的非 GET 请求生效；GET 属读端点放行）：
/// - Host 必须精确匹配 127.0.0.1:8765 / localhost:8765 / [::1]:8765；
/// - Origin 头若存在（浏览器 fetch/form 必带或可省略——same-origin 时省略），
///   必须同源 http(s)://127.0.0.1:8765；其余（evil.com 等）一律 403；
/// - Referer 同理作为辅助校验（存在即须同源）。
/// 非浏览器客户端（ControlCenter HttpClient）不发 Origin/Referer → 不受影响。
/// </summary>
internal static class LoopbackOriginGuard
{
    private static readonly string[] AllowedHosts =
    {
        "127.0.0.1:8765",
        "localhost:8765",
        "[::1]:8765",
    };

    private static readonly string[] AllowedOrigins =
    {
        "http://127.0.0.1:8765",
        "http://localhost:8765",
        "https://127.0.0.1:8765",
        "https://localhost:8765",
    };

    /// <summary>返回 null 表示通过；否则为拒绝原因（403 文案）。</summary>
    internal static string? Validate(string? hostHeader, string? originHeader, string? refererHeader)
    {
        var host = hostHeader?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(host) || !AllowedHosts.Contains(host))
        {
            return "请求主机不是本机回环地址";
        }
        var origin = originHeader?.Trim();
        if (!string.IsNullOrEmpty(origin))
        {
            var normalized = origin.TrimEnd('/').ToLowerInvariant();
            if (!AllowedOrigins.Contains(normalized))
            {
                return "跨站来源被拒绝";
            }
        }
        var referer = refererHeader?.Trim();
        if (!string.IsNullOrEmpty(referer))
        {
            var ok = AllowedOrigins.Any(allowed =>
                referer.StartsWith(allowed + "/", StringComparison.OrdinalIgnoreCase)
                || referer.StartsWith(allowed, StringComparison.OrdinalIgnoreCase));
            if (!ok)
            {
                return "引用页来源被拒绝";
            }
        }
        return null;
    }
}
