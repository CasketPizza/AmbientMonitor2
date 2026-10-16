using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace AmbientMonitor;

public partial class MainWindow
{
    internal IReadOnlyList<SetupPreset> SearchProfiles(string query) => _presets.Where(p => p.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
    private void ProfileSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_refreshingPresets || PresetBox is null) return;
        RefreshPresetControls(PresetBox.SelectedItem as SetupPreset);
    }
    private void RefreshProfileLibraryEditor()
    {
        if (ProfileRenameBox is null) return;
        ProfileRenameBox.Text = (PresetBox.SelectedItem as SetupPreset)?.Name ?? "";
    }
    internal bool RenameProfile(string id, string name)
    {
        var preset = _presets.FirstOrDefault(p => p.Id == id); name = name.Trim();
        if (preset is null || name.Length is < 1 or > 60 || _presets.Any(p => p.Id != id && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        { ProfileLibraryStatusText.Text = "Choose a unique name (1–60 characters)."; return false; }
        string previous = preset.Name; preset.Name = name;
        try { SettingsStorage.Save(PresetsPath, _presets); }
        catch (Exception ex) { preset.Name = previous; ProfileLibraryStatusText.Text = ex.Message; return false; }
        RefreshPresetControls(preset); ProfileLibraryStatusText.Text = "Profile renamed"; return true;
    }
    private string UniqueProfileName(string name)
    {
        string basis = string.IsNullOrWhiteSpace(name) ? "Imported profile" : name.Trim();
        if (basis.Length > 50) basis = basis[..50];
        string result = basis;
        for (int number = 2; _presets.Any(p => p.Name.Equals(result, StringComparison.OrdinalIgnoreCase)); number++) result = $"{basis} ({number})";
        return result;
    }
    internal SetupPreset? DuplicateProfile(string id)
    {
        var original = _presets.FirstOrDefault(p => p.Id == id); if (original is null) return null;
        var copy = JsonSerializer.Deserialize<SetupPreset>(JsonSerializer.Serialize(original))!;
        copy.Id = Guid.NewGuid().ToString("N"); copy.Name = UniqueProfileName(original.Name + " copy");
        copy.Shortcut = new(); copy.Automation.Enabled = false;
        _presets.Insert(_presets.IndexOf(original) + 1, copy);
        try { SettingsStorage.Save(PresetsPath, _presets); }
        catch (Exception ex) { _presets.Remove(copy); ProfileLibraryStatusText.Text = ex.Message; return null; }
        RefreshPresetControls(copy); ProfileLibraryStatusText.Text = "Saved profile duplicated · hotkey and automatic activation cleared";
        return copy;
    }
    internal bool MoveProfile(string id, int direction)
    {
        int index = _presets.FindIndex(p => p.Id == id), next = index + Math.Sign(direction);
        if (index < 0 || next < 0 || next >= _presets.Count) return false;
        (_presets[index], _presets[next]) = (_presets[next], _presets[index]);
        try { SettingsStorage.Save(PresetsPath, _presets); }
        catch (Exception ex) { (_presets[index], _presets[next]) = (_presets[next], _presets[index]); ProfileLibraryStatusText.Text = ex.Message; return false; }
        RefreshPresetControls(_presets[next]); ProfileLibraryStatusText.Text = "Order saved · used by cycling, tray lists and automation priority"; return true;
    }
    private void RenameProfile_OnClick(object sender, RoutedEventArgs e) { if (PresetBox.SelectedItem is SetupPreset p) RenameProfile(p.Id, ProfileRenameBox.Text); }
    private void DuplicateProfile_OnClick(object sender, RoutedEventArgs e) { if (PresetBox.SelectedItem is SetupPreset p) DuplicateProfile(p.Id); }
    private void MoveProfile_OnClick(object sender, RoutedEventArgs e) { if (PresetBox.SelectedItem is SetupPreset p) MoveProfile(p.Id, ((FrameworkElement)sender).Tag as string == "Up" ? -1 : 1); }
    private void ExportProfile_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_desktopIntegrationEnabled || PresetBox.SelectedItem is not SetupPreset preset) return;
        if (preset.Id == _applicationSettings.ActivePresetId && !ConfirmUnsaved()) return;
        var dialog = new SaveFileDialog { Title = "Export saved profile with images", Filter = "Ambient Monitor profile|*.ambientprofile", FileName = string.Concat(preset.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".ambientprofile" };
        if (RunSettingsDialog(() => dialog.ShowDialog(this)) != true) return;
        try { ProfilePackage.Export(preset, dialog.FileName); ProfileLibraryStatusText.Text = "Profile and referenced images exported"; }
        catch (Exception ex) { ProfileLibraryStatusText.Text = $"Export failed: {ex.Message}"; }
    }
    internal SetupPreset? ImportProfile(string path)
    {
        ImportedProfile? imported = null;
        try
        {
            imported = ProfilePackage.Import(path, Path.Combine(Path.GetDirectoryName(_settingsPath)!, "assets"));
            imported.Preset.Name = UniqueProfileName(imported.Preset.Name);
            _presets.Add(imported.Preset);
            try { SettingsStorage.Save(PresetsPath, _presets); }
            catch { _presets.Remove(imported.Preset); throw; }
            RefreshPresetControls(imported.Preset); RestorePresetShortcuts();
            ProfileLibraryStatusText.Text = "Imported with managed images · choose Load to activate; hotkeys/triggers can be reassigned";
            return imported.Preset;
        }
        catch (Exception ex)
        {
            if (imported is not null) imported.RemoveAssets();
            ProfileLibraryStatusText.Text = $"Import failed: {ex.Message}"; return null;
        }
    }
    private void ImportProfile_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_desktopIntegrationEnabled) return;
        var dialog = new OpenFileDialog { Title = "Import portable profile", Filter = "Ambient Monitor profile|*.ambientprofile" };
        if (RunSettingsDialog(() => dialog.ShowDialog(this)) == true) ImportProfile(dialog.FileName);
    }
}
