using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AmbientMonitor;

// WPF hosts only the HWND; Direct3D presents into it without a WPF bitmap bridge.
internal sealed class GpuSurface(GpuRenderService service, int width, int height, OverlaySettings settings) : HwndHost
{
    private nint _window;
    private OverlaySettings _settings = settings;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _window = CreateWindowEx(0, "static", "Ambient Monitor GPU", 0x50000000,
            0, 0, width, height, hwndParent.Handle, 0, 0, 0);
        if (_window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        service.Add(_window, width, height, _settings);
        return new HandleRef(this, _window);
    }

    public void UpdateSettings(OverlaySettings settings)
    {
        _settings = settings;
        if (_window != 0) service.Update(_window, settings);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // Release the swap chain on its owning thread before destroying its HWND.
        service.Remove(hwnd.Handle);
        DestroyWindow(hwnd.Handle);
        _window = 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
}
