using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class VoiceEngineCatalogTests
{
    [TestMethod]
    public void BuiltinProfilesAreAllValid()
    {
        foreach (var builtin in VoiceEngineCatalog.BuiltinProfiles)
        {
            var profile = VoiceEngineProfileJson.Parse(
                builtin.Json, $"内置档案 {builtin.Id}");
            Assert.AreEqual(builtin.Id, profile.Id, $"内置档案 {builtin.Id} 的 id 不一致");
            Assert.IsTrue(profile.Modes.Length >= 1, $"{builtin.Id} 至少要有一个模式");
            CollectionAssert.AllItemsAreUnique(profile.ModeIds.ToArray());
        }
    }

    [TestMethod]
    public void ParseNormalizesIdsTriggersAndBlankKeys()
    {
        var profile = VoiceEngineProfileJson.Parse("""
            {
              "id": " MyEngine ",
              "displayName": "我的引擎",
              "processNames": [" MyEngine ", ""],
              "modes": [
                { "id": "Dictation", "keys": "  ", "trigger": "HOLD" },
                { "id": "ask", "label": "问答", "keys": "Ctrl+D" }
              ]
            }
            """, "测试档案");

        Assert.AreEqual("myengine", profile.Id);
        CollectionAssert.AreEqual(new[] { "MyEngine" }, profile.ProcessNames);
        Assert.AreEqual("dictation", profile.Modes[0].Id);
        Assert.AreEqual(EngineTriggers.Hold, profile.Modes[0].Trigger);
        Assert.IsNull(profile.Modes[0].Keys, "空白 keys 应规范化为 null（无默认，需手动配置）");
        Assert.AreEqual(EngineTriggers.Toggle, profile.Modes[1].Trigger, "trigger 缺省为 toggle");
        Assert.IsFalse(profile.VerifiesMicrophone);
    }

    [TestMethod]
    public void ParseRejectsInvalidProfiles()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            VoiceEngineProfileJson.Parse("""{ "displayName": "x", "processNames": ["x"], "modes": [{"id":"dictation"}] }""", "t"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            VoiceEngineProfileJson.Parse("""{ "id": "x", "displayName": "x", "modes": [{"id":"dictation"}] }""", "t"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            VoiceEngineProfileJson.Parse("""{ "id": "x", "displayName": "x", "processNames": ["x"], "modes": [] }""", "t"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            VoiceEngineProfileJson.Parse(
                """{ "id": "x", "displayName": "x", "processNames": ["x"], "modes": [{"id":"a"},{"id":"a"}] }""", "t"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            VoiceEngineProfileJson.Parse(
                """{ "id": "x", "displayName": "x", "processNames": ["x"], "modes": [{"id":"a","trigger":"doubleTap"}] }""", "t"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            VoiceEngineProfileJson.Parse("这不是 JSON", "t"));
    }

    [TestMethod]
    public void OnlyTypelessIsBuiltInAndExtensionFilesAreIgnored()
    {
        using var dataDirectory = new TempPhoneDeckDataDirectory();
        dataDirectory.WriteEngineFile("my-engine.json", """
            {
              "id": "myengine",
              "displayName": "我的引擎",
              "processNames": ["MyEngine"],
              "modes": [ { "id": "dictation", "keys": "F6" } ]
            }
            """);
        dataDirectory.WriteSettings("""
            {
              "activeEngine": "myengine"
            }
            """);

        var catalog = VoiceEngineCatalog.Load();

        Assert.AreEqual(VoiceEngineCatalog.DefaultEngineId, catalog.Active.Id, "只支持 Typeless");
        Assert.AreEqual(1, catalog.Profiles.Count);
        Assert.IsNull(catalog.Find("myengine"), "不再加载 voice-engines 扩展档案");
        Assert.IsNull(catalog.Find("doubao"));
        Assert.IsNull(catalog.Find("wetype"));
    }

    [TestMethod]
    public void LoadFallsBackToTypelessWhenActiveEngineMissing()
    {
        using var dataDirectory = new TempPhoneDeckDataDirectory();
        dataDirectory.WriteSettings("""
            {
              "activeEngine": "no-such-engine"
            }
            """);

        var catalog = VoiceEngineCatalog.Load();

        Assert.AreEqual(VoiceEngineCatalog.DefaultEngineId, catalog.Active.Id);
    }

    [TestMethod]
    public void ShortcutOverrideLookupIsCaseInsensitive()
    {
        var settings = new VoiceEngineSettings
        {
            ShortcutOverrides = new Dictionary<string, Dictionary<string, string>>
            {
                ["Typeless"] = new() { ["Dictation"] = "Ctrl+Shift+V" }
            }
        };

        Assert.AreEqual("Ctrl+Shift+V", settings.ShortcutOverrideFor("typeless", "dictation"));
        Assert.IsNull(settings.ShortcutOverrideFor("typeless", "translation"));
        Assert.IsNull(settings.ShortcutOverrideFor("other", "dictation"));
    }

    private sealed class TempPhoneDeckDataDirectory : IDisposable
    {
        private readonly string? previousDataDirectory;
        private readonly string directory;

        internal TempPhoneDeckDataDirectory()
        {
            previousDataDirectory = Environment.GetEnvironmentVariable(
                PhoneDeckDataDirectory.EnvironmentVariable);
            directory = Path.Combine(Path.GetTempPath(),
                "phonedeck-engines-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directory, "voice-engines"));
            Environment.SetEnvironmentVariable(
                PhoneDeckDataDirectory.EnvironmentVariable, directory);
        }

        internal void WriteEngineFile(string fileName, string content) =>
            File.WriteAllText(
                Path.Combine(directory, "voice-engines", fileName), content);

        internal void WriteSettings(string content) =>
            File.WriteAllText(
                Path.Combine(directory, "voice-engine-settings.json"), content);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(
                PhoneDeckDataDirectory.EnvironmentVariable, previousDataDirectory);
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响测试结果。
            }
        }
    }
}
