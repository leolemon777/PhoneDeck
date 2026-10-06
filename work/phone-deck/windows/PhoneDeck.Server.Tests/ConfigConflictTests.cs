using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// M1-B / V47（L1 部分）：两组配置修订流独立、过期修订冲突语义、原子写失败不留半文件。
/// 手机端草稿保留与“不自动用响应覆盖本地 revision”属 L3 真机验收。
/// </summary>
[TestClass]
public sealed class ConfigConflictTests
{
    [TestMethod]
    public void VoiceAndConnectionRevisionsAreIndependentStreams()
    {
        var voiceA = new { engine = "typeless", mode = "click" };
        var voiceB = new { engine = "typeless", mode = "hold" };
        var connection = new { usbWatchdog = true, lanDiscovery = true };
        // voice 流内部：内容变化 → revision 变化；connection 对象与 voice 流互不相干。
        Assert.AreNotEqual(
            DesktopConfiguration.Revision(voiceA),
            DesktopConfiguration.Revision(voiceB));
        // 修订是内容哈希：与对象身份无关，同内容即同修订（幂等重放不产生新修订）。
        Assert.AreEqual(
            DesktopConfiguration.Revision(voiceA),
            DesktopConfiguration.Revision(new { engine = "typeless", mode = "click" }));
        Assert.AreNotEqual(
            DesktopConfiguration.Revision(voiceA),
            DesktopConfiguration.Revision(connection));
    }

    [TestMethod]
    public void StaleRevisionConflictIsExplicitAndDoesNotAutoMerge()
    {
        var current = DesktopConfiguration.Revision(new { engine = "typeless" });
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DesktopConfiguration.CheckRevision("stale-revision", current));
        // 相同修订通过：确认-保存窗口内不冲突。
        DesktopConfiguration.CheckRevision(current, current);
    }

    [TestMethod]
    public void AtomicWriteFailureLeavesPreviousFileIntact()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "phonedeck-m1b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "server-settings.json");
        try
        {
            DesktopConfiguration.WriteAtomic(path, "{\"marker\":\"old\"}");
            var baseline = File.ReadAllText(path);
            // 目标被独占 → 写失败必须抛 IOException/UnauthorizedAccessException
            // （端点对两者均映射 500）且原文件保持旧内容（无半写文件）。
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
                    DesktopConfiguration.WriteAtomic(path, "{\"marker\":\"new\"}"));
            }
            Assert.AreEqual(baseline, File.ReadAllText(path),
                "写失败后原文件保持不变（V47/V49 一致性）");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
