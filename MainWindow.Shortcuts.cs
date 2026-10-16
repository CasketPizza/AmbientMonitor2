using System.Windows;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;
using System.Windows.Input;
using System.Runtime.InteropServices;

namespace AmbientMonitor;

public partial class MainWindow
{
    private string _shortcutTarget = "Toggle";
    private Wpf.Ui.Controls.Button? _recordingButton;
    private TextBox? _recordingBox;
    private string? _lastShortcutTarget;
    private bool _lastShortcutWasLocal;
    private long _lastShortcutTime;
    private int? _lastShortcutInputTimestamp;

    private void TryHandleLocalShortcut(System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (TryDispatchLocalShortcut(KeyInterop.VirtualKeyFromKey(key), (uint)Keyboard.Modifiers, e.IsRepeat, e.Timestamp)) e.Handled = true;
    }
    internal bool TryDispatchLocalShortcut(int virtualKey, uint modifiers, bool repeat = false, int? inputTimestamp = null)
    {
        if (_recordingShortcut) return false;
        string? target = null;
        bool Matches(ShortcutDefinition shortcut) => shortcut.IsConfigured && shortcut.VirtualKey == virtualKey && shortcut.Modifiers == modifiers;
        if (Matches(_applicationSettings.Shortcut)) target = "Toggle";
        else if (Matches(_applicationSettings.NextPresetShortcut)) target = "Next";
        else if (Matches(_applicationSettings.PreviousPresetShortcut)) target = "Previous";
        else target = _presets.FirstOrDefault(p => Matches(p.Shortcut))?.Id;
        if (target is null) return false;
        if (!repeat) _ = InvokeShortcutAsync(target, local: true, inputTimestamp);
        return true;
    }
    internal async Task<bool> InvokeShortcutAsync(string target, bool local, int? inputTimestamp = null)
    {
        if (_recordingShortcut || _selectingCanvas || _settingsDialogDepth > 0 || _closed || _workerCloseInProgress) return false;
        long now = Environment.TickCount64;
        inputTimestamp ??= local ? null : GetMessageTime();
        // WM_HOTKEY and a focused WPF key event may describe the same press.
        // Suppress only the other delivery route; repeated separate presses on
        // the same route remain responsive (native registration uses NOREPEAT).
        bool samePress = inputTimestamp is { } stamp && _lastShortcutInputTimestamp is { } previousStamp
            ? Math.Abs((long)unchecked(stamp - previousStamp)) <= 5
            : now - _lastShortcutTime < 250;
        if (_lastShortcutTarget == target && _lastShortcutWasLocal != local && samePress) return false;
        _lastShortcutTarget = target; _lastShortcutWasLocal = local; _lastShortcutTime = now;
        _lastShortcutInputTimestamp = inputTimestamp;
        try
        {
            if (target == "Toggle") { ToggleOverlays(); return true; }
            if (target is "Next" or "Previous")
            {
                if (_presets.Count == 0) return false;
                await CyclePresetAsync(target == "Next" ? 1 : -1); return true;
            }
            return await LoadPresetAsync(target);
        }
        catch (Exception ex) { if (!_closed) ApplicationSettingsStatusText.Text = $"Shortcut: {ex.Message}"; return false; }
    }
    [DllImport("user32.dll")] private static extern int GetMessageTime();

