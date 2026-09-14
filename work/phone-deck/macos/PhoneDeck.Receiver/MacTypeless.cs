using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneDeck.MacReceiver;

/// <summary>语音引擎受管听写控制器：toggle 引擎按一下切换键，hold 引擎
/// 开始时按下保持、结束时释放。实现必须保证 hold 按键在任何异常路径都被释放。</summary>
internal interface IMacVoiceEngineController : IDisposable
{
    string EngineDisplayName { get; }

    IReadOnlyCollection<string> Modes { get; }

    /// <summary>null 表示该引擎无可读配置、无法校验麦克风（不阻断启动，仅提示）。</summary>
    bool? CanVerifyVirtualCable { get; }

    bool UsesVirtualCable { get; }

    bool? IsCapturing();

    bool? WaitForCapturing(bool expected, int timeoutMilliseconds);

    bool IsModeConfigured(string mode);

    /// <summary>开始触发：toggle 按一下切换键；hold 按下并保持。
    /// 返回 true 表示重复 requestId（已去重，不会再次发送）。</summary>
    bool BeginOnce(string requestId, string mode);

    /// <summary>结束触发：toggle 再按一下；hold 释放按键（幂等，重复 keyup 安全）。
    /// requestId 为 null 表示服务端内部复位，此时不去重。</summary>
    bool End(string mode, string? requestId);
}

internal sealed class MacVoiceEngineController(
    MacKeyboardInput keyboard) : IMacVoiceEngineController
{
    private const int MinimumToggleGapMilliseconds = 400;
    private readonly object syncRoot = new();
    private readonly Dictionary<string, long> requests = new(StringComparer.Ordinal);
    private long lastToggleAt;
    private MacChordHold? heldChord;

    public string EngineDisplayName => MacVoiceEngines.ActiveDisplayName;

    public IReadOnlyCollection<string> Modes => MacVoiceEngines.Active.ModeIds;

    public bool? CanVerifyVirtualCable =>
        MacVoiceEngines.Active.VerifiesMicrophone ? true : null;

    public bool UsesVirtualCable => MacVoiceEngines.UsesVirtualCable ?? false;

    public bool? IsCapturing() => MacVoiceEngineStateProbe.IsCapturing(MacVoiceEngines.Active);

    public bool? WaitForCapturing(bool expected, int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (true)
        {
            var value = IsCapturing();
            if (value is null)
            {
                return null;
            }
            if (value == expected)
            {
                return true;
            }
            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }
            Thread.Sleep(50);
        }
    }

    public bool IsModeConfigured(string mode) => MacVoiceEngines.IsModeConfigured(mode);

    public bool BeginOnce(string requestId, string mode)
    {
        var keys = ResolveKeysOrThrow(mode);
        lock (syncRoot)
        {
            if (string.Equals(MacVoiceEngines.TriggerFor(mode), MacEngineTriggers.Hold,
                    StringComparison.Ordinal))
            {
                WaitForToggleGap();
                var duplicate = false;
                PruneExpiredRequests();
                if (requests.ContainsKey(requestId))
                {
                    duplicate = true;
                }
                else
                {
                    heldChord = keyboard.EngineHoldDown(keys);
                    requests[requestId] = Environment.TickCount64;
                    lastToggleAt = Environment.TickCount64;
                }
                return duplicate;
            }
            WaitForToggleGap();
            var toggleDuplicate = false;
            PruneExpiredRequests();
            if (requests.ContainsKey(requestId))
            {
                toggleDuplicate = true;
            }
            else
            {
                keyboard.SendEngineChord(keys);
                requests[requestId] = Environment.TickCount64;
                lastToggleAt = Environment.TickCount64;
            }
            return toggleDuplicate;
        }
    }

    public bool End(string mode, string? requestId)
    {
        var keys = ResolveKeysOrThrow(mode);
        var isHold = string.Equals(MacVoiceEngines.TriggerFor(mode), MacEngineTriggers.Hold,
            StringComparison.Ordinal);
        lock (syncRoot)
        {
            // hold 引擎：释放按住的键（以实际按下的键为准）；释放未按住的键
            // 是安全空操作，因此重复 End 幂等。
            if (isHold || heldChord is not null)
            {
                var held = heldChord ?? keyboard.EngineHoldDown(keys);
                heldChord = null;
                keyboard.EngineHoldUp(held);
                lastToggleAt = Environment.TickCount64;
                return false;
            }
            WaitForToggleGap();
            if (requestId is null)
            {
                keyboard.SendEngineChord(keys);
                lastToggleAt = Environment.TickCount64;
                return false;
            }
            var duplicate = false;
            PruneExpiredRequests();
            if (requests.ContainsKey(requestId))
            {
                duplicate = true;
            }
            else
            {
                keyboard.SendEngineChord(keys);
                requests[requestId] = Environment.TickCount64;
                lastToggleAt = Environment.TickCount64;
            }
            return duplicate;
        }
    }

    /// <summary>退出清理：hold 引擎仍有按键按住时必须释放。</summary>
    public void Dispose()
    {
        lock (syncRoot)
        {
            if (heldChord is null)
            {
                return;
            }
            var held = heldChord;
            heldChord = null;
            try
            {
                keyboard.EngineHoldUp(held);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"释放引擎按住键失败：{exception.Message}");
            }
        }
    }

    private static MacKey[] ResolveKeysOrThrow(string mode) =>
        MacVoiceEngines.ResolveBinding(mode) is { } binding
            ? MacKeyboardInput.ParseEngineBinding(binding)
            : throw new InvalidOperationException(
                $"{MacVoiceEngines.ActiveDisplayName} 未配置「{MacVoiceEngines.LabelOf(mode)}」模式的快捷键");

    private void PruneExpiredRequests()
    {
        var now = Environment.TickCount64;
        foreach (var expired in requests
                     .Where(pair => now - pair.Value > 30_000)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            requests.Remove(expired);
        }
    }

    private void WaitForToggleGap()
    {
        var remaining = MinimumToggleGapMilliseconds
            - (Environment.TickCount64 - lastToggleAt);
        if (lastToggleAt > 0 && remaining > 0)
        {
            Thread.Sleep((int)remaining);
        }
    }
}

