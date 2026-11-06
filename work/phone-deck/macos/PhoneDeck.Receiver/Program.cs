using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using PhoneDeck.MacReceiver;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (!OperatingSystem.IsMacOS())
{
    Console.Error.WriteLine("PhoneDeck macOS 接收端只能在 macOS 运行。");
    return;
}
if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
{
    Console.Error.WriteLine("PhoneDeck 手机音频要求 macOS 14.2 或更高版本。");
    return;
}

// 实时音频线程（WASAPI/AUHAL 回调）不能被长时间阻塞式 GC 打断。
System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
using var singleInstance = new Mutex(
    initiallyOwned: true,
    "PhoneDeck.MacReceiver.Singleton",
    out var isFirstInstance);
if (!isFirstInstance)
{
    Console.WriteLine("PhoneDeck macOS 接收端已经在运行。");
    return;
}
// 只有主实例写日志文件：第二个实例会立即退出，不应覆盖或轮换正在使用的日志。
ReceiverLog.Install(MacLogDirectory());

var capabilities = new[]
{
    "fixedAction", "keyChord", "text", "macro", "secureLan", "macInput",
    "phoneAudio", "sharedMicrophone",
    "phoneManagedSettingsV1", PhoneAudioFormat.Wifi16kCapability
};
// 再次双击 App：已有接收端在跑就只打开它的状态页，不再启动第二个（端口会冲突）。
if (MacLaunch.ReceiverAlreadyRunning())
{
    MacLaunch.OpenStatusPage();
    return;
}
var receiverIdentity = ReceiverIdentity.LoadOrCreate();
using var lanIdentity = LanIdentity.LoadOrCreate(receiverIdentity.ComputerId);
var settings = MacReceiverSettings.LoadOrCreate();
var keyboard = new MacKeyboardInput();
using var audioBridge = new MacPhoneAudioBridge(
    new CoreAudioHalOutputFactory(settings.AudioDeviceUid),
    keepOutputWarm: Environment.GetEnvironmentVariable("PHONEDECK_WARM_AUDIO") != "0");
// 常驻 BlackHole 输出放到后台预热，不阻塞 Kestrel 启动。
// 常驻输出改为按需：手机请求到达时预热，手机空闲 90 秒后关闭（见 MacPhoneAudioBridge.WarmIdleMs）。
using var engineController = new MacVoiceEngineController(keyboard);
var inputProcessor = new InputCommandProcessor(keyboard);
using var usbWatchdog = new UsbWatchdog(settings.AdbPath);
using var lanDiscovery = new LanDiscoveryResponder(
    receiverIdentity,
    lanIdentity.HttpsPort,
    capabilities);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
// 共享音频是不会自行结束的长流；退出时下面的端点会主动收尾，这里只做兜底，
// 避免默认 30 秒等待让重启/升级时新实例抢不到端口。
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    // 编译期元数据优先（原生编译必需），其余匿名应答仍由默认解析器处理。
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, MacApiJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(1, ReceiverApiJsonContext.Default);
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

// Receiver.Core：与 Windows 相同的逐手机凭据、免扫码连接（本机确认）、撤销与 mDNS。
var clientCredentials = new ClientCredentialsStore(
    Path.Combine(PhoneDeckDataDirectory.Get(), "clients.json"));
var clientSessions = new ClientSessionRegistry();
var phonePresence = new PhonePresence();
var pairingWindows = new PairingWindowManager(
    receiverIdentity.ComputerId,
    receiverIdentity.DisplayName,
    lanIdentity.CertificateSha256,
    lanIdentity.HttpsPort);
MacNearbyPrompt.Attach(pairingWindows);
using var mdnsAdvertiser = new MdnsAdvertiser();

if (settings.UsbWatchdog)
{
    usbWatchdog.Start();
}
if (settings.LanDiscovery)
{
    lanDiscovery.Start();
    // mDNS 与 UDP 应答共用“局域网发现”开关；多播不可用时降级为仅 UDP。
    mdnsAdvertiser.Start(receiverIdentity.ComputerId, receiverIdentity.DisplayName,
        receiverIdentity.Platform, lanIdentity.HttpsPort, capabilities);
}

