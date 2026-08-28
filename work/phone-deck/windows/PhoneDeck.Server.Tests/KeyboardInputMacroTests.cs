using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class KeyboardInputMacroTests
{
    [TestMethod]
    public void MacroAcceptsTextAndKeyChordStepsWithDelays()
    {
        var steps = new[]
        {
            new MacroStep("keyChord", new[] { "CTRL", "BACKTICK" }, 45, null, false, 0),
            new MacroStep("text", null, null, "/compact", true, 300)
        };

        KeyboardInput.ValidateMacroSteps(steps);
    }

    [TestMethod]
    public void MacroRejectsTooManySteps()
    {
        var steps = new MacroStep[9];
        for (var index = 0; index < steps.Length; index++)
        {
            steps[index] = new MacroStep("text", null, null, "x", false, 0);
        }

        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            KeyboardInput.ValidateMacroSteps(steps));

        StringAssert.Contains(exception.Message, "1–8");
    }

    [TestMethod]
    public void MacroRejectsOutOfRangeDelay()
    {
        var steps = new[]
        {
            new MacroStep("text", null, null, "x", false, 2001)
        };

        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            KeyboardInput.ValidateMacroSteps(steps));

        StringAssert.Contains(exception.Message, "0–2000");
    }

    [TestMethod]
    public void MacroRejectsUnknownStepTypeAndEmptyText()
    {
        var unknown = new[]
        {
            new MacroStep("shell", null, null, null, false, 0)
        };
        Assert.ThrowsExactly<ArgumentException>(() =>
            KeyboardInput.ValidateMacroSteps(unknown));

        var emptyText = new[]
        {
            new MacroStep("text", null, null, "", false, 0)
        };
        Assert.ThrowsExactly<ArgumentException>(() =>
            KeyboardInput.ValidateMacroSteps(emptyText));
    }

    [TestMethod]
    public void MacroRejectsInvalidKeyChordInsideStep()
    {
        var steps = new[]
        {
            new MacroStep("keyChord", new[] { "CTRL", "SHIFT" }, 45, null, false, 0)
        };

        // 仅修饰键不构成合法组合键，宏步骤必须沿用同一白名单。
        Assert.ThrowsExactly<ArgumentException>(() =>
            KeyboardInput.ValidateMacroSteps(steps));
    }
}
