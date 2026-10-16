using System.Runtime.InteropServices;

namespace AmbientMonitor;

internal static class WindowChromeHelper
{
    internal static void RefreshFrame(nint window)
    {
        if (window != 0) SetWindowPos(window, 0, 0, 0, 0, 0, 0x0037); // FRAMECHANGED, no move/size/z-order/activation
    }
    public static void MatchCaptionSurface(nint window, bool dark = true, bool acrylic = false)
    {
        // Match any DWM non-client caption pixels to the Fluent client surface.
        // Windows 10 simply ignores this Windows 11 attribute.
        int captionColor = acrylic ? unchecked((int)0xFFFFFFFF) : dark ? 0x00343030 : 0x00F6F2F2;
        DwmSetWindowAttribute(window, 35, ref captionColor, sizeof(int));
        int useDark = dark ? 1 : 0;
        DwmSetWindowAttribute(window, 20, ref useDark, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
