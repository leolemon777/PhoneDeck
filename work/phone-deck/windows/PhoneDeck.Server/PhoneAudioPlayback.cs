using NAudio.CoreAudioApi;
using NAudio.Wave;

internal interface IPhoneAudioPlayback : IDisposable
{
    void Play();
    void Stop();
}

/// <summary>Owns the device and WASAPI output for one stream.</summary>
internal sealed class WasapiPhoneAudioPlayback : IPhoneAudioPlayback
{
    private readonly MMDevice device;
    private readonly WasapiOut output;

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
                useEventSync: true, latency: 60);
            created.Init(source);
            output = created;
        }
        catch
        {
            created?.Dispose();
            device.Dispose();
            throw;
        }
    }

    public void Play() => output.Play();
    public void Stop() => output.Stop();

    public void Dispose()
    {
        output.Dispose();
        device.Dispose();
    }
}
