using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;

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
using var audioBridge = new PhoneAudioBridge();
using var dictationSessions = new DictationSessionManager(audioBridge);
using var usbWatchdog = new UsbWatchdog(serverSettings.AdbPath);
using var lanDiscovery = new LanDiscoveryResponder(
    receiverIdentity,
    lanIdentity.HttpsPort);
using var diagnostics = new DiagnosticsMonitor(() =>
{
    var typelessState = KeyboardInput.ReadTypelessState();
    return new DiagnosticsSnapshot
    {
        CheckedAtMs = Environment.TickCount64,
        TypelessCapturing = TypelessStateProbe.IsCapturing(),
        TypelessMicrophone = typelessState.MicrophoneDescription,
        TypelessUsesVirtualCable = typelessState.UsesVirtualCable,
        DictationKeys = KeyboardInput.TypelessModeKeyNamesFromBinding(
            typelessState.DictationBinding),
        TranslationKeys = KeyboardInput.TypelessModeKeyNamesFromBinding(
            typelessState.TranslationBinding),
        AskKeys = KeyboardInput.TypelessModeKeyNamesFromBinding(
            typelessState.AskBinding),
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

app.MapGet("/api/health", () =>
{
    // 只读后台诊断快照与易变内存状态：零文件 IO、零 Core Audio 枚举、
    // 零跨线程锁等待，保证即使 Typeless 卡死也持续快速响应。
    var snapshot = diagnostics.Current;
    var ageMs = unchecked(Environment.TickCount64 - snapshot.CheckedAtMs);
    var stale = ageMs > DiagnosticsMonitor.StaleAfterMs;
    return Results.Ok(new
    {
        ok = true,
        name = "PhoneDeck",
        version = "1.6.0-dev.4",
        protocolVersion = 2,
        computerId = receiverIdentity.ComputerId,
        displayName = receiverIdentity.DisplayName,
        platform = receiverIdentity.Platform,
        architecture = receiverIdentity.Architecture,
        capabilities = new[]
        {
            "fixedAction", "keyChord", "text", "macro", "phoneAudio",
            "managedDictation", "secureLan"
        },
        audio = new
        {
            available = snapshot.AudioAvailable,
            device = snapshot.VirtualCableDevice,
            streaming = audioBridge.IsStreaming,
            sessionId = audioBridge.ActiveSessionId,
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
        typeless = new
        {
            capturing = snapshot.TypelessCapturing,
            virtualCableSelected = snapshot.TypelessUsesVirtualCable,
            microphone = snapshot.TypelessMicrophone,
            shortcuts = new
            {
                dictation = snapshot.DictationKeys,
                translation = snapshot.TranslationKeys,
                ask = snapshot.AskKeys
            },
            checkedAtMs = snapshot.CheckedAtMs,
            ageMs,
            lastError = snapshot.LastError,
            stale
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

app.MapGet("/api/diagnostics", async () =>
{
    // 深诊断：强制刷新一次（带超时），不阻塞 health、音频或快捷键请求。
    var snapshot = await diagnostics.RefreshAsync(2_000);
    var ageMs = unchecked(Environment.TickCount64 - snapshot.CheckedAtMs);
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
            capturing = snapshot.TypelessCapturing,
            virtualCableSelected = snapshot.TypelessUsesVirtualCable,
            microphone = snapshot.TypelessMicrophone,
            shortcuts = new
            {
                dictation = snapshot.DictationKeys,
                translation = snapshot.TranslationKeys,
                ask = snapshot.AskKeys
            }
        },
        audio = new
        {
            available = snapshot.AudioAvailable,
            device = snapshot.VirtualCableDevice,
            streaming = audioBridge.IsStreaming,
            sessionId = audioBridge.ActiveSessionId
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

app.MapGet("/api/update/package", (HttpContext context) =>
{
    var zipPath = @"E:\Desktop\PhoneDeck-Windows-WiFi-1.6.0-dev.2\PhoneDeck-1号机极速更新包.zip";
    if (!File.Exists(zipPath))
    {
        return Results.NotFound(new { ok = false, error = "Update package not found" });
    }
    return Results.File(zipPath, "application/zip", "PhoneDeck-Update.zip");
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

    try
    {
        var bytes = await audioBridge.StreamAsync(
            request.Body,
            sessionId,
            dictationSessions.AudioEnded,
            cancellationToken);
        return Results.Ok(new { ok = true, sessionId, bytes });
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499);
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
            KeyboardInput.NormalizeTypelessMode(command.Mode));
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
    foreach (var mode in KeyboardInput.TypelessModes)
    {
        Console.WriteLine(
            $"  Typeless {KeyboardInput.TypelessModeDisplayName(mode)}："
            + $"{KeyboardInput.TypelessModeDescription(mode)}"
            + (KeyboardInput.TypelessModeConfigured(mode) ? "" : "（未配置，手机端不显示）"));
    }
    Console.WriteLine($"  Typeless 麦克风：{KeyboardInput.TypelessMicrophoneDescription}");
    Console.WriteLine("  保持此窗口运行；按 Ctrl+C 可退出。");
    Console.WriteLine("========================================");
});

await app.RunAsync();

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
        Console.Error.WriteLine($"Typeless 会话处理失败：{exception.Message}");
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
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

internal static class KeyboardInput
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, long> RecentRequestIds =
        new(StringComparer.Ordinal);
    private const long RequestIdLifetimeMilliseconds = 30_000;
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
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

    internal static readonly string[] TypelessModes = { "dictation", "translation", "ask" };

    internal static string TypelessShortcutDescription => TypelessModeDescription("dictation");

    internal static string TypelessModeDescription(string mode)
    {
        var keys = TypelessModeKeyNames(mode);
        return keys is null || keys.Length == 0 ? "未配置" : string.Join(" + ", keys);
    }

    internal static string TypelessModeDisplayName(string mode) => mode switch
    {
        "translation" => "翻译",
        "ask" => "问答",
        _ => "听写"
    };

    internal static string NormalizeTypelessMode(string? mode)
    {
        var normalized = string.IsNullOrWhiteSpace(mode)
            ? "dictation"
            : mode.Trim().ToLowerInvariant();
        if (!TypelessModes.Contains(normalized))
        {
            throw new ArgumentException($"未知的 Typeless 模式：{mode}");
        }
        return normalized;
    }

    internal static bool TypelessModeConfigured(string mode)
        => ReadTypelessModeBinding(NormalizeTypelessMode(mode)) is not null;

    /// <summary>规范化键名数组（修饰键在前），如 ["SHIFT","Z"]；未配置该模式时返回 null。</summary>
    internal static string[]? TypelessModeKeyNames(string mode) =>
        TypelessModeKeyNamesFromBinding(
            ReadTypelessModeBinding(NormalizeTypelessMode(mode)));

    /// <summary>把 "CTRL+SHIFT+S" 这类绑定字符串规范化为键名数组；空绑定返回 null。</summary>
    internal static string[]? TypelessModeKeyNamesFromBinding(string? binding)
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

    internal static string TypelessMicrophoneDescription =>
        ReadTypelessMicrophoneDescription() ?? "未读取到配置";
    internal static bool TypelessUsesVirtualCable =>
        GetCachedTypelessState().UsesVirtualCable;

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

            EnsureNotControlCenterForeground();
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
                SendChordWithHold(70, ResolveTypelessShortcut(
                    string.IsNullOrWhiteSpace(text) ? "dictation" : NormalizeTypelessMode(text)));
                break;
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

    private static TypelessConfigState? cachedTypelessState;
    private static long lastTypelessConfigCheckTick;
    private const int TypelessConfigCacheTtlMs = 2_500;

    internal static TypelessConfigState GetCachedTypelessState()
    {
        var now = Environment.TickCount64;
        if (cachedTypelessState is null || now - lastTypelessConfigCheckTick > TypelessConfigCacheTtlMs)
        {
            cachedTypelessState = ReadTypelessState();
            lastTypelessConfigCheckTick = now;
        }
        return cachedTypelessState;
    }

    private static ushort[] ResolveTypelessShortcut(string mode)
    {
        var binding = ReadTypelessModeBinding(mode)
            ?? throw new ArgumentException(
                $"Typeless 未配置「{TypelessModeDisplayName(mode)}」模式的快捷键");
        var keys = new List<ushort>();
        foreach (var token in binding.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var key = ParseTypelessKey(token);
            if (key is null)
            {
                if (mode != "dictation")
                {
                    throw new ArgumentException(
                        $"Typeless「{TypelessModeDisplayName(mode)}」模式快捷键无法识别：{binding}");
                }
                return [VkRightMenu];
            }
            keys.Add(key.Value);
        }

        if (keys.Count == 0)
        {
            if (mode != "dictation")
            {
                throw new ArgumentException(
                    $"Typeless 未配置「{TypelessModeDisplayName(mode)}」模式的快捷键");
            }
            return [VkRightMenu];
        }

        return keys.OrderByDescending(IsModifier).ToArray();
    }

    private static string? ReadTypelessModeBinding(string mode)
    {
        var cached = GetCachedTypelessState();
        var binding = mode switch
        {
            "translation" => cached.TranslationBinding,
            "ask" => cached.AskBinding,
            _ => cached.DictationBinding
        };
        return binding ?? (mode == "dictation" ? "RightAlt" : null);
    }

    private static string? ReadTypelessMicrophoneDescription()
    {
        return GetCachedTypelessState().MicrophoneDescription;
    }

    /// <summary>一次读取 Typeless 配置文件，聚合麦克风与三种模式绑定；
    /// 由后台诊断线程调用，避免每个请求重复读盘。
    /// 读不到配置时听写键退回官方默认 RightAlt，其余模式视为未配置。</summary>
    internal static TypelessConfigState ReadTypelessState()
    {
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Typeless.exe", "app-settings.json");
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root = document.RootElement;

            string? ReadBinding(string propertyName)
            {
                if (root.TryGetProperty("featureShortcutBindings", out var bindings)
                    && bindings.TryGetProperty(propertyName, out var mode)
                    && mode.ValueKind == JsonValueKind.Array
                    && mode.GetArrayLength() > 0)
                {
                    var binding = mode[0].GetString();
                    return string.IsNullOrWhiteSpace(binding) ? null : binding;
                }
                return null;
            }

            string? microphone = null;
            if (root.TryGetProperty("selectedMicrophoneDevice", out var selected))
            {
                var label = selected.TryGetProperty("label", out var labelValue)
                    ? labelValue.GetString()
                    : null;
                var description = selected.TryGetProperty("description", out var descriptionValue)
                    ? descriptionValue.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(label)
                    && !string.IsNullOrWhiteSpace(description))
                {
                    microphone = $"{label} / {description}";
                }
                else
                {
                    microphone = string.IsNullOrWhiteSpace(label) ? description : label;
                }
            }

            var usesVirtualCable = microphone is not null
                && (microphone.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)
                    || microphone.Contains(
                        "VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase));

            var result = new TypelessConfigState(
                microphone,
                usesVirtualCable,
                ReadBinding("dictationMode") ?? "RightAlt",
                ReadBinding("translationMode"),
                ReadBinding("askAnythingMode"));
            cachedTypelessState = result;
            lastTypelessConfigCheckTick = Environment.TickCount64;
            return result;
        }
        catch (Exception)
        {
            var fallback = new TypelessConfigState(null, false, "RightAlt", null, null);
            cachedTypelessState = fallback;
            lastTypelessConfigCheckTick = Environment.TickCount64;
            return fallback;
        }
    }

    private static ushort? ParseTypelessKey(string token)
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

    private static ushort NormalizeVirtualKey(ushort key) => key switch
    {
        VkShift => VkLeftShift,
        VkControl => VkLeftControl,
        VkMenu => VkLeftMenu,
        _ => key
    };

    private static bool IsExtendedKey(ushort key) => key is
        VkLWin or VkRightMenu or VkRightControl or
        VkLeft or VkRight or VkUp or VkDown or
        VkInsert or VkDelete or VkHome or VkEnd or VkPageUp or VkPageDown or
        VkMediaNext or VkMediaPrevious or VkMediaPlayPause or VkVolumeDown or VkVolumeUp or VkVolumeMute;

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
            foreach (var rawKey in keys)
            {
                var key = rawKey;
                pressedKeys.Add(key);

                var scan = (byte)MapVirtualKey(key, MAPVK_VK_TO_VSC);
                if (scan == 0)
                {
                    scan = key switch
                    {
                        VkLeftShift or VkShift => 0x2A,
                        VkRightShift => 0x36,
                        VkLeftControl or VkControl => 0x1D,
                        VkRightControl => 0x1D,
                        VkLeftMenu or VkMenu => 0x38,
                        VkRightMenu => 0x38,
                        _ => 0
                    };
                }

                Send([VirtualKey(key, keyUp: false)]);

                var keybdVk = key switch
                {
                    VkLeftShift or VkRightShift => (byte)VkShift,
                    VkLeftControl or VkRightControl => (byte)VkControl,
                    VkLeftMenu or VkRightMenu => (byte)VkMenu,
                    _ => (byte)key
                };
                keybd_event(keybdVk, scan, 0, UIntPtr.Zero);
                if (key != keybdVk)
                {
                    keybd_event((byte)key, scan, 0, UIntPtr.Zero);
                }

                Thread.Sleep(30);
            }

            var hold = holdMilliseconds > 0 ? holdMilliseconds : 80;
            Thread.Sleep(hold);
        }
        finally
        {
            for (var index = pressedKeys.Count - 1; index >= 0; index--)
            {
                var key = pressedKeys[index];
                var scan = (byte)MapVirtualKey(key, MAPVK_VK_TO_VSC);
                if (scan == 0)
                {
                    scan = key switch
                    {
                        VkLeftShift or VkShift => 0x2A,
                        VkRightShift => 0x36,
                        VkLeftControl or VkControl => 0x1D,
                        VkRightControl => 0x1D,
                        VkLeftMenu or VkMenu => 0x38,
                        VkRightMenu => 0x38,
                        _ => 0
                    };
                }

                Send([VirtualKey(key, keyUp: true)]);

                var keybdVk = key switch
                {
                    VkLeftShift or VkRightShift => (byte)VkShift,
                    VkLeftControl or VkRightControl => (byte)VkControl,
                    VkLeftMenu or VkRightMenu => (byte)VkMenu,
                    _ => (byte)key
                };
                keybd_event(keybdVk, scan, KeyEventKeyUp, UIntPtr.Zero);
                if (key != keybdVk)
                {
                    keybd_event((byte)key, scan, KeyEventKeyUp, UIntPtr.Zero);
                }

                Thread.Sleep(20);
            }
        }
    }

    private static void SendKey(ushort key)
    {
        Send([VirtualKey(key, keyUp: false), VirtualKey(key, keyUp: true)]);
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

    private static Input VirtualKey(ushort key, bool keyUp)
    {
        var scanCode = (ushort)MapVirtualKey(key, MAPVK_VK_TO_VSC);
        if (scanCode == 0)
        {
            scanCode = key switch
            {
                VkLeftShift or VkShift => 0x2A,
                VkRightShift => 0x36,
                VkLeftControl or VkControl => 0x1D,
                VkRightControl => 0x1D,
                VkLeftMenu or VkMenu => 0x38,
                VkRightMenu => 0x38,
                _ => 0
            };
        }
        return new()
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInputData
                {
                    VirtualKey = key,
                    ScanCode = scanCode,
                    Flags = (keyUp ? KeyEventKeyUp : 0) | (IsExtendedKey(key) ? KeyEventExtendedKey : 0)
                }
            }
        };
    }

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
            // 当 SendInput 因特定键码或 UIPI 受到部分拦截时，使用 keybd_event 逐个兜底模拟
            foreach (var input in array)
            {
                if (input.Type == InputKeyboard)
                {
                    var vk = (byte)input.Union.Keyboard.VirtualKey;
                    var scan = (byte)input.Union.Keyboard.ScanCode;
                    var isUp = (input.Union.Keyboard.Flags & KeyEventKeyUp) != 0;
                    if (vk != 0)
                    {
                        keybd_event(vk, scan, isUp ? KeyEventKeyUp : 0, UIntPtr.Zero);
                    }
                }
            }
        }
    }

    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInput);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private const int SwMinimize = 6;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void EnsureNotControlCenterForeground()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return;
            }
            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
            {
                return;
            }
            var name = Process.GetProcessById((int)processId).ProcessName;
            if (string.Equals(name, "PhoneDeck.ControlCenter", StringComparison.OrdinalIgnoreCase))
            {
                ShowWindow(window, SwMinimize);
                Thread.Sleep(30);
            }
        }
        catch
        {
            // 尽力最小化控制台窗口，失败不阻塞发键
        }
    }

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
