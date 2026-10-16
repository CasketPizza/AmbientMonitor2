using System.Text.Json.Serialization;

namespace AmbientMonitor;

public enum RenderingBackend { Gpu, Cpu }
public enum CanvasTargetMode { Displays, CustomBoxes }
public enum InterfaceTheme { System, Dark, Light }

public sealed class ApplicationSettings
{
    public RenderingBackend Backend { get; set; } = RenderingBackend.Gpu;
    public CanvasTargetMode TargetMode { get; set; }
    public bool CloseToTray { get; set; }
    public ShortcutDefinition Shortcut { get; set; } = new();
    public ShortcutDefinition NextPresetShortcut { get; set; } = new();
    public ShortcutDefinition PreviousPresetShortcut { get; set; } = new();
    public InterfaceTheme Theme { get; set; }
    public bool Acrylic { get; set; } = true;
    public double AcrylicTransparency { get; set; } = 0.7;
    public string? ActivePresetId { get; set; }
    public bool AutomationPaused { get; set; }
    public bool PreviewGuides { get; set; } = true;
    public bool PreviewSnap { get; set; } = true;
    public bool GpuHdrAutomatic { get; set; } = true;
}

public sealed class ShortcutDefinition
{
    public uint Modifiers { get; set; }
    public int VirtualKey { get; set; }
    [JsonIgnore] public bool IsConfigured => VirtualKey != 0;

    [JsonIgnore] public bool IsValid => VirtualKey == 0 || (Modifiers is > 0 and <= 15 && VirtualKey is > 0 and < 255 &&
        VirtualKey is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5));

    public override string ToString()
    {
        if (!IsConfigured) return "Not set";
        var parts = new List<string>();
        if ((Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((Modifiers & 1) != 0) parts.Add("Alt");
        if ((Modifiers & 4) != 0) parts.Add("Shift");
        if ((Modifiers & 8) != 0) parts.Add("Win");
        var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(VirtualKey);
        parts.Add(new System.Windows.Input.KeyConverter().ConvertToString(key) ?? key.ToString());
        return string.Join(" + ", parts);
    }
}
