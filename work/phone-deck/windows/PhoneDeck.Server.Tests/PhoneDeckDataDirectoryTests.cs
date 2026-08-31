using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class PhoneDeckDataDirectoryTests
{
    [TestMethod]
    public void Resolve_UsesConfiguredPortableDirectory()
    {
        var expected = Path.GetFullPath(Path.Combine("E:\\", "PhoneDeck", "data"));

        var actual = PhoneDeckDataDirectory.Resolve(expected, "C:\\LocalAppData");

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void Resolve_FallsBackToLocalApplicationData()
    {
        var actual = PhoneDeckDataDirectory.Resolve(null, "C:\\LocalAppData");

        Assert.AreEqual(Path.Combine("C:\\LocalAppData", "PhoneDeck"), actual);
    }
}
