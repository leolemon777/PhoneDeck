using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class PreRollBufferTests
{
    [TestMethod]
    public void TakeAllReturnsBytesInWriteOrderWithoutLossOrDuplication()
    {
        var buffer = new PreRollBuffer(16);
        buffer.Write(new byte[] { 1, 2, 3, 4 }, 0, 4);
        buffer.Write(new byte[] { 5, 6, 7, 8 }, 0, 4);

        var taken = buffer.TakeAll();

        CollectionAssert.AreEqual(
            new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, taken,
            "pre-roll 必须保序、不丢、不重复");
        Assert.AreEqual(0, buffer.StoredBytes);
        Assert.AreEqual(0, buffer.TakeAll().Length, "取空后不能再次吐出旧数据");
    }

    [TestMethod]
    public void WraparoundPreservesOrder()
    {
        var buffer = new PreRollBuffer(6);
        buffer.Write(new byte[] { 1, 2, 3, 4, 5 }, 0, 5);   // 跨容量写
        buffer.Write(new byte[] { 6, 7 }, 0, 2);            // 触发回绕+丢最旧

        var taken = buffer.TakeAll();

        // 容量 6，共写 7 字节：最旧的 1 被覆盖，保留 2..7。
        CollectionAssert.AreEqual(new byte[] { 2, 3, 4, 5, 6, 7 }, taken);
    }

    [TestMethod]
    public void OversizedWriteKeepsOnlyMostRecentWindow()
    {
        var buffer = new PreRollBuffer(4);
        buffer.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 0, 8);

        CollectionAssert.AreEqual(new byte[] { 5, 6, 7, 8 }, buffer.TakeAll());
    }

    [TestMethod]
    public void StoredBytesTracksCapacityBound()
    {
        var buffer = new PreRollBuffer(4);
        buffer.Write(new byte[] { 1, 2 }, 0, 2);
        Assert.AreEqual(2, buffer.StoredBytes);
        buffer.Write(new byte[] { 3, 4, 5, 6, 7, 8 }, 0, 6);
        Assert.AreEqual(4, buffer.StoredBytes);
    }

    [TestMethod]
    public void TakeAllIsAtomicAgainstConcurrentWrites()
    {
        var buffer = new PreRollBuffer(1024);
        var writer = new Thread(() =>
        {
            var chunk = new byte[64];
            for (var i = 0; i < 2_000; i++)
            {
                buffer.Write(chunk, 0, chunk.Length);
            }
        });
        writer.Start();

        var totalTaken = 0;
        while (writer.IsAlive)
        {
            totalTaken += buffer.TakeAll().Length;
            Thread.Sleep(1);
        }
        totalTaken += buffer.TakeAll().Length;

        Assert.IsTrue(totalTaken <= 2_000 * 64, "取出的数据不能多于写入的数据");
        writer.Join();
    }
}
