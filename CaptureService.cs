using System.Windows.Forms;

namespace AmbientMonitor;

public static class CaptureService
{
    public static IEnumerable<Screen> GetScreens() => Screen.AllScreens;
}
