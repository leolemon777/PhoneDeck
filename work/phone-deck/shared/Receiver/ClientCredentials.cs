using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// M1-A A1：逐手机客户端凭据（D02 设计 §2/§6）。
/// 服务端只存令牌 SHA-256 哈希，令牌本体只出现在签发响应与手机端；
/// 凭据找回唯一路径是重新扫码配对（rotate 永不重发）。
/// clients.json 原子写（temp + Replace），损坏时备份后从空表启动（CFG-03 纪律）。
/// M1-A A4：文件升级为对象信封 {version, legacyRevokedAt, clients}（读取容忍 A1 裸数组旧格式）；
/// 签发/撤销/rotate/legacy 撤销逐条追加同目录 clients-audit.log（脱敏，≤500 行，设计 §5/§6）。
/// </summary>
internal sealed class ClientCredentialRecord
{
    public string ClientId { get; set; } = "";
    public string Label { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string PairingId { get; set; } = "";
    public List<string> Scopes { get; set; } = new();
    public string IssuedAtUtc { get; set; } = "";
    public string? RevokedAtUtc { get; set; }
}

/// <summary>clients.json 信封（M1-A A4）：{version, legacyRevokedAt, clients}。</summary>
internal sealed class ClientCredentialsFileEnvelope
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("legacyRevokedAt")]
    public string? LegacyRevokedAtUtc { get; set; }

    [JsonPropertyName("clients")]
    public List<ClientCredentialRecord> Clients { get; set; } = new();
}

internal enum RotateOutcome
{
    Issued,
    AlreadyUpgraded,
    Revoked,
    RateLimited,
}

internal sealed record RotateResult(RotateOutcome Outcome, ClientCredentialRecord? Record, string? Token)
{
    public static RotateResult StatusOnly(RotateOutcome outcome) => new(outcome, null, null);
}

internal sealed class ClientCredentialsStore
{
    /// <summary>迁移窗口内旧共享令牌的隐式身份（NET-07 / 设计 §5.1）。</summary>
    public const string LegacySharedClientId = "legacy-shared";

    /// <summary>rotate 同 clientId 两次进入签发路径的最小间隔（设计 §5.2：简单限速，内存态）。</summary>
    private const long RotateMinIntervalMs = 3_000;

    /// <summary>审计文件行数上限（设计 §6：有界轮转）。</summary>
    internal const int MaxAuditLines = 500;

    private readonly string filePath;
    private readonly string auditFilePath;
    private readonly object gate = new();
    private readonly Dictionary<string, long> lastRotateAttemptMs = new(StringComparer.Ordinal);
    private List<ClientCredentialRecord> records = new();
    private Dictionary<string, ClientCredentialRecord> indexByTokenHash = new(StringComparer.Ordinal);
    private string? legacyRevokedAtUtc;

    /// <summary>rotate 限速用的单调时钟（墙上时间跳变不放宽间隔），测试可注入。</summary>
    internal Func<long> MonotonicClock { get; set; } = () => Environment.TickCount64;

    /// <summary>legacy 旧共享令牌入口是否已应急撤销（设计 §5.3/§5.4）。</summary>
    public bool LegacyRevoked => legacyRevokedAtUtc is not null;

    public string? LegacyRevokedAtUtc => legacyRevokedAtUtc;

    public ClientCredentialsStore(string filePath)
    {
        this.filePath = filePath;
        var directory = Path.GetDirectoryName(filePath);
        auditFilePath = string.IsNullOrEmpty(directory)
            ? "clients-audit.log"
            : Path.Combine(directory, "clients-audit.log");
        LoadOrInit();
    }

    public int ActiveCount
    {
        get
        {
            lock (gate)
            {
                return records.Count(record => record.RevokedAtUtc is null);
            }
        }
    }

    /// <summary>签发新凭据；令牌本体只通过 out 参数交出一次，落库的只有哈希。clientId 可由手机提供（须为 GUID），冲突或缺失时服务端生成。</summary>
    public ClientCredentialRecord Issue(
        string label,
        IEnumerable<string> scopes,
        string pairingId,
        out string token,
        string? clientId = null)
    {
        token = NewToken();
        string resolvedClientId;
        if (clientId is { Length: > 0 }
            && Guid.TryParse(clientId, out _)
            && !records.Any(record => string.Equals(record.ClientId, clientId, StringComparison.Ordinal)))
        {
            resolvedClientId = clientId.Trim();
        }
        else
        {
            do
            {
                resolvedClientId = Guid.NewGuid().ToString();
            }
            while (records.Any(record => string.Equals(record.ClientId, resolvedClientId, StringComparison.Ordinal)));
        }
        var record = new ClientCredentialRecord
        {
            ClientId = resolvedClientId,
            Label = string.IsNullOrWhiteSpace(label) ? "未命名手机" : label.Trim(),
            TokenHash = HashToken(token),
            PairingId = pairingId,
            Scopes = scopes.ToList(),
            IssuedAtUtc = DateTime.UtcNow.ToString("o"),
        };
        lock (gate)
        {
            records.Add(record);
            try
            {
                SaveLocked();
            }
            catch (Exception)
            {
                // 写盘失败回滚内存新增，保持与磁盘一致（与 Revoke 的 M-2 纪律对齐）。
                records.Remove(record);
                throw;
            }
            RebuildIndexLocked();
            AppendAuditLocked("issue", $"clientId={record.ClientId} pairingId={record.PairingId}");
        }
        return record;
    }

