using System.Diagnostics;

/// <summary>
/// M1-B 会话租约与墓账登记（contracts/session-states.json §16.3 R1–R3、PRO-03、MIC-10）。
/// 与 DictationSessionManager 并行侧挂，不改变既有状态机的音频/引擎时序：
/// - 代次（generation）：迟到 start 不得复活已取消代次（R1）；
/// - 租约（lease）：单调时钟（Stopwatch），墙上时间回拨不延长（PRO-03）；
/// - 墓碑（tombstone）：会话终止后保留期内拒绝迟到事件（R2：旧 stop 只清旧会话），
///   保留期满后允许同 sessionId 重新开始（sessionId 复用为合法新代次）；
/// - 重连窗口（RC1/RC2，15s）：断流后会话在窗口内可续接（MIC-10 提案值）。
/// </summary>
internal sealed class SessionLeaseRegistry
{
    internal const long DefaultLeaseMilliseconds = 30_000;
    internal const long DefaultTombstoneRetentionMilliseconds = 60_000;
    internal const long DefaultReconnectWindowMilliseconds = 15_000;

    internal enum StartOutcome
    {
        /// <summary>接受新会话（IDLE 或同会话重试）。</summary>
        Accepted,
        /// <summary>同会话同代次重复 start：幂等成功。</summary>
        Idempotent,
        /// <summary>墓碑保留期内复用已终止会话：拒绝（R1，迟到 start 不复活）。</summary>
        RejectedTombstoned,
        /// <summary>另一会话仍持有：拒绝（单所有者）。</summary>
        RejectedOwned,
    }

    private sealed class ActiveSession
    {
        public string SessionId = null!;
        public string ClientId = null!;
        public long Generation;
        public long LeaseDeadline;
        public long ReconnectDeadline;
    }

    private readonly object gate = new();
    private readonly long leaseMilliseconds;
    private readonly long tombstoneRetentionMilliseconds;
    private readonly long reconnectWindowMilliseconds;
    private readonly Dictionary<string, long> tombstones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveSession> active = new(StringComparer.Ordinal);
    private long generationCounter;

    /// <summary>单调时钟注入点（默认 Stopwatch），供 V25 式竞态测试操纵。</summary>
    internal Func<long> MonotonicMilliseconds { get; set; } =
        () => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    internal SessionLeaseRegistry(
        long leaseMilliseconds = DefaultLeaseMilliseconds,
        long tombstoneRetentionMilliseconds = DefaultTombstoneRetentionMilliseconds,
        long reconnectWindowMilliseconds = DefaultReconnectWindowMilliseconds)
    {
        this.leaseMilliseconds = leaseMilliseconds;
        this.tombstoneRetentionMilliseconds = tombstoneRetentionMilliseconds;
        this.reconnectWindowMilliseconds = reconnectWindowMilliseconds;
    }

    internal long CurrentGeneration => Volatile.Read(ref generationCounter);

    /// <summary>
    /// 会话启动登记（在引擎/音频启动前调用）。同会话重试为幂等；墓碑期内拒绝；
    /// 旧会话租约已过期时允许新会话接管（后台看门狗语义，不依赖本调用清理）。
    /// </summary>
    internal StartOutcome BeginStart(string sessionId, string clientId)
    {
        lock (gate)
        {
            ExpireStaleLocked();
            if (tombstones.TryGetValue(sessionId, out var diedAt)
                && MonotonicMilliseconds() - diedAt < tombstoneRetentionMilliseconds)
            {
                return StartOutcome.RejectedTombstoned;
            }
            if (active.TryGetValue(sessionId, out var session))
            {
                return string.Equals(session.ClientId, clientId, StringComparison.Ordinal)
                    ? StartOutcome.Idempotent
                    : StartOutcome.RejectedOwned;
            }
            // 其他活动会话：租约存活则拒绝（单所有者）。
            if (active.Values.Any(held => held.LeaseDeadline > MonotonicMilliseconds()))
            {
                return StartOutcome.RejectedOwned;
            }
            active.Clear();
            generationCounter += 1;
            var now = MonotonicMilliseconds();
            active[sessionId] = new ActiveSession
            {
                SessionId = sessionId,
                ClientId = clientId,
                Generation = generationCounter,
                LeaseDeadline = now + leaseMilliseconds,
                ReconnectDeadline = now + reconnectWindowMilliseconds,
            };
            tombstones.Remove(sessionId);
            return StartOutcome.Accepted;
        }
    }

