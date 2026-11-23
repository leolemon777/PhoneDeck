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

    /// <summary>仅释放已按住的键，不向 toggle 引擎发送启动/停止。</summary>
    void ReleaseHeldKeys() { }
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
    public void Dispose() => ReleaseHeldKeys();

    public void ReleaseHeldKeys()
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
    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const uint SystemObject = 1;
    private const int ProcessNameBufferSize = 256;

    // Core Audio 进程对象 ID 在进程退出前不变：缓存“是否属于当前引擎”，
    // 每次只读一遍音频进程列表，避免逐个枚举全部系统进程（数百个 proc_info 调用）。
    private static readonly object ProbeGate = new();
    private static readonly List<bool?> ProbeStates = new();
    private static readonly byte[] NameBuffer = new byte[ProcessNameBufferSize];
    private static MacVoiceEngineProfile? matchProfile;
    private static Dictionary<uint, bool> matchCache = new();
    private static Dictionary<uint, bool> nextMatchCache = new();
    private static uint[] processObjects = new uint[64];

    internal static bool? IsCapturing(MacVoiceEngineProfile profile)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
        {
            return null;
        }
        lock (ProbeGate)
        {
            try
            {
                var count = ReadProcessObjects();
                if (count >= 0)
                {
                    return CombineCaptureStates(ReadAudioProcessStates(profile, count));
                }
            }
            catch
            {
                // 读不到音频进程列表时退回逐进程枚举。
            }
        }
        return IsCapturingByProcessScan(profile);
    }

    private static List<bool?> ReadAudioProcessStates(MacVoiceEngineProfile profile, int count)
    {
        if (!ReferenceEquals(matchProfile, profile))
        {
            matchCache.Clear();
            matchProfile = profile;
        }
        nextMatchCache.Clear();
        ProbeStates.Clear();
        for (var index = 0; index < count; index++)
        {
            var processObject = processObjects[index];
            if (!matchCache.TryGetValue(processObject, out var matches)
                && !TryMatchProcessObject(profile, processObject, out matches))
            {
                // 进程刚退出、PID 不可读：不缓存，下次重试。
                continue;
            }
            nextMatchCache[processObject] = matches;
            if (matches)
            {
                ProbeStates.Add(ReadRunningInputOfObject(processObject));
            }
        }
        (matchCache, nextMatchCache) = (nextMatchCache, matchCache);
        return ProbeStates;
    }

    private static int ReadProcessObjects()
    {
        var address = new AudioObjectPropertyAddress(FourCC("prs#"), FourCC("glob"), 0);
        if (AudioObjectGetPropertyDataSize(SystemObject, ref address, 0, IntPtr.Zero, out var size) != 0)
        {
            return -1;
        }
        var count = (int)(size / sizeof(uint));
        if (count > processObjects.Length)
        {
            processObjects = new uint[Math.Max(count, processObjects.Length * 2)];
        }
        size = (uint)(processObjects.Length * sizeof(uint));
        if (AudioObjectGetPropertyData(SystemObject, ref address, 0, IntPtr.Zero, ref size, processObjects) != 0)
        {
            return -1;
        }
        return Math.Min((int)(size / sizeof(uint)), processObjects.Length);
    }

    private static bool TryMatchProcessObject(
        MacVoiceEngineProfile profile, uint processObject, out bool matches)
    {
        matches = false;
        var address = new AudioObjectPropertyAddress(FourCC("ppid"), FourCC("glob"), 0);
        uint size = sizeof(int);
        if (AudioObjectGetPropertyData(processObject, ref address, 0, IntPtr.Zero, ref size, out int pid) != 0
            || pid <= 0)
        {
            return false;
        }
        // 名称读不到的是其他用户（root/系统服务）的进程，语音引擎以当前用户运行，按不匹配缓存。
        var length = proc_name(pid, NameBuffer, ProcessNameBufferSize);
        matches = length > 0
            && MatchesProcess(profile, System.Text.Encoding.UTF8.GetString(NameBuffer, 0, length));
        return true;
    }

    private static bool? ReadRunningInputOfObject(uint processObject)
    {
        var address = new AudioObjectPropertyAddress(FourCC("piri"), FourCC("glob"), 0);
        uint size = sizeof(int);
        return AudioObjectGetPropertyData(processObject, ref address, 0, IntPtr.Zero, ref size, out int running) == 0
            ? running != 0
            : null;
    }

    private static bool? IsCapturingByProcessScan(MacVoiceEngineProfile profile)
    {
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

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref AudioObjectPropertyAddress address,
        uint qualifierDataSize, IntPtr qualifierData, ref uint dataSize, [Out] uint[] data);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref AudioObjectPropertyAddress address,
        uint qualifierDataSize, IntPtr qualifierData, ref uint dataSize, out int data);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyDataSize(
        uint objectId, ref AudioObjectPropertyAddress address,
        uint qualifierDataSize, IntPtr qualifierData, out uint dataSize);

    [DllImport(LibSystem)]
    private static extern int proc_name(int pid, [Out] byte[] buffer, uint bufferSize);
}
