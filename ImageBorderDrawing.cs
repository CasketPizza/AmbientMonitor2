using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;

namespace AmbientMonitor;

internal static class ImageBorderDrawing
{
    public static BitmapSource Load(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var image = decoder.Frames[0];
        if (image.PixelWidth > 16384 || image.PixelHeight > 16384)
            throw new InvalidDataException("Choose an image no larger than 16384 pixels per side.");
        image.Freeze();
        return image;
    }

    public static DrawingGroup Create(BitmapSource source, OverlaySettings settings, double targetWidth, double targetHeight)
    {
        if (settings.RepeatMode != SourceRepeatMode.None && (settings.RepeatX > 1 || settings.RepeatY > 1))
        {
            int nx = Math.Clamp(settings.RepeatX, 1, 16), ny = Math.Clamp(settings.RepeatY, 1, 16);
            var single = settings.Snapshot(); single.RepeatMode = SourceRepeatMode.None; single.GradientMode = CanvasGradientMode.None;
            var tile = Create(source, single, targetWidth / nx, targetHeight / ny);
            var repeated = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, targetWidth, targetHeight)) };
            using (var dc = repeated.Open())
            {
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, targetWidth, targetHeight));
                for (int row = 0; row < ny; row++) for (int column = 0; column < nx; column++)
                {
                    dc.PushTransform(new TranslateTransform(column * targetWidth / nx, row * targetHeight / ny));
                    dc.PushTransform(new ScaleTransform(settings.RepeatMode == SourceRepeatMode.Mirror && (column & 1) != 0 ? -1 : 1,
                        settings.RepeatMode == SourceRepeatMode.Mirror && (row & 1) != 0 ? -1 : 1, targetWidth / nx / 2, targetHeight / ny / 2));
                    dc.DrawDrawing(tile); dc.Pop(); dc.Pop();
                }
                CanvasEffects.DrawGradient(dc, settings, targetWidth, targetHeight);
            }
            repeated.Freeze(); return repeated;
        }
        var (left, right) = SourceFraming.Normalize(settings.CropLeft, settings.CropRight);
        var (top, bottom) = SourceFraming.Normalize(settings.CropTop, settings.CropBottom);
        double sourceWidth = source.PixelWidth, sourceHeight = source.PixelHeight;
        double width = sourceWidth * (1 - left - right), height = sourceHeight * (1 - top - bottom);
        double sx = targetWidth / width, sy = targetHeight / height;
        if (settings.BorderScale != BorderScaleMode.Stretch)
            sx = sy = settings.BorderScale == BorderScaleMode.Fill ? Math.Max(sx, sy) : Math.Min(sx, sy);
        double zoom = Math.Clamp(settings.ImageZoom, 0.05, 20);
        sx *= zoom; sy *= zoom;
        double drawnWidth = width * sx, drawnHeight = height * sy;
        double x = (targetWidth - drawnWidth) * Math.Clamp(settings.FocusX, 0, 1) + settings.ImageOffsetX;
        double y = (targetHeight - drawnHeight) * Math.Clamp(settings.FocusY, 0, 1) + settings.ImageOffsetY;
        var group = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, targetWidth, targetHeight)), Opacity = Math.Clamp(settings.Strength, 0, 1) };
        using (var dc = group.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, targetWidth, targetHeight));
            dc.PushClip(new RectangleGeometry(new Rect(x, y, drawnWidth, drawnHeight)));
            dc.PushTransform(new ScaleTransform(settings.FlipHorizontal ? -1 : 1, settings.FlipVertical ? -1 : 1,
                x + drawnWidth / 2, y + drawnHeight / 2));
            dc.DrawImage(source, new Rect(x - sourceWidth * left * sx, y - sourceHeight * top * sy, sourceWidth * sx, sourceHeight * sy));
            dc.Pop(); dc.Pop();
        }
        group.Freeze();
        if (settings.GradientMode == CanvasGradientMode.None) return group;
        var composite = new DrawingGroup { ClipGeometry = group.ClipGeometry };
        using (var dc = composite.Open()) { dc.DrawDrawing(group); CanvasEffects.DrawGradient(dc, settings, targetWidth, targetHeight); }
        composite.Freeze(); return composite;
    }
    internal static Rect Bounds(BitmapSource source, OverlaySettings settings, double width, double height)
    {
        var (left, right) = SourceFraming.Normalize(settings.CropLeft, settings.CropRight);
        var (top, bottom) = SourceFraming.Normalize(settings.CropTop, settings.CropBottom);
        double sourceWidth = source.PixelWidth * (1 - left - right), sourceHeight = source.PixelHeight * (1 - top - bottom);
        double sx = width / sourceWidth, sy = height / sourceHeight;
        if (settings.BorderScale != BorderScaleMode.Stretch) sx = sy = settings.BorderScale == BorderScaleMode.Fill ? Math.Max(sx, sy) : Math.Min(sx, sy);
        double drawnWidth = sourceWidth * sx * Math.Clamp(settings.ImageZoom, 0.05, 20), drawnHeight = sourceHeight * sy * Math.Clamp(settings.ImageZoom, 0.05, 20);
        return new Rect((width - drawnWidth) * Math.Clamp(settings.FocusX, 0, 1) + settings.ImageOffsetX, (height - drawnHeight) * Math.Clamp(settings.FocusY, 0, 1) + settings.ImageOffsetY, drawnWidth, drawnHeight);
    }
}
