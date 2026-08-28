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
            manager.Start(sessionId, requestId, "dictation"));
        Assert.IsFalse(manager.IsActive);

        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(false);
        typeless.IsCapturingResults.Enqueue(false);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(sessionId, requestId, "dictation"));
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
            manager.Start(Guid.NewGuid().ToString(), "start-request", null));

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

        Assert.IsFalse(manager.Start(sessionId, "start-request", "dictation"));
        Assert.AreEqual(1, audio.WaitForSessionCalls);
        Assert.IsTrue(manager.IsActive);
    }

    [TestMethod]
    public void StartRejectsUnconfiguredModeBeforeWaitingForAudio()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeTypelessController();
        typeless.ConfiguredModes.Remove("translation");
        var manager = new DictationSessionManager(audio, typeless);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(Guid.NewGuid().ToString(), "start-request", "translation"));

        StringAssert.Contains(exception.Message, "未配置");
        Assert.AreEqual(0, typeless.ToggleCount);
        Assert.AreEqual(0, audio.WaitForSessionCalls,
            "模式未配置时不应等待音频会话登记");
        Assert.IsFalse(manager.IsActive);
    }

    [TestMethod]
    public void StopReusesModeFromStart()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeTypelessController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = Guid.NewGuid().ToString();

        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(true);
        Assert.IsFalse(manager.Start(sessionId, "start-request", "translation"));
        Assert.AreEqual("translation", typeless.LastMode);

        typeless.IsCapturingResults.Enqueue(true);
        typeless.WaitResults.Enqueue(false);
        typeless.IsCapturingResults.Enqueue(false);

        Assert.IsFalse(manager.Stop(sessionId, "stop-request"));
        Assert.AreEqual("translation", typeless.LastMode,
            "停止时必须沿用启动时的模式键，不能用当前选中的模式切换");
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
        Assert.IsFalse(manager.Start(sessionId, "initial-start-request", "dictation"));
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
        public HashSet<string> ConfiguredModes { get; } = new()
            { "dictation", "translation", "ask" };
        public int ToggleCount { get; private set; }
        public string LastMode { get; private set; } = "dictation";
        public bool UsesVirtualCable { get; set; } = true;

        public bool? IsCapturing() => IsCapturingResults.Count > 0
            ? IsCapturingResults.Dequeue()
            : false;

        public bool? WaitForCapturing(bool expected, int timeoutMilliseconds) =>
            WaitResults.Count > 0 ? WaitResults.Dequeue() : expected;

        public bool IsModeConfigured(string mode) => ConfiguredModes.Contains(mode);

        public bool ToggleOnce(string requestId, string mode)
        {
            if (!requestIds.Add(requestId))
            {
                return true;
            }
            LastMode = mode;
            ToggleCount++;
            return false;
        }

        public void Toggle(string mode)
        {
            LastMode = mode;
            ToggleCount++;
        }
    }
}
