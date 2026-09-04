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

using var singleInstance = new Mutex(
    initiallyOwned: true,
    "PhoneDeck.MacReceiver.Singleton",
    out var isFirstInstance);
if (!isFirstInstance)
{
    Console.WriteLine("PhoneDeck macOS 接收端已经在运行。");
    return;
}

var capabilities = new[]
{
    "fixedAction", "keyChord", "text", "macro", "secureLan", "macInput",
    "phoneAudio", "sharedMicrophone", "managedDictation"
};
var receiverIdentity = ReceiverIdentity.LoadOrCreate();
using var lanIdentity = LanIdentity.LoadOrCreate(receiverIdentity.ComputerId);
var settings = MacReceiverSettings.LoadOrCreate();
var keyboard = new MacKeyboardInput();
using var audioBridge = new MacPhoneAudioBridge(
    new CoreAudioHalOutputFactory(settings.AudioDeviceUid));
var typeless = new MacTypelessController(settings, keyboard);
using var dictationSessions = new MacDictationSessionManager(audioBridge, typeless);
var inputProcessor = new InputCommandProcessor(keyboard);
using var usbWatchdog = new UsbWatchdog(settings.AdbPath);
using var lanDiscovery = new LanDiscoveryResponder(
    receiverIdentity,
    lanIdentity.HttpsPort,
    capabilities);

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

