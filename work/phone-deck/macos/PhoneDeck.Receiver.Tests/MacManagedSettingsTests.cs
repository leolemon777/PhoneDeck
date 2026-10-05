using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PhoneDeck.MacReceiver.Tests;

[TestClass]
public sealed class MacManagedSettingsTests
{
    private static MacVoiceEngineCatalog Catalog() => MacVoiceEngines.Catalog;

    [TestMethod]
    public void ValidatorAcceptsKnownEngineAndMacBindings()
    {
        var settings = MacVoiceSettingsValidator.ValidateVoice(Catalog(), new DesktopVoiceRequest(
            "pc", "rev", "doubao", new() { ["doubao"] = new() { ["dictation"] = "Control+Option+D" } }));

        Assert.AreEqual("doubao", settings.ActiveEngine);
        Assert.AreEqual("Control+Option+D", settings.ShortcutOverrideFor("doubao", "dictation"));
    }

    [TestMethod]
    public void ValidatorRejectsUnknownEngineModeAndUnparsableKeys()
    {
        Assert.ThrowsExactly<ArgumentException>(() => MacVoiceSettingsValidator.ValidateVoice(Catalog(),
            new DesktopVoiceRequest("pc", "rev", "no-such-engine", null)));
        Assert.ThrowsExactly<ArgumentException>(() => MacVoiceSettingsValidator.ValidateVoice(Catalog(),
            new DesktopVoiceRequest("pc", "rev", "typeless", new() { ["typeless"] = new() { ["teleport"] = "Fn" } })));
        var error = Assert.ThrowsExactly<ArgumentException>(() => MacVoiceSettingsValidator.ValidateVoice(Catalog(),
            new DesktopVoiceRequest("pc", "rev", "typeless", new() { ["typeless"] = new() { ["dictation"] = "Control+D+E" } })));
        StringAssert.Contains(error.Message, "快捷键");
    }

    [TestMethod]
    public void CatalogWithSettingsSwitchesActiveEngineWithoutReloadingProfiles()
    {
        var catalog = Catalog();
        var switched = catalog.WithSettings(new MacVoiceEngineSettings { ActiveEngine = "wetype" });
        var fallback = catalog.WithSettings(new MacVoiceEngineSettings { ActiveEngine = "missing" });

        Assert.AreEqual("wetype", switched.Active.Id);
        Assert.AreEqual(MacVoiceEngineCatalog.DefaultEngineId, fallback.Active.Id);
        Assert.AreEqual(catalog.Profiles.Count, switched.Profiles.Count);
    }

    [TestMethod]
    public void AutoStartWritesAndRemovesLaunchAgentForThisExecutable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "phonedeck-launchagent-" + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(directory, "PhoneDeck & Receiver");
        var originalPlist = MacAutoStart.PlistPath;
        var originalExecutable = MacAutoStart.Executable;
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(executable, "");
            MacAutoStart.PlistPath = Path.Combine(directory, "LaunchAgents", MacAutoStart.Label + ".plist");
            MacAutoStart.Executable = executable;

            Assert.IsFalse(MacAutoStart.Enabled);
            MacAutoStart.Set(true);
            Assert.IsTrue(MacAutoStart.Enabled);
            var plist = File.ReadAllText(MacAutoStart.PlistPath);
            StringAssert.Contains(plist, "PhoneDeck &amp; Receiver", "路径必须做 XML 转义");
            StringAssert.Contains(plist, "<key>RunAtLoad</key><true/>");

            MacAutoStart.Set(false);
            Assert.IsFalse(MacAutoStart.Enabled);
            Assert.IsFalse(File.Exists(MacAutoStart.PlistPath));
        }
        finally
        {
            MacAutoStart.PlistPath = originalPlist;
            MacAutoStart.Executable = originalExecutable;
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void ConfigurationGateRejectsWritesDuringUseOrBusyState()
    {
        var gate = new ConfigurationGate();
        Assert.IsTrue(gate.EnterUse());
        Assert.IsFalse(gate.Apply(() => false, () => Assert.Fail("使用期间不能写入")));
        gate.ExitUse();
        Assert.IsFalse(gate.Apply(() => true, () => Assert.Fail("忙碌时不能写入")));
        var written = false;
        Assert.IsTrue(gate.Apply(() => false, () => written = true));
        Assert.IsTrue(written);
    }
}
