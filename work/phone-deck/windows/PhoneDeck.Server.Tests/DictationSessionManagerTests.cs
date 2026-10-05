using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class DictationSessionManagerTests
{
    [TestMethod]
    public void FailedStartRetryWithSameRequestIdNeverReportsSuccess()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeVoiceEngineController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = Guid.NewGuid().ToString();
        const string requestId = "start-request";

        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(false);
        typeless.IsCapturingResults.Enqueue(false);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(sessionId, requestId, "dictation"));
        Assert.IsFalse(manager.IsActive);

        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(false);
        typeless.IsCapturingResults.Enqueue(false);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(sessionId, requestId, "dictation"));
        Assert.IsFalse(manager.IsActive);
        Assert.AreEqual(1, typeless.ToggleCount,
            "重复 requestId 不能再次切换，也不能绕过真实启动状态检查");
    }

    [TestMethod]
    public void StartRejectsUnavailableStateProbeBeforeToggling()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeVoiceEngineController();
        var manager = new DictationSessionManager(audio, typeless);

        typeless.IsCapturingResults.Enqueue(null);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(Guid.NewGuid().ToString(), "start-request", null));

        StringAssert.Contains(exception.Message, "无法读取");
        Assert.AreEqual(0, typeless.ToggleCount);
        Assert.IsFalse(manager.IsActive);
    }

    [TestMethod]
    public void PreflightFailureDoesNotReserveLeaseOrTurnRetryIntoFalseSuccess()
    {
        var audio = new FakeAudioSessionController();
        var engine = new FakeVoiceEngineController { UsesVirtualCable = false };
        using var manager = new DictationSessionManager(audio, engine);
        var session = Guid.NewGuid().ToString();
        Assert.ThrowsExactly<InvalidOperationException>(() => manager.Start(session, "first", "dictation"));
        Assert.ThrowsExactly<InvalidOperationException>(() => manager.Start(session, "retry", "dictation"));
        Assert.AreEqual(0, engine.ToggleCount);
        engine.UsesVirtualCable = true;
        engine.WaitResults.Enqueue(true);
        Assert.IsFalse(manager.Start(Guid.NewGuid().ToString(), "fixed", "dictation"));
        Assert.IsTrue(manager.IsActive);
    }

    [TestMethod]
    public void FailedBeginReleasesLeaseAndAudioForRetry()
    {
        var audio = new FakeAudioSessionController();
        var engine = new FakeVoiceEngineController { FailBegin = true };
        using var manager = new DictationSessionManager(audio, engine);
        var session = Guid.NewGuid().ToString();
        Assert.ThrowsExactly<InvalidOperationException>(() => manager.Start(session, "first", "dictation"));
        Assert.IsFalse(manager.IsActive);
        Assert.AreEqual(1, audio.StopCalls);
        engine.FailBegin = false;
        engine.WaitResults.Enqueue(true);
        Assert.IsFalse(manager.Start(session, "retry", "dictation"));
        Assert.IsTrue(manager.IsActive);
    }

    [TestMethod]
    public void AudioEndedBuriesOldLeaseAndAllowsImmediateNewSession()
    {
        var audio = new FakeAudioSessionController();
        var engine = new FakeVoiceEngineController();
        using var manager = new DictationSessionManager(audio, engine);
        var previous = StartSuccessfulSession(manager, engine);
        engine.IsCapturingResults.Enqueue(false);
        manager.AudioEnded(previous);
        Assert.ThrowsExactly<InvalidOperationException>(() => manager.Start(previous, "late", "dictation"));
        engine.WaitResults.Enqueue(true);
        var next = Guid.NewGuid().ToString();
        Assert.IsFalse(manager.Start(next, "new", "dictation"));
        Assert.AreEqual(next, manager.ActiveSessionId);
    }

    [TestMethod]
    public void StopDuringWarmupWinsBeforeEngineShortcutAndKeepsTombstone()
    {
        var audio = new FakeAudioSessionController();
        var engine = new FakeVoiceEngineController();
        using var manager = new DictationSessionManager(audio, engine);
        var session = Guid.NewGuid().ToString();
        audio.OnWaitForSession = call => { if (call == 1) manager.Stop(session, "cancel"); };
        Assert.ThrowsExactly<InvalidOperationException>(() => manager.Start(session, "start", "dictation"));
        Assert.ThrowsExactly<InvalidOperationException>(() => manager.Start(session, "late", "dictation"));
        Assert.AreEqual(0, engine.ToggleCount);
        Assert.IsFalse(manager.IsActive);
    }

    [TestMethod]
    public async Task DuplicateStartWaitsForActualEngineConfirmation()
    {
        using var gate = new ManualResetEventSlim(false);
        var audio = new FakeAudioSessionController();
        var engine = new FakeVoiceEngineController { WaitForCapturingGate = gate };
        using var manager = new DictationSessionManager(audio, engine);
        var session = Guid.NewGuid().ToString();
        var first = Task.Run(() => manager.Start(session, "first", "dictation"));
        Task<bool>? duplicate = null;
        try
        {
            Assert.IsTrue(SpinWait.SpinUntil(() => manager.IsActive, 3_000));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            duplicate = Task.Run(() => { entered.SetResult(); return manager.Start(session, "again", "dictation"); });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreNotSame(duplicate, await Task.WhenAny(duplicate, Task.Delay(100)),
                "A reserved lease is not confirmation that the engine started");
        }
        finally
        {
            gate.Set(); await first;
            if (duplicate is not null) await duplicate;
        }
        Assert.IsTrue(await duplicate!);
        Assert.AreEqual(1, engine.ToggleCount);
    }

    [TestMethod]
    public void StartWaitsForConcurrentAudioStreamRegistration()
    {
        var audio = new FakeAudioSessionController
        {
            WaitForSessionResult = true
        };
        var typeless = new FakeVoiceEngineController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = Guid.NewGuid().ToString();

        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(true);

        Assert.IsFalse(manager.Start(sessionId, "start-request", "dictation"));
        Assert.AreEqual(2, audio.WaitForSessionCalls,
            "应先短预热一次，再用完整预算复核音频会话");
        Assert.IsTrue(manager.IsActive);
    }

    [TestMethod]
    public void StartUsesShortAudioWarmupBeforeRequestingTypeless()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeVoiceEngineController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = Guid.NewGuid().ToString();

        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(true);
        audio.OnWaitForSession = call =>
        {
            if (call == 1)
            {
                Assert.AreEqual(0, typeless.ToggleCount,
                    "短预热必须发生在 Typeless 启动键之前");
                Assert.IsTrue(audio.WaitTimeouts[0] <= 250,
                    "预热窗口不得重新变成长串行等待");
            }
            else
            {
                Assert.AreEqual(1, typeless.ToggleCount,
                    "完整音频复核必须发生在 Typeless 启动确认之后");
            }
        };

        Assert.IsFalse(manager.Start(sessionId, "start-request", "dictation"));
        Assert.AreEqual(2, audio.WaitForSessionCalls);
        Assert.AreEqual(1, audio.BeginPlaybackCalls);
    }

    [TestMethod]
    public void MissingAudioAfterFastTypelessStartResetsTypeless()
    {
        var audio = new FakeAudioSessionController
        {
            WaitForSessionResult = false
        };
        var typeless = new FakeVoiceEngineController();
        var manager = new DictationSessionManager(audio, typeless);

        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(true);
        typeless.IsCapturingResults.Enqueue(true);
        typeless.WaitResults.Enqueue(true);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(Guid.NewGuid().ToString(), "start-request", "dictation"));

        StringAssert.Contains(exception.Message, "音频会话不存在");
        Assert.AreEqual(2, typeless.ToggleCount,
            "音频未建立时必须把已快速唤醒的 Typeless 自动关闭");
        Assert.IsFalse(manager.IsActive);
        Assert.AreEqual(0, audio.BeginPlaybackCalls);
    }

    [TestMethod]
    public void StartRejectsUnconfiguredModeBeforeWaitingForAudio()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeVoiceEngineController();
        typeless.ConfiguredModes.Remove("translation");
        var manager = new DictationSessionManager(audio, typeless);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(Guid.NewGuid().ToString(), "start-request", "translation"));

        StringAssert.Contains(exception.Message, "未配置");
        Assert.AreEqual(0, typeless.ToggleCount);
        Assert.AreEqual(0, audio.WaitForSessionCalls,
            "模式未配置时不应等待音频会话登记");
        Assert.IsFalse(manager.IsActive);
    }

    [TestMethod]
    public void StopReusesModeFromStart()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeVoiceEngineController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = Guid.NewGuid().ToString();

        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(true);
        Assert.IsFalse(manager.Start(sessionId, "start-request", "translation"));
        Assert.AreEqual("translation", typeless.LastMode);

        typeless.IsCapturingResults.Enqueue(true);
        typeless.WaitResults.Enqueue(false);
        typeless.IsCapturingResults.Enqueue(false);

        Assert.IsFalse(manager.Stop(sessionId, "stop-request"));
        Assert.AreEqual("translation", typeless.LastMode,
            "停止时必须沿用启动时的模式键，不能用当前选中的模式切换");
    }

    [TestMethod]
    public void StopWithUnavailableProbeReportsFailureButReleasesOwnership()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeVoiceEngineController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = StartSuccessfulSession(manager, typeless);

        typeless.IsCapturingResults.Enqueue(null);
        typeless.WaitResults.Enqueue(null);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Stop(sessionId, "stop-request"));

        StringAssert.Contains(exception.Message, "未被确认");
        Assert.IsFalse(manager.IsActive);
        Assert.AreEqual(1, audio.StopCalls);
        Assert.AreEqual(2, typeless.ToggleCount);
    }

    [TestMethod]
    public void AudioEndedFailureCannotPermanentlyLockNextSession()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeVoiceEngineController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = StartSuccessfulSession(manager, typeless);

        typeless.IsCapturingResults.Enqueue(true);
        typeless.WaitResults.Enqueue(false);
        typeless.IsCapturingResults.Enqueue(true);
        typeless.WaitResults.Enqueue(false);

        manager.AudioEnded(sessionId);

        Assert.IsFalse(manager.IsActive);
        Assert.IsNull(manager.ActiveSessionId);
        Assert.AreEqual(3, typeless.ToggleCount);
    }

    [TestMethod]
    public void StopRechecksStateBeforeSendingSecondToggle()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeVoiceEngineController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = StartSuccessfulSession(manager, typeless);

        typeless.IsCapturingResults.Enqueue(true);
        typeless.WaitResults.Enqueue(false);
        typeless.IsCapturingResults.Enqueue(false);

        var duplicate = manager.Stop(sessionId, "stop-request");

        Assert.IsFalse(duplicate);
        Assert.IsFalse(manager.IsActive);
        Assert.AreEqual(2, typeless.ToggleCount,
            "状态已经停止时不能再发第二个切换键把 Typeless 重新打开");
    }

    private static string StartSuccessfulSession(
        DictationSessionManager manager,
        FakeVoiceEngineController typeless)
    {
        var sessionId = Guid.NewGuid().ToString();
        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(true);
        Assert.IsFalse(manager.Start(sessionId, "initial-start-request", "dictation"));
        Assert.IsTrue(manager.IsActive);
        return sessionId;
    }

    [TestMethod]
    public void StartReleasesPreRollOnlyAfterTypelessCapturingConfirmed()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeVoiceEngineController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = Guid.NewGuid().ToString();

        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(true);

        Assert.IsFalse(manager.Start(sessionId, "start-request", "dictation"));
        Assert.AreEqual(1, audio.BeginPlaybackCalls,
            "Typeless 确认采集后必须放行 pre-roll");
    }

    [TestMethod]
    public void FailedStartDoesNotReleasePreRoll()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeVoiceEngineController();
        var manager = new DictationSessionManager(audio, typeless);

        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(false);
        typeless.IsCapturingResults.Enqueue(false);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Start(Guid.NewGuid().ToString(), "start-request", "dictation"));
        Assert.AreEqual(0, audio.BeginPlaybackCalls);
    }

    [TestMethod]
    public void StartUsesColdStartBudgetsForTypelessAndWasapi()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeVoiceEngineController();
        var manager = new DictationSessionManager(audio, typeless);

        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(true);

        Assert.IsFalse(manager.Start(
            Guid.NewGuid().ToString(), "start-request", "dictation"));
        Assert.IsTrue(typeless.LastWaitTimeoutMilliseconds >= 2_000,
            "Typeless 冷启动确认窗口不得退回原 1.2 秒竞态值");
        Assert.IsTrue(audio.LastWaitTimeoutMilliseconds >= 3_000,
            "WASAPI 冷启动登记窗口不得退回原 1.2 秒竞态值");
        Assert.IsTrue(audio.WaitTimeouts[0] <= 250,
            "音频预热只允许使用短窗口，不能拖慢 Typeless 浮窗");
    }

    [TestMethod]
    public void IsActiveAndHealthReadsDoNotBlockDuringSlowStart()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeVoiceEngineController
        {
            WaitForCapturingGate = new ManualResetEventSlim(false)
        };
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = Guid.NewGuid().ToString();

        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(true);
        var startTask = Task.Run(() =>
            manager.Start(sessionId, "start-request", "dictation"));

        var deadline = Environment.TickCount64 + 5_000;
        while (!manager.IsActive && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(10);
        }
        Assert.IsTrue(manager.IsActive, "启动线程应已进入 WaitForCapturing 阶段");

        // 启动线程持有 syncRoot 并阻塞在慢速状态等待中，
        // 无锁读路径（/api/health 的 dictation.active）必须立即返回。
        var stopwatch = Stopwatch.StartNew();
        Assert.IsTrue(manager.IsActive);
        Assert.AreEqual(sessionId, manager.ActiveSessionId);
        stopwatch.Stop();
        Assert.IsTrue(stopwatch.ElapsedMilliseconds < 500,
            $"IsActive 在慢速启动期间耗时 {stopwatch.ElapsedMilliseconds}ms，health 会被拖慢");

        typeless.WaitForCapturingGate.Set();
        Assert.IsTrue(startTask.Wait(5_000));
        Assert.AreEqual(1, audio.BeginPlaybackCalls);
    }

    [TestMethod]
    public void StopWaitsForAudioDrainBeforeTogglingTypeless()
    {
        var audio = new FakeAudioSessionController();
        var typeless = new FakeVoiceEngineController();
        var manager = new DictationSessionManager(audio, typeless);
        var sessionId = Guid.NewGuid().ToString();

        typeless.IsCapturingResults.Enqueue(false);
        typeless.WaitResults.Enqueue(true);
        Assert.IsFalse(manager.Start(sessionId, "start-request", "dictation"));

        typeless.IsCapturingResults.Enqueue(false);
        Assert.IsTrue(manager.Stop(sessionId, "stop-request"));
        Assert.AreEqual(1, audio.WaitForSessionEndCalls,
            "停止 Typeless 前必须等待音频流排空，避免丢失 pre-roll 尾部");
    }

    [TestMethod]
    public void StopKeepsEngineActiveThroughoutFullDrainBudget()
    {
        var audio = new FakeAudioSessionController();
        var engine = new FakeVoiceEngineController();
        var manager = new DictationSessionManager(audio, engine);
        var session = StartSuccessfulSession(manager, engine);
        audio.OnWaitForEnd = timeout =>
        {
            Assert.IsTrue(timeout > PhoneAudioBridge.DrainMaxMs + PhoneAudioBridge.OutputTailMs,
                "The stop request must allow the bridge its full drain and device-tail budget.");
            Assert.AreEqual(1, engine.ToggleCount, "Engine must still be recording during drain.");
        };
        engine.IsCapturingResults.Enqueue(true);
        engine.WaitResults.Enqueue(true);

        manager.Stop(session, "stop");

        Assert.AreEqual(2, engine.ToggleCount);
    }

    [TestMethod]
    public void DrainTimeoutStopsSafelyButDoesNotClaimCompleteAudio()
    {
        var audio = new FakeAudioSessionController { WaitForSessionEndResult = false };
        var engine = new FakeVoiceEngineController();
        var manager = new DictationSessionManager(audio, engine);
        var session = StartSuccessfulSession(manager, engine);
        engine.IsCapturingResults.Enqueue(true);
        engine.WaitResults.Enqueue(true);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => manager.Stop(session, "stop"));

        StringAssert.Contains(error.Message, "尾音");
        Assert.IsFalse(manager.IsActive);
        Assert.AreEqual(1, audio.StopCalls);
        Assert.AreEqual(2, engine.ToggleCount, "Timeout must still release the engine.");
    }

    [TestMethod]
    public void WarmOutputSkipsAudioWarmupGrace()
    {
        var audio = new FakeAudioSessionController { OutputWarm = true };
        var engine = new FakeVoiceEngineController();
        using var manager = new DictationSessionManager(audio, engine);

        manager.Start(Guid.NewGuid().ToString(), "warm", "dictation");

        Assert.AreEqual(0, audio.WaitTimeouts[0], "常驻输出已运行时不应再等待声卡预热");
    }

    [TestMethod]
    public void DesktopStopAfterConfirmationIssuesReceiptOnlyToOwner()
    {
        var audio = new FakeAudioSessionController();
        var engine = new FakeVoiceEngineController();
        using var manager = new DictationSessionManager(audio, engine);
        var session = Guid.NewGuid().ToString();
        manager.Start(session, "start", "dictation", "phone-a");

        // 确认后宽限期内的“未采集”观测是抖动，不作为电脑端停止依据。
        manager.ObserveEngineCapturing(false, Environment.TickCount64);
        Assert.IsNull(manager.Receipts.For("phone-a"));
        manager.ObserveEngineCapturing(true, Environment.TickCount64 + 10_000);
        Assert.IsNull(manager.Receipts.For("phone-a"));
        manager.ObserveEngineCapturing(null, Environment.TickCount64 + 10_000);
        Assert.IsNull(manager.Receipts.For("phone-a"));

        var afterGrace = Environment.TickCount64
            + DictationSessionManager.EngineStopObservationGraceMilliseconds + 50;
        manager.ObserveEngineCapturing(false, afterGrace);
        Assert.IsNull(manager.Receipts.For("phone-a"), "单次未采集可能是探针熔断，不能据此停录");
        manager.ObserveEngineCapturing(true, afterGrace + 50);
        manager.ObserveEngineCapturing(false, afterGrace + 100);
        Assert.IsNull(manager.Receipts.For("phone-a"), "中间出现采集观测时重新计数");
        manager.ObserveEngineCapturing(false,
            afterGrace + 100 + DictationSessionManager.EngineStopConfirmationMilliseconds);

        Assert.AreEqual(session, manager.Receipts.For("phone-a"));
        Assert.IsNull(manager.Receipts.For("phone-b"), "凭据不能泄露给其他手机");
        Assert.IsNull(manager.Receipts.For(null));
    }

    [TestMethod]
    public void UnconfirmedOrEndedSessionNeverIssuesReceipt()
    {
        var audio = new FakeAudioSessionController();
        var engine = new FakeVoiceEngineController();
        using var manager = new DictationSessionManager(audio, engine);
        manager.ObserveEngineCapturing(false, Environment.TickCount64 + 10_000);
        Assert.IsNull(manager.Receipts.For(null));

        var session = Guid.NewGuid().ToString();
        manager.Start(session, "start", "dictation");
        manager.Stop(session, "stop");
        manager.ObserveEngineCapturing(false, Environment.TickCount64 + 10_000);

        Assert.IsNull(manager.Receipts.For(null), "手机自己停止的会话不需要停止凭据");
    }

    [TestMethod]
    public void LocalDesktopStopRecordsReceiptBeforeWaitingForPhoneTail()
    {
        var audio = new FakeAudioSessionController();
        var engine = new FakeVoiceEngineController();
        using var manager = new DictationSessionManager(audio, engine);
        var session = Guid.NewGuid().ToString();
        manager.Start(session, "start", "dictation");
        string? receiptDuringDrain = null;
        audio.OnWaitForEnd = _ => receiptDuringDrain = manager.Receipts.For(null);

        manager.StopFromDesktop();

        Assert.AreEqual(session, receiptDuringDrain, "手机需要在服务端等尾音之前就收到停止凭据");
        Assert.IsFalse(manager.IsActive);
    }

    private sealed class FakeAudioSessionController : IPhoneAudioSessionController
    {
        public bool OutputWarm { get; set; }
        public int StopCalls { get; private set; }
        public int WaitForSessionCalls { get; private set; }
        public int BeginPlaybackCalls { get; private set; }
        public int WaitForSessionEndCalls { get; private set; }
        public int LastWaitTimeoutMilliseconds { get; private set; }
        public List<int> WaitTimeouts { get; } = new();
        public bool WaitForSessionResult { get; set; } = true;
        public bool WaitForSessionEndResult { get; set; } = true;
        public Action<int>? OnWaitForSession { get; set; }
        public Action<int>? OnWaitForEnd { get; set; }

        public bool IsSessionActive(string sessionId) => true;

        public bool WaitForSessionActive(string sessionId, int timeoutMilliseconds)
        {
            WaitForSessionCalls++;
            LastWaitTimeoutMilliseconds = timeoutMilliseconds;
            WaitTimeouts.Add(timeoutMilliseconds);
            OnWaitForSession?.Invoke(WaitForSessionCalls);
            return WaitForSessionResult;
        }

        public bool StopSession(string sessionId)
        {
            StopCalls++;
            return true;
        }

        public void BeginPlayback(string sessionId)
        {
            BeginPlaybackCalls++;
        }

        public bool WaitForSessionEnd(string sessionId, int timeoutMilliseconds)
        {
            WaitForSessionEndCalls++;
            OnWaitForEnd?.Invoke(timeoutMilliseconds);
            return WaitForSessionEndResult;
        }
    }

    private sealed class FakeVoiceEngineController : IVoiceEngineController
    {
        private readonly HashSet<string> requestIds = new(StringComparer.Ordinal);

        public Queue<bool?> IsCapturingResults { get; } = new();
        public Queue<bool?> WaitResults { get; } = new();
        public HashSet<string> ConfiguredModes { get; } = new()
            { "dictation", "translation", "ask" };
        public int ToggleCount { get; private set; }
        public string LastMode { get; private set; } = "dictation";
        public bool UsesVirtualCable { get; set; } = true;
        public bool FailBegin { get; set; }
        public int LastWaitTimeoutMilliseconds { get; private set; }

        public string EngineDisplayName => "Typeless";

        public IReadOnlyCollection<string> Modes =>
            new[] { "dictation", "translation", "ask" };

        public bool? CanVerifyVirtualCable => true;

        /// <summary>非空时 WaitForCapturing 先阻塞在该闸门上，模拟慢速引擎。</summary>
        public ManualResetEventSlim? WaitForCapturingGate { get; set; }

        public bool? IsCapturing() => IsCapturingResults.Count > 0
            ? IsCapturingResults.Dequeue()
            : false;

        public bool? WaitForCapturing(bool expected, int timeoutMilliseconds)
        {
            LastWaitTimeoutMilliseconds = timeoutMilliseconds;
            WaitForCapturingGate?.Wait();
            return WaitResults.Count > 0 ? WaitResults.Dequeue() : expected;
        }

        public bool IsModeConfigured(string mode) => ConfiguredModes.Contains(mode);

        public bool BeginOnce(string requestId, string mode)
        {
            if (FailBegin) throw new InvalidOperationException("Simulated input failure");
            if (!requestIds.Add(requestId))
            {
                return true;
            }
            LastMode = mode;
            ToggleCount++;
            return false;
        }

        public bool End(string mode, string? requestId)
        {
            if (requestId is not null && !requestIds.Add(requestId))
            {
                return true;
            }
            LastMode = mode;
            ToggleCount++;
            return false;
        }

        public void Dispose()
        {
        }
    }
}
