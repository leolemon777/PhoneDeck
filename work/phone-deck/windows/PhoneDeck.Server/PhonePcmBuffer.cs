using NAudio.Wave;

/// <summary>Managed pre-roll may queue speech; shared audio must stay live.</summary>
internal sealed class PhonePcmBuffer : IWaveProvider
{
    internal const int SharedQueueMs = 120;
    private readonly object sync = new();
    private readonly BufferedWaveProvider buffer;
    private readonly int? liveLimit;
    private readonly byte[] discard;

    internal PhonePcmBuffer(AudioStreamMode mode)
    {
        buffer = new BufferedWaveProvider(new WaveFormat(48_000, 16, 1))
        {
            BufferDuration = TimeSpan.FromMilliseconds(PhoneAudioBridge.PreRollHoldMs + 1_200),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        liveLimit = mode == AudioStreamMode.Shared
            ? 48_000 * 2 * SharedQueueMs / 1_000 : null;
        discard = new byte[liveLimit ?? 0];
    }

    public WaveFormat WaveFormat => buffer.WaveFormat;
    internal long DroppedBytes { get; private set; }
    internal int BufferedBytes { get { lock (sync) { return buffer.BufferedBytes; } } }

    internal void AddSamples(byte[] samples, int offset, int count)
    {
        lock (sync)
        {
            if (liveLimit is int limit)
            {
                // Drop stale backlog rather than keeping old audio and discarding
                // new words. A native engine stop cannot wait for PhoneDeck queues.
                var skip = Math.Max(0, count - limit);
                var remove = Math.Max(0, buffer.BufferedBytes + count - skip - limit);
                if (remove > 0)
                {
                    buffer.Read(discard, 0, remove);
                }
                offset += skip;
                count -= skip;
                DroppedBytes += skip + remove;
            }
            buffer.AddSamples(samples, offset, count);
        }
    }

    public int Read(byte[] destination, int offset, int count)
    {
        lock (sync)
        {
            return buffer.Read(destination, offset, count);
        }
    }
}
