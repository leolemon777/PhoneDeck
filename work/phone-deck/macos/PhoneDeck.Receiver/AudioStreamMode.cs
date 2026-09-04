namespace PhoneDeck.MacReceiver;

internal enum AudioStreamMode
{
    Managed,
    Shared
}

internal static class AudioStreamModeParser
{
    internal static AudioStreamMode Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "managed" => AudioStreamMode.Managed,
            "shared" => AudioStreamMode.Shared,
            _ => throw new ArgumentException("X-PhoneDeck-Audio-Mode 必须是 managed 或 shared")
        };

    internal static string ToWireValue(this AudioStreamMode mode) =>
        mode == AudioStreamMode.Shared ? "shared" : "managed";
}

internal sealed class AudioStreamConflictException(string message) : Exception(message);
