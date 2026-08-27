using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class DictationSessionManagerTests
{
    [TestMethod]
    public void FailedStartRetryWithSameRequestIdNeverReportsSuccess()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeTypelessController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = Guid.NewGuid().ToString();
        const string requestId = "start-request";

        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(false);
        typeless.IsCapturingResults.Enqueue(false);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(sessionId, requestId));
        Assert.IsFalse(manager.IsActive);

        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(false);
        typeless.IsCapturingResults.Enqueue(false);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(sessionId, requestId));
        Assert.IsFalse(manager.IsActive);
        Assert.AreEqual(1, typeless.ToggleCount,
            "重复 requestId 不能再次切换，也不能绕过真实启动状态检查");
    }

    [TestMethod]
    public void StartRejectsUnavailableStateProbeBeforeToggling()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeTypelessController();
        var manager = new DictationSessionManager(audio, typeless);

        typeless.IsCapturingResults.Enqueue(null);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(Guid.NewGuid().ToString(), "start-request"));

        StringAssert.Contains(exception.Message, "无法读取");
        Assert.AreEqual(0, typeless.ToggleCount);
        Assert.IsFalse(manager.IsActive);
    }

    [TestMethod]
    public void StartWaitsForConcurrentAudioStreamRegistration()
    {
        var audio = new FakeAudioSessionController
        {
            WaitForSessionResult = true
        };
        var typeless = new FakeTypelessController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = Guid.NewGuid().ToString();

        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(true);

        Assert.IsFalse(manager.Start(sessionId, "start-request"));
        Assert.AreEqual(1, audio.WaitForSessionCalls);
        Assert.IsTrue(manager.IsActive);
    }

    [TestMethod]
    public void StopWithUnavailableProbeReportsFailureButReleasesOwnership()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeTypelessController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = StartSuccessfulSession(manager, typeless);

        typeless.IsCapturingResults.Enqueue(null);
        typeless.WaitResults.Enqueue(null);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Stop(sessionId, "stop-request"));

        StringAssert.Contains(exception.Message, "未被确认");
        Assert.IsFalse(manager.IsActive);
        Assert.AreEqual(1, audio.StopCalls);
        Assert.AreEqual(2, typeless.ToggleCount);
    }

    [TestMethod]
    public void AudioEndedFailureCannotPermanentlyLockNextSession()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeTypelessController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = StartSuccessfulSession(manager, typeless);

        typeless.IsCapturingResults.Enqueue(true);
        typeless.WaitResults.Enqueue(false);
        typeless.IsCapturingResults.Enqueue(true);
        typeless.WaitResults.Enqueue(false);

        manager.AudioEnded(sessionId);

        Assert.IsFalse(manager.IsActive);
        Assert.IsNull(manager.ActiveSessionId);
        Assert.AreEqual(3, typeless.ToggleCount);
    }

    [TestMethod]
    public void StopRechecksStateBeforeSendingSecondToggle()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeTypelessController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = StartSuccessfulSession(manager, typeless);

        typeless.IsCapturingResults.Enqueue(true);
        typeless.WaitResults.Enqueue(false);
        typeless.IsCapturingResults.Enqueue(false);

        var duplicate = manager.Stop(sessionId, "stop-request");

        Assert.IsFalse(duplicate);
        Assert.IsFalse(manager.IsActive);
        Assert.AreEqual(2, typeless.ToggleCount,
            "状态已经停止时不能再发第二个切换键把 Typeless 重新打开");
    }

    private static string StartSuccessfulSession(
        DictationSessionManager manager,
        FakeTypelessController typeless)
    {
        var sessionId = Guid.NewGuid().ToString();
        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(true);
        Assert.IsFalse(manager.Start(sessionId, "initial-start-request"));
        Assert.IsTrue(manager.IsActive);
        return sessionId;
    }

    private sealed class FakeAudioSessionController : IPhoneAudioSessionController
    {
        public int StopCalls { get; private set; }
        public int WaitForSessionCalls { get; private set; }
        public bool WaitForSessionResult { get; set; } = true;

        public bool IsSessionActive(string sessionId) => true;

        public bool WaitForSessionActive(string sessionId, int timeoutMilliseconds)
        {
            WaitForSessionCalls++;
            return WaitForSessionResult;
        }

        public bool StopSession(string sessionId)
        {
            StopCalls++;
            return true;
        }
    }

    private sealed class FakeTypelessController : ITypelessController
    {
        private readonly HashSet<string> requestIds = new(StringComparer.Ordinal);

        public Queue<bool?> IsCapturingResults { get; } = new();
        public Queue<bool?> WaitResults { get; } = new();
        public int ToggleCount { get; private set; }
        public bool UsesVirtualCable { get; set; } = true;

        public bool? IsCapturing() => IsCapturingResults.Count > 0
            ? IsCapturingResults.Dequeue()
            : false;

        public bool? WaitForCapturing(bool expected, int timeoutMilliseconds) =>
            WaitResults.Count > 0 ? WaitResults.Dequeue() : expected;

        public bool ToggleOnce(string requestId)
        {
            if (!requestIds.Add(requestId))
            {
                return true;
            }
            ToggleCount++;
            return false;
        }

        public void Toggle()
        {
            ToggleCount++;
        }
    }
}
