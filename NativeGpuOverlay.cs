using System.ComponentModel;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace AmbientMonitor;

// GPU overlays have no WPF HwndTarget/MIL render surface. DXGI owns the child
// presentation HWND, and it is released before either native window is destroyed.
internal sealed class NativeGpuOverlay : IOverlayWindow
{
    private const string ClassName = "AmbientMonitorGpuOverlay";
    private static readonly WindowProcedure Procedure = WindowMessage;
    private static readonly Dictionary<nint, NativeGpuOverlay> Windows = new();
    private static ushort _windowClass;
    private readonly string _deviceName;
    private readonly System.Drawing.Rectangle _bounds;
    private readonly GpuRenderService _renderer;
    private readonly OverlayLayerService _layers;
    private OverlaySettings _settings;
    private nint _window, _surface;
    private bool _closing;
    public string DeviceName => _deviceName;
    public nint LayerHandle => _window;

    public NativeGpuOverlay(WinForms.Screen screen, OverlaySettings settings, GpuRenderService renderer, OverlayLayerService layers)
        : this(screen.DeviceName, screen.Bounds, settings, renderer, layers) { }

    internal NativeGpuOverlay(string deviceName, System.Drawing.Rectangle bounds, OverlaySettings settings, GpuRenderService renderer, OverlayLayerService layers)
    {
        _deviceName = deviceName; _bounds = bounds; _settings = settings.Snapshot(); _renderer = renderer; _layers = layers;
    }
    public void Show()
    {
        if (_closing || _window != 0) return;
        EnsureWindowClass();
        var bounds = _bounds;
        _window = CreateWindowEx(0x80 | 0x8000000, ClassName, "Ambient Monitor overlay", 0x80000000 | 0x02000000,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0, 0, GetModuleHandle(null), 0);
        if (_window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        Windows[_window] = this;
        try
        {
            ApplyInputStyle();
            SetWindowDisplayAffinity(_window, 0x11);
            _surface = CreateWindowEx(0, ClassName, "Ambient Monitor GPU", 0x50000000,
                0, 0, bounds.Width, bounds.Height, _window, 0, GetModuleHandle(null), 0);
            if (_surface == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _renderer.Add(_surface, bounds.Width, bounds.Height, _settings);
            ShowWindow(_window, 4);
            // Apply only after native ShowWindow has completed its initial ordering.
            _layers.Register(_window, _settings.Layer, _settings.Enabled);
        }
        catch { Close(); throw; }
    }
    public void UpdateSettings(OverlaySettings settings)
    {
        if (_closing) return;
        var previous = _settings;
        _settings = settings.Snapshot();
        if (_window == 0) return;
        if (previous.ClickThrough != _settings.ClickThrough) ApplyInputStyle();
        if (previous.Enabled != _settings.Enabled) ShowWindow(_window, _settings.Enabled ? 4 : 0);
        if (_surface != 0) _renderer.Update(_surface, _settings);
        _layers.Update(_window, _settings.Layer, _settings.Enabled);
    }
    private void ApplyInputStyle()
    {
        long style = GetWindowLongPtr(_window, -20).ToInt64() | 0x80L | 0x8000000L;
        if (_settings.ClickThrough)
        {
            SetWindowLongPtr(_window, -20, new nint(style | 0x20L | 0x80000L));
            SetLayeredWindowAttributes(_window, 0, 255, 2);
        }
        else SetWindowLongPtr(_window, -20, new nint(style & ~(0x20L | 0x80000L)));
    }
    public void Close()
    {
        if (_closing) return;
        _closing = true;
        if (_window == 0) return;
        _layers.Unregister(_window);
        // Stop presentation on the renderer thread while both HWNDs are valid.
        if (_surface != 0)
        {
            _renderer.Remove(_surface);
            DestroyWindow(_surface);
            _surface = 0;
        }
        nint window = _window;
        Windows.Remove(window);
        _window = 0;
        DestroyWindow(window);
    }
    private static nint WindowMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == 0x10 && Windows.TryGetValue(window, out var overlay)) { overlay.Close(); return 0; }
        if (message == 0x21) return 3; // MA_NOACTIVATE
        // GDI must not erase a cached DXGI frame between presentations.
        if (message == 0x14) return 1; // WM_ERASEBKGND
        if (message == 0x0F) { ValidateRect(window, 0); return 0; } // WM_PAINT
        return DefWindowProc(window, message, wParam, lParam);
    }
    private static void EnsureWindowClass()
    {
        if (_windowClass != 0) return;
        var definition = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Procedure,
            Instance = GetModuleHandle(null), Background = GetStockObject(4), Name = ClassName
        };
        _windowClass = RegisterClassEx(ref definition);
        if (_windowClass == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public WindowProcedure Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string Name;
        public nint SmallIcon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass definition);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool ValidateRect(nint window, nint rectangle);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
}
