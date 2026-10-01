namespace PhoneDeck.Desktop;

// A shared stream supplies audio only. A local key starts a bounded segment from that stream.
// Managed start/stop and local segment control share this state machine; no audio is written to disk.
internal sealed class SpeechSession(ISpeechEngine engine, TranscriptStore transcripts, string computerId, Func<Action<string>?>? prepareInsertion = null) : IDisposable
{
    internal const int MaxBytes = 48000 * 2 * 120;
    private readonly object gate = new();
    private string? streamSession, streamOwner, streamMode, recordingSession, recordingOwner;
    private MemoryStream? recording;
    private readonly Queue<byte[]> preroll = new();
    private int prerollBytes;
    private TaskCompletionSource streamEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, long> stopped = new();
    private CancellationTokenSource? processing;
    private string? processingOwner, processingSession;
    private Action<string>? insertion;
    private long recordingStarted, audioEnded;
    private Timer? watchdog;
    private string state = "idle";
    private string? error;
    private Task pendingResult = Task.CompletedTask;
    internal bool Streaming { get { lock (gate) return streamSession is not null; } }
    internal bool Recording { get { lock (gate) return recording is not null; } }
    internal string? StreamSession { get { lock (gate) return streamSession; } }
    internal string? RecordingSession { get { lock (gate) return recordingSession; } }
    internal string? Mode { get { lock (gate) return streamMode; } }
    internal object Snapshot { get { lock (gate) return new { state, error, sessionId = recordingSession }; } }

