/// <summary>
/// 48 kHz / PCM16 / mono 的延迟控制：只丢“静音”，不丢语音。
/// Windows 与 macOS 接收端共用同一份源码（各自 csproj 以 Compile Link 引入）。
///
/// 背景：手机启动缓存与接收端 pre-roll 会在输入法确认采集后一次性放行，
/// 之后按 1 倍速播放，积压会一直保留到会话结束，松手后还要等它放完。
/// 这里提供两种无损于识别的手段：
/// 1. 放行 pre-roll 时裁掉开口前多余的静音，只保留 <see cref="LeadInMs"/>；
/// 2. 播放中积压超过目标时，跳过已持续静音一段时间（hangover）之后的 10 ms 静音块。
/// 不完整的尾块、疑似语音块、静音刚开始的 hangover 区间一律保留。
/// </summary>
internal static class PcmLatency
{
    internal const int SampleRate = 48_000;
    internal const int BytesPerMs = SampleRate * 2 / 1_000;

    /// <summary>判定粒度：10 ms。</summary>
    internal const int BlockBytes = BytesPerMs * 10;

    /// <summary>RMS 低于约 -42 dBFS 视为静音；轻辅音通常远高于此值。</summary>
    internal const double SilenceRms = 250;

    /// <summary>动态底噪追赶上限（约 -36.5 dBFS）：房间空调/风扇交流底噪平稳且低于此值时允许追赶。</summary>
    internal const double MaxAdaptiveSilenceRms = 480;

    /// <summary>pre-roll 裁剪后在首个语音块前保留的静音。</summary>
    internal const int LeadInMs = 150;

    /// <summary>静音持续超过该时长后才允许跳过，保护词尾与句间自然停顿的开头。</summary>
    internal const int HangoverMs = 200;

    /// <summary>共享麦克风由用户在电脑直接按键结束，句间 120 ms 短停顿即追赶积压，避免句末滞后吃字。</summary>
    internal const int SharedHangoverMs = 120;

    internal static int MsToBytes(int milliseconds) => milliseconds * BytesPerMs;

    internal const int DefaultOutputTailMs = 480;

    /// <summary>
    /// 尾音排空后继续输出静音的时长，让虚拟声卡与输入法收到最后几帧。默认 480 ms；
    /// 用 voice-loopback-delay 实测出虚拟声卡延迟后，可用环境变量
    /// PHONEDECK_OUTPUT_TAIL_MS 调整（100–1000），无需重新编译。
    /// </summary>
    internal static int OutputTailMs { get; } = ResolveOutputTailMs(
        Environment.GetEnvironmentVariable("PHONEDECK_OUTPUT_TAIL_MS"));

    internal static int ResolveOutputTailMs(string? configured) =>
        int.TryParse(configured, out var value) ? Math.Clamp(value, 100, 1_000) : DefaultOutputTailMs;

    internal static double CalculateRms(ReadOnlySpan<byte> pcm, out bool hasZeroCrossing)
    {
        var samples = pcm.Length / 2;
        hasZeroCrossing = false;
        if (samples == 0)
        {
            return 0;
        }
        double sum = 0;
        var hasPositive = false;
        var hasNegative = false;
        for (var index = 0; index + 1 < pcm.Length; index += 2)
        {
            var sample = (short)(pcm[index] | (pcm[index + 1] << 8));
            if (sample > 0) hasPositive = true;
            else if (sample < 0) hasNegative = true;
            double d = sample;
            sum += d * d;
        }
        hasZeroCrossing = hasPositive && hasNegative;
        return Math.Sqrt(sum / samples);
    }

    internal static bool IsSilent(ReadOnlySpan<byte> pcm) =>
        CalculateRms(pcm, out _) < SilenceRms;

    /// <summary>
    /// 返回 pre-roll 中应开始播放的偏移：首个语音块前保留 <paramref name="leadInBytes"/>；
    /// 完全没有语音时只保留末尾 <paramref name="leadInBytes"/>。结果按样本对齐。
    /// </summary>
    internal static int LeadingSilenceTrimOffset(ReadOnlySpan<byte> pcm, int leadInBytes)
    {
        var length = pcm.Length & ~1;
        for (var offset = 0; offset + BlockBytes <= length; offset += BlockBytes)
        {
            if (!IsSilent(pcm.Slice(offset, BlockBytes)))
            {
                return Math.Max(0, offset - leadInBytes) & ~1;
            }
        }
        return Math.Max(0, length - leadInBytes) & ~1;
    }
}

/// <summary>
/// 播放侧的静音追赶器（有状态，单个音频会话一个实例，调用方负责串行化）。
/// </summary>
internal sealed class PcmSilenceCatchUp
{
    private readonly int targetBacklogBytes;
    private readonly int hangoverBlocks;
    private int silentRun;
    private double ambientNoiseRms = PcmLatency.SilenceRms;
    private int steadyAmbientBlocks;

    internal PcmSilenceCatchUp(int targetBacklogMs, int hangoverMs = PcmLatency.HangoverMs)
    {
        targetBacklogBytes = PcmLatency.MsToBytes(targetBacklogMs);
        hangoverBlocks = Math.Max(0, hangoverMs / 10);
    }

    internal long SkippedBytes { get; private set; }

    private bool IsBlockSilentOrAmbient(ReadOnlySpan<byte> block)
    {
        var rms = PcmLatency.CalculateRms(block, out var hasZeroCrossing);
        if (rms < PcmLatency.SilenceRms)
        {
            ambientNoiseRms = ambientNoiseRms * 0.9 + Math.Max(40.0, rms) * 0.1;
            steadyAmbientBlocks = Math.Min(steadyAmbientBlocks + 1, 100);
            return true;
        }
        if (hasZeroCrossing && rms <= PcmLatency.MaxAdaptiveSilenceRms)
        {
            ambientNoiseRms = ambientNoiseRms * 0.92 + rms * 0.08;
            steadyAmbientBlocks = Math.Min(steadyAmbientBlocks + 1, 100);
            var threshold = steadyAmbientBlocks >= 20
                ? Math.Min(PcmLatency.MaxAdaptiveSilenceRms, Math.Max(PcmLatency.SilenceRms, ambientNoiseRms * 1.35))
                : PcmLatency.SilenceRms;
            return rms < threshold;
        }
        steadyAmbientBlocks = Math.Max(0, steadyAmbientBlocks - 2);
        return false;
    }

    /// <summary>
    /// 把 <paramref name="input"/> 中应播放的部分按序写入 <paramref name="output"/>（长度不小于输入），
    /// 返回写入字节数。<paramref name="backlogBytes"/> 为写入前播放缓冲中尚未播放的字节。
    /// </summary>
    internal int Filter(ReadOnlySpan<byte> input, Span<byte> output, int backlogBytes)
    {
        var written = 0;
        var offset = 0;
        while (offset + PcmLatency.BlockBytes <= input.Length)
        {
            var block = input.Slice(offset, PcmLatency.BlockBytes);
            offset += PcmLatency.BlockBytes;
            if (IsBlockSilentOrAmbient(block))
            {
                silentRun++;
                if (silentRun > hangoverBlocks && backlogBytes + written > targetBacklogBytes)
                {
                    SkippedBytes += block.Length;
                    continue;
                }
            }
            else
            {
                silentRun = 0;
            }
            block.CopyTo(output[written..]);
            written += block.Length;
        }
        var tail = input[offset..];
        tail.CopyTo(output[written..]);
        return written + tail.Length;
    }
}
