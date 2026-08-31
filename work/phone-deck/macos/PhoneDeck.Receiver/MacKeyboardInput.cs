using System.Runtime.InteropServices;
using System.Text;

namespace PhoneDeck.MacReceiver;

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

internal sealed record InputExecutionResult(bool Duplicate, string Message);

internal sealed class InputCommandProcessor
{
    private readonly MacKeyboardInput keyboard;

    internal InputCommandProcessor(MacKeyboardInput keyboard)
    {
        this.keyboard = keyboard;
    }

    internal InputExecutionResult Execute(InputCommand command, string computerId)
    {
        if (string.IsNullOrWhiteSpace(command.Action))
        {
            throw new ArgumentException("缺少 action");
        }
        if (command.Text is { Length: > 4096 })
        {
            throw new ArgumentException("输入数据过大");
        }
        TargetEnvelopeValidator.Validate(
            command.ProtocolVersion,
            command.RequestId,
            command.SessionId,
            command.TargetComputerId,
            computerId);

        if (string.Equals(command.Action, "keyChord", StringComparison.Ordinal))
        {
            if (command.ProtocolVersion != 2)
            {
                throw new ArgumentException("keyChord 必须使用协议 v2");
            }
            var duplicate = keyboard.ExecuteKeyChordOnce(
                command.Keys,
                command.HoldMs,
                command.RequestId,
                out var description);
            return new InputExecutionResult(duplicate, $"已发送 {description}");
        }
        if (string.Equals(command.Action, "macro", StringComparison.Ordinal))
        {
            if (command.ProtocolVersion != 2)
            {
                throw new ArgumentException("macro 必须使用协议 v2");
            }
            var duplicate = keyboard.ExecuteMacroOnce(
                command.Steps,
                command.RequestId,
                out var description);
            return new InputExecutionResult(duplicate, $"已执行宏：{description}");
        }

        var fixedDuplicate = keyboard.ExecuteFixedOnce(
            command.Action,
            command.Text,
            command.RequestId);
        return new InputExecutionResult(fixedDuplicate, $"已执行 {command.Action}");
    }
}

[Flags]
internal enum MacModifierFlags : ulong
{
    None = 0,
    Shift = 1UL << 17,
    Control = 1UL << 18,
    Option = 1UL << 19,
    Command = 1UL << 20,
    Function = 1UL << 23
}

internal readonly record struct MacKey(
    string Name,
    ushort KeyCode,
    bool IsModifier = false,
    int ModifierOrder = 10,
    MacModifierFlags Flag = MacModifierFlags.None);

internal interface IMacKeyboardSink
{
    bool IsAccessibilityTrusted { get; }
    void PostKey(ushort keyCode, bool keyDown, MacModifierFlags flags);
    void PostText(string text);
}

internal sealed class MacKeyboardInput
{
    private const long RequestIdLifetimeMilliseconds = 30_000;
    private readonly object syncRoot = new();
    private readonly Dictionary<string, long> recentRequestIds = new(StringComparer.Ordinal);
    private readonly IMacKeyboardSink sink;

    internal MacKeyboardInput() : this(new CoreGraphicsKeyboardSink())
    {
    }

    internal MacKeyboardInput(IMacKeyboardSink sink)
    {
        this.sink = sink;
    }

    internal bool IsAccessibilityTrusted => sink.IsAccessibilityTrusted;

    internal bool ExecuteKeyChordOnce(
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
        return ExecuteOnce(requestId, () => SendChord(keys, holdMilliseconds));
    }

    internal bool ExecuteMacroOnce(
        MacroStep[]? steps,
        string? requestId,
        out string description)
    {
        var plan = ParseMacroSteps(steps, out description);
        return ExecuteOnce(requestId, () => RunMacro(plan));
    }

    internal bool ExecuteFixedOnce(string action, string? text, string? requestId) =>
        ExecuteOnce(requestId, () => ExecuteFixed(action, text));

    internal MacKey[] ParseKeyChord(string[]? keyNames, out string description)
    {
        if (keyNames is null || keyNames.Length is < 1 or > 4)
        {
            throw new ArgumentException("组合键必须包含 1–4 个键");
        }
        var normalized = keyNames.Select(NormalizeKeyName).ToArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ArgumentException("组合键包含重复键");
        }

