using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using MediaColor = System.Windows.Media.Color;

namespace AmbientMonitor;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly List<DisplayProfile> _profiles = new();
    private readonly List<DisplayProfile> _canvases = new();
    private readonly string _settingsPath;
    private readonly string _applicationSettingsPath;
    private readonly string _canvasSettingsPath;
    private readonly bool _desktopIntegrationEnabled;
    private bool _loading;
    private readonly RendererProcessHost _rendererHost;
    private readonly Dictionary<string, string> _layerNotices = new();
    private bool _workerCloseInProgress, _workerClosed;
    private ApplicationSettings _applicationSettings = new();
    private bool _loadingPreferences = true;
    private bool _overlayRunning;
    private long _rendererGeneration;
    private GlobalHotkey? _hotkey;
    private TrayIcon? _tray;
    private bool _recordingShortcut;
    private bool _exitRequested;
    private bool _closed;
    private bool _selectingCanvas;

    public MainWindow() : this(null, true) { }

    // Desktop integration can be disabled for non-interactive, isolated UI checks.
    internal MainWindow(string? settingsDirectory, bool desktopIntegrationEnabled)
    {
        SettingsWindowRendering.Configure();
        var directory = settingsDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmbientMonitor");
        _settingsPath = Path.Combine(directory, "settings.json");
        _applicationSettingsPath = Path.Combine(directory, "app-settings.json");
        _canvasSettingsPath = Path.Combine(directory, "canvases.json");
        _desktopIntegrationEnabled = desktopIntegrationEnabled;
        _rendererHost = new RendererProcessHost(offscreen: !desktopIntegrationEnabled);
        _rendererHost.Message += reply =>
        {
            if (!_closed && !Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(() => HandleWorkerReply(reply)));
        };
        InitializeComponent();
        CanvasPreviewSurface.PositionChanged += PreviewPositionChanged;
        ContentRendered += SettingsWindowFirstContentRendered;
        BuildVersionText.Text = BuildInfo.DisplayLabel;
        BuildVersionText.ToolTip = BuildInfo.Description;
        ApplyWindowsAccent();
        SystemEvents.UserPreferenceChanged += WindowsPreferenceChanged;
        LoadProfiles();
        LoadApplicationSettings();
        LoadCanvases();
        RefreshTargetControls();
        AppearanceNav.IsChecked = true;
        RefreshValueLabels();
        RefreshApplicationControls();
        _loadingPreferences = false;
        LoadPresets();
        ApplyInterfaceAppearance();
        Deactivated += (_, _) => CancelShortcutRecording();
    }

    private DisplayProfile? SelectedProfile => DisplayTabs.SelectedItem as DisplayProfile;
    private List<DisplayProfile> ActiveProfiles => _applicationSettings.TargetMode == CanvasTargetMode.CustomBoxes ? _canvases : _profiles;

    private void RefreshEditor()
    {
        if (SelectedProfile is not { } profile) return;
        _loading = true;
        DisplayEnabledBox.IsChecked = profile.Enabled;
        ModeBox.SelectedIndex = (int)profile.Mode;
        StrengthSlider.Value = profile.Strength;
        BlurSlider.Value = profile.Blur;
        BlurValueBox.Text = profile.Blur.ToString("0.##");
        BlurTypeBox.SelectedIndex = profile.BlurType == BlurType.Gaussian ? 1 : 0;
        BrightnessSlider.Value = profile.Brightness;
        NoiseSlider.Value = profile.Noise;
        NoiseGrainSlider.Value = DitherGrain.Size(profile.NoiseGrainPixels);
        NoiseBox.IsChecked = profile.NoiseEnabled;
        ScaleModeBox.SelectedIndex = profile.ScaleMode == ImageScaleMode.Fit ? 1 : 0;
        CropZoomSlider.Value = profile.CropZoom;
        FocusXSlider.Value = profile.FocusX;
        FocusYSlider.Value = profile.FocusY;
        CaptureScaleSlider.Value = profile.CaptureScalePercent;
        FlipHorizontalBox.IsChecked = profile.FlipHorizontal;
        FlipVerticalBox.IsChecked = profile.FlipVertical;
        ClickThroughBox.IsChecked = profile.ClickThrough;
        LayerBox.SelectedIndex = (int)profile.Layer;
        CropLeftSlider.Value = profile.CropLeft * 100;
        CropRightSlider.Value = profile.CropRight * 100;
        CropTopSlider.Value = profile.CropTop * 100;
        CropBottomSlider.Value = profile.CropBottom * 100;
        RefreshImageControls(profile);
        RefreshCanvasEffects(profile);
        _loading = false;
        var screen = CaptureService.GetScreens().FirstOrDefault(s => s.DeviceName == profile.DeviceName);
        DisplayProfileNameText.Text = profile.TabTitle;
        NicknameEditor.Visibility = Visibility.Collapsed;
        NicknameBox.Text = profile.Nickname;
        DisplayDetailsText.Text = profile.Canvas is { } bounds ? $"{bounds.Width} × {bounds.Height} · desktop position {bounds.Left}, {bounds.Top}" : screen is null ? profile.DeviceName :
            $"{screen.Bounds.Width} × {screen.Bounds.Height} · {(screen.Primary ? "Capture source" : "Secondary display")}";
        RefreshValueLabels();
        RefreshModeControls();
        RefreshLayerDescription();
        CanvasBoundsEditor.Visibility = profile.Canvas is null ? Visibility.Collapsed : Visibility.Visible;
        if (profile.Canvas is { } box)
        {
            CanvasLeftBox.Text = box.Left.ToString(); CanvasTopBox.Text = box.Top.ToString();
            CanvasWidthBox.Text = box.Width.ToString(); CanvasHeightBox.Text = box.Height.ToString();
        }
        QueuePreviewRefresh();
    }

    private void UpdateSelectedProfile()
    {
        if (_loading || SelectedProfile is not { } profile) return;
        profile.Enabled = DisplayEnabledBox.IsChecked == true;
        profile.Mode = (OverlayMode)Math.Max(0, ModeBox.SelectedIndex);
        profile.Strength = StrengthSlider.Value;
        profile.Blur = double.TryParse(BlurValueBox.Text, out var blur) ? Math.Max(0, blur) : BlurSlider.Value;
        profile.Blur = Math.Min(profile.Blur, 1000);
        profile.BlurType = BlurTypeBox.SelectedIndex == 1 ? BlurType.Gaussian : BlurType.Box;
        profile.Brightness = BrightnessSlider.Value;
        profile.Noise = NoiseSlider.Value;
        profile.NoiseGrainPixels = NoiseGrainSlider.Value;
        profile.NoiseEnabled = NoiseBox.IsChecked == true;
        profile.ScaleMode = ScaleModeBox.SelectedIndex == 1 ? ImageScaleMode.Fit : ImageScaleMode.Fill;
        profile.CropZoom = CropZoomSlider.Value;
        profile.FocusX = FocusXSlider.Value;
        profile.FocusY = FocusYSlider.Value;
        profile.CaptureScalePercent = (int)Math.Round(CaptureScaleSlider.Value);
        profile.FlipHorizontal = FlipHorizontalBox.IsChecked == true;
        profile.FlipVertical = FlipVerticalBox.IsChecked == true;
        profile.ClickThrough = ClickThroughBox.IsChecked == true;
        profile.Layer = LayerBox.SelectedIndex >= 0 ? (OverlayLayer)LayerBox.SelectedIndex : OverlayLayer.Topmost;
        (profile.CropLeft, profile.CropRight) = SourceFraming.Normalize(CropLeftSlider.Value / 100, CropRightSlider.Value / 100);
        (profile.CropTop, profile.CropBottom) = SourceFraming.Normalize(CropTopSlider.Value / 100, CropBottomSlider.Value / 100);
        profile.BorderScale = (BorderScaleMode)Math.Max(0, BorderScaleBox.SelectedIndex);
        profile.ImageZoom = ImageZoomSlider.Value;
        profile.ImageOffsetX = ImageOffsetXSlider.Value; profile.ImageOffsetY = ImageOffsetYSlider.Value;
        ReadCanvasEffects(profile);
        ProfileSaveStateText.Text = "Unsaved changes";
        RefreshModeControls();
        ApplySettings();
        RefreshLayerDescription();
        QueuePreviewRefresh();
    }

    private void StartButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleOverlays();
    }

    private async void StartOverlays()
    {
        CancelShortcutRecording();
        if (!ActiveProfiles.Any(p => p.Enabled))
        {
            _overlayRunning = false;
            try { await _rendererHost.StopAsync(); }
            catch (Exception ex) { ApplicationSettingsStatusText.Text = $"Renderer shutdown: {ex.Message}"; }
            StatusText.Text = "No displays enabled · enable a display above";
            StartButton.Content = "Start overlay";
            SetStatusColor("#8E91A0");
            return;
        }
        _overlayRunning = true;
        StartButton.Content = "Stop overlay";
        await ReplaceRendererAsync(true);
    }

    private async void StopOverlays()
    {
        long generation = ++_rendererGeneration;
        _overlayRunning = false;
        StartButton.Content = "Start overlay";
        _layerNotices.Clear();
        try { await _rendererHost.StopAsync(); }
        catch (Exception ex) { ApplicationSettingsStatusText.Text = $"Renderer shutdown: {ex.Message}"; }
        if (generation != _rendererGeneration || _closed) return;
        StatusText.Text = "Stopped · ready when you are";
        StartButton.Content = "Start overlay";
        SetStatusColor("#8E91A0");
        _tray?.Update(false);
    }
    private async Task ReplaceRendererAsync(bool running)
    {
        long generation = ++_rendererGeneration;
        _layerNotices.Clear();
        StatusText.Text = $"Resetting {_applicationSettings.Backend.ToString().ToUpperInvariant()} backend · keeping UI open";
        SetStatusColor("#F0C67B");
        try
        {
            await _rendererHost.ReplaceAsync(_applicationSettings.Backend, SnapshotProfiles(), running, _applicationSettings.GpuHdrAutomatic);
            if (generation == _rendererGeneration) _tray?.Update(_overlayRunning);
        }
        catch (Exception ex)
        {
            if (generation != _rendererGeneration || _closed) return;
            _overlayRunning = false;
            StartButton.Content = "Start overlay";
            StatusText.Text = $"Could not restart renderer: {ex.Message}";
        }
    }
    private List<DisplayProfile> SnapshotProfiles() => ActiveProfiles.Select(profile =>
    {
        var copy = new DisplayProfile { DeviceName = profile.DeviceName, DisplayName = profile.DisplayName, Nickname = profile.Nickname, Enabled = profile.Enabled, Canvas = profile.Canvas };
        copy.CopyDisplaySettingsFrom(profile);
        return copy;
    }).ToList();
    private void HandleWorkerReply(RendererReply reply)
    {
        if (_closed || reply.ProcessId != _rendererHost.ProcessId) return;
        if (reply.Kind == "layer" && reply.DeviceName is not null)
        {
            _layerNotices[reply.DeviceName] = reply.Text;
            RefreshLayerDescription();
        }
        else if (reply.Kind == "status" || reply.Kind == "error")
        {
            StatusText.Text = reply.Text;
            SetStatusColor(reply.Text.Contains(" running ·", StringComparison.Ordinal) ? "#64DEAE" : "#F0C67B");
            if (reply.Kind == "error")
            {
                _overlayRunning = false;
                StartButton.Content = "Start overlay";
                _tray?.Update(false);
            }
        }
        else if (reply.Kind == "configured" && !_overlayRunning)
        {
            StatusText.Text = $"{reply.Backend} backend ready · overlays stopped";
            SetStatusColor("#8E91A0");
        }
    }
    private void SettingChanged(object sender, RoutedEventArgs e) => UpdateSelectedProfile();
    private void SettingChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ReferenceEquals(sender, BlurSlider) && BlurValueBox is not null) BlurValueBox.Text = BlurSlider.Value.ToString("0.##");
        RefreshValueLabels();
        UpdateSelectedProfile();
    }
    private void ProfileSettingChanged(object sender, RoutedEventArgs e) => UpdateSelectedProfile();
    private void ModeBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectedProfile();
    private void ProfileSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectedProfile();
    private void LayerBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedProfile();
        if (LayerDescriptionText is not null) RefreshLayerDescription();
    }
    private void DisplayTabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, DisplayTabs)) return;
        CancelShortcutRecording();
        RefreshEditor();
        if (DisplayTabs.SelectedItem is { } selected) DisplayTabs.ScrollIntoView(selected);
    }
    private void BlurValueBox_OnLostFocus(object sender, RoutedEventArgs e) { UpdateSelectedProfile(); RefreshEditor(); }
    private void BlurValueBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Enter) { UpdateSelectedProfile(); RefreshEditor(); e.Handled = true; } }

    private async void ApplySettings()
    {
        if (!_overlayRunning || !_desktopIntegrationEnabled) return;
        try { await _rendererHost.UpdateAsync(_applicationSettings.Backend, SnapshotProfiles(), true, _applicationSettings.GpuHdrAutomatic); }
        catch (Exception ex) { if (!_closed) StatusText.Text = $"Could not update renderer: {ex.Message}"; }
    }

    private void ApplyAll_OnClick(object sender, RoutedEventArgs e)
    {
        UpdateSelectedProfile();
        if (SelectedProfile is not { } source) return;
        var snapshot = source.Snapshot();
        foreach (var profile in ActiveProfiles)
            if (!ReferenceEquals(profile, source))
            {
                string image = profile.ImagePath;
                profile.CopyDisplaySettingsFrom(snapshot);
                profile.ImagePath = image;
            }
        ApplySettings();
        ProfileSaveStateText.Text = "Applied to all · unsaved";
    }

    private void RefreshLayerDescription()
    {
        var profile = SelectedProfile;
        var layer = profile?.Layer ?? OverlayLayer.Topmost;
        string? notice = profile is null ? null : _layerNotices.GetValueOrDefault(profile.DeviceName);
        LayerDescriptionText.Text = LayerPlacementPolicy.Describe(layer) + (notice is null ? "" : $"\n{notice}");
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        SaveWorkspace();
    }

    private void LoadProfiles()
    {
        List<DisplayProfile>? saved = null;
        try { if (File.Exists(_settingsPath)) saved = JsonSerializer.Deserialize<List<DisplayProfile>>(File.ReadAllText(_settingsPath)); } catch { StatusText.Text = "Could not load settings"; }
        foreach (var screen in CaptureService.GetScreens())
        {
            var profile = saved?.FirstOrDefault(p => p.DeviceName == screen.DeviceName) ?? new DisplayProfile { DeviceName = screen.DeviceName, Enabled = !screen.Primary, DisplayName = screen.Primary ? "Primary display" : "Secondary display" };
            profile.DisplayName = screen.Primary ? "Primary display" : screen.DeviceName.Replace(@"\\.\DISPLAY", "Display ", StringComparison.OrdinalIgnoreCase);
            profile.IsPrimary = screen.Primary;
            if (!Enum.IsDefined(profile.Layer)) profile.Layer = OverlayLayer.Topmost;
            _profiles.Add(profile);
        }
    }

    private void ResetButton_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var profile in ActiveProfiles) { profile.Enabled = !profile.IsPrimary; profile.CopyDisplaySettingsFrom(new OverlaySettings { Blur = 36, Noise = 0.08 }); }
        RefreshEditor(); ApplySettings();
        ProfileSaveStateText.Text = "Unsaved changes";
    }

    private void NavigationChanged(object sender, RoutedEventArgs e)
    {
        if (AppearancePage is null || FramingPage is null || PerformancePage is null || ApplicationPage is null) return;
        var page = (sender as System.Windows.Controls.RadioButton)?.Tag as string;
        if (page != "Application") CancelShortcutRecording();
        AppearancePage.Visibility = page == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        FramingPage.Visibility = page == "Framing" ? Visibility.Visible : Visibility.Collapsed;
        PerformancePage.Visibility = page == "Performance" ? Visibility.Visible : Visibility.Collapsed;
        ApplicationPage.Visibility = page == "Application" ? Visibility.Visible : Visibility.Collapsed;
        TargetProfileCard.Visibility = page == "Application" ? Visibility.Collapsed : Visibility.Visible;
        PreviewCard.Visibility = page == "Application" ? Visibility.Collapsed : Visibility.Visible;
        NicknameEditor.Visibility = Visibility.Collapsed;
        ApplyAllButton.Visibility = page == "Application" ? Visibility.Collapsed : Visibility.Visible;
        PageTitle.Text = page switch { "Framing" => "Image framing", "Performance" => "Performance", "Application" => "App settings", _ => "Appearance" };
        PageDescription.Text = page switch
        {
            "Framing" => "Find the right angle for every screen.",
            "Performance" => "A lighter footprint. The same atmosphere.",
            "Application" => "Shortcuts and behavior for the whole app.",
            _ => "Give your other screens a little atmosphere."
        };
        SettingsScroll.ScrollToTop();
        QueuePreviewRefresh();
    }

    private void RefreshValueLabels()
    {
        if (CaptureScaleValue is null || CaptureScaleSlider is null) return;
        StrengthValue.Text = $"{StrengthSlider.Value:P0}";
        BrightnessValue.Text = $"{BrightnessSlider.Value:P0}";
        NoiseValue.Text = $"{NoiseSlider.Value:P0}";
        if (NoiseGrainValue is not null) NoiseGrainValue.Text = $"{NoiseGrainSlider.Value:0.#} px";
        ZoomValue.Text = $"{CropZoomSlider.Value:0.0}×";
        FocusXValue.Text = $"{FocusXSlider.Value:P0}";
        FocusYValue.Text = $"{FocusYSlider.Value:P0}";
        CaptureScaleValue.Text = $"{CaptureScaleSlider.Value:0}%";
        if (CropBottomSlider is null) return;
        CropLeftValue.Text = $"{CropLeftSlider.Value:0}%"; CropRightValue.Text = $"{CropRightSlider.Value:0}%";
        CropTopValue.Text = $"{CropTopSlider.Value:0}%"; CropBottomValue.Text = $"{CropBottomSlider.Value:0}%";
        if (ImageZoomBox is null) return;
        ImageZoomBox.Text = ImageZoomSlider.Value.ToString("0.###");
        ImageOffsetXBox.Text = ImageOffsetXSlider.Value.ToString("0.###");
        ImageOffsetYBox.Text = ImageOffsetYSlider.Value.ToString("0.###");
    }

    private void RefreshModeControls()
    {
        bool ambient = ModeBox.SelectedIndex == 0, image = ModeBox.SelectedIndex == 2;
        BrightnessControls.Visibility = ambient ? Visibility.Visible : Visibility.Collapsed;
        BlurCard.Visibility = ambient ? Visibility.Visible : Visibility.Collapsed;
        VeilColorCard.Visibility = ModeBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        BorderImageCard.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
        NoiseCard.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
        ProcessingQualityCard.Visibility = ambient ? Visibility.Visible : Visibility.Collapsed;
        CaptureInfoCard.Visibility = ambient ? Visibility.Visible : Visibility.Collapsed;
        FramingNav.Visibility = ambient || image ? Visibility.Visible : Visibility.Collapsed;
        FramingPage.IsEnabled = ambient || image;
        AmbientScalingCard.Visibility = ambient ? Visibility.Visible : Visibility.Collapsed;
        AmbientZoomControls.Visibility = ambient ? Visibility.Visible : Visibility.Collapsed;
        ImageTransformCard.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
        if (!ambient && !image && FramingNav.IsChecked == true) AppearanceNav.IsChecked = true;
        SourceCropInfoText.Text = image ? "Remove source edges before sizing the image. Transparency is preserved; use border scale, focus and pixel offsets to position it."
            : ScaleModeBox.SelectedIndex == 1
            ? "Stretch extracts the retained source rectangle and stretches it to every canvas edge. Zoom and focus operate inside the retained portion."
            : "Fill blacks out removed source regions without rescaling the remaining image. Use zoom and focus to frame the retained portion. Blacked-out regions stay black after blur and dithering.";
    }

    private void SetStatusColor(string color) => StatusIndicator.Fill = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(color)!;

    private void SourceCropChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || CropBottomSlider is null) return;
        _loading = true;
        var slider = (System.Windows.Controls.Slider)sender;
        if (ReferenceEquals(slider, CropLeftSlider)) CropLeftSlider.Value = Math.Min(slider.Value, 99 - CropRightSlider.Value);
        else if (ReferenceEquals(slider, CropRightSlider)) CropRightSlider.Value = Math.Min(slider.Value, 99 - CropLeftSlider.Value);
        else if (ReferenceEquals(slider, CropTopSlider)) CropTopSlider.Value = Math.Min(slider.Value, 99 - CropBottomSlider.Value);
        else CropBottomSlider.Value = Math.Min(slider.Value, 99 - CropTopSlider.Value);
        _loading = false;
        RefreshValueLabels(); UpdateSelectedProfile();
    }
    private void ResetSourceCrop_OnClick(object sender, RoutedEventArgs e)
    {
        _loading = true;
        CropLeftSlider.Value = CropRightSlider.Value = CropTopSlider.Value = CropBottomSlider.Value = 0;
        _loading = false;
        RefreshValueLabels(); UpdateSelectedProfile();
    }
    private void LoadCanvases()
    {
        try
        {
            if (File.Exists(_canvasSettingsPath))
                _canvases.AddRange((JsonSerializer.Deserialize<List<DisplayProfile>>(File.ReadAllText(_canvasSettingsPath)) ?? new()).Where(p => p.Canvas is { IsValid: true }));
        }
        catch { ApplicationSettingsStatusText.Text = "Could not load custom canvases."; }
    }
    private void RefreshTargetControls(DisplayProfile? selected = null)
    {
        bool boxes = _applicationSettings.TargetMode == CanvasTargetMode.CustomBoxes;
        bool loading = _loadingPreferences; _loadingPreferences = true;
        DisplayTargetButton.IsChecked = !boxes; CanvasTargetButton.IsChecked = boxes;
        _loadingPreferences = loading;
        CanvasActions.Visibility = boxes ? Visibility.Visible : Visibility.Collapsed;
        EmptyCanvasText.Visibility = boxes && _canvases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DisplayTabs.ItemsSource = null;
        DisplayTabs.ItemsSource = ActiveProfiles;
        if (selected is not null && ActiveProfiles.Contains(selected)) DisplayTabs.SelectedItem = selected;
        else if (ActiveProfiles.Count > 0) DisplayTabs.SelectedIndex = Math.Max(0, ActiveProfiles.FindIndex(p => p.Enabled));
        bool hasTarget = ActiveProfiles.Count > 0;
        SettingsScroll.IsEnabled = true; ApplyAllButton.IsEnabled = hasTarget;
        PreviewCard.IsEnabled = hasTarget;
        if (!hasTarget) { CanvasPreviewSurface.Dispose(); PreviewStatusText.Text = "Choose or draw a target to preview."; }
        AppearancePage.IsEnabled = hasTarget; PerformancePage.IsEnabled = hasTarget;
        RedrawCanvasButton.IsEnabled = boxes && hasTarget; DeleteCanvasButton.IsEnabled = boxes && hasTarget;
        ApplyAllButton.Content = boxes ? "Apply to all canvases" : "Apply to all displays";
        if (!hasTarget)
        {
            DisplayProfileNameText.Text = "Custom canvases"; DisplayDetailsText.Text = "Draw a box to choose its settings.";
            CanvasBoundsEditor.Visibility = Visibility.Collapsed; DisplayEnabledBox.IsEnabled = false;
        }
        else DisplayEnabledBox.IsEnabled = true;
    }
    private async void TargetModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingPreferences) return;
        var mode = ReferenceEquals(sender, CanvasTargetButton) ? CanvasTargetMode.CustomBoxes : CanvasTargetMode.Displays;
        if (_applicationSettings.TargetMode == mode) return;
        CancelShortcutRecording();
        CancelAutomationForManualAction(persistCurrent: true);
        bool running = _overlayRunning;
        _applicationSettings.TargetMode = mode;
        RefreshTargetControls(); SaveApplicationSettings();
        if (running && _desktopIntegrationEnabled)
        {
            _overlayRunning = ActiveProfiles.Any(p => p.Enabled);
            StartButton.Content = _overlayRunning ? "Stop overlay" : "Start overlay";
            await ReplaceRendererAsync(_overlayRunning);
        }
    }
    private async void DrawCanvas_OnClick(object sender, RoutedEventArgs e) => await SelectCanvasAsync(false);
    private async void RedrawCanvas_OnClick(object sender, RoutedEventArgs e) => await SelectCanvasAsync(true);
    private async Task SelectCanvasAsync(bool redraw)
    {
        if (!_desktopIntegrationEnabled || _selectingCanvas || _closed || _workerCloseInProgress) return; // automated checks never show the selector
        var existing = redraw ? SelectedProfile : null;
        if (redraw && existing?.Canvas is null) return;
        CancelShortcutRecording();
        _selectingCanvas = true;
        bool running = _overlayRunning;
        bool visible = IsVisible;
        try
        {
            await _rendererHost.StopAsync(); // reveals desktop and prevents overlay capture/input interference
            Hide();
            var selector = new DesktopSelectionWindow();
            if (selector.ShowDialog() == true && selector.Selection is { } selection)
            {
                if (existing is not null) { existing.Canvas = selection; RefreshTargetControls(existing); }
                else AddCanvas(selection);
                ProfileSaveStateText.Text = "Unsaved changes";
            }
        }
        catch (Exception ex)
        {
            ApplicationSettingsStatusText.Text = $"Could not select a canvas: {ex.Message}";
        }
        finally
        {
            try
            {
                if (!_closed && !_workerCloseInProgress && !_exitRequested && visible) { Show(); Activate(); }
                if (!_closed && !_workerCloseInProgress && !_exitRequested && running) await ReplaceRendererAsync(true);
            }
            finally { _selectingCanvas = false; }
        }
    }
    internal DisplayProfile AddCanvas(CanvasBounds bounds)
    {
        if (!bounds.IsValid) throw new ArgumentException("Choose a canvas at least 16 pixels per side.", nameof(bounds));
        int number = _canvases.Count + 1;
        while (_canvases.Any(p => p.DisplayName == $"Canvas {number}")) number++;
        var canvas = new DisplayProfile { DeviceName = "canvas-" + Guid.NewGuid().ToString("N"), DisplayName = $"Canvas {number}", Canvas = bounds, Enabled = true };
        if (SelectedProfile is { } source) canvas.CopyDisplaySettingsFrom(source);
        _canvases.Add(canvas);
        _applicationSettings.TargetMode = CanvasTargetMode.CustomBoxes;
        RefreshTargetControls(canvas); SaveApplicationSettings();
        ProfileSaveStateText.Text = "Unsaved changes";
        return canvas;
    }
    private void ApplyCanvasBounds_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile?.Canvas is null) return;
        if (!int.TryParse(CanvasLeftBox.Text, out var left) || !int.TryParse(CanvasTopBox.Text, out var top) ||
            !int.TryParse(CanvasWidthBox.Text, out var width) || !int.TryParse(CanvasHeightBox.Text, out var height) || !new CanvasBounds(left, top, width, height).IsValid)
        {
            CanvasBoundsInfoText.Text = "Use integer coordinates and a size from 16 to 16384 pixels per side.";
            return;
        }
        SelectedProfile.Canvas = new CanvasBounds(left, top, width, height);
        DisplayTabs.Items.Refresh(); RefreshEditor(); ApplySettings();
        CanvasBoundsInfoText.Text = "Bounds updated · Save profiles to keep them";
        ProfileSaveStateText.Text = "Unsaved changes";
    }
    private void DeleteCanvas_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { Canvas: not null } canvas) return;
        _canvases.Remove(canvas); RefreshTargetControls(); ApplySettings();
        ProfileSaveStateText.Text = "Unsaved changes";
    }

    private void LoadApplicationSettings()
    {
        try
        {
            if (File.Exists(_applicationSettingsPath))
                _applicationSettings = JsonSerializer.Deserialize<ApplicationSettings>(File.ReadAllText(_applicationSettingsPath)) ?? new();
        }
        catch { ApplicationSettingsStatusText.Text = "Could not load app preferences. Defaults are in use."; }
        if (!Enum.IsDefined(_applicationSettings.Backend)) _applicationSettings.Backend = RenderingBackend.Gpu;
        if (!Enum.IsDefined(_applicationSettings.TargetMode)) _applicationSettings.TargetMode = CanvasTargetMode.Displays;
        if (_applicationSettings.Shortcut is null || !_applicationSettings.Shortcut.IsValid) _applicationSettings.Shortcut = new();
        if (_applicationSettings.NextPresetShortcut is null || !_applicationSettings.NextPresetShortcut.IsValid) _applicationSettings.NextPresetShortcut = new();
        if (_applicationSettings.PreviousPresetShortcut is null || !_applicationSettings.PreviousPresetShortcut.IsValid) _applicationSettings.PreviousPresetShortcut = new();
        if (!Enum.IsDefined(_applicationSettings.Theme)) _applicationSettings.Theme = InterfaceTheme.System;
        _applicationSettings.AcrylicTransparency = double.IsFinite(_applicationSettings.AcrylicTransparency) ? Math.Clamp(_applicationSettings.AcrylicTransparency, 0, 1) : 0.7;
    }

    private bool SaveApplicationSettings()
    {
        try
        {
            SettingsStorage.Save(_applicationSettingsPath, PersistentApplicationSettings());
            ApplicationSettingsStatusText.Text = "App preferences saved immediately. Setup effects, colors and images use Save profiles.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ApplicationSettingsStatusText.Text = $"Could not save app preferences: {ex.Message}";
            return false;
        }
    }

    private void RefreshApplicationControls()
    {
        bool loading = _loadingPreferences; _loadingPreferences = true;
        GpuBackendButton.IsChecked = _applicationSettings.Backend == RenderingBackend.Gpu;
        CpuBackendButton.IsChecked = _applicationSettings.Backend == RenderingBackend.Cpu;
        BackendDescriptionText.Text = _applicationSettings.Backend == RenderingBackend.Gpu ? "Direct3D 11" : "GDI · software effects";
        ProcessingEngineText.Text = _applicationSettings.Backend == RenderingBackend.Gpu ? "GPU" : "CPU";
        CloseToTrayBox.IsChecked = _applicationSettings.CloseToTray;
        ShortcutBox.Text = _applicationSettings.Shortcut.ToString();
        AcrylicBox.IsChecked = _applicationSettings.Acrylic;
        ThemeBox.SelectedIndex = (int)_applicationSettings.Theme;
        AcrylicTransparencySlider.Value = _applicationSettings.AcrylicTransparency * 100;
        AcrylicTransparencyValue.Text = $"{_applicationSettings.AcrylicTransparency:P0}";
        PreviewGuidesBox.IsChecked = _applicationSettings.PreviewGuides; PreviewSnapBox.IsChecked = _applicationSettings.PreviewSnap;
        GpuHdrBox.IsChecked = _applicationSettings.GpuHdrAutomatic;
        _loadingPreferences = loading;
    }

    private async void BackendChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingPreferences) return;
        CancelShortcutRecording();
        var backend = (sender as System.Windows.Controls.RadioButton)?.Tag as string == "Cpu" ? RenderingBackend.Cpu : RenderingBackend.Gpu;
        if (_applicationSettings.Backend == backend) return;
        bool running = _overlayRunning;
        _applicationSettings.Backend = backend;
        RefreshApplicationControls();
        SaveApplicationSettings();
        if (_desktopIntegrationEnabled) await ReplaceRendererAsync(running);
    }

    private void RenameDisplay_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile) return;
        NicknameBox.Text = profile.Nickname;
        NicknameEditor.Visibility = Visibility.Visible;
        NicknameBox.Focus(); NicknameBox.SelectAll();
    }
    private void ApplyNickname_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile) return;
        profile.Nickname = NicknameBox.Text;
        RefreshEditor();
        ProfileSaveStateText.Text = "Unsaved changes";
    }
    private void ClearNickname_OnClick(object sender, RoutedEventArgs e)
    {
        NicknameBox.Text = "";
        ApplyNickname_OnClick(sender, e);
    }
    private void NicknameBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyNickname_OnClick(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { NicknameEditor.Visibility = Visibility.Collapsed; e.Handled = true; }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Apply the selected theme/backdrop after FluentWindow creates its HWND.
        ApplyInterfaceAppearance(force: true);
        WatchSettingsWindowMovement();
        if (!_desktopIntegrationEnabled) return;
        _hotkey = new GlobalHotkey(new WindowInteropHelper(this).Handle, async () => await InvokeShortcutAsync("Toggle", local: false));
        RestoreShortcut();
        RestorePresetShortcuts();
        UpdateTrayIcon();
        UpdateAutomationWatcher();
    }
    private void ToggleOverlays()
    {
        if (_recordingShortcut || _selectingCanvas || _switchingPreset || _workerCloseInProgress || _closed) return;
        CancelAutomationForManualAction(persistCurrent: true);
        if (_overlayRunning) StopOverlays();
        else StartOverlays();
    }
    private void RecordShortcut_OnClick(object sender, RoutedEventArgs e)
    {
        BeginShortcutRecording("Toggle", RecordShortcutButton, ShortcutBox);
    }
    private void ShortcutBox_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_recordingShortcut) { TryHandleLocalShortcut(e); return; }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { CancelShortcutRecording(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) { ShortcutInfoText.Text = "That key is not supported. Try another key combination."; return; }
        var shortcut = new ShortcutDefinition { Modifiers = (uint)Keyboard.Modifiers, VirtualKey = virtualKey };
        if (_shortcutTarget == "Toggle") SetShortcut(shortcut);
        else SetPresetShortcut(_shortcutTarget, shortcut);
    }
    private bool SetShortcut(ShortcutDefinition shortcut)
    {
        if (!shortcut.IsValid) { ShortcutInfoText.Text = "Use a modifier (Ctrl, Alt, Shift or Win) plus another key."; return false; }
        if (ShortcutConflicts("Toggle", shortcut)) { ShortcutInfoText.Text = "That key combination is assigned to another Ambient Monitor action."; return false; }
        if (_hotkey is not null && !_hotkey.TrySet(shortcut, out var error)) { ShortcutInfoText.Text = error; return false; }
        _applicationSettings.Shortcut = shortcut;
        _recordingShortcut = false;
        RecordShortcutButton.Content = "Record";
        ShortcutBox.Text = shortcut.ToString();
        ShortcutInfoText.Text = shortcut.IsConfigured ? "Shortcut active globally, including while the window is in the tray." : "Use Ctrl, Alt, Shift or Win plus another key. Escape cancels recording.";
        SaveApplicationSettings();
        FinishShortcutRecording();
        return true;
    }
    private void ClearShortcut_OnClick(object sender, RoutedEventArgs e) => SetShortcut(new ShortcutDefinition());
    private void CancelShortcutRecording()
    {
        if (!_recordingShortcut) return;
        _recordingShortcut = false;
        FinishShortcutRecording();
        RestoreShortcut();
        RestorePresetShortcuts();
    }
    private void RestoreShortcut()
    {
        if (_hotkey is not null && !_hotkey.TrySet(_applicationSettings.Shortcut, out var error)) ShortcutInfoText.Text = error;
        else ShortcutInfoText.Text = _applicationSettings.Shortcut.IsConfigured ? "Shortcut active globally, including while the window is in the tray." : "Use Ctrl, Alt, Shift or Win plus another key. Escape cancels recording.";
    }
    private void CloseToTrayChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingPreferences) return;
        _applicationSettings.CloseToTray = CloseToTrayBox.IsChecked == true;
        UpdateTrayIcon();
        SaveApplicationSettings();
    }
    private void UpdateTrayIcon()
    {
        if (!_desktopIntegrationEnabled) return;
        if (_applicationSettings.CloseToTray)
        {
            _tray ??= new TrayIcon(
                () => Dispatcher.BeginInvoke(new Action(ShowFromTray)),
                () => Dispatcher.BeginInvoke(new Action(ToggleOverlays)),
                () => Dispatcher.BeginInvoke(new Action(ExitApplication)),
                () => _overlayRunning,
                () => IsDarkTheme,
                () => _presets.Select(p => new TrayPresetItem(p.Id, p.Name, p.Id == _applicationSettings.ActivePresetId)).ToList(),
                id => Dispatcher.BeginInvoke(new Action(async () => await LoadPresetAsync(id))),
                () => _applicationSettings.AutomationPaused,
                () => Dispatcher.BeginInvoke(new Action(() => SetAutomationPaused(!_applicationSettings.AutomationPaused))));
            _tray.Update(_overlayRunning);
        }
        else { _tray?.Dispose(); _tray = null; }
    }
    private void ShowFromTray() => RestoreSettingsWindow(activate: true);
    internal void RestoreSettingsWindow(bool activate)
    {
        if (_closed || _workerCloseInProgress || _exitRequested) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (activate) Activate();
        RefreshSurfaceAfterRestore();
    }
    private void ExitApp_OnClick(object sender, RoutedEventArgs e) => ExitApplication();
    internal void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel && !_closeConfirmed && !_workerCloseInProgress)
        {
            if (!ConfirmUnsaved()) { e.Cancel = true; _exitRequested = false; return; }
            _closeConfirmed = true;
        }
        if (!e.Cancel && !_exitRequested && _desktopIntegrationEnabled && _applicationSettings.CloseToTray)
        {
            CancelShortcutRecording();
            UpdateTrayIcon();
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closed || _workerCloseInProgress || _exitRequested) return;
                Hide(); _closeConfirmed = false;
            }), System.Windows.Threading.DispatcherPriority.Background);
            return;
        }
        if (!e.Cancel && !_workerClosed)
        {
            if (_workerCloseInProgress) { e.Cancel = true; return; }
            _exitRequested = true;
            _workerCloseInProgress = true;
            _automationTimer?.Stop();
            var shutdown = _rendererHost.DisposeAsync().AsTask();
            if (shutdown.IsCompleted)
            {
                try { shutdown.GetAwaiter().GetResult(); }
                catch (Exception ex) { System.Diagnostics.Trace.TraceError("Worker shutdown: {0}", ex); }
                _workerClosed = true;
                return; // nothing pending: allow this original close to complete
            }
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(async () => await FinishClosingAsync(shutdown)), System.Windows.Threading.DispatcherPriority.Background);
        }
    }
    private async Task FinishClosingAsync(Task shutdown)
    {
        if (_closed) return;
        // Run after WPF has finished cancelling the original native close. Do
        // not Hide/Close reentrantly from its Closing event or async-void stack.
        Hide();
        try { await shutdown; }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Worker shutdown: {0}", ex); }
        _workerClosed = true;
        if (!_closed) Close();
    }
    private void ApplyWindowsAccent()
    {
        if (System.Windows.Application.Current is not null)
        {
            Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
                Wpf.Ui.Appearance.ApplicationAccentColorManager.GetColorizationColor(),
                IsDarkTheme ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light, systemAccentColor: true);
        }
        UpdateAccentResources();
    }

    private void UpdateAccentResources()
    {
        var windowsAccent = Wpf.Ui.Appearance.ApplicationAccentColorManager.GetColorizationColor();
        MediaColor ColorResource(string key, MediaColor fallback) =>
            Wpf.Ui.UiApplication.Current.Resources[key] is MediaColor color ? color : fallback;
        MediaColor Lighter(double amount) => MediaColor.FromRgb(
            (byte)(windowsAccent.R + (255 - windowsAccent.R) * amount),
            (byte)(windowsAccent.G + (255 - windowsAccent.G) * amount),
            (byte)(windowsAccent.B + (255 - windowsAccent.B) * amount));

        var primary = ColorResource("SystemAccentColorPrimary", Lighter(0.35));
        var secondary = ColorResource("SystemAccentColorSecondary", Lighter(0.45));
        var tertiary = ColorResource("SystemAccentColorTertiary", Lighter(0.55));
        var fill = ColorResource("AccentFillColorDefault", secondary);
        var hover = ColorResource("AccentFillColorSecondary", MediaColor.FromArgb(229, fill.R, fill.G, fill.B));
        var pressed = ColorResource("AccentFillColorTertiary", MediaColor.FromArgb(204, fill.R, fill.G, fill.B));

        // Local theme dictionaries contain default accents. Override them with the
        // Windows palette so every Fluent control and custom highlight agrees.
        Resources["SystemAccentColorPrimary"] = primary;
        Resources["SystemAccentColorSecondary"] = secondary;
        Resources["SystemAccentColorTertiary"] = tertiary;
        Resources["AccentFillColorDefault"] = fill;
        Resources["AccentFillColorSecondary"] = hover;
        Resources["AccentFillColorTertiary"] = pressed;
        Resources["AccentFillColorDefaultBrush"] = new SolidColorBrush(fill);
        Resources["AccentButtonBackground"] = new SolidColorBrush(fill);
        Resources["AccentButtonBackgroundPointerOver"] = new SolidColorBrush(hover);
        Resources["AccentButtonBackgroundPressed"] = new SolidColorBrush(pressed);
        var text = ColorResource("TextOnAccentFillColorPrimary", System.Windows.Media.Colors.Black);
        Resources["AccentButtonForeground"] = new SolidColorBrush(text);
        Resources["AccentButtonForegroundPointerOver"] = new SolidColorBrush(text);
        Resources["AccentButtonForegroundPressed"] = new SolidColorBrush(text);
        Resources["AccentText"] = new SolidColorBrush(primary);
        Resources["AccentTint"] = new SolidColorBrush(MediaColor.FromArgb(20, primary.R, primary.G, primary.B));
        Resources["AccentTintBorder"] = new SolidColorBrush(MediaColor.FromArgb(38, primary.R, primary.G, primary.B));
    }

    private void WindowsPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.Color or UserPreferenceCategory.General or
            UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Desktop)) return;
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(QueueAppearanceRefresh));
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _previewTimer?.Stop(); CanvasPreviewSurface.Dispose();
        _automationGeneration++;
        _automationTimer?.Stop();
        StopWatchingSettingsWindowMovement();
        SystemEvents.UserPreferenceChanged -= WindowsPreferenceChanged;
        _hotkey?.Dispose(); _hotkey = null;
        _nextPresetHotkey?.Dispose(); _previousPresetHotkey?.Dispose();
        foreach (var hotkey in _presetHotkeys.Values) hotkey.Dispose();
        _tray?.Dispose(); _tray = null;
        base.OnClosed(e);
        if (_desktopIntegrationEnabled) System.Windows.Application.Current?.Shutdown();
    }
}
