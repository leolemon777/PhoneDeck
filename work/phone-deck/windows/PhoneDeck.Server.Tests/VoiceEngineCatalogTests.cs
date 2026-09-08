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
    public void LoadMergesExtensionDirectoryAndSelectsActiveEngine()
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
        // 覆盖内置 doubao 的默认快捷键。
        dataDirectory.WriteEngineFile("doubao.json", """
            {
              "id": "doubao",
              "displayName": "豆包（社区修正）",
              "processNames": ["Doubao"],
              "modes": [ { "id": "dictation", "keys": "Ctrl+Shift+D" } ]
            }
            """);
        dataDirectory.WriteSettings("""
            {
              "activeEngine": "myengine"
            }
            """);

        var catalog = VoiceEngineCatalog.Load();

        Assert.AreEqual("myengine", catalog.Active.Id, "应按设置文件选择激活引擎");
        Assert.AreEqual("我的引擎", catalog.Active.DisplayName);
        Assert.IsNotNull(catalog.Find("typeless"), "内置档案必须保留");
        Assert.AreEqual("豆包（社区修正）", catalog.Find("doubao")?.DisplayName,
            "扩展档案按 id 覆盖内置档案");
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
    public void LoadSkipsBrokenExtensionFilesButKeepsOthers()
    {
        using var dataDirectory = new TempPhoneDeckDataDirectory();
        dataDirectory.WriteEngineFile("broken.json", "{ 这不是 JSON");
        dataDirectory.WriteEngineFile("good.json", """
            {
              "id": "goodengine",
              "displayName": "好的引擎",
              "processNames": ["GoodEngine"],
              "modes": [ { "id": "dictation", "keys": "F7" } ]
            }
            """);

        var catalog = VoiceEngineCatalog.Load();

        Assert.IsNull(catalog.Find("broken"));
        Assert.IsNotNull(catalog.Find("goodengine"));
    }

    [TestMethod]
    public void ShortcutOverrideLookupIsCaseInsensitive()
    {
        var settings = new VoiceEngineSettings
        {
            ShortcutOverrides = new Dictionary<string, Dictionary<string, string>>
            {
                ["WeType"] = new() { ["Dictation"] = "Ctrl+Shift+V" }
            }
        };

        Assert.AreEqual("Ctrl+Shift+V", settings.ShortcutOverrideFor("wetype", "dictation"));
        Assert.IsNull(settings.ShortcutOverrideFor("wetype", "translation"));
        Assert.IsNull(settings.ShortcutOverrideFor("doubao", "dictation"));
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
