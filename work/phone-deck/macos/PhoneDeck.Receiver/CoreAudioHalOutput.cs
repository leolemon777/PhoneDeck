using System.Runtime.InteropServices;

namespace PhoneDeck.MacReceiver;

internal interface IMacAudioOutput : IDisposable
{
    string DeviceName { get; }
    string DeviceUid { get; }
    void Write(ReadOnlySpan<byte> stereoPcm16);
}

internal interface IMacAudioOutputFactory
{
    (bool Available, string? DeviceName, string? DeviceUid, string? Error) Probe();
    IMacAudioOutput Create();
}

/// <summary>
/// AUHAL output bound directly to BlackHole by AudioDeviceID. It never changes
/// the user's default input or output device.
/// </summary>
internal sealed class CoreAudioHalOutputFactory(string? configuredDeviceUid)
    : IMacAudioOutputFactory
{
    public (bool Available, string? DeviceName, string? DeviceUid, string? Error) Probe()
    {
        try
        {
            var device = CoreAudioHalOutput.ResolveDevice(configuredDeviceUid);
            return (true, device.Name, device.Uid, null);
        }
        catch (Exception exception)
        {
            return (false, null, configuredDeviceUid, exception.Message);
        }
    }

    public IMacAudioOutput Create() => new CoreAudioHalOutput(configuredDeviceUid);
}

internal sealed class CoreAudioHalOutput : IMacAudioOutput
{
    private const string AudioToolbox =
        "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreAudio =
        "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint SystemObject = 1;
    private const uint ScopeGlobal = 0;
    private const uint ScopeInput = 1;
    private const uint PropertyCurrentDevice = 2000;
    private const uint PropertyStreamFormat = 8;
    private const uint PropertySetRenderCallback = 23;
    private const uint FormatFlagIsSignedInteger = 1 << 2;
    private const uint FormatFlagIsPacked = 1 << 3;

    private readonly MacAudioRingBuffer ring = new(48_000 * 4 * 2);
    private readonly RenderCallback renderCallback;
    private byte[] callbackScratch = new byte[32 * 1024];
    private IntPtr audioUnit;
    private bool disposed;