// 8765 回环入口的 Host/Origin 防护：拦截本机恶意网页对配对、撤销等写操作的跨站调用。
app.Use(async (context, next) =>
{
    if (context.Connection.LocalPort == 8765
        && !HttpMethods.IsGet(context.Request.Method)
        && !HttpMethods.IsHead(context.Request.Method))
    {
        var rejection = LoopbackOriginGuard.Validate(
            context.Request.Headers.Host.ToString(),
            context.Request.Headers.Origin.FirstOrDefault(),
            context.Request.Headers.Referer.FirstOrDefault());
        if (rejection is not null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(ApiResult.Fail(rejection), ReceiverApiJsonContext.Default.ApiResult);
            return;
        }
    }
    await next();
});

app.Use(async (context, next) =>
{
    // /api/lan/pair/qr 是凭据自举端点：TLS + 一次性材料 + 本机确认即授权证明。
    var isPairingBootstrap = context.Connection.LocalPort == lanIdentity.HttpsPort
        && HttpMethods.IsPost(context.Request.Method)
        && (context.Request.Path.StartsWithSegments("/api/lan/pair/qr")
            || context.Request.Path.StartsWithSegments("/api/lan/pair/request"));
    var auth = isPairingBootstrap
        ? new LanAuthResult(true, null)
        : LanRequestAuthenticator.Resolve(
            context.Connection.LocalPort,
            context.Request.Headers["X-PhoneDeck-Token"].FirstOrDefault(),
            context.Request.Headers["Authorization"].FirstOrDefault(),
            lanIdentity.AccessToken,
            clientCredentials);
    if (!auth.Authorized)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(ApiResult.Fail("局域网连接尚未配对或密钥无效"),
            ReceiverApiJsonContext.Default.ApiResult);
        return;
    }
    context.Items["ClientId"] = auth.ClientId;
    phonePresence.Observe(context.Connection.LocalPort, auth.ClientId,
        context.Request.Headers.UserAgent.FirstOrDefault(),
        context.Request.Headers.ContainsKey("X-PhoneDeck-Foreground"),
        context.Request.Headers["X-PhoneDeck-Device"].FirstOrDefault());
    await next();
});

// 手机集中设置写入时排斥新的输入/音频请求；进行中的音频流持有使用租约直到收尾。
var configurationGate = new ConfigurationGate();
app.Use(async (context, next) =>
{
    var use = HttpMethods.IsPost(context.Request.Method)
        && !context.Request.Path.StartsWithSegments("/api/config/desktop");
    if (!use) { await next(); return; }
    if (!configurationGate.EnterUse())
    {
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(ApiResult.Fail("正在保存设置，请稍后重试"), ReceiverApiJsonContext.Default.ApiResult);
        return;
    }
    try { await next(); } finally { configurationGate.ExitUse(); }
});
MacDesktopConfigurationEndpoints.Map(app, receiverIdentity.ComputerId, configurationGate,
    () => audioBridge.IsStreaming,
    () => settings, value => settings = value, usbWatchdog, lanDiscovery);

NativePairingEndpoints.Map(app, new NativePairingHost(
    receiverIdentity.ComputerId,
    () => receiverIdentity.DisplayName,
    receiverIdentity.Platform,
    lanIdentity.CertificateSha256,
    lanIdentity.HttpsPort,
    lanIdentity.GetCandidateAddresses,
    lanIdentity.AccessToken,
    clientCredentials,
    clientSessions,
    pairingWindows,
    // macOS 没有跨请求按住的普通按键；撤销后音频长流由 clientSessions 终止。
    _ => { }) { Presence = phonePresence });

