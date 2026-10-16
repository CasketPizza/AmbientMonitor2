using Vortice.DXGI;

namespace AmbientMonitor;

internal static class HdrColor
{
    internal static readonly ColorSpaceType ScRgb = (ColorSpaceType)1; // RGB_FULL_G10_NONE_P709
    internal static bool IsHdr(IDXGIOutput output)
    {
        try
        {
            using var advanced = output.QueryInterfaceOrNull<IDXGIOutput6>();
            return advanced is not null && (int)advanced.Description1.ColorSpace == 12; // RGB_FULL_G2084_NONE_P2020
        }
        catch (Exception ex) when (HdrCompatibility.IsUnsupported(ex)) { return false; }
    }
    internal static double ToLinear(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    internal static double ToSrgb(double value) => value <= 0.0031308 ? value * 12.92 : 1.055 * Math.Pow(Math.Max(value, 0), 1 / 2.4) - 0.055;
    internal static double ToneMap(double value) { double normalized = Math.Max(0, value) / 2.5375; return normalized / (1 + normalized); }
}
