using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.FirstOrDefault() == "--apply-fleet-update")
{
    Environment.ExitCode = await FleetUpdateWorker.Run(args);
    return;
}

using var singleInstance = new Mutex(initiallyOwned: true, "PhoneDeck.Server.Singleton", out var isFirstInstance);
if (!isFirstInstance)
{
    Console.WriteLine("手机键盘电脑端已经在运行。");
    return;
}

var receiverIdentity = ReceiverIdentity.LoadOrCreate();
using var lanIdentity = LanIdentity.LoadOrCreate(receiverIdentity.ComputerId);
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 64 * 1024;
    options.ListenLocalhost(8765, listen => listen.Protocols = HttpProtocols.Http1);
    options.ListenAnyIP(lanIdentity.HttpsPort, listen =>
    {
        listen.Protocols = HttpProtocols.Http1;
        listen.UseHttps(lanIdentity.Certificate);
    });
});

var app = builder.Build();
var serverSettings = ServerSettings.LoadOrCreate();
var fleetUpdates = new FleetUpdates();
using var audioBridge = new PhoneAudioBridge();
using var dictationSessions = new DictationSessionManager(audioBridge);
using var usbWatchdog = new UsbWatchdog(serverSettings.AdbPath);
using var lanDiscovery = new LanDiscoveryResponder(
    receiverIdentity,
    lanIdentity.HttpsPort);
using var diagnostics = new DiagnosticsMonitor(() =>
{
    var engine = VoiceEngines.Active;
    return new DiagnosticsSnapshot
    {
        CheckedAtMs = Environment.TickCount64,
        Engine = VoiceEngines.BuildSnapshot(
            VoiceEngineStateProbe.IsCapturing(engine)),
        VirtualCableDevice = audioBridge.FindVirtualCable(),
        ForegroundApp = KeyboardInput.ForegroundAppName()
    };
});
diagnostics.Start();
await using var bluetoothReceiver = new BluetoothReceiver(
    receiverIdentity.ComputerId,
    receiverIdentity.DisplayName);
bluetoothReceiver.Start(app.Lifetime.ApplicationStopping);
if (serverSettings.UsbWatchdog)
{
    usbWatchdog.Start();
}
else
{
    Console.WriteLine("USB 看门狗已在 server-settings.json 中关闭。");
}
if (serverSettings.LanDiscovery)
{
    lanDiscovery.Start();
}
else
{
    Console.WriteLine("局域网发现在 server-settings.json 中关闭。");
}

app.Use(async (context, next) =>
{
    if (!LanRequestAuthenticator.IsAuthorized(
            context.Connection.LocalPort,
            context.Request.Headers["X-PhoneDeck-Token"].FirstOrDefault(),
            lanIdentity.AccessToken))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new
        {
            ok = false,
            error = "局域网连接尚未配对或密钥无效"
        });
        return;
    }
    await next();
});

app.Use(async (context, next) =>
{
    var isUse = HttpMethods.IsPost(context.Request.Method)
        && !context.Request.Path.StartsWithSegments("/api/updates");
    if (!isUse) { await next(); return; }
    if (!fleetUpdates.EnterUse())
    {
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new { ok = false, error = "正在更新，请稍后重试" });
        return;
    }
    try { await next(); }
    finally { fleetUpdates.ExitUse(); }
});
fleetUpdates.Map(app, receiverIdentity.ComputerId, () => audioBridge.IsStreaming
    || dictationSessions.IsActive || diagnostics.Current.Engine?.Capturing == true);

app.MapGet("/api/health", () =>
{
    // 只读后台诊断快照与易变内存状态：零文件 IO、零 Core Audio 枚举、
    // 零跨线程锁等待，保证即使 Typeless 卡死也持续快速响应。
    var snapshot = diagnostics.Current;
    var ageMs = unchecked(Environment.TickCount64 - snapshot.CheckedAtMs);
    var stale = ageMs > DiagnosticsMonitor.StaleAfterMs;
    var engine = snapshot.Engine;
    return Results.Ok(new
    {
        ok = true,
        name = "PhoneDeck",
        version = FleetUpdates.Version,
        updates = fleetUpdates.Health,
        protocolVersion = 2,
        computerId = receiverIdentity.ComputerId,
        displayName = receiverIdentity.DisplayName,
        platform = receiverIdentity.Platform,
        architecture = receiverIdentity.Architecture,
        capabilities = new[]
        {
            "fixedAction", "keyChord", "text", "macro", "phoneAudio",
            "managedDictation", "sharedMicrophone", "secureLan", "fleetUpdatesV1"
        },
        audio = new
        {
            available = snapshot.AudioAvailable,
            device = snapshot.VirtualCableDevice,
            streaming = audioBridge.IsStreaming,
            sessionId = audioBridge.ActiveSessionId,
            mode = audioBridge.ActiveMode,
            checkedAtMs = snapshot.CheckedAtMs,
            ageMs,
            lastError = snapshot.LastError,
            stale
        },
        dictation = new
        {
            active = dictationSessions.IsActive,
            sessionId = dictationSessions.ActiveSessionId
        },
        foregroundApp = snapshot.ForegroundApp,
        usbWatchdog = new
        {
            enabled = serverSettings.UsbWatchdog,
            running = usbWatchdog.Running,
            adbFound = usbWatchdog.AdbPath is not null,
            restoreCount = usbWatchdog.RestoreCount,
            lastRestoredAt = usbWatchdog.LastRestoredAt
        },
        // 遗留 typeless 块：从当前引擎映射生成，供尚未升级的旧手机端继续
        // 读取；新手机端应优先读 voiceEngine 块（含引擎 id 与完整模式列表）。
        typeless = new
        {
            capturing = engine?.Capturing,
            virtualCableSelected = engine?.UsesVirtualCable,
            microphone = engine?.Microphone,
            shortcuts = new
            {
                dictation = LegacyModeKeys(engine, "dictation"),
                translation = LegacyModeKeys(engine, "translation"),
                ask = LegacyModeKeys(engine, "ask")
            },
            checkedAtMs = snapshot.CheckedAtMs,
            ageMs,
            lastError = snapshot.LastError,
            stale
        },
        voiceEngine = new
        {
            id = engine?.Id,
            displayName = engine?.DisplayName,
            experimental = engine?.Experimental ?? false,
            capturing = engine?.Capturing,
            // null 表示该引擎无可读配置、无法校验（手机端不应据此阻断）。
            virtualCableSelected = engine?.UsesVirtualCable,
            microphone = engine?.Microphone,
            modes = engine?.Modes.Select(mode => new
            {
                id = mode.Id,
                label = mode.Label,
                trigger = mode.Trigger,
                configured = mode.Configured,
                keys = mode.Keys
            }).ToArray(),
            checkedAtMs = snapshot.CheckedAtMs,
            ageMs,
            lastError = snapshot.LastError,
            stale
        },
        shared = new
        {
            requested = serverSettings.SharedRequested
        }
    });
});

