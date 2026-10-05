using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// M1-A A2：扫码配对窗口（D02 设计 §3）。
/// 一次性材料 32 字符 base32（160bit，满足冻结值 ≥32 字符/≥128bit）；
/// 窗口 120 秒用单调时钟（墙上时间跳变不延长，V11）；提交失败 5 次关窗；
/// 未知 pairingId 按"窗口未开"语义处理且独立计数（防 LAN 反复关窗 DoS）；
/// 匹配后进入待确认态（30 秒），本机确认/拒绝/超时三态收束；
/// 材料单次使用；材料与令牌不落日志（审计只记 pairingId 与结果）。
/// </summary>
internal enum PairingSubmitStatus
{
    Pending,
    /// <summary>窗口未开或未知 pairingId：404，不计失败。</summary>
    WindowNotOpen,
    /// <summary>材料过期/已用/错误：401（抗枚举，不区分原因）。</summary>
    MaterialInvalid,
    /// <summary>失败达上限并已关窗：429。</summary>
    FailureLimit,
}

internal sealed class PairingWindowSession
{
    /// <summary>一次性材料明文：仅在窗口存续期驻留内存，用于比对；绝不写日志/落盘/进状态快照。</summary>
    internal required string Material { get; init; }
    public required string PairingId { get; init; }
    public required string QrPayloadJson { get; init; }
    public required string ManualCode { get; init; }
    public required string MaterialCheckCode { get; init; }
    public required long ExpiresAtTick { get; init; }
    public bool Consumed { get; set; }
    public PendingPairingSubmission? Pending { get; set; }
}

internal sealed class PendingPairingSubmission
{
    public required string ClientId { get; init; }
    public required string ClientLabel { get; init; }
    public required string PairingId { get; init; }
    public required TaskCompletionSource<bool> Decision { get; init; }
}

internal sealed class PairingWindowManager
{
    internal const int ValidSeconds = 120;
    internal const int MaxFailuresPerWindow = 5;
    internal const int ConfirmationTimeoutSeconds = 30;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private readonly string computerId;
    private readonly string displayName;
    private readonly string certificateSha256;
    private readonly int httpsPort;
    private readonly object gate = new();
    private PairingWindowSession? active;
    private int failures;

    /// <summary>单调时钟注入点，仅供测试操纵窗口过期（V11）。</summary>
    internal Func<long> MonotonicClock { get; set; } = () => Environment.TickCount64;

    public PairingWindowManager(
        string computerId,
        string displayName,
        string certificateSha256,
        int httpsPort)
    {
        this.computerId = computerId;
        this.displayName = displayName;
        this.certificateSha256 = certificateSha256;
        this.httpsPort = httpsPort;
    }

    /// <summary>开启（或返回未过期且无待确认的现有）配对窗口。</summary>
    public PairingWindowSession Begin()
    {
        lock (gate)
        {
            if (active is { } session
                && session.Pending is null
                && MonotonicClock() < session.ExpiresAtTick)
            {
                return session;
            }
            if (active?.Pending is { } orphan)
            {
                orphan.Decision.TrySetResult(false);
            }
            var material = NewMaterial();
            var pairingId = Guid.NewGuid().ToString();
            var session2 = new PairingWindowSession
            {
                Material = material,
                PairingId = pairingId,
                QrPayloadJson = JsonSerializer.Serialize(new
                {
                    version = 1,
                    computerId,
                    displayName,
                    httpsPort,
                    certificateSha256,
                    pairingId,
                    oneTimeMaterial = material,
                    validSeconds = ValidSeconds,
                    maxFailuresPerWindow = MaxFailuresPerWindow,
                }),
                ManualCode = FormatManualCode(material),
                MaterialCheckCode = MaterialCheckCode(material),
                ExpiresAtTick = MonotonicClock() + ValidSeconds * 1000L,
            };
            active = session2;
            failures = 0;
            return session2;
        }
    }

