using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhoneDeck.Desktop;

[assembly: DoNotParallelize]
namespace PhoneDeck.Desktop.Tests;

internal sealed class FakeEngine : ISpeechEngine
{
    public bool Ready { get; set; } = true;
    internal TaskCompletionSource<string>? Completion;
    internal int Calls;
    public async Task<string> TranscribeAsync(byte[] pcm, CancellationToken cancellation)
    {
        Interlocked.Increment(ref Calls);
        return Completion is null ? "多台电脑收到同一段文字" : await Completion.Task.WaitAsync(cancellation);
    }
}
[TestClass]
public sealed class SpeechTests
{
    private static string Id() => Guid.NewGuid().ToString();
    [TestMethod] public async Task ManagedAudioBeforeStartUsesPrerollAndCreatesOneFinalResult()
    {
        var engine = new FakeEngine(); var computer = Id(); var store = new TranscriptStore(computer); var id = Id();
        using var speech = new SpeechSession(engine, store, computer);
        speech.Attach("phone", id, "managed"); speech.Feed(id, new byte[9600]);
        Assert.IsFalse(speech.Start("phone", id)); Assert.IsTrue(speech.Start("phone", id));
        speech.EndStream(id, true); await speech.StopAsync("phone", id); await speech.StopAsync("phone", id); await speech.WaitForResultAsync();
        Assert.AreEqual(1, engine.Calls); Assert.HasCount(1, store.LocalHistory()); Assert.AreEqual(id, store.LocalHistory()[0].SessionId);
    }
    [TestMethod] public async Task LateStartAfterStopIsRejected()
    {
        var computer = Id(); using var speech = new SpeechSession(new FakeEngine(), new TranscriptStore(computer), computer); var id = Id();
        await speech.StopAsync("phone", id);
        Assert.ThrowsExactly<InvalidOperationException>(() => speech.Start("phone", id));
        Assert.ThrowsExactly<InvalidOperationException>(() => speech.Attach("phone", id, "managed"));
    }
    [TestMethod] public async Task AudioDisconnectCancelsWithoutTranscribing()
    {
        var engine = new FakeEngine(); var computer = Id(); var store = new TranscriptStore(computer); var id = Id();
        using var speech = new SpeechSession(engine, store, computer);
        speech.Start("phone", id); speech.Attach("phone", id, "managed"); speech.Feed(id, new byte[9600]); speech.EndStream(id, false);
        await speech.StopAsync("phone", id); await speech.WaitForResultAsync(); Assert.AreEqual(0, engine.Calls); Assert.IsEmpty(store.LocalHistory());
    }
    [TestMethod] public async Task StopWithoutAudioDrainCancelsInsteadOfTranscribingTruncatedSpeech()
    {
        var engine = new FakeEngine(); var computer = Id(); var id = Id(); using var speech = new SpeechSession(engine, new TranscriptStore(computer), computer);
        speech.Start("phone", id); speech.Attach("phone", id, "managed"); speech.Feed(id, new byte[9600]);
        await speech.StopAsync("phone", id); Assert.IsFalse(speech.Recording); Assert.AreEqual(0, engine.Calls);
    }
    [TestMethod] public async Task RevokingAnotherPhoneDoesNotCancelProcessing()
    {
        var engine = new FakeEngine { Completion = new() }; var computer = Id(); var store = new TranscriptStore(computer); var id = Id();
        using var speech = new SpeechSession(engine, store, computer);
        speech.Start("one", id); speech.Attach("one", id, "managed"); speech.Feed(id, new byte[9600]); speech.EndStream(id, true); await speech.StopAsync("one", id);
        speech.Revoke("two"); engine.Completion.SetResult("本段属于手机一"); await speech.WaitForResultAsync(); Assert.HasCount(1, store.LocalHistory());
    }
    [TestMethod] public async Task RevocationDuringRecognitionSuppressesBothHistoryAndInsertion()
    {
        var engine = new FakeEngine { Completion = new() }; var computer = Id(); var store = new TranscriptStore(computer); var id = Id(); var insertions = 0;
        using var speech = new SpeechSession(engine, store, computer, () => _ => insertions++);
        speech.Start("one", id); speech.Attach("one", id, "managed"); speech.Feed(id, new byte[9600]); speech.EndStream(id, true); await speech.StopAsync("one", id);
        speech.Revoke("one"); engine.Completion.SetResult("应当取消"); await speech.WaitForResultAsync(); Assert.IsEmpty(store.LocalHistory()); Assert.AreEqual(0, insertions);
    }
    [TestMethod] public async Task SharedLocalSegmentsKeepSharedStreamAndCreateIndependentResults()
    {
        var computer = Id(); var store = new TranscriptStore(computer); using var speech = new SpeechSession(new FakeEngine(), store, computer);
        var stream = Id(); speech.Attach("phone", stream, "shared");
        for (var i = 0; i < 2; i++)
        {
            speech.StartLocal(false); speech.Feed(stream, new byte[9600]); await speech.StopLocalAsync(); await speech.WaitForResultAsync();
            Assert.IsTrue(speech.Streaming);
            Assert.IsNull(speech.HealthForPhone("phone").StopRequestedSessionId);
        }
        Assert.HasCount(2, store.LocalHistory()); Assert.AreNotEqual(store.LocalHistory()[0].SessionId, store.LocalHistory()[1].SessionId);
    }
    [TestMethod] public async Task QuickManagedDesktopStopSurvivesRecognitionAndIsScopedToOwner()
    {
        var computer = Id(); var store = new TranscriptStore(computer); var engine = new FakeEngine();
        using var speech = new SpeechSession(engine, store, computer);
        var first = Id(); speech.Start("one", first); speech.Attach("one", first, "managed"); speech.Feed(first, new byte[9600]);
        await speech.StopLocalAsync(); await speech.StopLocalAsync(); await speech.WaitForResultAsync();
        Assert.AreEqual(first, speech.HealthForPhone("one").StopRequestedSessionId);
        Assert.IsNull(speech.HealthForPhone("two").StopRequestedSessionId);
        Assert.IsFalse(speech.HealthForPhone("one").Recording); Assert.AreEqual(1, engine.Calls);
        speech.EndStream(first, true);
        var next = Id(); speech.Start("one", next); speech.Attach("one", next, "managed");
        var health = speech.HealthForPhone("one");
        Assert.IsTrue(health.Recording); Assert.AreEqual(next, health.RecordingSession);
        Assert.AreNotEqual(next, health.StopRequestedSessionId);
    }
    [TestMethod] public async Task DesktopStopBeforeManagedStartRejectsLateStart()
    {
        var computer = Id(); using var speech = new SpeechSession(new FakeEngine(), new TranscriptStore(computer), computer);
        var stream = Id(); speech.Attach("phone", stream, "managed"); await speech.StopLocalAsync();
        Assert.AreEqual(stream, speech.HealthForPhone("phone").StopRequestedSessionId);
        Assert.ThrowsExactly<InvalidOperationException>(() => speech.Start("phone", stream));
    }
    [TestMethod] public async Task DesktopStopWithoutALocalSegmentKeepsSharedSupply()
    {
        var computer = Id(); using var speech = new SpeechSession(new FakeEngine(), new TranscriptStore(computer), computer);
        var stream = Id(); speech.Attach("phone", stream, "shared");
        Assert.IsFalse(speech.HealthForPhone("phone").Recording);
        Assert.IsNull(speech.HealthForPhone("phone").StopRequestedSessionId);
        await speech.StopLocalAsync();
        Assert.IsNull(speech.HealthForPhone("phone").StopRequestedSessionId);
        Assert.IsTrue(speech.Streaming);
    }
    [TestMethod] public async Task SharedSegmentEndingAfterStreamEofDoesNotCreateAManagedStopReceipt()
    {
        var computer = Id(); using var speech = new SpeechSession(new FakeEngine(), new TranscriptStore(computer), computer);
        var stream = Id(); speech.Attach("phone", stream, "shared"); speech.StartLocal(false);
        speech.Feed(stream, new byte[9600]); speech.EndStream(stream, true);
        await speech.StopLocalAsync(); await speech.WaitForResultAsync();
        Assert.IsNull(speech.HealthForPhone("phone").StopRequestedSessionId);
    }
    [TestMethod] public async Task OtherPhoneCannotStopOrAttachToActiveRecording()
    {
        var computer = Id(); using var speech = new SpeechSession(new FakeEngine(), new TranscriptStore(computer), computer); var id = Id(); speech.Start("one", id);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => speech.StopAsync("two", id));
        Assert.ThrowsExactly<InvalidOperationException>(() => speech.Attach("two", id, "managed")); Assert.IsTrue(speech.Recording);
    }
    [TestMethod] public void SharedLocalStartRequiresReadyModelAndLiveStream()
    {
        var computer = Id(); var engine = new FakeEngine { Ready = false }; using var speech = new SpeechSession(engine, new TranscriptStore(computer), computer);
        Assert.ThrowsExactly<InvalidOperationException>(() => speech.StartLocal()); speech.Attach("one", Id(), "shared"); Assert.ThrowsExactly<InvalidOperationException>(() => speech.StartLocal());
    }
    [TestMethod] public void WaveEnvelopeDeclaresPhonePcmFormat()
    {
        var pcm = new byte[9600]; var wave = WhisperEngine.Wave(pcm);
        Assert.AreEqual(9644, wave.Length); Assert.AreEqual(48000, BitConverter.ToInt32(wave, 24)); Assert.AreEqual((short)1, BitConverter.ToInt16(wave, 22)); Assert.AreEqual((short)16, BitConverter.ToInt16(wave, 34));
    }
    [TestMethod] public async Task SharedSupplyStopCancelsOnlyTheMatchingOwnersUnfinishedSegment()
    {
        var computer = Id(); var engine = new FakeEngine(); var history = new TranscriptStore(computer);
        using var speech = new SpeechSession(engine, history, computer);
        var stream = Id(); speech.Attach("one", stream, "shared"); speech.StartLocal(false); speech.Feed(stream, new byte[9600]);
        Assert.IsFalse(await speech.StopSupplyAsync("two", stream, true)); Assert.IsTrue(speech.Recording);
        Assert.IsFalse(await speech.StopSupplyAsync("one", Id(), true)); Assert.IsTrue(speech.Streaming);
        Assert.IsTrue(await speech.StopSupplyAsync("one", stream, true));
        Assert.IsFalse(speech.Recording); Assert.IsFalse(speech.Streaming); Assert.AreEqual(0, engine.Calls);
        Assert.ThrowsExactly<InvalidOperationException>(() => speech.Attach("one", stream, "shared"));
        var replacement = Id(); speech.Attach("one", replacement, "shared"); speech.StartLocal(false);
        Assert.IsFalse(await speech.StopSupplyAsync("one", stream, true));
        speech.EndStream(stream, false); Assert.IsTrue(speech.Streaming); Assert.IsTrue(speech.Recording);
        Assert.IsTrue(await speech.StopSupplyAsync("one", replacement, true)); Assert.IsEmpty(history.LocalHistory());
    }
}
[TestClass]
public sealed class TranscriptTests
{
    private static string Id() => Guid.NewGuid().ToString();
    private static JsonElement Snapshot(TranscriptStore store, string owner, long after = 0) => JsonSerializer.SerializeToElement(store.ForPhone(owner, after));
    [TestMethod] public void ResultsAreScopedToSupplyingPhoneAndImportsNeverRelayAgain()
    {
        var id = Id(); var store = new TranscriptStore(id); store.Add("one", new(Id(), id, Id(), "一", DateTimeOffset.UtcNow));
        store.Add("two", new(Id(), id, Id(), "二", DateTimeOffset.UtcNow)); store.Add("one", new(Id(), Id(), Id(), "收到的记录", DateTimeOffset.UtcNow), true);
        Assert.AreEqual(1, Snapshot(store, "one").GetProperty("results").GetArrayLength()); Assert.AreEqual(0, Snapshot(store, "unknown").GetProperty("results").GetArrayLength()); Assert.HasCount(3, store.LocalHistory());
    }
    [TestMethod] public void SameIdDeduplicatesAndRejectsDifferentText()
    {
        var computer = Id(); var store = new TranscriptStore(computer); var result = new Transcript(Id(), Id(), Id(), "一致", DateTimeOffset.UtcNow);
        store.Add("phone", result, true); store.Add("phone", result, true); Assert.HasCount(1, store.LocalHistory());
        Assert.ThrowsExactly<ArgumentException>(() => store.Add("phone", result with { Text = "篡改" }, true));
    }
    [TestMethod] public void HistoryIsBoundedAndAfterCursorExcludesEarlierResults()
    {
        var computer = Id(); var store = new TranscriptStore(computer);
        for (var i = 0; i < 105; i++) store.Add("phone", new(Id(), computer, Id(), i.ToString(), DateTimeOffset.UtcNow));
        Assert.HasCount(100, store.LocalHistory()); Assert.AreEqual(5, Snapshot(store, "phone", 100).GetProperty("results").GetArrayLength());
    }
    [TestMethod] public void HistoryExpiresAfterThirtyMinutes()
    {
        var clock = new FakeClock(); var computer = Id(); var store = new TranscriptStore(computer, clock);
        store.Add("one", new(Id(), computer, Id(), "临时", clock.GetUtcNow())); clock.Now += TimeSpan.FromMinutes(31); Assert.IsEmpty(store.LocalHistory());
    }
    private sealed class FakeClock : TimeProvider { internal DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    [TestMethod] public void InvalidTargetsKeysAndOversizedResultsAreRejected()
    {
        var computer = Id(); var store = new TranscriptStore(computer);
        Assert.ThrowsExactly<ArgumentException>(() => DesktopApp.ValidateEnvelope(2, Id(), computer, Id(), Id()));
        Assert.ThrowsExactly<ArgumentException>(() => PlatformInput.Normalize(["CTRL", "CONTROL", "A"]));
        Assert.ThrowsExactly<ArgumentException>(() => PlatformInput.Normalize(["CTRL", "A", "B"]));
        Assert.ThrowsExactly<ArgumentException>(() => store.Add("one", new(Id(), computer, Id(), new string('字', 4097), DateTimeOffset.UtcNow)));
    }
}
