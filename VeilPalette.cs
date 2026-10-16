using System.Globalization;
using System.Numerics;

namespace AmbientMonitor;

internal static class VeilPalette
{
    public static bool TryParse(string? text, out uint rgb)
    {
        rgb = 0x10151F;
        return text is { Length: 7 } && text[0] == '#' && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
    }
    public static Vector4 Color(string? text)
    {
        if (!TryParse(text, out uint rgb)) rgb = 0x10151F;
        return new Vector4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
    }
}
