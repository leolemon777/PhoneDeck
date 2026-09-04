using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneDeck.MacReceiver;

internal interface IMacTypelessController
{
    MacTypelessConfig Configuration { get; }
    bool? IsCapturing();
    bool? WaitForCapturing(bool expected, int timeoutMilliseconds);
    bool ToggleOnce(string requestId, string mode);
    void Toggle(string mode);
}

internal sealed class MacTypelessController(
    MacReceiverSettings settings,
    MacKeyboardInput keyboard) : IMacTypelessController
{
    private const int MinimumToggleGapMilliseconds = 400;
    private readonly object syncRoot = new();
    private readonly Dictionary<string, long> requests = new(StringComparer.Ordinal);
    private long lastToggleAt;

    public MacTypelessConfig Configuration => MacTypelessConfiguration.Load(settings);

    public bool? IsCapturing() => MacTypelessStateProbe.IsCapturing();

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

    public bool ToggleOnce(string requestId, string mode)
    {
        lock (syncRoot)
        {
            var now = Environment.TickCount64;
            foreach (var expired in requests
                         .Where(pair => now - pair.Value > 30_000)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                requests.Remove(expired);
            }
            if (requests.ContainsKey(requestId))
            {
                return true;
            }
            ToggleLocked(mode);
            requests[requestId] = Environment.TickCount64;
            return false;
        }
    }

    public void Toggle(string mode)
    {
        lock (syncRoot)
        {
            ToggleLocked(mode);
        }
    }

    private void ToggleLocked(string mode)
    {
        var binding = Configuration.BindingFor(mode)
            ?? throw new InvalidOperationException("Typeless 未配置该模式的快捷键");
        var remaining = MinimumToggleGapMilliseconds
            - (Environment.TickCount64 - lastToggleAt);
        if (lastToggleAt > 0 && remaining > 0)
        {
            Thread.Sleep((int)remaining);
        }
        keyboard.SendTypelessShortcut(binding);
        lastToggleAt = Environment.TickCount64;
    }
}

/// <summary>Reads Typeless's real HAL input state through its AudioHardwareProcess.</summary>
internal static class MacTypelessStateProbe
{
    private const string CoreAudio =
        "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const uint SystemObject = 1;

    internal static bool? IsCapturing()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
        {
            return null;
        }
        try
        {
            Process? process = null;
            foreach (var candidate in Process.GetProcesses())
            {
                try
                {
                    if (process is null && candidate.ProcessName.Contains(
                            "Typeless", StringComparison.OrdinalIgnoreCase))
                    {
                        process = candidate;
                    }
                    else
                    {
                        candidate.Dispose();
                    }
                }
                catch
                {
                    candidate.Dispose();
                }
            }
            if (process is null)
            {
                return false;
            }
            using (process)
            {
                return ReadRunningInput(process.Id);
            }
        }
        catch
        {
            return null;
        }
    }

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
