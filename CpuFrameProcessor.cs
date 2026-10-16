using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace AmbientMonitor;

// CPU capture/effects path; WPF/Windows still composite the final image on screen.
// Working buffers are reused, and sliding-window blur is linear in pixel count.
internal sealed class CpuFrameProcessor : IDisposable
{
    private Bitmap? _working;
    private byte[] _pixels = [], _temporary = [];
    private Bitmap? _composite;
    private byte[] _compositePixels = [];

    public BitmapSource Process(Bitmap? source, OverlaySettings settings, int targetWidth, int targetHeight, uint noiseFrame)
    {
        var framing = SourceFraming.Calculate(source?.Width ?? targetWidth, source?.Height ?? targetHeight, targetWidth, targetHeight, settings);
        double cropWidth = framing.View.Width, cropHeight = framing.View.Height;
        double radius = Math.Clamp(settings.Blur, 0, 1000) * 0.18 / 0.35;
        double support = settings.BlurType == BlurType.Gaussian ? radius * Math.Sqrt(3) : radius;
        double scale = Math.Clamp(settings.CaptureScalePercent, 10, 100) / 100d;
        if (support > 0) scale = Math.Min(scale, 16 / support);
        int width = settings.Mode == OverlayMode.Veil ? 128 : Math.Max(1, (int)Math.Round(cropWidth * scale));
        int height = settings.Mode == OverlayMode.Veil ? 128 : Math.Max(1, (int)Math.Round(cropHeight * scale));
        if (settings.RepeatMode != SourceRepeatMode.None)
        {
            width = Math.Min(targetWidth, width * Math.Clamp(settings.RepeatX, 1, 16));
            height = Math.Min(targetHeight, height * Math.Clamp(settings.RepeatY, 1, 16));
        }
        EnsureBuffers(width, height);

        if (settings.Mode == OverlayMode.Ambient && source is not null)
        {
            double left = framing.View.Left, top = framing.View.Top;
            using (var graphics = Graphics.FromImage(_working!))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.TranslateTransform(settings.FlipHorizontal ? width : 0, settings.FlipVertical ? height : 0);
                graphics.ScaleTransform(settings.FlipHorizontal ? -1 : 1, settings.FlipVertical ? -1 : 1);
                int repeatX = settings.RepeatMode == SourceRepeatMode.None ? 1 : Math.Clamp(settings.RepeatX, 1, 16);
                int repeatY = settings.RepeatMode == SourceRepeatMode.None ? 1 : Math.Clamp(settings.RepeatY, 1, 16);
                for (int ty = 0; ty < repeatY; ty++) for (int tx = 0; tx < repeatX; tx++)
                {
                    var state = graphics.Save();
                    float cellWidth = width / (float)repeatX, cellHeight = height / (float)repeatY;
                    graphics.TranslateTransform(tx * cellWidth, ty * cellHeight);
                    bool mirrorX = settings.RepeatMode == SourceRepeatMode.Mirror && (tx & 1) != 0, mirrorY = settings.RepeatMode == SourceRepeatMode.Mirror && (ty & 1) != 0;
                    if (mirrorX || mirrorY) { graphics.TranslateTransform(mirrorX ? cellWidth : 0, mirrorY ? cellHeight : 0); graphics.ScaleTransform(mirrorX ? -1 : 1, mirrorY ? -1 : 1); }
                    graphics.DrawImage(source, new RectangleF(0, 0, cellWidth, cellHeight),
                        new RectangleF((float)left, (float)top, (float)cropWidth, (float)cropHeight), GraphicsUnit.Pixel);
                    graphics.Restore(state);
                }
            }
            var data = _working!.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { Marshal.Copy(data.Scan0, _pixels, 0, _pixels.Length); }
            finally { _working!.UnlockBits(data); }
            if (framing.Mask)
                for (int i = 0; i < _pixels.Length; i += 4)
                    if (!framing.IsRetained((i / 4 % width + 0.5) / width, (i / 4 / width + 0.5) / height, settings))
                        _pixels[i] = _pixels[i + 1] = _pixels[i + 2] = 0;
            int passes = settings.BlurType == BlurType.Gaussian ? 3 : 1;
            int passRadius = (int)Math.Round(radius * scale / (passes == 3 ? Math.Sqrt(3) : 1));
            if (passRadius > 0)
                for (int pass = 0; pass < passes; pass++) Blur(width, height, passRadius);
        }
        else Array.Clear(_pixels);