// 健康应答用 JsonObject 组装（原生编译不能序列化匿名对象），字段与旧版逐字一致。
void NoteHealthActivity(HttpContext context)
{
    // 新版手机标明是否在前台；后台保活探测（"0"）不预热音频输出。旧版手机不带此头，按前台处理。
    if (context.Request.Headers["X-PhoneDeck-Foreground"].FirstOrDefault() != "0")
    {
        audioBridge.NotePhoneActivity();
    }
}

JsonObject BuildHealth(HttpContext context)
{
    var requesterClientId = context.Items["ClientId"] as string;
    NoteHealthActivity(context);
    var audio = audioBridge.Probe();
    // 只用共享模式：各电脑自己按快捷键，手机不需要知道输入法是否在采集；不再每次健康检查探测 Core Audio。
    bool? capturing = null;
    var engineProfile = MacVoiceEngines.Active;
    var readerConfig = MacTypelessConfiguration.Load(settings);
    bool? virtualCableSelected = engineProfile.VerifiesMicrophone
        ? MacVoiceEngines.UsesVirtualCable
        : null;
    return new JsonObject
    {
        ["ok"] = true,
        ["name"] = "PhoneDeck",
        ["version"] = "2.0.0-dev.4",
        ["protocolVersion"] = 2,
        ["computerId"] = receiverIdentity.ComputerId,
        ["displayName"] = receiverIdentity.DisplayName,
        ["platform"] = receiverIdentity.Platform,
        ["architecture"] = receiverIdentity.Architecture,
        ["capabilities"] = JsonStrings(capabilities),
        ["input"] = new JsonObject
        {
            ["available"] = keyboard.IsAccessibilityTrusted,
            ["backend"] = "CGEvent",
            ["accessibilityTrusted"] = keyboard.IsAccessibilityTrusted
        },
        ["audio"] = new JsonObject
        {
            ["available"] = audio.Available,
            ["device"] = audio.DeviceName,
            ["deviceUid"] = audio.DeviceUid,
            ["streaming"] = audioBridge.IsStreaming,
            // 多手机：会话 ID 只告诉所属手机；其他手机只看到“正在使用”。
            ["sessionId"] = audioBridge.IsStreamOwnedBy(requesterClientId) ? audioBridge.ActiveSessionId : null,
            ["mode"] = audioBridge.ActiveMode?.ToWireValue(),
            ["lastError"] = audio.Error
        },
        // 已删除受管听写；字段保留给旧版手机与契约，恒为未听写。
        ["dictation"] = new JsonObject
        {
            ["active"] = false,
            ["sessionId"] = null
        },
        ["foregroundApp"] = null,
        ["usbWatchdog"] = UsbWatchdogJson(),
        // 遗留 typeless 块：从当前引擎映射生成，供旧手机端继续读取；
        // 新手机端优先读 voiceEngine 块（含引擎 id 与完整模式列表）。
        ["typeless"] = new JsonObject
        {
            ["capturing"] = capturing,
            ["virtualCableSelected"] = virtualCableSelected,
            ["microphone"] = MacVoiceEngines.MicrophoneDescription,
            ["settingsPath"] = readerConfig.SettingsPath,
            ["lastError"] = readerConfig.Error,
            ["shortcuts"] = new JsonObject
            {
                ["dictation"] = JsonStrings(MacVoiceEngines.ModeKeyNames("dictation")),
                ["translation"] = JsonStrings(MacVoiceEngines.ModeKeyNames("translation")),
                ["ask"] = JsonStrings(MacVoiceEngines.ModeKeyNames("ask"))
            }
        },
        ["voiceEngine"] = new JsonObject
        {
            ["id"] = engineProfile.Id,
            ["displayName"] = engineProfile.DisplayName,
            ["experimental"] = engineProfile.Experimental,
            ["capturing"] = capturing,
            ["virtualCableSelected"] = virtualCableSelected,
            ["microphone"] = MacVoiceEngines.MicrophoneDescription,
            ["modes"] = EngineModesJson(engineProfile)
        }
    };
}

