using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// 同一 Wi-Fi 免扫码连接（/api/lan/pair/request）：单一待确认、限速、超时、确认/拒绝收束、
/// 校验码与 Android 端同一公式（固定向量与 NearbyPairingClientTest 对齐）。
/// </summary>
[TestClass]
public sealed class NearbyPairingTests
{
    private const string Cert = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ClientA = "11111111-1111-4111-8111-111111111111";
    private const string ClientB = "22222222-2222-4222-8222-222222222222";
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    private long tick = 1_000_000;

    private PairingWindowManager NewManager() =>
        new("computer-placeholder-01", "PLACEHOLDER-DESKTOP", Cert, 8766) { MonotonicClock = () => tick };

    [TestMethod]
    public void CheckCodeMatchesSharedVector()
    {
        Assert.AreEqual("3120", PairingWindowManager.NearbyCheckCode(Cert, ClientA, Nonce));
        Assert.AreEqual("3120", PairingWindowManager.NearbyCheckCode(Cert.ToUpperInvariant(), ClientA.ToUpperInvariant(), Nonce));
        Assert.AreNotEqual("3120", PairingWindowManager.NearbyCheckCode(new string('b', 64), ClientA, Nonce),
            "换了证书（中间人）校验码应不同");
    }

    [TestMethod]
    public void ConfirmResolvesAndSnapshotShowsCodeWithoutSecrets()
    {
        var manager = NewManager();
        string? notifiedCode = null;
        manager.NearbyRequested += (_, code) => notifiedCode = code;

        Assert.AreEqual(NearbyRequestStatus.Pending,
            manager.TryBeginNearbyRequest(ClientA, "Galaxy S20", Nonce, out var pending, out var code));
        Assert.IsNotNull(pending);
        Assert.AreEqual("3120", code);
        Assert.AreEqual("3120", notifiedCode);

        var snapshot = JsonSerializer.Serialize(manager.StatusSnapshot(), ReceiverApiJsonContext.Default.PairingStatusSnapshot);
        StringAssert.Contains(snapshot, "\"nearby\":true");
        StringAssert.Contains(snapshot, "\"checkCode\":\"3120\"");
        StringAssert.Contains(snapshot, "Galaxy S20");
        Assert.IsFalse(snapshot.Contains(Nonce), "快照不回显 nonce");

        Assert.IsTrue(manager.Confirm(pending.PairingId));
        Assert.IsTrue(pending.Decision.Task.IsCompletedSuccessfully);
        Assert.IsTrue(pending.Decision.Task.Result);
        Assert.IsFalse(manager.HasPendingConfirmation());
    }

    [TestMethod]
    public void DenyAndBusyAndInvalid()
    {
        var manager = NewManager();
        Assert.AreEqual(NearbyRequestStatus.Invalid,
            manager.TryBeginNearbyRequest("not-a-guid", "x", Nonce, out _, out _));
        Assert.AreEqual(NearbyRequestStatus.Invalid,
            manager.TryBeginNearbyRequest(ClientA, "x", "short", out _, out _));

        Assert.AreEqual(NearbyRequestStatus.Pending,
            manager.TryBeginNearbyRequest(ClientA, "A", Nonce, out var first, out _));
        Assert.AreEqual(NearbyRequestStatus.Busy,
            manager.TryBeginNearbyRequest(ClientB, "B", Nonce, out _, out _));

        Assert.IsTrue(manager.Deny(first!.PairingId));
        Assert.IsFalse(first.Decision.Task.Result);
        Assert.AreEqual(NearbyRequestStatus.Pending,
            manager.TryBeginNearbyRequest(ClientB, "B", Nonce, out _, out _));
    }

    [TestMethod]
    public void ExpiredRequestFreesTheSlotAndWindowBeginIsBlockedWhilePending()
    {
        var manager = NewManager();
        Assert.AreEqual(NearbyRequestStatus.Pending,
            manager.TryBeginNearbyRequest(ClientA, "A", Nonce, out var first, out _));
        Assert.IsTrue(manager.HasPendingConfirmation(), "待确认时回环 begin 应拒绝开窗");
        tick += PairingWindowManager.NearbyConfirmationTimeoutSeconds * 1000L;
        Assert.IsFalse(manager.HasPendingConfirmation());
        Assert.AreEqual(NearbyRequestStatus.Pending,
            manager.TryBeginNearbyRequest(ClientB, "B", Nonce, out _, out _));
        manager.FinishNearby(first!.PairingId);
        Assert.IsFalse(first.Decision.Task.Result);
    }

    [TestMethod]
    public void RateLimitsToSixPerMinute()
    {
        var manager = NewManager();
        for (var i = 0; i < PairingWindowManager.MaxNearbyRequestsPerMinute; i++)
        {
            Assert.AreEqual(NearbyRequestStatus.Pending,
                manager.TryBeginNearbyRequest(ClientA, "A", Nonce, out var pending, out _));
            manager.FinishNearby(pending!.PairingId);
        }
        Assert.AreEqual(NearbyRequestStatus.RateLimited,
            manager.TryBeginNearbyRequest(ClientA, "A", Nonce, out _, out _));
        tick += 60_000;
        Assert.AreEqual(NearbyRequestStatus.Pending,
            manager.TryBeginNearbyRequest(ClientA, "A", Nonce, out _, out _));
    }
}
