/// <summary>诊断快照：只由后台线程刷新，读者（/api/health）从不触发 IO。</summary>
internal sealed record DiagnosticsSnapshot
{
    public long CheckedAtMs { get; init; }
    public string? LastError { get; init; }

    /// <summary>当前语音引擎的一次完整状态（含各模式快捷键与录音探测）。</summary>
    public VoiceEngineSnapshot? Engine { get; init; }
    public string? VirtualCableDevice { get; init; }
    public string? ForegroundApp { get; init; }

    public bool AudioAvailable => VirtualCableDevice is not null;

    internal static DiagnosticsSnapshot Unavailable(string reason) => new()
    {
        CheckedAtMs = Environment.TickCount64,
        LastError = reason
    };
}

/// <summary>后台周期执行较慢的诊断（语音引擎配置读盘、Core Audio 会话枚举、
/// 设备枚举、前台窗口查询），以不可变快照提供给 /api/health。
/// 后台线程与 RefreshAsync 通过同一信号量串行，避免并发 COM 枚举互抢。</summary>
internal sealed class DiagnosticsMonitor : IDisposable
{
    internal const int RefreshIntervalMs = 3_000;
    internal const int StaleAfterMs = 6_000;

    private readonly Func<DiagnosticsSnapshot> reader;
    private readonly int refreshIntervalMs;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Thread worker;
    private DiagnosticsSnapshot current = DiagnosticsSnapshot.Unavailable("诊断尚未运行");

    internal DiagnosticsSnapshot Current => current;

    internal DiagnosticsMonitor(Func<DiagnosticsSnapshot> reader, int refreshIntervalMs = RefreshIntervalMs)
    {
        this.reader = reader;
        this.refreshIntervalMs = refreshIntervalMs;
        worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "PhoneDeckDiagnostics"
        };
    }

    internal void Start()
    {
        if (!worker.IsAlive)
        {
            worker.Start();
        }
    }

    internal bool Running => worker.IsAlive;

    /// <summary>强制刷新一次（/api/diagnostics 使用）。超时不取消底层读取，
    /// 只是停止等待并返回当前快照；读取仍由信号量保证单线程执行。</summary>
    internal async Task<DiagnosticsSnapshot> RefreshAsync(int timeoutMs)
    {
        if (!await refreshGate.WaitAsync(0))
        {
            return current;
        }
        try
        {
            var read = Task.Run(() =>
            {
                try
                {
                    current = reader();
                }
                catch (Exception exception)
                {
                    current = current with { LastError = exception.Message };
                }
                return current;
            });
            var winner = await Task.WhenAny(read, Task.Delay(timeoutMs));
            return winner == read
                ? await read
                : current with { LastError = "诊断刷新超时" };
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private void Run()
    {
        while (!cancellation.IsCancellationRequested)
        {
            if (refreshGate.Wait(0))
            {
                try
                {
                    try
                    {
                        current = reader();
                    }
                    catch (Exception exception)
                    {
                        current = current with { LastError = exception.Message };
                    }
                }
                finally
                {
                    refreshGate.Release();
                }
            }
            try
            {
                Task.Delay(refreshIntervalMs, cancellation.Token).Wait(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        refreshGate.Dispose();
        cancellation.Dispose();
    }
}
