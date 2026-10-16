using System.Runtime.InteropServices;

namespace AmbientMonitor;

internal static class NativeAcrylic
{
    internal static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
    internal static byte TintAlpha(double transparency)
    {
        double value = double.IsFinite(transparency) ? Math.Clamp(transparency, 0, 1) : 0.7;
        return (byte)Math.Round((1 - value) * 255);
    }
    internal static bool Apply(nint window, bool enabled)
    {
        if (!IsSupported || window == 0) return false;
        // DWMWA_SYSTEMBACKDROP_TYPE: Desktop Acrylic (3), or None (1).
        // Keep the supported DWM material active under WPF's software-rendered
        // surface. A successful legacy accent call did not establish a working
        // backdrop, and disabling this material produced an opaque black client.
        int backdrop = enabled ? 3 : 1;
        return DwmSetWindowAttribute(window, 38, ref backdrop, sizeof(int)) >= 0;
    }
    internal static int? GetBackdrop(nint window) => DwmGetWindowAttribute(window, 38, out int value, sizeof(int)) >= 0 ? value : null;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
