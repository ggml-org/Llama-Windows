using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace LlamaApp;

/// <summary>
/// Sets a window's DWM corner preference. Windows 11 rounds top-level window
/// corners by default, but the radius is the system's choice — pinning the
/// preference guarantees the standard 8px "round" corners for every LlamaApp
/// window (tray flyout, chat overlay, settings) regardless of the system
/// default. Ignored on Windows 10, where the attribute doesn't exist.
/// </summary>
internal static class WindowCorners
{
    // DWMWA_WINDOW_CORNER_PREFERENCE (Windows 11+). Values: DEFAULT = 0 (let
    // the system decide), DONOTROUND = 1 (square), ROUND = 2 (8px radius),
    // ROUNDSMALL = 3 (4px radius).
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>Pins the window's corners to the standard 8px round style.</summary>
    public static void ApplyRound8(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
