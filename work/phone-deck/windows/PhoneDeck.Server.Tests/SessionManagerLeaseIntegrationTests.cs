using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// M1-B 收尾 / V20+V25 集成台架（L2）：把 SessionLeaseRegistry 挂上真实
/// DictationSessionManager（注入假引擎/假音频）走完整时序——
/// 停止优先（R1：Stop 后同 sessionId 迟到 start 拒绝且零引擎副作用）、
/// 旧 stop 不清新会话（R2）、失败启动同会话可重试（T03→T09 无墓碑）、
/// STARTING/ACTIVE 中第二会话被拒（单所有者）。
/// </summary>
[TestClass]
public sealed class SessionManagerLeaseIntegrationTests
{
    private sealed class FakeAudio : IPhoneAudioSessionController
    {
        public bool WaitForSessionResult { get; set; } = true;
        public bool WaitForSessionEndResult { get; set; } = true;

        public bool IsSessionActive(string sessionId) => true;

        public bool WaitForSessionActive(string sessionId, int timeoutMilliseconds) =>
            WaitForSessionResult;

        public bool StopSession(string sessionId) => true;

        public void BeginPlayback(string sessionId)
        {
        }

        public bool WaitForSessionEnd(string sessionId, int timeoutMilliseconds) =>
            WaitForSessionEndResult;
    }

    private sealed class FakeEngine : IVoiceEngineController
    {
        public Queue<bool?> CapturingChecks { get; } = new();
        public Queue<bool?> WaitResults { get; } = new();
        public int ToggleCount { get; private set; }
        public ManualResetEventSlim? StartGate { get; set; }

        public string EngineDisplayName => "FakeEngine";
        public IReadOnlyCollection<string> Modes => new[] { "dictation" };
        public bool? CanVerifyVirtualCable => false;
        public bool UsesVirtualCable => true;

        public bool? IsCapturing() => CapturingChecks.Count > 0
            ? CapturingChecks.Dequeue()
            : false;

        public bool? WaitForCapturing(bool expected, int timeoutMilliseconds)
        {
            StartGate?.Wait();
            return WaitResults.Count > 0 ? WaitResults.Dequeue() : expected;
        }

        public bool IsModeConfigured(string mode) => true;

        public bool BeginOnce(string requestId, string mode)
        {
            ToggleCount++;
            return false;
        }

        public bool End(string mode, string? requestId) => true;

        public void Dispose()
        {
        }
    }

    private static DictationSessionManager NewManager(FakeEngine engine, FakeAudio audio)
    {
        var registry = new SessionLeaseRegistry();
        return new DictationSessionManager(audio, engine, registry);
    }

    /// <summary>成功启动一次会话（引擎确认采集 + 音频就绪）。</summary>
    private static void StartSuccessfully(
        DictationSessionManager manager, FakeEngine engine, string sessionId)
    {
        engine.CapturingChecks.Enqueue(false);
        engine.WaitResults.Enqueue(true);
        manager.Start(sessionId, $"start-{sessionId[..8]}", "dictation");
    }

    private static void StopCleanly(
        DictationSessionManager manager, FakeEngine engine, string sessionId)
    {
        engine.CapturingChecks.Enqueue(true);
        engine.WaitResults.Enqueue(false);
        manager.Stop(sessionId, $"stop-{sessionId[..8]}");
    }

