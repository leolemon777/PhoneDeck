internal interface IPhoneAudioSessionController
{
    bool IsSessionActive(string sessionId);
    bool WaitForSessionActive(string sessionId, int timeoutMilliseconds);
    bool StopSession(string sessionId);
}

internal interface ITypelessController
{
    bool UsesVirtualCable { get; }
    bool? IsCapturing();
    bool? WaitForCapturing(bool expected, int timeoutMilliseconds);
    bool IsModeConfigured(string mode);
    bool ToggleOnce(string requestId, string mode);
    void Toggle(string mode);
}

internal sealed class WindowsTypelessController : ITypelessController
{
    private const int MinimumToggleGapMilliseconds = 400;
    private long lastToggleMilliseconds;

    public bool UsesVirtualCable => KeyboardInput.TypelessUsesVirtualCable;

    public bool? IsCapturing() => TypelessStateProbe.IsCapturing();

    public bool? WaitForCapturing(bool expected, int timeoutMilliseconds) =>
        TypelessStateProbe.WaitForCapturing(expected, timeoutMilliseconds);

    public bool IsModeConfigured(string mode) => KeyboardInput.TypelessModeConfigured(mode);

    public bool ToggleOnce(string requestId, string mode)
    {
        WaitForToggleGap();
        var duplicate = KeyboardInput.ExecuteOnce("typeless", mode, requestId);
        if (!duplicate)
        {
            lastToggleMilliseconds = Environment.TickCount64;
        }
        return duplicate;
    }

    public void Toggle(string mode)
    {
        WaitForToggleGap();
        KeyboardInput.Execute("typeless", mode);
        lastToggleMilliseconds = Environment.TickCount64;
    }

    private void WaitForToggleGap()
    {
        if (lastToggleMilliseconds <= 0)
        {
            return;
        }
        var remaining = MinimumToggleGapMilliseconds
            - (Environment.TickCount64 - lastToggleMilliseconds);
        if (remaining > 0)
        {
            Thread.Sleep((int)remaining);
        }
    }
}
