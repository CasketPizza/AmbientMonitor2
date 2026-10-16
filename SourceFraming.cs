namespace AmbientMonitor;

internal readonly record struct SourceRectangle(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

internal readonly record struct FramingLayout(SourceRectangle View, SourceRectangle Retained, bool Mask)
{
    public bool IsRetained(double u, double v, OverlaySettings settings)
    {
        if (!Mask) return true;
        if (settings.FlipHorizontal) u = 1 - u;
        if (settings.FlipVertical) v = 1 - v;
        double x = View.Left + u * View.Width, y = View.Top + v * View.Height;
        return x >= Retained.Left && x < Retained.Right && y >= Retained.Top && y < Retained.Bottom;
    }
}

internal static class SourceFraming
{
    public static FramingLayout Calculate(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, OverlaySettings settings)
    {
        var (left, right) = Normalize(settings.CropLeft, settings.CropRight);
        var (top, bottom) = Normalize(settings.CropTop, settings.CropBottom);
        var retained = new SourceRectangle(sourceWidth * left, sourceHeight * top,
            sourceWidth * (1 - left - right), sourceHeight * (1 - top - bottom));
        bool repeating = settings.RepeatMode != SourceRepeatMode.None;
        bool mask = !repeating && settings.ScaleMode == ImageScaleMode.Fill && (left + right + top + bottom > 0);
        var domain = repeating || settings.ScaleMode == ImageScaleMode.Fit ? retained : new SourceRectangle(0, 0, sourceWidth, sourceHeight);
        double width = domain.Width, height = domain.Height;
        if (!repeating && settings.ScaleMode == ImageScaleMode.Fill)
        {
            double aspect = targetWidth / (double)targetHeight;
            if (width / height > aspect) width = height * aspect;
            else height = width / aspect;
        }
        double zoom = Math.Clamp(settings.CropZoom, 1, 20);
        width /= zoom; height /= zoom;
        // Clamp offsets before adding the origin: subtracting Width from Right can
        // round below Left when a decimal crop leaves no travel at 1x zoom.
        double x = domain.Left + Math.Clamp(Math.Clamp(settings.FocusX, 0, 1) * domain.Width - width / 2, 0, Math.Max(0, domain.Width - width));
        double y = domain.Top + Math.Clamp(Math.Clamp(settings.FocusY, 0, 1) * domain.Height - height / 2, 0, Math.Max(0, domain.Height - height));
        return new FramingLayout(new SourceRectangle(x, y, width, height), retained, mask);
    }
    public static (double First, double Second) Normalize(double first, double second)
    {
        first = double.IsFinite(first) ? Math.Clamp(first, 0, 0.99) : 0;
        second = double.IsFinite(second) ? Math.Clamp(second, 0, 0.99) : 0;
        if (first + second > 0.99) { double factor = 0.99 / (first + second); first *= factor; second *= factor; }
        return (first, second);
    }
}
