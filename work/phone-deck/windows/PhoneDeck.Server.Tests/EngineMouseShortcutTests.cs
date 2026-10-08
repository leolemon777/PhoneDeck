using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class EngineMouseShortcutTests
{
    [TestMethod]
    [DataRow("MouseButton2", 0x20u, 0x40u, 0u)]
    [DataRow("MouseButton3", 0x80u, 0x100u, 1u)]
    [DataRow("MouseButton4", 0x80u, 0x100u, 2u)]
    public void MouseBindingProducesNativeButtonEvents(
        string binding, uint downFlags, uint upFlags, uint mouseData)
    {
        var keys = KeyboardInput.ParseBindingKeys(binding);
        Assert.IsNotNull(keys);
        Assert.AreEqual(1, keys.Length);
        var down = KeyboardInput.ShortcutInput(keys[0], keyUp: false);
        var up = KeyboardInput.ShortcutInput(keys[0], keyUp: true);
        AssertMouse(down, downFlags, mouseData);
        AssertMouse(up, upFlags, mouseData);
        Assert.AreEqual(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<KeyboardInput.Input>(),
            "SendInput 的 INPUT 必须保留包含 MOUSEINPUT 的完整联合布局");
    }

    [TestMethod]
    public void BindingDisplayNormalizesMouseTokensAndKeepsModifiersFirst()
    {
        CollectionAssert.AreEqual(new[] { "CTRL", "MouseButton2" },
            KeyboardInput.BindingKeyNames(" mouse_button-2 + RightControl "));
        var keys = Resolve("mouse_button-2+RightControl");
        CollectionAssert.AreEqual(new ushort[] { 0xA3, 0x04 }, keys);
    }

    [TestMethod]
    [DataRow("MouseButton0")]
    [DataRow("MouseButton1")]
    [DataRow("MouseButton5")]
    [DataRow("MouseButton999")]
    [DataRow("MouseWheel")]
    [DataRow("MouseMove")]
    [DataRow("MouseButton2+mouse_button-2")]
    [DataRow("Ctrl+Control+MouseButton2")]
    [DataRow("Ctrl+Shift+Alt+Win+MouseButton2")]
    public void UnsupportedOrUnboundedBindingsAreRejected(string binding) =>
        Assert.IsNull(KeyboardInput.ParseBindingKeys(binding));

    [TestMethod]
    public void MouseTokensStayOutsideOrdinaryKeyChordAndMacroProtocol()
    {
        Assert.ThrowsExactly<ArgumentException>(() => KeyboardInput.ExecuteKeyChordOnce(
            ["MouseButton2"], 45, null, null, out _));
        Assert.ThrowsExactly<ArgumentException>(() => KeyboardInput.ValidateMacroSteps(
            [new MacroStep("keyChord", ["MouseButton2"], 45, null, false, 0)]));
    }

    [TestMethod]
    public void ToggleDeduplicatesMouseChordAndReleasesInReverseOrder()
    {
        var events = new List<KeyboardInput.Input>();
        var dispatcher = new KeyboardEngineKeyDispatcher(events.Add);
        var keys = Resolve("MouseButton2+RightControl");
        var requestId = Guid.NewGuid().ToString();

        Assert.IsFalse(dispatcher.ToggleOnce(requestId, keys));
        Assert.IsTrue(dispatcher.ToggleOnce(requestId, keys));
        CollectionAssert.AreEqual(new[] { "key:A3:down", "mouse:20:0", "mouse:40:0", "key:A3:up" },
            events.Select(Describe).ToArray());

        dispatcher.Toggle(keys);
        Assert.AreEqual(8, events.Count, "内部复位仍可再次触发完整的鼠标组合");
    }

    [TestMethod]
    public void HoldMouseChordKeepsPressedBindingWhenSettingsBecomeInvalid()
    {
        var events = new List<KeyboardInput.Input>();
        var bindings = new MutableMouseBinding();
        using var controller = new WindowsVoiceEngineController(
            new KeyboardEngineKeyDispatcher(events.Add), bindings);
        var requestId = Guid.NewGuid().ToString();

        Assert.IsFalse(controller.BeginOnce(requestId, "dictation"));
        Assert.IsTrue(controller.BeginOnce(requestId, "dictation"));
        Assert.AreEqual(2, events.Count, "重复开始不能再次按下，也不能提前松开");
        bindings.FailResolution = true;
        Assert.IsFalse(controller.End("dictation", null));
        CollectionAssert.AreEqual(new[] { "key:A2:down", "mouse:20:0", "mouse:40:0", "key:A2:up" },
            events.Select(Describe).ToArray());
    }

    [TestMethod]
    public void ToggleDownFailureReleasesAttemptedMouseAndModifiers()
    {
        var events = new List<KeyboardInput.Input>();
        var dispatcher = new KeyboardEngineKeyDispatcher(input =>
        {
            events.Add(input);
            if (Describe(input) == "mouse:20:0") throw new InvalidOperationException("down failed");
        });

        var failure = Assert.ThrowsExactly<InvalidOperationException>(() =>
            dispatcher.ToggleOnce(Guid.NewGuid().ToString(), Resolve("Ctrl+MouseButton2+V")));
        Assert.AreEqual("down failed", failure.Message);
        CollectionAssert.AreEqual(new[] { "key:11:down", "mouse:20:0", "mouse:40:0", "key:11:up" },
            events.Select(Describe).ToArray(), "中途失败后不发送剩余键，并反向清理尝试按下的键");
    }

    [TestMethod]
    public void FailedHoldStartReleasesModifiersEvenWhenMouseCleanupFailsAndCanRetry()
    {
        var events = new List<KeyboardInput.Input>();
        var fail = true;
        var dispatcher = new KeyboardEngineKeyDispatcher(input =>
        {
            events.Add(input);
            if (fail && input.Type == 0) throw new InvalidOperationException("mouse blocked");
        });
        var requestId = Guid.NewGuid().ToString();
        var keys = Resolve("Ctrl+MouseButton2");
        Assert.ThrowsExactly<InvalidOperationException>(() => dispatcher.HoldDownOnce(requestId, keys));
        CollectionAssert.AreEqual(new[] { "key:11:down", "mouse:20:0", "mouse:40:0", "key:11:up" },
            events.Select(Describe).ToArray());

        fail = false;
        Assert.IsFalse(dispatcher.HoldDownOnce(requestId, keys), "失败的请求不得记为已执行");
        Assert.IsTrue(dispatcher.HoldDownOnce(requestId, keys));
        dispatcher.HoldUp(keys);
        Assert.AreEqual(8, events.Count);
    }

    [TestMethod]
    public void StopFailureStillReleasesModifierAndDisposeRetriesHeldMouse()
    {
        var events = new List<KeyboardInput.Input>();
        var failNextMouseUp = true;
        var dispatcher = new KeyboardEngineKeyDispatcher(input =>
        {
            events.Add(input);
            if (failNextMouseUp && Describe(input) == "mouse:40:0")
            {
                failNextMouseUp = false;
                throw new InvalidOperationException("up blocked");
            }
        });
        var controller = new WindowsVoiceEngineController(dispatcher, new MutableMouseBinding());
        try
        {
            controller.BeginOnce(Guid.NewGuid().ToString(), "dictation");
            Assert.ThrowsExactly<InvalidOperationException>(() => controller.End("dictation", null));
        }
        finally { controller.Dispose(); }
        CollectionAssert.AreEqual(new[]
            { "key:A2:down", "mouse:20:0", "mouse:40:0", "key:A2:up", "mouse:40:0", "key:A2:up" },
            events.Select(Describe).ToArray());
    }

    private static ushort[] Resolve(string binding) =>
        KeyboardInput.OrderModifiersFirst(KeyboardInput.ParseBindingKeys(binding)
            ?? throw new AssertFailedException($"绑定无法解析：{binding}"));

    private static void AssertMouse(KeyboardInput.Input input, uint flags, uint mouseData)
    {
        Assert.AreEqual(0u, input.Type, "鼠标绑定必须使用 INPUT_MOUSE");
        Assert.AreEqual(flags, input.Union.Mouse.Flags);
        Assert.AreEqual(mouseData, input.Union.Mouse.MouseData);
        Assert.AreEqual(0, input.Union.Mouse.X);
        Assert.AreEqual(0, input.Union.Mouse.Y);
        Assert.AreEqual(0u, input.Union.Mouse.Time);
        Assert.AreEqual(UIntPtr.Zero, input.Union.Mouse.ExtraInfo);
    }

    private static string Describe(KeyboardInput.Input input) => input.Type == 0
        ? $"mouse:{input.Union.Mouse.Flags:X}:{input.Union.Mouse.MouseData}"
        : $"key:{input.Union.Keyboard.VirtualKey:X2}:{(input.Union.Keyboard.Flags == 0 ? "down" : "up")}";

    private sealed class MutableMouseBinding : IEngineBindingSource
    {
        public bool FailResolution { get; set; }
        public string DisplayName => "测试引擎";
        public IReadOnlyCollection<string> Modes => ["dictation"];
        public bool? CanVerifyVirtualCable => null;
        public bool UsesVirtualCable => false;
        public bool IsModeConfigured(string mode) => true;
        public string TriggerFor(string mode) => EngineTriggers.Hold;
        public ushort[] ResolveKeysOrThrow(string mode) => FailResolution
            ? throw new ArgumentException("binding changed")
            : Resolve("LeftControl+MouseButton2");
    }
}
