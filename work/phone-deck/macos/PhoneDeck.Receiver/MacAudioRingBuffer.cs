namespace PhoneDeck.MacReceiver;

/// <summary>A bounded byte ring. Overflow drops the oldest audio; underrun emits silence.</summary>
internal sealed class MacAudioRingBuffer
{
    private readonly object syncRoot = new();
    private readonly byte[] data;
    private int readIndex;
    private int count;

    internal MacAudioRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        data = new byte[capacity];
    }

    internal int Capacity => data.Length;
    internal int Count { get { lock (syncRoot) return count; } }

    internal void Write(ReadOnlySpan<byte> source)
    {
        lock (syncRoot)
        {
            if (source.Length >= data.Length)
            {
                source = source[^data.Length..];
                source.CopyTo(data);
                readIndex = 0;
                count = data.Length;
                return;
            }
            var overflow = Math.Max(0, count + source.Length - data.Length);
            readIndex = (readIndex + overflow) % data.Length;
            count -= overflow;
            var writeIndex = (readIndex + count) % data.Length;
            var first = Math.Min(source.Length, data.Length - writeIndex);
            source[..first].CopyTo(data.AsSpan(writeIndex, first));
            source[first..].CopyTo(data);
            count += source.Length;
        }
    }

    internal int Read(Span<byte> destination)
    {
        lock (syncRoot)
        {
            var copied = Math.Min(count, destination.Length);
            var first = Math.Min(copied, data.Length - readIndex);
            data.AsSpan(readIndex, first).CopyTo(destination);
            data.AsSpan(0, copied - first).CopyTo(destination[first..]);
            readIndex = (readIndex + copied) % data.Length;
            count -= copied;
            destination[copied..].Clear();
            return copied;
        }
    }

    internal void Clear()
    {
        lock (syncRoot)
        {
            readIndex = 0;
            count = 0;
        }
    }

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