JsonArray EngineModesJson(MacVoiceEngineProfile profile)
{
    var modes = new JsonArray();
    foreach (var mode in profile.Modes)
    {
        modes.Add((JsonNode)new JsonObject
        {
            ["id"] = mode.Id,
            ["label"] = mode.Label ?? mode.Id,
            ["trigger"] = mode.Trigger,
            ["configured"] = MacVoiceEngines.IsModeConfigured(mode.Id),
            ["keys"] = JsonStrings(MacVoiceEngines.ModeKeyNames(mode.Id))
        });
    }
    return modes;
}

static string MacLogDirectory() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "PhoneDeck");

JsonObject AudioQualityJson()
{
    var (current, last) = audioBridge.Quality;
    return new JsonObject
    {
        ["current"] = AudioQualitySessionJson(current),
        ["last"] = AudioQualitySessionJson(last),
        ["logFile"] = Path.Combine(MacLogDirectory(), "receiver.log")
    };
}

static JsonObject? AudioQualitySessionJson(MacAudioQuality? quality) => quality is null ? null : new JsonObject
{
    ["sessionId"] = quality.SessionId,
    ["mode"] = quality.Mode,
    ["durationMs"] = quality.DurationMs,
    ["receivedMs"] = quality.ReceivedMs,
    ["gapEvents"] = quality.GapEvents,
    ["gapMs"] = quality.GapMs,
    ["droppedMs"] = quality.DroppedMs,
    ["silenceSkippedMs"] = quality.SilenceSkippedMs,
    ["maxBufferedMs"] = quality.MaxBufferedMs,
    ["bufferedMs"] = quality.BufferedMs,
    ["digitalSilenceMs"] = quality.DigitalSilenceMs
};

JsonObject UsbWatchdogJson() => new()
{
    ["enabled"] = settings.UsbWatchdog,
    ["running"] = usbWatchdog.Running,
    ["adbFound"] = usbWatchdog.AdbPath is not null,
    ["restoreCount"] = usbWatchdog.RestoreCount,
    ["lastRestoredAt"] = usbWatchdog.LastRestoredAt
};

static JsonArray? JsonStrings(IEnumerable<string>? values)
{
    if (values is null)
    {
        return null;
    }
    var array = new JsonArray();
    foreach (var value in values)
    {
        array.Add((JsonNode?)JsonValue.Create(value));
    }
    return array;
}

app.MapGet("/api/health", (HttpContext context) => Results.Ok(BuildHealth(context)));

app.MapGet("/api/diagnostics", () =>
{
    var audio = audioBridge.Probe();
    var engineProfile = MacVoiceEngines.Active;
    var readerConfig = MacTypelessConfiguration.Load(settings);
    return Results.Ok(new JsonObject
    {
        ["ok"] = true,
        ["computerId"] = receiverIdentity.ComputerId,
        ["displayName"] = receiverIdentity.DisplayName,
        ["platform"] = receiverIdentity.Platform,
        ["architecture"] = receiverIdentity.Architecture,
        ["input"] = new JsonObject
        {
            ["backend"] = "CGEvent",
            ["accessibilityTrusted"] = keyboard.IsAccessibilityTrusted
        },
        ["audio"] = new JsonObject
        {
            ["available"] = audio.Available,
            ["backend"] = "Core Audio AUHAL",
            ["device"] = audio.DeviceName,
            ["deviceUid"] = audio.DeviceUid,
            ["configuredDeviceUid"] = settings.AudioDeviceUid,
            ["streaming"] = audioBridge.IsStreaming,
            ["sessionId"] = audioBridge.ActiveSessionId,
            ["mode"] = audioBridge.ActiveMode?.ToWireValue(),
            ["lastError"] = audio.Error,
            ["recommendedVirtualDevice"] = "BlackHole 2ch / 48 kHz",
            ["quality"] = AudioQualityJson()
        },
        ["voiceEngine"] = new JsonObject
        {
            ["id"] = engineProfile.Id,
            ["displayName"] = engineProfile.DisplayName,
            ["experimental"] = engineProfile.Experimental,
            ["capturing"] = engineController.IsCapturing(),
            ["microphone"] = MacVoiceEngines.MicrophoneDescription,
            ["settingsPath"] = readerConfig.SettingsPath,
            ["usesBlackHole"] = MacVoiceEngines.UsesVirtualCable,
            ["lastError"] = readerConfig.Error,
            ["modes"] = EngineModesJson(engineProfile)
        },
        ["lan"] = new JsonObject
        {
            ["httpsPort"] = lanIdentity.HttpsPort,
            ["candidateAddresses"] = JsonStrings(lanIdentity.GetCandidateAddresses()),
            ["discovery"] = new JsonObject
            {
                ["enabled"] = settings.LanDiscovery,
                ["portBound"] = lanDiscovery.PortBound,
                ["running"] = lanDiscovery.Running
            }
        },
        ["usbWatchdog"] = UsbWatchdogJson()
    });
});

