using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class DiagnosticsMonitorTests
{
    private static DiagnosticsMonitor StartMonitor(
        Func<DiagnosticsSnapshot> reader,
        out DiagnosticsSnapshot first)
    {
        var monitor = new DiagnosticsMonitor(reader, refreshIntervalMs: 50);
        monitor.Start();
        var deadline = Environment.TickCount64 + 5_000;
        do
        {
            Thread.Sleep(10);
        }
        while (monitor.Current.LastError == "诊断尚未运行"
            && Environment.TickCount64 < deadline);
        first = monitor.Current;
        return monitor;
    }

    [TestMethod]
    public void SnapshotReflectsReaderResults()
    {
        var expected = new DiagnosticsSnapshot
        {
            CheckedAtMs = Environment.TickCount64,
            Engine = new VoiceEngineSnapshot(
                "typeless", "Typeless", false, true, null, true,
                new[] { new EngineModeSnapshot("dictation", "听写", "toggle", true, new[] { "RightAlt" }) }),
            VirtualCableDevice = "CABLE Input (VB-Audio Virtual Cable)"
        };
        var monitor = StartMonitor(() => expected, out var snapshot);
        try
        {
            Assert.IsTrue(snapshot.Engine?.Capturing);
            Assert.AreEqual("typeless", snapshot.Engine?.Id);
            Assert.IsTrue(snapshot.AudioAvailable);
            Assert.IsNull(snapshot.LastError);
        }
        finally
        {
            monitor.Dispose();
        }
    }

    [TestMethod]
    public void ReaderFailureIsCapturedAsLastErrorNotThrown()
    {
        DiagnosticsSnapshot snapshot = new() { CheckedAtMs = 0 };
        var monitor = StartMonitor(
            () => throw new InvalidOperationException("Core Audio 卡死"),
            out snapshot);
        try
        {
            Assert.IsNotNull(snapshot.LastError);
            StringAssert.Contains(snapshot.LastError, "Core Audio");
        }
        finally
        {
            monitor.Dispose();
        }
    }

    [TestMethod]
    public async Task RefreshAsyncReturnsFreshSnapshotAndResilientToSlowReader()
    {
        var version = 0;
        var monitor = new DiagnosticsMonitor(
            () =>
            {
                version++;
                return new DiagnosticsSnapshot
                {
                    CheckedAtMs = Environment.TickCount64,
                    ForegroundApp = $"app-{version}"
                };
            },
            refreshIntervalMs: 60_000);
        try
        {
            var refreshed = await monitor.RefreshAsync(2_000);
            StringAssert.StartsWith(refreshed.ForegroundApp, "app-");
        }
        finally
        {
            monitor.Dispose();
        }
    }

    [TestMethod]
    public async Task TimedOutRefreshDoesNotStartAnotherReaderUntilTheFirstReaderExits()
    {
        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var active = 0;
        var maximumActive = 0;
        var monitor = new DiagnosticsMonitor(
            () =>
            {
                Interlocked.Increment(ref calls);
                var nowActive = Interlocked.Increment(ref active);
                while (true)
                {
                    var previous = Volatile.Read(ref maximumActive);
                    if (nowActive <= previous
                        || Interlocked.CompareExchange(ref maximumActive, nowActive, previous) == previous)
                    {
                        break;
                    }
                }

                entered.TrySetResult(null);
                try
                {
                    release.Task.GetAwaiter().GetResult();
                    return new DiagnosticsSnapshot
                    {
                        CheckedAtMs = Environment.TickCount64,
                        ForegroundApp = $"app-{Volatile.Read(ref calls)}"
                    };
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            refreshIntervalMs: 60_000);

        try
        {
            var firstRefresh = monitor.RefreshAsync(50);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var timedOut = await firstRefresh;
            StringAssert.Contains(timedOut.LastError, "超时");

            // The timeout returns, but the blocking reader still owns the gate.
            // The first timeout was published, so the skipped refresh reports it.
            var second = await monitor.RefreshAsync(50);
            Assert.AreEqual(1, Volatile.Read(ref calls));
            Assert.AreEqual(1, Volatile.Read(ref maximumActive));
            Assert.AreEqual(1, Volatile.Read(ref active));
            Assert.AreEqual("诊断刷新超时", second.LastError);

            release.TrySetResult(null);
            var deadline = Environment.TickCount64 + 5_000;
            while (Volatile.Read(ref calls) < 2 && Environment.TickCount64 < deadline)
            {
                await Task.Delay(10);
                await monitor.RefreshAsync(100);
            }

            Assert.AreEqual(2, Volatile.Read(ref calls));
            Assert.AreEqual(1, Volatile.Read(ref maximumActive));
        }
        finally
        {
            release.TrySetResult(null);
            monitor.Dispose();
        }
    }

    [TestMethod]
    public async Task DisposeWhileTimedOutReaderIsBlockedLetsWorkerExitSafely()
    {
        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new DiagnosticsMonitor(
            () =>
            {
                entered.TrySetResult(null);
                release.Task.GetAwaiter().GetResult();
                return new DiagnosticsSnapshot { CheckedAtMs = Environment.TickCount64 };
            },
            refreshIntervalMs: 60_000);

        try
        {
            var refresh = monitor.RefreshAsync(50);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            monitor.Start();
            Assert.IsTrue(SpinWait.SpinUntil(() => monitor.Running, 5_000));

            var timedOut = await refresh;
            StringAssert.Contains(timedOut.LastError, "超时");

            // Dispose while the timed-out reader still owns refreshGate.
            monitor.Dispose();
            release.TrySetResult(null);

            var deadline = Environment.TickCount64 + 5_000;
            while (monitor.Running && Environment.TickCount64 < deadline)
            {
                await Task.Delay(10);
            }

            Assert.IsFalse(monitor.Running);
        }
        finally
        {
            release.TrySetResult(null);
            monitor.Dispose();
        }
    }
}
