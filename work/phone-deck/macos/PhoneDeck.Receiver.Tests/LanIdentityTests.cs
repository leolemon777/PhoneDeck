using PhoneDeck.MacReceiver;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PhoneDeck.MacReceiver.Tests;

[TestClass]
public sealed class LanIdentityTests
{
    [TestMethod]
    public void LoadOrCreatePersistsReloadableCertificateWithPrivateKey()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PhoneDeck-LanIdentityTests-" + Guid.NewGuid().ToString("N"));
        var computerId = Guid.NewGuid().ToString();

        try
        {
            string firstFingerprint;
            string firstToken;
            using (var first = LanIdentity.LoadOrCreate(computerId, directory))
            {
                Assert.IsTrue(first.Certificate.HasPrivateKey);
                firstFingerprint = first.CertificateSha256;
                firstToken = first.AccessToken;
            }

            using var reloaded = LanIdentity.LoadOrCreate(computerId, directory);
            Assert.IsTrue(reloaded.Certificate.HasPrivateKey);
            Assert.AreEqual(firstFingerprint, reloaded.CertificateSha256);
            Assert.AreEqual(firstToken, reloaded.AccessToken);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
