using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneDeck.Desktop;

internal sealed class DesktopHotkeys : IDisposable
{
    private readonly SpeechSession speech;
    private readonly string tapShortcut, holdShortcut;
    private readonly CancellationTokenSource cancellation = new();
    private readonly ManualResetEventSlim initialized = new();
    private readonly Thread worker;
    private uint windowsThread;
    private volatile string status = "checking";
    internal string Status => status;
    internal string WaitUntilReady() { initialized.Wait(TimeSpan.FromSeconds(3)); return status; }
    internal DesktopHotkeys(SpeechSession speech, string tapShortcut = "Ctrl+Alt+Space", string holdShortcut = "Ctrl+Alt+V")
    {
        this.speech = speech;
        if (!VoiceShortcutOptions.Contains(tapShortcut) || !VoiceShortcutOptions.Contains(holdShortcut) || tapShortcut == holdShortcut)
            throw new ArgumentException("语音快捷键配置无效");
        this.tapShortcut = tapShortcut; this.holdShortcut = holdShortcut;
        worker = new Thread(Run) { IsBackground = true, Name = "PhoneDeckDesktopKeys" };
        worker.Start();
    }
    private void Run()
    {
        try
        {
            if (OperatingSystem.IsWindows()) RunWindows();
            else if (OperatingSystem.IsMacOS()) RunMac();
            else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) RunX11();
            else { status = "wayland-custom-shortcut"; initialized.Set(); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or Win32Exception or IOException)
        { status = "unavailable"; }
        finally { initialized.Set(); }
    }
    private void Toggle() { if (speech.Recording) _ = speech.StopLocalAsync(); else Start(); }
    private bool Start() { try { speech.StartLocal(); return true; } catch (InvalidOperationException) { return false; } }
    private void RunWindows()
    {
        windowsThread = GetCurrentThreadId();
        PeekMessage(out _, 0, 0, 0, 0); // Create the thread message queue before registering/shutting down.
        var toggle = RegisterHotKey(0, 1, 0x4003, VoiceShortcutOptions.WindowsKey(tapShortcut));
        var holdKey = (int)VoiceShortcutOptions.WindowsKey(holdShortcut);
        var hold = RegisterHotKey(0, 2, 0x4003, (uint)holdKey);
        status = toggle && hold ? "ready" : "shortcut-conflict";
        initialized.Set();
        var holding = false;
        try
        {
            if (!toggle || !hold) return;
            while (!cancellation.IsCancellationRequested)
            {
                while (PeekMessage(out var message, 0, 0, 0, 1))
                {
                    if (message.Message == 0x312 && message.WParam == 1) Toggle();
                    if (message.Message == 0x312 && message.WParam == 2 && !holding) holding = Start();
                }
                if (holding && (GetAsyncKeyState(holdKey) & 0x8000) == 0) { holding = false; _ = speech.StopLocalAsync(); }
                cancellation.Token.WaitHandle.WaitOne(20);
            }
        }
        finally { if (toggle) UnregisterHotKey(0, 1); if (hold) UnregisterHotKey(0, 2); if (holding) _ = speech.StopLocalAsync(true); }
    }
    private void RunMac()
    {
        // The helper owns a native application event loop on its OS main thread.
        // Arguments are identifiers from the controlled shortcut table, never executable paths or commands.
        using var helper = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "hotkey-runtime", "phonedeck-hotkeys"),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        } };
        helper.StartInfo.ArgumentList.Add(tapShortcut); helper.StartInfo.ArgumentList.Add(holdShortcut);
        if (!helper.Start()) { status = "unavailable"; return; }
        // Drain native framework diagnostics without retaining or logging them.
        _ = helper.StandardError.BaseStream.CopyToAsync(Stream.Null);
        void KillHelper() { try { if (!helper.HasExited) helper.Kill(true); } catch (InvalidOperationException) { } catch (Win32Exception) { } }
        using var shutdown = cancellation.Token.Register(KillHelper);
        var holding = false;
        try
        {
            while (!cancellation.IsCancellationRequested && helper.StandardOutput.ReadLine() is { } command)
            {
                if (command == "ready:" + tapShortcut + ":" + holdShortcut) { status = "ready"; initialized.Set(); continue; }
                switch (command)
                {
                    // Older packaged helpers only implement the two defaults.
                    case "ready":
                        status = tapShortcut == "Ctrl+Alt+Space" && holdShortcut == "Ctrl+Alt+V" ? "ready" : "runtime-upgrade-required";
                        initialized.Set(); if (status != "ready") return; break;
                    case "shortcut-conflict": case "unavailable": status = command; initialized.Set(); break;
                    case "toggle": Toggle(); break;
                    case "start": holding = Start(); break;
                    case "stop": if (holding) { holding = false; _ = speech.StopLocalAsync(); } break;
                    default: status = "unavailable"; return;
                }
            }
        }
        finally
        {
            KillHelper();
            helper.WaitForExit(1000);
            if (holding) _ = speech.StopLocalAsync(true);
            if (!cancellation.IsCancellationRequested && status is not ("runtime-upgrade-required" or "shortcut-conflict")) status = "unavailable";
        }
    }
    private void RunX11()
    {
        var display = XOpenDisplay(0); if (display == 0) { status = "unavailable"; return; }
        XkbSetDetectableAutoRepeat(display, true, out _);
        var conflict = 0;
        XErrorHandler errorHandler = (_, _) => { Interlocked.Exchange(ref conflict, 1); return 0; };
        var previousErrorHandler = XSetErrorHandler(errorHandler);
        var root = XDefaultRootWindow(display);
        var toggleKey = XKeysymToKeycode(display, VoiceShortcutOptions.X11Keysym(tapShortcut)); var holdKey = XKeysymToKeycode(display, VoiceShortcutOptions.X11Keysym(holdShortcut));
        if (toggleKey == 0 || holdKey == 0) { XCloseDisplay(display); XRestoreErrorHandler(previousErrorHandler); status = "unavailable"; return; }
        foreach (var modifier in new uint[] { 12, 14, 28, 30 }) { XGrabKey(display, toggleKey, modifier, root, false, 1, 1); XGrabKey(display, holdKey, modifier, root, false, 1, 1); }
        XSync(display, false); status = conflict == 0 ? "ready" : "shortcut-conflict";
        initialized.Set();
        var toggleDown = false; var holdDown = false;
        try
        {
            if (conflict != 0) return;
            while (!cancellation.IsCancellationRequested)
            {
                while (XPending(display) > 0)
                {
                    XNextEvent(display, out var ev);
                    if (ev.Type == 2 && ev.KeyCode == toggleKey && !toggleDown) { toggleDown = true; Toggle(); }
                    if (ev.Type == 2 && ev.KeyCode == holdKey && !holdDown) holdDown = Start();
                    if (ev.Type == 3 && ev.KeyCode == toggleKey) toggleDown = false;
                    if (ev.Type == 3 && ev.KeyCode == holdKey && holdDown) { holdDown = false; _ = speech.StopLocalAsync(); }
                }
                cancellation.Token.WaitHandle.WaitOne(20);
            }
        }
        finally { XUngrabKey(display, 0, 1U << 15, root); XCloseDisplay(display); XRestoreErrorHandler(previousErrorHandler); GC.KeepAlive(errorHandler); }
    }
    public void Dispose()
    {
        cancellation.Cancel();
        if (windowsThread != 0) PostThreadMessage(windowsThread, 0, 0, 0);
        if (!worker.Join(4000)) throw new IOException("快捷键工作线程仍在退出，请稍后重试");
    }
    [StructLayout(LayoutKind.Sequential)] private struct WinMessage { public nint Window; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [StructLayout(LayoutKind.Explicit, Size = 192)] private struct XEvent { [FieldOffset(0)] public int Type; [FieldOffset(84)] public uint KeyCode; }
    private delegate int XErrorHandler(nint display, nint error);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out WinMessage message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint id, uint msg, nuint wparam, nint lparam);
    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] private static extern nuint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] private static extern byte XKeysymToKeycode(nint display, nuint symbol);
    [DllImport("libX11.so.6")] private static extern int XGrabKey(nint display, int key, uint modifiers, nuint root, [MarshalAs(UnmanagedType.Bool)] bool ownerEvents, int pointerMode, int keyboardMode);
    [DllImport("libX11.so.6")] private static extern int XUngrabKey(nint display, int key, uint modifiers, nuint root);
    [DllImport("libX11.so.6")] private static extern int XPending(nint display);
    [DllImport("libX11.so.6")] private static extern int XNextEvent(nint display, out XEvent ev);
    [DllImport("libX11.so.6")] private static extern int XFlush(nint display);
    [DllImport("libX11.so.6")] private static extern int XSync(nint display, [MarshalAs(UnmanagedType.Bool)] bool discard);
    [DllImport("libX11.so.6")] private static extern int XkbSetDetectableAutoRepeat(nint display, [MarshalAs(UnmanagedType.Bool)] bool enabled, [MarshalAs(UnmanagedType.Bool)] out bool supported);
    [DllImport("libX11.so.6")] private static extern nint XSetErrorHandler(XErrorHandler callback);
    [DllImport("libX11.so.6", EntryPoint = "XSetErrorHandler")] private static extern nint XRestoreErrorHandler(nint callback);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
}