    [TestMethod]
    public void V25_LateStartAfterStopIsRejectedWithZeroEngineSideEffects()
    {
        var engine = new FakeEngine();
        var manager = NewManager(engine, new FakeAudio());
        var sessionId = Guid.NewGuid().ToString();
        StartSuccessfully(manager, engine, sessionId);
        var togglesAfterStart = engine.ToggleCount;
        StopCleanly(manager, engine, sessionId);

        // 停止优先（R1）：同 sessionId 迟到 start 必须在引擎触碰前被拒。
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(sessionId, "late-start", "dictation"));
        StringAssert.Contains(exception.Message, "已终止");
        Assert.AreEqual(togglesAfterStart, engine.ToggleCount,
            "迟到 start 不得产生任何引擎副作用（校验在 IsCapturing 探测之前完成）");
        Assert.IsFalse(manager.IsActive);
    }

    [TestMethod]
    public void V25_StaleStopDoesNotClearNewSession()
    {
        var engine = new FakeEngine();
        var manager = NewManager(engine, new FakeAudio());
        var sessionA = Guid.NewGuid().ToString();
        var sessionB = Guid.NewGuid().ToString();
        StartSuccessfully(manager, engine, sessionA);
        StopCleanly(manager, engine, sessionA);
        StartSuccessfully(manager, engine, sessionB);
        Assert.IsTrue(manager.IsActive);

        // 旧会话 A 的迟到 stop：不停止 B（R2），音频清理只针对请求的旧会话。
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Stop(sessionA, "stale-stop"));
        Assert.IsTrue(manager.IsActive, "新会话 B 必须不受旧 stop 影响");
        Assert.AreEqual(sessionB, manager.ActiveSessionId);
    }

    [TestMethod]
    public void V25_FailedStartCanBeRetriedWithSameSessionId()
    {
        var engine = new FakeEngine();
        var manager = NewManager(engine, new FakeAudio());
        var sessionId = Guid.NewGuid().ToString();

        // 第一次启动失败：引擎探针读了 false（未开始）→ ResetFailedStart → Abandon（无墓碑）。
        engine.CapturingChecks.Enqueue(false);
        engine.WaitResults.Enqueue(false);
        engine.CapturingChecks.Enqueue(false);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(sessionId, "attempt-1", "dictation"));
        Assert.IsFalse(manager.IsActive);

        // 同 sessionId 重试成功（T03→T09：失败清理不写墓碑）。
        StartSuccessfully(manager, engine, sessionId);
        Assert.IsTrue(manager.IsActive);
        StopCleanly(manager, engine, sessionId);
    }

    [TestMethod]
    public void V20_SecondSessionRejectedWhileFirstActive()
    {
        var engine = new FakeEngine();
        var manager = NewManager(engine, new FakeAudio());
        var sessionA = Guid.NewGuid().ToString();
        var sessionB = Guid.NewGuid().ToString();
        StartSuccessfully(manager, engine, sessionA);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(sessionB, "second", "dictation"));
        StringAssert.Contains(exception.Message, "另一个语音听写会话");
        Assert.AreEqual(sessionA, manager.ActiveSessionId,
            "被拒的 B 不得抢占 A（单所有者，DEV-04）");

        // 停止 A 后 B 可以开始（目标切换先停旧会话，V20 语义）。
        StopCleanly(manager, engine, sessionA);
        StartSuccessfully(manager, engine, sessionB);
        Assert.AreEqual(sessionB, manager.ActiveSessionId);
    }

    [TestMethod]
    public async Task V20_SecondSessionRejectedWhileFirstStarting()
    {
        var engine = new FakeEngine
        {
            StartGate = new ManualResetEventSlim(false),
        };
        var manager = NewManager(engine, new FakeAudio());
        var sessionA = Guid.NewGuid().ToString();
        var sessionB = Guid.NewGuid().ToString();
        engine.CapturingChecks.Enqueue(false);

        var starting = Task.Run(() =>
        {
            engine.WaitResults.Enqueue(true);
            manager.Start(sessionA, "start-a", "dictation");
        });
        // 等到引擎启动请求已发出（STARTING 态内）再发第二个 start。
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (engine.ToggleCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.IsTrue(engine.ToggleCount >= 1, "A 的引擎启动请求应已发出");

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(sessionB, "start-b", "dictation"));
        StringAssert.Contains(exception.Message, "另一个语音听写会话");

        engine.StartGate.Set();
        await starting;
        Assert.IsTrue(manager.IsActive);
        Assert.AreEqual(sessionA, manager.ActiveSessionId);
        StopCleanly(manager, engine, sessionA);
    }
}
