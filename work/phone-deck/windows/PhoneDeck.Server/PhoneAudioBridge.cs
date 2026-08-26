using NAudio.CoreAudioApi;
using NAudio.Wave;

internal sealed class PhoneAudioBridge : IDisposable
{
    private const int SampleRate = 48_000;
    private readonly SemaphoreSlim streamGate = new(1, 1);
    private readonly object sessionSync = new();
    private CancellationTokenSource? activeCancellation;
    private string? activeSessionId;

    internal bool IsStreaming
    {
        get
        {
            lock (sessionSync)
            {
                return activeSessionId is not null;
            }
        }
    }

    internal string? ActiveSessionId
    {
        get
        {
            lock (sessionSync)
            {
                return activeSessionId;
            }
        }
    }

    internal string? FindVirtualCable()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .ToList();
        try
        {
            return SelectDevice(devices)?.FriendlyName;
        }
        finally
        {
            foreach (var device in devices)
            {
                device.Dispose();
            }
        }
    }

    internal bool IsSessionActive(string sessionId)
    {
        lock (sessionSync)
        {
            return string.Equals(activeSessionId, sessionId, StringComparison.Ordinal);
        }
    }

    internal bool StopSession(string sessionId)
    {
        CancellationTokenSource? cancellation;
        lock (sessionSync)
        {
            if (!string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
            {
                return false;
            }
            cancellation = activeCancellation;
        }
        cancellation?.Cancel();
        return true;
    }

    internal async Task<long> StreamAsync(
        Stream input,
        string sessionId,
        Action<string> sessionEnded,
        CancellationToken cancellationToken)
    {
        if (!await streamGate.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("已有手机麦克风正在传输");
        }

        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        lock (sessionSync)
        {
            activeSessionId = sessionId;
            activeCancellation = sessionCancellation;
        }

        MMDevice? selected = null;
        List<MMDevice>? devices = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            devices = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .ToList();
            selected = SelectDevice(devices);
            if (selected is null)
            {
                throw new InvalidOperationException("未找到 VB-Audio Virtual Cable 播放端");
            }

            var provider = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, 1))
            {
                BufferDuration = TimeSpan.FromMilliseconds(700),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            using var output = new WasapiOut(
                selected,
                AudioClientShareMode.Shared,
                useEventSync: true,
                latency: 60);
            output.Init(provider);
            output.Play();

            var bytes = new byte[16 * 1024];
            var carry = 0;
            long totalBytes = 0;
            while (true)
            {
                var read = await input.ReadAsync(
                    bytes.AsMemory(carry, bytes.Length - carry),
                    sessionCancellation.Token);
                if (read == 0)
                {
                    break;
                }

                var available = carry + read;
                var completeBytes = available & ~1;
                if (completeBytes > 0)
                {
                    provider.AddSamples(bytes, 0, completeBytes);
                    totalBytes += completeBytes;
                }
                carry = available - completeBytes;
                if (carry == 1)
                {
                    bytes[0] = bytes[completeBytes];
                }
            }

            await Task.Delay(100, CancellationToken.None);
            output.Stop();
            return totalBytes;
        }
        finally
        {
            lock (sessionSync)
            {
                if (ReferenceEquals(activeCancellation, sessionCancellation))
                {
                    activeCancellation = null;
                    activeSessionId = null;
                }
            }
            sessionEnded(sessionId);
            if (devices is not null)
            {
                foreach (var device in devices)
                {
                    device.Dispose();
                }
            }
            streamGate.Release();
        }
    }

    private static MMDevice? SelectDevice(IEnumerable<MMDevice> devices) =>
        devices
            .Where(device => device.FriendlyName.Contains(
                "VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase))
            .OrderBy(device => device.FriendlyName.Contains(
                "16", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (sessionSync)
        {
            cancellation = activeCancellation;
        }
        cancellation?.Cancel();
    }
}
