using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace AmbientMonitor;

public partial class MainWindow
{
    private readonly List<SetupPreset> _presets = new();
    private readonly Dictionary<string, GlobalHotkey> _presetHotkeys = new();
    private GlobalHotkey? _nextPresetHotkey, _previousPresetHotkey;
    private string _savedFingerprint = "";
    private bool _refreshingPresets, _switchingPreset, _closeConfirmed;
    private string PresetsPath => Path.Combine(Path.GetDirectoryName(_settingsPath)!, "presets.json");
    internal Func<MessageBoxResult>? UnsavedPrompt { get; set; }
    internal bool HasUnsavedChanges => _savedFingerprint != CaptureSetup().Fingerprint();

    internal SetupSnapshot CaptureSetup() => new SetupSnapshot
    {
        TargetMode = _applicationSettings.TargetMode, Displays = _profiles, Canvases = _canvases
    }.Copy();

    private void LoadPresets()
    {
        try
        {
            if (File.Exists(PresetsPath))
                _presets.AddRange((JsonSerializer.Deserialize<List<SetupPreset>>(File.ReadAllText(PresetsPath)) ?? new())
                    .Where(p => !string.IsNullOrWhiteSpace(p.Id) && p.Setup is not null).DistinctBy(p => p.Id));
        }
        catch (Exception ex) { ApplicationSettingsStatusText.Text = $"Could not load profiles: {ex.Message}"; }
        foreach (var preset in _presets)
        {
            if (preset.Shortcut is null || !preset.Shortcut.IsValid) preset.Shortcut = new();
            preset.Automation ??= new(); preset.Automation.ExecutablePath ??= "";
            preset.Automation.ExecutablePaths ??= new();
            preset.Automation.ExitDelaySeconds = double.IsFinite(preset.Automation.ExitDelaySeconds) ? Math.Clamp(preset.Automation.ExitDelaySeconds, 0, 30) : 0;
            preset.Setup.Displays ??= new(); preset.Setup.Canvases ??= new();
            if (!Enum.IsDefined(preset.Setup.TargetMode)) preset.Setup.TargetMode = CanvasTargetMode.Displays;
        }
        if (!_presets.Any(p => p.Id == _applicationSettings.ActivePresetId)) _applicationSettings.ActivePresetId = null;
        _savedFingerprint = CaptureSetup().Fingerprint();
        RefreshPresetControls();
    }

    private void RefreshPresetControls(SetupPreset? selectedOverride = null)
    {
        _refreshingPresets = true;
        var selected = selectedOverride ?? PresetBox.SelectedItem as SetupPreset;
        var matches = SearchProfiles(ProfileSearchBox?.Text ?? "");
        PresetBox.ItemsSource = null; PresetBox.ItemsSource = matches;
        PresetBox.SelectedItem = selected is not null && matches.Contains(selected) ? selected : matches.FirstOrDefault(p => p.Id == _applicationSettings.ActivePresetId) ?? matches.FirstOrDefault();
        _refreshingPresets = false;
        PresetShortcutBox.Text = (PresetBox.SelectedItem as SetupPreset)?.Shortcut.ToString() ?? "Not set";
        ActivePresetText.Text = _presets.FirstOrDefault(p => p.Id == _applicationSettings.ActivePresetId)?.Name ?? "Current setup";
        NextPresetShortcutBox.Text = _applicationSettings.NextPresetShortcut.ToString();
        PreviousPresetShortcutBox.Text = _applicationSettings.PreviousPresetShortcut.ToString();
        RefreshQuickProfileStatus();
        RefreshAutomationControls();
        RefreshProfileLibraryEditor();
    }

