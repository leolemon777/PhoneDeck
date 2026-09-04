internal enum AudioStreamMode
{
    Managed,
    Shared
}

internal static class AudioStreamModes
{
    internal static AudioStreamMode Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Equals("managed", StringComparison.OrdinalIgnoreCase))
        {
            return AudioStreamMode.Managed;
        }
        if (value.Equals("shared", StringComparison.OrdinalIgnoreCase))
        {
            return AudioStreamMode.Shared;
        }
        throw new ArgumentException("无效的音频工作模式");
    }

    internal static string ToWireValue(this AudioStreamMode mode) => mode switch
    {
        AudioStreamMode.Shared => "shared",
        _ => "managed"
    };

    internal static bool ControlsTypeless(this AudioStreamMode mode) =>
        mode == AudioStreamMode.Managed;
}

internal sealed class AudioStreamConflictException : InvalidOperationException
{
    internal AudioStreamConflictException(string message) : base(message)
    {
    }
}
