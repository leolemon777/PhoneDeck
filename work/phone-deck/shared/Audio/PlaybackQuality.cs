/// <summary>
/// 播放侧的断续统计与抖动缓冲。Windows（48 kHz mono）与 macOS（48 kHz stereo）接收端共用，
/// 字节阈值由调用方按各自的输出格式换算。两个类的观察方法只由音频渲染线程调用：不加锁、不分配。
/// </summary>
internal static class PlaybackQuality
{
    /// <summary>抖动缓冲：开始播放或播空后先攒够这么多再继续。</summary>
    internal const int PrefillMs = 40;

    /// <summary>超过这么久没有数据视为流已结束（排空、会话间空闲），不计为断续。</summary>
    internal const int MaxGapMs = 2_000;
}

/// <summary>
/// 统计播放中途的断续：已有音频后渲染取不到足够数据、随后又恢复，计一次。
/// 超过上限没有数据视为流已结束，不计入。
/// </summary>
internal sealed class AudioGapCounter(long maxGapBytes)
{
    private bool hadAudio;
    private long pendingBytes;
    private long events;
    private long bytes;

    internal long Events => Interlocked.Read(ref events);
    internal long Bytes => Interlocked.Read(ref bytes);

    internal void Observe(int copied, int requested)
    {
        if (copied > 0)
        {
            if (pendingBytes > 0)
            {
                Interlocked.Increment(ref events);
                Interlocked.Add(ref bytes, pendingBytes);
                pendingBytes = 0;
            }
            hadAudio = true;
        }
        if (copied < requested && hadAudio)
        {
            pendingBytes += requested - copied;
            if (pendingBytes > maxGapBytes)
            {
                pendingBytes = 0;
                hadAudio = false;
            }
        }
    }
}

/// <summary>
/// 抖动缓冲：开始播放或播空后，先攒够 prefillBytes 再继续，
/// 把网络晚到造成的频繁小断续换成一次很短的等待。排空尾音时（<see cref="Flush"/>）不再等待。
/// </summary>
internal sealed class JitterPrefill(int prefillBytes)
{
    private bool priming = true;
    private volatile bool flushing;

    internal bool ShouldPlay(int bufferedBytes)
    {
        if (priming && !flushing && bufferedBytes < prefillBytes)
        {
            return false;
        }
        priming = false;
        return true;
    }

    internal void AfterRead(int copied, int requested)
    {
        if (copied < requested)
        {
            priming = true;
        }
    }

    /// <summary>流结束、排空尾音：不足的部分也立即播放。</summary>
    internal void Flush() => flushing = true;

    /// <summary>有新音频写入：恢复抖动缓冲。</summary>
    internal void Resume() => flushing = false;
}