    public PairingSubmitStatus TryBeginSubmit(
        string? pairingId,
        string? material,
        string clientId,
        string clientLabel,
        out PendingPairingSubmission? pending)
    {
        pending = null;
        lock (gate)
        {
            var session = active;
            var now = MonotonicClock();
            if (session is null
                || !string.Equals(session.PairingId, pairingId, StringComparison.Ordinal))
            {
                // 未知/不匹配 pairingId：窗口未开语义，独立计数不触发关窗（设计 §3）。
                return PairingSubmitStatus.WindowNotOpen;
            }
            if (now >= session.ExpiresAtTick || session.Consumed)
            {
                return PairingSubmitStatus.MaterialInvalid;
            }
            if (!MaterialMatches(session, material))
            {
                failures += 1;
                if (failures >= MaxFailuresPerWindow)
                {
                    active = null;
                    return PairingSubmitStatus.FailureLimit;
                }
                return PairingSubmitStatus.MaterialInvalid;
            }
            session.Consumed = true;
            pending = new PendingPairingSubmission
            {
                ClientId = clientId,
                ClientLabel = clientLabel,
                PairingId = session.PairingId,
                Decision = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously),
            };
            session.Pending = pending;
            return PairingSubmitStatus.Pending;
        }
    }

    public bool Confirm(string? pairingId)
    {
        lock (gate)
        {
            if (active?.Pending is not { } pending
                || !string.Equals(active.PairingId, pairingId, StringComparison.Ordinal))
            {
                return false;
            }
            active.Pending = null;
            return pending.Decision.TrySetResult(true);
        }
    }

    public bool Deny(string? pairingId)
    {
        lock (gate)
        {
            if (active?.Pending is not { } pending
                || !string.Equals(active.PairingId, pairingId, StringComparison.Ordinal))
            {
                return false;
            }
            active.Pending = null;
            return pending.Decision.TrySetResult(false);
        }
    }

    /// <summary>取消窗口：关闭并拒绝待确认提交。</summary>
    public void Cancel()
    {
        lock (gate)
        {
            active?.Pending?.Decision.TrySetResult(false);
            active = null;
        }
    }

    /// <summary>是否有待确认提交（回环 begin 端点据此拒绝重复开窗）。</summary>
    public bool HasPendingConfirmation()
    {
        lock (gate)
        {
            return active?.Pending is not null;
        }
    }

    /// <summary>回环管理端点快照：含手工码/校验码/剩余秒数/待确认信息（供托盘 UI）。</summary>
    public object StatusSnapshot()
    {
        lock (gate)
        {
            if (active is not { } session)
            {
                return new { ok = true, open = false };
            }
            var remaining = Math.Max(0, (int)((session.ExpiresAtTick - MonotonicClock()) / 1000));
            return new
            {
                ok = true,
                open = MonotonicClock() < session.ExpiresAtTick,
                pairingId = session.PairingId,
                qrPayload = session.QrPayloadJson,
                manualCode = session.ManualCode,
                checkCode = session.MaterialCheckCode,
                remainingSeconds = remaining,
                failuresRemaining = MaxFailuresPerWindow - failures,
                pending = session.Pending is { } pending
                    ? new
                    {
                        clientId = pending.ClientId,
                        clientLabel = pending.ClientLabel,
                    }
                    : null,
            };
        }
    }

    /// <summary>32 字符 base32（A-Z2-7）= 160bit 一次性材料。</summary>
    private static string NewMaterial()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var characters = new char[32];
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = Base32Alphabet[bytes[index] & 31];
        }
        return new string(characters);
    }

    /// <summary>位独立性：每字符 5bit 取自独立随机字节，不截断熵。</summary>
    private static string FormatManualCode(string material) =>
        string.Join("-",
            Enumerable.Range(0, material.Length / 4)
                .Select(group => material.Substring(group * 4, 4)));

    /// <summary>材料派生校验码（SHA256 前 4 位大写十六进制），供本机确认卡与手机侧人工比对。</summary>
    internal static string MaterialCheckCode(string material) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..4];

    /// <summary>材料比对：等长 + 常量时间（CryptographicOperations.FixedTimeEquals）。</summary>
    private static bool MaterialMatches(PairingWindowSession session, string? material)
    {
        if (string.IsNullOrEmpty(material))
        {
            return false;
        }
        var expected = Encoding.UTF8.GetBytes(session.Material);
        var supplied = Encoding.UTF8.GetBytes(material);
        return expected.Length == supplied.Length
            && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
