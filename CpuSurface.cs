using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AmbientMonitor;

internal sealed class CpuSurface(CpuRenderService service, int width, int height, OverlaySettings settings)
    : System.Windows.Controls.Image, IDisposable
{
    private Guid? _id;
    private OverlaySettings _settings = settings;
    private BitmapSource? _latest;
    private bool _presentationQueued;
    private readonly object _frameLock = new();
    private bool _disposed;

    public void Start()
    {
        if (_id is not null || _disposed) return;
        Stretch = Stretch.Fill;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        _id = service.Add(width, height, _settings, Present);
    }
    public void UpdateSettings(OverlaySettings settings)
    {
        _settings = settings;
        if (_id is { } id) service.Update(id, settings);
    }
    private void Present(BitmapSource frame)
    {
        lock (_frameLock)
        {
            if (_disposed) return;
            _latest = frame;
            if (_presentationQueued) return;
            _presentationQueued = true;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            BitmapSource? latest;
            lock (_frameLock) { latest = _latest; _latest = null; _presentationQueued = false; }
            if (!_disposed && latest is not null) Source = latest;
        }));
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_id is { } id) service.Remove(id);
        _id = null; _latest = null; Source = null;
    }
}