        // 兼容当前 Android 默认布局中的 Windows 组合，同时保持逻辑动作在 Mac 上正确。
        normalized = AdaptLegacyAndroidChord(normalized);
        var mapped = normalized.Select(MapKeyName).ToArray();
        if (mapped.Select(key => key.Name).Distinct(StringComparer.Ordinal).Count() != mapped.Length)
        {
            throw new ArgumentException("组合键映射后包含重复修饰键");
        }
        if (mapped.Count(key => !key.IsModifier) != 1)
        {
            throw new ArgumentException("组合键必须且只能包含一个普通键");
        }
        var ordered = mapped.OrderBy(key => key.IsModifier ? key.ModifierOrder : 10).ToArray();
        description = string.Join(" + ", ordered.Select(key => key.Name));
        return ordered;
    }

    private static string[] AdaptLegacyAndroidChord(string[] names)
    {
        if (SetEquals(names, "WIN", "SHIFT", "S")
            || SetEquals(names, "PRINTSCREEN"))
        {
            return ["COMMAND", "SHIFT", "4"];
        }
        if (SetEquals(names, "WIN", "SPACE"))
        {
            return ["CONTROL", "SPACE"];
        }
        if (SetEquals(names, "ALT", "TAB"))
        {
            return ["COMMAND", "TAB"];
        }
        return names.Select(name => name switch
        {
            "PRIMARY" or "CTRL" or "WIN" or "WINDOWS" => "COMMAND",
            "ALT" => "OPTION",
            _ => name
        }).ToArray();
    }

    private static bool SetEquals(string[] names, params string[] expected) =>
        names.Length == expected.Length
        && names.ToHashSet(StringComparer.Ordinal).SetEquals(expected);

    private static string NormalizeKeyName(string? keyName)
    {
        var normalized = keyName?.Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 24)
        {
            throw new ArgumentException("组合键包含无效按键名称");
        }
        return normalized;
    }

    private static MacKey MapKeyName(string name)
    {
        if (LetterKeyCodes.TryGetValue(name, out var letter))
        {
            return new MacKey(name, letter);
        }
        if (DigitKeyCodes.TryGetValue(name, out var digit))
        {
            return new MacKey(name, digit);
        }
        if (FunctionKeyCodes.TryGetValue(name, out var function))
        {
            return new MacKey(name, function);
        }
        return name switch
        {
            "COMMAND" => Modifier(name, 55, 0, MacModifierFlags.Command),
            "CONTROL" => Modifier(name, 59, 1, MacModifierFlags.Control),
            "SHIFT" => Modifier(name, 56, 2, MacModifierFlags.Shift),
            "OPTION" => Modifier(name, 58, 3, MacModifierFlags.Option),
            "FN" or "FUNCTION" => Modifier("FN", 63, 4, MacModifierFlags.Function),
            "BACKTICK" or "`" => new MacKey("BACKTICK", 50),
            "ENTER" or "RETURN" => new MacKey("ENTER", 36),
            "ESC" or "ESCAPE" => new MacKey("ESC", 53),
            "TAB" => new MacKey("TAB", 48),
            "SPACE" or "SPACEBAR" => new MacKey("SPACE", 49),
            "BACKSPACE" => new MacKey("BACKSPACE", 51),
            "DELETE" => new MacKey("DELETE", 117),
            "INSERT" => new MacKey("INSERT", 114),
            "HOME" => new MacKey("HOME", 115),
            "END" => new MacKey("END", 119),
            "PAGEUP" => new MacKey("PAGEUP", 116),
            "PAGEDOWN" => new MacKey("PAGEDOWN", 121),
            "UP" => new MacKey("UP", 126),
            "DOWN" => new MacKey("DOWN", 125),
            "LEFT" => new MacKey("LEFT", 123),
            "RIGHT" => new MacKey("RIGHT", 124),
            "VOLUMEUP" => new MacKey("VOLUMEUP", 72),
            "VOLUMEDOWN" => new MacKey("VOLUMEDOWN", 73),
            "VOLUMEMUTE" or "MUTE" => new MacKey("VOLUMEMUTE", 74),
            "MEDIAPLAYPAUSE" or "PLAYPAUSE" or "MEDIANEXT" or "NEXTTRACK"
                or "MEDIAPREVIOUS" or "PREVIOUSTRACK" =>
                throw new ArgumentException($"macOS 第一阶段暂不支持媒体键：{name}"),
            _ => throw new ArgumentException($"macOS 不支持的按键：{name}")
        };
    }

    private static MacKey Modifier(
        string name,
        ushort code,
        int order,
        MacModifierFlags flag) => new(name, code, true, order, flag);

    private void SendChord(IReadOnlyList<MacKey> keys, int holdMilliseconds)
    {
        EnsureAccessibility();
        var pressed = new List<MacKey>(keys.Count);
        var flags = MacModifierFlags.None;
        try
        {
            foreach (var key in keys)
            {
                if (key.IsModifier)
                {
                    flags |= key.Flag;
                }
                pressed.Add(key);
                sink.PostKey(key.KeyCode, true, flags);
            }
            if (holdMilliseconds > 0)
            {
                Thread.Sleep(holdMilliseconds);
            }
        }
        finally
        {
            Exception? releaseFailure = null;
            for (var index = pressed.Count - 1; index >= 0; index--)
            {
                var key = pressed[index];
                try
                {
                    sink.PostKey(key.KeyCode, false, flags);
                }
                catch (Exception exception)
                {
                    releaseFailure ??= exception;
                }
                if (key.IsModifier)
                {
                    flags &= ~key.Flag;
                }
            }
            if (releaseFailure is not null)
            {
                throw new InvalidOperationException("未能释放全部 macOS 组合键", releaseFailure);
            }
        }
    }

    private void SendText(string text)
    {
        EnsureAccessibility();
        var pending = new StringBuilder();
        void FlushPending()
        {
            if (pending.Length > 0)
            {
                sink.PostText(pending.ToString());
                pending.Clear();
            }
        }

        foreach (var character in text)
        {
            if (character == '\r')
            {
                continue;
            }
            if (character is '\n' or '\t')
            {
                FlushPending();
                SendChord(
                    [MapKeyName(character == '\n' ? "ENTER" : "TAB")],
                    0);
                continue;
            }
            pending.Append(character);
        }
        FlushPending();
    }

    private void EnsureAccessibility()
    {
        if (!sink.IsAccessibilityTrusted)
        {
            throw new InvalidOperationException(
                "PhoneDeck 尚未获得 macOS 辅助功能权限；请在系统设置 → 隐私与安全性 → 辅助功能中启用 PhoneDeck Receiver");
        }
    }

    private List<MacroPlanStep> ParseMacroSteps(MacroStep[]? steps, out string description)
    {
        if (steps is null || steps.Length is < 1 or > 8)
        {
            throw new ArgumentException("宏必须包含 1–8 个步骤");
        }
        var plan = new List<MacroPlanStep>(steps.Length);
        var descriptions = new List<string>(steps.Length);
        foreach (var step in steps)
        {
            var delay = step.DelayBeforeMs ?? 0;
            if (delay is < 0 or > 2000)
            {
                throw new ArgumentException("宏步骤延迟必须在 0–2000 毫秒之间");
            }
            var prefix = delay > 0 ? $"等{delay}ms·" : string.Empty;
            var type = (step.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (type == "keychord")
            {
                var keys = ParseKeyChord(step.Keys, out var keyDescription);
                var hold = step.HoldMs ?? 45;
                if (hold is < 20 or > 500)
                {
                    throw new ArgumentException("holdMs 必须在 20–500 毫秒之间");
                }
                plan.Add(new MacroPlanStep(delay, keys, hold, null, false));
                descriptions.Add(prefix + keyDescription);
            }
            else if (type == "text")
            {
                if (string.IsNullOrEmpty(step.Text) || step.Text.Length > 4096)
                {
                    throw new ArgumentException("宏的文本步骤内容无效");
                }
                plan.Add(new MacroPlanStep(
                    delay, Array.Empty<MacKey>(), 0, step.Text, step.Submit ?? false));
                descriptions.Add(prefix + "输入文本" + (step.Submit == true ? "并回车" : string.Empty));
            }
            else
            {
                throw new ArgumentException($"未知的宏步骤类型：{step.Type}");
            }
        }
        description = string.Join(" → ", descriptions);
        return plan;
    }

    private void RunMacro(IEnumerable<MacroPlanStep> plan)
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
                    SendChord([MapKeyName("ENTER")], 0);
                }
            }
            else
            {
                SendChord(step.Keys, step.HoldMs);
            }
        }
    }

    private void ExecuteFixed(string action, string? text)
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
            case "copy": SendNamedChord("COMMAND", "C"); break;
            case "paste": SendNamedChord("COMMAND", "V"); break;
            case "cut": SendNamedChord("COMMAND", "X"); break;
            case "undo": SendNamedChord("COMMAND", "Z"); break;
            case "redo": SendNamedChord("COMMAND", "SHIFT", "Z"); break;
            case "selectAll": SendNamedChord("COMMAND", "A"); break;
            case "save": SendNamedChord("COMMAND", "S"); break;
            case "altTab": SendNamedChord("COMMAND", "TAB"); break;
            case "taskView": SendNamedChord("CONTROL", "UP"); break;
            case "desktop": SendNamedChord("FN", "F11"); break;
            case "screenshot": SendNamedChord("COMMAND", "SHIFT", "4"); break;
            case "typeless": SendModifierOnly("FN"); break;
            case "switchInputMethod": SendNamedChord("CONTROL", "SPACE"); break;
            case "enter": SendNamedChord("ENTER"); break;
            case "backspace": SendNamedChord("BACKSPACE"); break;
            case "delete": SendNamedChord("DELETE"); break;
            case "escape": SendNamedChord("ESC"); break;
            case "space": SendNamedChord("SPACE"); break;
            case "left": SendNamedChord("LEFT"); break;
            case "right": SendNamedChord("RIGHT"); break;
            case "up": SendNamedChord("UP"); break;
            case "down": SendNamedChord("DOWN"); break;
            case "volumeMute": SendNamedChord("VOLUMEMUTE"); break;
            case "volumeDown": SendNamedChord("VOLUMEDOWN"); break;
            case "volumeUp": SendNamedChord("VOLUMEUP"); break;
            default: throw new ArgumentException($"未知操作：{action}");
        }
    }

    private void SendNamedChord(params string[] names)
    {
        var keys = names.Select(MapKeyName)
            .OrderBy(key => key.IsModifier ? key.ModifierOrder : 10)
            .ToArray();
        SendChord(keys, 45);
    }

    private void SendModifierOnly(string name)
    {
        SendChord([MapKeyName(name)], 55);
    }

    private bool ExecuteOnce(string? requestId, Action execute)
    {
        lock (syncRoot)
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
                foreach (var expired in recentRequestIds
                             .Where(pair => now - pair.Value > RequestIdLifetimeMilliseconds)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    recentRequestIds.Remove(expired);
                }
                if (recentRequestIds.ContainsKey(normalizedRequestId))
                {
                    return true;
                }
            }
            execute();
            if (normalizedRequestId is not null)
            {
                recentRequestIds[normalizedRequestId] = now;
            }
            return false;
        }
    }

    private sealed record MacroPlanStep(
        int DelayMs,
        MacKey[] Keys,
        int HoldMs,
        string? Text,
        bool Submit);

    private static readonly IReadOnlyDictionary<string, ushort> LetterKeyCodes =
        new Dictionary<string, ushort>(StringComparer.Ordinal)
        {
            ["A"] = 0, ["S"] = 1, ["D"] = 2, ["F"] = 3, ["H"] = 4,
            ["G"] = 5, ["Z"] = 6, ["X"] = 7, ["C"] = 8, ["V"] = 9,
            ["B"] = 11, ["Q"] = 12, ["W"] = 13, ["E"] = 14, ["R"] = 15,
            ["Y"] = 16, ["T"] = 17, ["O"] = 31, ["U"] = 32, ["I"] = 34,
            ["P"] = 35, ["L"] = 37, ["J"] = 38, ["K"] = 40, ["N"] = 45,
            ["M"] = 46
        };

    private static readonly IReadOnlyDictionary<string, ushort> DigitKeyCodes =
        new Dictionary<string, ushort>(StringComparer.Ordinal)
        {
            ["1"] = 18, ["2"] = 19, ["3"] = 20, ["4"] = 21, ["6"] = 22,
            ["5"] = 23, ["9"] = 25, ["7"] = 26, ["8"] = 28, ["0"] = 29
        };

    private static readonly IReadOnlyDictionary<string, ushort> FunctionKeyCodes =
        new Dictionary<string, ushort>(StringComparer.Ordinal)
        {
            ["F1"] = 122, ["F2"] = 120, ["F3"] = 99, ["F4"] = 118,
            ["F5"] = 96, ["F6"] = 97, ["F7"] = 98, ["F8"] = 100,
            ["F9"] = 101, ["F10"] = 109, ["F11"] = 103, ["F12"] = 111,
            ["F13"] = 105, ["F14"] = 107, ["F15"] = 113, ["F16"] = 106,
            ["F17"] = 64, ["F18"] = 79, ["F19"] = 80, ["F20"] = 90
        };
}

