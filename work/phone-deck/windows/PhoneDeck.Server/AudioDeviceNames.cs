using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

/// <summary>
/// NAudio 2.2.1 FriendlyName performs Contains + key lookup, each walking the
/// entire COM property store and reading Count on every iteration. Keep only
/// a bounded property-index hint; always read the current device and value.
/// </summary>
internal static class AudioDeviceNames
{
    private const int MaxCachedDevices = 64;
    private static readonly object sync = new();
    private static readonly Dictionary<string, int> indices = new(StringComparer.Ordinal);

    internal static string Read(MMDevice device)
    {
        var id = device.ID;
        var properties = device.Properties;
        var count = properties.Count;
        int index;
        lock (sync)
        {
            index = indices.TryGetValue(id, out var cached) ? cached : -1;
        }

        // A hint is never a cached availability or name result. Revalidate the
        // key before using it, including after device/driver/property changes.
        if (index < 0 || index >= count || !IsFriendlyName(properties.Get(index)))
        {
            index = -1;
            for (var candidate = 0; candidate < count; candidate++)
            {
                if (IsFriendlyName(properties.Get(candidate)))
                {
                    index = candidate;
                    break;
                }
            }
            lock (sync)
            {
                if (index < 0)
                {
                    indices.Remove(id);
                }
                else
                {
                    if (indices.Count >= MaxCachedDevices && !indices.ContainsKey(id))
                    {
                        indices.Clear();
                    }
                    indices[id] = index;
                }
            }
        }
        if (index < 0)
        {
            return "Unknown";
        }

        var value = properties.GetValue(index);
        try
        {
            // A changing property store must not turn a different string
            // property into a device-name match. Let diagnostics mark unknown.
            if (!IsFriendlyName(properties.Get(index)))
            {
                throw new InvalidOperationException("音频设备属性已变化，请重新读取");
            }
            return value.DataType == VarEnum.VT_LPWSTR
                ? value.Value as string ?? "Unknown" : "Unknown";
        }
        finally
        {
            // GetValue transfers a PROPVARIANT whose native string must be
            // released after copying it to a managed string (Ole32 contract).
            _ = PropVariantClear(ref value);
        }
    }

    private static bool IsFriendlyName(PropertyKey key) =>
        key.formatId == PropertyKeys.PKEY_Device_FriendlyName.formatId
        && key.propertyId == PropertyKeys.PKEY_Device_FriendlyName.propertyId;

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);
}
