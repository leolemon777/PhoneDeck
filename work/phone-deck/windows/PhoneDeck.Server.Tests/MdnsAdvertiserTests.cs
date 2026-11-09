using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// M1-A A5 / V10 子集（L2）：发现层无泄漏不变量。
/// mDNS TXT 字段必须与 UDP 应答同一集合（computerId/displayName/platform/port/capabilities），
/// 绝不携带配对令牌或证书指纹（NET-01）；实例名满足 DNS 标签约束。
/// </summary>
[TestClass]
public sealed class MdnsAdvertiserTests
{
    private static readonly string[] SecretKeyMarkers =
    {
        "token", "secret", "password", "accessKey", "fingerprint", "sha256", "certificate",
    };

    [TestMethod]
    public void TxtFieldsMirrorUdpAdvertisementSetAndCarryNoSecrets()
    {
        var fields = MdnsAdvertiser.BuildTxtFields(
            "11111111-2222-4333-8333-444444444444",
            "PLACEHOLDER-DESKTOP",
            "windows",
            8766);

        CollectionAssert.AreEquivalent(
            new[] { "computerId", "displayName", "platform", "port", "capabilities" },
            fields.Keys.ToList());
        Assert.AreEqual("11111111-2222-4333-8333-444444444444", fields["computerId"]);
        Assert.AreEqual("PLACEHOLDER-DESKTOP", fields["displayName"]);
        Assert.AreEqual("windows", fields["platform"]);
        Assert.AreEqual("8766", fields["port"]);
        Assert.AreEqual(
            "fixedAction,keyChord,text,macro,phoneAudio,managedDictation,secureLan",
            fields["capabilities"]);
        foreach (var marker in SecretKeyMarkers)
        {
            Assert.IsFalse(fields.Keys.Any(key => key.Contains(marker, StringComparison.OrdinalIgnoreCase)),
                $"发现字段不得包含敏感键：{marker}");
        }
        // 值同样不得包含共享令牌或证书指纹形态（64 位十六进制）。
        foreach (var value in fields.Values)
        {
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(
                value, "^[0-9a-f]{64}$"), "发现字段值不得为证书指纹形态");
        }
    }

    [TestMethod]
    public void EmptyPlatformFallsBackToUnknownAndCustomCapabilitiesAreKept()
    {
        var fields = MdnsAdvertiser.BuildTxtFields("c1", "desk", "", 8766, new[] { "a", "b" });
        Assert.AreEqual("unknown", fields["platform"]);
        Assert.AreEqual("a,b", fields["capabilities"]);
    }

    [TestMethod]
    public void InstanceNameSatisfiesDnsLabelConstraints()
    {
        var sanitized = MdnsAdvertiser.SanitizeInstanceName(
            "id/with 非法字符.and.dots-" + new string('x', 80));
        Assert.IsTrue(sanitized.Length <= 63, "DNS 标签 ≤63");
        Assert.IsTrue(sanitized.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'),
            "仅 ASCII 字母数字与连字符：" + sanitized);
        Assert.AreEqual("11111111-2222", MdnsAdvertiser.SanitizeInstanceName("11111111-2222"));
    }

    [TestMethod]
    public void NonAsciiDisplayNameSerializesToDnsWireWithoutException()
    {
        var profile = MdnsAdvertiser.CreateProfile(
            "0c24d2de-4dda-4d0e-b74d-56c489601d2e",
            "往里走的COMPUTE",
            "windows",
            8766,
            addresses: new[] { System.Net.IPAddress.Parse("192.168.0.100") });

        foreach (var resource in profile.Resources)
        {
            var bytes = resource.ToByteArray();
            Assert.IsTrue(bytes.Length > 0);
        }
    }
}