    /// <summary>续租（音视频活动即续租）；过期租约不再复活（R1）。</summary>
    internal bool RenewLease(string sessionId)
    {
        lock (gate)
        {
            if (!active.TryGetValue(sessionId, out var session))
            {
                return false;
            }
            var now = MonotonicMilliseconds();
            if (session.LeaseDeadline <= now)
            {
                return false;
            }
            session.LeaseDeadline = now + leaseMilliseconds;
            return true;
        }
    }

    /// <summary>断流：进入重连窗口（RC1/RC2）；窗口内可 Resume，期间阻止新会话接管。</summary>
    internal bool MarkStreamBroken(string sessionId)
    {
        lock (gate)
        {
            if (!active.TryGetValue(sessionId, out var session))
            {
                return false;
            }
            session.ReconnectDeadline = MonotonicMilliseconds() + reconnectWindowMilliseconds;
            return true;
        }
    }

    /// <summary>窗口内是否允许同会话续接（RC1：仅仍有效的授权会话）。</summary>
    internal bool CanResume(string sessionId)
    {
        lock (gate)
        {
            return active.TryGetValue(sessionId, out var session)
                && session.ReconnectDeadline > MonotonicMilliseconds()
                && session.LeaseDeadline > MonotonicMilliseconds();
        }
    }

    /// <summary>
    /// 停止优先（R1）：登记停止意图并写墓碑（返回 false=旧/未知会话：stale-ignored，不改状态）。
    /// 墓碑即刻生效——此后同 sessionId 的迟到 start 拒绝，直到保留期过。
    /// </summary>
    internal bool BeginStop(string sessionId)
    {
        lock (gate)
        {
            var now = MonotonicMilliseconds();
            if (!active.TryGetValue(sessionId, out var session))
            {
                return false;
            }
            tombstones[sessionId] = now;
            active.Remove(sessionId);
            return true;
        }
    }

    /// <summary>会话异常终止（断流超窗、引擎失败）：同样写墓碑，迟到事件拒绝。</summary>
    internal void Bury(string sessionId)
    {
        lock (gate)
        {
            if (active.Remove(sessionId))
            {
                tombstones[sessionId] = MonotonicMilliseconds();
            }
        }
    }

    /// <summary>
    /// 启动失败的清理（T03→T09）：移除活动记录但不写墓碑——
    /// 手机按 PRO-02 用同 sessionId 重试失败启动是合法新尝试，不得按迟到 start 拒绝。
    /// </summary>
    internal void Abandon(string sessionId)
    {
        lock (gate)
        {
            active.Remove(sessionId);
        }
    }

    /// <summary>当前活动会话（无则 null）；租约过期视为无。</summary>
    internal (string SessionId, long Generation)? ActiveSnapshot()
    {
        lock (gate)
        {
            ExpireStaleLocked();
            var session = active.Values.FirstOrDefault();
            return session is null
                ? null
                : (session.SessionId, session.Generation);
        }
    }

    private void ExpireStaleLocked()
    {
        var now = MonotonicMilliseconds();
        foreach (var stale in active
                     .Where(pair => pair.Value.LeaseDeadline <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            tombstones[stale] = now;
            active.Remove(stale);
        }
        foreach (var expired in tombstones
                     .Where(pair => now - pair.Value >= tombstoneRetentionMilliseconds)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            tombstones.Remove(expired);
        }
    }
}
