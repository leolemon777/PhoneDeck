using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PhoneDeck.MacReceiver.Tests;

[TestClass]
public sealed class MacKeyboardInputTests
{
    [TestMethod]
    public void LegacyCtrlCMapsToCommandC()
    {
        var sink = new RecordingSink();
        var keyboard = new MacKeyboardInput(sink);

        var duplicate = keyboard.ExecuteKeyChordOnce(
            ["CTRL", "C"], 20, Guid.NewGuid().ToString(), out var description);

        Assert.IsFalse(duplicate);
        Assert.AreEqual("COMMAND + C", description);
        CollectionAssert.AreEqual(
            new[] { "55:down", "8:down", "8:up", "55:up" },
            sink.KeyEvents.Select(EventText).ToArray());
        Assert.AreEqual(MacModifierFlags.Command, sink.KeyEvents[1].Flags);
    }

    [TestMethod]
    public void LegacyScreenshotMapsToMacScreenshotSelection()
    {
        var keyboard = new MacKeyboardInput(new RecordingSink());

        var keys = keyboard.ParseKeyChord(["WIN", "SHIFT", "S"], out var description);

        Assert.AreEqual("COMMAND + SHIFT + 4", description);
        CollectionAssert.AreEqual(
            new ushort[] { 55, 56, 21 },
            keys.Select(key => key.KeyCode).ToArray());
    }

    [TestMethod]
    public void LegacyInputMethodMapsToControlSpace()
    {
        var keyboard = new MacKeyboardInput(new RecordingSink());

        var keys = keyboard.ParseKeyChord(["WIN", "SPACE"], out var description);

        Assert.AreEqual("CONTROL + SPACE", description);
        CollectionAssert.AreEqual(
            new ushort[] { 59, 49 },
            keys.Select(key => key.KeyCode).ToArray());
    }

    [TestMethod]
    public void DuplicateRequestIdDoesNotPostKeysAgain()
    {
        var sink = new RecordingSink();
        var keyboard = new MacKeyboardInput(sink);
        var requestId = Guid.NewGuid().ToString();

        Assert.IsFalse(keyboard.ExecuteKeyChordOnce(
            ["PRIMARY", "V"], 20, requestId, out _));
        Assert.IsTrue(keyboard.ExecuteKeyChordOnce(
            ["PRIMARY", "V"], 20, requestId, out _));

        Assert.HasCount(4, sink.KeyEvents);
    }

    [TestMethod]
    public void ModifierKeyUpClearsItsOwnFlag()
    {
        var sink = new RecordingSink();
        var keyboard = new MacKeyboardInput(sink);

        keyboard.SendEngineChord(MacKeyboardInput.ParseEngineBinding("Fn"));

        Assert.HasCount(2, sink.KeyEvents);
        Assert.AreEqual(MacModifierFlags.Function, sink.KeyEvents[0].Flags);
        Assert.AreEqual(MacModifierFlags.None, sink.KeyEvents[1].Flags);
    }

    [TestMethod]
    public void FailureStillReleasesEveryPossiblyPressedKey()
    {
        var sink = new RecordingSink { ThrowOnKeyDownCode = 8 };
        var keyboard = new MacKeyboardInput(sink);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            keyboard.ExecuteKeyChordOnce(
                ["CTRL", "C"], 20, Guid.NewGuid().ToString(), out _));

        CollectionAssert.Contains(
            sink.KeyEvents.Select(EventText).ToArray(),
            "8:up");
        CollectionAssert.Contains(
            sink.KeyEvents.Select(EventText).ToArray(),
            "55:up");
    }

    [TestMethod]
    public void TextActionUsesUnicodeSink()
    {
        var sink = new RecordingSink();
        var keyboard = new MacKeyboardInput(sink);

        var duplicate = keyboard.ExecuteFixedOnce(
            "text", "你好，Mac", Guid.NewGuid().ToString());

        Assert.IsFalse(duplicate);
        CollectionAssert.AreEqual(new[] { "你好，Mac" }, sink.TextEvents);
    }

    [TestMethod]
    public void UnsupportedMacMediaKeyIsRejected()
    {
        var keyboard = new MacKeyboardInput(new RecordingSink());

        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            keyboard.ParseKeyChord(["MEDIAPLAYPAUSE"], out _));

        StringAssert.Contains(exception.Message, "暂不支持媒体键");
    }

    private static string EventText(KeyEvent value) =>
        $"{value.Code}:{(value.Down ? "down" : "up")}";

    private sealed record KeyEvent(
        ushort Code,
        bool Down,
        MacModifierFlags Flags);

    private sealed class RecordingSink : IMacKeyboardSink
    {
        private bool hasThrown;

        public bool IsAccessibilityTrusted { get; set; } = true;
        public ushort? ThrowOnKeyDownCode { get; set; }
        public List<KeyEvent> KeyEvents { get; } = [];
        public List<string> TextEvents { get; } = [];

        public void PostKey(ushort keyCode, bool keyDown, MacModifierFlags flags)
        {
            KeyEvents.Add(new KeyEvent(keyCode, keyDown, flags));
            if (!hasThrown && keyDown && keyCode == ThrowOnKeyDownCode)
            {
                hasThrown = true;
                throw new InvalidOperationException("模拟 CGEvent 失败");
            }
        }

        public void PostText(string text) => TextEvents.Add(text);
    }
}
