using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class FleetUpdateTests
{
    private string root = null!;
    private RSA key = null!;
    [TestInitialize] public void Init() { root = Path.Combine(Path.GetTempPath(), "phonedeck-update-test-" + Guid.NewGuid()); Directory.CreateDirectory(root); key = RSA.Create(2048); }
    [TestCleanup] public void Cleanup() { key.Dispose(); Directory.Delete(root, true); }

    private string Package(bool tamper = false, string? extra = null, bool badSignature = false, bool missing = false)
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var files = UpdatePackage.Names.Select(n => new UpdateArtifact(n, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)))).ToArray();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new UpdateManifest(1, 23, "1.6.0-dev.11", 22, files), UpdatePackage.Json);
        var signature = key.SignData(manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (badSignature) signature[0] ^= 1;
        var path = Path.Combine(root, Guid.NewGuid() + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, byte[] data) { using var output = archive.CreateEntry(name).Open(); output.Write(data); }
        Add("manifest.json", manifest); Add("manifest.sig", signature);
        foreach (var name in UpdatePackage.Names)
        {
            if (missing && name == "PhoneDeck.apk") continue;
            Add(name, tamper && name == "PhoneDeck.apk" ? [4, 3, 2, 1] : bytes);
        }
        if (extra is not null) Add(extra, bytes);
        return path;
    }
    private UpdateManifest Verify(string path, string? destination = null) => UpdatePackage.Verify(path, destination, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

    [TestMethod] public void ValidPackageExtractsOnlyFixedArtifacts()
    {
        var stage = Path.Combine(root, "verified");
        Assert.AreEqual(23L, Verify(Package(), stage).Sequence);
        Assert.AreEqual(3, Directory.GetFiles(stage).Length);
    }
    [TestMethod] public void UpdateWaitsForInFlightAudioRequestAndRejectsNewUseOnceInstalling()
    {
        var updates = new FleetUpdates(root);
        Assert.IsTrue(updates.EnterUse());
        Assert.IsFalse(updates.BeginInstall(() => false));
        updates.ExitUse();
        Assert.IsFalse(updates.BeginInstall(() => true));
        Assert.IsTrue(updates.BeginInstall(() => false));
        Assert.IsFalse(updates.EnterUse());
    }
    [TestMethod] public void TamperedPayloadNeverExtractsAnyFile()
    {
        var stage = Path.Combine(root, "verified");
        Assert.ThrowsExactly<InvalidDataException>(() => Verify(Package(tamper: true), stage));
        Assert.IsFalse(Directory.Exists(stage));
    }
    [TestMethod] public void BadPublisherSignatureRejected() => Assert.ThrowsExactly<InvalidDataException>(() => Verify(Package(badSignature: true)));
    [TestMethod] public void UntrustedPublisherRejected() => Assert.ThrowsExactly<InvalidDataException>(() => UpdatePackage.Verify(Package()));
    [TestMethod] public void IncompletePackageRejected() => Assert.ThrowsExactly<InvalidDataException>(() => Verify(Package(missing: true)));
    [TestMethod]
    [DataRow("../PhoneDeck.Server.exe")]
    [DataRow("data/computer-id.txt")]
    [DataRow("PhoneDeck.apk")]
    [DataRow("install.ps1")]
    public void ExtraPathsAndDuplicateEntriesRejected(string extra) => Assert.ThrowsExactly<InvalidDataException>(() => Verify(Package(extra: extra)));

    [TestMethod] public void RollbackRestoresFilesWithoutTouchingIdentity()
    {
        var install = Path.Combine(root, "app"); Directory.CreateDirectory(install);
        var data = Path.Combine(install, "data"); Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "computer-id.txt"), "keep-identity");
        File.WriteAllText(Path.Combine(install, "PhoneDeck.Server.exe"), "old-server");
        var stage = Path.Combine(root, "stage"); Verify(Package(), stage);
        var transaction = new UpdateFileTransaction(install, stage, Path.Combine(root, "backup"));
        transaction.Apply(); transaction.Rollback();
        Assert.AreEqual("old-server", File.ReadAllText(Path.Combine(install, "PhoneDeck.Server.exe")));
        Assert.IsFalse(File.Exists(Path.Combine(install, "PhoneDeck.ControlCenter.exe")));
        Assert.AreEqual("keep-identity", File.ReadAllText(Path.Combine(data, "computer-id.txt")));
    }
    [TestMethod] public void PartialCopyFailureRestoresFirstFile()
    {
        var install = Path.Combine(root, "app"); Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "PhoneDeck.Server.exe"), "old-server");
        File.WriteAllText(Path.Combine(install, "PhoneDeck.ControlCenter.exe"), "old-console");
        var stage = Path.Combine(root, "stage"); Verify(Package(), stage);
        File.Delete(Path.Combine(stage, "PhoneDeck.ControlCenter.exe"));
        var transaction = new UpdateFileTransaction(install, stage, Path.Combine(root, "backup"));
        Assert.ThrowsExactly<FileNotFoundException>(() => transaction.Apply());
        transaction.Rollback();
        Assert.AreEqual("old-server", File.ReadAllText(Path.Combine(install, "PhoneDeck.Server.exe")));
        Assert.AreEqual("old-console", File.ReadAllText(Path.Combine(install, "PhoneDeck.ControlCenter.exe")));
    }
}
