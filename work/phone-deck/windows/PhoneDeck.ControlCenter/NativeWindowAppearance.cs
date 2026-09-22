using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace PhoneDeck.ControlCenter;

// Let the desktop compositor round the opaque window, rather than maintaining
// an entire per-pixel transparent surface just to round its four corners.
internal static class NativeWindowAppearance
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    internal static void Initialize(Window window)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            const int WindowCornerPreference = 33;
            var round = 2;
            _ = DwmSetWindowAttribute(new WindowInteropHelper(window).Handle,
                WindowCornerPreference, ref round, sizeof(int));
        }
    }

    internal static void UpdateBorder(Window window, Border border)
    {
        var maximized = window.WindowState == WindowState.Maximized;
        border.CornerRadius = new CornerRadius(
            !maximized && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? 8 : 0);
        border.BorderThickness = new Thickness(maximized ? 0 : 1);
    }
}