app.MapPost("/api/audio/stream", async (HttpContext context) =>
{
    try
    {
        var protocol = context.Request.Headers["X-PhoneDeck-Protocol"].FirstOrDefault();
        var sessionId = context.Request.Headers["X-PhoneDeck-Session"].FirstOrDefault()?.Trim();
        var targetComputerId = context.Request.Headers["X-PhoneDeck-Computer-Id"]
            .FirstOrDefault()?.Trim();
        if (!string.Equals(protocol, "2", StringComparison.Ordinal)
            || !Guid.TryParse(sessionId, out _))
        {
            return Results.BadRequest(ApiResult.Fail("无效的音频协议或 sessionId"));
        }
        if (!string.Equals(targetComputerId, receiverIdentity.ComputerId,
                StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(ApiResult.Fail("请求目标不是当前电脑"));
        }
        var mode = AudioStreamModes.Parse(
            context.Request.Headers["X-PhoneDeck-Audio-Mode"].FirstOrDefault());
        if (mode != AudioStreamMode.Shared)
        {
            return Results.BadRequest(ApiResult.Fail("只支持共享麦克风"));
        }
        var audioRate = PhoneAudioFormat.ParseRate(
            context.Request.Headers[PhoneAudioFormat.RateHeader].FirstOrDefault());
        PhoneAudioFormat.RequireAllowed(audioRate, mode);
        var probe = audioBridge.Probe();
        if (!probe.Available)
        {
            return Results.Json(ApiResult.Fail(probe.Error), ReceiverApiJsonContext.Default.ApiResult, statusCode: 503);
        }
        var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false })
        {
            bodySize.MaxRequestBodySize = null;
        }
        // 撤销该手机即终止其音频长流（不只拦截新请求）。
        var streamClientId = context.Items["ClientId"] as string;
        // 退出时立即结束长流（已收到的尾音照常排空），不让关机等满超时。
        using var revocation = streamClientId is null
            ? CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, app.Lifetime.ApplicationStopping)
            : CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, app.Lifetime.ApplicationStopping,
                clientSessions.Register(streamClientId));
        // Wi-Fi 共享供音可能是 16 kHz：进入播放缓冲前还原为 48 kHz，后续逻辑不变。
        await audioBridge.StreamAsync(
            PhoneAudioFormat.ToBaseline(context.Request.Body, audioRate),
            sessionId!,
            mode,
            (_, _) => { },
            revocation.Token,
            streamClientId);
        return Results.Ok(new AudioStreamResult(true, sessionId, mode.ToWireValue()));
    }
    catch (AudioStreamStalledException)
    {
        // 半开连接收不到响应；直接中止，Kestrel 不再等待剩余请求体。
        context.Abort();
        return Results.Empty;
    }
    catch (AudioStreamConflictException exception)
    {
        return Results.Conflict(ApiResult.Fail(exception.Message));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(ApiResult.Fail(exception.Message));
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(ApiResult.Fail(exception.Message), ReceiverApiJsonContext.Default.ApiResult, statusCode: 503);
    }
});