    private void BeginShortcutRecording(string target, Wpf.Ui.Controls.Button button, TextBox box)
    {
        if (_recordingShortcut && _shortcutTarget == target) { CancelShortcutRecording(); return; }
        CancelShortcutRecording();
        _shortcutTarget = target; _recordingButton = button; _recordingBox = box;
        _recordingShortcut = true;
        _hotkey?.Suspend(); SuspendPresetShortcuts();
        box.Text = "Press a key combination…"; button.Content = "Cancel";
        box.Focus();
    }
    private void FinishShortcutRecording()
    {
        _recordingShortcut = false;
        if (_recordingButton is not null) _recordingButton.Content = "Record";
        _recordingButton = null; _recordingBox = null;
        ShortcutBox.Text = _applicationSettings.Shortcut.ToString();
        NextPresetShortcutBox.Text = _applicationSettings.NextPresetShortcut.ToString();
        PreviousPresetShortcutBox.Text = _applicationSettings.PreviousPresetShortcut.ToString();
        PresetShortcutBox.Text = (PresetBox.SelectedItem as SetupPreset)?.Shortcut.ToString() ?? "Not set";
        RestoreShortcut(); RestorePresetShortcuts();
    }
    private void RecordPresetShortcut_OnClick(object sender, RoutedEventArgs e)
    {
        var button = (Wpf.Ui.Controls.Button)sender;
        string target = button.Tag as string ?? "";
        if (target == "Preset")
        {
            if (PresetBox.SelectedItem is not SetupPreset preset) { PresetShortcutInfoText.Text = "Save and select a profile first."; return; }
            BeginShortcutRecording(preset.Id, button, PresetShortcutBox);
        }
        else BeginShortcutRecording(target, button, target == "Next" ? NextPresetShortcutBox : PreviousPresetShortcutBox);
    }
    private void ClearPresetShortcut_OnClick(object sender, RoutedEventArgs e)
    {
        CancelShortcutRecording();
        string target = ((Wpf.Ui.Controls.Button)sender).Tag as string ?? "";
        if (target == "Preset")
        {
            if (PresetBox.SelectedItem is not SetupPreset preset) return;
            target = preset.Id;
        }
        SetPresetShortcut(target, new ShortcutDefinition());
    }
    private bool ShortcutConflicts(string target, ShortcutDefinition shortcut)
    {
        if (!shortcut.IsConfigured) return false;
        var assignments = new List<(string Target, ShortcutDefinition Shortcut)>
        {
            ("Toggle", _applicationSettings.Shortcut), ("Next", _applicationSettings.NextPresetShortcut), ("Previous", _applicationSettings.PreviousPresetShortcut)
        };
        assignments.AddRange(_presets.Select(p => (p.Id, p.Shortcut)));
        return assignments.Any(a => a.Target != target && a.Shortcut.VirtualKey == shortcut.VirtualKey && a.Shortcut.Modifiers == shortcut.Modifiers);
    }
    internal bool SetPresetShortcut(string target, ShortcutDefinition shortcut)
    {
        if (!shortcut.IsValid || ShortcutConflicts(target, shortcut))
        {
            PresetShortcutInfoText.Text = "Use a modifier plus a key that is not assigned to another action.";
            return false;
        }
        var preset = _presets.FirstOrDefault(p => p.Id == target);
        if (target is not ("Next" or "Previous") && preset is null) return false;
        GlobalHotkey? registered = target == "Next" ? _nextPresetHotkey : target == "Previous" ? _previousPresetHotkey : _presetHotkeys.GetValueOrDefault(target);
        if (registered is not null && !registered.TrySet(shortcut, out string error)) { PresetShortcutInfoText.Text = error; return false; }
        var previous = target == "Next" ? _applicationSettings.NextPresetShortcut : target == "Previous" ? _applicationSettings.PreviousPresetShortcut : preset!.Shortcut;
        if (target == "Next") _applicationSettings.NextPresetShortcut = shortcut;
        else if (target == "Previous") _applicationSettings.PreviousPresetShortcut = shortcut;
        else preset!.Shortcut = shortcut;
        bool success;
        if (preset is not null)
        {
            try { SettingsStorage.Save(PresetsPath, _presets); success = true; }
            catch (Exception ex) { preset.Shortcut = previous; PresetShortcutInfoText.Text = $"Could not save shortcut: {ex.Message}"; success = false; }
        }
        else
        {
            success = SaveApplicationSettings();
            if (!success)
            {
                if (target == "Next") _applicationSettings.NextPresetShortcut = previous;
                else _applicationSettings.PreviousPresetShortcut = previous;
            }
        }
        FinishShortcutRecording();
        if (success) PresetShortcutInfoText.Text = shortcut.IsConfigured ? "Shortcut saved and active globally." : "Shortcut cleared.";
        return success;
    }
}
