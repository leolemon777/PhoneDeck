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

    private sealed class RecordingOutputFactory : IMacAudioOutputFactory
    {
        internal List<RecordingOutput> Outputs { get; } = [];

        public (bool Available, string? DeviceName, string? DeviceUid, string? Error) Probe() =>
            (true, "BlackHole 2ch", "blackhole", null);

        public IMacAudioOutput Create()
        {
            var output = new RecordingOutput();
            Outputs.Add(output);
            return output;
        }
    }

    private sealed class RecordingOutput : IMacAudioOutput
    {
        private readonly object syncRoot = new();
        internal List<byte[]> Writes { get; } = [];
        public string DeviceName => "BlackHole 2ch";
        public string DeviceUid => "blackhole";
        public void Write(ReadOnlySpan<byte> stereoPcm16)
        {
            lock (syncRoot)
            {
                Writes.Add(stereoPcm16.ToArray());
            }
        }
        public void Dispose() { }
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
