using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using System.Numerics;

namespace AmbientMonitor;

internal static class CanvasEffects
{
    internal static double RepeatCoordinate(double value, int count, SourceRepeatMode mode)
    {
        if (mode == SourceRepeatMode.None) return value;
        double position = Math.Clamp(value, 0, 0.999999999) * Math.Clamp(count, 1, 16);
        int tile = (int)Math.Floor(position); double fraction = position - tile;
        return mode == SourceRepeatMode.Mirror && (tile & 1) != 0 ? 1 - fraction : fraction;
    }
    internal static double GradientAlpha(OverlaySettings settings, double u, double v)
        => GradientSample(settings, u, v).Alpha;
    internal static (double Start, double End) GradientStops(OverlaySettings settings)
    {
        double start = Math.Clamp(settings.GradientStart, 0, 0.99);
        return (start, Math.Clamp(settings.GradientEnd, start + 0.01, 1));
    }
    internal static double Curve(double value, double falloff)
    {
        double t = Math.Clamp(value, 0, 1);
        return Math.Pow(t * t * (3 - 2 * t), Math.Clamp(falloff, 0.25, 4));
    }
    internal static (double Mix, double Alpha) GradientSample(OverlaySettings settings, double u, double v)
    {
        if (!settings.GradientEnabled || settings.GradientMode == CanvasGradientMode.None) return (0, 0);
        double position;
        if (settings.GradientMode == CanvasGradientMode.EdgeFade && settings.GradientIndependentEdges)
        {
            double strength = 0;
            double Edge(double distance, double width, double opacity) => width <= 0 ? 0 : Curve(1 - distance / Math.Clamp(width, 0.001, 0.5), settings.GradientFalloff) * Math.Clamp(opacity, 0, 1);
            if ((settings.GradientEdges & 1) != 0) strength = Math.Max(strength, Edge(u, settings.GradientLeftWidth, settings.GradientLeftOpacity));
            if ((settings.GradientEdges & 2) != 0) strength = Math.Max(strength, Edge(1 - u, settings.GradientRightWidth, settings.GradientRightOpacity));
            if ((settings.GradientEdges & 4) != 0) strength = Math.Max(strength, Edge(v, settings.GradientTopWidth, settings.GradientTopOpacity));
            if ((settings.GradientEdges & 8) != 0) strength = Math.Max(strength, Edge(1 - v, settings.GradientBottomWidth, settings.GradientBottomOpacity));
            return (1, strength * Math.Clamp(settings.GradientOpacity, 0, 1) * Math.Clamp(settings.GradientIntensity, 0, 1));
        }
        if (settings.GradientMode == CanvasGradientMode.Linear)
        {
            double radians = settings.GradientAngle * Math.PI / 180, x = Math.Cos(radians), y = Math.Sin(radians);
            position = 0.5 + ((u - 0.5) * x + (v - 0.5) * y) / (Math.Abs(x) + Math.Abs(y));
        }
        else
        {
            position = 0;
            if ((settings.GradientEdges & 1) != 0) position = Math.Max(position, 1 - 2 * u);
            if ((settings.GradientEdges & 2) != 0) position = Math.Max(position, 2 * u - 1);
            if ((settings.GradientEdges & 4) != 0) position = Math.Max(position, 1 - 2 * v);
            if ((settings.GradientEdges & 8) != 0) position = Math.Max(position, 2 * v - 1);
        }
        var (start, end) = GradientStops(settings);
        double t = Curve((position - start) / (end - start), settings.GradientFalloff);
        double initial = settings.GradientMode == CanvasGradientMode.Linear ? Math.Clamp(settings.GradientStartOpacity, 0, 1) : 0;
        return (settings.GradientMode == CanvasGradientMode.Linear ? t : 1,
            (initial * (1 - t) + Math.Clamp(settings.GradientOpacity, 0, 1) * t) * Math.Clamp(settings.GradientIntensity, 0, 1));
    }
    internal static void DrawGradient(DrawingContext dc, OverlaySettings settings, double width, double height)
    {
        if (!settings.GradientEnabled || settings.GradientMode == CanvasGradientMode.None) return;
        var start = VeilPalette.Color(settings.GradientStartColor); var end = VeilPalette.Color(settings.GradientColor);
        // Premultiplied overlay pixels share the CPU/shader fade formula and
        // preserve transparent centers on image borders.
        const int size = 128; var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            int i = (y * size + x) * 4;
            var sample = GradientSample(settings, x / (double)(size - 1), y / (double)(size - 1));
            var color = Vector4.Lerp(start, end, (float)sample.Mix); double alpha = sample.Alpha * 255;
            pixels[i] = (byte)Math.Round(color.Z * alpha); pixels[i + 1] = (byte)Math.Round(color.Y * alpha); pixels[i + 2] = (byte)Math.Round(color.X * alpha); pixels[i + 3] = (byte)Math.Round(alpha);
        }
        var mask = System.Windows.Media.Imaging.BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4); mask.Freeze();
        dc.DrawImage(mask, new Rect(0, 0, width, height));
    }
}
