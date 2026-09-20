using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Wave;

[TestClass]
public sealed class PhoneAudioTailTests
{
    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(true, 0)]
    [DataRow(false, 1)]
    [DataRow(true, 1)]
    [DataRow(false, 2)]
    [DataRow(true, 2)]
    public async Task EofOrAbortedUploadPlaysEveryReceivedTailSample(
        bool shared, int ending)
    {
        DelayedPlayback? playback = null;
        using var bridge = new PhoneAudioBridge(source =>
            playback = new DelayedPlayback(source));
        var session = Guid.NewGuid().ToString();
        // Nonzero PCM markers make silence distinguishable from the final word.
        var pcm = Enumerable.Range(0, 9_600)
            .Select(index => (byte)(1 + index % 251)).ToArray();
        using var input = new EndingStream(pcm, ending, () =>
        {
            if (!shared)
            {
                bridge.BeginPlayback(session);
            }
        });
        var callback = false;

        var bytes = await bridge.StreamAsync(input, session,
            shared ? AudioStreamMode.Shared : AudioStreamMode.Managed,
            (_, _) =>
            {
                callback = true;
                Assert.IsTrue(playback!.Stopped);
                CollectionAssert.AreEqual(pcm, playback.Rendered.ToArray(),
                    "AudioEnded must not stop the engine before the last device frame.");
            }, CancellationToken.None);

        Assert.AreEqual((long)pcm.Length, bytes);
        Assert.IsTrue(callback);
        Assert.IsTrue(bridge.WaitForSessionEnd(session, 1));
        Assert.IsFalse(bridge.IsStreaming);
    }

    [TestMethod]
    public async Task CancelledUnconfirmedStartDoesNotPlayPreRoll()
    {
        DelayedPlayback? playback = null;
        using var bridge = new PhoneAudioBridge(source =>
            playback = new DelayedPlayback(source));
        using var input = new EndingStream(new byte[] { 1, 2, 3, 4 }, 2, () => {});

        await bridge.StreamAsync(input, Guid.NewGuid().ToString(),
            AudioStreamMode.Managed, (_, _) => {}, CancellationToken.None);

        Assert.AreEqual(0, playback!.Rendered.Count);
    }

    [TestMethod]
    public async Task PlaybackFailureDoesNotReportSuccessfulDrain()
    {
        using var bridge = new PhoneAudioBridge(_ => new StalledPlayback());
        var session = Guid.NewGuid().ToString();
        using var input = new MemoryStream(new byte[] { 1, 2, 3, 4 });

        await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            bridge.StreamAsync(input, session, AudioStreamMode.Shared,
                (_, _) => {}, CancellationToken.None));

        Assert.IsFalse(bridge.WaitForSessionEnd(session, 1));
        Assert.IsFalse(bridge.IsStreaming);
    }

    private sealed class EndingStream(byte[] pcm, int ending, Action onEnd)
        : MemoryStream(pcm)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length)
            {
                return base.ReadAsync(buffer, cancellationToken);
            }
            onEnd();
            return ending switch
            {
                1 => ValueTask.FromException<int>(new IOException("upload disconnected")),
                2 => ValueTask.FromException<int>(new OperationCanceledException()),
                _ => ValueTask.FromResult(0)
            };
        }
    }

    private sealed class DelayedPlayback(IWaveProvider source) : IPhoneAudioPlayback
    {
        private volatile bool playing;
        private Task? worker;
        internal List<byte> Rendered { get; } = new();
        internal bool Stopped { get; private set; }

        public void Play()
        {
            playing = true;
            worker = Task.Run(async () =>
            {
                var frame = new byte[1_920];
                while (playing)
                {
                    source.Read(frame, 0, frame.Length);
                    // Simulate PCM already consumed from the provider but not yet
                    // emitted by the physical/virtual device. Stop would lose it.
                    await Task.Delay(30);
                    if (playing)
                    {
                        Rendered.AddRange(frame.Where(value => value != 0));
                    }
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

    private sealed class StalledPlayback : IPhoneAudioPlayback
    {
        public void Play() {}
        public void Stop() {}
        public void Dispose() {}
    }
}