/// <summary>按引擎档案的进程名列表读取 Typeless/其他引擎的真实 HAL 输入状态。</summary>
internal static class MacVoiceEngineStateProbe
{
    private const string CoreAudio =
        "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const uint SystemObject = 1;

    internal static bool? IsCapturing(MacVoiceEngineProfile profile)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
        {
            return null;
        }
        try
        {
            var states = new List<bool?>();
            foreach (var candidate in Process.GetProcesses())
            {
                try
                {
                    if (MatchesProcess(profile, candidate.ProcessName))
                    {
                        states.Add(ReadRunningInput(candidate.Id));
                    }
                }
                catch
                {
                    states.Add(null);
                }
                finally
                {
                    candidate.Dispose();
                }
            }
            return CombineCaptureStates(states);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Electron 类语音引擎通常由独立音频服务进程持有输入流。
    /// 任一匹配进程正在采集即为 true；没有匹配进程时表示引擎未运行。</summary>
    internal static bool? CombineCaptureStates(IEnumerable<bool?> states)
    {
        var found = false;
        var unreadable = false;
        foreach (var state in states)
        {
            found = true;
            if (state is true)
            {
                return true;
            }
            unreadable |= state is null;
        }
        return !found ? false : unreadable ? null : false;
    }

    private static bool MatchesProcess(MacVoiceEngineProfile profile, string processName) =>
        profile.ProcessNames.Any(name =>
            processName.Equals(name, StringComparison.OrdinalIgnoreCase)
            || processName.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static bool? ReadRunningInput(int pid)
    {
        var translate = new AudioObjectPropertyAddress(
            FourCC("id2p"), FourCC("glob"), 0);
        var qualifier = Marshal.AllocHGlobal(sizeof(int));
        var output = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(qualifier, pid);
            uint size = sizeof(uint);
            var status = AudioObjectGetPropertyData(
                SystemObject, ref translate, sizeof(int), qualifier, ref size, output);
            if (status != 0)
            {
                return null;
            }
            var processObject = unchecked((uint)Marshal.ReadInt32(output));
            if (processObject == 0)
            {
                return false;
            }
            var runningInput = new AudioObjectPropertyAddress(
                FourCC("piri"), FourCC("glob"), 0);
            size = sizeof(uint);
            status = AudioObjectGetPropertyData(
                processObject, ref runningInput, 0, IntPtr.Zero, ref size, output);
            return status == 0 ? Marshal.ReadInt32(output) != 0 : null;
        }
        finally
        {
            Marshal.FreeHGlobal(output);
            Marshal.FreeHGlobal(qualifier);
        }
    }

    private static uint FourCC(string value) =>
        ((uint)value[0] << 24) | ((uint)value[1] << 16)
        | ((uint)value[2] << 8) | value[3];

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioObjectPropertyAddress(
        uint selector, uint scope, uint element)
    {
        internal uint Selector = selector;
        internal uint Scope = scope;
        internal uint Element = element;
    }

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref AudioObjectPropertyAddress address,
        uint qualifierDataSize, IntPtr qualifierData, ref uint dataSize, IntPtr data);
}
