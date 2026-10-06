using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// M1-A A4 验收（V17 服务端子集，L1/L2）：旧共享令牌升级与 legacy 应急撤销。
/// 依据 docs/design/M1A_PAIRING_DESIGN.md §5：rotate 单次签发（G-1 永不重发，重放只回
/// already-upgraded 三字段状态）、revoked 永不重发、仅旧共享令牌可调（Bearer 调用者 403）、
/// 同 clientId 简单限速（≥3s、单调时钟）；legacy 撤销先持久化再生效，此后旧令牌一切请求
/// 401 且逐手机凭据不受影响；clients.json 升级为 {version,legacyRevokedAt,clients} 信封
/// （读取容忍旧裸数组）；签发/撤销/rotate/legacy 撤销逐条审计（脱敏，≤500 行）。
/// 端点映射：Revoked→403、RateLimited→429、非 legacy 调用者→403「请使用旧共享令牌升级」。
/// </summary>
[TestClass]
public sealed class RotateLegacyTests
{
    private string directory = "";

    [TestInitialize]
    public void CreateTempDirectory()
    {
        directory = Path.Combine(Path.GetTempPath(), "phonedeck-a4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TestCleanup]
    public void RemoveTempDirectory()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string StorePath => Path.Combine(directory, "clients.json");

    private string AuditPath => Path.Combine(directory, "clients-audit.log");

    [TestMethod]
    public void RotateIssuesOnceAndCredentialAuthenticates()
    {
        var store = new ClientCredentialsStore(StorePath);
        var clientId = "99999999-9999-4999-8999-999999999999";

        var result = store.Rotate(clientId, "PHONE-A");
        Assert.AreEqual(RotateOutcome.Issued, result.Outcome);
        Assert.IsNotNull(result.Token);
        var record = result.Record!;
        // 手机自带 clientId 被采纳；pairingId 以 rotate- 前缀区分扫码配对来源。
        Assert.AreEqual(clientId, record.ClientId);
        StringAssert.StartsWith(record.PairingId, "rotate-");
        CollectionAssert.AreEquivalent(
            new[] { "control", "audio", "settings", "update-request" }, record.Scopes);

        // 签发即可认证：Bearer 鉴权回到自身 clientId（与端点 issued 响应同一条凭据）。
        var authenticated = store.Authenticate(result.Token);
        Assert.IsNotNull(authenticated);
        Assert.AreEqual(clientId, authenticated.ClientId);
        var resolved = LanRequestAuthenticator.Resolve(
            8766, null, "Bearer " + result.Token, "shared-placeholder", store);
        Assert.IsTrue(resolved.Authorized);
        Assert.AreEqual(clientId, resolved.ClientId);
    }

    [TestMethod]
    public void RotateRepeatIsAlreadyUpgradedWithoutToken()
    {
        var store = new ClientCredentialsStore(StorePath);
        var clientId = Guid.NewGuid().ToString();
        var first = store.Rotate(clientId, "PHONE-A");
        Assert.AreEqual(RotateOutcome.Issued, first.Outcome);
        var token = first.Token!;
        Assert.IsFalse(string.IsNullOrWhiteSpace(token));

        // 同 clientId 再次 rotate：already-upgraded，绝无令牌本体（G-1 处置）。
        var second = store.Rotate(clientId, "PHONE-A");
        Assert.AreEqual(RotateOutcome.AlreadyUpgraded, second.Outcome);
        Assert.IsNull(second.Token);
        // 原令牌仍有效且未变。
        Assert.IsNotNull(store.Authenticate(token));
        // 令牌本体不出现在 clients.json 与审计文件。
        Assert.IsFalse(File.ReadAllText(StorePath).Contains(token));
        Assert.IsFalse(File.ReadAllText(AuditPath).Contains(token));
    }

    [TestMethod]
    public void RotateAfterRevokeIsRejected()
    {
        var store = new ClientCredentialsStore(StorePath);
        var clientId = Guid.NewGuid().ToString();
        var first = store.Rotate(clientId, "PHONE-A");
        Assert.AreEqual(RotateOutcome.Issued, first.Outcome);

        Assert.IsTrue(store.Revoke(clientId));
        // 已撤销 clientId 永不重发（端点映射 403）。
        var afterRevoke = store.Rotate(clientId, "PHONE-A");
        Assert.AreEqual(RotateOutcome.Revoked, afterRevoke.Outcome);
        Assert.IsNull(afterRevoke.Token);
        // 撤销后的令牌也早已失效。
        Assert.IsNull(store.Authenticate(first.Token));
    }

    /// <summary>Bearer 调用者 403：rotate 仅接受旧共享令牌鉴权（设计 §5.1/§5.2）。
    /// 端点用 IsLegacySharedCaller(解析出的 clientId) 判定，这里验证整条判定链。</summary>
    [TestMethod]
    public void RotateRejectsBearerCallerDecision()
    {
        var store = new ClientCredentialsStore(StorePath);
        const string sharedToken = "legacy-shared-secret";
        var issued = store.Rotate(Guid.NewGuid().ToString(), "PHONE-A");

        // Bearer 调用者：鉴权解析回自身 clientId（≠ legacy-shared）→ 判定非 legacy → 403。
        var bearer = LanRequestAuthenticator.Resolve(
            8766, sharedToken, "Bearer " + issued.Token, sharedToken, store);
        Assert.IsTrue(bearer.Authorized);
        Assert.IsFalse(LanRequestAuthenticator.IsLegacySharedCaller(bearer.ClientId));

        // 旧共享令牌调用者：解析为 legacy-shared → 可调 rotate。
        var legacy = LanRequestAuthenticator.Resolve(8766, sharedToken, null, sharedToken, store);
        Assert.IsTrue(legacy.Authorized);
        Assert.IsTrue(LanRequestAuthenticator.IsLegacySharedCaller(legacy.ClientId));

        // 无 clientId（如回环放行）不得视为 legacy 调用者；无效 bearer 在中间件已被 401。
        Assert.IsFalse(LanRequestAuthenticator.IsLegacySharedCaller(null));
        Assert.IsFalse(LanRequestAuthenticator.Resolve(
            8766, sharedToken, "Bearer wrong-token", sharedToken, store).Authorized);
    }

    [TestMethod]
    public void LegacyRevokeDeniesSharedTokenButKeepsPerPhoneCredentials()
    {
        var store = new ClientCredentialsStore(StorePath);
        const string sharedToken = "legacy-shared-secret";
        var upgraded = store.Rotate(Guid.NewGuid().ToString(), "PHONE-A");
        Assert.AreEqual(RotateOutcome.Issued, upgraded.Outcome);
        var clientToken = upgraded.Token;

        // 撤销前：旧共享令牌映射 legacy-shared（迁移窗口共存，设计 §5.1）。
        var before = LanRequestAuthenticator.Resolve(8766, sharedToken, null, sharedToken, store);
        Assert.IsTrue(before.Authorized);
        Assert.AreEqual(ClientCredentialsStore.LegacySharedClientId, before.ClientId);

        Assert.IsTrue(store.RevokeLegacy());
        // 先持久化再生效：文件信封已带 legacyRevokedAt（M-2 处置）。
        var envelope = JsonSerializer.Deserialize<ClientCredentialsFileEnvelope>(
            File.ReadAllText(StorePath));
        Assert.IsNotNull(envelope);
        Assert.IsNotNull(envelope!.LegacyRevokedAtUtc);

        // 此后旧共享令牌一切请求 401（正确与错误令牌同拒，抗枚举）。
        Assert.IsFalse(LanRequestAuthenticator.Resolve(
            8766, sharedToken, null, sharedToken, store).Authorized);
        Assert.IsFalse(LanRequestAuthenticator.Resolve(
            8766, "wrong-token", null, sharedToken, store).Authorized);

        // 逐手机凭据不受影响。
        var kept = LanRequestAuthenticator.Resolve(
            8766, null, "Bearer " + clientToken, sharedToken, store);
        Assert.IsTrue(kept.Authorized);

        // 重启语义：从盘重建的实例仍拒绝旧令牌、接受新凭据。
        var reloaded = new ClientCredentialsStore(StorePath);
        Assert.IsFalse(LanRequestAuthenticator.Resolve(
            8766, sharedToken, null, sharedToken, reloaded).Authorized);
        Assert.IsTrue(LanRequestAuthenticator.Resolve(
            8766, null, "Bearer " + clientToken, sharedToken, reloaded).Authorized);

        // 一次性：重复撤销幂等返回 false，状态不变。
        Assert.IsFalse(store.RevokeLegacy());
        Assert.IsTrue(store.LegacyRevoked);
    }

    [TestMethod]
    public void AuditLogRecordsActionsWithoutTokens()
    {
        var store = new ClientCredentialsStore(StorePath);
        var issued = store.Rotate("11111111-2222-4333-8444-555555555555", "PHONE-A");
        Assert.AreEqual(RotateOutcome.Issued, issued.Outcome);
        Assert.IsTrue(store.Revoke(issued.Record!.ClientId));
        Assert.AreEqual(RotateOutcome.Revoked,
            store.Rotate("11111111-2222-4333-8444-555555555555", "PHONE-A").Outcome);
        Assert.IsTrue(store.RevokeLegacy());

        // 签发/撤销/rotate（含结果）/legacy 撤销逐条留痕（设计 §6）。
        Assert.IsTrue(File.Exists(AuditPath));
        var audit = File.ReadAllLines(AuditPath);
        Assert.IsTrue(audit.Any(line => line.Contains("|issue|")));
        Assert.IsTrue(audit.Any(line => line.Contains("|rotate|") && line.Contains("result=issued")));
        Assert.IsTrue(audit.Any(line => line.Contains("|rotate|") && line.Contains("result=revoked")));
        Assert.IsTrue(audit.Any(line => line.Contains("|revoke|")));
        Assert.IsTrue(audit.Any(line => line.Contains("|legacy-revoke|")));
        // 审计与存储绝不含令牌本体（脱敏）。
        Assert.IsTrue(audit.All(line => !line.Contains(issued.Token!)));
        Assert.IsFalse(File.ReadAllText(StorePath).Contains(issued.Token!));
    }

    /// <summary>同 clientId 简单限速：进入签发路径的两次尝试至少间隔 3s（单调时钟，内存态）。
    /// 可达路径示例：签发持久化失败（写盘被锁）→ 立即重试被限速；时钟推进后放行。</summary>
    [TestMethod]
    public void RotateRateLimitEnforcesMinimumInterval()
    {
        var tick = 0L;
        var store = new ClientCredentialsStore(StorePath) { MonotonicClock = () => tick };
        store.Issue("PHONE-SEED", new[] { "control" }, "pairing-seed", out _);
        var clientId = Guid.NewGuid().ToString();

        using (new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                store.Rotate(clientId, "PHONE-A");
                Assert.Fail("写盘被锁时 rotate 签发应抛出（中止，可安全重试）");
            }
            catch (IOException)
            {
            }
            // 签发失败已回滚：不留幽灵记录。
            Assert.IsNull(store.Find(clientId));
            // 同 clientId 立即重试（间隔 0 < 3s）→ 限速（端点映射 429）。
            Assert.AreEqual(RotateOutcome.RateLimited, store.Rotate(clientId, "PHONE-A").Outcome);
        }

        // 单调时钟推进超过 3s → 正常签发，clientId 沿用手机提供值。
        tick += 3001;
        var late = store.Rotate(clientId, "PHONE-A");
        Assert.AreEqual(RotateOutcome.Issued, late.Outcome);
        Assert.AreEqual(clientId, late.Record!.ClientId);
        Assert.IsNotNull(store.Authenticate(late.Token));
    }

    /// <summary>clients.json 读取容忍 A1 裸数组旧格式，写盘后升级为信封（设计 §6/任务 A4）。</summary>
    [TestMethod]
    public void LegacyBareArrayFileUpgradesToEnvelope()
    {
        var clientId = "44444444-4444-4444-8444-444444444444";
        File.WriteAllText(StorePath, JsonSerializer.Serialize(new List<ClientCredentialRecord>
        {
            new ClientCredentialRecord
            {
                ClientId = clientId,
                Label = "OLD",
                TokenHash = "hash-placeholder",
                PairingId = "pairing-old",
                Scopes = new List<string> { "control" },
                IssuedAtUtc = "2026-01-01T00:00:00Z",
            },
        }));

        var store = new ClientCredentialsStore(StorePath);
        Assert.AreEqual(1, store.ActiveCount);
        Assert.IsNotNull(store.Find(clientId));
        Assert.IsFalse(store.LegacyRevoked);

        // 任意写操作后升级为信封，记录与撤销状态保留。
        Assert.IsTrue(store.Revoke(clientId));
        var envelope = JsonSerializer.Deserialize<ClientCredentialsFileEnvelope>(
            File.ReadAllText(StorePath));
        Assert.IsNotNull(envelope);
        Assert.AreEqual(1, envelope!.Clients.Count);
        Assert.IsNull(envelope.LegacyRevokedAtUtc);
        Assert.IsNotNull(envelope.Clients[0].RevokedAtUtc);
    }

    /// <summary>审计有界轮转：最多保留 500 行，超出丢弃最旧（设计 §6）。</summary>
    [TestMethod]
    public void AuditLogIsCappedAtFiveHundredLines()
    {
        var filler = System.Linq.Enumerable.Range(0, 600)
            .Select(index => "filler-" + index)
            .ToArray();
        File.WriteAllLines(AuditPath, filler);

        var store = new ClientCredentialsStore(StorePath);
        store.Issue("PHONE-A", new[] { "control" }, "pairing-cap", out _);

        var lines = File.ReadAllLines(AuditPath);
        Assert.AreEqual(ClientCredentialsStore.MaxAuditLines, lines.Length);
        Assert.IsTrue(lines[^1].Contains("|issue|"), "最新一条审计应是本次签发");
        Assert.IsTrue(lines.Any(line => line.Contains("filler-101")), "窗口内的旧记录保留");
        Assert.IsFalse(lines.Any(line => line.Contains("filler-100")), "超出上限的最旧记录被轮转掉");
    }
}
