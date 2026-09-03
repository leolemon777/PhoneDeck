using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class DictationSessionManagerTests
{
    [TestMethod]
    public void StartDispatchesToggleAndPlaybackImmediately()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeTypelessController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = Guid.NewGuid().ToString();

        var duplicate = manager.Start(sessionId, "start-request", "translation");

        Assert.IsFalse(duplicate);
        Assert.IsTrue(manager.IsActive);
        Assert.AreEqual(sessionId, manager.ActiveSessionId);
        Assert.AreEqual(1, typeless.ToggleCount);
        Assert.AreEqual("translation", typeless.LastMode);
        Assert.AreEqual(1, audio.BeginPlaybackCalls, "启动时必须立即放行 pre-roll 音频播放");
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
        Assert.AreEqual(0, audio.WaitForSessionCalls, "模式未配置时不应等待音频会话登记");
        Assert.IsFalse(manager.IsActive);
    }

    [TestMethod]
    public void StartRejectsMissingVirtualCable()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeTypelessController { UsesVirtualCable = false };
        var manager = new DictationSessionManager(audio, typeless);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(Guid.NewGuid().ToString(), "start-request", "dictation"));

        StringAssert.Contains(exception.Message, "CABLE Output");
        Assert.AreEqual(0, typeless.ToggleCount);
        Assert.IsFalse(manager.IsActive);
    }

    [TestMethod]
    public void DuplicateStartRequestReturnsDuplicateTrueWithoutRetoggling()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeTypelessController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = Guid.NewGuid().ToString();
        const string requestId = "start-request-1";

        var first = manager.Start(sessionId, requestId, "dictation");
        Assert.IsFalse(first);
        Assert.AreEqual(1, typeless.ToggleCount);

        var second = manager.Start(sessionId, requestId, "dictation");
        Assert.IsTrue(second);
        Assert.AreEqual(1, typeless.ToggleCount, "同一会话的重复请求不能重新发键");
    }

    [TestMethod]
    public void StopReusesModeFromStart()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeTypelessController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = Guid.NewGuid().ToString();

        Assert.IsFalse(manager.Start(sessionId, "start-request", "translation"));
        Assert.AreEqual("translation", typeless.LastMode);

        typeless.IsCapturingResults.Enqueue(true);
        Assert.IsFalse(manager.Stop(sessionId, "stop-request"));
        Assert.AreEqual("translation", typeless.LastMode,
            "停止时必须沿用启动时的模式键，不能用当前选中的模式切换");
    }

    [TestMethod]
    public void StopWaitsForAudioDrainBeforeTogglingTypeless()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeTypelessController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = Guid.NewGuid().ToString();

        Assert.IsFalse(manager.Start(sessionId, "start-request", "dictation"));
        typeless.IsCapturingResults.Enqueue(true);
        Assert.IsFalse(manager.Stop(sessionId, "stop-request"));

        Assert.AreEqual(1, audio.WaitForSessionEndCalls,
            "停止 Typeless 前必须等待音频流排空，避免丢失 pre-roll 尾部");
        Assert.AreEqual(2, typeless.ToggleCount);
        Assert.IsFalse(manager.IsActive);
    }

    [TestMethod]
    public void AudioEndedFailureCannotPermanentlyLockNextSession()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeTypelessController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = Guid.NewGuid().ToString();

        Assert.IsFalse(manager.Start(sessionId, "initial-start", "dictation"));
        manager.AudioEnded(sessionId);

        Assert.IsFalse(manager.IsActive);
        Assert.IsNull(manager.ActiveSessionId);
    }

    private sealed class FakeAudioSessionController : IPhoneAudioSessionController
    {
        public int StopCalls { get; private set; }
        public int WaitForSessionCalls { get; private set; }
        public int BeginPlaybackCalls { get; private set; }
        public int WaitForSessionEndCalls { get; private set; }
        public int LastWaitTimeoutMilliseconds { get; private set; }
        public List<int> WaitTimeouts { get; } = new();
        public bool WaitForSessionResult { get; set; } = true;
        public bool WaitForSessionEndResult { get; set; } = true;
        public Action<int>? OnWaitForSession { get; set; }

        public bool IsSessionActive(string sessionId) => true;

        public bool WaitForSessionActive(string sessionId, int timeoutMilliseconds)
        {
            WaitForSessionCalls++;
            LastWaitTimeoutMilliseconds = timeoutMilliseconds;
            WaitTimeouts.Add(timeoutMilliseconds);
            OnWaitForSession?.Invoke(WaitForSessionCalls);
            return WaitForSessionResult;
        }

        public bool StopSession(string sessionId)
        {
            StopCalls++;
            return true;
        }

        public void BeginPlayback(string sessionId)
        {
            BeginPlaybackCalls++;
        }

        public bool WaitForSessionEnd(string sessionId, int timeoutMilliseconds)
        {
            WaitForSessionEndCalls++;
            return WaitForSessionEndResult;
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
        public int LastWaitTimeoutMilliseconds { get; private set; }

        public ManualResetEventSlim? WaitForCapturingGate { get; set; }

        public bool? IsCapturing() => IsCapturingResults.Count > 0
            ? IsCapturingResults.Dequeue()
            : false;

        public bool? WaitForCapturing(bool expected, int timeoutMilliseconds)
        {
            LastWaitTimeoutMilliseconds = timeoutMilliseconds;
            WaitForCapturingGate?.Wait();
            return WaitResults.Count > 0 ? WaitResults.Dequeue() : expected;
        }

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
