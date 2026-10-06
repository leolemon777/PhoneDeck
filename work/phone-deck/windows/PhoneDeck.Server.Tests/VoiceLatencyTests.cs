using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Wave;

[TestClass]
public sealed class VoiceLatencyTests
{
    private const int Block = PcmLatency.BlockBytes;

    internal static byte[] Silence(int milliseconds) => new byte[PcmLatency.MsToBytes(milliseconds)];

    internal static byte[] Voice(int milliseconds, short amplitude = 3_000)
    {
        var bytes = new byte[PcmLatency.MsToBytes(milliseconds)];
        for (var index = 0; index + 1 < bytes.Length; index += 2)
        {
            // 方波：每个 10 ms 块的 RMS 都远高于静音阈值。
            var sample = (short)((index / 2 / 24) % 2 == 0 ? amplitude : -amplitude);
            bytes[index] = (byte)sample;
            bytes[index + 1] = (byte)(sample >> 8);
        }
        return bytes;
    }

    [TestMethod]
    public void SilenceDetectionUsesRmsThreshold()
    {
        Assert.IsTrue(PcmLatency.IsSilent(Silence(10)));
        Assert.IsTrue(PcmLatency.IsSilent(Voice(10, amplitude: 80)));
        Assert.IsFalse(PcmLatency.IsSilent(Voice(10, amplitude: 400)));
    }

    [TestMethod]
    public void LeadingSilenceTrimKeepsLeadInBeforeFirstWord()
    {
        var preRoll = Silence(600).Concat(Voice(200)).ToArray();

        var offset = PcmLatency.LeadingSilenceTrimOffset(preRoll, PcmLatency.MsToBytes(150));

        Assert.AreEqual(PcmLatency.MsToBytes(450), offset);
    }

    [TestMethod]
    public void LeadingSilenceTrimKeepsOnlyTailWhenNobodySpoke()
    {
        var preRoll = Silence(800);

        Assert.AreEqual(PcmLatency.MsToBytes(650),
            PcmLatency.LeadingSilenceTrimOffset(preRoll, PcmLatency.MsToBytes(150)));
        Assert.AreEqual(0, PcmLatency.LeadingSilenceTrimOffset(Voice(300), PcmLatency.MsToBytes(150)));
        Assert.AreEqual(0, PcmLatency.LeadingSilenceTrimOffset(Silence(100), PcmLatency.MsToBytes(150)));
    }

    [TestMethod]
    public void CatchUpNeverDropsVoiceOrHangoverOrPartialBlocks()
    {
        var catchUp = new PcmSilenceCatchUp(targetBacklogMs: 0, hangoverMs: 200);
        var input = Voice(100).Concat(Silence(500)).Concat(Voice(100)).Concat(new byte[100]).ToArray();
        var output = new byte[input.Length];

        var written = catchUp.Filter(input, output, backlogBytes: PcmLatency.MsToBytes(1_000));

        // 100 ms 语音 + 200 ms hangover + 100 ms 语音 + 100 字节不完整尾块。
        Assert.AreEqual(PcmLatency.MsToBytes(400) + 100, written);
        CollectionAssert.AreEqual(Voice(100), output[..PcmLatency.MsToBytes(100)]);
        CollectionAssert.AreEqual(Voice(100),
            output[PcmLatency.MsToBytes(300)..PcmLatency.MsToBytes(400)]);
        Assert.AreEqual((long)PcmLatency.MsToBytes(300), catchUp.SkippedBytes);
    }

    [TestMethod]
    public void CatchUpKeepsEverythingWhenAlreadyLive()
    {
        var catchUp = new PcmSilenceCatchUp(targetBacklogMs: 150);
        var input = Voice(50).Concat(Silence(100)).ToArray();
        var output = new byte[input.Length];

        Assert.AreEqual(input.Length, catchUp.Filter(input, output, backlogBytes: 0));
        Assert.AreEqual(0L, catchUp.SkippedBytes);
    }

