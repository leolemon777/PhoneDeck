using System.Threading.Channels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PhoneDeck.MacReceiver.Tests;

[TestClass]
public sealed class MacAudioTests
{
    [TestMethod]
    public void MonoPcmIsCopiedToBothStereoChannels()
    {
        var stereo = MacAudioRingBuffer.MonoPcm16ToStereo(
            new byte[] { 0x34, 0x12, 0x78, 0x56 });

        CollectionAssert.AreEqual(
            new byte[] { 0x34, 0x12, 0x34, 0x12, 0x78, 0x56, 0x78, 0x56 },
            stereo);
    }

    [TestMethod]
    public void StreamingConverterPreservesSampleAcrossOddChunks()
    {
        var converter = new Pcm16MonoToStereoConverter();

        Assert.HasCount(0, converter.Convert(new byte[] { 0x34 }));
        CollectionAssert.AreEqual(
            new byte[] { 0x34, 0x12, 0x34, 0x12 },
            converter.Convert(new byte[] { 0x12 }));
    }

    [TestMethod]
    public void RingDropsOldestAndPadsUnderrunWithSilence()
    {
        var ring = new MacAudioRingBuffer(4);
        ring.Write(new byte[] { 1, 2, 3 });
        ring.Write(new byte[] { 4, 5, 6 });
        var output = new byte[6];

        var copied = ring.Read(output);

        Assert.AreEqual(4, copied);
        CollectionAssert.AreEqual(new byte[] { 3, 4, 5, 6, 0, 0 }, output);
    }

