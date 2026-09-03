using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class LanRequestAuthenticatorTests
{
    [TestMethod]
    public void UsbPortDoesNotRequireLanToken()
    {
        Assert.IsTrue(LanRequestAuthenticator.IsAuthorized(8765, null, "secret"));
    }

    [TestMethod]
    public void LanPortRequiresExactToken()
    {
        Assert.IsFalse(LanRequestAuthenticator.IsAuthorized(8766, null, "secret"));
        Assert.IsFalse(LanRequestAuthenticator.IsAuthorized(8766, "wrong", "secret"));
        Assert.IsTrue(LanRequestAuthenticator.IsAuthorized(8766, "secret", "secret"));
    }

    [TestMethod]
    public void PairingMaterialIsOnlyAvailableOnUsbLoopback()
    {
        Assert.IsTrue(LanRequestAuthenticator.IsUsbPairingRequest(
            8765, IPAddress.Loopback));
        Assert.IsFalse(LanRequestAuthenticator.IsUsbPairingRequest(
            8766, IPAddress.Loopback));
        Assert.IsFalse(LanRequestAuthenticator.IsUsbPairingRequest(
            8765, IPAddress.Parse("192.168.1.20")));
    }
}
