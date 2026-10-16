using System.Windows;
using System.Windows.Threading;

namespace AmbientMonitor;

public partial class MainWindow
{
    private DispatcherTimer? _previewTimer;
    private void QueuePreviewRefresh()
    {
        if (PreviewExpander is null || !PreviewExpander.IsExpanded || _closed) return;
        if (_previewTimer is null)
        {
            _previewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
            _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RefreshPreview(); };
        }
        _previewTimer.Stop(); _previewTimer.Start();
    }
    internal void RefreshPreview()
    {
        if (SelectedProfile is not { } profile || !PreviewExpander.IsExpanded || _closed) return;
        var screen = CaptureService.GetScreens().FirstOrDefault(s => s.DeviceName == profile.DeviceName);
        var source = CaptureService.GetScreens().FirstOrDefault(s => s.Primary);
        int width = profile.Canvas?.Width ?? screen?.Bounds.Width ?? 1920, height = profile.Canvas?.Height ?? screen?.Bounds.Height ?? 1080;
        CanvasPreviewSurface.Guides = PreviewGuidesBox.IsChecked == true; CanvasPreviewSurface.Snap = PreviewSnapBox.IsChecked == true;
        CanvasPreviewSurface.Update(profile, width, height, source is null ? 16 / 9d : source.Bounds.Width / (double)source.Bounds.Height, source?.Bounds.Width ?? 1920);
        PreviewStatusText.Text = $"{width} × {height} · {CanvasPreviewSurface.Status}";
    }
    private void PreviewExpanded(object sender, RoutedEventArgs e) => RefreshPreview();
    private void PreviewPreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingPreferences || PreviewSnapBox is null) return;
        _applicationSettings.PreviewGuides = PreviewGuidesBox.IsChecked == true; _applicationSettings.PreviewSnap = PreviewSnapBox.IsChecked == true;
        SaveApplicationSettings(); RefreshPreview();
    }
    private void PreviewPositionChanged(double x, double y)
    {
        _loading = true; ImageOffsetXSlider.Value = x; ImageOffsetYSlider.Value = y; _loading = false;
        RefreshValueLabels(); UpdateSelectedProfile(); RefreshPreview();
    }
    private void AlignPreview_OnClick(object sender, RoutedEventArgs e)
    {
        _loading = true;
        switch (((FrameworkElement)sender).Tag as string)
        {
            case "Left": FocusXSlider.Value = 0; ImageOffsetXSlider.Value = 0; break;
            case "Right": FocusXSlider.Value = 1; ImageOffsetXSlider.Value = 0; break;
            case "Top": FocusYSlider.Value = 0; ImageOffsetYSlider.Value = 0; break;
            case "Bottom": FocusYSlider.Value = 1; ImageOffsetYSlider.Value = 0; break;
            default: FocusXSlider.Value = FocusYSlider.Value = 0.5; ImageOffsetXSlider.Value = ImageOffsetYSlider.Value = 0; break;
        }
        _loading = false; RefreshValueLabels(); UpdateSelectedProfile(); RefreshPreview();
    }
}
