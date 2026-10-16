namespace AmbientMonitor;

internal interface IOverlayWindow
{
    string DeviceName { get; }
    nint LayerHandle { get; }
    void Show();
    void Close();
    void UpdateSettings(OverlaySettings settings);
}
