using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class TargetEnvelopeValidatorTests
{
    private const string ComputerId = "461422b9-cf19-4bb0-afde-b0bf3818de4f";

    [TestMethod]
    public void V2EnvelopeAcceptsMatchingComputer()
    {
        TargetEnvelopeValidator.Validate(
            2,
            "request-1",
            Guid.NewGuid().ToString(),
            ComputerId.ToUpperInvariant(),
            ComputerId);
    }

    [TestMethod]
    public void V2EnvelopeRejectsMissingTarget()
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            TargetEnvelopeValidator.Validate(
                2,
                "request-1",
                Guid.NewGuid().ToString(),
                null,
                ComputerId));

        StringAssert.Contains(exception.Message, "targetComputerId");
    }

    [TestMethod]
    public void V2EnvelopeRejectsAnotherComputer()
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            TargetEnvelopeValidator.Validate(
                2,
                "request-1",
                Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString(),
                ComputerId));

        StringAssert.Contains(exception.Message, "不是当前电脑");
    }

    [TestMethod]
    public void LegacyUsbEnvelopeRemainsCompatible()
    {
        TargetEnvelopeValidator.Validate(
            null,
            "legacy-request",
            Guid.NewGuid().ToString(),
            null,
            ComputerId);
    }

    [TestMethod]
    public void FutureProtocolIsRejected()
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            TargetEnvelopeValidator.ValidateProtocolAndTarget(
                3,
                ComputerId,
                ComputerId));

        StringAssert.Contains(exception.Message, "protocolVersion");
    }
}
