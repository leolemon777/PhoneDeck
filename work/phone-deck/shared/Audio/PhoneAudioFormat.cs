/// <summary>
/// 手机上传音频的采样率协商。基线仍是 48 kHz / PCM16 / mono；接收端在 health 里声明
/// <see cref="Wifi16kCapability"/> 后，手机在 Wi-Fi 共享供音时可改发 16 kHz（流量降为 1/3），
/// 并用请求头 <see cref="RateHeader"/> 标明。接收端在进入播放缓冲前还原成 48 kHz，
/// 抖动缓冲、静音追赶、虚拟声卡输出等后续逻辑完全不变。USB 与受管听写仍发 48 kHz。
/// </summary>
internal static class PhoneAudioFormat
{
    internal const int BaselineRate = 48_000;
    internal const int WifiRate = 16_000;
    internal const string RateHeader = "X-PhoneDeck-Audio-Rate";
    internal const string Wifi16kCapability = "sharedPcm16kV1";

    /// <summary>缺省为 48 kHz（旧手机不发此头）；只接受 48000 与 16000。</summary>
    internal static int ParseRate(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return BaselineRate;
        }
        return header.Trim() switch
        {
            "48000" => BaselineRate,
            "16000" => WifiRate,
            _ => throw new ArgumentException("不支持的音频采样率：" + header.Trim())
        };
    }

    /// <summary>16 kHz 只用于 Wi-Fi 共享供音；受管听写保持 48 kHz 基线（契约 request.audio-stream）。</summary>
    internal static void RequireAllowed(int rate, AudioStreamMode mode)
    {
        if (rate != BaselineRate && mode != AudioStreamMode.Shared)
        {
            throw new ArgumentException("16 kHz 音频只用于共享麦克风");
        }
    }

    /// <summary>按采样率包装请求体：16 kHz 时返回升采样到 48 kHz 的只读流。</summary>
    internal static Stream ToBaseline(Stream source, int rate) =>
        rate == WifiRate ? new Pcm16kTo48kStream(source) : source;
}

/// <summary>
/// 16 kHz → 48 kHz 的 3 倍多相 FIR 升采样（Kaiser 窗 sinc，144 抽头，截止 8 kHz）。
/// 语音识别本身只用到 8 kHz 以内，手机端已做同样带宽的抗混叠滤波；镜像频率被压到约 -60 dB 以下。
/// 每输出样本 48 次乘加，单路约 2.3 M 次/秒。只读、顺序读取；不是线程安全的。
/// </summary>
internal sealed class Pcm16kTo48kStream : Stream
{
    internal const int Factor = 3;
    internal const int Taps = 144;
    private const int PhaseLength = Taps / Factor;
    private static readonly float[][] Phases = DesignPhases();

    private readonly Stream inner;
    private readonly float[] history = new float[PhaseLength * 2];
    private int historyPosition;
    private readonly byte[] inputBuffer = new byte[4096];
    private int pendingLowByte = -1;
    private byte[] pendingOutput = new byte[4096 * Factor + 8];
    private int pendingOffset;
    private int pendingCount;

