internal interface IPhoneAudioSessionController
{
    bool IsSessionActive(string sessionId);
    bool WaitForSessionActive(string sessionId, int timeoutMilliseconds);
    bool StopSession(string sessionId);

    /// <summary>语音引擎确认开始采集后放行 pre-roll，按原顺序快速送入音频桥。</summary>
    void BeginPlayback(string sessionId);

    /// <summary>等待该会话的音频流（含尾部排空）真正结束。</summary>
    bool WaitForSessionEnd(string sessionId, int timeoutMilliseconds);
}

/// <summary>语音引擎触发键的底层发送通道。抽象出来是为了让
/// WindowsVoiceEngineController 的 toggle/hold 语义可以用假通道做单元测试。</summary>
internal interface IEngineKeyDispatcher
{
    /// <summary>发送一次“按下 55ms 再松开”的完整触发键；requestId 去重，
    /// 返回 true 表示重复请求（不会再次发送）。</summary>
    bool ToggleOnce(string? requestId, ushort[] keys);

    /// <summary>不去重的触发键（服务端内部复位/重试用）。</summary>
    void Toggle(ushort[] keys);

    /// <summary>按下并保持（hold 引擎开始）；requestId 去重，
    /// 返回 true 表示重复请求（不重复按下）。</summary>
    bool HoldDownOnce(string? requestId, ushort[] keys);

    /// <summary>释放 HoldDownOnce 按住的键；未按住时为安全空操作。</summary>
    void HoldUp(ushort[] keys);
}

/// <summary>引擎绑定信息来源：把静态的 VoiceEngines 门面包一层，
/// 让 WindowsVoiceEngineController 的 toggle/hold 语义可以脱离真实配置做单元测试。</summary>
internal interface IEngineBindingSource
{
    string DisplayName { get; }

    IReadOnlyCollection<string> Modes { get; }

    bool? CanVerifyVirtualCable { get; }

    bool UsesVirtualCable { get; }

    bool IsModeConfigured(string mode);

    string TriggerFor(string mode);

    ushort[] ResolveKeysOrThrow(string mode);
}

internal sealed class VoiceEngineBindingSource : IEngineBindingSource
{
    internal static readonly VoiceEngineBindingSource Instance = new();

    public string DisplayName => VoiceEngines.ActiveDisplayName;

    public IReadOnlyCollection<string> Modes => VoiceEngines.Active.ModeIds;

    public bool? CanVerifyVirtualCable =>
        VoiceEngines.Active.VerifiesMicrophone ? true : null;

    public bool UsesVirtualCable => VoiceEngines.UsesVirtualCable ?? false;

    public bool IsModeConfigured(string mode) => VoiceEngines.IsModeConfigured(mode);

    public string TriggerFor(string mode) => VoiceEngines.TriggerFor(mode);

    public ushort[] ResolveKeysOrThrow(string mode) => VoiceEngines.ResolveKeysOrThrow(mode);
}

/// <summary>语音引擎受管听写控制器：toggle 引擎按一下切换键，
/// hold 引擎开始时按下保持、结束时释放。实现必须保证 hold 按键
/// 在任何异常路径（断流、退出清理）都被释放。</summary>
internal interface IVoiceEngineController : IDisposable
{
    /// <summary>引擎显示名（用于错误消息与日志）。</summary>
    string EngineDisplayName { get; }

    /// <summary>引擎支持的全部模式 id。</summary>
    IReadOnlyCollection<string> Modes { get; }

    /// <summary>null 表示该引擎无可读配置、无法校验麦克风（不阻断启动，仅提示）。</summary>
    bool? CanVerifyVirtualCable { get; }

    /// <summary>仅当 CanVerifyVirtualCable 为 true 时有意义。</summary>
    bool UsesVirtualCable { get; }

    bool? IsCapturing();

    bool? WaitForCapturing(bool expected, int timeoutMilliseconds);

    bool IsModeConfigured(string mode);

    /// <summary>开始触发：toggle 引擎按一下切换键；hold 引擎按下并保持。
    /// 返回 true 表示重复 requestId（已去重，不会再次发送）。</summary>
    bool BeginOnce(string requestId, string mode);

    /// <summary>结束触发：toggle 引擎再按一下切换键；hold 引擎释放按键。
    /// requestId 为 null 表示服务端内部复位（音频断流、退出清理），此时不去重。
    /// 返回值含义同 BeginOnce：true 表示重复 requestId。</summary>
    bool End(string mode, string? requestId);
}

