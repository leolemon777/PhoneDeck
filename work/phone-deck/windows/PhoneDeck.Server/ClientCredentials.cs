using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// M1-A A1：逐手机客户端凭据（D02 设计 §2/§6）。
/// 服务端只存令牌 SHA-256 哈希，令牌本体只出现在签发响应与手机端；
/// 凭据找回唯一路径是重新扫码配对（rotate 永不重发）。
/// clients.json 原子写（temp + Replace），损坏时备份后从空表启动（CFG-03 纪律）。
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

internal sealed class ClientCredentialsStore
{
    /// <summary>迁移窗口内旧共享令牌的隐式身份（NET-07 / 设计 §5.1）。</summary>
    public const string LegacySharedClientId = "legacy-shared";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string filePath;
    private readonly object gate = new();
    private List<ClientCredentialRecord> records = new();
    private Dictionary<string, ClientCredentialRecord> indexByTokenHash = new(StringComparer.Ordinal);

    public ClientCredentialsStore(string filePath)
    {
        this.filePath = filePath;
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

    /// <summary>签发新凭据；令牌本体只通过 out 参数交出一次，落库的只有哈希。</summary>
    public ClientCredentialRecord Issue(
        string label,
        IEnumerable<string> scopes,
        string pairingId,
        out string token)
    {
        token = NewToken();
        var record = new ClientCredentialRecord
        {
            ClientId = Guid.NewGuid().ToString(),
            Label = string.IsNullOrWhiteSpace(label) ? "未命名手机" : label.Trim(),
            TokenHash = HashToken(token),
            PairingId = pairingId,
            Scopes = scopes.ToList(),
            IssuedAtUtc = DateTime.UtcNow.ToString("o"),
        };
        lock (gate)
        {
            records.Add(record);
            SaveLocked();
            RebuildIndexLocked();
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
                records = JsonSerializer.Deserialize<List<ClientCredentialRecord>>(
                    File.ReadAllText(filePath)) ?? new List<ClientCredentialRecord>();
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
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(records, JsonOptions));
        if (File.Exists(filePath))
        {
            File.Replace(temporaryPath, filePath, null);
        }
        else
        {
            File.Move(temporaryPath, filePath);
        }
    }

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