app.MapGet("/api/config/agent-shortcuts", () =>
{
    var settings = AgentShortcutSettings.LoadOrCreate();
    return Results.Json(new
    {
        ok = true,
        schemaVersion = settings.SchemaVersionValue,
        updatedAt = settings.UpdatedAt,
        buttons = settings.Buttons.Select(button => new
        {
            id = button.Id,
            label = button.Label,
            text = button.Text,
            submit = button.Submit,
            visible = button.Visible
        })
    });
});

// 共享麦克风联动开关：控制台界面或 Ctrl+Alt+M 热键从 loopback 调用；
// 局域网调用仍需令牌。手机轮询 /api/health 里的 shared.requested 自动跟随，
// 持久化到 server-settings.json，电脑重启后手机会重新自动开启。
void PersistServerSettings()
{
    try
    {
        var settingsPath = Path.Combine(PhoneDeckDataDirectory.Get(), "server-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(serverSettings,
            new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (Exception exception)
    {
        Console.WriteLine("写入 server-settings.json 失败：" + exception.Message);
    }
}

app.MapPost("/api/shared/request", (SharedMicrophoneRequest command) =>
{
    serverSettings.SharedRequested = command.Requested;
    PersistServerSettings();
    Console.WriteLine(command.Requested
        ? "已请求手机开启共享麦克风（联动）。"
        : "已取消共享麦克风联动请求。");
    return Results.Ok(new
    {
        ok = true,
        requested = serverSettings.SharedRequested
    });
});

// 语音引擎配置：控制台读取可用引擎列表、写入选择与快捷键覆盖。
// 保存后由控制台重启接收端生效（档案目录启动时加载一次）。
app.MapGet("/api/config/voice-engines", () =>
{
    var catalog = VoiceEngines.Catalog;
    return Results.Ok(new
    {
        ok = true,
        activeEngine = catalog.Active.Id,
        engines = catalog.Profiles.Select(profile => new
        {
            id = profile.Id,
            displayName = profile.DisplayName,
            experimental = profile.Experimental,
            verifiesMicrophone = profile.VerifiesMicrophone,
            processNames = profile.ProcessNames,
            modes = profile.Modes.Select(mode => new
            {
                id = mode.Id,
                label = mode.Label ?? mode.Id,
                trigger = mode.Trigger,
                defaultKeys = mode.Keys,
                overrideKeys = catalog.Settings.ShortcutOverrideFor(profile.Id, mode.Id)
            }).ToArray()
        }).ToArray()
    });
});

app.MapPost("/api/config/voice-engines", (VoiceEngineConfigRequest request) =>
{
    var catalog = VoiceEngines.Catalog;
    var activeId = request.ActiveEngine?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(activeId) || catalog.Find(activeId) is null)
    {
        return Results.BadRequest(new { ok = false, error = $"未知引擎：{request.ActiveEngine}" });
    }
    if (request.ShortcutOverrides is not null)
    {
        foreach (var (engineId, engineOverrides) in request.ShortcutOverrides)
        {
            if (engineOverrides is null)
            {
                continue;
            }
            foreach (var (modeId, binding) in engineOverrides)
            {
                if (string.IsNullOrWhiteSpace(binding))
                {
                    continue;
                }
                if (KeyboardInput.ParseBindingKeys(binding) is null)
                {
                    return Results.BadRequest(new
                    {
                        ok = false,
                        error = $"引擎 {engineId} 模式 {modeId} 的快捷键无法识别：{binding}"
                    });
                }
            }
        }
    }
    try
    {
        var settings = catalog.Settings;
        settings.ActiveEngine = activeId;
        settings.ShortcutOverrides = request.ShortcutOverrides
            ?? new Dictionary<string, Dictionary<string, string>>();
        VoiceEngineSettings.Save(settings);
        Console.WriteLine($"语音引擎设置已更新：{activeId}（重启接收端后生效）");
        return Results.Ok(new { ok = true, activeEngine = activeId });
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"保存语音引擎设置失败：{exception.Message}");
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/diagnostics", async () =>
{
    // 深诊断：强制刷新一次（带超时），不阻塞 health、音频或快捷键请求。
    var snapshot = await diagnostics.RefreshAsync(2_000);
    var ageMs = unchecked(Environment.TickCount64 - snapshot.CheckedAtMs);
    var engine = snapshot.Engine;
    return Results.Ok(new
    {
        ok = true,
        computerId = receiverIdentity.ComputerId,
        displayName = receiverIdentity.DisplayName,
        checkedAtMs = snapshot.CheckedAtMs,
        ageMs,
        lastError = snapshot.LastError,
        typeless = new
        {
            capturing = engine?.Capturing,
            virtualCableSelected = engine?.UsesVirtualCable,
            microphone = engine?.Microphone,
            shortcuts = new
            {
                dictation = LegacyModeKeys(engine, "dictation"),
                translation = LegacyModeKeys(engine, "translation"),
                ask = LegacyModeKeys(engine, "ask")
            }
        },
        voiceEngine = new
        {
            id = engine?.Id,
            displayName = engine?.DisplayName,
            experimental = engine?.Experimental ?? false,
            capturing = engine?.Capturing,
            virtualCableSelected = engine?.UsesVirtualCable,
            microphone = engine?.Microphone,
            modes = engine?.Modes.Select(mode => new
            {
                id = mode.Id,
                label = mode.Label,
                trigger = mode.Trigger,
                configured = mode.Configured,
                keys = mode.Keys
            }).ToArray()
        },
        audio = new
        {
            available = snapshot.AudioAvailable,
            device = snapshot.VirtualCableDevice,
            streaming = audioBridge.IsStreaming,
            sessionId = audioBridge.ActiveSessionId,
            mode = audioBridge.ActiveMode
        },
        lan = new
        {
            httpsPort = lanIdentity.HttpsPort,
            candidateAddresses = lanIdentity.GetCandidateAddresses(),
            discovery = new
            {
                enabled = serverSettings.LanDiscovery,
                portBound = lanDiscovery.PortBound,
                running = lanDiscovery.Running
            }
        },
        usbWatchdog = new
        {
            enabled = serverSettings.UsbWatchdog,
            running = usbWatchdog.Running,
            adbFound = usbWatchdog.AdbPath is not null,
            restoreCount = usbWatchdog.RestoreCount,
            lastRestoredAt = usbWatchdog.LastRestoredAt
        }
    });
});

app.MapPost("/api/lan/pair", (HttpContext context) =>
{
    if (!LanRequestAuthenticator.IsUsbPairingRequest(
            context.Connection.LocalPort,
            context.Connection.RemoteIpAddress))
    {
        return Results.NotFound();
    }
    return Results.Ok(new
    {
        ok = true,
        protocolVersion = 2,
        computerId = receiverIdentity.ComputerId,
        displayName = receiverIdentity.DisplayName,
        platform = receiverIdentity.Platform,
        addresses = lanIdentity.GetCandidateAddresses(),
        port = lanIdentity.HttpsPort,
        certificateSha256 = lanIdentity.CertificateSha256,
        accessToken = lanIdentity.AccessToken
    });
});

app.MapPost("/api/audio/stream", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    var bodySizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (bodySizeFeature is { IsReadOnly: false })
    {
        bodySizeFeature.MaxRequestBodySize = null;
    }
    if (!string.Equals(request.Headers["X-PhoneDeck-Audio"], "pcm-s16le",
            StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { ok = false, error = "不支持的音频格式" });
    }

    int? protocolVersion = null;
    var protocolHeader = request.Headers["X-PhoneDeck-Protocol"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(protocolHeader))
    {
        if (!int.TryParse(protocolHeader, out var parsedProtocolVersion))
        {
            return Results.BadRequest(new { ok = false, error = "无效的 protocolVersion" });
        }
        protocolVersion = parsedProtocolVersion;
    }
    try
    {
        TargetEnvelopeValidator.ValidateProtocolAndTarget(
            protocolVersion,
            request.Headers["X-PhoneDeck-Computer-Id"].FirstOrDefault(),
            receiverIdentity.ComputerId);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { ok = false, error = exception.Message });
    }

    var sessionId = request.Headers["X-PhoneDeck-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(sessionId))
    {
        sessionId = Guid.NewGuid().ToString();
    }
    else if (sessionId.Length > 128 || !Guid.TryParse(sessionId, out _))
    {
        return Results.BadRequest(new { ok = false, error = "无效的音频 sessionId" });
    }

    AudioStreamMode audioMode;
    try
    {
        audioMode = AudioStreamModes.Parse(
            request.Headers["X-PhoneDeck-Audio-Mode"].FirstOrDefault());
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { ok = false, error = exception.Message });
    }

    try
    {
        var bytes = await audioBridge.StreamAsync(
            request.Body,
            sessionId,
            audioMode,
            (endedSessionId, endedMode) =>
            {
                if (endedMode.ControlsTypeless())
                {
                    dictationSessions.AudioEnded(endedSessionId);
                }
            },
            cancellationToken);
        return Results.Ok(new
        {
            ok = true,
            sessionId,
            mode = audioMode.ToWireValue(),
            bytes
        });
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499);
    }
    catch (AudioStreamConflictException exception)
    {
        Console.Error.WriteLine($"音频连接冲突：{exception.Message}");
        return Results.Conflict(new { ok = false, error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        Console.Error.WriteLine($"音频连接失败：{exception.Message}");
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"音频传输失败：{exception.Message}");
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/dictation/start", (DictationCommand command) =>
    ExecuteDictationCommand(() =>
    {
        TargetEnvelopeValidator.Validate(
            command.ProtocolVersion,
            command.RequestId,
            command.SessionId,
            command.TargetComputerId,
            receiverIdentity.ComputerId);
        var duplicate = dictationSessions.Start(
            command.SessionId,
            command.RequestId,
            VoiceEngines.NormalizeMode(command.Mode));
        return Results.Ok(new
        {
            ok = true,
            duplicate,
            requestId = command.RequestId,
            sessionId = command.SessionId,
            computerId = receiverIdentity.ComputerId,
            active = true
        });
    }));

app.MapPost("/api/dictation/stop", (DictationCommand command) =>
    ExecuteDictationCommand(() =>
    {
        TargetEnvelopeValidator.Validate(
            command.ProtocolVersion,
            command.RequestId,
            command.SessionId,
            command.TargetComputerId,
            receiverIdentity.ComputerId);
        var duplicate = dictationSessions.Stop(command.SessionId, command.RequestId);
        return Results.Ok(new
        {
            ok = true,
            duplicate,
            requestId = command.RequestId,
            sessionId = command.SessionId,
            computerId = receiverIdentity.ComputerId,
            active = false
        });
    }));

app.MapPost("/api/input", (InputCommand command) =>
{
    try
    {
        var result = InputCommandProcessor.Execute(command, receiverIdentity.ComputerId);
        return Results.Ok(new
        {
            ok = true,
            duplicate = result.Duplicate,
            requestId = command.RequestId,
            computerId = receiverIdentity.ComputerId,
            message = result.Message,
            text = (string?)null
        });
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { ok = false, error = exception.Message });
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"输入失败：{exception.Message}");
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine("========================================");
    Console.WriteLine("  手机键盘电脑端已启动");
    Console.WriteLine("  USB 通道：127.0.0.1:8765");
    Console.WriteLine($"  Wi-Fi 通道：HTTPS {lanIdentity.HttpsPort}（需先通过 USB 配对）");
    Console.WriteLine($"  局域网发现：UDP {LanDiscoveryResponder.DiscoveryPort}" +
        (serverSettings.LanDiscovery ? "（应答中）" : "（已关闭）"));
    Console.WriteLine($"  电脑身份：{receiverIdentity.DisplayName} / {receiverIdentity.ComputerId}");
    Console.WriteLine("  蓝牙通道：正在查找已配对的手机");
    Console.WriteLine($"  手机麦克风：{audioBridge.FindVirtualCable() ?? "未找到 VB-CABLE"}");
    var engineProfile = VoiceEngines.Active;
    Console.WriteLine(
        $"  语音引擎：{engineProfile.DisplayName}{(engineProfile.Experimental ? "（实验性，快捷键/进程名未在真机核实）" : "")}（{engineProfile.Id}）");
    foreach (var mode in engineProfile.Modes)
    {
        var modeKeys = VoiceEngines.ModeKeyNames(mode.Id);
        var triggerName = VoiceEngines.TriggerFor(mode.Id) == EngineTriggers.Hold
            ? "按住式"
            : "切换式";
        Console.WriteLine(
            $"  {engineProfile.DisplayName} {VoiceEngines.LabelOf(mode.Id)}："
            + (modeKeys is null || modeKeys.Length == 0 ? "未配置" : string.Join(" + ", modeKeys))
            + $"（{triggerName}）"
            + (VoiceEngines.IsModeConfigured(mode.Id) ? "" : "（未配置，手机端不显示）"));
    }
    if (engineProfile.VerifiesMicrophone)
    {
        Console.WriteLine(
            $"  {engineProfile.DisplayName} 麦克风：{VoiceEngines.MicrophoneDescription ?? "未读取到配置"}");
    }
    else
    {
        Console.WriteLine(
            $"  麦克风校验：{engineProfile.DisplayName} 无可读配置，请自行确认其输入设备已选择 CABLE Output");
    }
    Console.WriteLine("  保持此窗口运行；按 Ctrl+C 可退出。");
    Console.WriteLine("========================================");
});

try
{
    await app.RunAsync();
}
catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"\n[错误] 接收端服务启动失败：端口可能已被占用（8765 或 {lanIdentity.HttpsPort}）。");
    Console.Error.WriteLine($"详细错误：{exception.Message}");
    Console.ResetColor();
    Environment.ExitCode = 1;
}

