/// <summary>
/// phoneStopV1：电脑端结束了手机发起的 managed 会话时，在健康响应的
/// <c>audio.stopRequestedSessionId</c> 中返回该会话 ID，手机据此立即关麦。
/// 只在内存保留 <see cref="RetentionMilliseconds"/>，并且只返回给发起该会话的手机，
/// 与新版 Desktop 的同名能力语义一致。
/// </summary>
internal sealed class PhoneStopReceipts
{
    internal const long RetentionMilliseconds = 60_000;

    private readonly object gate = new();
    private readonly Dictionary<string, (string SessionId, long RecordedAt)> latestByOwner =
        new(StringComparer.Ordinal);

    internal Func<long> MonotonicMilliseconds { get; set; } = () => Environment.TickCount64;

    internal static string NormalizeOwner(string? clientId) =>
        clientId ?? ClientCredentialsStore.LegacySharedClientId;

    internal void Record(string sessionId, string? ownerClientId)
    {
        lock (gate)
        {
            latestByOwner[NormalizeOwner(ownerClientId)] = (sessionId, MonotonicMilliseconds());
        }
    }

    internal string? For(string? ownerClientId)
    {
        lock (gate)
        {
            var owner = NormalizeOwner(ownerClientId);
            if (!latestByOwner.TryGetValue(owner, out var receipt))
            {
                return null;
            }
            if (MonotonicMilliseconds() - receipt.RecordedAt > RetentionMilliseconds)
            {
                latestByOwner.Remove(owner);
                return null;
            }
            return receipt.SessionId;
        }
    }
}