    internal CoreAudioHalOutput(string? configuredDeviceUid)
    {
        EnsureMacOS();
        var device = ResolveDevice(configuredDeviceUid);
        DeviceName = device.Name;
        DeviceUid = device.Uid;
        renderCallback = Render;

        var description = new AudioComponentDescription
        {
            ComponentType = FourCC("auou"),
            ComponentSubType = FourCC("ahal"),
            ComponentManufacturer = FourCC("appl")
        };
        var component = AudioComponentFindNext(IntPtr.Zero, ref description);
        if (component == IntPtr.Zero
            || AudioComponentInstanceNew(component, out audioUnit) != 0
            || audioUnit == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法创建 AUHAL 输出单元");
        }

        try
        {
            SetProperty(PropertyCurrentDevice, ScopeGlobal, 0, device.Id);
            var format = new AudioStreamBasicDescription
            {
                SampleRate = 48_000,
                FormatId = FourCC("lpcm"),
                FormatFlags = FormatFlagIsSignedInteger | FormatFlagIsPacked,
                BytesPerPacket = 4,
                FramesPerPacket = 1,
                BytesPerFrame = 4,
                ChannelsPerFrame = 2,
                BitsPerChannel = 16
            };
            SetProperty(PropertyStreamFormat, ScopeInput, 0, format);
            var callback = new AudioUnitRenderCallback
            {
                Callback = Marshal.GetFunctionPointerForDelegate(renderCallback),
                Context = IntPtr.Zero
            };
            SetProperty(PropertySetRenderCallback, ScopeInput, 0, callback);
            Check(AudioUnitInitialize(audioUnit), "初始化 AUHAL");
            Check(AudioOutputUnitStart(audioUnit), "启动 AUHAL");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string DeviceName { get; }
    public string DeviceUid { get; }

    public void Write(ReadOnlySpan<byte> stereoPcm16)
    {
        if (disposed)
        {
            return;
        }
        ring.Write(stereoPcm16);
    }

    private int Render(
        IntPtr context,
        IntPtr actionFlags,
        IntPtr timestamp,
        uint busNumber,
        uint numberFrames,
        IntPtr ioData)
    {
        try
        {
            var bufferCount = Marshal.ReadInt32(ioData);
            var bufferSize = Marshal.SizeOf<AudioBuffer>();
            var firstOffset = IntPtr.Size == 8 ? 8 : 4;
            for (var index = 0; index < bufferCount; index++)
            {
                var address = IntPtr.Add(ioData, firstOffset + index * bufferSize);
                var buffer = Marshal.PtrToStructure<AudioBuffer>(address);
                var requested = Math.Min(
                    checked((int)buffer.DataByteSize), checked((int)numberFrames * 4));
                if (buffer.Data == IntPtr.Zero || requested <= 0)
                {
                    continue;
                }
                if (callbackScratch.Length < requested)
                {
                    callbackScratch = new byte[requested];
                }
                ring.Read(callbackScratch.AsSpan(0, requested));
                Marshal.Copy(callbackScratch, 0, buffer.Data, requested);
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        ring.Clear();
        if (audioUnit != IntPtr.Zero)
        {
            AudioOutputUnitStop(audioUnit);
            AudioUnitUninitialize(audioUnit);
            AudioComponentInstanceDispose(audioUnit);
            audioUnit = IntPtr.Zero;
        }
        GC.KeepAlive(renderCallback);
    }

    internal static AudioDevice ResolveDevice(string? configuredDeviceUid)
    {
        EnsureMacOS();
        var address = new AudioObjectPropertyAddress(
            FourCC("dev#"), FourCC("glob"), 0);
        uint size = 0;
        Check(AudioObjectGetPropertyDataSize(
            SystemObject, ref address, 0, IntPtr.Zero, ref size),
            "枚举 Core Audio 设备");
        if (size == 0)
        {
            throw new InvalidOperationException("没有可用的 Core Audio 设备");
        }
        var memory = Marshal.AllocHGlobal((int)size);
        try
        {
            Check(AudioObjectGetPropertyData(
                SystemObject, ref address, 0, IntPtr.Zero, ref size, memory),
                "读取 Core Audio 设备");
            var count = (int)size / sizeof(uint);
            var devices = new List<AudioDevice>(count);
            for (var index = 0; index < count; index++)
            {
                var id = unchecked((uint)Marshal.ReadInt32(memory, index * sizeof(uint)));
                var name = ReadStringProperty(id, FourCC("lnam"));
                var uid = ReadStringProperty(id, FourCC("uid "));
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(uid))
                {
                    devices.Add(new AudioDevice(id, name, uid));
                }
            }
            AudioDevice? match = null;
            if (!string.IsNullOrWhiteSpace(configuredDeviceUid))
            {
                match = devices.FirstOrDefault(device => string.Equals(
                    device.Uid, configuredDeviceUid.Trim(), StringComparison.Ordinal));
                if (match is null)
                {
                    throw new InvalidOperationException(
                        $"未找到 audioDeviceUid 指定的设备：{configuredDeviceUid}");
                }
            }
            match ??= devices.FirstOrDefault(device =>
                device.Name.Contains("BlackHole 2ch", StringComparison.OrdinalIgnoreCase));
            return match ?? throw new InvalidOperationException(
                "未检测到 BlackHole 2ch；请安装并设为 48 kHz");
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static string? ReadStringProperty(uint objectId, uint selector)
    {
        var address = new AudioObjectPropertyAddress(selector, FourCC("glob"), 0);
        uint size = (uint)IntPtr.Size;
        var memory = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(memory, IntPtr.Zero);
            if (AudioObjectGetPropertyData(
                    objectId, ref address, 0, IntPtr.Zero, ref size, memory) != 0)
            {
                return null;
            }
            var value = Marshal.ReadIntPtr(memory);
            if (value == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                var length = CFStringGetLength(value);
                var maximum = CFStringGetMaximumSizeForEncoding(length, 0x08000100) + 1;
                var buffer = Marshal.AllocHGlobal((int)maximum);
                try
                {
                    return CFStringGetCString(value, buffer, maximum, 0x08000100)
                        ? Marshal.PtrToStringUTF8(buffer)
                        : null;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CFRelease(value);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private void SetProperty<T>(uint property, uint scope, uint element, T value)
        where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, memory, false);
            Check(AudioUnitSetProperty(
                audioUnit, property, scope, element, memory, (uint)size),
                $"设置 AUHAL 属性 {property}");
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static uint FourCC(string value) =>
        ((uint)value[0] << 24) | ((uint)value[1] << 16)
        | ((uint)value[2] << 8) | value[3];

    private static void Check(int status, string operation)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"{operation}失败（OSStatus {status}）");
        }
    }

    private static void EnsureMacOS()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
        {
            throw new PlatformNotSupportedException("手机音频桥要求 macOS 14.2 或更高版本");
        }
    }

    internal sealed record AudioDevice(uint Id, string Name, string Uid);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioComponentDescription
    {
        internal uint ComponentType;
        internal uint ComponentSubType;
        internal uint ComponentManufacturer;
        internal uint ComponentFlags;
        internal uint ComponentFlagsMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        internal double SampleRate;
        internal uint FormatId;
        internal uint FormatFlags;
        internal uint BytesPerPacket;
        internal uint FramesPerPacket;
        internal uint BytesPerFrame;
        internal uint ChannelsPerFrame;
        internal uint BitsPerChannel;
        internal uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioUnitRenderCallback
    {
        internal IntPtr Callback;
        internal IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioBuffer
    {
        internal uint NumberChannels;
        internal uint DataByteSize;
        internal IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct AudioObjectPropertyAddress(
        uint selector, uint scope, uint element)
    {
        internal readonly uint Selector = selector;
        internal readonly uint Scope = scope;
        internal readonly uint Element = element;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RenderCallback(
        IntPtr context,
        IntPtr actionFlags,
        IntPtr timestamp,
        uint busNumber,
        uint numberFrames,
        IntPtr ioData);

    [DllImport(AudioToolbox)]
    private static extern IntPtr AudioComponentFindNext(
        IntPtr component, ref AudioComponentDescription description);
    [DllImport(AudioToolbox)]
    private static extern int AudioComponentInstanceNew(IntPtr component, out IntPtr instance);
    [DllImport(AudioToolbox)]
    private static extern int AudioComponentInstanceDispose(IntPtr instance);
    [DllImport(AudioToolbox)]
    private static extern int AudioUnitSetProperty(
        IntPtr unit, uint property, uint scope, uint element,
        IntPtr data, uint dataSize);
    [DllImport(AudioToolbox)]
    private static extern int AudioUnitInitialize(IntPtr unit);
    [DllImport(AudioToolbox)]
    private static extern int AudioUnitUninitialize(IntPtr unit);
    [DllImport(AudioToolbox)]
    private static extern int AudioOutputUnitStart(IntPtr unit);
    [DllImport(AudioToolbox)]
    private static extern int AudioOutputUnitStop(IntPtr unit);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyDataSize(
        uint objectId, ref AudioObjectPropertyAddress address,
        uint qualifierDataSize, IntPtr qualifierData, ref uint dataSize);
    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref AudioObjectPropertyAddress address,
        uint qualifierDataSize, IntPtr qualifierData, ref uint dataSize, IntPtr data);
    [DllImport(CoreFoundation)]
    private static extern nint CFStringGetLength(IntPtr value);
    [DllImport(CoreFoundation)]
    private static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);
    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(
        IntPtr value, IntPtr buffer, nint bufferSize, uint encoding);
    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);
}
