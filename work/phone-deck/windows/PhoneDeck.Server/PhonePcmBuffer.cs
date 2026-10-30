using NAudio.Wave;

/// <summary>
/// 手机 PCM 的播放缓冲。两种模式都用 <see cref="PcmSilenceCatchUp"/> 跳过积压中的静音，
/// 让播放追上实时、语音不丢：
/// - managed：积压超过 <see cref="ManagedTargetMs"/> 时跳过静音；pre-roll 突发不设硬上限；
/// - shared：积压超过 <see cref="SharedTargetMs"/> 时跳过静音，仍超过
///   <see cref="SharedQueueMs"/> 才丢弃最旧音频，保持共享输入的实时性。
/// </summary>
internal sealed class PhonePcmBuffer : IWaveProvider
{
    internal const int ManagedTargetMs = 150;
    internal const int SharedTargetMs = 120;
    internal const int SharedQueueMs = 240;
    private readonly object sync = new();
    private readonly BufferedWaveProvider buffer;
    private readonly PcmSilenceCatchUp catchUp;
    private readonly int? liveLimit;
    private byte[] discard = Array.Empty<byte>();
    private byte[] filtered = new byte[16 * 1024];
    // 48 kHz 单声道 PCM16：每毫秒 96 字节。
    private readonly AudioGapCounter gaps = new(PcmLatency.MsToBytes(PlaybackQuality.MaxGapMs));
    private readonly JitterPrefill prefill = new(PcmLatency.MsToBytes(PlaybackQuality.PrefillMs));
    private int maxBufferedBytes;
    private long digitalSilenceBytes;

    internal PhonePcmBuffer(AudioStreamMode mode)
    {
        var shared = mode == AudioStreamMode.Shared;
        buffer = new BufferedWaveProvider(new WaveFormat(48_000, 16, 1))
        {
            // Shared mode releases pre-roll immediately and enforces its own
            // 240ms live queue below; do not allocate the managed 2.7s buffer
            // for every shared microphone session.
            BufferDuration = TimeSpan.FromMilliseconds(
                shared ? SharedQueueMs : PhoneAudioBridge.PreRollHoldMs + 1_200),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        catchUp = new PcmSilenceCatchUp(shared ? SharedTargetMs : ManagedTargetMs);
        liveLimit = shared ? PcmLatency.MsToBytes(SharedQueueMs) : null;
    }

    public WaveFormat WaveFormat => buffer.WaveFormat;
    internal long DroppedBytes { get; private set; }
    internal long SkippedSilenceBytes { get { lock (sync) { return catchUp.SkippedBytes; } } }
    // BufferedWaveProvider's CircularBuffer.Count is independently locked;
    // this read-only query need not contend with the producer gate.
    internal int BufferedBytes => buffer.BufferedBytes;
    /// <summary>播放中途缺数据又恢复的断续次数与补静音字节（流结束的自然排空不计）。</summary>
    internal long GapEvents => gaps.Events;
    internal long GapBytes => gaps.Bytes;
    internal int MaxBufferedBytes { get { lock (sync) { return maxBufferedBytes; } } }
    /// <summary>整块全零的输入：持续全零通常表示 Android 麦克风被其他应用占用或被系统静音。</summary>
    internal long DigitalSilenceBytes { get { lock (sync) { return digitalSilenceBytes; } } }

    /// <summary>流结束、排空尾音：抖动缓冲不再等待凑够。</summary>
    internal void Flush() => prefill.Flush();

    internal void AddSamples(byte[] samples, int offset, int count)
    {
        lock (sync)
        {
            prefill.Resume();
            for (var block = offset; block + PcmLatency.BlockBytes <= offset + count; block += PcmLatency.BlockBytes)
            {
                if (!samples.AsSpan(block, PcmLatency.BlockBytes).ContainsAnyExcept((byte)0))
                {
                    digitalSilenceBytes += PcmLatency.BlockBytes;
                }
            }
            if (filtered.Length < count)
            {
                filtered = new byte[count];
            }
            var bufferedBytes = buffer.BufferedBytes;
            count = catchUp.Filter(samples.AsSpan(offset, count), filtered, bufferedBytes);
            samples = filtered;
            offset = 0;
            if (liveLimit is int limit)
            {
                // Drop stale backlog rather than keeping old audio and discarding
                // new words. A native engine stop cannot wait for PhoneDeck queues.
                var skip = Math.Max(0, count - limit);
                var remove = Math.Max(0, bufferedBytes + count - skip - limit);
                if (remove > 0)
                {
                    if (discard.Length < remove)
                    {
                        discard = new byte[remove];
                    }
                    buffer.Read(discard, 0, remove);
                }
                offset += skip;
                count -= skip;
                DroppedBytes += skip + remove;
            }
            buffer.AddSamples(samples, offset, count);
            maxBufferedBytes = Math.Max(maxBufferedBytes, buffer.BufferedBytes);
        }
    }

    public int Read(byte[] destination, int offset, int count)
    {
        lock (sync)
        {
            // ReadFully 会把不足部分补零并照常返回 count，先记下真实可读量用于断续统计。
            var available = buffer.BufferedBytes;
            int copied;
            if (prefill.ShouldPlay(available))
            {
                buffer.Read(destination, offset, count);
                copied = Math.Min(available, count);
            }
            else
            {
                Array.Clear(destination, offset, count);
                copied = 0;
            }
            prefill.AfterRead(copied, count);
            gaps.Observe(copied, count);
            return count;
        }
    }
}