    internal Pcm16kTo48kStream(Stream inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        while (pendingCount == 0)
        {
            var read = inner.Read(inputBuffer, 0, InputRequest(destination.Length));
            if (read == 0)
            {
                return 0;
            }
            Convert(inputBuffer.AsSpan(0, read));
        }
        return CopyPending(destination);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        while (pendingCount == 0)
        {
            var read = await inner.ReadAsync(
                inputBuffer.AsMemory(0, InputRequest(destination.Length)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return 0;
            }
            Convert(inputBuffer.AsSpan(0, read));
        }
        return CopyPending(destination.Span);
    }

    /// <summary>按目标缓冲大小向下层要数据：每 2 字节输入产生 6 字节输出。</summary>
    private int InputRequest(int destinationLength) =>
        Math.Clamp(destinationLength / Factor & ~1, 2, inputBuffer.Length);

    private int CopyPending(Span<byte> destination)
    {
        var count = Math.Min(pendingCount, destination.Length);
        pendingOutput.AsSpan(pendingOffset, count).CopyTo(destination);
        pendingOffset += count;
        pendingCount -= count;
        if (pendingCount == 0)
        {
            pendingOffset = 0;
        }
        return count;
    }

    private void Convert(ReadOnlySpan<byte> input)
    {
        var maxOutput = (input.Length / 2 + 1) * Factor * 2;
        if (pendingOutput.Length < maxOutput)
        {
            pendingOutput = new byte[maxOutput];
        }
        var written = 0;
        var index = 0;
        if (pendingLowByte >= 0 && input.Length > 0)
        {
            written += Push((short)(pendingLowByte | (input[0] << 8)), written);
            pendingLowByte = -1;
            index = 1;
        }
        for (; index + 1 < input.Length; index += 2)
        {
            written += Push((short)(input[index] | (input[index + 1] << 8)), written);
        }
        if (index < input.Length)
        {
            pendingLowByte = input[index];
        }
        pendingOffset = 0;
        pendingCount = written;
    }

    /// <summary>压入一个 16 kHz 样本，写出 3 个 48 kHz 样本，返回写入字节数。</summary>
    private int Push(short sample, int outputOffset)
    {
        history[historyPosition] = sample;
        history[historyPosition + PhaseLength] = sample;
        historyPosition = (historyPosition + 1) % PhaseLength;
        // history[historyPosition .. +PhaseLength) 为最旧 → 最新。
        var window = history.AsSpan(historyPosition, PhaseLength);
        for (var phase = 0; phase < Factor; phase++)
        {
            var coefficients = Phases[phase];
            float sum = 0;
            for (var j = 0; j < PhaseLength; j++)
            {
                // coefficients[j] 作用于 x[m - j]，window 末尾是 x[m]。
                sum += coefficients[j] * window[PhaseLength - 1 - j];
            }
            var value = (int)MathF.Round(sum);
            value = Math.Clamp(value, short.MinValue, short.MaxValue);
            pendingOutput[outputOffset++] = (byte)value;
            pendingOutput[outputOffset++] = (byte)(value >> 8);
        }
        return Factor * 2;
    }

    /// <summary>原型低通 h[k]，拆成 3 个相位 h[p + 3j]，每相位归一化为直流增益 1。</summary>
    private static float[][] DesignPhases()
    {
        var prototype = KaiserLowPass(Taps, 8_000.0 / PhoneAudioFormat.BaselineRate, 7.0);
        var phases = new float[Factor][];
        for (var phase = 0; phase < Factor; phase++)
        {
            var coefficients = new float[PhaseLength];
            double sum = 0;
            for (var j = 0; j < PhaseLength; j++)
            {
                sum += prototype[phase + Factor * j];
            }
            for (var j = 0; j < PhaseLength; j++)
            {
                coefficients[j] = (float)(prototype[phase + Factor * j] / sum);
            }
            phases[phase] = coefficients;
        }
        return phases;
    }

    /// <summary>Kaiser 窗 sinc 低通；cutoff 为相对采样率的截止频率（0–0.5）。</summary>
    internal static double[] KaiserLowPass(int taps, double cutoff, double beta)
    {
        var result = new double[taps];
        var center = (taps - 1) / 2.0;
        var denominator = BesselI0(beta);
        for (var n = 0; n < taps; n++)
        {
            var x = n - center;
            var sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            var ratio = 2.0 * n / (taps - 1) - 1;
            var window = BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - ratio * ratio))) / denominator;
            result[n] = sinc * window;
        }
        return result;
    }

    private static double BesselI0(double value)
    {
        double sum = 1, term = 1;
        var half = value / 2;
        for (var k = 1; k < 64; k++)
        {
            term *= half / k * (half / k);
            sum += term;
            if (term < sum * 1e-12)
            {
                break;
            }
        }
        return sum;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