    internal void Attach(string owner, string session, string mode)
    {
        Validate(session);
        if (mode is not ("managed" or "shared")) throw new ArgumentException("音频模式无效");
        lock (gate)
        {
            if (streamSession is not null || (recordingOwner is not null && (recordingOwner != owner || (mode == "managed" && recordingSession != session))))
                throw new InvalidOperationException("另一手机或会话正在供音");
            RejectStopped(session);
            streamSession = session; streamOwner = owner; streamMode = mode;
            audioEnded = 0;
            streamEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            preroll.Clear(); prerollBytes = 0;
        }
    }
    internal void Feed(string session, ReadOnlySpan<byte> pcm)
    {
        lock (gate)
        {
            if (streamSession != session) return;
            if (recording is not null)
            {
                if (recording.Length + pcm.Length > MaxBytes) { AbortLocked("单次说话最多两分钟，请分段输入"); return; }
                recording.Write(pcm);
            }
            else if (streamMode == "managed")
            {
                var bytes = pcm.ToArray(); preroll.Enqueue(bytes); prerollBytes += bytes.Length;
                while (prerollBytes > 96000 && preroll.TryDequeue(out var old)) prerollBytes -= old.Length;
            }
        }
    }
    internal void EndStream(string session, bool orderly)
    {
        lock (gate)
        {
            if (streamSession != session) return;
            streamSession = null; streamMode = null; streamOwner = null; streamEnded.TrySetResult();
            audioEnded = Environment.TickCount64;
            if (!orderly && recording is not null) AbortLocked("手机音频中断，本次听写已取消");
        }
    }
    internal bool Start(string owner, string session)
    {
        Validate(session);
        lock (gate)
        {
            RejectStopped(session);
            if (recordingSession == session && recordingOwner == owner) return true;
            if (!engine.Ready) throw new InvalidOperationException("请先在电脑完成语音模型下载");
            if (recording is not null || !pendingResult.IsCompleted) throw new InvalidOperationException("上一段语音仍在处理");
            if (streamSession is not null && (streamOwner != owner || streamMode != "managed" || streamSession != session))
                throw new InvalidOperationException("共享供音中，请用这台电脑的快捷键或先停止共享");
            recordingSession = session; recordingOwner = owner; recording = new(); state = "recording"; error = null;
            BeginWatchdog(); insertion = prepareInsertion?.Invoke();
            while (preroll.TryDequeue(out var bytes)) recording.Write(bytes); prerollBytes = 0;
            return false;
        }
    }
    internal string StartLocal(bool insert = true)
    {
        lock (gate)
        {
            if (streamSession is null || streamMode != "shared" || streamOwner is null) throw new InvalidOperationException("先在手机开启共享麦克风，并将这台电脑加入共享组");
            if (!engine.Ready) throw new InvalidOperationException("请先下载语音模型");
            if (recording is not null || !pendingResult.IsCompleted) throw new InvalidOperationException("上一段语音仍在处理");
            recordingSession = Guid.NewGuid().ToString(); recordingOwner = streamOwner; recording = new(); state = "recording"; error = null;
            BeginWatchdog(); insertion = insert ? prepareInsertion?.Invoke() : null;
            return recordingSession;
        }
    }
    internal async Task StopAsync(string owner, string session, bool local = false, bool cancel = false)
    {
        Validate(session);
        Task? drain = null;
        lock (gate)
        {
            if (recordingSession != session)
            {
                if (cancel && processingSession == session && processingOwner == owner) processing?.Cancel();
                stopped[session] = Environment.TickCount64; return;
            }
            if (recordingOwner != owner) throw new InvalidOperationException("该会话属于另一手机");
            stopped[session] = Environment.TickCount64;
            if (cancel) { AbortLocked(null); return; }
            if (!local && streamSession == session) { state = "stopping"; drain = streamEnded.Task; }
        }
        if (drain is not null)
        {
            try { await drain.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { lock (gate) if (recordingSession == session) AbortLocked("音频尚未收尾，本段已取消"); return; }
        }
        lock (gate)
        {
            if (recordingSession != session || recording is null) return;
            var pcm = recording.ToArray(); ClearRecording(); recording = null; recordingSession = null; recordingOwner = null;
            state = "processing"; processing = new(); processingOwner = owner; processingSession = session;
            var insert = insertion; insertion = null;
            pendingResult = ProcessResultAsync(owner, session, pcm, insert, processing.Token);
        }
    }
    internal Task StopLocalAsync(bool cancel = false)
    {
        lock (gate)
        {
            if (cancel && recordingSession is null) processing?.Cancel();
            return recordingSession is null ? Task.CompletedTask : StopAsync(recordingOwner!, recordingSession, true, cancel);
        }
    }
    internal void Revoke(string owner)
    {
        lock (gate)
        {
            if (recordingOwner == owner || streamOwner == owner) { AbortLocked(null); streamSession = null; streamOwner = null; streamMode = null; streamEnded.TrySetResult(); }
            // Do not allow a result produced after authorization was revoked to enter history.
            if (processingOwner == owner) processing?.Cancel();
        }
    }
    internal Task WaitForResultAsync() { lock (gate) return pendingResult; }
    private async Task ProcessResultAsync(string owner, string session, byte[] pcm, Action<string>? insert, CancellationToken cancellation)
    {
        // Force the potentially CPU-bound engine off the HTTP/GUI caller and outside the state lock.
        await Task.Yield();
        try
        {
            var text = await engine.TranscribeAsync(pcm, cancellation);
            lock (gate)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    transcripts.Add(owner, new(Guid.NewGuid().ToString(), computerId, session, text, DateTimeOffset.UtcNow));
                    try { insert?.Invoke(text); } catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { error = "文字已保留，自动输入未完成，请从记录复制"; }
                }
                state = "idle";
            }
        }
        catch (OperationCanceledException) { lock (gate) state = "idle"; }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        { lock (gate) { state = "error"; error = "识别未完成，请重试并检查模型"; } }
        finally { Array.Clear(pcm); lock (gate) { processing?.Dispose(); processing = null; processingOwner = null; processingSession = null; } }
    }
    private void AbortLocked(string? reason)
    {
        if (recordingSession is not null) stopped[recordingSession] = Environment.TickCount64;
        ClearRecording(); recording = null; recordingSession = null; recordingOwner = null;
        insertion = null;
        preroll.Clear(); prerollBytes = 0; state = reason is null ? "idle" : "error"; error = reason;
    }
    private void RejectStopped(string session)
    {
        foreach (var key in stopped.Where(x => Environment.TickCount64 - x.Value > 60000).Select(x => x.Key).ToArray()) stopped.Remove(key);
        if (stopped.ContainsKey(session)) throw new InvalidOperationException("该会话已停止，迟到启动已拒绝");
    }
    private void ClearRecording()
    {
        if (recording is null) return;
        if (recording.TryGetBuffer(out var bytes)) bytes.AsSpan().Clear();
        recording.Dispose();
    }
    private static void Validate(string session) { if (!Guid.TryParse(session, out _)) throw new ArgumentException("sessionId 无效"); }
    private void BeginWatchdog()
    {
        recordingStarted = Environment.TickCount64; audioEnded = 0;
        watchdog ??= new Timer(_ => CheckTimeout());
        watchdog.Change(1000, 1000);
    }
    private void CheckTimeout()
    {
        lock (gate)
        {
            if (recording is null) return;
            var now = Environment.TickCount64;
            if (now - recordingStarted > 120000 || (streamSession is null && ((audioEnded != 0 && now - audioEnded > 5000) || (audioEnded == 0 && now - recordingStarted > 8000))))
                AbortLocked("供音或结束指令超时，本段已取消");
        }
    }
    public void Dispose() { lock (gate) { AbortLocked(null); processing?.Cancel(); watchdog?.Dispose(); } }
}
