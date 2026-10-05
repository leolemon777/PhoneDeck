using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PhoneDeck.MacReceiver.Tests;

[TestClass]
public sealed class MacDictationSessionManagerTests
{
    [TestMethod]
    public void ManagedSessionRequiresReliableProcessProbe()
    {
        var audio = new FakeAudio();
        var typeless = new FakeTypeless { Capturing = null };
        using var manager = new MacDictationSessionManager(audio, typeless);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => manager.Start(
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "dictation"));

        StringAssert.Contains(exception.Message, "状态探针不可用");
        Assert.IsFalse(audio.PlaybackReleased);
    }

    [TestMethod]
    public void ManagedSessionReleasesPreRollOnlyAfterTypelessStarts()
    {
        var audio = new FakeAudio();
        var typeless = new FakeTypeless { Capturing = false };
        using var manager = new MacDictationSessionManager(audio, typeless);
        var session = Guid.NewGuid().ToString();

        manager.Start(session, Guid.NewGuid().ToString(), "translation");

        Assert.IsTrue(manager.IsActive);
        Assert.IsTrue(audio.PlaybackReleased);
        Assert.AreEqual("translation", typeless.LastMode);

        manager.Stop(session, Guid.NewGuid().ToString());
        Assert.IsFalse(manager.IsActive);
        Assert.IsFalse(typeless.Capturing);
    }

    [TestMethod]
    public void AudioLossResetsOnlyAnActiveManagedSession()
    {
        var audio = new FakeAudio();
        var typeless = new FakeTypeless { Capturing = false };
        using var manager = new MacDictationSessionManager(audio, typeless);
        var session = Guid.NewGuid().ToString();
        manager.Start(session, Guid.NewGuid().ToString(), "dictation");

        manager.AudioEnded(session);

        Assert.IsFalse(manager.IsActive);
        Assert.IsFalse(typeless.Capturing);
        Assert.AreEqual(2, typeless.ToggleCount);
    }

    [TestMethod]
    public void PhoneStopAfterDesktopStopDoesNotRestartTypeless()
    {
        var audio = new FakeAudio(); var typeless = new FakeTypeless { Capturing = false };
        using var manager = new MacDictationSessionManager(audio, typeless);
        var session = Guid.NewGuid().ToString(); manager.Start(session, Guid.NewGuid().ToString(), "dictation");
        typeless.Capturing = false; // Stopped using the desktop input method itself.
        manager.Stop(session, Guid.NewGuid().ToString());
        Assert.AreEqual(1, typeless.ToggleCount);
        Assert.IsFalse(manager.IsActive); Assert.IsFalse(typeless.Capturing);
    }

    [TestMethod]
    public void FailedTailDrainStillStopsEngineAndAllowsAnotherSession()
    {
        var audio = new FakeAudio { Drained = false };
        var typeless = new FakeTypeless { Capturing = false };
        using var manager = new MacDictationSessionManager(audio, typeless);
        var session = Guid.NewGuid().ToString();
        manager.Start(session, Guid.NewGuid().ToString(), "dictation");
        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Stop(session, Guid.NewGuid().ToString()));
        StringAssert.Contains(error.Message, "尾音");
        Assert.IsFalse(manager.IsActive);
        Assert.IsFalse(typeless.Capturing);
        Assert.AreEqual(2, typeless.ToggleCount);
        audio.Drained = true;
        manager.Start(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "dictation");
        Assert.IsTrue(manager.IsActive);
    }

    [TestMethod]
    public void CaptureProbeAcceptsAnyCapturingEngineProcess()
    {
        Assert.AreEqual(true, MacVoiceEngineStateProbe.CombineCaptureStates(
            [false, null, true]));
        Assert.AreEqual(false, MacVoiceEngineStateProbe.CombineCaptureStates(
            [false, false]));
        Assert.IsNull(MacVoiceEngineStateProbe.CombineCaptureStates(
            [false, null]));
        Assert.AreEqual(false, MacVoiceEngineStateProbe.CombineCaptureStates([]));
    }

    [TestMethod]
    public void WarmOutputTriggersEngineBeforeWaitingForPhoneAudio()
    {
        var audio = new FakeAudio { OutputWarm = true };
        var typeless = new FakeTypeless { Capturing = false, Calls = audio.Calls };
        using var manager = new MacDictationSessionManager(audio, typeless);

        manager.Start(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "dictation");

        CollectionAssert.AreEqual(new[] { "begin", "waitAudio" }, audio.Calls);
        Assert.IsTrue(audio.PlaybackReleased);
    }

    [TestMethod]
    public void ColdOutputKeepsAudioFirstOrderAndMissingAudioResetsEngine()
    {
        var audio = new FakeAudio();
        var typeless = new FakeTypeless { Capturing = false, Calls = audio.Calls };
        using var manager = new MacDictationSessionManager(audio, typeless);
        manager.Start(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "dictation");
        CollectionAssert.AreEqual(new[] { "waitAudio", "begin" }, audio.Calls);

        var warm = new FakeAudio { OutputWarm = true, SessionArrives = false };
        var engine = new FakeTypeless { Capturing = false };
        using var parallel = new MacDictationSessionManager(warm, engine);
        Assert.ThrowsExactly<InvalidOperationException>(() => parallel.Start(
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "dictation"));
        Assert.IsFalse(parallel.IsActive);
        Assert.IsFalse(engine.Capturing, "音频始终未到时必须复位已唤醒的引擎");
    }

    [TestMethod]
    public void DesktopStopIssuesReceiptBeforeWaitingForPhoneTail()
    {
        var audio = new FakeAudio();
        var typeless = new FakeTypeless { Capturing = false };
        using var manager = new MacDictationSessionManager(audio, typeless);
        var session = Guid.NewGuid().ToString();
        manager.Start(session, Guid.NewGuid().ToString(), "dictation");
        string? receiptDuringDrain = null;
        audio.OnWaitForEnd = () => receiptDuringDrain = manager.Receipts.For(null);

        manager.StopFromDesktop();

        Assert.AreEqual(session, receiptDuringDrain);
        Assert.IsFalse(manager.IsActive);
    }

    [TestMethod]
    public void LateStartCannotReviveAStoppedSession()
    {
        var audio = new FakeAudio();
        var typeless = new FakeTypeless { Capturing = false };
        using var manager = new MacDictationSessionManager(audio, typeless);
        var session = Guid.NewGuid().ToString();
        manager.Start(session, Guid.NewGuid().ToString(), "dictation");
        manager.Stop(session, Guid.NewGuid().ToString());

        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(session, Guid.NewGuid().ToString(), "dictation"));

        StringAssert.Contains(error.Message, "已终止");
        Assert.IsFalse(manager.IsActive);
    }

    [TestMethod]
    public void FailedStartCanBeRetriedWithTheSameSession()
    {
        var audio = new FakeAudio { OutputWarm = true, SessionArrives = false };
        var typeless = new FakeTypeless { Capturing = false };
        using var manager = new MacDictationSessionManager(audio, typeless);
        var session = Guid.NewGuid().ToString();
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(session, Guid.NewGuid().ToString(), "dictation"));

        audio.SessionArrives = true;
        manager.Start(session, Guid.NewGuid().ToString(), "dictation");

        Assert.IsTrue(manager.IsActive);
    }

    [TestMethod]
    public void AnotherPhoneCannotStopAndReceiptGoesToOwner()
    {
        var audio = new FakeAudio();
        var typeless = new FakeTypeless { Capturing = false };
        using var manager = new MacDictationSessionManager(audio, typeless);
        var session = Guid.NewGuid().ToString();
        manager.Start(session, Guid.NewGuid().ToString(), "dictation", "phone-a");

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Stop(session, Guid.NewGuid().ToString(), "phone-b", checkOwner: true));
        Assert.IsTrue(manager.IsOwnedBy("phone-a"));

        manager.StopFromDesktop();
        Assert.AreEqual(session, manager.Receipts.For("phone-a"));
        Assert.IsNull(manager.Receipts.For("phone-b"));
        Assert.IsNull(manager.Receipts.For(null));
    }

    private sealed class FakeAudio : IMacPhoneAudioSessionController
    {
        internal bool PlaybackReleased { get; private set; }
        internal bool Drained { get; set; } = true;
        internal bool SessionArrives { get; set; } = true;
        internal List<string> Calls { get; } = [];
        internal Action? OnWaitForEnd { get; set; }
        public bool OutputWarm { get; set; }
        public bool WaitForSessionActive(string sessionId, int timeoutMilliseconds)
        {
            Calls.Add("waitAudio");
            return SessionArrives;
        }
        public bool WaitForSessionEnd(string sessionId, int timeoutMilliseconds)
        {
            OnWaitForEnd?.Invoke();
            return Drained;
        }
        public void BeginPlayback(string sessionId) => PlaybackReleased = true;
        public bool StopSession(string sessionId) => true;
    }

    private sealed class FakeTypeless : IMacVoiceEngineController
    {
        internal bool? Capturing { get; set; }
        internal string? LastMode { get; private set; }
        internal int ToggleCount { get; private set; }
        public string EngineDisplayName => "Typeless";
        public IReadOnlyCollection<string> Modes => new[] { "dictation", "translation", "ask" };
        public bool? CanVerifyVirtualCable => true;
        public bool UsesVirtualCable => true;
        public bool? IsCapturing() => Capturing;
        public bool? WaitForCapturing(bool expected, int timeoutMilliseconds) =>
            Capturing is null ? null : Capturing == expected;
        public bool IsModeConfigured(string mode) => true;
        internal List<string>? Calls { get; set; }
        public bool BeginOnce(string requestId, string mode)
        {
            Calls?.Add("begin");
            Toggle(mode);
            return false;
        }
        public bool End(string mode, string? requestId)
        {
            Toggle(mode);
            return false;
        }
        private void Toggle(string mode)
        {
            LastMode = mode;
            ToggleCount++;
            Capturing = !(Capturing ?? false);
        }
        public void Dispose()
        {
        }
    }
}
