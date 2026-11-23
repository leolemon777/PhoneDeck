using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class PhonePcmBufferTests
{
    [TestMethod]
    public void SharedBurstKeepsNewestAudioInsteadOfBuildingSecondsOfLag()
    {
        var buffer = new PhonePcmBuffer(AudioStreamMode.Shared);
        var pcm = Enumerable.Range(0, 48_000).Select(i => (byte)(i % 251)).ToArray();
        buffer.AddSamples(pcm, 0, pcm.Length);
        var limit = 48_000 * 2 * PhonePcmBuffer.SharedQueueMs / 1_000;
        var actual = new byte[limit];

        buffer.Read(actual, 0, actual.Length);

        CollectionAssert.AreEqual(pcm[^limit..], actual);
        Assert.AreEqual((long)pcm.Length - limit, buffer.DroppedBytes);
    }

    [TestMethod]
    public void SharedNewFrameReplacesOnlyOldestBacklog()
    {
        var buffer = new PhonePcmBuffer(AudioStreamMode.Shared);
        var limit = 48_000 * 2 * PhonePcmBuffer.SharedQueueMs / 1_000;
        var initial = Enumerable.Repeat((byte)1, limit).ToArray();
        var tail = Enumerable.Repeat((byte)2, 1_920).ToArray();
        buffer.AddSamples(initial, 0, initial.Length);
        buffer.AddSamples(tail, 0, tail.Length);
        var actual = new byte[limit];

        buffer.Read(actual, 0, limit);

        CollectionAssert.AreEqual(initial[tail.Length..].Concat(tail).ToArray(), actual);
        Assert.AreEqual((long)tail.Length, buffer.DroppedBytes);
    }

    [TestMethod]
    public void ManagedPreRollPreservesWholeUtterance()
    {
        var buffer = new PhonePcmBuffer(AudioStreamMode.Managed);
        var pcm = Enumerable.Range(0, 144_000).Select(i => (byte)(i % 251)).ToArray();
        buffer.AddSamples(pcm, 0, pcm.Length);
        var actual = new byte[pcm.Length];

        buffer.Read(actual, 0, actual.Length);

        CollectionAssert.AreEqual(pcm, actual);
        Assert.AreEqual(0L, buffer.DroppedBytes);
    }

    [TestMethod]
    public void PlaybackWaitsForJitterCushionThenPlaysWithoutLoss()
    {
        var buffer = new PhonePcmBuffer(AudioStreamMode.Shared);
        var small = VoiceLatencyTests.Voice(PlaybackQuality.PrefillMs - 10);
        buffer.AddSamples(small, 0, small.Length);
        var frame = new byte[PcmLatency.MsToBytes(10)];

        buffer.Read(frame, 0, frame.Length);
        Assert.IsTrue(frame.All(value => value == 0), "不足 40 ms 时先补静音等待");
        Assert.AreEqual(small.Length, buffer.BufferedBytes);

        var more = VoiceLatencyTests.Voice(20);
        buffer.AddSamples(more, 0, more.Length);
        var played = new byte[small.Length + more.Length];
        buffer.Read(played, 0, played.Length);
        CollectionAssert.AreEqual(small.Concat(more).ToArray(), played);
        Assert.AreEqual(0L, buffer.GapEvents, "开始前的等待不算断续");
    }

    [TestMethod]
    public void UnderrunThatResumesIsCountedAsOneGap()
    {
        var buffer = new PhonePcmBuffer(AudioStreamMode.Shared);
        var voice = VoiceLatencyTests.Voice(60);
        buffer.AddSamples(voice, 0, voice.Length);
        var frame = new byte[PcmLatency.MsToBytes(20)];
        for (var index = 0; index < 4; index++)
        {
            buffer.Read(frame, 0, frame.Length); // 第 4 次播空
        }

        buffer.AddSamples(voice, 0, voice.Length);
        buffer.Read(frame, 0, frame.Length);

        Assert.AreEqual(1L, buffer.GapEvents);
        Assert.AreEqual((long)PcmLatency.MsToBytes(20), buffer.GapBytes);
    }

    [TestMethod]
    public void FlushPlaysShortTailWithoutWaiting()
    {
        var buffer = new PhonePcmBuffer(AudioStreamMode.Managed);
        var tail = VoiceLatencyTests.Voice(10);
        buffer.AddSamples(tail, 0, tail.Length);

        buffer.Flush();
        var frame = new byte[tail.Length];
        buffer.Read(frame, 0, frame.Length);

        CollectionAssert.AreEqual(tail, frame);
        Assert.AreEqual(0, buffer.BufferedBytes);
    }

    [TestMethod]
    public void DigitalZeroInputIsCountedSeparatelyFromQuietAudio()
    {
        var buffer = new PhonePcmBuffer(AudioStreamMode.Shared);
        var zeros = new byte[PcmLatency.MsToBytes(100)];
        var quiet = VoiceLatencyTests.Voice(100, amplitude: 1);
        buffer.AddSamples(zeros, 0, zeros.Length);
        buffer.AddSamples(quiet, 0, quiet.Length);

        Assert.AreEqual((long)zeros.Length, buffer.DigitalSilenceBytes);
    }
}
