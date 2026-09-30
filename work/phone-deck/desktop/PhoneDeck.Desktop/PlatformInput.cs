using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PhoneDeck.Desktop;

internal sealed class PlatformInput
{
    private readonly object gate = new();
    private static readonly Lazy<bool> X11Threads = new(() =>
    {
        try { return XInitThreads() != 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    });
    internal PlatformInput()
    {
        // Initialize Xlib locking before DesktopHotkeys opens its separate connection.
        if (OperatingSystem.IsLinux() && !Wayland) _ = X11Threads.Value;
    }
    internal bool Available => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() && AXIsProcessTrusted()
        || OperatingSystem.IsLinux() && LinuxTool is not null;
    internal string Backend => OperatingSystem.IsWindows() ? "SendInput" : OperatingSystem.IsMacOS() ? "CGEvent" : LinuxTool ?? "未安装输入辅助工具";
    private static bool Wayland => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    private static string? LinuxTool => FindProgram(Wayland ? "wtype" : "xdotool");
    private static readonly Dictionary<string, string[]> Fixed = new(StringComparer.Ordinal)
    {
        ["copy"] = ["PRIMARY", "C"], ["paste"] = ["PRIMARY", "V"], ["cut"] = ["PRIMARY", "X"],
        ["undo"] = ["PRIMARY", "Z"], ["redo"] = ["PRIMARY", "SHIFT", "Z"], ["selectAll"] = ["PRIMARY", "A"],
        ["save"] = ["PRIMARY", "S"], ["enter"] = ["ENTER"], ["backspace"] = ["BACKSPACE"], ["delete"] = ["DELETE"],
        ["escape"] = ["ESC"], ["space"] = ["SPACE"], ["left"] = ["LEFT"], ["right"] = ["RIGHT"], ["up"] = ["UP"], ["down"] = ["DOWN"],
        ["altTab"] = ["ALT", "TAB"], ["desktop"] = ["WIN", "D"], ["taskView"] = ["WIN", "TAB"],
        ["switchInputMethod"] = ["CTRL", "SPACE"], ["screenshot"] = ["WIN", "SHIFT", "S"],
        ["volumeMute"] = ["VOLUMEMUTE"], ["volumeDown"] = ["VOLUMEDOWN"], ["volumeUp"] = ["VOLUMEUP"]
    };
    internal void Execute(string action, string? text, string[]? keys, int? holdMs)
    {
        ValidateAction(action, text, keys, holdMs);
        lock (gate)
        {
            if (!Available) throw new InvalidOperationException("输入权限或辅助工具尚未就绪，请查看电脑设置页");
            if (action == "text")
            {
                if (string.IsNullOrEmpty(text) || text.Length > 4096 || text.Contains('\0')) throw new ArgumentException("文字长度或格式无效");
                Type(text); return;
            }
            var supplied = action == "keyChord" ? keys : Fixed.GetValueOrDefault(action);
            if (OperatingSystem.IsMacOS() && action != "keyChord") supplied = action switch
            {
                "screenshot" => ["COMMAND", "SHIFT", "4"], "desktop" => ["F11"], "taskView" => ["CTRL", "UP"],
                "altTab" => ["COMMAND", "TAB"], _ => supplied
            };
            var chord = Normalize(supplied);
            var duration = holdMs ?? 45;
            if (duration is < 20 or > 500) throw new ArgumentException("按键持续时间超出范围");
            if (OperatingSystem.IsWindows()) WindowsChord(chord, duration);
            else if (OperatingSystem.IsMacOS()) MacChord(chord, duration);
            else LinuxChord(chord);
        }
    }
    internal static void ValidateAction(string action, string? text, string[]? keys, int? holdMs)
    {
        if (action == "text")
        {
            if (string.IsNullOrEmpty(text) || text.Length > 4096 || text.Contains('\0')) throw new ArgumentException("文字长度或格式无效");
            return;
        }
        Normalize(action == "keyChord" ? keys : Fixed.GetValueOrDefault(action));
        if ((holdMs ?? 45) is < 20 or > 500) throw new ArgumentException("按键持续时间超出范围");
        if (OperatingSystem.IsMacOS() && action == "keyChord" && keys!.Any(k => !MacKeys.ContainsKey(k.ToUpperInvariant()))) throw new ArgumentException("该键尚不支持 macOS");
    }
    internal static string[] Normalize(string[]? keys)
    {
        if (keys is null || keys.Length is < 1 or > 4) throw new ArgumentException("未知操作或按键数量无效");
        var normalized = keys.Select(x => (x ?? "").ToUpperInvariant() switch { "CONTROL" => "CTRL", "OPTION" => "ALT", "COMMAND" => "WIN", var k => k }).ToArray();
        if (normalized.Contains("PRIMARY") && normalized.Contains(OperatingSystem.IsMacOS() ? "WIN" : "CTRL")) throw new ArgumentException("组合键重复");
        if (normalized.Distinct().Count() != normalized.Length || normalized.Any(x => !WindowsKeys.ContainsKey(x))) throw new ArgumentException("按键不在受控列表中");
        if (normalized.Count(x => !IsModifier(x)) > 1) throw new ArgumentException("组合键最多一个普通键");
        return normalized.OrderBy(x => IsModifier(x) ? 0 : 1).ToArray();
    }
    private static bool IsModifier(string key) => key is "CTRL" or "CONTROL" or "PRIMARY" or "SHIFT" or "ALT" or "WIN" or "COMMAND" or "OPTION";
    private static Dictionary<string, ushort> CreateWindowsKeys()
    {
        var result = new Dictionary<string, ushort> { ["CTRL"] = 0x11, ["CONTROL"] = 0x11, ["PRIMARY"] = 0x11, ["SHIFT"] = 0x10, ["ALT"] = 0x12,
            ["OPTION"] = 0x12, ["WIN"] = 0x5B, ["COMMAND"] = 0x5B, ["ENTER"] = 0x0D, ["BACKSPACE"] = 8, ["DELETE"] = 0x2E,
            ["ESC"] = 0x1B, ["SPACE"] = 0x20, ["TAB"] = 9, ["LEFT"] = 0x25, ["UP"] = 0x26, ["RIGHT"] = 0x27, ["DOWN"] = 0x28,
            ["HOME"] = 0x24, ["END"] = 0x23, ["PAGEUP"] = 0x21, ["PAGEDOWN"] = 0x22, ["INSERT"] = 0x2D, ["PRINTSCREEN"] = 0x2C,
            ["VOLUMEMUTE"] = 0xAD, ["VOLUMEDOWN"] = 0xAE, ["VOLUMEUP"] = 0xAF };
        for (var c = 'A'; c <= 'Z'; c++) result[c.ToString()] = c;
        for (var c = '0'; c <= '9'; c++) result[c.ToString()] = c;
        for (var i = 1; i <= 24; i++) result["F" + i] = (ushort)(0x6F + i);
        return result;
    }
    private static readonly Dictionary<string, ushort> WindowsKeys = CreateWindowsKeys();
    private static readonly Dictionary<string, ushort> MacKeys = new()
    {
        ["PRIMARY"] = 55, ["COMMAND"] = 55, ["WIN"] = 55, ["CTRL"] = 59, ["CONTROL"] = 59, ["SHIFT"] = 56, ["ALT"] = 58, ["OPTION"] = 58,
        ["A"] = 0, ["S"] = 1, ["D"] = 2, ["F"] = 3, ["H"] = 4, ["G"] = 5, ["Z"] = 6, ["X"] = 7, ["C"] = 8, ["V"] = 9,
        ["B"] = 11, ["Q"] = 12, ["W"] = 13, ["E"] = 14, ["R"] = 15, ["Y"] = 16, ["T"] = 17,
        ["1"] = 18, ["2"] = 19, ["3"] = 20, ["4"] = 21, ["6"] = 22, ["5"] = 23, ["9"] = 25, ["7"] = 26, ["8"] = 28, ["0"] = 29,
        ["O"] = 31, ["U"] = 32, ["I"] = 34, ["P"] = 35, ["L"] = 37, ["J"] = 38, ["K"] = 40, ["N"] = 45, ["M"] = 46,
        ["ENTER"] = 36, ["TAB"] = 48, ["SPACE"] = 49, ["BACKSPACE"] = 51, ["ESC"] = 53, ["DELETE"] = 117,
        ["HOME"] = 115, ["END"] = 119, ["PAGEUP"] = 116, ["PAGEDOWN"] = 121, ["LEFT"] = 123, ["RIGHT"] = 124, ["DOWN"] = 125, ["UP"] = 126,
        ["F1"] = 122, ["F2"] = 120, ["F3"] = 99, ["F4"] = 118, ["F5"] = 96, ["F6"] = 97, ["F7"] = 98, ["F8"] = 100, ["F9"] = 101,
        ["F10"] = 109, ["F11"] = 103, ["F12"] = 111, ["F13"] = 105, ["F14"] = 107, ["F15"] = 113, ["F16"] = 106, ["F17"] = 64, ["F18"] = 79, ["F19"] = 80, ["F20"] = 90
    };
    private void Type(string text)
    {
        if (OperatingSystem.IsWindows())
            foreach (var c in text) { WinKey(0, c, 4); WinKey(0, c, 6); }
        else if (OperatingSystem.IsMacOS())
        {
            for (var offset = 0; offset < text.Length;)
            {
                var length = Math.Min(20, text.Length - offset);
                if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1])) length--;
                var value = text.Substring(offset, length); offset += length; var e = CGEventCreateKeyboardEvent(0, 0, true);
                if (e == 0) throw new IOException("无法创建输入事件");
                try { CGEventSetFlags(e, 0); CGEventKeyboardSetUnicodeString(e, (nuint)value.Length, value); CGEventPost(0, e); CGEventSetType(e, 11); CGEventPost(0, e); }
                finally { CFRelease(e); }
            }
        }
        else RunTool(LinuxTool!, Wayland ? ["--", text] : ["type", "--delay", "1", "--", text]);
    }
    private static void WindowsChord(string[] keys, int duration)
    {
        var pressed = new List<ushort>();
        try
        {
            foreach (var name in keys)
            {
                var key = WindowsKeys[name];
                if ((GetAsyncKeyState(key) & 0x8000) != 0) throw new InvalidOperationException("请先松开电脑上已按住的组合键");
                WinKey(key, 0, 0); pressed.Add(key);
            }
            Thread.Sleep(duration);
        }
        finally { foreach (var key in pressed.AsEnumerable().Reverse()) { try { WinKey(key, 0, 2); } catch (IOException) { } } }
    }
    private static void MacChord(string[] keys, int duration)
    {
        var resolved = keys.Select(x => MacKeys.TryGetValue(x, out var key) ? key : throw new ArgumentException("该键尚不支持 macOS")).ToArray();
        var pressed = new List<ushort>();
        try
        {
            foreach (var key in resolved)
            {
                if (CGEventSourceKeyState(1, key)) throw new InvalidOperationException("请先松开电脑上已按住的组合键");
                MacKey(key, true); pressed.Add(key);
            }
            Thread.Sleep(duration);
        }
        finally { foreach (var key in pressed.AsEnumerable().Reverse()) MacKey(key, false); }
    }
    private static void LinuxChord(string[] keys)
    {
        var names = keys.Select(x => x switch { "PRIMARY" or "CTRL" or "CONTROL" => "ctrl", "WIN" or "COMMAND" => "super", "ALT" or "OPTION" => "alt", "SHIFT" => "shift",
            "ENTER" => "Return", "BACKSPACE" => "BackSpace", "ESC" => "Escape", "PAGEUP" => "Prior", "PAGEDOWN" => "Next", "SPACE" => "space", "PRINTSCREEN" => "Print",
            "VOLUMEMUTE" => "XF86AudioMute", "VOLUMEUP" => "XF86AudioRaiseVolume", "VOLUMEDOWN" => "XF86AudioLowerVolume", _ => x.Length == 1 ? x.ToLowerInvariant() : char.ToUpperInvariant(x[0]) + x[1..].ToLowerInvariant() }).ToArray();
        if (!Wayland) { RunTool(LinuxTool!, ["key", "--", string.Join('+', names)]); return; }
        var args = new List<string>(); var modifiers = keys.Select((x, i) => (x, i)).Where(x => IsModifier(x.x)).Select(x => names[x.i]).ToArray();
        foreach (var modifier in modifiers) { args.Add("-M"); args.Add(modifier); }
        foreach (var key in keys.Select((x, i) => (x, i)).Where(x => !IsModifier(x.x))) { args.Add("-k"); args.Add(names[key.i]); }
        foreach (var modifier in modifiers.Reverse()) { args.Add("-m"); args.Add(modifier); }
        RunTool(LinuxTool!, args);
    }
    internal static string? FindProgram(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        { var path = Path.Combine(directory, name); if (File.Exists(path)) return Path.GetFullPath(path); }
        return null;
    }
    private static void RunTool(string executable, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var p = Process.Start(start) ?? throw new IOException("无法启动输入工具");
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(5000)) { p.Kill(true); throw new IOException("输入辅助工具超时"); }
        if (p.ExitCode != 0) throw new IOException("输入失败，请检查桌面权限或显示服务器兼容性");
        error.GetAwaiter().GetResult();
    }
    // Capture only local focus metadata, never the contents of the focused application.
    internal Action<string>? PrepareVoiceInsertion()
    {
        if (!Available) return null;
        var before = Focus(); if (before is null) return null;
        return text =>
        {
            lock (gate)
            {
                ValidateVoiceInsertionText(text);
                if (Focus() != before) throw new IOException("输入焦点已改变");
                if (OperatingSystem.IsWindows() && new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0))
                    throw new IOException("请松开电脑组合键后复制文字");
                if (OperatingSystem.IsMacOS() && new ushort[] { 54, 55, 56, 58, 59, 60, 61, 62, 63 }.Any(k => CGEventSourceKeyState(1, k)))
                    throw new IOException("请松开电脑组合键后复制文字");
                if (OperatingSystem.IsLinux() && X11ModifiersHeld())
                    throw new IOException("请松开电脑组合键后复制文字");
                Execute("text", text, null, null);
            }
        };
    }
    internal static void ValidateVoiceInsertionText(string text)
    {
        if (text.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029'))
            throw new IOException("结果包含控制字符，请从记录复制文字");
    }
    internal static bool HasUnsafeX11Modifiers(uint mask) => (mask & (1U | 4U | 8U | 32U | 64U | 128U)) != 0;
    private static bool X11ModifiersHeld()
    {
        if (!X11Threads.Value) throw new IOException("无法检查桌面组合键，请从记录复制文字");
        var display = XOpenDisplay(0);
        if (display == 0) throw new IOException("无法检查桌面组合键，请从记录复制文字");
        try
        {
            if (XQueryPointer(display, XDefaultRootWindow(display), out _, out _, out _, out _, out _, out _, out var mask) == 0)
                throw new IOException("无法检查桌面组合键，请从记录复制文字");
            return HasUnsafeX11Modifiers(mask);
        }
        finally { XCloseDisplay(display); }
    }
    private static string? Focus()
    {
        if (OperatingSystem.IsWindows())
        {
            var window = GetForegroundWindow(); if (window == 0) return null;
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            var thread = GetWindowThreadProcessId(window, out _);
            return GetGUIThreadInfo(thread, ref info) && info.Focus != 0 ? $"{window}:{info.Focus}" : null;
        }
        if (OperatingSystem.IsMacOS())
        {
            var root = AXUIElementCreateSystemWide(); var attribute = CFStringCreateWithCString(0, "AXFocusedUIElement", 0x08000100);
            nint focused = 0;
            try { return AXUIElementCopyAttributeValue(root, attribute, out focused) == 0 && focused != 0 && AXUIElementGetPid(focused, out var pid) == 0 ? $"{pid}:{CFHash(focused)}" : null; }
            finally { if (focused != 0) CFRelease(focused); CFRelease(attribute); CFRelease(root); }
        }
        if (Wayland || LinuxTool is null) return null; // Wayland intentionally does not expose global focus.
        try
        {
            var s = new ProcessStartInfo(LinuxTool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            s.ArgumentList.Add("getwindowfocus"); using var p = Process.Start(s); if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync(); var errors = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(1000)) { p.Kill(true); return null; }
            var value = output.GetAwaiter().GetResult().Trim(); errors.GetAwaiter().GetResult();
            return p.ExitCode == 0 && ulong.TryParse(value, out _) ? value : null;
        }
        catch (IOException) { return null; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo { public uint Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")] private static extern nint AXUIElementCreateSystemWide();
    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")] private static extern int AXUIElementCopyAttributeValue(nint element, nint attribute, out nint value);
    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")] private static extern int AXUIElementGetPid(nint element, out int pid);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern nuint CFHash(nint value);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public Keyboard Key; [FieldOffset(0)] public Mouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard { public ushort Vk, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    private static void WinKey(ushort key, ushort scan, uint flags)
    { var input = new Input { Type = 1, Data = new InputUnion { Key = new() { Vk = key, Scan = scan, Flags = flags } } }; if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1) throw new IOException("输入被系统阻止"); }
    private static void MacKey(ushort key, bool down) { var e = CGEventCreateKeyboardEvent(0, key, down); if (e == 0) throw new IOException("输入事件失败"); try { CGEventPost(0, e); } finally { CFRelease(e); } }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrusted();
    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")] private static extern nint CGEventCreateKeyboardEvent(nint source, ushort key, [MarshalAs(UnmanagedType.I1)] bool down);
    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")] private static extern void CGEventKeyboardSetUnicodeString(nint e, nuint length, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")] private static extern void CGEventPost(int tap, nint e);
    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")] private static extern void CGEventSetType(nint e, uint type);
    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")] private static extern void CGEventSetFlags(nint e, ulong flags);
    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGEventSourceKeyState(int source, ushort key);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(nint value);
    [DllImport("libX11.so.6")] private static extern int XInitThreads();
    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] private static extern nuint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] private static extern int XQueryPointer(nint display, nuint window, out nuint root, out nuint child, out int rootX, out int rootY, out int windowX, out int windowY, out uint mask);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
}
