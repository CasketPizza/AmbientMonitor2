using System.Windows.Interop;
using System.Windows.Threading;

namespace AmbientMonitor;

public partial class MainWindow
{
    private HwndSource? _settingsWindowSource;
    private DispatcherTimer? _appearanceRefreshTimer;
    internal bool SettingsWindowMoving { get; private set; }
    internal int AppearanceRevision { get; private set; }

    private void WatchSettingsWindowMovement()
    {
        _settingsWindowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _settingsWindowSource?.AddHook(SettingsWindowMessage);
    }
    private nint SettingsWindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        switch (message)
        {
            case 0x0231: // WM_ENTERSIZEMOVE
                SettingsWindowMoving = true;
                break;
            case 0x0232: // WM_EXITSIZEMOVE
                SettingsWindowMoving = false;
                QueueAppearanceRefresh();
                break;
        }
        return 0; // Observe only; WPF still owns movement and DPI handling.
    }
    private void QueueAppearanceRefresh()
    {
        if (_closed || _workerCloseInProgress || Dispatcher.HasShutdownStarted) return;
        if (_appearanceRefreshTimer is null)
        {
            _appearanceRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
            _appearanceRefreshTimer.Tick += (_, _) =>
            {
                _appearanceRefreshTimer.Stop();
                if (_closed || _workerCloseInProgress) return;
                if (SettingsWindowMoving) { _appearanceRefreshTimer.Start(); return; }
                ApplyInterfaceAppearance();
            };
        }
        _appearanceRefreshTimer.Stop();
        _appearanceRefreshTimer.Start();
    }
    private void StopWatchingSettingsWindowMovement()
    {
        _appearanceRefreshTimer?.Stop();
        if (_settingsWindowSource is { IsDisposed: false }) _settingsWindowSource.RemoveHook(SettingsWindowMessage);
        _settingsWindowSource = null;
    }
}