    [TestMethod]
    public void CatchUpHangoverSpansChunkBoundaries()
    {
        var catchUp = new PcmSilenceCatchUp(targetBacklogMs: 0, hangoverMs: 200);
        var output = new byte[PcmLatency.MsToBytes(150)];
        var backlog = PcmLatency.MsToBytes(1_000);

        Assert.AreEqual(output.Length, catchUp.Filter(Silence(150), output, backlog));
        Assert.AreEqual(PcmLatency.MsToBytes(50), catchUp.Filter(Silence(150), output, backlog));
        Assert.AreEqual(0, catchUp.Filter(Silence(150), output, backlog));
    }

    [TestMethod]
    public void ManagedBufferSkipsBacklogSilenceButKeepsSpeech()
    {
        var buffer = new PhonePcmBuffer(AudioStreamMode.Managed);
        var utterance = Voice(400).Concat(Silence(800)).Concat(Voice(400)).ToArray();

        buffer.AddSamples(utterance, 0, utterance.Length);

        var expected = Voice(400).Concat(Silence(PcmLatency.HangoverMs)).Concat(Voice(400)).ToArray();
        Assert.AreEqual(expected.Length, buffer.BufferedBytes);
        var actual = new byte[expected.Length];
        buffer.Read(actual, 0, actual.Length);
        CollectionAssert.AreEqual(expected, actual);
        Assert.AreEqual(0L, buffer.DroppedBytes);
        Assert.AreEqual((long)PcmLatency.MsToBytes(600), buffer.SkippedSilenceBytes);
    }

    [TestMethod]
    public void SharedBufferAllowsJitterHeadroomBeforeDroppingSpeech()
    {
        var buffer = new PhonePcmBuffer(AudioStreamMode.Shared);
        var jitterBurst = Voice(200);

        buffer.AddSamples(jitterBurst, 0, jitterBurst.Length);

        Assert.AreEqual(jitterBurst.Length, buffer.BufferedBytes, "200 ms 抖动突发不应丢语音");
        Assert.AreEqual(0L, buffer.DroppedBytes);
    }

    [TestMethod]
    public async Task BeginPlaybackTrimsSilenceBeforeFirstWord()
    {
        RecordingPlayback? playback = null;
        using var bridge = new PhoneAudioBridge(source => playback = new RecordingPlayback(source));
        var session = Guid.NewGuid().ToString();
        // 低幅度非零样本：判定为静音，但能在输出端被计数。
        var pcm = Voice(700, amplitude: 50).Concat(Voice(100)).ToArray();
        using var input = new GatedStream(pcm);

        var streaming = bridge.StreamAsync(input, session, AudioStreamMode.Managed,
            (_, _) => { }, CancellationToken.None);
        await input.Consumed.Task;
        bridge.BeginPlayback(session);
        input.Finish();
        await streaming;

        Assert.AreEqual(PcmLatency.MsToBytes(PcmLatency.LeadInMs + 100) / 2, playback!.NonZeroSamples);
    }

    [TestMethod]
    public async Task WarmOutputIsReusedAcrossSessionsAndPlaysSilenceWhenIdle()
    {
        var created = 0;
        RecordingPlayback? playback = null;
        using var bridge = new PhoneAudioBridge(source =>
        {
            created++;
            return playback = new RecordingPlayback(source);
        }, keepOutputWarm: true);
        bridge.Prewarm();
        Assert.IsTrue(bridge.OutputWarm);

        for (var round = 0; round < 2; round++)
        {
            using var input = new MemoryStream(Voice(40));
            await bridge.StreamAsync(input, Guid.NewGuid().ToString(), AudioStreamMode.Shared,
                (_, _) => { }, CancellationToken.None);
        }

        Assert.AreEqual(1, created, "常驻输出不应按会话重建");
        Assert.IsFalse(playback!.Stopped);
        var idle = new byte[64];
        Array.Fill(idle, (byte)7);
        playback.Source.Read(idle, 0, idle.Length);
        Assert.IsTrue(idle.All(value => value == 0), "未挂接会话时输出静音");
    }

