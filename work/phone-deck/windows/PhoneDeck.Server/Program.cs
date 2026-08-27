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
using var audioBridge = new PhoneAudioBridge();
using var dictationSessions = new DictationSessionManager(audioBridge);
await using var bluetoothReceiver = new BluetoothReceiver(
    receiverIdentity.ComputerId,
    receiverIdentity.DisplayName);
bluetoothReceiver.Start(app.Lifetime.ApplicationStopping);

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
    var audioDevice = audioBridge.FindVirtualCable();
    return Results.Ok(new
    {
        ok = true,
        name = "PhoneDeck",
        version = "1.6.0-dev.2",
        protocolVersion = 2,
        computerId = receiverIdentity.ComputerId,
        displayName = receiverIdentity.DisplayName,
        platform = receiverIdentity.Platform,
        architecture = receiverIdentity.Architecture,
        capabilities = new[]
        {
            "fixedAction", "keyChord", "text", "phoneAudio", "managedDictation",
            "secureLan"
        },
        audio = new
        {
            available = audioDevice is not null,
            device = audioDevice,
            streaming = audioBridge.IsStreaming,
            sessionId = audioBridge.ActiveSessionId
        },
        dictation = new
        {
            active = dictationSessions.IsActive,
            sessionId = dictationSessions.ActiveSessionId
        },
        typeless = new
        {
            capturing = TypelessStateProbe.IsCapturing(),
            virtualCableSelected = KeyboardInput.TypelessUsesVirtualCable,
            microphone = KeyboardInput.TypelessMicrophoneDescription
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
        var duplicate = dictationSessions.Start(command.SessionId, command.RequestId);
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
    Console.WriteLine($"  电脑身份：{receiverIdentity.DisplayName} / {receiverIdentity.ComputerId}");
    Console.WriteLine("  蓝牙通道：正在查找已配对的手机");
    Console.WriteLine($"  手机麦克风：{audioBridge.FindVirtualCable() ?? "未找到 VB-CABLE"}");
    Console.WriteLine($"  Typeless 唤醒键：{KeyboardInput.TypelessShortcutDescription}");
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
    int? HoldMs);
internal sealed record DictationCommand(
    int? ProtocolVersion,
    string? SessionId,
    string? RequestId,
    string? TargetComputerId);

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

    internal static string TypelessShortcutDescription => ReadTypelessShortcutBinding();
    internal static string TypelessMicrophoneDescription =>
        ReadTypelessMicrophoneDescription() ?? "未读取到配置";
    internal static bool TypelessUsesVirtualCable
    {
        get
        {
            var microphone = ReadTypelessMicrophoneDescription();
            return microphone is not null
                && (microphone.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)
                    || microphone.Contains(
                        "VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase));
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
            case "typeless": SendChordWithHold(55, ResolveTypelessShortcut()); break;
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

    private static ushort[] ResolveTypelessShortcut()
    {
        var binding = ReadTypelessShortcutBinding();
        var keys = new List<ushort>();
        foreach (var token in binding.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var key = ParseTypelessKey(token);
            if (key is null)
            {
                return [VkRightMenu];
            }
            keys.Add(key.Value);
        }

        if (keys.Count == 0)
        {
            return [VkRightMenu];
        }

        return keys.OrderByDescending(IsModifier).ToArray();
    }

    private static string ReadTypelessShortcutBinding()
    {
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Typeless.exe", "app-settings.json");
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var bindings = document.RootElement
                .GetProperty("featureShortcutBindings")
                .GetProperty("dictationMode");
            if (bindings.ValueKind == JsonValueKind.Array && bindings.GetArrayLength() > 0)
            {
                var binding = bindings[0].GetString();
                if (!string.IsNullOrWhiteSpace(binding))
                {
                    return binding;
                }
            }
        }
        catch (Exception)
        {
            // Typeless 未安装、配置文件被占用或格式变化时，退回官方 Windows 默认键。
        }
        return "RightAlt";
    }

    private static string? ReadTypelessMicrophoneDescription()
    {
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Typeless.exe", "app-settings.json");
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var selected = document.RootElement.GetProperty("selectedMicrophoneDevice");
            var label = selected.TryGetProperty("label", out var labelValue)
                ? labelValue.GetString()
                : null;
            var description = selected.TryGetProperty("description", out var descriptionValue)
                ? descriptionValue.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(description))
            {
                return $"{label} / {description}";
            }
            return string.IsNullOrWhiteSpace(label) ? description : label;
        }
        catch (Exception)
        {
            return null;
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
