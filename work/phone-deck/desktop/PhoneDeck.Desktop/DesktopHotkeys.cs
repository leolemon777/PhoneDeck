using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneDeck.Desktop;

internal sealed class DesktopHotkeys : IDisposable
{
    private readonly SpeechSession speech;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Thread worker;
    private uint windowsThread;
    private string status = "checking";
    internal string Status => status;
    internal DesktopHotkeys(SpeechSession speech)
    {
        this.speech = speech;
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
            else status = "wayland-custom-shortcut";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or Win32Exception or IOException)
        { status = "unavailable"; }
    }
    private void Toggle() { if (speech.Recording) _ = speech.StopLocalAsync(); else Start(); }
    private void Start() { try { speech.StartLocal(); } catch (InvalidOperationException) { } }
    private void RunWindows()
    {
        windowsThread = GetCurrentThreadId();
        PeekMessage(out _, 0, 0, 0, 0); // Create the thread message queue before registering/shutting down.
        var toggle = RegisterHotKey(0, 1, 0x4003, 0x20); // Ctrl+Alt+Space, no repeat
        var hold = RegisterHotKey(0, 2, 0x4003, 0x56); // Ctrl+Alt+V
        status = toggle && hold ? "ready" : "shortcut-conflict";
        var holding = false;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                while (PeekMessage(out var message, 0, 0, 0, 1))
                {
                    if (message.Message == 0x312 && message.WParam == 1) Toggle();
                    if (message.Message == 0x312 && message.WParam == 2 && !holding) { Start(); holding = speech.Recording; }
                }
                if (holding && (GetAsyncKeyState(0x56) & 0x8000) == 0) { holding = false; _ = speech.StopLocalAsync(); }
                cancellation.Token.WaitHandle.WaitOne(20);
            }
        }
        finally { if (toggle) UnregisterHotKey(0, 1); if (hold) UnregisterHotKey(0, 2); if (holding) _ = speech.StopLocalAsync(true); }
    }
    private void RunMac()
    {
        // The helper owns a native application event loop on its OS main thread.
        // Only fixed hotkey commands cross this pipe; it accepts no remote arguments.
        using var helper = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "hotkey-runtime", "phonedeck-hotkeys"),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        } };
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
                switch (command)
                {
                    case "ready": case "shortcut-conflict": case "unavailable": status = command; break;
                    case "toggle": Toggle(); break;
                    case "start": Start(); holding = speech.Recording; break;
                    case "stop": holding = false; _ = speech.StopLocalAsync(); break;
                    default: status = "unavailable"; return;
                }
            }
        }
        finally
        {
            KillHelper();
            helper.WaitForExit(1000);
            if (holding) _ = speech.StopLocalAsync(true);
            if (!cancellation.IsCancellationRequested) status = "unavailable";
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
        var toggleKey = XKeysymToKeycode(display, 0x20); var holdKey = XKeysymToKeycode(display, 0x76);
        foreach (var modifier in new uint[] { 12, 14, 28, 30 }) { XGrabKey(display, toggleKey, modifier, root, false, 1, 1); XGrabKey(display, holdKey, modifier, root, false, 1, 1); }
        XSync(display, false); status = conflict == 0 ? "ready" : "shortcut-conflict";
        var toggleDown = false; var holdDown = false;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                while (XPending(display) > 0)
                {
                    XNextEvent(display, out var ev);
                    if (ev.Type == 2 && ev.KeyCode == toggleKey && !toggleDown) { toggleDown = true; Toggle(); }
                    if (ev.Type == 2 && ev.KeyCode == holdKey && !holdDown) { holdDown = true; Start(); }
                    if (ev.Type == 3 && ev.KeyCode == toggleKey) toggleDown = false;
                    if (ev.Type == 3 && ev.KeyCode == holdKey) { holdDown = false; _ = speech.StopLocalAsync(); }
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
        worker.Join(1000);
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
