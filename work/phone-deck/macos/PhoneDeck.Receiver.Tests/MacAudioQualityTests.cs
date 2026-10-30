using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PhoneDeck.MacReceiver.Tests;

[TestClass]
public sealed class MacAudioQualityTests
{
    [TestMethod]
    public void GapIsCountedOnlyWhenAudioResumes()
    {
        var gaps = new AudioGapCounter();

        gaps.Observe(0, 512); // 开始前的空闲不算断续
        gaps.Observe(512, 512);
        gaps.Observe(100, 512);
        gaps.Observe(0, 512);
        Assert.AreEqual(0, gaps.Events, "恢复之前不能计数");

        gaps.Observe(512, 512);
        Assert.AreEqual(1, gaps.Events);
        Assert.AreEqual(412 + 512, gaps.Bytes);
    }

    [TestMethod]
    public void EndOfStreamDrainIsNotAGap()
    {
        var gaps = new AudioGapCounter();
        gaps.Observe(512, 512);
        for (long silent = 0; silent <= AudioGapCounter.MaxGapBytes; silent += 512)
        {
            gaps.Observe(0, 512);
        }

        gaps.Observe(512, 512); // 下一个会话开始

        Assert.AreEqual(0, gaps.Events);
        Assert.AreEqual(0, gaps.Bytes);
    }

    [TestMethod]
    public void RingCountsBytesOverwrittenBeforePlayback()
    {
        var ring = new MacAudioRingBuffer(8);
        ring.Write(new byte[6]);
        ring.Write(new byte[6]);

        var read = ring.Read(new byte[8]);

        Assert.AreEqual(8, read);
        Assert.AreEqual(4, ring.DroppedBytes);
    }

    [TestMethod]
    public void PrefillWaitsForCushionAtStartAndAfterUnderrun()
    {
        var prefill = new JitterPrefill(prefillBytes: 100);

        Assert.IsFalse(prefill.ShouldPlay(99));
        Assert.IsTrue(prefill.ShouldPlay(100));
        prefill.AfterRead(64, 64);
        Assert.IsTrue(prefill.ShouldPlay(10), "已在播放时不因缓冲变浅而停顿");

        prefill.AfterRead(10, 64); // 播空
        Assert.IsFalse(prefill.ShouldPlay(50));
        Assert.IsTrue(prefill.ShouldPlay(100));
    }

    [TestMethod]
    public void FlushPlaysTailWithoutWaitingForCushion()
    {
        var prefill = new JitterPrefill(prefillBytes: 100);
        prefill.AfterRead(0, 64);

        prefill.Flush();
        Assert.IsTrue(prefill.ShouldPlay(30), "尾音不足 40 ms 也必须播完");

        prefill.Resume();
        prefill.AfterRead(0, 64);
        Assert.IsFalse(prefill.ShouldPlay(30));
    }

    [TestMethod]
    public void ReceiverLogTimestampsLinesAndRotates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "phonedeck-log-" + Guid.NewGuid());
        var path = Path.Combine(directory, "receiver.log");
        var console = new StringWriter();
        try
        {
            using (var log = new ReceiverLog(console, path, maxBytes: 300))
            {
                log.WriteLine("第一行");
                log.Write("部分");
                log.WriteLine("内容");
                for (var index = 0; index < 10; index++)
                {
                    log.WriteLine($"填充 {index}");
                }
            }

            StringAssert.Contains(console.ToString(), "第一行");
            var rotated = Path.Combine(directory, "receiver.1.log");
            Assert.IsTrue(File.Exists(rotated), "超过上限应轮换旧文件");
            var all = File.ReadAllText(rotated) + File.ReadAllText(path);
            StringAssert.Contains(all, "部分内容");
            StringAssert.Matches(File.ReadAllLines(path)[^1],
                new System.Text.RegularExpressions.Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} 填充 9$"));
            Assert.IsTrue(new FileInfo(path).Length <= 300);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
