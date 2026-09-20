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
}
