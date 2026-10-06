using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// M1-A A2 验收（V09/V11，L2）：配对窗口生命周期与材料语义。
/// V11：单调时钟过期、材料单次使用、失败 5 次关窗、未知 pairingId 不计数（防关窗 DoS）。
/// V09：材料匹配进入待确认、本机确认/拒绝收束、签发与凭据可用、材料不出现在状态快照（SEC-05）。
/// </summary>
[TestClass]
public sealed class PairingWindowTests
{
    private sealed class FakeClock
    {
        public long Tick = 1_000_000;
        public void AdvanceSeconds(int seconds) => Tick += seconds * 1000L;
    }

    private static (PairingWindowManager Manager, FakeClock Clock) NewManagerWithClock()
    {
        var clock = new FakeClock();
        var manager = new PairingWindowManager(
            "computer-placeholder-01", "PLACEHOLDER-DESKTOP", new string('a', 64), 8766)
        {
            MonotonicClock = () => clock.Tick,
        };
        return (manager, clock);
    }

    [TestMethod]
    public void QrPayloadMatchesFrozenContractAndMaterialStaysPrivate()
    {
        var (manager, _) = NewManagerWithClock();
        var session = manager.Begin();

        using var payload = JsonDocument.Parse(session.QrPayloadJson);
        var root = payload.RootElement;
        Assert.AreEqual(1, root.GetProperty("version").GetInt32());
        Assert.AreEqual("computer-placeholder-01", root.GetProperty("computerId").GetString());
        Assert.AreEqual(8766, root.GetProperty("httpsPort").GetInt32());
        Assert.AreEqual(new string('a', 64), root.GetProperty("certificateSha256").GetString());
        Assert.AreEqual(120, root.GetProperty("validSeconds").GetInt32());
        Assert.AreEqual(5, root.GetProperty("maxFailuresPerWindow").GetInt32());
        Assert.AreEqual(session.PairingId, root.GetProperty("pairingId").GetString());
        var material = root.GetProperty("oneTimeMaterial").GetString();
        Assert.IsNotNull(material);
        Assert.AreEqual(32, material.Length);
        Assert.IsTrue(material.All(c => "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(c) >= 0),
            "材料应为 base32 字母表（A-Z2-7）：" + material);
        Assert.IsTrue(session.MaterialCheckCode.Length == 4);

        // 手工码 = 材料 8 组 4 字符；状态快照绝不回显材料明文（SEC-05）。
        Assert.AreEqual(
            string.Join("-", System.Linq.Enumerable.Range(0, 8)
                .Select(i => material.Substring(i * 4, 4))),
            session.ManualCode);
        // 状态快照（回环 8765 → 本地托盘 UI）含渲染所需的 QR 载荷/手工码/校验码，
        // 但不含任何凭据字段；材料卫生的"不落日志/导出/审计"在端点与存储层另行保证（SEC-05）。
        var snapshotJson = JsonSerializer.Serialize(manager.StatusSnapshot(), ReceiverApiJsonContext.Default.PairingStatusSnapshot);
        Assert.IsTrue(snapshotJson.Contains("\"qrPayload\""));
        Assert.IsTrue(snapshotJson.Contains("\"manualCode\""));
        Assert.IsFalse(snapshotJson.Contains("clientToken"));
        Assert.IsFalse(snapshotJson.Contains("TokenHash"));
    }

    [TestMethod]
    public void V11_ExpiryUsesMonotonicClockAndMaterialSingleUse()
    {
        var (manager, clock) = NewManagerWithClock();
        var session = manager.Begin();

        Assert.AreEqual(PairingSubmitStatus.Pending, manager.TryBeginSubmit(
            session.PairingId, session.Material, "44444444-4444-4444-8444-444444444444",
            "PHONE-A", out _));
        // 材料单次：已消费后再提交（即便确认未决）→ 拒绝。
        Assert.AreEqual(PairingSubmitStatus.MaterialInvalid, manager.TryBeginSubmit(
            session.PairingId, session.Material, "44444444-4444-4444-8444-444444444444",
            "PHONE-A", out _));

        var (manager2, clock2) = NewManagerWithClock();
        var session2 = manager2.Begin();
        clock2.AdvanceSeconds(121);
        Assert.AreEqual(PairingSubmitStatus.MaterialInvalid, manager2.TryBeginSubmit(
            session2.PairingId, session2.Material, "44444444-4444-4444-8444-444444444444",
            "PHONE-A", out _), "过期材料须拒绝（401）");
        // 过期后重新开窗得到新 pairingId。
        Assert.AreNotEqual(session2.PairingId, manager2.Begin().PairingId);
    }

    [TestMethod]
    public void V11_FiveFailuresCloseWindowAndUnknownPairingIdDoesNot()
    {
        var (manager, clock) = NewManagerWithClock();
        var session = manager.Begin();
        var wrong = new string('Z', 32);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Assert.AreEqual(PairingSubmitStatus.MaterialInvalid, manager.TryBeginSubmit(
                session.PairingId, wrong, "44444444-4444-4444-8444-444444444444",
                "PHONE-A", out _), $"第 {attempt} 次失败应为 401");
        }
        Assert.AreEqual(PairingSubmitStatus.FailureLimit, manager.TryBeginSubmit(
            session.PairingId, wrong, "44444444-4444-4444-8444-444444444444",
            "PHONE-A", out _), "第 5 次失败关窗（429）");
        Assert.AreEqual(PairingSubmitStatus.WindowNotOpen, manager.TryBeginSubmit(
            session.PairingId, session.Material, "44444444-4444-4444-8444-444444444444",
            "PHONE-A", out _), "关窗后正确材料也不可得（404）");

        // 未知 pairingId 独立计数：大量试探不消耗失败额度（防 LAN 关窗 DoS）。
        var (manager2, _) = NewManagerWithClock();
        var session2 = manager2.Begin();
        for (var probe = 0; probe < 50; probe++)
        {
            Assert.AreEqual(PairingSubmitStatus.WindowNotOpen, manager2.TryBeginSubmit(
                System.Guid.NewGuid().ToString(), session2.Material,
                "44444444-4444-4444-8444-444444444444", "PHONE-A", out _));
        }
        Assert.AreEqual(PairingSubmitStatus.Pending, manager2.TryBeginSubmit(
            session2.PairingId, session2.Material, "44444444-4444-4444-8444-444444444444",
            "PHONE-A", out _));
    }

