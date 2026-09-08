using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class WindowsVoiceEngineControllerTests
{
    private const ushort VkControl = 0xA2;
    private const ushort VkVKey = 0x56;

    [TestMethod]
    public void HoldEngineBeginPressesOnceAndStopReleases()
    {
        var keys = new RecordingKeyDispatcher();
        using (var controller = new WindowsVoiceEngineController(
                   keys, new FakeBindingSource(EngineTriggers.Hold)))
        {
            Assert.IsFalse(controller.BeginOnce("start-1", "dictation"));
            CollectionAssert.AreEqual(
                new[] { "holdDown:0xA2+0x56" }, keys.Calls.ToArray());

            Assert.IsFalse(controller.End("dictation", "stop-1"));
        }
        CollectionAssert.AreEqual(
            new[] { "holdDown:0xA2+0x56", "holdUp:0xA2+0x56" }, keys.Calls.ToArray(),
            "hold 引擎结束必须释放按下的键，而不是再按一下");
    }

    [TestMethod]
    public void HoldEngineDuplicateStartDoesNotDoublePress()
    {
        var keys = new RecordingKeyDispatcher();
        using var controller = new WindowsVoiceEngineController(
            keys, new FakeBindingSource(EngineTriggers.Hold));

        Assert.IsFalse(controller.BeginOnce("start-1", "dictation"));
        Assert.IsTrue(controller.BeginOnce("start-1", "dictation"), "重复 requestId 应报告重复");

        Assert.IsFalse(controller.End("dictation", "stop-1"));
        CollectionAssert.AreEqual(
            new[] { "holdDown:0xA2+0x56", "holdUp:0xA2+0x56" }, keys.Calls.ToArray(),
            "重复开始不得导致二次按下");
    }

    [TestMethod]
    public void HoldEngineRepeatedEndIsIdempotent()
    {
        var keys = new RecordingKeyDispatcher();
        using var controller = new WindowsVoiceEngineController(
            keys, new FakeBindingSource(EngineTriggers.Hold));

        controller.BeginOnce("start-1", "dictation");
        controller.End("dictation", null);
        controller.End("dictation", null);

        CollectionAssert.AreEqual(
            new[] { "holdDown:0xA2+0x56", "holdUp:0xA2+0x56", "holdUp:0xA2+0x56" },
            keys.Calls.ToArray(),
            "hold 引擎重复 End 是幂等释放（重复 keyup 为安全空操作），绝不能落入 toggle 分支重新触发");
    }

    [TestMethod]
    public void HoldEngineDisposeWithoutEndReleasesHeldKeys()
    {
        var keys = new RecordingKeyDispatcher();
        var controller = new WindowsVoiceEngineController(
            keys, new FakeBindingSource(EngineTriggers.Hold));

        controller.BeginOnce("start-1", "dictation");
        controller.Dispose();

        CollectionAssert.AreEqual(
            new[] { "holdDown:0xA2+0x56", "holdUp:0xA2+0x56" }, keys.Calls.ToArray(),
            "退出清理必须释放按住的键，避免悬挂修饰键");
    }

    [TestMethod]
    public void ToggleEngineBeginAndEndUseToggleDispatcher()
    {
        var keys = new RecordingKeyDispatcher();
        using (var controller = new WindowsVoiceEngineController(
                   keys, new FakeBindingSource(EngineTriggers.Toggle)))
        {
            Assert.IsFalse(controller.BeginOnce("start-1", "dictation"));
            Assert.IsFalse(controller.End("dictation", "stop-1"));
            Assert.IsFalse(controller.End("dictation", null));
        }
        CollectionAssert.AreEqual(
            new[] { "toggleOnce:0xA2+0x56", "toggleOnce:0xA2+0x56", "toggle:0xA2+0x56" },
            keys.Calls.ToArray(),
            "toggle 引擎：带 requestId 用去重通道，内部复位用不去重通道");
    }

    [TestMethod]
    public void ToggleEngineDuplicateRequestIdIsDeduplicated()
    {
        var keys = new RecordingKeyDispatcher();
        using var controller = new WindowsVoiceEngineController(
            keys, new FakeBindingSource(EngineTriggers.Toggle));

        Assert.IsFalse(controller.BeginOnce("start-1", "dictation"));
        Assert.IsTrue(controller.BeginOnce("start-1", "dictation"));
        Assert.IsTrue(controller.End("dictation", "start-1"),
            "同一 requestId 的 End 也应去重（网络重试场景）");

        CollectionAssert.AreEqual(
            new[] { "toggleOnce:0xA2+0x56" }, keys.Calls.ToArray());
    }

    private sealed class FakeBindingSource : IEngineBindingSource
    {
        private readonly string trigger;

        public FakeBindingSource(string trigger)
        {
            this.trigger = trigger;
        }

        public string DisplayName => "测试引擎";

        public IReadOnlyCollection<string> Modes => new[] { "dictation" };

        public bool? CanVerifyVirtualCable => null;

        public bool UsesVirtualCable => false;

        public bool IsModeConfigured(string mode) => true;

        public string TriggerFor(string mode) => trigger;

        public ushort[] ResolveKeysOrThrow(string mode) => new[] { VkControl, VkVKey };
    }

    private sealed class RecordingKeyDispatcher : IEngineKeyDispatcher
    {
        private readonly HashSet<string> seenRequestIds = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = new();

        public bool ToggleOnce(string? requestId, ushort[] keys)
        {
            if (requestId is not null && !seenRequestIds.Add(requestId))
            {
                return true;
            }
            Calls.Add($"toggleOnce:{Describe(keys)}");
            return false;
        }

        public void Toggle(ushort[] keys) => Calls.Add($"toggle:{Describe(keys)}");

        public bool HoldDownOnce(string? requestId, ushort[] keys)
        {
            if (requestId is not null && !seenRequestIds.Add(requestId))
            {
                return true;
            }
            Calls.Add($"holdDown:{Describe(keys)}");
            return false;
        }

        public void HoldUp(ushort[] keys) => Calls.Add($"holdUp:{Describe(keys)}");

        private static string Describe(ushort[] keys) =>
            string.Join("+", keys.Select(key => $"0x{key:X2}"));
    }
}
