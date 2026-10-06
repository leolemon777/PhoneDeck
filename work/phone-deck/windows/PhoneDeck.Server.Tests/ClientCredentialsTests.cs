using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// M1-A A1 验收：L1（凭据存储/鉴权解析）+ V12 子集（L2 假流撤销）。
/// 依据 docs/design/M1A_PAIRING_DESIGN.md §2/§5/§6：服务端只存哈希、rotate 永不重发、
/// 撤销先持久化再生效、长流即时终止、跨手机零影响（≤1s 预算，D03 冻结）。
/// </summary>
[TestClass]
public sealed class ClientCredentialsTests
{
    private string directory = "";

    [TestInitialize]
    public void CreateTempDirectory()
    {
        directory = Path.Combine(Path.GetTempPath(), "phonedeck-a1-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>M1-A A4：clients.json 为 {version, legacyRevokedAt, clients} 对象信封。</summary>
    private ClientCredentialsFileEnvelope ReadPersistedEnvelope() =>
        JsonSerializer.Deserialize<ClientCredentialsFileEnvelope>(File.ReadAllText(StorePath))!;

    [TestMethod]
    public void IssueAuthenticateAndRevokeRoundTripPersists()
    {
        var store = new ClientCredentialsStore(StorePath);
        var record = store.Issue(
            "PHONE-A", new[] { "control", "audio", "settings", "update-request" },
            "pairing-0001", out var token);

        Assert.AreEqual(1, store.ActiveCount);
        var authenticated = store.Authenticate(token);
        Assert.IsNotNull(authenticated);
        Assert.AreEqual(record.ClientId, authenticated.ClientId);
        Assert.AreEqual(4, authenticated.Scopes.Count);

        Assert.IsTrue(store.Revoke(record.ClientId));
        // 持久化先行：撤销后文件里必须已带 revokedAt（M-2 处置）；信封字段齐备（M1-A A4）。
        var envelope = ReadPersistedEnvelope();
        Assert.AreEqual(1, envelope.Version);
        Assert.IsNull(envelope.LegacyRevokedAtUtc);
        Assert.AreEqual(1, envelope.Clients.Count);
        Assert.IsNotNull(envelope.Clients[0].RevokedAtUtc);

        Assert.IsNull(store.Authenticate(token));
        // 重启语义：从同一文件重建的实例仍拒绝该令牌。
        var reloaded = new ClientCredentialsStore(StorePath);
        Assert.IsNull(reloaded.Authenticate(token));
        Assert.AreEqual(0, reloaded.ActiveCount);
    }

    [TestMethod]
    public void PlainTokenNeverTouchesDiskOrListing()
    {
        var store = new ClientCredentialsStore(StorePath);
        store.Issue("PHONE-A", new[] { "control" }, "pairing-0002", out var token);

        Assert.IsFalse(File.ReadAllText(StorePath).Contains(token));
        foreach (var listed in store.ListRedacted())
        {
            Assert.AreEqual("", listed.TokenHash);
            Assert.AreEqual("PHONE-A", listed.Label);
        }
    }

    [TestMethod]
    public void CorruptFileIsBackedUpAndStartsEmpty()
    {
        File.WriteAllText(StorePath, "{ not valid json !!!");
        var store = new ClientCredentialsStore(StorePath);
        Assert.AreEqual(0, store.ActiveCount);
        Assert.AreEqual(1, Directory.GetFiles(directory, "clients.json.corrupt-*").Length);
        // 损坏后仍可正常签发并落盘。
        store.Issue("PHONE-B", new[] { "control" }, "pairing-0003", out var token);
        Assert.IsNotNull(new ClientCredentialsStore(StorePath).Authenticate(token));
    }

    [TestMethod]
    public void RevokeWriteFailureKeepsInMemoryStateConsistent()
    {
        var store = new ClientCredentialsStore(StorePath);
        var record = store.Issue("PHONE-A", new[] { "control" }, "pairing-0004", out var token);

        // 独占打开文件，使原子写失败。
        using (new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                store.Revoke(record.ClientId);
                Assert.Fail("写盘被锁时撤销应抛出");
            }
            catch (IOException)
            {
            }
        }
        // 写盘失败 → 内存回滚：令牌仍有效，磁盘仍无 revokedAt（设计 §6 M-2）。
        Assert.IsNotNull(store.Authenticate(token));
        var persisted = ReadPersistedEnvelope().Clients;
        Assert.IsNull(persisted[0].RevokedAtUtc);
        // 锁释放后重试成功。
        Assert.IsTrue(store.Revoke(record.ClientId));
        Assert.IsNull(store.Authenticate(token));
    }

    [TestMethod]
    public void ResolveHandlesBearerLegacyAndLoopback()
    {
        var store = new ClientCredentialsStore(StorePath);
        var record = store.Issue("PHONE-A", new[] { "control" }, "pairing-0005", out var token);
        const string sharedToken = "legacy-shared-secret";

        // 回环端口（8765）：放行、无 clientId。
        var loopback = LanRequestAuthenticator.Resolve(
            8765, null, null, sharedToken, store);
        Assert.IsTrue(loopback.Authorized);
        Assert.IsNull(loopback.ClientId);

        // 新式 bearer：通过并返回 clientId。
        var bearer = LanRequestAuthenticator.Resolve(
            8766, null, "Bearer " + token, sharedToken, store);
        Assert.IsTrue(bearer.Authorized);
        Assert.AreEqual(record.ClientId, bearer.ClientId);

        // 无效 bearer：拒绝，且不回退旧头（防混淆降级）。
        Assert.IsFalse(LanRequestAuthenticator.Resolve(
            8766, sharedToken, "Bearer wrong-token", sharedToken, store).Authorized);

        // 旧共享令牌：通过并映射 legacy-shared（迁移窗口，设计 §5.1）。
        var legacy = LanRequestAuthenticator.Resolve(
            8766, sharedToken, null, sharedToken, store);
        Assert.IsTrue(legacy.Authorized);
        Assert.AreEqual(ClientCredentialsStore.LegacySharedClientId, legacy.ClientId);

        // 已撤销的 bearer：拒绝。
        store.Revoke(record.ClientId);
        Assert.IsFalse(LanRequestAuthenticator.Resolve(
            8766, null, "Bearer " + token, sharedToken, store).Authorized);

        // 什么都没有：拒绝。
        Assert.IsFalse(LanRequestAuthenticator.Resolve(
            8766, null, null, sharedToken, store).Authorized);
    }

    /// <summary>V12 子集（L2 假流）：撤销在 ≤1s 内终止该客户端长流，另一手机不受影响。</summary>
    [TestMethod]
    public async Task RevocationCancelsFakeStreamWithinBudgetAndSparesOtherClient()
    {
        var store = new ClientCredentialsStore(StorePath);
        var registry = new ClientSessionRegistry();
        var clientA = store.Issue("PHONE-A", new[] { "audio" }, "pairing-0006", out _);
        store.Issue("PHONE-B", new[] { "audio" }, "pairing-0007", out _);

        var tokenA = registry.Register(clientA.ClientId);
        var tokenB = registry.Register("client-b-placeholder");

        var streamA = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, tokenA);
                return "alive";
            }
            catch (OperationCanceledException)
            {
                return "cancelled";
            }
        });

        await Task.Delay(100);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 撤销编排（与 /api/admin/clients/revoke 相同顺序：先持久化，再终止长流）。
        Assert.IsTrue(store.Revoke(clientA.ClientId));
        Assert.IsTrue(registry.Cancel(clientA.ClientId));

        var outcome = await streamA;
        stopwatch.Stop();
        Assert.AreEqual("cancelled", outcome);
        Assert.IsTrue(stopwatch.ElapsedMilliseconds < 1000,
            $"撤销生效 {stopwatch.ElapsedMilliseconds}ms，超过 D03 冻结预算 1000ms");
        Assert.IsFalse(tokenB.IsCancellationRequested, "另一手机的长流不得受影响（V12）");
        // 撤销后再次注册得到全新令牌（旧令牌不复活）。
        Assert.IsFalse(registry.Register(clientA.ClientId).IsCancellationRequested);
    }
}