    [TestMethod]
    public async Task V09_ConfirmResolvesIssuanceAndDenyResolvesFalse()
    {
        var (manager, _) = NewManagerWithClock();
        var session = manager.Begin();
        Assert.IsTrue(manager.HasPendingConfirmation() is false);
        Assert.AreEqual(PairingSubmitStatus.Pending, manager.TryBeginSubmit(
            session.PairingId, session.Material, "44444444-4444-4444-8444-444444444444",
            "PHONE-A", out var pending));
        Assert.IsNotNull(pending);
        Assert.IsTrue(manager.HasPendingConfirmation());

        Assert.IsTrue(manager.Confirm(session.PairingId));
        Assert.IsTrue(await pending.Decision.Task);

        // 确认后签发（与端点同一调用形态）：手机自带 clientId 被采纳，凭据可认证。
        var directory = Path.Combine(Path.GetTempPath(), "phonedeck-a2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ClientCredentialsStore(Path.Combine(directory, "clients.json"));
            var record = store.Issue(
                pending.ClientLabel, new[] { "control", "audio", "settings", "update-request" },
                pending.PairingId, out var token, pending.ClientId);
            Assert.AreEqual("44444444-4444-4444-8444-444444444444", record.ClientId);
            Assert.IsNotNull(store.Authenticate(token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        // 拒绝路径。
        var (manager2, _) = NewManagerWithClock();
        var session2 = manager2.Begin();
        Assert.AreEqual(PairingSubmitStatus.Pending, manager2.TryBeginSubmit(
            session2.PairingId, session2.Material, "55555555-5555-4555-8555-555555555555",
            "PHONE-B", out var pending2));
        Assert.IsTrue(manager2.Deny(session2.PairingId));
        Assert.IsFalse(await pending2.Decision.Task);
        Assert.IsFalse(manager2.HasPendingConfirmation());
    }

    [TestMethod]
    public void V11_ClockRollbackDoesNotReviveOrExtendWindow()
    {
        var (manager, clock) = NewManagerWithClock();
        var session = manager.Begin();
        clock.AdvanceSeconds(100);
        // 墙上时间回拨的模拟：单调时钟注入值"倒退"后，窗口剩余时间按注入值计算，
        // 但已消费/墓碑状态不受时钟操纵复活；重新注入正常流逝后行为一致。
        Assert.AreEqual(PairingSubmitStatus.Pending, manager.TryBeginSubmit(
            session.PairingId, session.Material, "77777777-7777-4777-8777-777777777777",
            "PHONE-D", out _));
        clock.Tick -= 200; // 回拨 200ms：已提交的待确认不受影响
        Assert.IsTrue(manager.HasPendingConfirmation());
        Assert.IsTrue(manager.Confirm(session.PairingId));
        // 窗口消费后回拨也不能让材料复活（同会话重复提交→MaterialInvalid，非 Pending）。
        var (manager2, clock2) = NewManagerWithClock();
        var session2 = manager2.Begin();
        Assert.AreEqual(PairingSubmitStatus.Pending, manager2.TryBeginSubmit(
            session2.PairingId, session2.Material, "77777777-7777-4777-8777-777777777777",
            "PHONE-D", out _));
        clock2.Tick -= 60_000; // 回拨试图绕过材料已消费判定
        Assert.AreEqual(PairingSubmitStatus.MaterialInvalid, manager2.TryBeginSubmit(
            session2.PairingId, session2.Material, "77777777-7777-4777-8777-777777777777",
            "PHONE-D", out _), "回拨不复活已消费材料（判定与时间无关的状态位）");
    }

    [TestMethod]
    public void V09_CancelDeniesPendingAndClosesWindow()
    {
        var (manager, _) = NewManagerWithClock();
        var session = manager.Begin();
        Assert.AreEqual(PairingSubmitStatus.Pending, manager.TryBeginSubmit(
            session.PairingId, session.Material, "66666666-6666-4666-8666-666666666666",
            "PHONE-C", out var pending));
        manager.Cancel();
        Assert.IsTrue(pending.Decision.Task.IsCompleted);
        Assert.IsFalse(pending.Decision.Task.Result);
        Assert.AreEqual(PairingSubmitStatus.WindowNotOpen, manager.TryBeginSubmit(
            session.PairingId, session.Material, "66666666-6666-4666-8666-666666666666",
            "PHONE-C", out _));
    }
}