app.MapPost("/api/input", async (InputCommand command, HttpContext context) =>
{
    try
    {
        if (audioBridge.IsStreaming
            && string.Equals(command.Action, "typeless", StringComparison.Ordinal)
            && engineController.IsCapturing() == true)
        {
            // 共享麦克风下通过手机快捷键结束听写：等待手机尾音经缓冲与虚拟声卡送达输入法后再发停止键。
            await Task.Delay(260, context.RequestAborted);
        }
        var result = inputProcessor.Execute(command, receiverIdentity.ComputerId);
        return Results.Ok(new InputResult(true, result.Duplicate, command.RequestId,
            receiverIdentity.ComputerId, result.Message, null));
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(ApiResult.Fail(exception.Message));
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(ApiResult.Fail(exception.Message));
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"macOS 输入失败：{exception.Message}");
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    // 缺辅助功能权限：请系统弹出授权提示，并打开状态页说明下一步。
    if (!keyboard.IsAccessibilityTrusted && !MacLaunch.RequestAccessibility())
    {
        MacLaunch.OpenStatusPage();
    }
    Console.WriteLine("========================================");
    Console.WriteLine("  PhoneDeck macOS 接收端已启动");
    Console.WriteLine("  USB 通道：127.0.0.1:8765");
    Console.WriteLine($"  Wi-Fi 通道：HTTPS {lanIdentity.HttpsPort}（手机连同一 Wi-Fi 自动发现，连接请求在本机弹框确认；状态页：http://127.0.0.1:8765/admin/pairing）");
    Console.WriteLine($"  局域网发现：UDP {LanDiscoveryResponder.DiscoveryPort}");
    Console.WriteLine($"  电脑身份：{receiverIdentity.DisplayName} / {receiverIdentity.ComputerId}");
    Console.WriteLine("  输入后端：CGEvent");
    Console.WriteLine(keyboard.IsAccessibilityTrusted
        ? "  辅助功能权限：已授权"
        : "  辅助功能权限：未授权，请在系统设置 → 隐私与安全性 → 辅助功能中启用");
    var audio = audioBridge.Probe();
    Console.WriteLine(audio.Available
        ? $"  手机语音：Core Audio AUHAL → {audio.DeviceName}"
        : $"  手机语音：不可用（{audio.Error}）");
    var engineProfile = MacVoiceEngines.Active;
    Console.WriteLine(
        $"  语音引擎：{engineProfile.DisplayName}{(engineProfile.Experimental ? "（实验性，快捷键/进程名未在真机核实）" : "")}（{engineProfile.Id}）");
    foreach (var mode in engineProfile.Modes)
    {
        var modeKeys = MacVoiceEngines.ModeKeyNames(mode.Id);
        var triggerName = string.Equals(MacVoiceEngines.TriggerFor(mode.Id),
            MacEngineTriggers.Hold, StringComparison.Ordinal) ? "按住式" : "切换式";
        Console.WriteLine(
            $"  {engineProfile.DisplayName} {MacVoiceEngines.LabelOf(mode.Id)}："
            + (modeKeys is null || modeKeys.Length == 0 ? "未配置" : string.Join(" + ", modeKeys))
            + $"（{triggerName}）"
            + (MacVoiceEngines.IsModeConfigured(mode.Id) ? "" : "（未配置，手机端不显示）"));
    }
    if (engineProfile.VerifiesMicrophone)
    {
        Console.WriteLine(
            $"  {engineProfile.DisplayName} 麦克风：{MacVoiceEngines.MicrophoneDescription ?? "未读取到配置"}");
    }
    else
    {
        Console.WriteLine(
            $"  麦克风校验：{engineProfile.DisplayName} 无可读配置，请自行确认其输入设备已选择 BlackHole 2ch");
    }
    Console.WriteLine("========================================");
});

await app.RunAsync();
