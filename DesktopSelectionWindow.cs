using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace AmbientMonitor;

// Shown only by an explicit user click, never by regression checks/startup.
internal sealed class DesktopSelectionWindow : Window
{
    private readonly System.Windows.Controls.Canvas _canvas = new();
    private readonly Rectangle _selection = new() { StrokeThickness = 2, Stroke = System.Windows.Media.Brushes.White, Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(45, 255, 255, 255)), Visibility = Visibility.Collapsed };
    private readonly TextBlock _caption = new() { Foreground = System.Windows.Media.Brushes.White, FontSize = 16, Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(210, 30, 30, 34)), Padding = new Thickness(14), Text = "Drag a desktop canvas · Escape cancels" };
    private readonly System.Drawing.Rectangle _desktop;
    private PhysicalPoint? _start;
    public CanvasBounds? Selection { get; private set; }

    public DesktopSelectionWindow()
    {
        _desktop = new System.Drawing.Rectangle(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(35, 0, 0, 0));
        ShowInTaskbar = false; Topmost = true; Cursor = System.Windows.Input.Cursors.Cross;
        Left = _desktop.Left; Top = _desktop.Top; Width = _desktop.Width; Height = _desktop.Height;
        Content = _canvas;
        _canvas.Children.Add(_selection); _canvas.Children.Add(_caption);
        System.Windows.Controls.Canvas.SetLeft(_caption, 24); System.Windows.Controls.Canvas.SetTop(_caption, 24);
        Loaded += (_, _) =>
        {
            SetWindowPos(new WindowInteropHelper(this).Handle, -1, _desktop.Left, _desktop.Top, _desktop.Width, _desktop.Height, 0);
            Activate();
        };
        MouseLeftButtonDown += (_, e) =>
        {
            GetPhysicalCursorPos(out var point); _start = point;
            _selection.Visibility = Visibility.Visible; CaptureMouse(); UpdateSelection(point); e.Handled = true;
        };
        MouseMove += (_, _) => { if (_start is not null) { GetPhysicalCursorPos(out var point); UpdateSelection(point); } };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_start is not { } start) return;
            GetPhysicalCursorPos(out var point); ReleaseMouseCapture();
            var bounds = CanvasBounds.FromPoints(start.X, start.Y, point.X, point.Y);
            if (!bounds.IsValid) { _start = null; _selection.Visibility = Visibility.Collapsed; _caption.Text = "Choose a box at least 16 × 16 pixels · Escape cancels"; return; }
            Selection = bounds; DialogResult = true; e.Handled = true;
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
    }
    private void UpdateSelection(PhysicalPoint point)
    {
        if (_start is not { } start) return;
        var bounds = CanvasBounds.FromPoints(start.X, start.Y, point.X, point.Y);
        double sx = _canvas.ActualWidth / _desktop.Width, sy = _canvas.ActualHeight / _desktop.Height;
        System.Windows.Controls.Canvas.SetLeft(_selection, (bounds.Left - _desktop.Left) * sx);
        System.Windows.Controls.Canvas.SetTop(_selection, (bounds.Top - _desktop.Top) * sy);
        _selection.Width = bounds.Width * sx; _selection.Height = bounds.Height * sy;
        _caption.Text = $"{bounds.Width} × {bounds.Height} px at {bounds.Left}, {bounds.Top} · release to add · Escape cancels";
    }
    [StructLayout(LayoutKind.Sequential)] private struct PhysicalPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out PhysicalPoint point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
