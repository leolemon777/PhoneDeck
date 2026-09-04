namespace PhoneDeck.Server.Tests;

[TestClass]
public sealed class AudioStreamModeTests
{
    [TestMethod]
    public void MissingHeaderKeepsLegacyManagedBehavior()
    {
        var mode = AudioStreamModes.Parse(null);

        Assert.AreEqual(AudioStreamMode.Managed, mode);
        Assert.IsTrue(mode.ControlsTypeless());
        Assert.AreEqual("managed", mode.ToWireValue());
    }

    [TestMethod]
    public void SharedModeNeverControlsTypeless()
    {
        var mode = AudioStreamModes.Parse("shared");

        Assert.AreEqual(AudioStreamMode.Shared, mode);
        Assert.IsFalse(mode.ControlsTypeless());
        Assert.AreEqual("shared", mode.ToWireValue());
    }

    [TestMethod]
    public void UnknownModeIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => AudioStreamModes.Parse("broadcast"));
    }
}
