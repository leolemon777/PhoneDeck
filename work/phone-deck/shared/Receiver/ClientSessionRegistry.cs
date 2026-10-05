/// <summary>
/// M1-A A1：逐手机会话注册表（设计 §6）。
/// 每个 clientId 一个 CancellationTokenSource；撤销时 Cancel 并移除，
/// 该手机的全部音频长流经链接令牌同步终止（V12：不能只拦新 HTTP）。
/// CTS 按 clientId 常驻（数量受客户端数约束），撤销后再次注册得到全新令牌。
/// </summary>
internal sealed class ClientSessionRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, CancellationTokenSource> sources = new(StringComparer.Ordinal);

    public CancellationToken Register(string clientId)
    {
        lock (gate)
        {
            if (!sources.TryGetValue(clientId, out var existing))
            {
                existing = new CancellationTokenSource();
                sources[clientId] = existing;
            }
            return existing.Token;
        }
    }

    /// <summary>撤销：取消并移除该 clientId 的令牌；未注册返回 false。</summary>
    public bool Cancel(string clientId)
    {
        CancellationTokenSource? source;
        lock (gate)
        {
            if (!sources.Remove(clientId, out source))
            {
                return false;
            }
        }
        source.Cancel();
        source.Dispose();
        return true;
    }
}
