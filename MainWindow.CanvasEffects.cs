using System.Windows;

namespace AmbientMonitor;

public partial class MainWindow
{
    private void RefreshCanvasEffects(DisplayProfile profile)
    {
        RepeatModeBox.SelectedIndex = (int)profile.RepeatMode;
        RepeatXSlider.Value = profile.RepeatX; RepeatYSlider.Value = profile.RepeatY;
        GradientModeBox.SelectedIndex = (int)profile.GradientMode; GradientColorBox.Text = profile.GradientColor;
        GradientOpacitySlider.Value = profile.GradientOpacity; GradientStartSlider.Value = profile.GradientStart;
        GradientAngleSlider.Value = profile.GradientAngle;
        GradientEnabledBox.IsChecked = profile.GradientEnabled && profile.GradientMode != CanvasGradientMode.None;
        GradientIntensitySlider.Value = profile.GradientIntensity;
        GradientStartColorBox.Text = profile.GradientStartColor; GradientStartOpacitySlider.Value = profile.GradientStartOpacity;
        GradientEndSlider.Value = profile.GradientEnd; GradientFalloffSlider.Value = profile.GradientFalloff;
        GradientIndependentEdgesBox.IsChecked = profile.GradientIndependentEdges;
        GradientLeftWidthSlider.Value = profile.GradientLeftWidth; GradientRightWidthSlider.Value = profile.GradientRightWidth;
        GradientTopWidthSlider.Value = profile.GradientTopWidth; GradientBottomWidthSlider.Value = profile.GradientBottomWidth;
        GradientLeftOpacitySlider.Value = profile.GradientLeftOpacity; GradientRightOpacitySlider.Value = profile.GradientRightOpacity;
        GradientTopOpacitySlider.Value = profile.GradientTopOpacity; GradientBottomOpacitySlider.Value = profile.GradientBottomOpacity;
        GradientLeftBox.IsChecked = (profile.GradientEdges & 1) != 0; GradientRightBox.IsChecked = (profile.GradientEdges & 2) != 0;
        GradientTopBox.IsChecked = (profile.GradientEdges & 4) != 0; GradientBottomBox.IsChecked = (profile.GradientEdges & 8) != 0;
        RefreshGradientControls();
    }
    private void ReadCanvasEffects(DisplayProfile profile)
    {
        profile.RepeatMode = (SourceRepeatMode)Math.Max(0, RepeatModeBox.SelectedIndex);
        profile.RepeatX = (int)RepeatXSlider.Value; profile.RepeatY = (int)RepeatYSlider.Value;
        profile.GradientMode = (CanvasGradientMode)Math.Max(0, GradientModeBox.SelectedIndex);
        profile.GradientOpacity = GradientOpacitySlider.Value; profile.GradientStart = GradientStartSlider.Value;
        profile.GradientAngle = GradientAngleSlider.Value;
        profile.GradientEdges = (GradientLeftBox.IsChecked == true ? 1 : 0) | (GradientRightBox.IsChecked == true ? 2 : 0) | (GradientTopBox.IsChecked == true ? 4 : 0) | (GradientBottomBox.IsChecked == true ? 8 : 0);
        profile.GradientEnabled = GradientEnabledBox.IsChecked == true;
        profile.GradientIntensity = GradientIntensitySlider.Value; profile.GradientStartOpacity = GradientStartOpacitySlider.Value;
        profile.GradientEnd = GradientEndSlider.Value; profile.GradientFalloff = GradientFalloffSlider.Value;
        profile.GradientIndependentEdges = GradientIndependentEdgesBox.IsChecked == true;
        profile.GradientLeftWidth = GradientLeftWidthSlider.Value; profile.GradientRightWidth = GradientRightWidthSlider.Value;
        profile.GradientTopWidth = GradientTopWidthSlider.Value; profile.GradientBottomWidth = GradientBottomWidthSlider.Value;
        profile.GradientLeftOpacity = GradientLeftOpacitySlider.Value; profile.GradientRightOpacity = GradientRightOpacitySlider.Value;
        profile.GradientTopOpacity = GradientTopOpacitySlider.Value; profile.GradientBottomOpacity = GradientBottomOpacitySlider.Value;
        RefreshGradientControls();
    }
    private void RefreshGradientControls()
    {
        if (GradientLinearControls is null) return;
        GradientLinearControls.Visibility = GradientModeBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        GradientEdgeControls.Visibility = GradientModeBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        GradientCommonStops.Visibility = GradientModeBox.SelectedIndex == 2 && GradientIndependentEdgesBox.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
    }
    private void GradientModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || GradientEnabledBox is null) return;
        _loading = true; GradientEnabledBox.IsChecked = GradientModeBox.SelectedIndex > 0; _loading = false;
        UpdateSelectedProfile();
    }
    private void GradientEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || GradientModeBox is null) return;
        if (GradientEnabledBox.IsChecked == true && GradientModeBox.SelectedIndex == 0)
        { _loading = true; GradientModeBox.SelectedIndex = 2; _loading = false; }
        UpdateSelectedProfile();
    }
    private void GradientStopsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || GradientEndSlider is null) return;
        _loading = true;
        if (ReferenceEquals(sender, GradientStartSlider)) GradientEndSlider.Value = Math.Max(GradientEndSlider.Value, GradientStartSlider.Value + 0.01);
        else GradientStartSlider.Value = Math.Min(GradientStartSlider.Value, GradientEndSlider.Value - 0.01);
        _loading = false; UpdateSelectedProfile();
    }
    private void ApplyGradientColor_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile) return;
        if (!VeilPalette.TryParse(GradientColorBox.Text.Trim(), out _) || !VeilPalette.TryParse(GradientStartColorBox.Text.Trim(), out _)) { GradientInfoText.Text = "Enter gradient colors as #RRGGBB."; return; }
        profile.GradientColor = GradientColorBox.Text.Trim().ToUpperInvariant();
        profile.GradientStartColor = GradientStartColorBox.Text.Trim().ToUpperInvariant();
        GradientInfoText.Text = "Gradient color updated"; ProfileSaveStateText.Text = "Unsaved changes"; ApplySettings(); QueuePreviewRefresh();
    }
    private void PickGradientColor_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_desktopIntegrationEnabled) return;
        var box = ((FrameworkElement)sender).Tag as string == "Start" ? GradientStartColorBox : GradientColorBox;
        var color = VeilPalette.Color(box.Text);
        using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb((int)Math.Round(color.X * 255), (int)Math.Round(color.Y * 255), (int)Math.Round(color.Z * 255)) };
        if (RunSettingsDialog(() => picker.ShowDialog()) != System.Windows.Forms.DialogResult.OK) return;
        box.Text = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}"; ApplyGradientColor_OnClick(sender, e);
    }
    private async void HdrPreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingPreferences) return;
        _applicationSettings.GpuHdrAutomatic = GpuHdrBox.IsChecked == true;
        SaveApplicationSettings();
        if (_desktopIntegrationEnabled && _applicationSettings.Backend == RenderingBackend.Gpu) await ReplaceRendererAsync(_overlayRunning);
    }
}
