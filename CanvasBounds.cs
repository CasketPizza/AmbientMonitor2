using System.Text.Json.Serialization;

namespace AmbientMonitor;

public sealed record CanvasBounds(int Left, int Top, int Width, int Height)
{
    [JsonIgnore] public bool IsValid => Width is >= 16 and <= 16384 && Height is >= 16 and <= 16384;
    [JsonIgnore] public System.Drawing.Rectangle Rectangle => new(Left, Top, Width, Height);
    public static CanvasBounds FromPoints(int startX, int startY, int endX, int endY) =>
        new(Math.Min(startX, endX), Math.Min(startY, endY), Math.Abs(endX - startX), Math.Abs(endY - startY));
}
