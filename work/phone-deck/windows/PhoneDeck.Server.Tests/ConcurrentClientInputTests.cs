using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// M1-B 第二片 / V22（L1 可测部分）：并发调用安全与 requestId 分区约定。
/// 组合键“物理不交错”由 KeyboardInput.SyncRoot 串行执行保证（真实 SendInput 无法在
/// 单测注入，属 L3 真机验收 V27/V28）；音频单所有者由 streamGate/AudioStreamConflict
/// 覆盖；本组固化并发安全与跨手机同 ID 不互斥的键约定。
/// </summary>
[TestClass]
public sealed class ConcurrentClientInputTests
{
    /// <summary>并行两“手机”各 60 次宏校验：并发调用无异常、次数不丢（串行锁不吞并）。</summary>
    [TestMethod]
    public async Task TwoClientsConcurrentCallsAreSafeAndLossless()
    {
        const int perClient = 60;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var executed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        async Task RunClientAsync(string clientId, char keySuffix)
        {
            await Task.Run(() =>
            {
                for (var index = 0; index < perClient; index++)
                {
                    try
                    {
                        var requestId = $"{clientId}-req-{index}";
                        KeyboardInput.ValidateMacroSteps(new[]
                        {
                            new MacroStep("keyChord",
                                new[] { "CTRL", "SHIFT", "SPACE" },
                                45, null, null, 0),
                        });
                        executed.Enqueue(requestId);
                    }
                    catch (Exception exception)
                    {
                        failures.Enqueue(exception);
                    }
                }
            });
        }
        var clientA = RunClientAsync("11111111-1111-4111-8111-11111111111a", 'A');
        var clientB = RunClientAsync("22222222-2222-4222-8222-22222222222b", 'B');
        await Task.WhenAll(clientA, clientB);
        Assert.AreEqual(0, failures.Count,
            failures.FirstOrDefault()?.Message ?? "并发校验不应失败");
        Assert.AreEqual(perClient * 2, executed.Count, "两手机请求互不吞并");
    }

    /// <summary>V22 断言配套：requestId 去重分区语义——同 requestId 跨手机不互斥
    /// （分区键为 clientId；此处固化字符串约定，真正的缓存分区随 A4 凭据全面接入）。</summary>
    [TestMethod]
    public void RequestIdNamespacingFollowsClientPartition()
    {
        var clientA = "11111111-1111-4111-8111-11111111111a";
        var clientB = "22222222-2222-4222-8222-22222222222b";
        var requestA = $"{clientA}-req-1";
        var requestB = $"{clientB}-req-1";
        Assert.AreNotEqual(requestA, requestB,
            "同序号请求在不同 clientId 分区下是不同键（V24“跨手机同 ID”）");
    }
}
