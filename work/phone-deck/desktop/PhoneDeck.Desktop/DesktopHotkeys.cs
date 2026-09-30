using System.Runtime.InteropServices;

namespace PhoneDeck.Desktop;

internal sealed class DesktopHotkeys : IDisposable
{
    private readonly SpeechSession speech;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Thread worker;
    private uint windowsThread;
    private nint macLoop;
    private CarbonHandler? carbonHandler;
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
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
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
        var types = new[] { new CarbonEventType { Class = 0x6B657962, Kind = 6 }, new CarbonEventType { Class = 0x6B657962, Kind = 9 } };
        carbonHandler = (_, ev, _) =>
        {
            if (GetEventParameter(ev, 0x2D2D2D2D, 0x686B6964, 0, 8, out _, out var id) != 0) return 0;
            var kind = GetEventKind(ev);
            if (id.Id == 1 && kind == 6) Toggle();
            if (id.Id == 2 && kind == 6) Start();
            if (id.Id == 2 && kind == 9) _ = speech.StopLocalAsync();
            return 0;
        };
        if (InstallApplicationEventHandler(carbonHandler, 2, types, 0, out var handler) != 0) { status = "unavailable"; return; }
        var toggle = RegisterEventHotKey(49, 0x1800, new() { Signature = 0x5048444B, Id = 1 }, GetApplicationEventTarget(), 0, out var toggleRef);
        var hold = RegisterEventHotKey(9, 0x1800, new() { Signature = 0x5048444B, Id = 2 }, GetApplicationEventTarget(), 0, out var holdRef);
        status = toggle == 0 && hold == 0 ? "ready" : "shortcut-conflict";
        macLoop = CFRunLoopGetCurrent();
        try { CFRunLoopRun(); }
        finally { if (toggle == 0) UnregisterEventHotKey(toggleRef); if (hold == 0) UnregisterEventHotKey(holdRef); RemoveEventHandler(handler); }
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
        cancellation.Cancel(); if (macLoop != 0) CFRunLoopStop(macLoop);
        if (windowsThread != 0) PostThreadMessage(windowsThread, 0, 0, 0);
        worker.Join(1000);
    }
    [StructLayout(LayoutKind.Sequential)] private struct WinMessage { public nint Window; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] private struct CarbonEventType { public uint Class, Kind; }
    [StructLayout(LayoutKind.Sequential)] private struct CarbonHotkeyId { public uint Signature, Id; }
    [StructLayout(LayoutKind.Explicit, Size = 192)] private struct XEvent { [FieldOffset(0)] public int Type; [FieldOffset(84)] public uint KeyCode; }
    private delegate int CarbonHandler(nint next, nint ev, nint data);
    private delegate int XErrorHandler(nint display, nint error);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out WinMessage message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint id, uint msg, nuint wparam, nint lparam);
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [DllImport(Carbon)] private static extern int InstallApplicationEventHandler(CarbonHandler callback, uint count, CarbonEventType[] types, nint data, out nint handler);
    [DllImport(Carbon)] private static extern int RemoveEventHandler(nint handler);
    [DllImport(Carbon)] private static extern int RegisterEventHotKey(uint code, uint modifiers, CarbonHotkeyId id, nint target, uint options, out nint reference);
    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(nint reference);
    [DllImport(Carbon)] private static extern nint GetApplicationEventTarget();
    [DllImport(Carbon)] private static extern uint GetEventKind(nint ev);
    [DllImport(Carbon)] private static extern int GetEventParameter(nint ev, uint name, uint type, nint actualType, uint size, out uint actualSize, out CarbonHotkeyId id);
    [DllImport(CoreFoundation)] private static extern nint CFRunLoopGetCurrent();
    [DllImport(CoreFoundation)] private static extern void CFRunLoopRun();
    [DllImport(CoreFoundation)] private static extern void CFRunLoopStop(nint loop);
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
