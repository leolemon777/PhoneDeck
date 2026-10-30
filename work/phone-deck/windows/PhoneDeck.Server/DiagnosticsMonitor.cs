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

    /// <summary>语音会话进行中的刷新间隔：电脑端停止要在亚秒级同步到手机。</summary>
    internal const int ActiveRefreshIntervalMs = 250;
    internal const int StaleAfterMs = 6_000;

    private readonly Func<DiagnosticsSnapshot> reader;
    private readonly int refreshIntervalMs;
    private readonly Func<bool>? sessionActive;
    private readonly Action<DiagnosticsSnapshot>? refreshed;
    private readonly ManualResetEventSlim wake = new(false);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationToken cancellationToken;
    private readonly Thread worker;
    private readonly object lifecycleGate = new();
    private readonly object callbackGate = new();
    private int disposed;
    private int started;
    private DiagnosticsSnapshot current = DiagnosticsSnapshot.Unavailable("诊断尚未运行");

    /// <summary>
    /// 不可变快照仍需显式 acquire/release 发布；否则健康请求线程可能继续看到旧引用，
    /// 尤其是在后台读线程刚完成昂贵的 Core Audio 枚举时。
    /// </summary>
    internal DiagnosticsSnapshot Current => Volatile.Read(ref current);

    internal DiagnosticsMonitor(
        Func<DiagnosticsSnapshot> reader,
        int refreshIntervalMs = RefreshIntervalMs,
        Func<bool>? sessionActive = null,
        Action<DiagnosticsSnapshot>? refreshed = null)
    {
        this.reader = reader;
        this.refreshIntervalMs = refreshIntervalMs;
        this.sessionActive = sessionActive;
        this.refreshed = refreshed;
        cancellationToken = cancellation.Token;
        worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "PhoneDeckDiagnostics"
        };
    }

    internal void Start()
    {
        lock (lifecycleGate)
        {
            if (Volatile.Read(ref disposed) != 0
                || worker.IsAlive
                || Interlocked.CompareExchange(ref started, 1, 0) != 0)
            {
                return;
            }

            try
            {
                worker.Start();
            }
            catch
            {
                Volatile.Write(ref started, 0);
                throw;
            }
        }
    }

    internal bool Running => worker.IsAlive;

    /// <summary>会话开始等状态变化时立即刷新一次，并切换到会话期间的快速节奏。</summary>
    internal void Nudge()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        try
        {
            wake.Set();
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0)
        {
            // Keep late engine callbacks harmless during shutdown.
        }
    }

    internal int CurrentIntervalMs =>
        Volatile.Read(ref disposed) == 0 && sessionActive?.Invoke() == true
            ? Math.Min(refreshIntervalMs, ActiveRefreshIntervalMs)
            : refreshIntervalMs;

    /// <summary>强制刷新一次（/api/diagnostics 使用）。超时不取消底层读取，
    /// 只是停止等待并返回当前快照；读取仍由信号量保证单线程执行。</summary>
    internal async Task<DiagnosticsSnapshot> RefreshAsync(int timeoutMs)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return Current;
        }

        try
        {
            if (!await refreshGate.WaitAsync(0))
            {
                return Current;
            }
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0)
        {
            return Current;
        }

        var releaseWhenReadCompletes = false;
        try
        {
            // The timeout marker may race the reader's final publication. Use
            // this reference as an optimistic expected value so a completed
            // read cannot be overwritten by a late timeout.
            var expectedBeforeRead = Current;
            var read = Task.Run(() =>
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return Current;
                }

                try
                {
                    return PublishSnapshot(reader());
                }
                catch (Exception exception)
                {
                    return PublishError(exception.Message);
                }
            });
            var winner = await Task.WhenAny(read, Task.Delay(timeoutMs));
            if (winner == read)
            {
                return await read;
            }

            // When the timeout and the reader complete together, prefer the
            // completed read; otherwise a late timeout marker could overwrite
            // a snapshot that was already published successfully.
            if (read.IsCompleted)
            {
                return await read;
            }

            // A timed-out COM/Core Audio read cannot be cancelled safely. Keep
            // refreshGate owned until that reader really exits; otherwise the
            // next refresh can enter the same non-reentrant API concurrently.
            _ = read.ContinueWith(
                static (_, state) => ((DiagnosticsMonitor)state!).ReleaseRefreshGate(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            releaseWhenReadCompletes = true;
            // Keep the old CheckedAtMs: a timeout is not a successful fresh read.
            // Publish the error as well as returning it so /api/health cannot report
            // an apparently healthy old snapshot while the expensive read is stuck.
            return PublishError("诊断刷新超时", expectedBeforeRead);
        }
        finally
        {
            if (!releaseWhenReadCompletes)
            {
                ReleaseRefreshGate();
            }
        }
    }

    private void ReleaseRefreshGate()
    {
        try
        {
            refreshGate.Release();
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0)
        {
            // A timed-out reader may finish after Dispose. Its continuation
            // must not surface an unobserved exception during shutdown.
        }
    }

    private DiagnosticsSnapshot PublishSnapshot(DiagnosticsSnapshot snapshot)
    {
        lock (lifecycleGate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return Volatile.Read(ref current);
            }

            Volatile.Write(ref current, snapshot);
            return snapshot;
        }
    }

    private DiagnosticsSnapshot PublishError(string error, DiagnosticsSnapshot? expected = null)
    {
        lock (lifecycleGate)
        {
            var snapshot = Volatile.Read(ref current);
            if (Volatile.Read(ref disposed) != 0)
            {
                return snapshot;
            }

            if (expected is not null && !ReferenceEquals(snapshot, expected))
            {
                return snapshot;
            }

            snapshot = snapshot with { LastError = error };
            Volatile.Write(ref current, snapshot);
            return snapshot;
        }
    }

    private void NotifyRefreshed(DiagnosticsSnapshot snapshot)
    {
        // Keep the external callback out of lifecycleGate: a callback is allowed
        // to observe or request another refresh without blocking publication.
        lock (callbackGate)
        {
            lock (lifecycleGate)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }
            }

            try
            {
                refreshed?.Invoke(snapshot);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"诊断刷新回调失败：{exception.Message}");
            }
        }
    }

    private void Run()
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool entered;
            try
            {
                entered = refreshGate.Wait(0);
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0)
            {
                break;
            }

            if (entered)
            {
                DiagnosticsSnapshot snapshot;
                try
                {
                    try
                    {
                        snapshot = PublishSnapshot(reader());
                    }
                    catch (Exception exception)
                    {
                        snapshot = PublishError(exception.Message);
                    }
                }
                finally
                {
                    ReleaseRefreshGate();
                }
                NotifyRefreshed(snapshot);
            }
            try
            {
                wake.Wait(CurrentIntervalMs, cancellationToken);
                wake.Reset();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            Volatile.Write(ref disposed, 1);
        }
        // wake 不在此释放：后台线程可能仍在等待它，取消令牌已足以让线程退出。
        cancellation.Cancel();
        // Do not let a late callback touch components that are disposed after
        // this monitor. The callback itself never runs under lifecycleGate.
        lock (callbackGate)
        {
        }
        refreshGate.Dispose();
        cancellation.Dispose();
    }
}
