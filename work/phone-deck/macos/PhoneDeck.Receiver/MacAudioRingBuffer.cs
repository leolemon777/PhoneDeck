namespace PhoneDeck.MacReceiver;

/// <summary>
/// A bounded single-producer/single-consumer byte ring. Overflow drops the oldest
/// audio; underrun emits silence.
/// <para>
/// The consumer is the Core Audio render callback on a real-time thread, so it never
/// takes a lock: producer and consumer only exchange monotonically increasing byte
/// positions through volatile reads/writes. Writes are serialized by the caller
/// (the audio bridge lock); <see cref="Clear"/> is only used when the output stops.
/// </para>
/// </summary>
internal sealed class MacAudioRingBuffer
{
    private readonly byte[] data;
    private long writePosition;
    private long readPosition;

    internal MacAudioRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        data = new byte[capacity];
    }

    internal int Capacity => data.Length;

    internal int Count
    {
        get
        {
            var available = Volatile.Read(ref writePosition) - Volatile.Read(ref readPosition);
            return (int)Math.Clamp(available, 0, data.Length);
        }
    }

    internal async Task<bool> WaitForEmptyAsync(int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Count > 0)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) return false;
            await Task.Delay((int)Math.Min(20, remaining));
        }
        return true;
    }

    internal void Write(ReadOnlySpan<byte> source)
    {
        if (source.Length > data.Length)
        {
            source = source[^data.Length..];
        }
        var position = Volatile.Read(ref writePosition);
        var index = (int)(position % data.Length);
        var first = Math.Min(source.Length, data.Length - index);
        source[..first].CopyTo(data.AsSpan(index, first));
        source[first..].CopyTo(data);
        // Publish after the bytes are in place; the reader skips anything older than
        // one capacity behind this position (drop-oldest on overflow).
        Volatile.Write(ref writePosition, position + source.Length);
    }

    internal int Read(Span<byte> destination)
    {
        var write = Volatile.Read(ref writePosition);
        var read = Volatile.Read(ref readPosition);
        if (write - read > data.Length)
        {
            read = write - data.Length;
        }
        var copied = (int)Math.Min(write - read, destination.Length);
        var index = (int)(read % data.Length);
        var first = Math.Min(copied, data.Length - index);
        data.AsSpan(index, first).CopyTo(destination);
        data.AsSpan(0, copied - first).CopyTo(destination[first..]);
        Volatile.Write(ref readPosition, read + copied);
        destination[copied..].Clear();
        return copied;
    }

    internal void Clear() => Volatile.Write(ref readPosition, Volatile.Read(ref writePosition));

    internal static byte[] MonoPcm16ToStereo(ReadOnlySpan<byte> mono)
    {
        var sampleBytes = mono.Length - mono.Length % 2;
        var stereo = new byte[sampleBytes * 2];
        for (var input = 0; input < sampleBytes; input += 2)
        {
            var output = input * 2;
            stereo[output] = mono[input];
            stereo[output + 1] = mono[input + 1];
            stereo[output + 2] = mono[input];
            stereo[output + 3] = mono[input + 1];
        }
        return stereo;
    }
}

internal sealed class Pcm16MonoToStereoConverter
{
    private byte? pendingLowByte;

    internal byte[] Convert(ReadOnlySpan<byte> mono)
    {
        var sampleCount = (mono.Length + (pendingLowByte.HasValue ? 1 : 0)) / 2;
        var stereo = new byte[sampleCount * 4];
        var input = 0;
        var output = 0;
        if (pendingLowByte.HasValue && mono.Length > 0)
        {
            WriteSample(stereo, ref output, pendingLowByte.Value, mono[0]);
            pendingLowByte = null;
            input = 1;
        }
        while (input + 1 < mono.Length)
        {
            WriteSample(stereo, ref output, mono[input], mono[input + 1]);
            input += 2;
        }
        if (input < mono.Length)
        {
            pendingLowByte = mono[input];
        }
        return output == stereo.Length ? stereo : stereo[..output];
    }

    private static void WriteSample(byte[] target, ref int offset, byte low, byte high)
    {
        target[offset++] = low;
        target[offset++] = high;
        target[offset++] = low;
        target[offset++] = high;
    }
}
