using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;

using var singleInstance = new Mutex(initiallyOwned: true, "PhoneDeck.Server.Singleton", out var isFirstInstance);
if (!isFirstInstance)
{
    Console.WriteLine("手机键盘电脑端已经在运行。");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = null;
    options.ListenLocalhost(8765, listen => listen.Protocols = HttpProtocols.Http1);
});

var app = builder.Build();
using var audioBridge = new PhoneAudioBridge();
await using var bluetoothReceiver = new BluetoothReceiver();
bluetoothReceiver.Start(app.Lifetime.ApplicationStopping);

app.MapGet("/api/health", () =>
{
    var audioDevice = audioBridge.FindVirtualCable();
    return Results.Ok(new
    {
        ok = true,
        name = "PhoneDeck",
        version = "1.4.0",
        audio = new
        {
            available = audioDevice is not null,
            device = audioDevice
        }
    });
});

app.MapPost("/api/audio/stream", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    if (!string.Equals(request.Headers["X-PhoneDeck-Audio"], "pcm-s16le",
            StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { ok = false, error = "不支持的音频格式" });
    }

    try
    {
        var bytes = await audioBridge.StreamAsync(request.Body, cancellationToken);
        return Results.Ok(new { ok = true, bytes });
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

app.MapPost("/api/input", (InputCommand command) =>
{
    if (string.IsNullOrWhiteSpace(command.Action))
    {
        return Results.BadRequest(new { ok = false, error = "缺少 action" });
    }

    if (command.Text is { Length: > 4096 })
    {
        return Results.BadRequest(new { ok = false, error = "输入数据过大" });
    }

    try
    {
        var duplicate = KeyboardInput.ExecuteOnce(
            command.Action, command.Text, command.RequestId);
        return Results.Ok(new
        {
            ok = true,
            duplicate,
            requestId = command.RequestId,
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
    Console.WriteLine("  蓝牙通道：正在查找已配对的手机");
    Console.WriteLine($"  手机麦克风：{audioBridge.FindVirtualCable() ?? "未找到 VB-CABLE"}");
    Console.WriteLine($"  Typeless 唤醒键：{KeyboardInput.TypelessShortcutDescription}");
    Console.WriteLine("  保持此窗口运行；按 Ctrl+C 可退出。");
    Console.WriteLine("========================================");
});

await app.RunAsync();

internal sealed record InputCommand(string? Action, string? Text, string? RequestId);

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
    private const ushort VkDelete = 0x2E;
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

    internal static string TypelessShortcutDescription => ReadTypelessShortcutBinding();

    internal static void Execute(string action, string? text)
    {
        ExecuteOnce(action, text, null);
    }

    internal static bool ExecuteOnce(string action, string? text, string? requestId)
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

            ExecuteCore(action, text);
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
        var inputs = new List<Input>(keys.Length * 2);
        foreach (var key in keys)
        {
            inputs.Add(VirtualKey(key, keyUp: false));
        }
        for (var index = keys.Length - 1; index >= 0; index--)
        {
            inputs.Add(VirtualKey(keys[index], keyUp: true));
        }
        Send(inputs);
    }

    private static void SendChordWithHold(int holdMilliseconds, params ushort[] keys)
    {
        var downInputs = keys
            .Select(key => VirtualKey(key, keyUp: false))
            .ToArray();
        var upInputs = keys
            .Reverse()
            .Select(key => VirtualKey(key, keyUp: true))
            .ToArray();

        Send(downInputs);
        try
        {
            Thread.Sleep(holdMilliseconds);
        }
        finally
        {
            Send(upInputs);
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
