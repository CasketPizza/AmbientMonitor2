namespace AmbientMonitor;

internal sealed record LayerWindow(nint Handle, bool Topmost, bool Visible, bool DesktopHost = false);
internal sealed record LayerSnapshot(IReadOnlyList<LayerWindow> Windows, nint Foreground);
internal readonly record struct LayerPlacement(bool Topmost, nint InsertAfter, bool RestoreDesktop, string? Notice = null);

// Pure z-order policy: never changes another application's windows or rendering
// handles. Desktop positioning stays top-level to preserve DXGI and DPI behavior.
internal static class LayerPlacementPolicy
{
    public static string Describe(OverlayLayer layer) => layer switch
    {
        OverlayLayer.StayTopmost => "Reasserts the overlay above other topmost windows when window order changes.",
        OverlayLayer.Topmost => "Above normal application windows, but below other topmost windows and widgets.",
        OverlayLayer.Normal => "Normal window ordering. Other applications can move in front of the overlay.",
        OverlayLayer.Bottom => "Behind application windows, just above the desktop. Show desktop can cover this layer.",
        OverlayLayer.OnDesktop => "Desktop-level behind application windows, including after Show desktop. This opaque overlay can cover desktop icons beneath it.",
        _ => Describe(OverlayLayer.Topmost)
    };

    public static LayerPlacement Decide(OverlayLayer layer, LayerSnapshot snapshot, IReadOnlySet<nint> overlays)
    {
        var windows = snapshot.Windows.Where(w => w.Visible && !overlays.Contains(w.Handle)).ToList();
        if (layer == OverlayLayer.StayTopmost) return new(true, -1, false);
        if (layer == OverlayLayer.Topmost)
            // A style flag is not enough: desktop skins can retain TOPMOST while
            // sitting below ordinary applications. Only anchor within the front
            // contiguous topmost group, never to a low desktop/widget window.
            return new(true, windows.TakeWhile(w => w.Topmost).LastOrDefault()?.Handle ?? -1, false);
        if (layer == OverlayLayer.Normal) return new(false, 0, false);

        var desktop = windows.FirstOrDefault(w => w.DesktopHost);
        if (desktop is null)
            return new(false, 1, false, layer == OverlayLayer.OnDesktop ? "Desktop host unavailable; using Bottom until Explorer returns." : null);
        if (layer == OverlayLayer.Bottom && (snapshot.Foreground == desktop.Handle || desktop.Topmost))
            return new(false, 1, false);

        int index = windows.IndexOf(desktop);
        var beforeDesktop = windows.Take(index).LastOrDefault(w => w.Topmost == desktop.Topmost);
        return new(desktop.Topmost, beforeDesktop?.Handle ?? (desktop.Topmost ? -1 : 0),
            layer == OverlayLayer.OnDesktop && snapshot.Foreground == desktop.Handle);
    }
}
