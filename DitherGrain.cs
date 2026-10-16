namespace AmbientMonitor;

internal static class DitherGrain
{
    internal static double Size(double pixels) => double.IsFinite(pixels) ? Math.Clamp(pixels, 1, 32) : 1;
    internal static uint Coordinate(int pixel, double size) => (uint)Math.Floor((pixel + 0.5) / Size(size));
}
