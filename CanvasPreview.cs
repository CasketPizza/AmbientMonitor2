using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using LinearGradientBrush = System.Drawing.Drawing2D.LinearGradientBrush;

namespace AmbientMonitor;

public sealed class CanvasPreview : FrameworkElement, IDisposable
{
    private OverlaySettings _settings = new();
    private int _width = 1920, _height = 1080;
    private BitmapSource? _image, _frame;
    private string _imagePath = "";
    private Rect _viewport;
    private Point? _dragStart;
    private Point _dragOffset;
    public bool Guides { get; set; } = true;
    public bool Snap { get; set; } = true;
    internal string Status { get; private set; } = "";
    internal event Action<double, double>? PositionChanged;
    internal void Update(OverlaySettings settings, int width, int height, double sampleAspect, int sourceWidth = 1920)
    {
        _settings = settings.Snapshot(); _width = Math.Max(16, width); _height = Math.Max(16, height);
        try
        {
            Status = settings.Mode == OverlayMode.Ambient ? "Sample reflection · no desktop capture" : settings.Mode == OverlayMode.Veil ? "Live veil preview" : "Live image preview · drag to position";
            if (settings.Mode == OverlayMode.ImageBorder)
            {
                if (_image is null || _imagePath != settings.ImagePath)
                { _image = string.IsNullOrWhiteSpace(settings.ImagePath) ? null : ImageBorderDrawing.Load(settings.ImagePath); _imagePath = settings.ImagePath; }
                _frame = null;
            }
            else
            {
                using var sample = new System.Drawing.Bitmap(320, Math.Clamp((int)Math.Round(320 / sampleAspect), 32, 640));
                using (var g = System.Drawing.Graphics.FromImage(sample))
                {
                    using var gradient = new LinearGradientBrush(new System.Drawing.Rectangle(0, 0, sample.Width, sample.Height), System.Drawing.Color.FromArgb(20, 150, 220), System.Drawing.Color.FromArgb(220, 40, 90), 20);
                    g.FillRectangle(gradient, 0, 0, sample.Width, sample.Height);
                    using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(150, 255, 255, 255), 3);
                    g.DrawRectangle(pen, 20, 20, sample.Width - 40, sample.Height - 40);
                    g.DrawLine(pen, 0, sample.Height / 2, sample.Width, sample.Height / 2);
                }
                using var processor = new CpuFrameProcessor();
                var preview = settings.Snapshot(); preview.Blur *= 320 / (double)Math.Max(1, sourceWidth);
                _frame = processor.Process(settings.Mode == OverlayMode.Ambient ? sample : null, preview, _width, _height, 0);
            }
        }
        catch (Exception ex) { _image = _frame = null; Status = $"Preview unavailable: {ex.Message}"; }
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        double scale = Math.Min(Math.Max(1, ActualWidth - 8) / _width, Math.Max(1, ActualHeight - 8) / _height);
        _viewport = new Rect((ActualWidth - _width * scale) / 2, (ActualHeight - _height * scale) / 2, _width * scale, _height * scale);
        dc.PushClip(new RectangleGeometry(_viewport));
        for (int y = 0; y < _viewport.Height; y += 16) for (int x = 0; x < _viewport.Width; x += 16)
            dc.DrawRectangle((x / 16 + y / 16) % 2 == 0 ? Brushes.DimGray : Brushes.DarkSlateGray, null, new Rect(_viewport.X + x, _viewport.Y + y, 16, 16));
        if (_settings.Mode == OverlayMode.ImageBorder && _image is not null)
        {
            dc.PushTransform(new TranslateTransform(_viewport.X, _viewport.Y)); dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawDrawing(ImageBorderDrawing.Create(_image, _settings, _width, _height)); dc.Pop(); dc.Pop();
        }
        else if (_frame is not null) dc.DrawImage(_frame, _viewport);
        if (Guides)
        {
            var pen = new System.Windows.Media.Pen(new SolidColorBrush(Color.FromArgb(170, 255, 220, 80)), 1) { DashStyle = DashStyles.Dash };
            foreach (double fraction in new[] { 1 / 3d, 0.5, 2 / 3d })
            {
                dc.DrawLine(pen, new Point(_viewport.Left + _viewport.Width * fraction, _viewport.Top), new Point(_viewport.Left + _viewport.Width * fraction, _viewport.Bottom));
                dc.DrawLine(pen, new Point(_viewport.Left, _viewport.Top + _viewport.Height * fraction), new Point(_viewport.Right, _viewport.Top + _viewport.Height * fraction));
            }
        }
        dc.Pop();
        dc.DrawRectangle(null, new System.Windows.Media.Pen(Brushes.Gray, 1), _viewport);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_settings.Mode != OverlayMode.ImageBorder || _image is null || !_viewport.Contains(e.GetPosition(this))) return;
        _dragStart = e.GetPosition(this); _dragOffset = new Point(_settings.ImageOffsetX, _settings.ImageOffsetY); CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStart is not { } start || _image is null || !IsMouseCaptured) return;
        var current = e.GetPosition(this); double scale = _viewport.Width / _width;
        double x = _dragOffset.X + (current.X - start.X) / scale, y = _dragOffset.Y + (current.Y - start.Y) / scale;
        if (Guides && Snap && _settings.RepeatMode == SourceRepeatMode.None)
        {
            var next = _settings.Snapshot(); next.ImageOffsetX = x; next.ImageOffsetY = y;
            var bounds = ImageBorderDrawing.Bounds(_image, next, _width, _height);
            x += SnapAxis(bounds.Left, bounds.Width, _width, 8 / scale);
            y += SnapAxis(bounds.Top, bounds.Height, _height, 8 / scale);
        }
        PositionChanged?.Invoke(Math.Clamp(x, -16384, 16384), Math.Clamp(y, -16384, 16384)); e.Handled = true;
    }
    internal static double SnapAxis(double origin, double size, double canvas, double threshold)
    {
        double best = threshold + 1, result = 0;
        foreach (double feature in new[] { origin, origin + size / 2, origin + size })
        foreach (double guide in new[] { 0d, canvas / 3, canvas / 2, canvas * 2 / 3, canvas })
        { double delta = guide - feature; if (Math.Abs(delta) <= threshold && Math.Abs(delta) < best) { best = Math.Abs(delta); result = delta; } }
        return result;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); _dragStart = null; ReleaseMouseCapture(); }
    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e) { base.OnLostMouseCapture(e); _dragStart = null; }
    public void Dispose() { _image = _frame = null; InvalidateVisual(); }
}
