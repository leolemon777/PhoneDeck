using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PhoneDeck.MacReceiver.Tests;

/// <summary>Wi-Fi 共享 16 kHz 上传：协商头解析与 16 → 48 kHz 还原质量。</summary>
[TestClass]
public sealed class PhoneAudioFormatTests
{
    [TestMethod]
    public void RateHeaderDefaultsToBaselineAndRejectsUnknownRates()
    {
        Assert.AreEqual(48_000, PhoneAudioFormat.ParseRate(null));
        Assert.AreEqual(48_000, PhoneAudioFormat.ParseRate(" "));
        Assert.AreEqual(48_000, PhoneAudioFormat.ParseRate("48000"));
        Assert.AreEqual(16_000, PhoneAudioFormat.ParseRate(" 16000 "));
        Assert.ThrowsExactly<ArgumentException>(() => PhoneAudioFormat.ParseRate("44100"));
        Assert.ThrowsExactly<ArgumentException>(() => PhoneAudioFormat.ParseRate("abc"));
    }

    [TestMethod]
    public void SixteenKilohertzIsOnlyAllowedForSharedMicrophone()
    {
        PhoneAudioFormat.RequireAllowed(16_000, AudioStreamMode.Shared);
        PhoneAudioFormat.RequireAllowed(48_000, AudioStreamMode.Managed);
        Assert.ThrowsExactly<ArgumentException>(
            () => PhoneAudioFormat.RequireAllowed(16_000, AudioStreamMode.Managed));
    }

    [TestMethod]
    public void BaselineStreamIsPassedThroughUntouched()
    {
        using var source = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        Assert.AreSame(source, PhoneAudioFormat.ToBaseline(source, 48_000));
    }

    [TestMethod]
    public void UpsamplingTriplesLengthAndKeepsDcLevel()
    {
        var input = Samples(Enumerable.Repeat((short)1_000, 1_600).ToArray());
        var output = ReadAll(new Pcm16kTo48kStream(new MemoryStream(input)), 16 * 1024);

        Assert.AreEqual(input.Length * 3, output.Length);
        var samples = ToShorts(output);
        // 跳过滤波器起始的过渡段后应为稳定的 1000。
        foreach (var value in samples.Skip(Pcm16kTo48kStream.Taps).Take(3_000))
        {
            Assert.IsTrue(Math.Abs(value - 1_000) <= 2, $"直流偏差过大：{value}");
        }
    }

    [TestMethod]
    public void SpeechBandToneKeepsAmplitudeAndFrequency()
    {
        const double frequency = 1_000;
        var tone = new short[16_000];
        for (var n = 0; n < tone.Length; n++)
        {
            tone[n] = (short)Math.Round(10_000 * Math.Sin(2 * Math.PI * frequency * n / 16_000));
        }
        var output = ToShorts(ReadAll(new Pcm16kTo48kStream(new MemoryStream(Samples(tone))), 16 * 1024));

        // 在 48 kHz 上与 1 kHz 正交基做相关，得到幅度；能量应几乎全部落在 1 kHz。
        var start = Pcm16kTo48kStream.Taps * 2;
        var length = 48_000 / 2;
        double i = 0, q = 0, energy = 0;
        for (var n = start; n < start + length; n++)
        {
            var angle = 2 * Math.PI * frequency * n / 48_000;
            i += output[n] * Math.Cos(angle);
            q += output[n] * Math.Sin(angle);
            energy += (double)output[n] * output[n];
        }
        var amplitude = 2 * Math.Sqrt(i * i + q * q) / length;
        Assert.AreEqual(10_000, amplitude, 150, "1 kHz 幅度应保持（±1.5%）");
        var toneEnergy = amplitude * amplitude / 2 * length;
        Assert.IsTrue(toneEnergy / energy > 0.999, "镜像与失真能量应低于 -30 dB");
    }

    [TestMethod]
    public void OneByteReadsMatchBulkReads()
    {
        var random = new Random(7);
        var input = new byte[3_202];
        random.NextBytes(input);

        var bulk = ReadAll(new Pcm16kTo48kStream(new MemoryStream(input)), 16 * 1024);
        var trickle = ReadAll(new Pcm16kTo48kStream(new TrickleStream(input)), 5);

        CollectionAssert.AreEqual(bulk, trickle, "下层每次只给 1 字节、上层小缓冲读取，结果必须一致");
        Assert.AreEqual(input.Length / 2 * 6, bulk.Length);
    }

    private static byte[] ReadAll(Stream stream, int bufferSize)
    {
        using var output = new MemoryStream();
        var buffer = new byte[bufferSize];
        int read;
        while ((read = stream.ReadAsync(buffer.AsMemory()).AsTask().GetAwaiter().GetResult()) > 0)
        {
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static byte[] Samples(short[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var index = 0; index < values.Length; index++)
        {
            bytes[index * 2] = (byte)values[index];
            bytes[index * 2 + 1] = (byte)(values[index] >> 8);
        }
        return bytes;
    }

    private static short[] ToShorts(byte[] bytes)
    {
        var values = new short[bytes.Length / 2];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (short)(bytes[index * 2] | (bytes[index * 2 + 1] << 8));
        }
        return values;
    }

    /// <summary>每次只返回 1 字节，模拟 Wi-Fi 上任意切分的分块请求体。</summary>
    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= data.Length || count == 0) return 0;
            buffer[offset] = data[position++];
            return 1;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
