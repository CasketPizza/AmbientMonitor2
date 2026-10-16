using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace AmbientMonitor;

public partial class MainWindow
{
    private sealed record AutomationSession(string PresetId, SetupSnapshot ReturnSetup, string? ReturnPresetId, bool ReturnRunning, string? ReturnSelectedDevice);
    private AutomationSession? _automationSession;
    private readonly HashSet<string> _automationSuppressed = new();
    private HashSet<string> _automationMatching = new();
    private DispatcherTimer? _automationTimer;
    private bool _automationPollBusy, _automationEvaluating, _refreshingAutomation;
    private long _automationGeneration;
    private bool _suppressNextAutomationMatches;
    private readonly Dictionary<string, long> _automationExitDeadlines = new();
    internal Func<long> AutomationClock { get; set; } = () => Environment.TickCount64;
    internal string? AutomatedPresetId => _automationSession?.PresetId;

    private void RefreshAutomationControls()
    {
        if (AutomationEnabledBox is null) return;
        var preset = PresetBox.SelectedItem as SetupPreset;
        _refreshingAutomation = true;
        AutomationEnabledBox.IsEnabled = preset is not null;
        AutomationEnabledBox.IsChecked = preset?.Automation.Enabled == true;
        AutomationExecutableText.Text = preset?.Automation.Paths.Count > 0 ? $"{preset.Automation.Paths.Count} executable trigger(s)" : "No executable selected";
        AutomationExecutablesList.ItemsSource = preset?.Automation.Paths;
        AutomationExitDelaySlider.Value = preset?.Automation.ExitDelaySeconds ?? 0;
        AutomationExitDelayValue.Text = $"{AutomationExitDelaySlider.Value:0.#} s";
        _refreshingAutomation = false;
    }
    private void AutomationEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_refreshingAutomation || _refreshingPresets) return;
        if (PresetBox.SelectedItem is SetupPreset preset)
            if (!SetExecutableTriggers(preset.Id, preset.Automation.Paths, AutomationEnabledBox.IsChecked == true, preset.Automation.ExitDelaySeconds)) RefreshAutomationControls();
    }
    private void ChooseAutomationExecutable_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_desktopIntegrationEnabled || PresetBox.SelectedItem is not SetupPreset preset) return;
        var dialog = new OpenFileDialog { Title = $"Add executables for {preset.Name}", Filter = "Windows executable|*.exe", Multiselect = true };
        if (RunSettingsDialog(() => dialog.ShowDialog(this)) == true)
            SetExecutableTriggers(preset.Id, preset.Automation.Paths.Concat(dialog.FileNames), preset.Automation.Enabled, preset.Automation.Paths.Count == 0 ? 2 : preset.Automation.ExitDelaySeconds);
    }
    private void ClearAutomationExecutable_OnClick(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is SetupPreset preset) SetExecutableAutomation(preset.Id, "", false);
    }
    internal bool SetExecutableAutomation(string id, string executablePath, bool enabled)
        => SetExecutableTriggers(id, string.IsNullOrWhiteSpace(executablePath) ? Array.Empty<string>() : new[] { executablePath }, enabled, _presets.FirstOrDefault(p => p.Id == id)?.Automation.ExitDelaySeconds ?? 0);
    internal bool SetExecutableTriggers(string id, IEnumerable<string> paths, bool enabled, double exitDelaySeconds)
    {
        var preset = _presets.FirstOrDefault(p => p.Id == id);
        if (preset is null) return false;
        try
        {
            string[] executables = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if ((enabled && executables.Length == 0) || executables.Any(p => !Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase) || enabled && !File.Exists(p)))
            {
                AutomationStatusText.Text = "Choose the game's actual .exe before enabling its trigger.";
                return false;
            }
            var previous = preset.Automation;
            if (!double.IsFinite(exitDelaySeconds) || exitDelaySeconds is < 0 or > 30) throw new ArgumentException("Exit delay must be 0–30 seconds.");
            preset.Automation = new ExecutableAutomation { Enabled = enabled, ExecutablePath = executables.FirstOrDefault() ?? "", ExecutablePaths = executables.Skip(1).ToList(), ExitDelaySeconds = exitDelaySeconds };
            try { SettingsStorage.Save(PresetsPath, _presets); }
            catch { preset.Automation = previous; throw; }
            _automationSuppressed.Remove(id);
            _suppressNextAutomationMatches = false;
            RefreshAutomationControls(); UpdateAutomationWatcher();
            _automationExitDeadlines.Remove(id);
            AutomationStatusText.Text = enabled ? $"Waiting for {executables.Length} executable(s) · triggers saved" : "Triggers saved · automatic activation disabled";
            return true;
        }
        catch (Exception ex) { AutomationStatusText.Text = $"Could not save trigger: {ex.Message}"; return false; }
    }
    private void RemoveAutomationExecutable_OnClick(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not SetupPreset preset || AutomationExecutablesList.SelectedItem is not string path) return;
        var remaining = preset.Automation.Paths.Where(p => !p.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
        SetExecutableTriggers(preset.Id, remaining, preset.Automation.Enabled && remaining.Length > 0, preset.Automation.ExitDelaySeconds);
    }
    private void AutomationExitDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshingAutomation || _refreshingPresets || PresetBox.SelectedItem is not SetupPreset preset) return;
        SetExecutableTriggers(preset.Id, preset.Automation.Paths, preset.Automation.Enabled, AutomationExitDelaySlider.Value);
    }
    private void PauseAutomation_OnClick(object sender, RoutedEventArgs e) => SetAutomationPaused(!_applicationSettings.AutomationPaused);
    internal void SetAutomationPaused(bool paused)
    {
        _applicationSettings.AutomationPaused = paused;
        _automationGeneration++; _automationExitDeadlines.Clear();
        SaveApplicationSettings(); UpdateAutomationWatcher(); RefreshQuickProfileStatus();
        AutomationStatusText.Text = paused ? "Automation paused · current overlay kept as-is" : "Automation resumed";
    }
    private void UpdateAutomationWatcher()
    {
        if (!_desktopIntegrationEnabled || _closed || _workerCloseInProgress) return;
        bool needed = !_applicationSettings.AutomationPaused && (_automationSession is not null || _presets.Any(p => p.Automation.Enabled));
        if (!needed) { _automationTimer?.Stop(); return; }
        if (_automationTimer is null)
        {
            _automationTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            _automationTimer.Tick += async (_, _) =>
            {
                if (_automationPollBusy || _closed || _workerCloseInProgress) return;
                _automationPollBusy = true;
                try
                {
                    long generation = _automationGeneration;
                    string[] paths = _presets.Where(p => p.Automation.Enabled).SelectMany(p => p.Automation.Paths).ToArray();
                    var running = await Task.Run(() => ExecutableProcessReader.Read(paths));
                    if (generation == _automationGeneration) await EvaluateAutomationAsync(running);
                }
                catch (Exception ex) { if (!_closed) AutomationStatusText.Text = $"Process watcher: {ex.Message}"; }
                finally { _automationPollBusy = false; }
            };
        }
        _automationTimer.Start();
    }
    internal async Task EvaluateAutomationAsync(IReadOnlyList<RunningExecutable> processes)
    {
        if (_applicationSettings.AutomationPaused || _automationEvaluating || _switchingPreset || _recordingShortcut || _selectingCanvas || _settingsDialogDepth > 0 || QuickSaveAsPopup.IsOpen || SettingsWindowMoving || _closed || _workerCloseInProgress) return;
        _automationEvaluating = true;
        long generation = _automationGeneration;
        try
        {
            _automationMatching = _presets.Where(p => p.Automation.Enabled && processes.Any(process => p.Automation.Paths.Any(path => ExecutableProcessReader.Matches(path, process.Path))))
                .Select(p => p.Id).ToHashSet();
            if (_automationSession is { } active && _presets.FirstOrDefault(p => p.Id == active.PresetId) is { Automation.Enabled: true } current && !_automationMatching.Contains(active.PresetId))
            {
                if (!_automationExitDeadlines.TryGetValue(active.PresetId, out long deadline))
                    _automationExitDeadlines[active.PresetId] = deadline = AutomationClock() + (long)(current.Automation.ExitDelaySeconds * 1000);
                if (AutomationClock() < deadline)
                {
                    _automationMatching.Add(active.PresetId);
                    AutomationStatusText.Text = $"Waiting for executable handoff · {(deadline - AutomationClock()) / 1000d:0.#} s remaining";
                }
            }
            foreach (string id in _automationExitDeadlines.Keys.Where(id => processes.Any(process => _presets.FirstOrDefault(p => p.Id == id)?.Automation.Paths.Any(path => ExecutableProcessReader.Matches(path, process.Path)) == true)).ToArray())
                _automationExitDeadlines.Remove(id);
            if (_suppressNextAutomationMatches) { _automationSuppressed.UnionWith(_automationMatching); _suppressNextAutomationMatches = false; }
            _automationSuppressed.RemoveWhere(id => !_automationMatching.Contains(id));
            var candidate = _presets.FirstOrDefault(p => p.Id == _automationSession?.PresetId && _automationMatching.Contains(p.Id) && !_automationSuppressed.Contains(p.Id))
                ?? _presets.FirstOrDefault(p => _automationMatching.Contains(p.Id) && !_automationSuppressed.Contains(p.Id));
            if (candidate?.Id == _automationSession?.PresetId && candidate is not null)
            {
                if (!_overlayRunning && ActiveProfiles.Any(p => p.Enabled)) await ApplyAutomationRenderingAsync(true);
                return;
            }
            if (HasUnsavedChanges)
            {
                if (_automationSession is not null && !_automationMatching.Contains(_automationSession.PresetId))
                {
                    if (_overlayRunning) await ApplyAutomationRenderingAsync(false);
                    AutomationStatusText.Text = "Executable closed · overlays stopped. Save edits or choose a profile to finish restoring the previous setup.";
                }
                else if (candidate is not null) AutomationStatusText.Text = "Automation waiting · save your setup edits before automatic activation.";
                return;
            }
            if (candidate is not null)
            {
                var session = _automationSession;
                _automationSession = session is null
                    ? new AutomationSession(candidate.Id, CaptureSetup(), _applicationSettings.ActivePresetId, _overlayRunning, SelectedProfile?.DeviceName)
                    : session with { PresetId = candidate.Id };
                ApplySetupToEditor(candidate.Setup.Copy(), candidate.Id);
                _savedFingerprint = CaptureSetup().Fingerprint();
                ProfileSaveStateText.Text = $"Automatic · {candidate.Name}";
                await ApplyAutomationRenderingAsync(true);
                if (generation == _automationGeneration && !_closed) AutomationStatusText.Text = $"Automatic profile · {candidate.Name}";
            }
            else if (_automationSession is { } previous)
            {
                _automationSession = null;
                _automationExitDeadlines.Clear();
                ApplySetupToEditor(previous.ReturnSetup.Copy(), _presets.Any(p => p.Id == previous.ReturnPresetId) ? previous.ReturnPresetId : null, previous.ReturnSelectedDevice);
                _savedFingerprint = CaptureSetup().Fingerprint();
                ProfileSaveStateText.Text = "Previous setup restored";
                await ApplyAutomationRenderingAsync(previous.ReturnRunning);
                if (generation == _automationGeneration && !_closed)
                {
                    SaveApplicationSettings();
                    AutomationStatusText.Text = "Executable closed · previous setup restored";
                }
            }
        }
        catch (Exception ex) { if (!_closed) AutomationStatusText.Text = $"Automatic profile: {ex.Message}"; }
        finally { _automationEvaluating = false; UpdateAutomationWatcher(); }
    }
    private async Task ApplyAutomationRenderingAsync(bool running)
    {
        _overlayRunning = running && ActiveProfiles.Any(p => p.Enabled);
        StartButton.Content = _overlayRunning ? "Stop overlay" : "Start overlay";
        if (_overlayRunning)
        {
            if (_rendererHost.ProcessId is not null) await _rendererHost.UpdateAsync(_applicationSettings.Backend, SnapshotProfiles(), true, _applicationSettings.GpuHdrAutomatic);
            else await ReplaceRendererAsync(true);
        }
        else
        {
            ++_rendererGeneration;
            await _rendererHost.StopAsync();
            StatusText.Text = "Stopped · ready when you are"; SetStatusColor("#8E91A0");
        }
        _tray?.Update(_overlayRunning);
        RefreshQuickProfileStatus();
    }
    private void CancelAutomationForManualAction(bool persistCurrent = false)
    {
        _automationGeneration++;
        _automationExitDeadlines.Clear();
        _automationSuppressed.UnionWith(_automationMatching);
        _suppressNextAutomationMatches = true;
        var previous = _automationSession; _automationSession = null;
        if (previous is not null)
        {
            AutomationStatusText.Text = "Manual override · automation resumes after the executable closes and runs again";
            ProfileSaveStateText.Text = HasUnsavedChanges ? "Unsaved changes" : "Manual profile selection";
            if (persistCurrent)
            {
                var saved = _presets.FirstOrDefault(p => p.Id == _applicationSettings.ActivePresetId)?.Setup;
                if (saved is not null)
                {
                    try { SettingsStorage.Save(_settingsPath, saved.Displays); SettingsStorage.Save(_canvasSettingsPath, saved.Canvases); SaveApplicationSettings(); }
                    catch (Exception ex) { ApplicationSettingsStatusText.Text = $"Could not save manual selection: {ex.Message}"; }
                }
            }
        }
        RefreshQuickProfileStatus();
        UpdateAutomationWatcher();
    }
    private ApplicationSettings PersistentApplicationSettings()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<ApplicationSettings>(System.Text.Json.JsonSerializer.Serialize(_applicationSettings))!;
        if (_automationSession is { } session)
        {
            settings.ActivePresetId = session.ReturnPresetId;
            settings.TargetMode = session.ReturnSetup.TargetMode;
        }
        return settings;
    }
}
