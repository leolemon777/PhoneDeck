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
}