/// <summary>把引擎触发键映射到 Windows SendInput 的真实发送通道。</summary>
internal sealed class KeyboardEngineKeyDispatcher : IEngineKeyDispatcher
{
    public bool ToggleOnce(string? requestId, ushort[] keys) =>
        KeyboardInput.EngineToggleOnce(requestId, keys);

    public void Toggle(ushort[] keys) => KeyboardInput.EngineToggle(keys);

    public bool HoldDownOnce(string? requestId, ushort[] keys) =>
        KeyboardInput.EngineHoldDownOnce(requestId, keys);

    public void HoldUp(ushort[] keys) => KeyboardInput.EngineHoldUp(keys);
}

internal sealed class WindowsVoiceEngineController : IVoiceEngineController
{
    private const int MinimumToggleGapMilliseconds = 400;
    private readonly IEngineKeyDispatcher keys;
    private readonly IEngineBindingSource bindings;
    private readonly object sync = new();
    private long lastTriggerMilliseconds;
    private ushort[]? heldKeys;

    public WindowsVoiceEngineController()
        : this(new KeyboardEngineKeyDispatcher(), VoiceEngineBindingSource.Instance)
    {
    }

    internal WindowsVoiceEngineController(
        IEngineKeyDispatcher keys,
        IEngineBindingSource bindings)
    {
        this.keys = keys;
        this.bindings = bindings;
    }

    public string EngineDisplayName => bindings.DisplayName;

    public IReadOnlyCollection<string> Modes => bindings.Modes;

    public bool? CanVerifyVirtualCable => bindings.CanVerifyVirtualCable;

    public bool UsesVirtualCable => bindings.UsesVirtualCable;

    public bool IsModeConfigured(string mode) => bindings.IsModeConfigured(mode);

    public bool? IsCapturing() => VoiceEngineStateProbe.IsCapturing(VoiceEngines.Active);

    public bool? WaitForCapturing(bool expected, int timeoutMilliseconds) =>
        VoiceEngineStateProbe.WaitForCapturing(VoiceEngines.Active, expected, timeoutMilliseconds);

    public bool BeginOnce(string requestId, string mode)
    {
        var resolved = bindings.ResolveKeysOrThrow(mode);
        lock (sync)
        {
            if (string.Equals(bindings.TriggerFor(mode), EngineTriggers.Hold,
                    StringComparison.Ordinal))
            {
                WaitForToggleGap();
                var duplicate = keys.HoldDownOnce(requestId, resolved);
                if (!duplicate)
                {
                    heldKeys = resolved;
                    lastTriggerMilliseconds = Environment.TickCount64;
                }
                return duplicate;
            }
            WaitForToggleGap();
            var toggleDuplicate = keys.ToggleOnce(requestId, resolved);
            if (!toggleDuplicate)
            {
                lastTriggerMilliseconds = Environment.TickCount64;
            }
            return toggleDuplicate;
        }
    }

    public bool End(string mode, string? requestId)
    {
        var resolved = bindings.ResolveKeysOrThrow(mode);
        var isHold = string.Equals(bindings.TriggerFor(mode), EngineTriggers.Hold,
            StringComparison.Ordinal);
        lock (sync)
        {
            // hold 引擎：释放按住的键（以实际按下的键为准）。释放未按下的键
            // 是安全空操作，因此重复 End 幂等；未按住时退化为释放解析出的键。
            if (isHold || heldKeys is not null)
            {
                var held = heldKeys ?? resolved;
                heldKeys = null;
                keys.HoldUp(held);
                lastTriggerMilliseconds = Environment.TickCount64;
                return false;
            }
            if (requestId is null)
            {
                WaitForToggleGap();
                keys.Toggle(resolved);
                lastTriggerMilliseconds = Environment.TickCount64;
                return false;
            }
            WaitForToggleGap();
            var duplicate = keys.ToggleOnce(requestId, resolved);
            if (!duplicate)
            {
                lastTriggerMilliseconds = Environment.TickCount64;
            }
            return duplicate;
        }
    }

    /// <summary>退出清理：hold 引擎仍有按键按住时必须释放，避免悬挂修饰键。</summary>
    public void Dispose()
    {
        lock (sync)
        {
            if (heldKeys is null)
            {
                return;
            }
            var held = heldKeys;
            heldKeys = null;
            try
            {
                keys.HoldUp(held);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"释放引擎按住键失败：{exception.Message}");
            }
        }
    }

    private void WaitForToggleGap()
    {
        if (lastTriggerMilliseconds <= 0)
        {
            return;
        }
        var remaining = MinimumToggleGapMilliseconds
            - (Environment.TickCount64 - lastTriggerMilliseconds);
        if (remaining > 0)
        {
            Thread.Sleep((int)remaining);
        }
    }
}
