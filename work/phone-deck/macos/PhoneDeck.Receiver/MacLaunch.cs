using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneDeck.MacReceiver;

/// <summary>
/// 双击 App 时的体验：已有接收端在运行就只打开它的状态页后退出（不抢端口）；
/// 缺辅助功能权限时请系统弹出授权提示（并把本 App 加进“辅助功能”列表），同时打开状态页说明下一步。
/// </summary>
internal static class MacLaunch
{
    internal const string StatusPageUrl = "http://127.0.0.1:8765/admin/pairing";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    /// <summary>本机 8765 已经是言渡接收端在应答。</summary>
    internal static bool ReceiverAlreadyRunning()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
            var body = http.GetStringAsync("http://127.0.0.1:8765/api/health").GetAwaiter().GetResult();
            using var health = System.Text.Json.JsonDocument.Parse(body);
            return health.RootElement.TryGetProperty("name", out var name) && name.GetString() == "PhoneDeck";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
            or System.Text.Json.JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static void OpenStatusPage()
    {
        try
        {
            using var _ = Process.Start("/usr/bin/open", StatusPageUrl);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"请在浏览器打开 {StatusPageUrl}");
        }
    }

    /// <summary>AXIsProcessTrustedWithOptions(prompt: true)：未授权时系统弹出授权提示。返回当前是否已授权。</summary>
    internal static bool RequestAccessibility()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }
        try
        {
            var cf = NativeLibrary.Load(CoreFoundation);
            var ax = NativeLibrary.Load(ApplicationServices);
            var promptKey = Marshal.ReadIntPtr(NativeLibrary.GetExport(ax, "kAXTrustedCheckOptionPrompt"));
            var yes = Marshal.ReadIntPtr(NativeLibrary.GetExport(cf, "kCFBooleanTrue"));
            var keyCallbacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks");
            var valueCallbacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks");
            var options = CFDictionaryCreate(IntPtr.Zero, [promptKey], [yes], 1, keyCallbacks, valueCallbacks);
            try
            {
                return AXIsProcessTrustedWithOptions(options);
            }
            finally
            {
                CFRelease(options);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count,
        IntPtr keyCallbacks, IntPtr valueCallbacks);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);
}
