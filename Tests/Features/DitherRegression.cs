using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using AmbientMonitor;
using Vortice.Direct3D11;
using Vortice.DXGI;

internal static partial class Program
{
    private static void DitherSizeRegression()
    {
        var settings = new OverlaySettings { Mode = OverlayMode.Veil, Strength = 0, NoiseEnabled = true, Noise = 0.3, NoiseGrainPixels = 8, CaptureScalePercent = 10 };
        using var cpu = new CpuFrameProcessor();
        var coarse = cpu.Process(null, settings, 64, 64, 3);
        Require(coarse.PixelWidth == 64 && coarse.PixelHeight == 64 && Pixel(coarse, 0, 0, 2) == Pixel(coarse, 7, 7, 2), "CPU grain cells did not use physical output pixels.");
        Require(!Pixels(coarse).SequenceEqual(Pixels(cpu.Process(null, settings, 64, 64, 4))), "Sized CPU grain stopped animating.");
        settings.NoiseGrainPixels = 1;
        var fine = cpu.Process(null, settings, 64, 64, 3);
        Require(!Pixels(coarse).SequenceEqual(Pixels(fine)) && Enumerable.Range(0, 8).Select(x => Pixel(fine, x, 0, 2)).Distinct().Count() > 1,
            "Fine grain still rendered as enlarged low-resolution veil pixels.");
        using var image = new Bitmap(128, 128);
        using (var g = Graphics.FromImage(image)) g.Clear(System.Drawing.Color.Black);
        settings.Mode = OverlayMode.Ambient; settings.Blur = 12; settings.CaptureScalePercent = 10;
        var reduced = cpu.Process(image, settings, 64, 64, 3);
        settings.CaptureScalePercent = 100;
        Require(Pixels(reduced).SequenceEqual(Pixels(cpu.Process(image, settings, 64, 64, 3))), "CPU processing resolution changed noise grain size.");
        var copy = settings.Snapshot(); copy.NoiseGrainPixels = 6.5;
        Require(!copy.HasDifferentImageSettings(settings), "Dither size incorrectly invalidated crop/blur cache keys.");
        var saved = JsonSerializer.Deserialize<OverlaySettings>(JsonSerializer.Serialize(copy))!;
        var legacy = JsonSerializer.Deserialize<OverlaySettings>("{}")!;
        Require(saved.NoiseGrainPixels == 6.5 && legacy.NoiseGrainPixels == 1 && DitherGrain.Size(double.NaN) == 1, "Grain size persistence/legacy normalization failed.");

        using var renderer = new GpuRenderService(captureDesktop: false);
        using var layers = new OverlayLayerService(Dispatcher.CurrentDispatcher, new PassiveLayerPlatform());
        settings.Mode = OverlayMode.Veil; settings.NoiseGrainPixels = 8;
        var overlay = new NativeGpuOverlay("dither-size-test", new Rectangle(-30000, -30000, 64, 64), settings, renderer, layers);
        try
        {
            overlay.Show(); bool ready = false;
            for (int i = 0; i < 100 && !ready; i++)
            {
                Thread.Sleep(50);
                OnGpu(renderer, () => ready = ((System.Collections.IDictionary)GpuField(renderer, "_targets")).Values.Cast<object>().Any(t => t.GetType().GetField("SwapChain")!.GetValue(t) is not null));
            }
            Require(ready, "GPU grain-size test did not initialize.");
            foreach (double size in new[] { 8d, 1d, 6.5d })
            {
                settings.NoiseGrainPixels = size; overlay.UpdateSettings(settings);
                OnGpu(renderer, () =>
                {
                    var target = ((System.Collections.IDictionary)GpuField(renderer, "_targets")).Values.Cast<object>().Single();
                    var device = (ID3D11Device)GpuField(renderer, "_device"); var context = (ID3D11DeviceContext)GpuField(renderer, "_context");
                    using var output = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 64, 64, 1, 1, bindFlags: BindFlags.RenderTarget));
                    using var view = device.CreateRenderTargetView(output);
                    var field = target.GetType().GetField("BackBuffer")!; var original = field.GetValue(target); field.SetValue(target, view);
                    try
                    {
                        typeof(GpuRenderService).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(renderer, [target, true, 3f]);
                        var description = output.Description; description.Usage = ResourceUsage.Staging; description.BindFlags = BindFlags.None; description.CPUAccessFlags = CpuAccessFlags.Read;
                        using var readback = device.CreateTexture2D(description); context.CopyResource(readback, output); var mapped = context.Map(readback, 0, MapMode.Read);
                        var expected = Pixels(cpu.Process(null, settings, 64, 64, 3));
                        try
                        {
                            var row = new byte[64 * 4];
                            for (int y = 0; y < 64; y++)
                            {
                                Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, row, 0, row.Length);
                                for (int x = 0; x < 64; x++) Require(Math.Abs(row[x * 4 + 2] - expected[(y * 64 + x) * 4 + 2]) <= 1,
                                    "CPU/GPU dither size or output coordinate mapping differed.");
                            }
                        }
                        finally { context.Unmap(readback, 0); }
                    }
                    finally { field.SetValue(target, original); }
                });
            }
        }
        finally { overlay.Close(); }
        Console.WriteLine("PASS: fine/coarse/fractional grain pixels match CPU/GPU output, animate, ignore blur resolution, preserve cache keys and serialize.");
    }
}
