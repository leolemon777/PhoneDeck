using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using PhoneDeck.MacReceiver;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (!OperatingSystem.IsMacOS())
{
    Console.Error.WriteLine("PhoneDeck macOS 接收端只能在 macOS 运行。");
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
    "fixedAction", "keyChord", "text", "macro", "secureLan", "macInput"
};
var receiverIdentity = ReceiverIdentity.LoadOrCreate();
using var lanIdentity = LanIdentity.LoadOrCreate(receiverIdentity.ComputerId);
var settings = MacReceiverSettings.LoadOrCreate();
var keyboard = new MacKeyboardInput();
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

app.MapGet("/api/health", () => Results.Ok(new
{
    ok = true,
    name = "PhoneDeck",
    version = "2.0.0-dev.1",
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
        available = false,
        device = (string?)null,
        streaming = false,
        sessionId = (string?)null,
        lastError = "macOS Core Audio / BlackHole 语音桥接将在下一阶段接入"
    },
    dictation = new
    {
        active = false,
        sessionId = (string?)null
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
        capturing = (bool?)null,
        virtualCableSelected = false,
        microphone = (string?)null,
        shortcuts = new
        {
            dictation = new[] { "FN" },
            translation = (string[]?)null,
            ask = (string[]?)null
        }
    }
}));

app.MapGet("/api/diagnostics", () => Results.Ok(new
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
        available = false,
        backend = "Core Audio（待接入）",
        recommendedVirtualDevice = "BlackHole 2ch"
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
}));

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
    Console.WriteLine("  手机语音：下一阶段接入 Core Audio / BlackHole，本版明确禁用");
    Console.WriteLine("========================================");
});

await app.RunAsync();
