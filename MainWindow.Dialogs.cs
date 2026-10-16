namespace AmbientMonitor;

public partial class MainWindow
{
    private int _settingsDialogDepth;
    private T RunSettingsDialog<T>(Func<T> show)
    {
        _settingsDialogDepth++;
        try { return show(); }
        finally { _settingsDialogDepth--; }
    }
}
