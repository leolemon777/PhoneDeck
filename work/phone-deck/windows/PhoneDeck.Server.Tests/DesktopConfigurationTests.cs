using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
[DoNotParallelize]
public sealed class DesktopConfigurationTests
{
    [TestMethod]
    public void InputAndStreamsExcludeWritesUntilTheirLeaseEnds()
    {
        var gate = new ConfigurationGate();
        Assert.IsTrue(gate.EnterUse());
        Assert.IsFalse(gate.Apply(() => false, () => Assert.Fail("active stream was modified")));
        gate.ExitUse();
        Assert.IsFalse(gate.Apply(() => true, () => Assert.Fail("dictation was modified")));
        Assert.IsTrue(gate.Apply(() => false, () => Assert.IsFalse(gate.EnterUse())));
        Assert.IsTrue(gate.EnterUse()); gate.ExitUse();
    }

    [TestMethod]
    public void FailedWriteReleasesAdmissionAndDoesNotSwallowFailure()
    {
        var gate = new ConfigurationGate();
        Assert.ThrowsExactly<IOException>(() => gate.Apply(() => false, () => throw new IOException()));
        Assert.IsTrue(gate.EnterUse()); gate.ExitUse();
        Assert.IsTrue(gate.Apply(() => false, () => { }));
    }

    [TestMethod]
    public void TargetAndRevisionAreMandatory()
    {
        Assert.ThrowsExactly<ArgumentException>(() => DesktopConfiguration.CheckTarget(null, "pc-a"));
        Assert.ThrowsExactly<ArgumentException>(() => DesktopConfiguration.CheckTarget("pc-b", "pc-a"));
        DesktopConfiguration.CheckTarget("PC-A", "pc-a");
        var revision = DesktopConfiguration.Revision(new { engine = "typeless" });
        Assert.ThrowsExactly<InvalidOperationException>(() => DesktopConfiguration.CheckRevision(null, revision));
        Assert.ThrowsExactly<InvalidOperationException>(() => DesktopConfiguration.CheckRevision("stale", revision));
        DesktopConfiguration.CheckRevision(revision, revision);
    }

    [TestMethod]
    public void VoiceValidationRejectsUnknownModesAndKeysWithoutChangingCurrentSettings()
    {
        WithDirectory(directory =>
        {
            var catalog = VoiceEngineCatalog.Load();
            var revision = DesktopConfiguration.Revision(catalog.Settings);
            DesktopVoiceRequest Request(string mode, string keys) => new("pc", "revision", "wetype",
                new() { ["wetype"] = new() { [mode] = keys } });
            Assert.ThrowsExactly<ArgumentException>(() => DesktopConfiguration.ValidateVoice(catalog, Request("unknown", "Ctrl+V")));
            Assert.ThrowsExactly<ArgumentException>(() => DesktopConfiguration.ValidateVoice(catalog, Request("dictation", "RunAnyScript")));
            var request = Request("dictation", "Ctrl+Shift+V");
            var validated = DesktopConfiguration.ValidateVoice(catalog, request);
            request.ShortcutOverrides!["wetype"]["dictation"] = "F1";
            Assert.AreEqual("Ctrl+Shift+V", validated.ShortcutOverrides!["wetype"]["dictation"]);
            Assert.AreEqual("wetype", catalog.WithSettings(validated).Active.Id);
            Assert.AreEqual(revision, DesktopConfiguration.Revision(catalog.Settings));
        });
    }

    [TestMethod]
    public void VoiceValidationAcceptsMouseBindingsWithoutWritingSettings()
    {
        WithDirectory(directory =>
        {
            var catalog = VoiceEngineCatalog.Load();
            var settingsPath = VoiceEngineSettings.SettingsPath;
            var original = File.ReadAllText(settingsPath);
            var request = new DesktopVoiceRequest("pc", "revision", "typeless",
                new() { ["typeless"] = new()
                {
                    ["dictation"] = "MouseButton2", ["translation"] = "Ctrl+MouseButton3",
                    ["ask"] = "Shift+MouseButton4"
                } });
            var validated = DesktopConfiguration.ValidateVoice(catalog, request);
            Assert.AreEqual("MouseButton2", validated.ShortcutOverrideFor("typeless", "dictation"));
            Assert.AreEqual("Ctrl+MouseButton3", validated.ShortcutOverrideFor("typeless", "translation"));
            Assert.AreEqual("Shift+MouseButton4", validated.ShortcutOverrideFor("typeless", "ask"));
            Assert.AreEqual(original, File.ReadAllText(settingsPath),
                "校验快捷键不应写入本机配置");
            request.ShortcutOverrides!["typeless"]["dictation"] = "MouseButton5";
            Assert.ThrowsExactly<ArgumentException>(() => DesktopConfiguration.ValidateVoice(catalog, request));
        });
    }

    [TestMethod]
    public void AtomicPersistencePreservesOldFileWhenReplacementFails()
    {
        WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            DesktopConfiguration.WriteAtomic(path, new { value = "before" });
            var original = File.ReadAllText(path);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Exception? failure = null;
                try { DesktopConfiguration.WriteAtomic(path, new { value = "after" }); }
                catch (Exception e) { failure = e; }
                Assert.IsTrue(failure is IOException or UnauthorizedAccessException);
            }
            Assert.AreEqual(original, File.ReadAllText(path));
            Assert.AreEqual(0, Directory.GetFiles(directory, "*.tmp").Length);
            DesktopConfiguration.WriteAtomic(path, new { value = "after" });
            StringAssert.Contains(File.ReadAllText(path), "after");
        });
    }

    private static void WithDirectory(Action<string> action)
    {
        var previous = Environment.GetEnvironmentVariable(PhoneDeckDataDirectory.EnvironmentVariable);
        var directory = Path.Combine(Path.GetTempPath(), "PhoneDeck-settings-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable(PhoneDeckDataDirectory.EnvironmentVariable, directory);
        try { action(directory); }
        finally { Environment.SetEnvironmentVariable(PhoneDeckDataDirectory.EnvironmentVariable, previous); Directory.Delete(directory, true); }
    }
}
