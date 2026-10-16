using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AmbientMonitor;

public partial class MainWindow
{
    private void RefreshQuickProfileStatus()
    {
        QuickProfileName.Text = ActivePresetText.Text;
        PauseAutomationButton.Content = _applicationSettings.AutomationPaused ? "Resume auto" : "Pause auto";
        QuickAutomationBadge.Visibility = _automationSession is null ? Visibility.Collapsed : Visibility.Visible;
        bool paused = _automationSession is not null && !_overlayRunning && HasUnsavedChanges;
        QuickAutomationBadge.Text = paused ? " · paused" : " · auto";
        QuickProfileButton.ToolTip = _automationSession is null ? $"Choose a profile · {ActivePresetText.Text}" : paused ? "Save profile edits to restore the previous setup" : $"Automatically enabled · {ActivePresetText.Text}";
    }
    internal ContextMenu BuildQuickProfilesMenu()
    {
        var menu = new ContextMenu();
        var search = new System.Windows.Controls.TextBox { MinWidth = 240, ToolTip = "Search profiles", Margin = new Thickness(4) };
        System.Windows.Automation.AutomationProperties.SetName(search, "Search quick profile list");
        var searchHeader = new StackPanel(); searchHeader.Children.Add(new TextBlock { Text = "Search profiles", FontSize = 11, Margin = new Thickness(4, 0, 4, 2) }); searchHeader.Children.Add(search);
        menu.Items.Add(new MenuItem { Header = searchHeader, StaysOpenOnClick = true, Focusable = false });
        void Populate()
        {
            while (menu.Items.Count > 1) menu.Items.RemoveAt(1);
            var matches = SearchProfiles(search.Text);
            foreach (var preset in matches)
            {
                string id = preset.Id;
                var item = new MenuItem { Header = preset.Name.Replace("_", "__"), IsCheckable = true, IsChecked = id == _applicationSettings.ActivePresetId,
                    InputGestureText = preset.Shortcut.IsConfigured ? preset.Shortcut.ToString() : "" };
                item.Click += async (_, _) => await LoadPresetAsync(id);
                menu.Items.Add(item);
            }
            if (matches.Count == 0) menu.Items.Add(new MenuItem { Header = "No matching profiles", IsEnabled = false });
            menu.Items.Add(new Separator());
            var manage = new MenuItem { Header = "Manage profiles, hotkeys & game triggers…" };
            manage.Click += (_, _) => ManagePresets_OnClick(this, new RoutedEventArgs()); menu.Items.Add(manage);
        }
        search.TextChanged += (_, _) => Populate(); Populate();
        search.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { menu.IsOpen = false; e.Handled = true; }
            else if (e.Key == Key.Enter && SearchProfiles(search.Text).FirstOrDefault() is { } first) { e.Handled = true; menu.IsOpen = false; await LoadPresetAsync(first.Id); }
        };
        return menu;
    }
    private void QuickProfiles_OnClick(object sender, RoutedEventArgs e)
    {
        CancelShortcutRecording();
        var menu = BuildQuickProfilesMenu();
        menu.PlacementTarget = QuickProfileButton; menu.Placement = PlacementMode.Bottom;
        QuickProfileButton.ContextMenu = menu; menu.IsOpen = true;
    }
    private void QuickSaveAs_OnClick(object sender, RoutedEventArgs e)
    {
        CancelShortcutRecording(); QuickPresetNameBox.Text = "";
        QuickSaveAsPopup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() => QuickPresetNameBox.Focus()));
    }
    private void SaveQuickPreset_OnClick(object sender, RoutedEventArgs e)
    {
        if (SavePresetAs(QuickPresetNameBox.Text)) QuickSaveAsPopup.IsOpen = false;
    }
    private void QuickPresetName_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SaveQuickPreset_OnClick(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { QuickSaveAsPopup.IsOpen = false; e.Handled = true; }
    }
    private void ManagePresets_OnClick(object sender, RoutedEventArgs e)
    {
        ApplicationNav.IsChecked = true;
        ProfileManagementCard.BringIntoView();
    }
}
