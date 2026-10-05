using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class HealthEventsTests
{
    private static object Health(bool streaming, string? session, bool? capturing, long ageMs = 0) => new
    {
        ok = true,
        audio = new { available = true, streaming, sessionId = session, mode = "managed", ageMs, stopRequestedSessionId = (string?)null },
        dictation = new { active = streaming, sessionId = session },
        voiceEngine = new { capturing, ageMs }
    };

    [TestMethod]
    public void StateVersionTracksVoiceStateButIgnoresDiagnosticAge()
    {
        var idle = HealthEvents.Snapshot(Health(false, null, false, ageMs: 10));
        var idleLater = HealthEvents.Snapshot(Health(false, null, false, ageMs: 2_000));
        var active = HealthEvents.Snapshot(Health(true, "s1", true));
        var stopped = HealthEvents.Snapshot(Health(true, "s1", false));

        Assert.AreEqual(idle["stateVersion"]!.GetValue<string>(), idleLater["stateVersion"]!.GetValue<string>());
        Assert.AreNotEqual(idle["stateVersion"]!.GetValue<string>(), active["stateVersion"]!.GetValue<string>());
        Assert.AreNotEqual(active["stateVersion"]!.GetValue<string>(), stopped["stateVersion"]!.GetValue<string>(),
            "电脑端停止（capturing 变为 false）必须唤醒长轮询");
        Assert.IsTrue(active["audio"]!["streaming"]!.GetValue<bool>(), "返回内容与 /api/health 相同");
    }

    [TestMethod]
    public async Task ReturnsImmediatelyWhenClientIsBehind()
    {
        var started = Environment.TickCount64;
        var snapshot = await HealthEvents.WaitAsync(() => Health(true, "s1", true), "stale-version", 5_000, CancellationToken.None);

        Assert.IsLessThan(500L, Environment.TickCount64 - started);
        Assert.IsNotNull(snapshot["stateVersion"]);
    }

    [TestMethod]
    public async Task WaitsUntilStateChangesThenWakes()
    {
        var capturing = true;
        var current = HealthEvents.Snapshot(Health(true, "s1", true))["stateVersion"]!.GetValue<string>();
        var waiting = HealthEvents.WaitAsync(() => Health(true, "s1", capturing), current, 5_000, CancellationToken.None);
        await Task.Delay(150);
        Assert.IsFalse(waiting.IsCompleted, "状态未变时请求保持挂起");

        capturing = false;
        var started = Environment.TickCount64;
        var snapshot = await waiting;

        Assert.IsLessThan(500L, Environment.TickCount64 - started);
        Assert.IsFalse(snapshot["voiceEngine"]!["capturing"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task TimesOutWithUnchangedSnapshotAndHonorsCancellation()
    {
        var current = HealthEvents.Snapshot(Health(false, null, false))["stateVersion"]!.GetValue<string>();
        var timedOut = await HealthEvents.WaitAsync(() => Health(false, null, false), current, 200, CancellationToken.None);
        Assert.AreEqual(current, timedOut["stateVersion"]!.GetValue<string>());

        using var cancel = new CancellationTokenSource(100);
        var started = Environment.TickCount64;
        await HealthEvents.WaitAsync(() => Health(false, null, false), current, 10_000, cancel.Token);
        Assert.IsLessThan(2_000L, Environment.TickCount64 - started);
        Assert.AreEqual(HealthEvents.DefaultTimeoutMs, HealthEvents.ClampTimeout(null));
        Assert.AreEqual(HealthEvents.MaxTimeoutMs, HealthEvents.ClampTimeout("999999"));
    }
}