    [TestMethod]
    public async Task FaultedWarmOutputIsRebuilt()
    {
        var outputs = new List<RecordingPlayback>();
        using var bridge = new PhoneAudioBridge(source =>
        {
            var output = new RecordingPlayback(source);
            outputs.Add(output);
            return output;
        }, keepOutputWarm: true);
        bridge.Prewarm();
        outputs[0].Faulted = true;
        Assert.IsFalse(bridge.OutputWarm);

        using var input = new MemoryStream(Voice(20));
        await bridge.StreamAsync(input, Guid.NewGuid().ToString(), AudioStreamMode.Shared,
            (_, _) => { }, CancellationToken.None);

        Assert.HasCount(2, outputs);
        Assert.IsTrue(bridge.OutputWarm);
    }

    [TestMethod]
    public async Task SameOwnerSharedReconnectTakesOverStalledStream()
    {
        using var bridge = new PhoneAudioBridge(source => new RecordingPlayback(source),
            keepOutputWarm: true);
        var session = Guid.NewGuid().ToString();
        using var stalled = new GatedStream(Voice(20));
        var first = bridge.StreamAsync(stalled, session, AudioStreamMode.Shared,
            (_, _) => { }, CancellationToken.None, ownerId: "phone-a");
        await stalled.Consumed.Task;

        using var other = new MemoryStream(Voice(20));
        await Assert.ThrowsExactlyAsync<AudioStreamConflictException>(() =>
            bridge.StreamAsync(other, session, AudioStreamMode.Shared,
                (_, _) => { }, CancellationToken.None, ownerId: "phone-b"));

        using var reconnect = new MemoryStream(Voice(20));
        var bytes = await bridge.StreamAsync(reconnect, session, AudioStreamMode.Shared,
            (_, _) => { }, CancellationToken.None, ownerId: "phone-a");

        Assert.AreEqual((long)PcmLatency.MsToBytes(20), bytes);
        Assert.IsTrue(first.IsCompleted);
        Assert.IsFalse(bridge.IsStreaming);
    }

    [TestMethod]
    public async Task StreamOwnershipIsVisibleOnlyToItsPhone()
    {
        using var bridge = new PhoneAudioBridge(source => new RecordingPlayback(source),
            keepOutputWarm: true);
        var session = Guid.NewGuid().ToString();
        using var stalled = new GatedStream(Voice(20));
        var streaming = bridge.StreamAsync(stalled, session, AudioStreamMode.Shared,
            (_, _) => { }, CancellationToken.None, ownerId: "phone-a");
        await stalled.Consumed.Task;

        Assert.IsTrue(bridge.IsStreamOwnedBy("phone-a"));
        Assert.IsFalse(bridge.IsStreamOwnedBy("phone-b"));
        Assert.IsFalse(bridge.IsStreamOwnedBy(null));

        stalled.Finish();
        await streaming;
        Assert.IsFalse(bridge.IsStreamOwnedBy("phone-a"));
    }

    [TestMethod]
    public async Task ManagedStreamIsNeverTakenOver()
    {
        using var bridge = new PhoneAudioBridge(source => new RecordingPlayback(source),
            keepOutputWarm: true);
        var session = Guid.NewGuid().ToString();
        using var stalled = new GatedStream(Voice(20));
        var first = bridge.StreamAsync(stalled, session, AudioStreamMode.Managed,
            (_, _) => { }, CancellationToken.None, ownerId: "phone-a");
        await stalled.Consumed.Task;

        using var retry = new MemoryStream(Voice(20));
        await Assert.ThrowsExactlyAsync<AudioStreamConflictException>(() =>
            bridge.StreamAsync(retry, session, AudioStreamMode.Managed,
                (_, _) => { }, CancellationToken.None, ownerId: "phone-a"));

        stalled.Finish();
        await first;
    }

    [TestMethod]
    public void StopReceiptsExpireAndStayPerOwner()
    {
        var now = 0L;
        var receipts = new PhoneStopReceipts { MonotonicMilliseconds = () => now };
        var session = Guid.NewGuid().ToString();
        receipts.Record(session, null);

        Assert.AreEqual(session, receipts.For(null));
        Assert.AreEqual(session, receipts.For(ClientCredentialsStore.LegacySharedClientId),
            "USB 与旧共享令牌沿用同一 legacy 身份");
        Assert.IsNull(receipts.For("phone-a"));
        now = PhoneStopReceipts.RetentionMilliseconds + 1;
        Assert.IsNull(receipts.For(null));
    }

