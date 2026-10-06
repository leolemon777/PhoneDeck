using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhoneDeck.Desktop;

namespace PhoneDeck.Desktop.Tests;

[TestClass]
public sealed class VoiceTextTests
{
    [TestMethod] public void NativeSegmentsBecomeOneCanonicalParagraph()
    {
        Assert.AreEqual("hello world 你好 世界。", WhisperEngine.NormalizeTranscript("  hello\r\n world\t你好\u0085\u2028 世界。\u2029\n"));
        Assert.AreEqual("姓名张三，编号123。😀", WhisperEngine.NormalizeTranscript("姓名张三，编号123。😀"));
    }
    [TestMethod] public void NativeOutputRejectsUnexpectedControlsAndOversizedText()
    {
        foreach (var value in new[] { "hello\0world", "hello\bworld", "\u001b[31mtext", new string('x', 4097) })
            Assert.ThrowsExactly<InvalidDataException>(() => WhisperEngine.NormalizeTranscript(value));
    }
    [TestMethod] public void AutomaticVoiceInsertionCannotSendControlCharacters()
    {
        foreach (var value in new[] { "a\rb", "a\nb", "a\tb", "a\0b", "a\bb", "a\u001bb", "a\u0085b", "a\u2028b", "a\u2029b" })
            Assert.ThrowsExactly<IOException>(() => PlatformInput.ValidateVoiceInsertionText(value));
        PlatformInput.ValidateVoiceInsertionText("中文与plain text 123，😀。");
    }
    [TestMethod] public void X11ModifiersBlockVoiceWhileLocksAndMouseButtonsAreAllowed()
    {
        foreach (var flags in new uint[] { 0, 2, 16, 18, 256, 512 }) Assert.IsFalse(PlatformInput.HasUnsafeX11Modifiers(flags));
        foreach (var flags in new uint[] { 1, 4, 8, 32, 64, 128, 260 }) Assert.IsTrue(PlatformInput.HasUnsafeX11Modifiers(flags));
    }
}
