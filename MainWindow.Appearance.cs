using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace AmbientMonitor;

public partial class MainWindow
{
    private (bool Dark, bool Acrylic, double Transparency)? _appliedAppearance;
    private Color? _appliedAccent;
    private bool _applyingAppearance;
    private bool _startupSurfacePending;
    internal bool StartupSurfaceReady { get; private set; }

    private void SettingsWindowFirstContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= SettingsWindowFirstContentRendered;
        // Fluent titlebar/WindowChrome initialization finishes after source
        // creation. Apply the surface after those first-render handlers, rather
        // than relying on a subsequent slider change to repair the caption.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closed || _workerCloseInProgress) return;
            _startupSurfacePending = true;
            if (SettingsWindowMoving) { QueueAppearanceRefresh(); return; }
            CompleteStartupSurface();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void CompleteStartupSurface()
    {
        if (!_startupSurfacePending || _closed || _workerCloseInProgress || SettingsWindowMoving) return;
        ApplyWindowSurface();
        WindowChromeHelper.RefreshFrame(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        _startupSurfacePending = false;
        StartupSurfaceReady = true;
    }
    private void RefreshSurfaceAfterRestore()
    {
        StartupSurfaceReady = false;
        _startupSurfacePending = true;
        // Show/restore can reset native chrome after its initial HWND setup.
        // Wait for the queued layout/render work, then repair just the surface.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closed || _workerCloseInProgress || !IsVisible) return;
            if (SettingsWindowMoving) { QueueAppearanceRefresh(); return; }
            CompleteStartupSurface();
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }
    private void ApplyWindowSurface()
    {
        bool dark = IsDarkTheme;
        var theme = dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light;
        WindowBackdropType = _applicationSettings.Acrylic && NativeAcrylic.IsSupported ? Wpf.Ui.Controls.WindowBackdropType.Acrylic : Wpf.Ui.Controls.WindowBackdropType.None;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        bool native = NativeAcrylic.Apply(handle, _applicationSettings.Acrylic);
        byte alpha = _applicationSettings.Acrylic && (native || handle == 0 && NativeAcrylic.IsSupported) ? NativeAcrylic.TintAlpha(_applicationSettings.AcrylicTransparency) : (byte)255;
        Background = Brushes.Transparent;
        ((Grid)Content).Background = new SolidColorBrush(dark ? Color.FromArgb(alpha, 48, 48, 52) : Color.FromArgb(alpha, 242, 242, 246));
        WindowTitleBar.Background = Brushes.Transparent;
        WindowTitleBar.ApplicationTheme = theme;
        if (handle != 0) WindowChromeHelper.MatchCaptionSurface(handle, dark, _applicationSettings.Acrylic);
    }
    internal bool IsDarkTheme
    {
        get
        {
            if (_applicationSettings.Theme != InterfaceTheme.System) return _applicationSettings.Theme == InterfaceTheme.Dark;
            try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
            catch { return true; }
        }
    }
    private void ApplyInterfaceAppearance(bool force = false)
    {
        if (_closed || _applyingAppearance) return;
        if (SettingsWindowMoving) { QueueAppearanceRefresh(); return; }
        bool dark = IsDarkTheme;
        var accent = Wpf.Ui.Appearance.ApplicationAccentColorManager.GetColorizationColor();
        var appearance = (dark, _applicationSettings.Acrylic, _applicationSettings.AcrylicTransparency);
        if (!force && _appliedAppearance == appearance)
        {
            if (_appliedAccent != accent) { ApplyWindowsAccent(); _appliedAccent = accent; }
            CompleteStartupSurface();
            return;
        }
        _applyingAppearance = true;
        try
        {
            var theme = dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light;
            bool themeChanged = force || _appliedAppearance?.Dark != dark;
            if (themeChanged)
            {
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme);
                Resources.MergedDictionaries[0] = new Wpf.Ui.Markup.ThemesDictionary { Theme = theme };
            }
            Resources["MainText"] = new SolidColorBrush(dark ? Color.FromRgb(244, 244, 247) : Color.FromRgb(26, 26, 32));
            Resources["MutedColor"] = dark ? Color.FromRgb(163, 165, 179) : Color.FromRgb(91, 93, 106);
            byte cardAlpha = _applicationSettings.Acrylic ? (byte)Math.Round(40 + (1 - _applicationSettings.AcrylicTransparency) * 150) : (byte)210;
            Resources["CardSurface"] = new SolidColorBrush(dark ? Color.FromArgb(cardAlpha, 60, 60, 66) : Color.FromArgb(cardAlpha, 255, 255, 255));
            Resources["SurfaceBorder"] = new SolidColorBrush(dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0));
            Resources["FlyoutSurface"] = new SolidColorBrush(dark ? Color.FromRgb(48, 48, 52) : Color.FromRgb(250, 250, 252));
            ApplyWindowSurface();
            Foreground = (Brush)Resources["MainText"];
            if (themeChanged || _appliedAccent != accent) ApplyWindowsAccent();
            _appliedAppearance = appearance;
            _appliedAccent = accent;
            AppearanceRevision++;
            CompleteStartupSurface();
        }
        finally { _applyingAppearance = false; }
    }
    private void InterfacePreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingPreferences || AcrylicBox is null || ThemeBox is null) return;
        _applicationSettings.Acrylic = AcrylicBox.IsChecked == true;
        _applicationSettings.Theme = (InterfaceTheme)Math.Max(0, ThemeBox.SelectedIndex);
        if (AcrylicTransparencySlider is not null) _applicationSettings.AcrylicTransparency = AcrylicTransparencySlider.Value / 100;
        if (AcrylicTransparencyValue is not null) AcrylicTransparencyValue.Text = $"{_applicationSettings.AcrylicTransparency:P0}";
        SaveApplicationSettings(); ApplyInterfaceAppearance();
    }
    private void ThemePreferenceChanged(object sender, SelectionChangedEventArgs e) => InterfacePreferenceChanged(sender, e);
    private void AcrylicTransparencyChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => InterfacePreferenceChanged(sender, e);

    private void ApplyVeilColor_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile) return;
        if (!VeilPalette.TryParse(VeilColorBox.Text.Trim(), out _)) { VeilColorInfoText.Text = "Enter a color as #RRGGBB."; return; }
        profile.VeilColor = VeilColorBox.Text.Trim().ToUpperInvariant();
        VeilColorPreview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(profile.VeilColor));
        VeilColorInfoText.Text = "Color updated · Save profiles to keep it";
        ProfileSaveStateText.Text = "Unsaved changes"; ApplySettings();
    }
    private void PickVeilColor_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_desktopIntegrationEnabled || SelectedProfile is not { } profile) return;
        var vector = VeilPalette.Color(profile.VeilColor);
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb((int)Math.Round(vector.X * 255), (int)Math.Round(vector.Y * 255), (int)Math.Round(vector.Z * 255)) };
        if (RunSettingsDialog(() => dialog.ShowDialog()) == System.Windows.Forms.DialogResult.OK)
        {
            VeilColorBox.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
            ApplyVeilColor_OnClick(sender, e);
        }
    }
    private void BrowseImage_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_desktopIntegrationEnabled) return;
        var dialog = new OpenFileDialog { Title = "Choose a border image", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*" };
        if (RunSettingsDialog(() => dialog.ShowDialog(this)) == true) SetBorderImage(dialog.FileName);
    }
    internal bool SetBorderImage(string path)
    {
        if (SelectedProfile is not { } profile) return false;
        try
        {
            var image = ImageBorderDrawing.Load(path);
            profile.ImagePath = Path.GetFullPath(path);
            BorderImagePreview.Source = image;
            BorderImagePathText.Text = profile.ImagePath;
            ImageInfoText.Text = $"{image.PixelWidth} × {image.PixelHeight} · transparency preserved";
            ProfileSaveStateText.Text = "Unsaved changes"; ApplySettings();
            return true;
        }
        catch (Exception ex) { ImageInfoText.Text = $"Could not load image: {ex.Message}"; return false; }
    }
    private void ClearImage_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile) return;
        profile.ImagePath = ""; BorderImagePreview.Source = null; BorderImagePathText.Text = "No image selected";
        ProfileSaveStateText.Text = "Unsaved changes"; ApplySettings();
    }
    private void RefreshImageControls(DisplayProfile profile)
    {
        VeilColorBox.Text = profile.VeilColor;
        var color = VeilPalette.Color(profile.VeilColor);
        VeilColorPreview.Background = new SolidColorBrush(Color.FromRgb((byte)Math.Round(color.X * 255), (byte)Math.Round(color.Y * 255), (byte)Math.Round(color.Z * 255)));
        BorderScaleBox.SelectedIndex = (int)profile.BorderScale;
        ImageZoomSlider.Value = profile.ImageZoom;
        ImageOffsetXSlider.Value = profile.ImageOffsetX; ImageOffsetYSlider.Value = profile.ImageOffsetY;
        BorderImagePathText.Text = string.IsNullOrWhiteSpace(profile.ImagePath) ? "No image selected" : profile.ImagePath;
        BorderImagePreview.Source = null;
        try { if (!string.IsNullOrWhiteSpace(profile.ImagePath)) BorderImagePreview.Source = ImageBorderDrawing.Load(profile.ImagePath); ImageInfoText.Text = "Transparent PNGs keep the center clear for your game or emulator."; }
        catch (Exception ex) { ImageInfoText.Text = $"Image unavailable: {ex.Message}"; }
    }
    private void ApplyImageTransform_OnClick(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(ImageZoomBox.Text, out double zoom) || !double.IsFinite(zoom) || zoom is < 0.05 or > 20 ||
            !double.TryParse(ImageOffsetXBox.Text, out double x) || !double.IsFinite(x) || Math.Abs(x) > 16384 ||
            !double.TryParse(ImageOffsetYBox.Text, out double y) || !double.IsFinite(y) || Math.Abs(y) > 16384)
        {
            ImageTransformInfoText.Text = "Use scale 0.05–20 and offsets −16384–16384 pixels.";
            return;
        }
        _loading = true;
        ImageZoomSlider.Value = zoom; ImageOffsetXSlider.Value = x; ImageOffsetYSlider.Value = y;
        _loading = false;
        RefreshValueLabels(); UpdateSelectedProfile();
        ImageTransformInfoText.Text = "Image placement updated";
    }
}
