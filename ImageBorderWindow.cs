using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;

namespace AmbientMonitor;

// Static transparent image borders use WPF composition in either engine; no capture or frame loop.
internal sealed class ImageBorderWindow : Window, IOverlayWindow
{
    private readonly System.Drawing.Rectangle _bounds;
    private readonly OverlayLayerService _layers;
    private readonly System.Windows.Controls.Image _image = new() { Stretch = Stretch.Fill };
    private BitmapSource? _source;
    private string _path = "";
    private OverlaySettings _settings;
    public string DeviceName { get; }
    public nint LayerHandle { get; private set; }

    public ImageBorderWindow(string deviceName, System.Drawing.Rectangle bounds, OverlaySettings settings, OverlayLayerService layers)
    {
        DeviceName = deviceName; _bounds = bounds; _layers = layers; _settings = settings.Snapshot();
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowActivated = false; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
        Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
        Content = _image;
        UpdateImage();
        Loaded += (_, _) =>
        {
            LayerHandle = new WindowInteropHelper(this).Handle;
            SetWindowPos(LayerHandle, -2, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0010 | 0x0200);
            SetWindowDisplayAffinity(LayerHandle, 0x11);
            ApplyInput(); _layers.Register(LayerHandle, _settings.Layer, _settings.Enabled);
        };
        ContentRendered += (_, _) => { if (LayerHandle != 0) _layers.Reapply(LayerHandle); };
        Closed += (_, _) => { if (LayerHandle != 0) _layers.Unregister(LayerHandle); _image.Source = null; _source = null; };
    }
    public void UpdateSettings(OverlaySettings settings)
    {
        _settings = settings.Snapshot();
        UpdateImage();
        Visibility = settings.Enabled ? Visibility.Visible : Visibility.Hidden;
        if (LayerHandle != 0) { ApplyInput(); _layers.Update(LayerHandle, settings.Layer, settings.Enabled); }
    }
    private void UpdateImage()
    {
        if (_path != _settings.ImagePath)
        {
            var loaded = string.IsNullOrWhiteSpace(_settings.ImagePath) ? null : ImageBorderDrawing.Load(_settings.ImagePath);
            _source = loaded; _path = _settings.ImagePath;
        }
        _image.Source = _source is null ? null : new DrawingImage(ImageBorderDrawing.Create(_source, _settings, _bounds.Width, _bounds.Height));
    }
    private void ApplyInput()
    {
        long style = GetWindowLongPtr(LayerHandle, -20).ToInt64() | 0x80L | 0x08000000L;
        SetWindowLongPtr(LayerHandle, -20, new nint(_settings.ClickThrough ? style | 0x20L : style & ~0x20L));
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
}
