using System.Runtime.InteropServices;

namespace AmbientMonitor;

internal static class GpuOutputSelection
{
    // Resolve against the current native display topology, not WinForms' cached
    // primary screen/device-name data retained across renderer restarts.
    public static nint PrimaryMonitor() => MonitorFromPoint(new NativePoint(), 1);
    public static PrimaryDisplay CurrentPrimary()
    {
        nint monitor = PrimaryMonitor();
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not read the current primary display.");
        return new PrimaryDisplay(monitor, info.DeviceName, info.Bounds.Left, info.Bounds.Top, info.Bounds.Right, info.Bounds.Bottom);
    }
    public static bool MatchesCurrent(PrimaryDisplay primary, nint outputMonitor, string outputName, bool attached, int left, int top, int right, int bottom)
    {
        // DXGI may retain stale AttachedToDesktop/geometry during a GDI/WPF to
        // D3D transition. An exact current native monitor/device match is authoritative.
        if (outputMonitor != 0 && outputMonitor == primary.Monitor) return true;
        if (!string.IsNullOrEmpty(outputName) && outputName.Equals(primary.DeviceName, StringComparison.OrdinalIgnoreCase)) return true;
        return IsPrimary(attached, outputMonitor, primary.Monitor, left, top, right, bottom);
    }
    public static bool IsPrimary(bool attached, nint outputMonitor, nint primaryMonitor, int left, int top, int right, int bottom)
        => attached && right > left && bottom > top && ((primaryMonitor != 0 && outputMonitor == primaryMonitor) ||
            (left <= 0 && top <= 0 && right > 0 && bottom > 0));

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Bounds, WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}

internal readonly record struct PrimaryDisplay(nint Monitor, string DeviceName, int Left, int Top, int Right, int Bottom);