internal sealed class CoreGraphicsKeyboardSink : IMacKeyboardSink
{
    private const string ApplicationServices =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreGraphics =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public bool IsAccessibilityTrusted =>
        OperatingSystem.IsMacOS() && AXIsProcessTrusted();

    public void PostKey(ushort keyCode, bool keyDown, MacModifierFlags flags)
    {
        EnsureMacOS();
        var keyboardEvent = CGEventCreateKeyboardEvent(IntPtr.Zero, keyCode, keyDown);
        if (keyboardEvent == IntPtr.Zero)
        {
            throw new InvalidOperationException("macOS 未能创建键盘事件");
        }
        try
        {
            CGEventSetFlags(keyboardEvent, (ulong)flags);
            CGEventPost(0, keyboardEvent);
        }
        finally
        {
            CFRelease(keyboardEvent);
        }
    }

    public void PostText(string text)
    {
        EnsureMacOS();
        var offset = 0;
        while (offset < text.Length)
        {
            var length = Math.Min(16, text.Length - offset);
            if (offset + length < text.Length
                && char.IsHighSurrogate(text[offset + length - 1]))
            {
                length--;
            }
            PostTextChunk(text.Substring(offset, length));
            offset += length;
        }
    }

    private static void PostTextChunk(string text)
    {
        // Core Graphics 的 UniChar 是固定 16 位；不要依赖平台默认的 char P/Invoke 编组。
        var characters = text.Select(character => (ushort)character).ToArray();
        var keyDown = CGEventCreateKeyboardEvent(IntPtr.Zero, 0, true);
        var keyUp = CGEventCreateKeyboardEvent(IntPtr.Zero, 0, false);
        if (keyDown == IntPtr.Zero || keyUp == IntPtr.Zero)
        {
            if (keyDown != IntPtr.Zero) CFRelease(keyDown);
            if (keyUp != IntPtr.Zero) CFRelease(keyUp);
            throw new InvalidOperationException("macOS 未能创建文字输入事件");
        }
        try
        {
            CGEventKeyboardSetUnicodeString(keyDown, (nuint)characters.Length, characters);
            CGEventKeyboardSetUnicodeString(keyUp, (nuint)characters.Length, characters);
            CGEventPost(0, keyDown);
            CGEventPost(0, keyUp);
        }
        finally
        {
            CFRelease(keyDown);
            CFRelease(keyUp);
        }
    }

    private static void EnsureMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("CGEvent 输入只能在 macOS 上运行");
        }
    }

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrusted();

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGEventCreateKeyboardEvent(
        IntPtr source,
        ushort virtualKey,
        [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [DllImport(CoreGraphics)]
    private static extern void CGEventSetFlags(IntPtr keyboardEvent, ulong flags);

    [DllImport(CoreGraphics)]
    private static extern void CGEventPost(uint tapLocation, IntPtr keyboardEvent);

    [DllImport(CoreGraphics)]
    private static extern void CGEventKeyboardSetUnicodeString(
        IntPtr keyboardEvent,
        nuint stringLength,
        [In] ushort[] unicodeString);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);
}