static IResult ExecuteDictationCommand(Func<IResult> execute)
{
    try
    {
        return execute();
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { ok = false, error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { ok = false, error = exception.Message });
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"语音听写会话处理失败：{exception.Message}");
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
}

/// <summary>旧 typeless 健康块只暴露 dictation/translation/ask 三个槽位；
/// 从当前引擎快照按 id 取，引擎没有该模式时为 null。</summary>
static string[]? LegacyModeKeys(VoiceEngineSnapshot? engine, string modeId)
{
    var mode = engine?.Modes.FirstOrDefault(
        candidate => string.Equals(candidate.Id, modeId, StringComparison.Ordinal));
    return mode?.Keys;
}

internal sealed record InputCommand(
    int? ProtocolVersion,
    string? Action,
    string? Text,
    string? RequestId,
    string? SessionId,
    string? TargetComputerId,
    string[]? Keys,
    int? HoldMs,
    MacroStep[]? Steps);
internal sealed record MacroStep(
    string? Type,
    string[]? Keys,
    int? HoldMs,
    string? Text,
    bool? Submit,
    int? DelayBeforeMs);
internal sealed record DictationCommand(
    int? ProtocolVersion,
    string? SessionId,
    string? RequestId,
    string? TargetComputerId,
    string? Mode);