    /// <summary>按 bearer 令牌认证；已撤销或未知令牌一律 null（不区分，抗枚举）。</summary>
    public ClientCredentialRecord? Authenticate(string? bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            return null;
        }
        lock (gate)
        {
            return indexByTokenHash.TryGetValue(HashToken(bearerToken), out var record)
                && record.RevokedAtUtc is null
                ? record
                : null;
        }
    }

    public ClientCredentialRecord? Find(string clientId)
    {
        lock (gate)
        {
            return records.FirstOrDefault(
                record => string.Equals(record.ClientId, clientId, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// 撤销（设计 §6，M-2 处置）：先原子持久化 revokedAt，写盘成功后才更新内存索引；
    /// 写盘失败恢复内存状态并抛出，不产生"崩溃可复活"窗口。
    /// </summary>
    public bool Revoke(string clientId)
    {
        lock (gate)
        {
            var record = records.FirstOrDefault(
                candidate => string.Equals(candidate.ClientId, clientId, StringComparison.Ordinal));
            if (record is null)
            {
                return false;
            }
            if (record.RevokedAtUtc is not null)
            {
                return true;
            }
            record.RevokedAtUtc = DateTime.UtcNow.ToString("o");
            try
            {
                SaveLocked();
            }
            catch (Exception)
            {
                record.RevokedAtUtc = null;
                throw;
            }
            RebuildIndexLocked();
            AppendAuditLocked("revoke", $"clientId={record.ClientId} pairingId={record.PairingId}");
            return true;
        }
    }

    /// <summary>
    /// 旧共享令牌升级（设计 §5.2，G-1 处置）：为手机自带 clientId 签发专属凭据，令牌本体只交出一次。
    /// 终态先行：已存在且未撤销 → already-upgraded（绝不重发令牌）；已撤销 → revoked（永不重发）。
    /// 限速只作用于签发路径：同 clientId 两次进入签发至少间隔 3s（内存、单调时钟、进程内）。
    /// </summary>
    public RotateResult Rotate(string clientId, string label)
    {
        if (string.IsNullOrWhiteSpace(clientId) || !Guid.TryParse(clientId.Trim(), out _))
        {
            throw new ArgumentException("clientId 必须为手机生成的 GUID", nameof(clientId));
        }
        var normalized = clientId.Trim();
        lock (gate)
        {
            var existing = Find(normalized);
            if (existing is not null)
            {
                var outcome = existing.RevokedAtUtc is null
                    ? RotateOutcome.AlreadyUpgraded
                    : RotateOutcome.Revoked;
                AppendAuditLocked("rotate",
                    $"clientId={normalized} pairingId={existing.PairingId} result={OutcomeName(outcome)}");
                // 携带既有记录但绝无令牌（G-1：永不重发）。
                return new RotateResult(outcome, existing, null);
            }
            var now = MonotonicClock();
            if (lastRotateAttemptMs.TryGetValue(normalized, out var last)
                && now - last < RotateMinIntervalMs)
            {
                AppendAuditLocked("rotate", $"clientId={normalized} result=rate-limited");
                return RotateResult.StatusOnly(RotateOutcome.RateLimited);
            }
            lastRotateAttemptMs[normalized] = now;
            var record = Issue(
                string.IsNullOrWhiteSpace(label) ? "旧共享令牌升级手机" : label.Trim(),
                new[] { "control", "audio", "settings", "update-request" },
                "rotate-" + Guid.NewGuid().ToString(),
                out var token,
                normalized);
            AppendAuditLocked("rotate",
                $"clientId={record.ClientId} pairingId={record.PairingId} result=issued");
            return new RotateResult(RotateOutcome.Issued, record, token);
        }
    }

    /// <summary>
    /// legacy 应急撤销（设计 §5.3，M-4 处置）：置 legacyRevokedAt 并先原子持久化再生效，
    /// 此后旧共享令牌一切请求 401（LanRequestAuthenticator 的 legacy 分支拒签），逐手机凭据不受影响。
    /// 一次性：无反向开关，重新打开旧入口只能手工删改 clients.json。
    /// </summary>
    public bool RevokeLegacy()
    {
        lock (gate)
        {
            if (legacyRevokedAtUtc is not null)
            {
                return false;
            }
            legacyRevokedAtUtc = DateTime.UtcNow.ToString("o");
            try
            {
                SaveLocked();
            }
            catch (Exception)
            {
                legacyRevokedAtUtc = null;
                throw;
            }
            AppendAuditLocked("legacy-revoke", $"clientId={LegacySharedClientId} result=revoked");
            return true;
        }
    }

    /// <summary>列表（脱敏：不含令牌哈希），供回环管理端点展示。</summary>
    public List<ClientCredentialRecord> ListRedacted()
    {
        lock (gate)
        {
            return records.Select(record => new ClientCredentialRecord
            {
                ClientId = record.ClientId,
                Label = record.Label,
                TokenHash = "",
                PairingId = record.PairingId,
                Scopes = record.Scopes.ToList(),
                IssuedAtUtc = record.IssuedAtUtc,
                RevokedAtUtc = record.RevokedAtUtc,
            }).ToList();
        }
    }

    private void LoadOrInit()
    {
        try
        {
            if (File.Exists(filePath))
            {
                var text = File.ReadAllText(filePath);
                if (text.TrimStart().StartsWith("["))
                {
                    // A1 裸数组旧格式：读取容忍，下一次写盘升级为信封。
                    records = JsonSerializer.Deserialize(
                        text, CredentialsJsonContext.Default.ListClientCredentialRecord)
                        ?? new List<ClientCredentialRecord>();
                }
                else
                {
                    var envelope = JsonSerializer.Deserialize(
                        text, CredentialsJsonContext.Default.ClientCredentialsFileEnvelope);
                    records = envelope?.Clients ?? new List<ClientCredentialRecord>();
                    legacyRevokedAtUtc = envelope?.LegacyRevokedAtUtc;
                }
            }
        }
        catch (JsonException)
        {
            var backupPath = filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            try
            {
                File.Copy(filePath, backupPath, overwrite: true);
            }
            catch (IOException)
            {
                // 备份失败不阻塞启动：原文件保留在原处，可人工取证。
            }
            records = new List<ClientCredentialRecord>();
            legacyRevokedAtUtc = null;
        }
        RebuildIndexLocked();
    }

    private void SaveLocked()
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var temporaryPath = filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
            new ClientCredentialsFileEnvelope
            {
                LegacyRevokedAtUtc = legacyRevokedAtUtc,
                Clients = records,
            },
            CredentialsJsonContext.Default.ClientCredentialsFileEnvelope));
        if (File.Exists(filePath))
        {
            File.Replace(temporaryPath, filePath, null);
        }
        else
        {
            File.Move(temporaryPath, filePath);
        }
    }

    /// <summary>
    /// 追加一行审计（设计 §6）：只含时间、动作、clientId/pairingId 与结果，绝无令牌或材料本体；
    /// 最多保留 500 行（超出丢弃最旧）；尽力而为，失败不阻塞凭据操作。
    /// </summary>
    private void AppendAuditLocked(string action, string details)
    {
        try
        {
            var lines = File.Exists(auditFilePath)
                ? new List<string>(File.ReadAllLines(auditFilePath))
                : new List<string>();
            lines.Add($"{DateTime.UtcNow.ToString("o")}|{action}|{details}");
            if (lines.Count > MaxAuditLines)
            {
                lines = lines[^MaxAuditLines..];
            }
            var temporaryPath = auditFilePath + ".tmp";
            File.WriteAllLines(temporaryPath, lines);
            if (File.Exists(auditFilePath))
            {
                File.Replace(temporaryPath, auditFilePath, null);
            }
            else
            {
                File.Move(temporaryPath, auditFilePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 审计为尽力而为：失败不阻塞签发/撤销/rotate 主流程。
        }
    }

    private static string OutcomeName(RotateOutcome outcome) => outcome switch
    {
        RotateOutcome.Issued => "issued",
        RotateOutcome.AlreadyUpgraded => "already-upgraded",
        RotateOutcome.Revoked => "revoked",
        _ => "rate-limited",
    };

    private void RebuildIndexLocked()
    {
        indexByTokenHash = records
            .Where(record => record.RevokedAtUtc is null)
            .GroupBy(record => record.TokenHash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    internal static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>32 字节随机 → base64url（256bit 熵，≥设计的 192bit 下限）。</summary>
    internal static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
