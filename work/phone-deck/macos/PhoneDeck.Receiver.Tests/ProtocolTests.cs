using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PhoneDeck.MacReceiver.Tests;

[TestClass]
public sealed class ProtocolTests
{
    [TestMethod]
    public void V2RejectsWrongTarget()
    {
        var computerId = Guid.NewGuid().ToString();
        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            TargetEnvelopeValidator.Validate(
                2,
                Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString(),
                computerId));

        StringAssert.Contains(exception.Message, "请求目标不是当前电脑");
    }

    [TestMethod]
    public void InputProcessorAcceptsV2TextForThisMac()
    {
        var computerId = Guid.NewGuid().ToString();
        var sink = new TextSink();
        var processor = new InputCommandProcessor(new MacKeyboardInput(sink));
        var command = new InputCommand(
            2,
            "text",
            "/plan\n",
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            computerId,
            null,
            null,
            null);

        var result = processor.Execute(command, computerId);

        Assert.IsFalse(result.Duplicate);
        Assert.AreEqual("/plan", sink.Text);
        CollectionAssert.AreEqual(
            new[] { "36:down", "36:up" },
            sink.KeyEvents);
    }

    [TestMethod]
    public void DefaultDataDirectoryUsesMacApplicationSupport()
    {
        var path = PhoneDeckDataDirectory.Resolve(null, "/Users/tester");

        Assert.AreEqual(
            Path.Combine("/Users/tester", "Library", "Application Support", "PhoneDeck"),
            path);
    }

    private sealed class TextSink : IMacKeyboardSink
    {
        public bool IsAccessibilityTrusted => true;
        public string? Text { get; private set; }
        public List<string> KeyEvents { get; } = [];

        public void PostKey(ushort keyCode, bool keyDown, MacModifierFlags flags)
        {
            KeyEvents.Add($"{keyCode}:{(keyDown ? "down" : "up")}");
        }

        public void PostText(string text) => Text = text;
    }
}