    [TestMethod]
    public async Task RingDrainWaitsForConsumptionAndTimeoutDoesNotDiscardAudio()
    {
        var ring = new MacAudioRingBuffer(8);
        ring.Write([1, 2, 3, 4]);
        Assert.IsFalse(await ring.WaitForEmptyAsync(20));
        Assert.AreEqual(4, ring.Count);
        var waiting = ring.WaitForEmptyAsync(1_000);
        var first = new byte[2]; var last = new byte[2];
        ring.Read(first);
        Assert.IsFalse(waiting.IsCompleted);
        ring.Read(last);
        Assert.IsTrue(await waiting);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, first);
        CollectionAssert.AreEqual(new byte[] { 3, 4 }, last);
    }

    [TestMethod]
    public async Task ManagedAudioStaysInPreRollUntilReleased()
    {
        var factory = new RecordingOutputFactory();
        using var bridge = new MacPhoneAudioBridge(factory);
        var source = new ChannelStream();
        var session = Guid.NewGuid().ToString();
        var streaming = bridge.StreamAsync(
            source, session, AudioStreamMode.Managed, (_, _) => { }, CancellationToken.None);
        Assert.IsTrue(bridge.WaitForSessionActive(session, 1_000));

        source.Push([0x34, 0x12]);
        await Task.Delay(50);
        Assert.HasCount(0, factory.Outputs.Single().Writes);

        bridge.BeginPlayback(session);
        await WaitUntilAsync(() => factory.Outputs.Single().Writes.Count == 1);
        CollectionAssert.AreEqual(
            new byte[] { 0x34, 0x12, 0x34, 0x12 },
            factory.Outputs.Single().Writes.Single());
        source.Complete();
        await streaming;
    }

    [TestMethod]
    public async Task SharedAudioWritesImmediatelyAndRejectsSecondStream()
    {
        var factory = new RecordingOutputFactory();
        using var bridge = new MacPhoneAudioBridge(factory);
        var source = new ChannelStream();
        var session = Guid.NewGuid().ToString();
        var streaming = bridge.StreamAsync(
            source, session, AudioStreamMode.Shared, (_, _) => { }, CancellationToken.None);
        Assert.IsTrue(bridge.WaitForSessionActive(session, 1_000));

        source.Push([0x78, 0x56]);
        await WaitUntilAsync(() => factory.Outputs.Single().Writes.Count == 1);
        await Assert.ThrowsExactlyAsync<AudioStreamConflictException>(() =>
            bridge.StreamAsync(
                new MemoryStream(), Guid.NewGuid().ToString(), AudioStreamMode.Managed,
                (_, _) => { }, CancellationToken.None));

        source.Complete();
        await streaming;
    }

    [TestMethod]
    public async Task EndOfStreamKeepsOutputAndOwnershipUntilTailHasDrained()
    {
        var drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new RecordingOutputFactory { Drain = () => drained.Task };
        using var bridge = new MacPhoneAudioBridge(factory);
        var source = new ChannelStream();
        var ended = false;
        var session = Guid.NewGuid().ToString();
        var streaming = bridge.StreamAsync(source, session, AudioStreamMode.Shared,
            (_, _) => ended = true, CancellationToken.None);
        var output = factory.Outputs.Single();
        source.Push([0x78, 0x56]);
        source.Complete();
        try
        {
            await WaitUntilAsync(() => output.DrainCalled || streaming.IsCompleted);
            Assert.IsTrue(output.DrainCalled, "EOF must drain the queued PCM before disposal");
            Assert.IsFalse(output.Disposed);
            Assert.IsFalse(ended);
            Assert.IsTrue(bridge.IsStreaming);
            Assert.IsFalse(bridge.WaitForSessionEnd(session, 1));
            await Assert.ThrowsExactlyAsync<AudioStreamConflictException>(() =>
                bridge.StreamAsync(new MemoryStream(), Guid.NewGuid().ToString(), AudioStreamMode.Shared,
                    (_, _) => { }, CancellationToken.None));
            CollectionAssert.AreEqual(new byte[] { 0x78, 0x56, 0x78, 0x56 }, output.Writes.Single());
        }
        finally
        {
            drained.TrySetResult(true);
            await streaming;
        }
        Assert.IsTrue(output.Disposed);
        Assert.IsTrue(ended);
        Assert.IsTrue(bridge.WaitForSessionEnd(session, 1));
        Assert.IsFalse(bridge.IsStreaming);
    }

    [TestMethod]
    public async Task FailedDrainIsReportedAndReleasesOutputForNextSession()
    {
        var factory = new RecordingOutputFactory { Drain = () => Task.FromResult(false) };
        using var bridge = new MacPhoneAudioBridge(factory);
        var session = Guid.NewGuid().ToString();
        var ended = false;
        await Assert.ThrowsExactlyAsync<IOException>(() => bridge.StreamAsync(
            new MemoryStream([1, 2]), session, AudioStreamMode.Shared,
            (_, _) => ended = true, CancellationToken.None));
        Assert.IsTrue(factory.Outputs.Single().Disposed);
        Assert.IsTrue(ended);
        Assert.IsFalse(bridge.IsStreaming);
        Assert.IsFalse(bridge.WaitForSessionEnd(session, 1));
        factory.Drain = () => Task.FromResult(true);
        await bridge.StreamAsync(new MemoryStream(), Guid.NewGuid().ToString(), AudioStreamMode.Shared,
            (_, _) => { }, CancellationToken.None);
    }

    [TestMethod]
    public async Task CanceledIntakeStillDrainsPcmAlreadyReleasedToOutput()
    {
        var drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new RecordingOutputFactory { Drain = () => drained.Task };
        using var bridge = new MacPhoneAudioBridge(factory);
        var source = new ChannelStream();
        var session = Guid.NewGuid().ToString();
        var streaming = bridge.StreamAsync(source, session, AudioStreamMode.Shared, (_, _) => { }, CancellationToken.None);
        var output = factory.Outputs.Single();
        try
        {
            source.Push([1, 2]);
            await WaitUntilAsync(() => output.Writes.Count == 1);
            Assert.IsTrue(bridge.StopSession(session));
            await WaitUntilAsync(() => output.DrainCalled);
            Assert.IsFalse(output.Disposed);
            Assert.IsFalse(streaming.IsCompleted);
        }
        finally { drained.TrySetResult(true); source.Complete(); await streaming; }
        Assert.IsTrue(output.Disposed);
    }

    [TestMethod]
    public async Task UnreleasedManagedPreRollIsDiscardedWithoutPlaybackOrDrain()
    {
        var factory = new RecordingOutputFactory();
        using var bridge = new MacPhoneAudioBridge(factory);
        await bridge.StreamAsync(new MemoryStream([1, 2]), Guid.NewGuid().ToString(), AudioStreamMode.Managed,
            (_, _) => { }, CancellationToken.None);
        var output = factory.Outputs.Single();
        Assert.HasCount(0, output.Writes);
        Assert.IsFalse(output.DrainCalled);
        Assert.IsTrue(output.Disposed);
    }

    [TestMethod]
    public void TypelessConfigReadsAllModesAndBlackHole()
    {
        const string json = """
            {
              "selectedMicrophoneDevice": {
                "label": "BlackHole 2ch",
                "description": "Core Audio"
              },
              "featureShortcutBindings": {
                "dictationMode": ["Fn"],
                "translationMode": ["Command+Shift+T"],
                "askAnythingMode": ["Control+Space"]
              }
            }
            """;

        var config = MacTypelessConfiguration.Parse(json, "/tmp/app-settings.json");

        Assert.IsTrue(config.UsesBlackHole);
        Assert.AreEqual("Fn", config.DictationBinding);
        Assert.AreEqual("Command+Shift+T", config.TranslationBinding);
        Assert.AreEqual("Control+Space", config.AskBinding);
    }

    [TestMethod]
    public void ExplicitShortcutOverrideWinsWithoutHardcodedFallback()
    {
        var config = MacTypelessConfiguration.Parse(
            "{}", "/tmp/app-settings.json",
            new TypelessShortcutOverrides { Dictation = "RightOption" });

        Assert.AreEqual("RightOption", config.DictationBinding);
        Assert.IsNull(config.TranslationBinding);
        Assert.IsNull(config.AskBinding);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 1_000;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
            {
                Assert.Fail("等待异步音频状态超时");
            }
            await Task.Delay(10);
        }
    }

    private static byte[] Silence(int milliseconds) => new byte[PcmLatency.MsToBytes(milliseconds)];

    private static byte[] Voice(int milliseconds)
    {
        var bytes = new byte[PcmLatency.MsToBytes(milliseconds)];
        for (var index = 0; index + 1 < bytes.Length; index += 2)
        {
            var sample = (short)((index / 2 / 24) % 2 == 0 ? 3_000 : -3_000);
            bytes[index] = (byte)sample;
            bytes[index + 1] = (byte)(sample >> 8);
        }
        return bytes;
    }

    [TestMethod]
    public async Task ManagedReleaseTrimsLeadingSilenceAndSkipsBacklogPauses()
    {
        var factory = new RecordingOutputFactory();
        using var bridge = new MacPhoneAudioBridge(factory);
        var source = new ChannelStream();
        var session = Guid.NewGuid().ToString();
        var streaming = bridge.StreamAsync(
            source, session, AudioStreamMode.Managed, (_, _) => { }, CancellationToken.None);
        await WaitUntilAsync(() => bridge.ActiveSessionId == session);
        // 共 1 秒，恰好不超过 pre-roll 容量。
        source.Push(Silence(300).Concat(Voice(100)).Concat(Silence(500)).Concat(Voice(100)).ToArray());
        await Task.Delay(100);

        bridge.BeginPlayback(session);
        source.Complete();
        await streaming;

        var stereoBytes = factory.Outputs[0].Writes.Sum(write => write.Length);
        // lead-in 150 ms + 语音 100 ms + 静音 hangover 200 ms + 语音 100 ms，双声道翻倍。
        Assert.AreEqual(PcmLatency.MsToBytes(550) * 2, stereoBytes);
    }

    [TestMethod]
    public async Task WarmOutputIsReusedAndKeptOpenBetweenSessions()
    {
        var factory = new RecordingOutputFactory();
        using var bridge = new MacPhoneAudioBridge(factory, keepOutputWarm: true);
        bridge.Prewarm();
        Assert.IsTrue(bridge.OutputWarm);

        for (var round = 0; round < 2; round++)
        {
            var source = new ChannelStream();
            source.Push(Voice(20));
            source.Complete();
            await bridge.StreamAsync(source, Guid.NewGuid().ToString(),
                AudioStreamMode.Shared, (_, _) => { }, CancellationToken.None);
        }

        Assert.HasCount(1, factory.Outputs);
        Assert.IsFalse(factory.Outputs[0].Disposed);
    }

    [TestMethod]
    public async Task FailedDrainDropsWarmOutputSoNextSessionRebuilds()
    {
        var factory = new RecordingOutputFactory { Drain = () => Task.FromResult(false) };
        using var bridge = new MacPhoneAudioBridge(factory, keepOutputWarm: true);
        var source = new ChannelStream();
        source.Push(Voice(20));
        source.Complete();

        await Assert.ThrowsExactlyAsync<IOException>(() => bridge.StreamAsync(source,
            Guid.NewGuid().ToString(), AudioStreamMode.Shared, (_, _) => { }, CancellationToken.None));

        Assert.IsTrue(factory.Outputs[0].Disposed);
        Assert.IsFalse(bridge.OutputWarm);
    }

    [TestMethod]
    public async Task SharedReconnectTakesOverButManagedStillConflicts()
    {
        var factory = new RecordingOutputFactory();
        using var bridge = new MacPhoneAudioBridge(factory, keepOutputWarm: true);
        var session = Guid.NewGuid().ToString();
        var stalled = new ChannelStream();
        var first = bridge.StreamAsync(stalled, session, AudioStreamMode.Shared,
            (_, _) => { }, CancellationToken.None);
        await WaitUntilAsync(() => bridge.ActiveSessionId == session);

        var managed = new ChannelStream();
        await Assert.ThrowsExactlyAsync<AudioStreamConflictException>(() => bridge.StreamAsync(
            managed, Guid.NewGuid().ToString(), AudioStreamMode.Managed, (_, _) => { }, CancellationToken.None));

        var reconnect = new ChannelStream();
        reconnect.Push(Voice(20));
        reconnect.Complete();
        await bridge.StreamAsync(reconnect, session, AudioStreamMode.Shared,
            (_, _) => { }, CancellationToken.None);

        Assert.IsTrue(first.IsCompleted);
        Assert.HasCount(1, factory.Outputs, "接管沿用同一常驻输出");
    }

    [TestMethod]
    public void RingReaderNeverBlocksAndReportsBacklog()
    {
        var ring = new MacAudioRingBuffer(8);
        ring.Write([1, 2, 3, 4, 5, 6]);
        Assert.AreEqual(6, ring.Count);
        var output = new byte[4];
        Assert.AreEqual(4, ring.Read(output));
        Assert.AreEqual(2, ring.Count);
        ring.Clear();
        Assert.AreEqual(0, ring.Count);
        Assert.AreEqual(0, ring.Read(output));
        CollectionAssert.AreEqual(new byte[4], output);
    }

    private sealed class RecordingOutputFactory : IMacAudioOutputFactory
    {
        internal List<RecordingOutput> Outputs { get; } = [];
        internal Func<Task<bool>> Drain { get; set; } = () => Task.FromResult(true);

        public (bool Available, string? DeviceName, string? DeviceUid, string? Error) Probe() =>
            (true, "BlackHole 2ch", "blackhole", null);

        public IMacAudioOutput Create()
        {
            var output = new RecordingOutput(Drain);
            Outputs.Add(output);
            return output;
        }
    }

    private sealed class RecordingOutput(Func<Task<bool>> drain) : IMacAudioOutput
    {
        private readonly object syncRoot = new();
        internal List<byte[]> Writes { get; } = [];
        internal bool DrainCalled, Disposed;
        public string DeviceName => "BlackHole 2ch";
        public string DeviceUid => "blackhole";
        public void Write(ReadOnlySpan<byte> stereoPcm16)
        {
            lock (syncRoot)
            {
                Writes.Add(stereoPcm16.ToArray());
            }
        }
        public Task<bool> DrainAsync(int timeoutMilliseconds, int tailMilliseconds)
        {
            DrainCalled = true;
            return drain();
        }
        public void Dispose() => Disposed = true;
        public int BufferedBytes => 0;
    }

    private sealed class ChannelStream : Stream
    {
        private readonly Channel<byte[]> channel = Channel.CreateUnbounded<byte[]>();
        private byte[]? current;
        private int offset;

        internal void Push(byte[] bytes) => channel.Writer.TryWrite(bytes);
        internal void Complete() => channel.Writer.TryComplete();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (current is null || offset >= current.Length)
            {
                try
                {
                    current = await channel.Reader.ReadAsync(cancellationToken);
                    offset = 0;
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }
            var count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }
    }
}