    private void PresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingPresets || PresetShortcutBox is null) return;
        CancelShortcutRecording();
        PresetShortcutBox.Text = (PresetBox.SelectedItem as SetupPreset)?.Shortcut.ToString() ?? "Not set";
        RefreshAutomationControls();
        RefreshProfileLibraryEditor();
    }

    private bool SaveWorkspace()
    {
        try
        {
            var setup = CaptureSetup();
            var active = _presets.FirstOrDefault(p => p.Id == _applicationSettings.ActivePresetId);
            if (_automationSession is null)
            {
                SettingsStorage.Save(_settingsPath, setup.Displays);
                SettingsStorage.Save(_canvasSettingsPath, setup.Canvases);
            }
            if (active is not null)
            {
                var previous = active.Setup; active.Setup = setup;
                try { SettingsStorage.Save(PresetsPath, _presets); }
                catch { active.Setup = previous; throw; }
            }
            if (!SaveApplicationSettings()) return false;
            _savedFingerprint = setup.Fingerprint();
            ProfileSaveStateText.Text = active is null ? "Setup saved" : $"Saved · {active.Name}";
            return true;
        }
        catch (Exception ex)
        {
            ProfileSaveStateText.Text = "Save failed";
            ApplicationSettingsStatusText.Text = $"Could not save setup: {ex.Message}";
            return false;
        }
    }

    internal bool SavePresetAs(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 60 || _presets.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            ApplicationSettingsStatusText.Text = "Choose a unique profile name (1–60 characters).";
            return false;
        }
        var preset = new SetupPreset { Name = name, Setup = CaptureSetup() };
        _presets.Add(preset);
        try { SettingsStorage.Save(PresetsPath, _presets); }
        catch (Exception ex) { _presets.Remove(preset); ApplicationSettingsStatusText.Text = $"Could not save profile: {ex.Message}"; return false; }
        CancelAutomationForManualAction();
        _applicationSettings.ActivePresetId = preset.Id;
        ProfileSearchBox.Text = ""; RefreshPresetControls(preset); RestorePresetShortcuts();
        return SaveWorkspace();
    }
    private async void LoadPreset_OnClick(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is SetupPreset preset) await LoadPresetAsync(preset.Id);
    }

    private bool ConfirmUnsaved()
    {
        if (!HasUnsavedChanges) return true;
        // Isolated checks can supply a response and never open a visible prompt.
        var answer = UnsavedPrompt?.Invoke() ?? (!_desktopIntegrationEnabled ? MessageBoxResult.No :
            RunSettingsDialog(() => MessageBox.Show(this, "This setup has unsaved changes. Save them before continuing?\n\nYes: save · No: continue without saving · Cancel: keep editing",
                "Unsaved profile changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning)));
        return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && SaveWorkspace());
    }

    private void ApplySetupToEditor(SetupSnapshot setup, string? id, string? selectedDevice = null)
    {
        selectedDevice ??= SelectedProfile?.DeviceName;
        foreach (var profile in _profiles)
        {
            var saved = setup.Displays.FirstOrDefault(p => p.DeviceName == profile.DeviceName);
            profile.CopyDisplaySettingsFrom(saved ?? new OverlaySettings());
            profile.Enabled = saved?.Enabled ?? !profile.IsPrimary;
            profile.Nickname = saved?.Nickname ?? "";
        }
        _canvases.Clear(); _canvases.AddRange(setup.Canvases.Where(p => p.Canvas is { IsValid: true }));
        _applicationSettings.TargetMode = setup.TargetMode;
        _applicationSettings.ActivePresetId = id;
        RefreshTargetControls(ActiveProfiles.FirstOrDefault(p => p.DeviceName == selectedDevice)); RefreshPresetControls(_presets.FirstOrDefault(p => p.Id == id));
    }

    internal async Task<bool> LoadPresetAsync(string id)
    {
        if (_switchingPreset || _selectingCanvas || _recordingShortcut || _workerCloseInProgress || _closed) return false;
        var preset = _presets.FirstOrDefault(p => p.Id == id);
        if (preset is null) return false;
        _switchingPreset = true;
        try
        {
            if (!ConfirmUnsaved()) return false;
            QuickSaveAsPopup.IsOpen = false;
            if (QuickProfileButton.ContextMenu is { } menu) menu.IsOpen = false;
            CancelAutomationForManualAction();
            var setup = preset.Setup.Copy();
            ApplySetupToEditor(setup, preset.Id);
            // Persist the loaded setup as the next startup state, without overwriting the named preset.
            SettingsStorage.Save(_settingsPath, _profiles); SettingsStorage.Save(_canvasSettingsPath, _canvases);
            if (!SaveApplicationSettings()) return false;
            _savedFingerprint = CaptureSetup().Fingerprint();
            ProfileSaveStateText.Text = $"Loaded · {preset.Name}";
            if (_overlayRunning)
            {
                _overlayRunning = ActiveProfiles.Any(p => p.Enabled);
                StartButton.Content = _overlayRunning ? "Stop overlay" : "Start overlay";
                if (_overlayRunning) await _rendererHost.UpdateAsync(_applicationSettings.Backend, SnapshotProfiles(), true, _applicationSettings.GpuHdrAutomatic);
                else await _rendererHost.StopAsync();
                _tray?.Update(_overlayRunning);
            }
            return true;
        }
        catch (Exception ex) { ApplicationSettingsStatusText.Text = $"Could not load profile: {ex.Message}"; return false; }
        finally { _switchingPreset = false; }
    }

    internal async Task CyclePresetAsync(int direction)
    {
        if (_presets.Count == 0) return;
        int index = _presets.FindIndex(p => p.Id == _applicationSettings.ActivePresetId);
        int next = index < 0 ? (direction > 0 ? 0 : _presets.Count - 1) : (index + direction + _presets.Count) % _presets.Count;
        await LoadPresetAsync(_presets[next].Id);
    }
    private async void NextPreset_OnClick(object sender, RoutedEventArgs e) => await CyclePresetAsync(1);
    private async void PreviousPreset_OnClick(object sender, RoutedEventArgs e) => await CyclePresetAsync(-1);
    private void DeletePreset_OnClick(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not SetupPreset preset) return;
        if (_desktopIntegrationEnabled && RunSettingsDialog(() => MessageBox.Show(this, $"Delete saved profile ‘{preset.Name}’?", "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Question)) != MessageBoxResult.Yes) return;
        if (_automationSession?.PresetId == preset.Id) CancelAutomationForManualAction(persistCurrent: true);
        int index = _presets.IndexOf(preset); _presets.Remove(preset);
        try { SettingsStorage.Save(PresetsPath, _presets); }
        catch (Exception ex) { _presets.Insert(index, preset); ApplicationSettingsStatusText.Text = ex.Message; return; }
        if (_applicationSettings.ActivePresetId == preset.Id) _applicationSettings.ActivePresetId = null;
        SaveApplicationSettings(); RefreshPresetControls(); RestorePresetShortcuts(); UpdateAutomationWatcher();
    }

    private void RestorePresetShortcuts()
    {
        if (!_desktopIntegrationEnabled || new System.Windows.Interop.WindowInteropHelper(this).Handle == 0) return;
        foreach (var hotkey in _presetHotkeys.Values) hotkey.Dispose();
        _presetHotkeys.Clear();
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _nextPresetHotkey ??= new GlobalHotkey(handle, async () => await InvokeShortcutAsync("Next", local: false));
        _previousPresetHotkey ??= new GlobalHotkey(handle, async () => await InvokeShortcutAsync("Previous", local: false));
        Register(_nextPresetHotkey, _applicationSettings.NextPresetShortcut, "Next profile");
        Register(_previousPresetHotkey, _applicationSettings.PreviousPresetShortcut, "Previous profile");
        foreach (var preset in _presets)
        {
            string id = preset.Id;
            var hotkey = new GlobalHotkey(handle, async () => await InvokeShortcutAsync(id, local: false));
            _presetHotkeys[id] = hotkey;
            Register(hotkey, preset.Shortcut, preset.Name);
        }
        void Register(GlobalHotkey hotkey, ShortcutDefinition shortcut, string name)
        {
            if (!hotkey.TrySet(shortcut, out string error)) PresetShortcutInfoText.Text = $"{name}: {error}";
        }
    }
    private void SuspendPresetShortcuts()
    {
        _nextPresetHotkey?.Suspend(); _previousPresetHotkey?.Suspend();
        foreach (var hotkey in _presetHotkeys.Values) hotkey.Suspend();
    }
}
