using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PhoneDeck.MacReceiver.Tests;

/// <summary>所有测试使用临时数据目录，绝不读写本机真实的 ~/Library/Application Support/PhoneDeck。</summary>
[TestClass]
public static class TestDataDirectory
{
    private static string? directory;

    [AssemblyInitialize]
    public static void Initialize(TestContext _)
    {
        directory = Path.Combine(Path.GetTempPath(), "phonedeck-mac-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable(PhoneDeckDataDirectory.EnvironmentVariable, directory);
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
