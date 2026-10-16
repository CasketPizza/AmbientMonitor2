using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WinForms = System.Windows.Forms;

namespace AmbientMonitor;

public partial class OverlayWindow : Window, IOverlayWindow
{
    private readonly string _deviceName;
    private readonly System.Drawing.Rectangle _bounds;
    private readonly GpuSurface? _surface;
    private readonly CpuSurface? _cpuSurface;
    private OverlaySettings _settings;
    private readonly OverlayLayerService _layers;
    private readonly bool _ownsLayers;
    private nint _layerHandle;
    public string DeviceName => _deviceName;
    public nint LayerHandle => _layerHandle;

    internal OverlayWindow(WinForms.Screen screen, OverlaySettings settings, GpuRenderService? service, CpuRenderService? cpuService = null, OverlayLayerService? layers = null)
        : this(screen.DeviceName, screen.Bounds, settings, service, cpuService, layers) { }

    internal OverlayWindow(string deviceName, System.Drawing.Rectangle bounds, OverlaySettings settings, GpuRenderService? service, CpuRenderService? cpuService, OverlayLayerService? layers)
    {
        InitializeComponent();
        _deviceName = deviceName; _bounds = bounds;
        _settings = settings.Snapshot();
        _ownsLayers = layers is null;
        _layers = layers ?? new OverlayLayerService(Dispatcher);
        if (service is not null)
        {
            _surface = new GpuSurface(service, bounds.Width, bounds.Height, settings);
            Content = _surface;
        }
        else
        {
            _cpuSurface = new CpuSurface(cpuService ?? throw new ArgumentNullException(nameof(cpuService)), bounds.Width, bounds.Height, settings);
            Content = _cpuSurface;
        }
        Left = bounds.Left; Top = bounds.Top;
        Width = bounds.Width; Height = bounds.Height;
        Topmost = settings.Layer is OverlayLayer.Topmost or OverlayLayer.StayTopmost;
        Loaded += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // Use physical desktop coordinates even on mixed-DPI monitor setups.
            SetWindowPos(handle, new nint(-2), _bounds.Left, _bounds.Top,
                _bounds.Width, _bounds.Height, 0x0010 | 0x0200);
            SetWindowDisplayAffinity(handle, 0x11); // Exclude overlays from capture feedback.
            ApplyWindowStyle();
            _layerHandle = handle;
            _layers.Register(handle, _settings.Layer, _settings.Enabled);
            _cpuSurface?.Start();
        };
        ContentRendered += (_, _) =>
        {
            if (_layerHandle != 0) _layers.Reapply(_layerHandle);
        };
        Closed += (_, _) =>
        {
            if (_layerHandle != 0) _layers.Unregister(_layerHandle);
            if (_ownsLayers) _layers.Dispose();
            _surface?.Dispose(); _cpuSurface?.Dispose();
        };
    }

    public void UpdateSettings(OverlaySettings settings)
    {
        bool inputChanged = _settings.ClickThrough != settings.ClickThrough;
        _settings = settings.Snapshot();
        Topmost = settings.Layer is OverlayLayer.Topmost or OverlayLayer.StayTopmost;
        Visibility = settings.Enabled ? Visibility.Visible : Visibility.Hidden;
        _surface?.UpdateSettings(settings);
        _cpuSurface?.UpdateSettings(settings);
        if (inputChanged) ApplyWindowStyle();
        if (_layerHandle != 0) _layers.Update(_layerHandle, settings.Layer, settings.Enabled);
    }

    private void ApplyWindowStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        long style = GetWindowLongPtr(handle, -20).ToInt64() | 0x80L | 0x8000000L;
        if (_settings.ClickThrough)
        {
            style |= 0x20L | 0x80000L;
            SetWindowLongPtr(handle, -20, new nint(style));
            // Full alpha permits HWND GPU presentation while the layered window's
            // transparent style routes mouse input through to other applications.
            SetLayeredWindowAttributes(handle, 0, 255, 0x2);
        }
        else SetWindowLongPtr(handle, -20, new nint(style & ~(0x20L | 0x80000L)));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
}
