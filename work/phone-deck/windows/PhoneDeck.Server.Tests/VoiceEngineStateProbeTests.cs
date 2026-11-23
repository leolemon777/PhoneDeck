using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class VoiceEngineStateProbeTests
{
    [TestMethod]
    public void ProbeWithinBudgetPreservesTrueAndFalseResults()
    {
        long now = 100;

        var active = VoiceEngineStateProbe.RunWithBudget(
            _ => true,
            () => now,
            budgetMilliseconds: 250);
        var idle = VoiceEngineStateProbe.RunWithBudget(
            _ => false,
            () => now,
            budgetMilliseconds: 250);

        Assert.IsTrue(active);
        Assert.IsFalse(idle);
    }

    [TestMethod]
    public void ProbeThatFinishesAfterBudgetIsUnknownRatherThanIdle()
    {
        long now = 100;

        var result = VoiceEngineStateProbe.RunWithBudget(
            _ =>
            {
                now += 251;
                return false;
            },
            () => now,
            budgetMilliseconds: 250);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ProbeFailureIsUnknownRatherThanIdle()
    {
        var result = VoiceEngineStateProbe.RunWithBudget(
            _ => throw new InvalidOperationException("simulated Core Audio failure"),
            () => 100,
            budgetMilliseconds: 250);

        Assert.IsNull(result);
    }
}
