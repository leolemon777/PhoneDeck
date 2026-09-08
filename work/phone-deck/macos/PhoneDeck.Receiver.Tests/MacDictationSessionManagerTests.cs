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

    private sealed class FakeAudio : IMacPhoneAudioSessionController
    {
        internal bool PlaybackReleased { get; private set; }
        public bool WaitForSessionActive(string sessionId, int timeoutMilliseconds) => true;
        public bool WaitForSessionEnd(string sessionId, int timeoutMilliseconds) => true;
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
        public bool BeginOnce(string requestId, string mode)
        {
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
