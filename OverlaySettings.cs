using System.ComponentModel;
using System.Text.Json.Serialization;

namespace AmbientMonitor;
public enum OverlayMode { Ambient, Veil, ImageBorder }
public enum BorderScaleMode { Fit, Fill, Stretch }
public enum ImageScaleMode { Fill, Fit }
public enum BlurType { Box, Gaussian }
public enum SourceRepeatMode { None, Repeat, Mirror }
public enum CanvasGradientMode { None, Linear, EdgeFade }
public enum OverlayLayer { StayTopmost, Topmost, Normal, Bottom, OnDesktop }
public class OverlaySettings
{
    internal OverlaySettings Snapshot()
    {
        var snapshot = new OverlaySettings { Enabled = Enabled };
        snapshot.CopyDisplaySettingsFrom(this);
        return snapshot;
    }
    internal ImageSettingsKey ImageKey => new(Mode, Blur, BlurType, FlipHorizontal, FlipVertical,
        ScaleMode, CropZoom, FocusX, FocusY, CaptureScalePercent, CropLeft, CropRight, CropTop, CropBottom, RepeatMode, RepeatX, RepeatY);
    internal bool HasDifferentImageSettings(OverlaySettings other) =>
        ImageKey != other.ImageKey;
    public bool Enabled { get; set; } = true;
    public OverlayMode Mode { get; set; } = OverlayMode.Ambient;
    public bool SecondaryOnly { get; set; } = true;
    public double Strength { get; set; } = 0.72;
    public double Blur { get; set; } = 18;
    public BlurType BlurType { get; set; } = BlurType.Gaussian;
    public double Brightness { get; set; } = 0.8;
    public double Noise { get; set; } = 0.04;
    public bool NoiseEnabled { get; set; } = true;
    public double NoiseGrainPixels { get; set; } = 1;
    public bool ClickThrough { get; set; } = true;
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    public ImageScaleMode ScaleMode { get; set; } = ImageScaleMode.Fill;
    public double CropZoom { get; set; } = 1;
    public double FocusX { get; set; } = 0.5;
    public double FocusY { get; set; } = 0.5;
    public int CaptureScalePercent { get; set; } = 35;
    public OverlayLayer Layer { get; set; } = OverlayLayer.Topmost;
    public double CropLeft { get; set; }
    public double CropRight { get; set; }
    public double CropTop { get; set; }
    public double CropBottom { get; set; }
    public string VeilColor { get; set; } = "#10151F";
    public string ImagePath { get; set; } = "";
    public BorderScaleMode BorderScale { get; set; } = BorderScaleMode.Fit;
    public double ImageZoom { get; set; } = 1;
    public double ImageOffsetX { get; set; }
    public double ImageOffsetY { get; set; }
    public SourceRepeatMode RepeatMode { get; set; }
    public int RepeatX { get; set; } = 1;
    public int RepeatY { get; set; } = 1;
    public CanvasGradientMode GradientMode { get; set; }
    public string GradientColor { get; set; } = "#000000";
    public double GradientOpacity { get; set; } = 0.8;
    public double GradientAngle { get; set; } = 90;
    public double GradientStart { get; set; } = 0.5;
    public int GradientEdges { get; set; } = 15;
    public bool GradientEnabled { get; set; } = true;
    public double GradientIntensity { get; set; } = 1;
    public string GradientStartColor { get; set; } = "#000000";
    public double GradientStartOpacity { get; set; }
    public double GradientEnd { get; set; } = 1;
    public double GradientFalloff { get; set; } = 1;
    public bool GradientIndependentEdges { get; set; }
    public double GradientLeftWidth { get; set; } = 0.25;
    public double GradientRightWidth { get; set; } = 0.25;
    public double GradientTopWidth { get; set; } = 0.25;
    public double GradientBottomWidth { get; set; } = 0.25;
    public double GradientLeftOpacity { get; set; } = 1;
    public double GradientRightOpacity { get; set; } = 1;
    public double GradientTopOpacity { get; set; } = 1;
    public double GradientBottomOpacity { get; set; } = 1;