    [TestMethod]
    public void DiagnosticsRefreshFasterDuringSessionAndReportsEachSnapshot()
    {
        var active = false;
        var observed = new List<long>();
        using var monitor = new DiagnosticsMonitor(
            () => new DiagnosticsSnapshot { CheckedAtMs = Environment.TickCount64 },
            sessionActive: () => active,
            refreshed: snapshot => { lock (observed) observed.Add(snapshot.CheckedAtMs); });

        Assert.AreEqual(DiagnosticsMonitor.RefreshIntervalMs, monitor.CurrentIntervalMs);
        active = true;
        Assert.AreEqual(DiagnosticsMonitor.ActiveRefreshIntervalMs, monitor.CurrentIntervalMs);

        monitor.Start();
        var deadline = Environment.TickCount64 + 3_000;
        while (Environment.TickCount64 < deadline)
        {
            lock (observed) if (observed.Count >= 3) break;
            Thread.Sleep(20);
        }
        lock (observed) Assert.IsGreaterThanOrEqualTo(3, observed.Count);
    }

    [TestMethod]
    public void OutputTailIsConfigurableWithinSafeBounds()
    {
        Assert.AreEqual(PcmLatency.DefaultOutputTailMs, PcmLatency.ResolveOutputTailMs(null));
        Assert.AreEqual(PcmLatency.DefaultOutputTailMs, PcmLatency.ResolveOutputTailMs("abc"));
        Assert.AreEqual(180, PcmLatency.ResolveOutputTailMs("180"));
        Assert.AreEqual(100, PcmLatency.ResolveOutputTailMs("0"));
        Assert.AreEqual(1_000, PcmLatency.ResolveOutputTailMs("5000"));
    }

    [TestMethod]
    public void SessionEventsAreANoOpOffWindowsAndDisposeSafely()
    {
        var signals = 0;
        using var events = new VoiceEngineStateEvents(() => signals++);
        if (!OperatingSystem.IsWindows())
        {
            events.Start();
            Assert.AreEqual(0, events.WatchedSessions);
        }
        events.Dispose();
        Assert.AreEqual(0, signals);
    }

    private sealed class RecordingPlayback(IWaveProvider source) : IPhoneAudioPlayback
    {
        private volatile bool playing;
        private Task? worker;
        private int nonZeroSamples;
        internal IWaveProvider Source { get; } = source;
        internal int NonZeroSamples => Volatile.Read(ref nonZeroSamples);
        internal bool Stopped { get; private set; }
        internal bool Faulted { get; set; }
        public bool IsFaulted => Faulted;

        public void Play()
        {
            playing = true;
            worker = Task.Run(async () =>
            {
                var frame = new byte[960];
                while (playing)
                {
                    Source.Read(frame, 0, frame.Length);
                    var count = 0;
                    for (var index = 0; index + 1 < frame.Length; index += 2)
                    {
                        if (frame[index] != 0 || frame[index + 1] != 0) count++;
                    }
                    Interlocked.Add(ref nonZeroSamples, count);
                    await Task.Delay(1);
                }
            });
        }

        public void Stop()
        {
            playing = false;
            worker?.GetAwaiter().GetResult();
            Stopped = true;
        }

        public void Dispose() => Stop();
    }

    /// <summary>读完数据后挂起，直到 Finish()，用于在中途放行 pre-roll 或模拟断线未被发现。</summary>
    private sealed class GatedStream(byte[] pcm) : MemoryStream(pcm)
    {
        private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Consumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Finish() => finished.TrySetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length)
            {
                return await base.ReadAsync(buffer, cancellationToken);
            }
            Consumed.TrySetResult();
            await finished.Task.WaitAsync(cancellationToken);
            return 0;
        }
    }
}
