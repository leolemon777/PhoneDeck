using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// V13 台架（L1）：8765 回环入口 Host/Origin 防护矩阵（SEC-02，修复 KNOWN_ISSUES #3）。
/// 覆盖：合法本机来源放行（无 Origin 的原生客户端 / 同源 Origin）、恶意 Host、
/// 跨站 Origin（evil.com / null origin 值）、跨站 Referer、大小写与尾斜杠归一。
/// </summary>
[TestClass]
public sealed class LoopbackOriginGuardTests
{
    [TestMethod]
    public void NativeClientWithoutOriginPasses()
    {
        // ControlCenter/HttpClient 不发 Origin/Referer——放行（同用户本机进程受信任）。
        Assert.IsNull(LoopbackOriginGuard.Validate("127.0.0.1:8765", null, null));
        Assert.IsNull(LoopbackOriginGuard.Validate("localhost:8765", null, null));
        Assert.IsNull(LoopbackOriginGuard.Validate("[::1]:8765", null, null));
        Assert.IsNull(LoopbackOriginGuard.Validate("LOCALHOST:8765", null, null), "大小写归一");
    }

    [TestMethod]
    public void SameOriginBrowserRequestsPass()
    {
        Assert.IsNull(LoopbackOriginGuard.Validate(
            "127.0.0.1:8765", "http://127.0.0.1:8765", null));
        Assert.IsNull(LoopbackOriginGuard.Validate(
            "localhost:8765", "http://localhost:8765/", "http://localhost:8765/some/page"));
    }

    [TestMethod]
    public void MaliciousHostIsRejected()
    {
        // 表单 action 指向别名的 Host 头伪造 / 空_host。
        Assert.IsNotNull(LoopbackOriginGuard.Validate("evil.com", null, null));
        Assert.IsNotNull(LoopbackOriginGuard.Validate("", null, null));
        Assert.IsNotNull(LoopbackOriginGuard.Validate(null, null, null));
        Assert.IsNotNull(LoopbackOriginGuard.Validate("127.0.0.1:9999", null, null), "端口不符");
        // DNS 重绑定 形态：Host 是域名但解析到回环——必须拒（只认字面回环 Host）。
        Assert.IsNotNull(LoopbackOriginGuard.Validate("attacker.example", null, null));
    }

    [TestMethod]
    public void CrossSiteOriginAndRefererAreRejected()
    {
        Assert.IsNotNull(LoopbackOriginGuard.Validate(
            "127.0.0.1:8765", "http://evil.com", null), "跨站 Origin");
        Assert.IsNotNull(LoopbackOriginGuard.Validate(
            "127.0.0.1:8765", "https://evil.example", null));
        Assert.IsNotNull(LoopbackOriginGuard.Validate(
            "127.0.0.1:8765", "null", null), "沙箱 iframe 的 Origin: null 值");
        Assert.IsNotNull(LoopbackOriginGuard.Validate(
            "127.0.0.1:8765", null, "http://evil.com/page"), "跨站 Referer");
        Assert.IsNotNull(LoopbackOriginGuard.Validate(
            "127.0.0.1:8765", "http://127.0.0.1:8766", null), "端口伪装");
    }
}