    internal void CopyDisplaySettingsFrom(OverlaySettings source)
    {
        Mode = source.Mode;
        SecondaryOnly = source.SecondaryOnly;
        Strength = source.Strength;
        Blur = source.Blur;
        BlurType = source.BlurType;
        Brightness = source.Brightness;
        Noise = source.Noise;
        NoiseEnabled = source.NoiseEnabled;
        NoiseGrainPixels = source.NoiseGrainPixels;
        ClickThrough = source.ClickThrough;
        FlipHorizontal = source.FlipHorizontal;
        FlipVertical = source.FlipVertical;
        ScaleMode = source.ScaleMode;
        CropZoom = source.CropZoom;
        FocusX = source.FocusX;
        FocusY = source.FocusY;
        CaptureScalePercent = source.CaptureScalePercent;
        Layer = source.Layer;
        CropLeft = source.CropLeft; CropRight = source.CropRight;
        CropTop = source.CropTop; CropBottom = source.CropBottom;
        VeilColor = source.VeilColor;
        ImagePath = source.ImagePath;
        BorderScale = source.BorderScale;
        ImageZoom = source.ImageZoom;
        ImageOffsetX = source.ImageOffsetX; ImageOffsetY = source.ImageOffsetY;
        RepeatMode = source.RepeatMode; RepeatX = source.RepeatX; RepeatY = source.RepeatY;
        GradientMode = source.GradientMode; GradientColor = source.GradientColor; GradientOpacity = source.GradientOpacity;
        GradientAngle = source.GradientAngle; GradientStart = source.GradientStart; GradientEdges = source.GradientEdges;
        GradientEnabled = source.GradientEnabled; GradientIntensity = source.GradientIntensity; GradientStartColor = source.GradientStartColor;
        GradientStartOpacity = source.GradientStartOpacity; GradientEnd = source.GradientEnd; GradientFalloff = source.GradientFalloff;
        GradientIndependentEdges = source.GradientIndependentEdges;
        GradientLeftWidth = source.GradientLeftWidth; GradientRightWidth = source.GradientRightWidth; GradientTopWidth = source.GradientTopWidth; GradientBottomWidth = source.GradientBottomWidth;
        GradientLeftOpacity = source.GradientLeftOpacity; GradientRightOpacity = source.GradientRightOpacity; GradientTopOpacity = source.GradientTopOpacity; GradientBottomOpacity = source.GradientBottomOpacity;
    }
}

internal readonly record struct ImageSettingsKey(OverlayMode Mode, double Blur, BlurType BlurType,
    bool FlipHorizontal, bool FlipVertical, ImageScaleMode ScaleMode, double CropZoom,
    double FocusX, double FocusY, int CaptureScalePercent, double CropLeft, double CropRight, double CropTop, double CropBottom,
    SourceRepeatMode RepeatMode, int RepeatX, int RepeatY);

public sealed class DisplayProfile : OverlaySettings, INotifyPropertyChanged
{
    private string _nickname = "";
    public string DeviceName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public CanvasBounds? Canvas { get; set; }
    public string Nickname
    {
        get => _nickname;
        set
        {
            var name = (value ?? "").Trim();
            if (name.Length > 40) name = name[..40];
            if (_nickname == name) return;
            _nickname = name;
            foreach (var property in new[] { nameof(Nickname), nameof(TabTitle), nameof(TabSubtitle), nameof(HasTabSubtitle) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
    [JsonIgnore] public bool IsPrimary { get; set; }
    [JsonIgnore] public string OriginalDisplayLabel => DeviceName.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase)
        ? DeviceName.Replace(@"\\.\DISPLAY", "Display ", StringComparison.OrdinalIgnoreCase) : DisplayName;
    [JsonIgnore] public string TabTitle => Nickname.Length > 0 ? Nickname : OriginalDisplayLabel;
    [JsonIgnore] public string TabSubtitle => Nickname.Length > 0 ? OriginalDisplayLabel : Canvas is not null ? $"{Canvas.Width} × {Canvas.Height}" : IsPrimary ? "Primary · capture source" : "";
    [JsonIgnore] public bool HasTabSubtitle => TabSubtitle.Length > 0;
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => TabTitle;
}
