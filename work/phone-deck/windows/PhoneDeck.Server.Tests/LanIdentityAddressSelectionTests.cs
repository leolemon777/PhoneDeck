using System.Net;
using System.Net.NetworkInformation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class LanIdentityAddressSelectionTests
{
    private static LanIdentity.AddressCandidate Candidate(
        string address, bool gateway = false,
        NetworkInterfaceType type = NetworkInterfaceType.Ethernet) =>
        new(IPAddress.Parse(address), gateway, type);

    [TestMethod]
    public void ExcludesLoopbackApapaAndBenchmarkTunRanges()
    {
        var selected = LanIdentity.SelectCandidateAddresses(new[]
        {
            Candidate("192.168.1.244", gateway: true),
            Candidate("127.0.0.1"),
            Candidate("169.254.7.7"),
            Candidate("198.18.0.1", gateway: true),
            Candidate("198.19.55.3"),
            Candidate("10.0.0.5")
        });

        CollectionAssert.AreEqual(new[] { "192.168.1.244", "10.0.0.5" }, selected);
    }

    [TestMethod]
    public void RealLanAddressOutranksTunAddressRegardlessOfOrder()
    {
        // 198.18.0.1 属于 198.18.0.0/15 基准/TUN 网段，必须被排除，
        // 即使它先被枚举且接口带“网关”（Meta/Clash TUN 常伪装成 Ethernet）。
        var selected = LanIdentity.SelectCandidateAddresses(new[]
        {
            Candidate("198.18.0.1", gateway: true, type: NetworkInterfaceType.Ethernet),
            Candidate("192.168.1.244", gateway: true, type: NetworkInterfaceType.Wireless80211)
        });

        CollectionAssert.AreEqual(new[] { "192.168.1.244" }, selected);
    }

    [TestMethod]
    public void GatewayInterfacesRankAheadOfGatewaylessVirtualAdapters()
    {
        var selected = LanIdentity.SelectCandidateAddresses(new[]
        {
            Candidate("172.20.0.1", gateway: false),          // 虚拟交换机
            Candidate("192.168.1.244", gateway: true),        // 真实 Wi-Fi
            Candidate("192.168.137.1", gateway: false)        // ICS 虚拟网卡
        });

        CollectionAssert.AreEqual(
            new[] { "192.168.1.244", "172.20.0.1", "192.168.137.1" }, selected);
    }

    [TestMethod]
    public void DeduplicatesAndKeepsFirstOccurrence()
    {
        var selected = LanIdentity.SelectCandidateAddresses(new[]
        {
            Candidate("192.168.1.244", gateway: true),
            Candidate("192.168.1.244", gateway: false),
            Candidate("10.1.2.3", gateway: true)
        });

        CollectionAssert.AreEqual(new[] { "192.168.1.244", "10.1.2.3" }, selected);
    }

    [TestMethod]
    public void AllExcludedWhenOnlyTunAddressExists()
    {
        var selected = LanIdentity.SelectCandidateAddresses(new[]
        {
            Candidate("198.18.0.1", gateway: true)
        });

        Assert.AreEqual(0, selected.Length);
    }
}
