using System.Configuration;
using System.Data;
using System.Windows;

namespace AmbientMonitor;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private RendererWorker? _worker;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        int workerIndex = Array.IndexOf(e.Args, "--renderer-worker");
        if (workerIndex >= 0)
        {
            if (workerIndex + 1 >= e.Args.Length) { Shutdown(1); return; }
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Resources.MergedDictionaries.Clear(); // workers need no control-panel theme/resources
            _worker = new RendererWorker(this, e.Args[workerIndex + 1], e.Args.Contains("--worker-offscreen-test"));
            _ = _worker.RunAsync();
            return;
        }
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { _worker?.Dispose(); base.OnExit(e); }
}