if (settings.UsbWatchdog)
{
    usbWatchdog.Start();
}
if (settings.LanDiscovery)
{
    lanDiscovery.Start();
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

app.MapGet("/api/health", () =>
{
    var audio = audioBridge.Probe();
    var typelessConfig = typeless.Configuration;
    return Results.Ok(new
    {
        ok = true,
        name = "PhoneDeck",
        version = "2.0.0-dev.2",
        protocolVersion = 2,
        computerId = receiverIdentity.ComputerId,
        displayName = receiverIdentity.DisplayName,
        platform = receiverIdentity.Platform,
        architecture = receiverIdentity.Architecture,
        capabilities,
        input = new
        {
            available = keyboard.IsAccessibilityTrusted,
            backend = "CGEvent",
            accessibilityTrusted = keyboard.IsAccessibilityTrusted
        },
        audio = new
        {
            available = audio.Available,
            device = audio.DeviceName,
            deviceUid = audio.DeviceUid,
            streaming = audioBridge.IsStreaming,
            sessionId = audioBridge.ActiveSessionId,
            mode = audioBridge.ActiveMode?.ToWireValue(),
            lastError = audio.Error
        },
        dictation = new
        {
            active = dictationSessions.IsActive,
            sessionId = dictationSessions.ActiveSessionId
        },
        foregroundApp = (string?)null,
        usbWatchdog = new
        {
            enabled = settings.UsbWatchdog,
            running = usbWatchdog.Running,
            adbFound = usbWatchdog.AdbPath is not null,
            restoreCount = usbWatchdog.RestoreCount,
            lastRestoredAt = usbWatchdog.LastRestoredAt
        },
        typeless = new
        {
            capturing = typeless.IsCapturing(),
            virtualCableSelected = typelessConfig.UsesBlackHole,
            microphone = typelessConfig.Microphone,
            settingsPath = typelessConfig.SettingsPath,
            lastError = typelessConfig.Error,
            shortcuts = new
            {
                dictation = SplitBinding(typelessConfig.DictationBinding),
                translation = SplitBinding(typelessConfig.TranslationBinding),
                ask = SplitBinding(typelessConfig.AskBinding)
            }
        }
    });
});

app.MapGet("/api/diagnostics", () =>
{
    var audio = audioBridge.Probe();
    var typelessConfig = typeless.Configuration;
    return Results.Ok(new
    {
        ok = true,
        computerId = receiverIdentity.ComputerId,
        displayName = receiverIdentity.DisplayName,
        platform = receiverIdentity.Platform,
        architecture = receiverIdentity.Architecture,
        input = new
        {
            backend = "CGEvent",
            accessibilityTrusted = keyboard.IsAccessibilityTrusted
        },
        audio = new
        {
            available = audio.Available,
            backend = "Core Audio AUHAL",
            device = audio.DeviceName,
            deviceUid = audio.DeviceUid,
            configuredDeviceUid = settings.AudioDeviceUid,
            streaming = audioBridge.IsStreaming,
            sessionId = audioBridge.ActiveSessionId,
            mode = audioBridge.ActiveMode?.ToWireValue(),
            lastError = audio.Error,
            recommendedVirtualDevice = "BlackHole 2ch / 48 kHz"
        },
        typeless = new
        {
            capturing = typeless.IsCapturing(),
            microphone = typelessConfig.Microphone,
            settingsPath = typelessConfig.SettingsPath,
            usesBlackHole = typelessConfig.UsesBlackHole,
            lastError = typelessConfig.Error
        },
        lan = new
        {
            httpsPort = lanIdentity.HttpsPort,
            candidateAddresses = lanIdentity.GetCandidateAddresses(),
            discovery = new
            {
                enabled = settings.LanDiscovery,
                portBound = lanDiscovery.PortBound,
                running = lanDiscovery.Running
            }
        },
        usbWatchdog = new
        {
            enabled = settings.UsbWatchdog,
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
            return Results.BadRequest(new { ok = false, error = "无效的音频协议或 sessionId" });
        }
        if (!string.Equals(targetComputerId, receiverIdentity.ComputerId,
                StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { ok = false, error = "请求目标不是当前电脑" });
        }
        var mode = AudioStreamModeParser.Parse(
            context.Request.Headers["X-PhoneDeck-Audio-Mode"].FirstOrDefault());
        var probe = audioBridge.Probe();
        if (!probe.Available)
        {
            return Results.Json(new { ok = false, error = probe.Error }, statusCode: 503);
        }
        var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false })
        {
            bodySize.MaxRequestBodySize = null;
        }
        await audioBridge.StreamAsync(
            context.Request.Body,
            sessionId!,
            mode,
            (endedSession, endedMode) =>
            {
                if (endedMode == AudioStreamMode.Managed)
                {
                    dictationSessions.AudioEnded(endedSession);
                }
            },
            context.RequestAborted);
        return Results.Ok(new
        {
            ok = true,
            sessionId,
            mode = mode.ToWireValue()
        });
    }
    catch (AudioStreamConflictException exception)
    {
        return Results.Conflict(new { ok = false, error = exception.Message });
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { ok = false, error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(new { ok = false, error = exception.Message }, statusCode: 503);
    }
});

app.MapPost("/api/dictation/start", (DictationCommand command) =>
{
    try
    {
        TargetEnvelopeValidator.Validate(
            command.ProtocolVersion,
            command.RequestId,
            command.SessionId,
            command.TargetComputerId,
            receiverIdentity.ComputerId);
        var duplicate = dictationSessions.Start(
            command.SessionId, command.RequestId, command.Mode);
        return Results.Ok(new
        {
            ok = true,
            duplicate,
            active = dictationSessions.IsActive,
            sessionId = dictationSessions.ActiveSessionId
        });
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { ok = false, error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { ok = false, error = exception.Message });
    }
});

app.MapPost("/api/dictation/stop", (DictationCommand command) =>
{
    try
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
            active = dictationSessions.IsActive,
            sessionId = dictationSessions.ActiveSessionId
        });
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { ok = false, error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { ok = false, error = exception.Message });
    }
});

app.MapPost("/api/input", (InputCommand command) =>
{
    try
    {
        var result = inputProcessor.Execute(command, receiverIdentity.ComputerId);
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
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { ok = false, error = exception.Message });
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"macOS 输入失败：{exception.Message}");
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine("========================================");
    Console.WriteLine("  PhoneDeck macOS 接收端已启动");
    Console.WriteLine("  USB 通道：127.0.0.1:8765");
    Console.WriteLine($"  Wi-Fi 通道：HTTPS {lanIdentity.HttpsPort}（需先通过 USB 配对）");
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
    Console.WriteLine("========================================");
});

await app.RunAsync();

static string[]? SplitBinding(string? binding) =>
    string.IsNullOrWhiteSpace(binding)
        ? null
        : binding.Split('+', StringSplitOptions.TrimEntries
            | StringSplitOptions.RemoveEmptyEntries);