        // Noise is a final output effect, not part of the low-resolution blur
        // image. Composite at canvas resolution so 1 px really is fine grain
        // in CPU mode as well as GPU mode (including the small veil buffer).
        byte[] pixels = _pixels;
        if (settings.NoiseEnabled && settings.Noise > 0 && (width != targetWidth || height != targetHeight))
        {
            var sourceData = _working!.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { Marshal.Copy(_pixels, 0, sourceData.Scan0, _pixels.Length); }
            finally { _working.UnlockBits(sourceData); }
            if (_composite?.Width != targetWidth || _composite.Height != targetHeight)
            {
                _composite?.Dispose(); _composite = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppPArgb);
                _compositePixels = new byte[targetWidth * targetHeight * 4];
            }
            using (var graphics = Graphics.FromImage(_composite))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(_working, new Rectangle(0, 0, targetWidth, targetHeight), new Rectangle(0, 0, width, height), GraphicsUnit.Pixel);
            }
            var outputData = _composite.LockBits(new Rectangle(0, 0, targetWidth, targetHeight), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { Marshal.Copy(outputData.Scan0, _compositePixels, 0, _compositePixels.Length); }
            finally { _composite.UnlockBits(outputData); }
            width = targetWidth; height = targetHeight; pixels = _compositePixels;
        }
        float strength = (float)Math.Clamp(settings.Strength, 0, 1);
        float gain = (float)Math.Clamp(settings.Brightness, 0.2, 1.5) * strength * 0.94f;
        float noise = settings.NoiseEnabled ? (float)Math.Clamp(settings.Noise, 0, 0.3) : 0;
        bool veil = settings.Mode == OverlayMode.Veil;
        var veilColor = VeilPalette.Color(settings.VeilColor);
        var gradientStartColor = VeilPalette.Color(settings.GradientStartColor);
        var gradientEndColor = VeilPalette.Color(settings.GradientColor);
        double grainSize = DitherGrain.Size(settings.NoiseGrainPixels);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (!veil && framing.Mask && !framing.IsRetained((i / 4 % width + 0.5) / width, (i / 4 / width + 0.5) / height, settings))
            {
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
                pixels[i + 3] = 255;
                // Continue through an explicitly enabled overlay gradient below.
                if (settings.GradientMode == CanvasGradientMode.None) continue;
            }
            float grain = noise > 0 ? Noise(DitherGrain.Coordinate(i / 4 % width, grainSize), DitherGrain.Coordinate(i / 4 / width, grainSize), noiseFrame) * 255 : 0;
            for (int c = 0; c < 3; c++)
            {
                float tint = c == 0 ? 31 : c == 1 ? 21 : 16;
                float veilChannel = c == 0 ? veilColor.Z : c == 1 ? veilColor.Y : veilColor.X;
                float value = veil ? veilChannel * 255 * strength : pixels[i + c] * gain + tint * 0.06f;
                pixels[i + c] = (byte)Math.Clamp(Math.Round(value * (1 - noise) + grain * noise), 0, 255);
            }
            pixels[i + 3] = 255;
            if (!veil && framing.Mask && !framing.IsRetained((i / 4 % width + 0.5) / width, (i / 4 / width + 0.5) / height, settings))
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
            var gradientSample = CanvasEffects.GradientSample(settings, (i / 4 % width + 0.5) / width, (i / 4 / width + 0.5) / height);
            double gradient = gradientSample.Alpha;
            if (gradient > 0)
            {
                var tint = System.Numerics.Vector4.Lerp(gradientStartColor, gradientEndColor, (float)gradientSample.Mix);
                pixels[i] = (byte)Math.Round(pixels[i] * (1 - gradient) + tint.Z * 255 * gradient);
                pixels[i + 1] = (byte)Math.Round(pixels[i + 1] * (1 - gradient) + tint.Y * 255 * gradient);
                pixels[i + 2] = (byte)Math.Round(pixels[i + 2] * (1 - gradient) + tint.X * 255 * gradient);
            }
        }
        var frame = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        frame.Freeze();
        return frame;
    }

    private void EnsureBuffers(int width, int height)
    {
        if (_working?.Width == width && _working.Height == height) return;
        _working?.Dispose();
        _working = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        _pixels = new byte[width * height * 4];
        _temporary = new byte[_pixels.Length];
    }

    private void Blur(int width, int height, int radius)
    {
        int diameter = radius * 2 + 1;
        for (int y = 0; y < height; y++)
            for (int c = 0; c < 3; c++)
            {
                int row = y * width * 4, sum = 0;
                for (int x = -radius; x <= radius; x++) sum += _pixels[row + Math.Clamp(x, 0, width - 1) * 4 + c];
                for (int x = 0; x < width; x++)
                {
                    _temporary[row + x * 4 + c] = (byte)(sum / diameter);
                    sum += _pixels[row + Math.Min(width - 1, x + radius + 1) * 4 + c] - _pixels[row + Math.Max(0, x - radius) * 4 + c];
                }
            }
        for (int x = 0; x < width; x++)
            for (int c = 0; c < 3; c++)
            {
                int column = x * 4 + c, sum = 0;
                for (int y = -radius; y <= radius; y++) sum += _temporary[Math.Clamp(y, 0, height - 1) * width * 4 + column];
                for (int y = 0; y < height; y++)
                {
                    _pixels[y * width * 4 + column] = (byte)(sum / diameter);
                    sum += _temporary[Math.Min(height - 1, y + radius + 1) * width * 4 + column] - _temporary[Math.Max(0, y - radius) * width * 4 + column];
                }
            }
    }

    private static float Noise(uint x, uint y, uint frame)
    {
        uint hash = x * 1973 + y * 9277 + frame * 26699 + 911;
        hash = (hash ^ (hash >> 16)) * 2246822519;
        hash = (hash ^ (hash >> 13)) * 3266489917;
        hash ^= hash >> 16;
        return (hash & 65535) / 65535f;
    }

    public void Dispose() { _working?.Dispose(); _working = null; _composite?.Dispose(); _composite = null; }
}
