namespace PhoneDeck.Desktop;

internal sealed class WebPhoneLocalTarget(ReceiverIdentity identity, SpeechSession speech,
    PlatformInput input, string owner, Func<bool> audioReady, Action<string>? testInput = null) : IWebPhoneTarget
{
    private readonly object gate = new();
    private string? activeSession;
    private string? mode;
    private string name = identity.DisplayName;
    public string Id => identity.ComputerId;
    public string Name => name;
    public void UpdateName(string value) => name = value;
    public Task<WebPhoneTargetState> HealthAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var health = speech.HealthForPhone(owner);
        string? session; lock (gate) session = activeSession;
        var ours = session is not null && health.StreamSession == session;
        return Task.FromResult(new WebPhoneTargetState(Id, Name, true, audioReady(), ours && health.Streaming,
            ours && health.Recording, ours ? session : null, health.StopRequestedSessionId));
    }
    public Task StartAsync(string session, string requestedMode, CancellationToken cancellation)
    {
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (activeSession is not null) throw new InvalidOperationException("请先结束上一段供音");
            speech.Attach(owner, session, requestedMode); activeSession = session; mode = requestedMode;
            try { if (mode == "managed") speech.Start(owner, session); }
            catch { speech.EndStream(session, false); activeSession = null; mode = null; throw; }
        }
        return Task.CompletedTask;
    }
    public bool Feed(string session, byte[] pcm)
    {
        lock (gate)
        {
            if (activeSession != session) return false;
            speech.Feed(session, pcm); return true;
        }
    }
    public async Task StopAsync(string session, bool cancel, CancellationToken cancellation)
    {
        bool managed;
        lock (gate)
        {
            if (activeSession != session) return;
            managed = mode == "managed"; activeSession = null; mode = null;
            if (managed) speech.EndStream(session, !cancel);
        }
        if (managed) await speech.StopAsync(owner, session, cancel: cancel);
        // Closing the shared microphone cancels a desktop segment that was not yet submitted.
        else await speech.StopSupplyAsync(owner, session, true);
    }
    public Task InputAsync(string request, string action, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (testInput is not null) testInput(action);
        else if (action == "goal") { input.Execute("text", "/goal", null, null); input.Execute("enter", null, null, null); }
        else input.Execute(action, null, null, null);
        return Task.CompletedTask;
    }
    public void Dispose() { lock (gate) if (activeSession is { } session) { speech.EndStream(session, false); activeSession = null; } }
}

