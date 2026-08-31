using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AgentShortcutSettingsTests
{
    [TestMethod]
    public void TryValidate_AcceptsDefaultSettings()
    {
        Assert.IsTrue(AgentShortcutSettings.TryValidate(
            new AgentShortcutSettings(), out var error), error);
    }

    [TestMethod]
    public void TryValidate_RejectsMultilineCommand()
    {
        var settings = new AgentShortcutSettings
        {
            Buttons = new List<AgentShortcutDefinition>
            {
                new("agentPlan", "规划", "/plan\nextra", true, true),
                new("agentGoal", "目标", "/goal", true, true),
                new("agentCompact", "压缩上下文", "/compact", true, true),
                new("agentClear", "新会话", "/clear", true, true)
            }
        };

        Assert.IsFalse(AgentShortcutSettings.TryValidate(settings, out _));
    }
}
