using System.Windows.Interop;
using System.Windows.Media;

namespace AmbientMonitor;

internal static class SettingsWindowRendering
{
    internal static void Configure()
    {
        // The control panel must not migrate WPF hardware render targets between
        // adapters during monitor/DPI changes. Overlay workers are separate
        // processes and retain their selected GPU/CPU renderer and composition.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
    }
}
