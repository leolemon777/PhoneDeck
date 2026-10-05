using NAudio.CoreAudioApi;
using NAudio.Wave;

internal interface IPhoneAudioPlayback : IDisposable
{
    void Play();
    void Stop();

    /// <summary>输出在未请求停止时意外结束（设备被移除/切换等）；常驻输出据此重建。</summary>
    bool IsFaulted => false;
}

/// <summary>Owns the device and WASAPI output for one stream.</summary>
internal sealed class WasapiPhoneAudioPlayback : IPhoneAudioPlayback
{
    /// <summary>事件驱动共享模式缓冲；VB-CABLE 自身还有内部缓冲，这里不再叠加大延迟。</summary>
    internal const int LatencyMilliseconds = 30;

    private readonly MMDevice device;
    private readonly WasapiOut output;
    private volatile bool stopRequested;
    private volatile bool faulted;

    internal WasapiPhoneAudioPlayback(IWaveProvider source)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(
            DataFlow.Render, DeviceState.Active).ToList();
        var selected = PhoneAudioBridge.SelectDevice(devices);
        foreach (var candidate in devices)
        {
            if (!ReferenceEquals(candidate, selected))
            {
                candidate.Dispose();
            }
        }
        device = selected ?? throw new InvalidOperationException(
            "未找到 VB-Audio Virtual Cable 播放端");
        WasapiOut? created = null;
        try
        {
            created = new WasapiOut(device, AudioClientShareMode.Shared,
                useEventSync: true, latency: LatencyMilliseconds);
            created.Init(source);
            created.PlaybackStopped += (_, _) =>
            {
                if (!stopRequested)
                {
                    faulted = true;
                }
            };
            output = created;
        }
        catch
        {
            created?.Dispose();
            device.Dispose();
            throw;
        }
    }

    public bool IsFaulted => faulted;

    public void Play()
    {
        stopRequested = false;
        output.Play();
    }

    public void Stop()
    {
        stopRequested = true;
        output.Stop();
    }

    public void Dispose()
    {
        stopRequested = true;
        output.Dispose();
        device.Dispose();
    }
}

/// <summary>
/// 常驻输出的数据源：未挂接会话时输出静音，挂接后读取当前会话的 PCM 缓冲。
/// 让 VB-CABLE 播放端始终处于运行状态，会话开始无需重新初始化 WASAPI，
/// 输入法打开 CABLE Output 时也不会遇到冷启动。
/// </summary>
internal sealed class SwitchingWaveProvider : IWaveProvider
{
    private readonly object sync = new();
    private IWaveProvider? source;

    public WaveFormat WaveFormat { get; } = new(48_000, 16, 1);

    internal void Attach(IWaveProvider provider)
    {
        lock (sync)
        {
            source = provider;
        }
    }

    internal void Detach(IWaveProvider provider)
    {
        lock (sync)
        {
            if (ReferenceEquals(source, provider))
            {
                source = null;
            }
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        lock (sync)
        {
            if (source is not null)
            {
                return source.Read(buffer, offset, count);
            }
        }
        Array.Clear(buffer, offset, count);
        return count;
    }
}
