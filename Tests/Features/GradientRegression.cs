using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AmbientMonitor;
using Vortice.Direct3D11;
using Vortice.DXGI;

internal static partial class Program
{
    private static void IndependentGradientRegression()
    {
        var settings = new OverlaySettings { Mode = OverlayMode.Veil, Strength = 0, NoiseEnabled = false, GradientEnabled = true, GradientMode = CanvasGradientMode.Linear,
            GradientStartColor = "#FF0000", GradientColor = "#0000FF", GradientStartOpacity = 1, GradientOpacity = 1,
            GradientStart = 0.2, GradientEnd = 0.8, GradientAngle = 0, GradientIntensity = 1 };
        using var cpu = new CpuFrameProcessor();
        var linear = cpu.Process(null, settings, 64, 64, 0);
        Require(Pixel(linear, 1, 64, 2) == 255 && Pixel(linear, 126, 64, 0) == 255, "Two-color independent gradient did not overlay a zero-strength canvas.");
        settings.GradientEnabled = false;
        Require(Pixel(cpu.Process(null, settings, 64, 64, 0), 1, 64, 2) == 0, "Disabling gradient did not leave the base canvas untouched.");
        settings.GradientEnabled = true; settings.GradientMode = CanvasGradientMode.EdgeFade; settings.GradientIndependentEdges = true;
        settings.GradientColor = "#000000"; settings.Strength = 1; settings.VeilColor = "#FFFFFF";
        settings.GradientLeftWidth = 0.15; settings.GradientRightWidth = 0.4; settings.GradientTopWidth = settings.GradientBottomWidth = 0;
        settings.GradientLeftOpacity = 1; settings.GradientRightOpacity = 0.4;
        var edges = cpu.Process(null, settings, 64, 64, 0);
        Require(Pixel(edges, 1, 64, 2) < 5 && Pixel(edges, 64, 64, 2) == 255 && Pixel(edges, 126, 64, 2) is > 145 and < 165,
            "Per-edge widths/strengths failed to preserve the center and distinct outer fades.");
        double broad = CanvasEffects.GradientAlpha(settings, 0.08, 0.5); settings.GradientFalloff = 3;
        Require(CanvasEffects.GradientAlpha(settings, 0.08, 0.5) < broad, "Falloff curve did not concentrate edge fade.");
        var cloned = JsonSerializer.Deserialize<OverlaySettings>(JsonSerializer.Serialize(settings.Snapshot()))!;
        Require(cloned.GradientRightWidth == 0.4 && cloned.GradientFalloff == 3 && cloned.GradientIndependentEdges && !cloned.HasDifferentImageSettings(new OverlaySettings { Mode = OverlayMode.Veil }),
            "Gradient settings were not copied/serialized or unnecessarily invalidated crop caches.");
        var legacy = JsonSerializer.Deserialize<OverlaySettings>("{\"GradientMode\":2,\"GradientOpacity\":0.8}")!;
        Require(legacy.GradientEnabled && legacy.GradientEnd == 1 && legacy.GradientFalloff == 1 && !legacy.GradientIndependentEdges, "Existing saved gradients did not retain their defaults.");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) CanvasEffects.DrawGradient(dc, settings, 128, 128);
        var image = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
        Require(Pixel(image, 64, 64, 3) == 0 && Pixel(image, 1, 64, 3) > 245, "WPF/image-border gradient lost transparent centers or edge overlay alpha.");

        using var renderer = new GpuRenderService(captureDesktop: false);
        using var layers = new OverlayLayerService(Dispatcher.CurrentDispatcher, new PassiveLayerPlatform());
        var overlay = new NativeGpuOverlay("independent-gradient", new Rectangle(-30000, -30000, 64, 64), settings, renderer, layers);
        try
        {
            overlay.Show(); bool ready = false;
            for (int i = 0; i < 100 && !ready; i++) { Thread.Sleep(50); OnGpu(renderer, () => ready = ((System.Collections.IDictionary)GpuField(renderer, "_targets")).Values.Cast<object>().Any(t => t.GetType().GetField("SwapChain")!.GetValue(t) is not null)); }
            Require(ready, "Gradient GPU pixel test did not initialize.");
            foreach (bool independent in new[] { true, false })
            {
                settings.GradientMode = independent ? CanvasGradientMode.EdgeFade : CanvasGradientMode.Linear;
                settings.GradientIndependentEdges = independent; settings.GradientStartColor = "#FF0000"; settings.GradientColor = "#0000FF";
                settings.GradientStartOpacity = 0.3; settings.GradientOpacity = 0.8; settings.GradientIntensity = 0.7;
                overlay.UpdateSettings(settings);
                OnGpu(renderer, () =>
                {
                    var target = ((System.Collections.IDictionary)GpuField(renderer, "_targets")).Values.Cast<object>().Single();
                    var device = (ID3D11Device)GpuField(renderer, "_device"); var context = (ID3D11DeviceContext)GpuField(renderer, "_context");
                    using var output = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 64, 64, 1, 1, bindFlags: BindFlags.RenderTarget));
                    using var view = device.CreateRenderTargetView(output); var field = target.GetType().GetField("BackBuffer")!; var previous = field.GetValue(target); field.SetValue(target, view);
                    try
                    {
                        typeof(GpuRenderService).GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(renderer, [target, true, 0f]);
                        var desc = output.Description; desc.BindFlags = BindFlags.None; desc.Usage = ResourceUsage.Staging; desc.CPUAccessFlags = CpuAccessFlags.Read;
                        using var readback = device.CreateTexture2D(desc); context.CopyResource(readback, output); var mapped = context.Map(readback, 0, MapMode.Read);
                        // Force CPU output resolution with a zero-strength dither
                        // amount too small to alter pixels, preserving same UVs.
                        var referenceSettings = settings.Snapshot(); referenceSettings.NoiseEnabled = true; referenceSettings.Noise = 0.000000001;
                        var expected = Pixels(cpu.Process(null, referenceSettings, 64, 64, 0));
                        try
                        {
                            var row = new byte[256];
                            for (int y = 0; y < 64; y++)
                            {
                                Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, row, 0, row.Length);
                                for (int x = 0; x < 64; x++) for (int c = 0; c < 3; c++) Require(Math.Abs(row[x * 4 + c] - expected[(y * 64 + x) * 4 + c]) <= 2,
                                    "CPU/GPU two-stop or independent-edge gradient differed.");
                            }
                        }
                        finally { context.Unmap(readback, 0); }
                    }
                    finally { field.SetValue(target, previous); }
                });
            }
        }
        finally { overlay.Close(); }
        Console.WriteLine("PASS: independent gradient toggle/opacity, two-color stops, per-edge widths/strengths/falloff, transparent center, CPU/GPU pixels and legacy persistence.");
    }
}