internal sealed record SharedMicrophoneRequest(bool Requested);
internal sealed record VoiceEngineConfigRequest(
    string? ActiveEngine,
    Dictionary<string, Dictionary<string, string>>? ShortcutOverrides);

internal static class KeyboardInput
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, long> RecentRequestIds =
        new(StringComparer.Ordinal);
    private const long RequestIdLifetimeMilliseconds = 30_000;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    private const ushort VkBack = 0x08;
    private const ushort VkTab = 0x09;
    private const ushort VkReturn = 0x0D;
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkEscape = 0x1B;
    private const ushort VkSpace = 0x20;
    private const ushort VkLeft = 0x25;
    private const ushort VkUp = 0x26;
    private const ushort VkRight = 0x27;
    private const ushort VkDown = 0x28;
    private const ushort VkSnapshot = 0x2C;
    private const ushort VkInsert = 0x2D;
    private const ushort VkDelete = 0x2E;
    private const ushort VkHome = 0x24;
    private const ushort VkEnd = 0x23;
    private const ushort VkPageUp = 0x21;
    private const ushort VkPageDown = 0x22;
    private const ushort VkLWin = 0x5B;
    private const ushort VkLeftShift = 0xA0;
    private const ushort VkRightShift = 0xA1;
    private const ushort VkLeftControl = 0xA2;
    private const ushort VkRightControl = 0xA3;
    private const ushort VkLeftMenu = 0xA4;
    private const ushort VkRightMenu = 0xA5;
    private const ushort VkVolumeMute = 0xAD;
    private const ushort VkVolumeDown = 0xAE;
    private const ushort VkVolumeUp = 0xAF;
    private const ushort VkMediaNext = 0xB0;
    private const ushort VkMediaPrevious = 0xB1;
    private const ushort VkMediaPlayPause = 0xB3;

    /// <summary>把 "CTRL+SHIFT+S" 这类引擎快捷键绑定串规范化为键名数组；空绑定返回 null。</summary>
    internal static string[]? BindingKeyNames(string? binding)
    {
        if (string.IsNullOrWhiteSpace(binding))
        {
            return null;
        }
        var modifiers = new List<string>();
        var baseKeys = new List<string>();
        foreach (var token in binding.Split('+',
                     StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = token.Replace("_", "", StringComparison.Ordinal)
                .Replace("-", "", StringComparison.Ordinal)
                .Trim()
                .ToLowerInvariant();
            string? canonical = normalized switch
            {
                "shift" or "leftshift" or "rightshift" => "SHIFT",
                "ctrl" or "control" or "leftctrl" or "leftcontrol"
                    or "rightctrl" or "rightcontrol" => "CTRL",
                "alt" or "menu" or "leftalt" => "ALT",
                "win" or "windows" => "WIN",
                "rightalt" => "RightAlt",
                _ => null
            };
            if (canonical is not null)
            {
                if (canonical == "RightAlt")
                {
                    baseKeys.Add(canonical);
                }
                else
                {
                    modifiers.Add(canonical);
                }
                continue;
            }
            if (token.Length == 1 && char.IsLetterOrDigit(token[0]))
            {
                baseKeys.Add(char.ToUpperInvariant(token[0]).ToString());
                continue;
            }
            if (normalized.Length > 1 && normalized[0] == 'f'
                && int.TryParse(normalized[1..], out var functionNumber)
                && functionNumber is >= 1 and <= 24)
            {
                baseKeys.Add($"F{functionNumber}");
                continue;
            }
            baseKeys.Add(token);
        }
        return modifiers.Concat(baseKeys).ToArray();
    }

    internal static string? ForegroundAppName()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return null;
            }
            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
            {
                return null;
            }
            // 只回传进程名，不读取窗口标题，避免泄露窗口内容。
            return Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static void Execute(string action, string? text)
    {
        ExecuteOnce(action, text, null);
    }

    internal static bool ExecuteOnce(string action, string? text, string? requestId)
    {
        return ExecuteOnceCore(requestId, () => ExecuteCore(action, text));
    }

    internal static bool ExecuteKeyChordOnce(
        string[]? keyNames,
        int? requestedHoldMilliseconds,
        string? requestId,
        out string description)
    {
        var keys = ParseKeyChord(keyNames, out description);
        var holdMilliseconds = requestedHoldMilliseconds ?? 45;
        if (holdMilliseconds is < 20 or > 500)
        {
            throw new ArgumentException("holdMs 必须在 20–500 毫秒之间");
        }
        return ExecuteOnceCore(requestId,
            () => SendChordSafely(holdMilliseconds, keys));
    }

    internal static bool ExecuteMacroOnce(
        MacroStep[]? steps,
        string? requestId,
        out string description)
    {
        var plan = ParseMacroSteps(steps, out description);
        return ExecuteOnceCore(requestId, () => RunMacro(plan));
    }

    /// 仅供单元测试：完整校验宏步骤但不执行。
    internal static void ValidateMacroSteps(MacroStep[]? steps)
    {
        _ = ParseMacroSteps(steps, out _);
    }

    private static List<MacroStepPlan> ParseMacroSteps(
        MacroStep[]? steps, out string description)
    {
        if (steps is null || steps.Length is < 1 or > 8)
        {
            throw new ArgumentException("宏必须包含 1–8 个步骤");
        }
        var plan = new List<MacroStepPlan>(steps.Length);
        var descriptions = new List<string>(steps.Length);
        foreach (var step in steps)
        {
            var delay = step.DelayBeforeMs ?? 0;
            if (delay is < 0 or > 2000)
            {
                throw new ArgumentException("宏步骤延迟必须在 0–2000 毫秒之间");
            }
            var prefix = delay > 0 ? $"等{delay}ms·" : "";
            var type = (step.Type ?? "").Trim().ToLowerInvariant();
            if (type == "keychord")
            {
                var keys = ParseKeyChord(step.Keys, out var keyDescription);
                var hold = step.HoldMs ?? 45;
                if (hold is < 20 or > 500)
                {
                    throw new ArgumentException("holdMs 必须在 20–500 毫秒之间");
                }
                plan.Add(new MacroStepPlan(delay, keys, hold, null, false));
                descriptions.Add(prefix + keyDescription);
            }
            else if (type == "text")
            {
                if (string.IsNullOrEmpty(step.Text) || step.Text.Length > 4096)
                {
                    throw new ArgumentException("宏的文本步骤内容无效");
                }
                plan.Add(new MacroStepPlan(delay, Array.Empty<ushort>(), 0, step.Text,
                    step.Submit ?? false));
                descriptions.Add(prefix + "输入文本" + (step.Submit == true ? "并回车" : ""));
            }
            else
            {
                throw new ArgumentException($"未知的宏步骤类型：{step.Type}");
            }
        }
        description = string.Join(" → ", descriptions);
        return plan;
    }

    private static void RunMacro(List<MacroStepPlan> plan)
    {
        foreach (var step in plan)
        {
            if (step.DelayMs > 0)
            {
                Thread.Sleep(step.DelayMs);
            }
            if (step.Text is not null)
            {
                SendText(step.Text);
                if (step.Submit)
                {
                    Thread.Sleep(45);
                    SendKey(VkReturn);
                }
            }
            else
            {
                SendChordSafely(step.HoldMs, step.Keys);
            }
        }
    }

    private sealed record MacroStepPlan(
        int DelayMs,
        ushort[] Keys,
        int HoldMs,
        string? Text,
        bool Submit);

    private static bool ExecuteOnceCore(string? requestId, Action execute)
    {
        lock (SyncRoot)
        {
            var normalizedRequestId = string.IsNullOrWhiteSpace(requestId)
                ? null
                : requestId.Trim();
            if (normalizedRequestId is { Length: > 128 })
            {
                throw new ArgumentException("requestId 过长");
            }

            var now = Environment.TickCount64;
            if (normalizedRequestId is not null)
            {
                RemoveExpiredRequestIds(now);
                if (RecentRequestIds.ContainsKey(normalizedRequestId))
                {
                    return true;
                }
            }

            execute();
            if (normalizedRequestId is not null)
            {
                RecentRequestIds[normalizedRequestId] = now;
            }
            return false;
        }
    }

    private static void RemoveExpiredRequestIds(long now)
    {
        foreach (var requestId in RecentRequestIds
                     .Where(pair => now - pair.Value > RequestIdLifetimeMilliseconds)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            RecentRequestIds.Remove(requestId);
        }
    }

    private static ushort[] ParseKeyChord(string[]? keyNames, out string description)
    {
        if (keyNames is null || keyNames.Length is < 1 or > 4)
        {
            throw new ArgumentException("组合键必须包含 1–4 个键");
        }

        var normalizedNames = new List<string>(keyNames.Length);
        var mappedKeys = new List<ushort>(keyNames.Length);
        foreach (var rawName in keyNames)
        {
            var normalized = NormalizeKeyName(rawName);
            if (normalizedNames.Contains(normalized, StringComparer.Ordinal))
            {
                throw new ArgumentException($"组合键包含重复键：{normalized}");
            }
            normalizedNames.Add(normalized);
            mappedKeys.Add(MapKeyName(normalized));
        }

        var ordinaryKeyCount = mappedKeys.Count(key => !IsModifier(key));
        if (ordinaryKeyCount != 1)
        {
            throw new ArgumentException("组合键必须且只能包含一个普通键");
        }

        var ordered = normalizedNames
            .Zip(mappedKeys)
            .OrderBy(pair => IsModifier(pair.Second) ? ModifierOrder(pair.Second) : 10)
            .ToArray();
        description = string.Join(" + ", ordered.Select(pair => pair.First));
        return ordered.Select(pair => pair.Second).ToArray();
    }

    private static string NormalizeKeyName(string? keyName)
    {
        var normalized = keyName?.Trim().Replace(" ", string.Empty,
            StringComparison.Ordinal).Replace("_", string.Empty,
            StringComparison.Ordinal).Replace("-", string.Empty,
            StringComparison.Ordinal).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 24)
        {
            throw new ArgumentException("组合键包含无效按键名称");
        }
        return normalized == "PRIMARY" ? "CTRL" : normalized;
    }

    private static ushort MapKeyName(string keyName)
    {
        if (keyName.Length == 1 && char.IsLetterOrDigit(keyName[0]))
        {
            return keyName[0];
        }
        if (keyName.Length > 1 && keyName[0] == 'F'
            && int.TryParse(keyName[1..], out var functionNumber)
            && functionNumber is >= 1 and <= 24)
        {
            return (ushort)(0x6F + functionNumber);
        }

        return keyName switch
        {
            "CTRL" or "CONTROL" => VkControl,
            "SHIFT" => VkShift,
            "ALT" => VkMenu,
            "WIN" or "WINDOWS" => VkLWin,
            "BACKTICK" or "`" => 0xC0,
            "ENTER" or "RETURN" => VkReturn,
            "ESC" or "ESCAPE" => VkEscape,
            "TAB" => VkTab,
            "SPACE" or "SPACEBAR" => VkSpace,
            "BACKSPACE" => VkBack,
            "DELETE" => VkDelete,
            "INSERT" => VkInsert,
            "HOME" => VkHome,
            "END" => VkEnd,
            "PAGEUP" => VkPageUp,
            "PAGEDOWN" => VkPageDown,
            "UP" => VkUp,
            "DOWN" => VkDown,
            "LEFT" => VkLeft,
            "RIGHT" => VkRight,
            "PRINTSCREEN" or "PRTSC" => VkSnapshot,
            "VOLUMEUP" => VkVolumeUp,
            "VOLUMEDOWN" => VkVolumeDown,
            "VOLUMEMUTE" or "MUTE" => VkVolumeMute,
            "MEDIAPLAYPAUSE" or "PLAYPAUSE" => VkMediaPlayPause,
            "MEDIANEXT" or "NEXTTRACK" => VkMediaNext,
            "MEDIAPREVIOUS" or "PREVIOUSTRACK" => VkMediaPrevious,
            _ => throw new ArgumentException($"不支持的按键：{keyName}")
        };
    }

    private static int ModifierOrder(ushort key) => key switch
    {
        VkControl or VkLeftControl or VkRightControl => 0,
        VkShift or VkLeftShift or VkRightShift => 1,
        VkMenu or VkLeftMenu or VkRightMenu => 2,
        VkLWin => 3,
        _ => 10
    };

    private static void ExecuteCore(string action, string? text)
    {
        switch (action)
        {
            case "text":
                if (string.IsNullOrEmpty(text))
                {
                    throw new ArgumentException("没有可输入的文字");
                }
                SendText(text);
                break;
            case "copy": SendChord(VkControl, 'C'); break;
            case "paste": SendChord(VkControl, 'V'); break;
            case "cut": SendChord(VkControl, 'X'); break;
            case "undo": SendChord(VkControl, 'Z'); break;
            case "redo": SendChord(VkControl, 'Y'); break;
            case "selectAll": SendChord(VkControl, 'A'); break;
            case "save": SendChord(VkControl, 'S'); break;
            case "altTab": SendChord(VkMenu, VkTab); break;
            case "taskView": SendChord(VkLWin, VkTab); break;
            case "desktop": SendChord(VkLWin, 'D'); break;
            case "screenshot": SendChord(VkLWin, VkShift, 'S'); break;
            case "typeless":
                {
                    // 遗留协议：旧手机端的 action=typeless，路由到当前激活引擎。
                    // toggle 引擎按一下切换键；hold 引擎退化为按下 55ms 再松开的
                    // 轻触（完整按住语义需要协议 v2 的 /api/dictation 端点）。
                    var legacyMode = string.IsNullOrWhiteSpace(text)
                        ? VoiceEngines.Active.PrimaryModeId
                        : VoiceEngines.NormalizeMode(text);
                    var legacyKeys = VoiceEngines.ResolveKeysOrThrow(legacyMode);
                    if (VoiceEngines.TriggerFor(legacyMode) == EngineTriggers.Hold)
                    {
                        EngineHoldTap(legacyKeys);
                    }
                    else
                    {
                        SendChordWithHold(55, legacyKeys);
                    }
                    break;
                }
            case "switchInputMethod": SendChord(VkLWin, VkSpace); break;
            case "enter": SendKey(VkReturn); break;
            case "backspace": SendKey(VkBack); break;
            case "delete": SendKey(VkDelete); break;
            case "escape": SendKey(VkEscape); break;
            case "space": SendKey(VkSpace); break;
            case "left": SendKey(VkLeft); break;
            case "right": SendKey(VkRight); break;
            case "up": SendKey(VkUp); break;
            case "down": SendKey(VkDown); break;
            case "volumeMute": SendKey(VkVolumeMute); break;
            case "volumeDown": SendKey(VkVolumeDown); break;
            case "volumeUp": SendKey(VkVolumeUp); break;
            default: throw new ArgumentException($"未知操作：{action}");
        }
    }

    /// <summary>解析引擎快捷键绑定串（如 "CTRL+SHIFT+S"）为虚拟键码序列；
    /// 含无法识别的键时返回 null。Typeless 配置读取逻辑已移至
    /// TypelessSettingsReader，引擎绑定解析见 VoiceEngines。</summary>
    internal static ushort[]? ParseBindingKeys(string? binding)
    {
        if (string.IsNullOrWhiteSpace(binding))
        {
            return null;
        }
        var keys = new List<ushort>();
        foreach (var token in binding.Split('+',
                     StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var key = ParseBindingKey(token);
            if (key is null)
            {
                return null;
            }
            keys.Add(key.Value);
        }
        return keys.Count == 0 ? null : keys.ToArray();
    }

    /// <summary>修饰键排前（Ctrl→Shift→Alt→Win），普通键最后；
    /// 与手动组合键的按下顺序约定一致。</summary>
    internal static ushort[] OrderModifiersFirst(ushort[] keys) =>
        keys.OrderByDescending(IsModifier).ToArray();

    private static ushort? ParseBindingKey(string token)
    {
        if (token.Length == 1 && char.IsLetterOrDigit(token[0]))
        {
            return char.ToUpperInvariant(token[0]);
        }

        var normalized = token.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();
        if (normalized.Length > 1 && normalized[0] == 'f'
            && int.TryParse(normalized[1..], out var functionNumber)
            && functionNumber is >= 1 and <= 24)
        {
            return (ushort)(0x6F + functionNumber);
        }

        return normalized switch
        {
            "shift" => VkShift,
            "leftshift" => VkLeftShift,
            "rightshift" => VkRightShift,
            "ctrl" or "control" => VkControl,
            "leftctrl" or "leftcontrol" => VkLeftControl,
            "rightctrl" or "rightcontrol" => VkRightControl,
            "alt" or "menu" => VkMenu,
            "leftalt" => VkLeftMenu,
            "rightalt" => VkRightMenu,
            "win" or "windows" => VkLWin,
            "space" or "spacebar" => VkSpace,
            "tab" => VkTab,
            "enter" or "return" => VkReturn,
            "escape" or "esc" => VkEscape,
            "semicolon" or ";" => 0xBA,
            "comma" or "," => 0xBC,
            "period" or "." => 0xBE,
            "slash" or "/" => 0xBF,
            _ => null
        };
    }

    private static bool IsModifier(ushort key) => key is
        VkShift or VkControl or VkMenu or VkLWin or
        VkLeftShift or VkRightShift or VkLeftControl or VkRightControl or
        VkLeftMenu or VkRightMenu;

    private static void SendChord(params ushort[] keys)
    {
        SendChordSafely(0, keys);
    }

    private static void SendChordWithHold(int holdMilliseconds, params ushort[] keys)
    {
        SendChordSafely(holdMilliseconds, keys);
    }

    private static void SendChordSafely(int holdMilliseconds, IReadOnlyList<ushort> keys)
    {
        var pressedKeys = new List<ushort>(keys.Count);
        try
        {
            foreach (var key in keys)
            {
                pressedKeys.Add(key);
                Send([VirtualKey(key, keyUp: false)]);
            }
            if (holdMilliseconds > 0)
            {
                Thread.Sleep(holdMilliseconds);
            }
        }
        finally
        {
            Exception? releaseFailure = null;
            for (var index = pressedKeys.Count - 1; index >= 0; index--)
            {
                try
                {
                    Send([VirtualKey(pressedKeys[index], keyUp: true)]);
                }
                catch (Exception exception)
                {
                    releaseFailure ??= exception;
                    Console.Error.WriteLine(
                        $"释放按键 0x{pressedKeys[index]:X2} 失败：{exception.Message}");
                }
            }
            if (releaseFailure is not null)
            {
                throw new InvalidOperationException("未能释放全部组合键", releaseFailure);
            }
        }
    }

    private static void SendKey(ushort key)
    {
        Send([VirtualKey(key, keyUp: false), VirtualKey(key, keyUp: true)]);
    }

    /// <summary>引擎切换式触发键（按住 55ms），带 requestId 去重；true 表示重复请求。</summary>
    internal static bool EngineToggleOnce(string? requestId, ushort[] keys) =>
        ExecuteOnceCore(requestId, () => SendChordSafely(55, keys));

    /// <summary>引擎切换式触发键，不去重（服务端内部复位/重试用）。</summary>
    internal static void EngineToggle(ushort[] keys) => SendChordSafely(55, keys);

    /// <summary>引擎按住式触发：按下并保持，带 requestId 去重；
    /// true 表示重复请求（不重复按下）。后续必须用 EngineHoldUp 释放。</summary>
    internal static bool EngineHoldDownOnce(string? requestId, ushort[] keys)
    {
        lock (SyncRoot)
        {
            var normalizedRequestId = string.IsNullOrWhiteSpace(requestId)
                ? null
                : requestId.Trim();
            if (normalizedRequestId is { Length: > 128 })
            {
                throw new ArgumentException("requestId 过长");
            }
            var now = Environment.TickCount64;
            if (normalizedRequestId is not null)
            {
                RemoveExpiredRequestIds(now);
                if (RecentRequestIds.ContainsKey(normalizedRequestId))
                {
                    return true;
                }
            }
            SendChordDown(keys);
            if (normalizedRequestId is not null)
            {
                RecentRequestIds[normalizedRequestId] = now;
            }
            return false;
        }
    }

    /// <summary>释放按住式触发键（倒序、逐键释放）；未按下的键释放为安全空操作。</summary>
    internal static void EngineHoldUp(ushort[] keys) => SendChordUp(keys);

    /// <summary>完整轻触一次按住式触发键：按下、保持、释放（遗留协议兜底）。</summary>
    internal static void EngineHoldTap(ushort[] keys)
    {
        SendChordDown(keys);
        try
        {
            Thread.Sleep(55);
        }
        finally
        {
            SendChordUp(keys);
        }
    }

    /// <summary>按给定顺序按下全部键；中途失败时把已按下的键全部释放再抛出。</summary>
    private static void SendChordDown(IReadOnlyList<ushort> keys)
    {
        var pressedKeys = new List<ushort>(keys.Count);
        try
        {
            foreach (var key in keys)
            {
                pressedKeys.Add(key);
                Send([VirtualKey(key, keyUp: false)]);
            }
        }
        catch (Exception)
        {
            ReleaseChord(pressedKeys, rethrow: false);
            throw;
        }
    }

    /// <summary>倒序释放全部键；任一键释放失败时抛出（按键悬挂必须显式暴露）。</summary>
    private static void SendChordUp(IReadOnlyList<ushort> keys) =>
        ReleaseChord(keys, rethrow: true);

    private static void ReleaseChord(IReadOnlyList<ushort> keys, bool rethrow)
    {
        if (keys.Count == 0)
        {
            return;
        }
        Exception? releaseFailure = null;
        for (var index = keys.Count - 1; index >= 0; index--)
        {
            try
            {
                Send([VirtualKey(keys[index], keyUp: true)]);
            }
            catch (Exception exception)
            {
                releaseFailure ??= exception;
                Console.Error.WriteLine(
                    $"释放按键 0x{keys[index]:X2} 失败：{exception.Message}");
            }
        }
        if (rethrow && releaseFailure is not null)
        {
            throw new InvalidOperationException("未能释放全部组合键", releaseFailure);
        }
    }

    private static void SendText(string text)
    {
        var inputs = new List<Input>(Math.Min(text.Length * 2, 256));
        foreach (var character in text)
        {
            if (character == '\r')
            {
                continue;
            }
            if (character == '\n')
            {
                inputs.Add(VirtualKey(VkReturn, keyUp: false));
                inputs.Add(VirtualKey(VkReturn, keyUp: true));
            }
            else if (character == '\t')
            {
                inputs.Add(VirtualKey(VkTab, keyUp: false));
                inputs.Add(VirtualKey(VkTab, keyUp: true));
            }
            else
            {
                inputs.Add(UnicodeKey(character, keyUp: false));
                inputs.Add(UnicodeKey(character, keyUp: true));
            }

            if (inputs.Count >= 256)
            {
                Send(inputs);
                inputs.Clear();
            }
        }
        Send(inputs);
    }

    private static Input VirtualKey(ushort key, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = key,
                Flags = keyUp ? KeyEventKeyUp : 0
            }
        }
    };

    private static Input UnicodeKey(char character, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                ScanCode = character,
                Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0)
            }
        }
    };

    private static void Send(IReadOnlyCollection<Input> inputs)
    {
        if (inputs.Count == 0)
        {
            return;
        }
        var array = inputs.ToArray();
        var sent = SendInput((uint)array.Length, array, Marshal.SizeOf<Input>());
        if (sent != array.Length)
        {
            throw new InvalidOperationException($"Windows 只接受了 {sent}/{array.Length} 个输入事件");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInput);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInputData Mouse;
        [FieldOffset(0)] public KeyboardInputData Keyboard;
        [FieldOffset(0)] public HardwareInputData Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInputData
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }
}
